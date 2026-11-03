using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SocialHub.Api.Infrastructure;
using SocialHub.Shared;

namespace SocialHub.Api.Hubs;

/// <summary>
/// One hub for all real-time traffic. Most events are pushed from controllers through
/// IHubContext (new messages, notifications); clients only call in for typing indicators.
/// Each connection is addressed through Clients.User(userId), so a user with several tabs
/// open receives events on all of them.
/// </summary>
[Authorize]
public class RealtimeHub : Hub
{
    public Task Typing(string recipientId)
    {
        var senderId = Context.User!.GetUserId();
        if (string.IsNullOrWhiteSpace(recipientId) || recipientId == senderId) return Task.CompletedTask;
        return Clients.User(recipientId).SendAsync(HubEvents.UserTyping, senderId);
    }
}
