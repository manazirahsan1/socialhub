using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SocialHub.Api.Data;
using SocialHub.Api.Data.Entities;
using SocialHub.Api.Hubs;
using SocialHub.Api.Infrastructure;
using SocialHub.Shared;

namespace SocialHub.Api.Controllers;

[Route("api/messages")]
public class MessagesController(AppDbContext db, IHubContext<RealtimeHub> hub) : ApiControllerBase
{
    /// <summary>One row per person you've exchanged messages with, most recent first.</summary>
    [HttpGet("conversations")]
    public async Task<ActionResult<List<ConversationDto>>> Conversations(CancellationToken ct)
    {
        var me = CurrentUserId;

        var lastIds = await db.Messages
            .Where(m => m.SenderId == me || m.RecipientId == me)
            .GroupBy(m => m.SenderId == me ? m.RecipientId : m.SenderId)
            .Select(g => g.Max(m => m.Id))
            .ToListAsync(ct);

        var lastMessages = await db.Messages.AsNoTracking()
            .Where(m => lastIds.Contains(m.Id))
            .Include(m => m.Sender)
            .Include(m => m.Recipient)
            .OrderByDescending(m => m.Id)
            .ToListAsync(ct);

        var unread = await db.Messages
            .Where(m => m.RecipientId == me && m.ReadAt == null)
            .GroupBy(m => m.SenderId)
            .Select(g => new { SenderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SenderId, x => x.Count, ct);

        return lastMessages.Select(m =>
        {
            var other = m.SenderId == me ? m.Recipient : m.Sender;
            return new ConversationDto(other.ToSummary(), m.Content, m.SentAt, m.SenderId == me,
                unread.GetValueOrDefault(other.Id));
        }).ToList();
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<CountDto>> UnreadCount(CancellationToken ct)
    {
        var me = CurrentUserId;
        // Number of conversations with unread messages, which is what the badge shows.
        var count = await db.Messages
            .Where(m => m.RecipientId == me && m.ReadAt == null)
            .Select(m => m.SenderId)
            .Distinct()
            .CountAsync(ct);
        return new CountDto(count);
    }

    /// <summary>Messages with one user, newest page first. Pass "before" (a message id) for older pages.</summary>
    [HttpGet("{userId}")]
    public async Task<ActionResult<PagedResult<MessageDto>>> Thread(string userId, [FromQuery] int? before,
        [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        var me = CurrentUserId;
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Messages.AsNoTracking().Where(m =>
            (m.SenderId == me && m.RecipientId == userId) || (m.SenderId == userId && m.RecipientId == me));
        if (before is not null) query = query.Where(m => m.Id < before);

        var items = await query
            .OrderByDescending(m => m.Id)
            .Take(pageSize + 1)
            .Select(m => new MessageDto(m.Id, m.SenderId, m.RecipientId, m.Content, m.SentAt, m.ReadAt))
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        items.Reverse(); // oldest first, ready to display
        return new PagedResult<MessageDto>(items, hasMore);
    }

    [HttpPost("{userId}")]
    public async Task<ActionResult<MessageDto>> Send(string userId, SendMessageRequest req, CancellationToken ct)
    {
        var me = CurrentUserId;
        if (userId == me) return Problem(statusCode: 400, title: "You can't message yourself.");
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return NotFound();

        var message = new Message { SenderId = me, RecipientId = userId, Content = req.Content.Trim() };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);

        var dto = message.ToDto();
        // Push to the recipient, and to the sender's other open tabs.
        await hub.Clients.Users(userId, me).SendAsync(HubEvents.ReceiveMessage, dto, ct);
        return dto;
    }

    /// <summary>Marks everything the other user sent you as read and tells them (read receipts).</summary>
    [HttpPost("{userId}/read")]
    public async Task<IActionResult> MarkRead(string userId, CancellationToken ct)
    {
        var me = CurrentUserId;
        var now = DateTime.UtcNow;
        var updated = await db.Messages
            .Where(m => m.SenderId == userId && m.RecipientId == me && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now), ct);

        if (updated > 0)
            await hub.Clients.User(userId).SendAsync(HubEvents.MessagesRead, me, ct);
        return NoContent();
    }
}
