using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialHub.Api.Data;
using SocialHub.Api.Infrastructure;
using SocialHub.Api.Services;
using SocialHub.Shared;

namespace SocialHub.Api.Controllers;

[Route("api/users")]
public class UsersController(AppDbContext db, FriendService friends, FileStorageService files) : ApiControllerBase
{
    [HttpGet("me")]
    public Task<ActionResult<ProfileDto>> Me(CancellationToken ct) => Profile(CurrentUserId, ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<ProfileDto>> Profile(string id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var friendCount = await friends.FriendIds(id).CountAsync(ct);
        var postCount = await db.Posts.CountAsync(p => p.AuthorId == id, ct);
        var (state, requestId) = await friends.StateAsync(CurrentUserId, id, ct);

        return new ProfileDto(user.Id, user.DisplayName, user.Bio, user.AvatarUrl, user.CreatedAt,
            friendCount, postCount, state, requestId);
    }

    [HttpPut("me")]
    public async Task<ActionResult<ProfileDto>> Update(UpdateProfileRequest req, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == CurrentUserId, ct);
        user.DisplayName = req.DisplayName.Trim();
        user.Bio = string.IsNullOrWhiteSpace(req.Bio) ? null : req.Bio.Trim();
        await db.SaveChangesAsync(ct);
        return await Profile(user.Id, ct);
    }

    [HttpPost("me/avatar")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<UserSummaryDto>> UploadAvatar(IFormFile file, CancellationToken ct)
    {
        var (url, error) = await files.SaveImageAsync(file, ct);
        if (error is not null) return Problem(statusCode: 400, title: error);

        var user = await db.Users.FirstAsync(u => u.Id == CurrentUserId, ct);
        files.Delete(user.AvatarUrl);
        user.AvatarUrl = url;
        await db.SaveChangesAsync(ct);
        return user.ToSummary();
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<UserSummaryDto>>> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return new List<UserSummaryDto>();
        var term = q.Trim();

        return await db.Users.AsNoTracking()
            .Where(u => u.DisplayName.Contains(term) || u.Email!.StartsWith(term))
            .OrderBy(u => u.DisplayName)
            .Take(25)
            .Select(u => new UserSummaryDto(u.Id, u.DisplayName, u.AvatarUrl))
            .ToListAsync(ct);
    }
}
