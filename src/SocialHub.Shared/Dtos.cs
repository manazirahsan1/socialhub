using System.ComponentModel.DataAnnotations;

namespace SocialHub.Shared;

// ---------- Auth ----------

public class RegisterRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = "";

    [Required, StringLength(60, MinimumLength = 2)]
    public string DisplayName { get; set; } = "";
}

public class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";
}

public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = "";
}

public record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    UserSummaryDto User);

// ---------- Users ----------

public record UserSummaryDto(string Id, string DisplayName, string? AvatarUrl);

public enum FriendshipState
{
    None = 0,
    Self = 1,
    Friends = 2,
    RequestSent = 3,
    RequestReceived = 4
}

public record ProfileDto(
    string Id,
    string DisplayName,
    string? Bio,
    string? AvatarUrl,
    DateTime JoinedAt,
    int FriendCount,
    int PostCount,
    FriendshipState Friendship,
    int? PendingRequestId);

public class UpdateProfileRequest
{
    [Required, StringLength(60, MinimumLength = 2)]
    public string DisplayName { get; set; } = "";

    [StringLength(500)]
    public string? Bio { get; set; }
}

// ---------- Posts ----------

public record PostDto(
    int Id,
    UserSummaryDto Author,
    string Content,
    string? ImageUrl,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int LikeCount,
    int CommentCount,
    bool LikedByMe,
    bool IsMine);

public class UpdatePostRequest
{
    [Required, StringLength(5000, MinimumLength = 1)]
    public string Content { get; set; } = "";
}

public record CommentDto(
    int Id,
    int PostId,
    UserSummaryDto Author,
    string Content,
    DateTime CreatedAt,
    bool IsMine);

public class CreateCommentRequest
{
    [Required, StringLength(2000, MinimumLength = 1)]
    public string Content { get; set; } = "";
}

public record LikeResultDto(bool Liked, int LikeCount);

public record PagedResult<T>(IReadOnlyList<T> Items, bool HasMore);

// ---------- Friends ----------

public record FriendshipStateDto(FriendshipState State, int? PendingRequestId);

/// <summary>A pending friend request. <see cref="User"/> is the other party.</summary>
public record FriendRequestDto(int Id, UserSummaryDto User, DateTime CreatedAt);

// ---------- Messages ----------

public record MessageDto(
    int Id,
    string SenderId,
    string RecipientId,
    string Content,
    DateTime SentAt,
    DateTime? ReadAt);

public class SendMessageRequest
{
    [Required, StringLength(4000, MinimumLength = 1)]
    public string Content { get; set; } = "";
}

public record ConversationDto(
    UserSummaryDto User,
    string LastMessage,
    DateTime LastMessageAt,
    bool LastFromMe,
    int UnreadCount);

// ---------- Notifications ----------

public enum NotificationType
{
    PostLiked = 0,
    PostCommented = 1,
    FriendRequestReceived = 2,
    FriendRequestAccepted = 3
}

public record NotificationDto(
    int Id,
    NotificationType Type,
    UserSummaryDto Actor,
    int? PostId,
    string Text,
    bool IsRead,
    DateTime CreatedAt);

public record CountDto(int Count);

// ---------- Real-time ----------

/// <summary>Names of the SignalR client methods the server invokes.</summary>
public static class HubEvents
{
    public const string HubPath = "hubs/social";
    public const string ReceiveMessage = "ReceiveMessage";
    public const string ReceiveNotification = "ReceiveNotification";
    public const string MessagesRead = "MessagesRead";
    public const string UserTyping = "UserTyping";
}
