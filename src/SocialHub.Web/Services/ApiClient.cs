using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SocialHub.Shared;

namespace SocialHub.Web.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    public static async Task<ApiException> FromResponseAsync(HttpResponseMessage response)
    {
        var fallback = $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).";
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return new ApiException(fallback, response.StatusCode);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // ValidationProblemDetails: { errors: { field: [ "msg", ... ] } }
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(p => p.Value.ValueKind == JsonValueKind.Array
                        ? p.Value.EnumerateArray().Select(v => v.GetString())
                        : Enumerable.Repeat<string?>(p.Value.ToString(), 1))
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                return new ApiException(string.Join(" ", messages), response.StatusCode);
            }

            foreach (var name in new[] { "detail", "title" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return new ApiException(value.GetString()!, response.StatusCode);
        }
        catch (JsonException)
        {
            // Not JSON; use the fallback.
        }
        return new ApiException(fallback, response.StatusCode);
    }
}

/// <summary>
/// Typed client for SocialHub.Api. It attaches the JWT, refreshes it shortly before expiry,
/// and retries once after a refresh if the API answers 401.
/// Registered through AddHttpClient, so it's resolved in the Blazor circuit's scope and shares
/// that circuit's <see cref="TokenStore"/>. (A DelegatingHandler wouldn't: handlers get their own scope.)
/// </summary>
public class ApiClient(HttpClient http, TokenStore store, JwtAuthStateProvider authState, ILogger<ApiClient> logger)
{
    public Uri BaseAddress => http.BaseAddress!;

    /// <summary>Turns an API-relative path like "/uploads/x.jpg" into an absolute URL for img tags.</summary>
    public string? AssetUrl(string? path) =>
        string.IsNullOrEmpty(path) ? null
        : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path
        : new Uri(BaseAddress, path.TrimStart('/')).ToString();

    // ---------- Auth ----------

    public async Task LoginAsync(LoginRequest req) => await CompleteSignInAsync(
        await http.PostAsJsonAsync("api/auth/login", req));

    public async Task RegisterAsync(RegisterRequest req) => await CompleteSignInAsync(
        await http.PostAsJsonAsync("api/auth/register", req));

    public async Task LogoutAsync()
    {
        var session = await store.GetAsync();
        if (session is not null)
        {
            try { await http.PostAsJsonAsync("api/auth/logout", new RefreshRequest { RefreshToken = session.RefreshToken }); }
            catch (Exception ex) { logger.LogWarning(ex, "Logout call failed; clearing local session anyway."); }
        }
        await store.ClearAsync();
        authState.NotifyChanged();
    }

    private async Task CompleteSignInAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw await ApiException.FromResponseAsync(response);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>()
                   ?? throw new ApiException("Empty response from server.");
        await store.SetAsync(AuthSession.From(auth));
        authState.NotifyChanged();
    }

    /// <summary>
    /// Returns a valid access token, refreshing it if it's about to expire.
    /// Pass <paramref name="rejectedToken"/> after a 401 to force a refresh, unless another
    /// request already replaced that token.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(string? rejectedToken = null)
    {
        var session = await store.GetAsync();
        if (session is null) return null;
        if (IsUsable(session, rejectedToken)) return session.AccessToken;

        await store.RefreshLock.WaitAsync();
        try
        {
            session = await store.GetAsync();
            if (session is null) return null;
            if (IsUsable(session, rejectedToken)) return session.AccessToken; // refreshed while we waited

            var response = await http.PostAsJsonAsync("api/auth/refresh",
                new RefreshRequest { RefreshToken = session.RefreshToken });
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Refresh token rejected ({Status}); signing out.", response.StatusCode);
                await store.ClearAsync();
                authState.NotifyChanged();
                return null;
            }

            var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
            if (auth is null) return null;
            await store.SetAsync(AuthSession.From(auth));
            return auth.AccessToken;
        }
        finally
        {
            store.RefreshLock.Release();
        }
    }

    private static bool IsUsable(AuthSession s, string? rejectedToken) =>
        s.AccessToken != rejectedToken && s.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30);

    // ---------- Users ----------

    public Task<ProfileDto> GetMyProfileAsync() => GetAsync<ProfileDto>("api/users/me");
    public Task<ProfileDto> GetProfileAsync(string id) => GetAsync<ProfileDto>($"api/users/{Esc(id)}");

    public async Task<ProfileDto> UpdateProfileAsync(UpdateProfileRequest req)
    {
        var profile = await SendAsync<ProfileDto>(() => Json(HttpMethod.Put, "api/users/me", req));
        await store.UpdateUserAsync(new UserSummaryDto(profile.Id, profile.DisplayName, profile.AvatarUrl));
        authState.NotifyChanged();
        return profile;
    }

    public async Task<UserSummaryDto> UploadAvatarAsync(byte[] data, string fileName, string contentType)
    {
        var user = await SendAsync<UserSummaryDto>(() =>
            new HttpRequestMessage(HttpMethod.Post, "api/users/me/avatar")
            {
                Content = FileForm("file", data, fileName, contentType)
            });
        await store.UpdateUserAsync(user);
        authState.NotifyChanged();
        return user;
    }

    public Task<List<UserSummaryDto>> SearchUsersAsync(string q) =>
        GetAsync<List<UserSummaryDto>>($"api/users/search?q={Esc(q)}");

    // ---------- Posts ----------

    public Task<PagedResult<PostDto>> GetFeedAsync(int? before = null) =>
        GetAsync<PagedResult<PostDto>>($"api/posts/feed{Before(before)}");

    public Task<PagedResult<PostDto>> GetUserPostsAsync(string userId, int? before = null) =>
        GetAsync<PagedResult<PostDto>>($"api/posts/user/{Esc(userId)}{Before(before)}");

    public Task<PostDto> GetPostAsync(int id) => GetAsync<PostDto>($"api/posts/{id}");

    public Task<PostDto> CreatePostAsync(string content, byte[]? image, string? fileName, string? contentType) =>
        SendAsync<PostDto>(() =>
        {
            var form = new MultipartFormDataContent { { new StringContent(content), "Content" } };
            if (image is not null)
            {
                var file = new ByteArrayContent(image);
                file.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
                form.Add(file, "Image", fileName ?? "image");
            }
            return new HttpRequestMessage(HttpMethod.Post, "api/posts") { Content = form };
        });

    public Task<PostDto> UpdatePostAsync(int id, string content) =>
        SendAsync<PostDto>(() => Json(HttpMethod.Put, $"api/posts/{id}", new UpdatePostRequest { Content = content }));

    public Task DeletePostAsync(int id) => SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"api/posts/{id}"));

    public Task<LikeResultDto> ToggleLikeAsync(int id) =>
        SendAsync<LikeResultDto>(() => new HttpRequestMessage(HttpMethod.Post, $"api/posts/{id}/like"));

    public Task<List<UserSummaryDto>> GetLikesAsync(int id) => GetAsync<List<UserSummaryDto>>($"api/posts/{id}/likes");

    public Task<List<CommentDto>> GetCommentsAsync(int postId) =>
        GetAsync<List<CommentDto>>($"api/posts/{postId}/comments");

    public Task<CommentDto> AddCommentAsync(int postId, string content) =>
        SendAsync<CommentDto>(() => Json(HttpMethod.Post, $"api/posts/{postId}/comments",
            new CreateCommentRequest { Content = content }));

    public Task DeleteCommentAsync(int commentId) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"api/posts/comments/{commentId}"));

    // ---------- Friends ----------

    public Task<List<UserSummaryDto>> GetFriendsAsync(string? userId = null) =>
        GetAsync<List<UserSummaryDto>>(userId is null ? "api/friends" : $"api/friends?userId={Esc(userId)}");

    public Task<List<FriendRequestDto>> GetIncomingRequestsAsync() =>
        GetAsync<List<FriendRequestDto>>("api/friends/requests/incoming");

    public Task<List<FriendRequestDto>> GetOutgoingRequestsAsync() =>
        GetAsync<List<FriendRequestDto>>("api/friends/requests/outgoing");

    public Task<List<UserSummaryDto>> GetSuggestionsAsync() => GetAsync<List<UserSummaryDto>>("api/friends/suggestions");

    public Task<FriendshipStateDto> SendFriendRequestAsync(string userId) =>
        SendAsync<FriendshipStateDto>(() => new HttpRequestMessage(HttpMethod.Post, $"api/friends/requests/{Esc(userId)}"));

    public Task AcceptFriendRequestAsync(int requestId) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/friends/requests/{requestId}/accept"));

    public Task DeclineFriendRequestAsync(int requestId) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"api/friends/requests/{requestId}"));

    public Task UnfriendAsync(string userId) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"api/friends/{Esc(userId)}"));

    // ---------- Messages ----------

    public Task<List<ConversationDto>> GetConversationsAsync() =>
        GetAsync<List<ConversationDto>>("api/messages/conversations");

    public Task<PagedResult<MessageDto>> GetThreadAsync(string userId, int? before = null) =>
        GetAsync<PagedResult<MessageDto>>($"api/messages/{Esc(userId)}{Before(before)}");

    public Task<MessageDto> SendMessageAsync(string userId, string content) =>
        SendAsync<MessageDto>(() => Json(HttpMethod.Post, $"api/messages/{Esc(userId)}",
            new SendMessageRequest { Content = content }));

    public Task MarkThreadReadAsync(string userId) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/messages/{Esc(userId)}/read"));

    public async Task<int> GetUnreadMessageCountAsync() =>
        (await GetAsync<CountDto>("api/messages/unread-count")).Count;

    // ---------- Notifications ----------

    public Task<List<NotificationDto>> GetNotificationsAsync() => GetAsync<List<NotificationDto>>("api/notifications");

    public async Task<int> GetUnreadNotificationCountAsync() =>
        (await GetAsync<CountDto>("api/notifications/unread-count")).Count;

    public Task MarkNotificationReadAsync(int id) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/notifications/{id}/read"));

    public Task MarkAllNotificationsReadAsync() =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/notifications/read-all"));

    // ---------- Plumbing ----------

    private Task<T> GetAsync<T>(string url) => SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, url));

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> createRequest)
    {
        using var response = await SendAsync(createRequest);
        return await response.Content.ReadFromJsonAsync<T>()
               ?? throw new ApiException("Empty response from server.");
    }

    /// <summary>Sends an authenticated request. The factory is called again for the retry after a refresh.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest)
    {
        var token = await GetAccessTokenAsync()
                    ?? throw new ApiException("You're signed out. Please sign in again.", HttpStatusCode.Unauthorized);

        var response = await http.SendAsync(WithToken(createRequest(), token));
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            var newToken = await GetAccessTokenAsync(rejectedToken: token)
                           ?? throw new ApiException("Your session expired. Please sign in again.", HttpStatusCode.Unauthorized);
            response = await http.SendAsync(WithToken(createRequest(), newToken));
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ApiException.FromResponseAsync(response);
            response.Dispose();
            throw error;
        }
        return response;
    }

    private static HttpRequestMessage WithToken(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Json<T>(HttpMethod method, string url, T body) =>
        new(method, url) { Content = JsonContent.Create(body) };

    private static MultipartFormDataContent FileForm(string field, byte[] data, string fileName, string contentType)
    {
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, field, fileName } };
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);
    private static string Before(int? before) => before is null ? "" : $"?before={before}";
}
