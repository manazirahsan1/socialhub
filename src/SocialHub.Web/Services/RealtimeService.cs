using Microsoft.AspNetCore.SignalR.Client;
using SocialHub.Shared;

namespace SocialHub.Web.Services;

/// <summary>
/// One SignalR connection to the API per Blazor circuit (browser tab). Components subscribe to
/// its events; handlers run on a SignalR thread, so components must use InvokeAsync to re-render.
/// It also keeps the unread badge counts for the top bar.
/// </summary>
public sealed class RealtimeService(ApiClient api, ILogger<RealtimeService> logger) : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public event Action<MessageDto>? MessageReceived;
    public event Action<NotificationDto>? NotificationReceived;
    public event Action<string>? MessagesRead;      // reader's user id
    public event Action<string>? UserTyping;        // typer's user id
    public event Action? CountsChanged;

    public int UnreadNotifications { get; private set; }
    public int UnreadMessages { get; private set; }

    /// <summary>The conversation the user is looking at, so its messages don't bump the badge.</summary>
    public string? ActiveChatUserId { get; set; }

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task StartAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_connection is not null) return;

            _connection = new HubConnectionBuilder()
                .WithUrl(new Uri(api.BaseAddress, HubEvents.HubPath), o =>
                    o.AccessTokenProvider = () => api.GetAccessTokenAsync())
                .WithAutomaticReconnect()
                .Build();

            _connection.On<MessageDto>(HubEvents.ReceiveMessage, m =>
            {
                MessageReceived?.Invoke(m);
                _ = RefreshCountsAsync();
            });
            _connection.On<NotificationDto>(HubEvents.ReceiveNotification, n =>
            {
                UnreadNotifications++;
                NotificationReceived?.Invoke(n);
                CountsChanged?.Invoke();
            });
            _connection.On<string>(HubEvents.MessagesRead, readerId => MessagesRead?.Invoke(readerId));
            _connection.On<string>(HubEvents.UserTyping, senderId => UserTyping?.Invoke(senderId));
            _connection.Reconnected += async _ => await RefreshCountsAsync();

            await _connection.StartAsync();
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            // The app still works without real-time updates; log and carry on.
            logger.LogWarning(ex, "Could not connect to the real-time hub.");
            if (_connection is not null) await _connection.DisposeAsync();
            _connection = null;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task RefreshCountsAsync()
    {
        try
        {
            UnreadNotifications = await api.GetUnreadNotificationCountAsync();
            UnreadMessages = await api.GetUnreadMessageCountAsync();
            CountsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not refresh unread counts.");
        }
    }

    public void ClearNotificationCount()
    {
        UnreadNotifications = 0;
        CountsChanged?.Invoke();
    }

    public async Task SendTypingAsync(string recipientId)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        try { await _connection.InvokeAsync("Typing", recipientId); }
        catch (Exception ex) { logger.LogDebug(ex, "Typing signal failed."); }
    }

    public async Task StopAsync()
    {
        if (_connection is null) return;
        await _connection.DisposeAsync();
        _connection = null;
        UnreadMessages = UnreadNotifications = 0;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
