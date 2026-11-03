using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialHub.Api.Data;
using SocialHub.Api.Infrastructure;
using SocialHub.Shared;

namespace SocialHub.Api.Controllers;

[Route("api/notifications")]
public class NotificationsController(AppDbContext db) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<NotificationDto>>> List([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var me = CurrentUserId;
        var rows = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == me)
            .OrderByDescending(n => n.Id)
            .Take(Math.Clamp(take, 1, 100))
            .Select(n => new
            {
                n.Id, n.Type, n.PostId, n.IsRead, n.CreatedAt,
                Actor = new UserSummaryDto(n.Actor.Id, n.Actor.DisplayName, n.Actor.AvatarUrl)
            })
            .ToListAsync(ct);

        return rows.Select(n => new NotificationDto(n.Id, n.Type, n.Actor, n.PostId,
            Mapping.NotificationText(n.Type, n.Actor.DisplayName), n.IsRead, n.CreatedAt)).ToList();
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<CountDto>> UnreadCount(CancellationToken ct)
    {
        var me = CurrentUserId;
        return new CountDto(await db.Notifications.CountAsync(n => n.UserId == me && !n.IsRead, ct));
    }

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        var me = CurrentUserId;
        await db.Notifications.Where(n => n.Id == id && n.UserId == me)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var me = CurrentUserId;
        await db.Notifications.Where(n => n.UserId == me && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
        return NoContent();
    }
}
