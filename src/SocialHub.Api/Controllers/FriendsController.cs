using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialHub.Api.Data;
using SocialHub.Api.Data.Entities;
using SocialHub.Api.Services;
using SocialHub.Shared;

namespace SocialHub.Api.Controllers;

[Route("api/friends")]
public class FriendsController(AppDbContext db, FriendService friends, NotificationService notifications)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<UserSummaryDto>>> List([FromQuery] string? userId, CancellationToken ct)
    {
        var ids = friends.FriendIds(userId ?? CurrentUserId);
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .Select(u => new UserSummaryDto(u.Id, u.DisplayName, u.AvatarUrl))
            .ToListAsync(ct);
    }

    [HttpGet("requests/incoming")]
    public async Task<ActionResult<List<FriendRequestDto>>> Incoming(CancellationToken ct)
    {
        var me = CurrentUserId;
        return await db.Friendships.AsNoTracking()
            .Where(f => f.AddresseeId == me && f.Status == FriendshipStatus.Pending)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FriendRequestDto(f.Id,
                new UserSummaryDto(f.Requester.Id, f.Requester.DisplayName, f.Requester.AvatarUrl), f.CreatedAt))
            .ToListAsync(ct);
    }

    [HttpGet("requests/outgoing")]
    public async Task<ActionResult<List<FriendRequestDto>>> Outgoing(CancellationToken ct)
    {
        var me = CurrentUserId;
        return await db.Friendships.AsNoTracking()
            .Where(f => f.RequesterId == me && f.Status == FriendshipStatus.Pending)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FriendRequestDto(f.Id,
                new UserSummaryDto(f.Addressee.Id, f.Addressee.DisplayName, f.Addressee.AvatarUrl), f.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>People you have no friendship or pending request with, preferring friends of friends.</summary>
    [HttpGet("suggestions")]
    public async Task<ActionResult<List<UserSummaryDto>>> Suggestions(CancellationToken ct)
    {
        var me = CurrentUserId;
        var connected = db.Friendships
            .Where(f => f.RequesterId == me || f.AddresseeId == me)
            .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId);

        // Count mutual friends in memory: the sets involved are small and the SQL is far simpler.
        var myFriendIds = (await friends.FriendIds(me).ToListAsync(ct)).ToHashSet();
        var friendsOfFriends = await db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (myFriendIds.Contains(f.RequesterId) || myFriendIds.Contains(f.AddresseeId)))
            .Select(f => new { f.RequesterId, f.AddresseeId })
            .ToListAsync(ct);

        var mutualCounts = friendsOfFriends
            .SelectMany(f => new[] { f.RequesterId, f.AddresseeId })
            .Where(id => id != me && !myFriendIds.Contains(id))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
        var mutualIds = mutualCounts.Keys.ToList();

        var candidates = await db.Users.AsNoTracking()
            .Where(u => u.Id != me && !connected.Contains(u.Id) && mutualIds.Contains(u.Id))
            .Select(u => new UserSummaryDto(u.Id, u.DisplayName, u.AvatarUrl))
            .ToListAsync(ct);

        var newest = await db.Users.AsNoTracking()
            .Where(u => u.Id != me && !connected.Contains(u.Id))
            .OrderByDescending(u => u.CreatedAt)
            .Take(12)
            .Select(u => new UserSummaryDto(u.Id, u.DisplayName, u.AvatarUrl))
            .ToListAsync(ct);

        return candidates
            .OrderByDescending(u => mutualCounts.GetValueOrDefault(u.Id))
            .Concat(newest)
            .DistinctBy(u => u.Id)
            .Take(12)
            .ToList();
    }

    /// <summary>Sends a friend request. If they already asked you, this accepts theirs instead.</summary>
    [HttpPost("requests/{userId}")]
    public async Task<ActionResult<FriendshipStateDto>> Send(string userId, CancellationToken ct)
    {
        var me = CurrentUserId;
        if (userId == me) return Problem(statusCode: 400, title: "You can't add yourself.");

        var actor = await db.Users.FirstAsync(u => u.Id == me, ct);
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return NotFound();

        var existing = await friends.FindBetweenAsync(me, userId, ct);
        if (existing is not null)
        {
            if (existing.Status == FriendshipStatus.Pending && existing.AddresseeId == me)
            {
                await AcceptInternal(existing, actor, ct);
                return new FriendshipStateDto(FriendshipState.Friends, null);
            }
            var (state, reqId) = await friends.StateAsync(me, userId, ct);
            return new FriendshipStateDto(state, reqId);
        }

        var request = new Friendship { RequesterId = me, AddresseeId = userId };
        db.Friendships.Add(request);
        await db.SaveChangesAsync(ct);
        await notifications.NotifyAsync(userId, actor, NotificationType.FriendRequestReceived, null, ct);
        return new FriendshipStateDto(FriendshipState.RequestSent, request.Id);
    }

    [HttpPost("requests/{id:int}/accept")]
    public async Task<IActionResult> Accept(int id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var request = await db.Friendships.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (request is null || request.Status != FriendshipStatus.Pending) return NotFound();
        if (request.AddresseeId != me) return Forbid();

        var actor = await db.Users.FirstAsync(u => u.Id == me, ct);
        await AcceptInternal(request, actor, ct);
        return NoContent();
    }

    /// <summary>Declines an incoming request, or cancels one you sent.</summary>
    [HttpDelete("requests/{id:int}")]
    public async Task<IActionResult> DeclineOrCancel(int id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var request = await db.Friendships.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (request is null || request.Status != FriendshipStatus.Pending) return NotFound();
        if (request.AddresseeId != me && request.RequesterId != me) return Forbid();

        db.Friendships.Remove(request);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> Unfriend(string userId, CancellationToken ct)
    {
        var friendship = await friends.FindBetweenAsync(CurrentUserId, userId, ct);
        if (friendship is null || friendship.Status != FriendshipStatus.Accepted) return NotFound();

        db.Friendships.Remove(friendship);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task AcceptInternal(Friendship request, AppUser actor, CancellationToken ct)
    {
        request.Status = FriendshipStatus.Accepted;
        request.RespondedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await notifications.NotifyAsync(request.RequesterId, actor, NotificationType.FriendRequestAccepted, null, ct);
    }
}
