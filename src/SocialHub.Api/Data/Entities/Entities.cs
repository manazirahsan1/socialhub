using Microsoft.AspNetCore.Identity;
using SocialHub.Shared;

namespace SocialHub.Api.Data.Entities;

public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Post> Posts { get; set; } = new List<Post>();
}

public class Post
{
    public int Id { get; set; }
    public string AuthorId { get; set; } = "";
    public AppUser Author { get; set; } = null!;
    public string Content { get; set; } = "";
    public string? ImageUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
    public ICollection<PostLike> Likes { get; set; } = new List<PostLike>();
}

public class Comment
{
    public int Id { get; set; }
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;
    public string AuthorId { get; set; } = "";
    public AppUser Author { get; set; } = null!;
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PostLike
{
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;
    public string UserId { get; set; } = "";
    public AppUser User { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum FriendshipStatus
{
    Pending = 0,
    Accepted = 1
}

public class Friendship
{
    public int Id { get; set; }
    public string RequesterId { get; set; } = "";
    public AppUser Requester { get; set; } = null!;
    public string AddresseeId { get; set; } = "";
    public AppUser Addressee { get; set; } = null!;
    public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAt { get; set; }
}

public class Message
{
    public int Id { get; set; }
    public string SenderId { get; set; } = "";
    public AppUser Sender { get; set; } = null!;
    public string RecipientId { get; set; } = "";
    public AppUser Recipient { get; set; } = null!;
    public string Content { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public AppUser User { get; set; } = null!;
    public string ActorId { get; set; } = "";
    public AppUser Actor { get; set; } = null!;
    public NotificationType Type { get; set; }
    public int? PostId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RefreshToken
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public AppUser User { get; set; } = null!;

    /// <summary>SHA-256 hash of the token. The raw token is only ever held by the client.</summary>
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}
