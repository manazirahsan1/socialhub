using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using SocialHub.Shared;

namespace SocialHub.Web.Services;

public class AuthSession
{
    public string AccessToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public string RefreshToken { get; set; } = "";
    public UserSummaryDto User { get; set; } = new("", "", null);

    public static AuthSession From(AuthResponse r) => new()
    {
        AccessToken = r.AccessToken,
        AccessTokenExpiresAt = r.AccessTokenExpiresAt,
        RefreshToken = r.RefreshToken,
        User = r.User
    };
}

/// <summary>
/// Per-circuit holder for the user's tokens. They are persisted in the browser's localStorage,
/// encrypted with ASP.NET Core Data Protection, so a page reload keeps the user signed in.
/// The tokens never reach JavaScript in readable form.
/// </summary>
public class TokenStore(ProtectedLocalStorage storage, ILogger<TokenStore> logger)
{
    private const string Key = "socialhub.auth";
    private AuthSession? _session;
    private bool _loaded;

    /// <summary>Serializes token refreshes so parallel requests don't all refresh at once.</summary>
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    public async Task<AuthSession?> GetAsync()
    {
        if (_loaded) return _session;
        try
        {
            var result = await storage.GetAsync<AuthSession>(Key);
            _session = result.Success ? result.Value : null;
        }
        catch (CryptographicException)
        {
            // Data Protection keys changed (e.g. app moved machines): treat as signed out.
            _session = null;
            await TryDeleteAsync();
        }
        catch (InvalidOperationException ex)
        {
            // JS interop isn't available yet (prerendering). Don't cache the miss.
            logger.LogDebug(ex, "Browser storage not available yet.");
            return null;
        }
        _loaded = true;
        return _session;
    }

    public async Task SetAsync(AuthSession session)
    {
        _session = session;
        _loaded = true;
        await storage.SetAsync(Key, session);
    }

    public async Task UpdateUserAsync(UserSummaryDto user)
    {
        if (_session is null) return;
        _session.User = user;
        await storage.SetAsync(Key, _session);
    }

    public async Task ClearAsync()
    {
        _session = null;
        _loaded = true;
        await TryDeleteAsync();
    }

    private async Task TryDeleteAsync()
    {
        try { await storage.DeleteAsync(Key); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not clear browser storage."); }
    }
}

/// <summary>Exposes the signed-in user from <see cref="TokenStore"/> to Blazor's auth system.</summary>
public class JwtAuthStateProvider(TokenStore store) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var session = await store.GetAsync();
        if (session is null) return Anonymous;

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.User.Id),
            new Claim(ClaimTypes.Name, session.User.DisplayName),
            new Claim("avatar", session.User.AvatarUrl ?? "")
        ], authenticationType: "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}

public static class ClaimsPrincipalExtensions
{
    public static string UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public static UserSummaryDto ToSummary(this ClaimsPrincipal user)
    {
        var avatar = user.FindFirstValue("avatar");
        return new UserSummaryDto(user.UserId(), user.Identity?.Name ?? "", string.IsNullOrEmpty(avatar) ? null : avatar);
    }
}
