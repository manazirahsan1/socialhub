using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using SocialHub.Api.Data.Entities;
using SocialHub.Shared;

namespace SocialHub.Api.Infrastructure;

public class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public class UploadOptions
{
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;
}

/// <summary>JWT claim names. Inbound claim mapping is turned off, so claims keep their short names.</summary>
public static class AppClaims
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string Email = "email";
}

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(AppClaims.UserId)
        ?? throw new InvalidOperationException("Authenticated user has no 'sub' claim.");
}

/// <summary>Makes SignalR's Clients.User(id) use the JWT "sub" claim.</summary>
public class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirstValue(AppClaims.UserId);
}

public static class Mapping
{
    public static UserSummaryDto ToSummary(this AppUser u) => new(u.Id, u.DisplayName, u.AvatarUrl);

    public static MessageDto ToDto(this Message m) =>
        new(m.Id, m.SenderId, m.RecipientId, m.Content, m.SentAt, m.ReadAt);

    public static string NotificationText(NotificationType type, string actorName) => type switch
    {
        NotificationType.PostLiked => $"{actorName} liked your post.",
        NotificationType.PostCommented => $"{actorName} commented on your post.",
        NotificationType.FriendRequestReceived => $"{actorName} sent you a friend request.",
        NotificationType.FriendRequestAccepted => $"{actorName} accepted your friend request.",
        _ => $"{actorName} interacted with you."
    };
}
