using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialHub.Api.Data;
using SocialHub.Api.Data.Entities;
using SocialHub.Api.Services;
using SocialHub.Shared;

namespace SocialHub.Api.Controllers;

public class CreatePostForm
{
    [StringLength(5000)]
    public string? Content { get; set; }

    public IFormFile? Image { get; set; }
}

[Route("api/posts")]
public class PostsController(
    AppDbContext db,
    FriendService friends,
    FileStorageService files,
    NotificationService notifications) : ApiControllerBase
{
    private const int MaxPageSize = 30;

    /// <summary>News feed: your posts and your friends' posts, newest first. Keyset-paged with "before".</summary>
    [HttpGet("feed")]
    public async Task<ActionResult<PagedResult<PostDto>>> Feed([FromQuery] int? before, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var me = CurrentUserId;
        var friendIds = friends.FriendIds(me);
        var query = db.Posts.Where(p => p.AuthorId == me || friendIds.Contains(p.AuthorId));
        return await PageAsync(query, before, pageSize, ct);
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<PagedResult<PostDto>>> ByUser(string userId, [FromQuery] int? before,
        [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        return await PageAsync(db.Posts.Where(p => p.AuthorId == userId), before, pageSize, ct);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PostDto>> Get(int id, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().Where(p => p.Id == id).Select(ToDto(CurrentUserId)).FirstOrDefaultAsync(ct);
        if (post is null) return NotFound();
        return post;
    }

    [HttpPost]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<PostDto>> Create([FromForm] CreatePostForm form, CancellationToken ct)
    {
        var content = form.Content?.Trim() ?? "";
        if (content.Length == 0 && form.Image is null)
            return Problem(statusCode: 400, title: "Write something or add a photo.");

        string? imageUrl = null;
        if (form.Image is not null)
        {
            var (url, error) = await files.SaveImageAsync(form.Image, ct);
            if (error is not null) return Problem(statusCode: 400, title: error);
            imageUrl = url;
        }

        var post = new Post { AuthorId = CurrentUserId, Content = content, ImageUrl = imageUrl };
        db.Posts.Add(post);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = post.Id }, (await Get(post.Id, ct)).Value);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PostDto>> Update(int id, UpdatePostRequest req, CancellationToken ct)
    {
        var post = await db.Posts.FindAsync([id], ct);
        if (post is null) return NotFound();
        if (post.AuthorId != CurrentUserId) return Forbid();

        post.Content = req.Content.Trim();
        post.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var post = await db.Posts.FindAsync([id], ct);
        if (post is null) return NotFound();
        if (post.AuthorId != CurrentUserId) return Forbid();

        // Notifications reference posts without a FK cascade, so remove them explicitly.
        await db.Notifications.Where(n => n.PostId == id).ExecuteDeleteAsync(ct);
        db.Posts.Remove(post); // comments and likes cascade
        await db.SaveChangesAsync(ct);
        files.Delete(post.ImageUrl);
        return NoContent();
    }

    /// <summary>Toggles the current user's like on a post.</summary>
    [HttpPost("{id:int}/like")]
    public async Task<ActionResult<LikeResultDto>> ToggleLike(int id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return NotFound();

        var existing = await db.PostLikes.FindAsync([id, me], ct);
        bool liked;
        if (existing is null)
        {
            db.PostLikes.Add(new PostLike { PostId = id, UserId = me });
            liked = true;
        }
        else
        {
            db.PostLikes.Remove(existing);
            liked = false;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Double-click race: the like already exists (or was already removed). Report current state.
            db.ChangeTracker.Clear();
            liked = await db.PostLikes.AnyAsync(l => l.PostId == id && l.UserId == me, ct);
        }

        if (liked && existing is null)
        {
            var actor = await db.Users.FirstAsync(u => u.Id == me, ct);
            await notifications.NotifyAsync(post.AuthorId, actor, NotificationType.PostLiked, id, ct);
        }

        var count = await db.PostLikes.CountAsync(l => l.PostId == id, ct);
        return new LikeResultDto(liked, count);
    }

    [HttpGet("{id:int}/likes")]
    public async Task<ActionResult<List<UserSummaryDto>>> Likes(int id, CancellationToken ct) =>
        await db.PostLikes.AsNoTracking()
            .Where(l => l.PostId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new UserSummaryDto(l.User.Id, l.User.DisplayName, l.User.AvatarUrl))
            .ToListAsync(ct);

    [HttpGet("{id:int}/comments")]
    public async Task<ActionResult<List<CommentDto>>> Comments(int id, CancellationToken ct)
    {
        var me = CurrentUserId;
        return await db.Comments.AsNoTracking()
            .Where(c => c.PostId == id)
            .OrderBy(c => c.Id)
            .Select(c => new CommentDto(c.Id, c.PostId,
                new UserSummaryDto(c.Author.Id, c.Author.DisplayName, c.Author.AvatarUrl),
                c.Content, c.CreatedAt, c.AuthorId == me))
            .ToListAsync(ct);
    }

    [HttpPost("{id:int}/comments")]
    public async Task<ActionResult<CommentDto>> AddComment(int id, CreateCommentRequest req, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return NotFound();

        var actor = await db.Users.FirstAsync(u => u.Id == CurrentUserId, ct);
        var comment = new Comment { PostId = id, AuthorId = actor.Id, Content = req.Content.Trim() };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        await notifications.NotifyAsync(post.AuthorId, actor, NotificationType.PostCommented, id, ct);

        return new CommentDto(comment.Id, id, new UserSummaryDto(actor.Id, actor.DisplayName, actor.AvatarUrl),
            comment.Content, comment.CreatedAt, true);
    }

    [HttpDelete("comments/{commentId:int}")]
    public async Task<IActionResult> DeleteComment(int commentId, CancellationToken ct)
    {
        var comment = await db.Comments.Include(c => c.Post).FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment is null) return NotFound();

        // The comment's author or the post's author may delete a comment.
        var me = CurrentUserId;
        if (comment.AuthorId != me && comment.Post.AuthorId != me) return Forbid();

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<PagedResult<PostDto>> PageAsync(IQueryable<Post> query, int? before, int pageSize,
        CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        if (before is not null) query = query.Where(p => p.Id < before);

        var items = await query.AsNoTracking()
            .OrderByDescending(p => p.Id)
            .Take(pageSize + 1)
            .Select(ToDto(CurrentUserId))
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new PagedResult<PostDto>(items, hasMore);
    }

    private static Expression<Func<Post, PostDto>> ToDto(string me) => p => new PostDto(
        p.Id,
        new UserSummaryDto(p.Author.Id, p.Author.DisplayName, p.Author.AvatarUrl),
        p.Content,
        p.ImageUrl,
        p.CreatedAt,
        p.UpdatedAt,
        p.Likes.Count,
        p.Comments.Count,
        p.Likes.Any(l => l.UserId == me),
        p.AuthorId == me);
}
