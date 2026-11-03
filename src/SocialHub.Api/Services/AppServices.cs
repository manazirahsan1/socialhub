using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialHub.Api.Data;
using SocialHub.Api.Data.Entities;
using SocialHub.Api.Hubs;
using SocialHub.Api.Infrastructure;
using SocialHub.Shared;

namespace SocialHub.Api.Services;

/// <summary>Stores notifications and pushes them to the recipient over SignalR.</summary>
public class NotificationService(AppDbContext db, IHubContext<RealtimeHub> hub)
{
    public async Task NotifyAsync(string recipientId, AppUser actor, NotificationType type, int? postId = null,
        CancellationToken ct = default)
    {
        if (recipientId == actor.Id) return; // never notify people about their own actions

        var n = new Notification { UserId = recipientId, ActorId = actor.Id, Type = type, PostId = postId };
        db.Notifications.Add(n);
        await db.SaveChangesAsync(ct);

        var dto = new NotificationDto(n.Id, type, actor.ToSummary(), postId,
            Mapping.NotificationText(type, actor.DisplayName), false, n.CreatedAt);
        await hub.Clients.User(recipientId).SendAsync(HubEvents.ReceiveNotification, dto, ct);
    }
}

/// <summary>Saves uploaded images under wwwroot/uploads and returns their public path.</summary>
public class FileStorageService(IWebHostEnvironment env, IOptions<UploadOptions> options)
{
    private static readonly Dictionary<string, byte[][]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = [[0xFF, 0xD8, 0xFF]],
        [".jpeg"] = [[0xFF, 0xD8, 0xFF]],
        [".png"] = [[0x89, 0x50, 0x4E, 0x47]],
        [".gif"] = [[0x47, 0x49, 0x46, 0x38]],
        [".webp"] = [[0x52, 0x49, 0x46, 0x46]] // "RIFF"
    };

    public long MaxBytes => options.Value.MaxBytes;

    /// <returns>The public URL path, or an error message.</returns>
    public async Task<(string? Url, string? Error)> SaveImageAsync(IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0) return (null, "The file is empty.");
        if (file.Length > MaxBytes) return (null, $"Images must be {MaxBytes / (1024 * 1024)} MB or smaller.");

        var ext = Path.GetExtension(file.FileName);
        if (!Signatures.TryGetValue(ext, out var sigs))
            return (null, "Only JPG, PNG, GIF and WEBP images are allowed.");

        // Check the file's leading bytes so a renamed executable can't be uploaded as an image.
        var header = new byte[8];
        await using (var peek = file.OpenReadStream())
        {
            var read = await peek.ReadAsync(header, ct);
            if (!sigs.Any(s => read >= s.Length && header.AsSpan(0, s.Length).SequenceEqual(s)))
                return (null, "The file content doesn't match its image type.");
        }

        var now = DateTime.UtcNow;
        var relativeDir = Path.Combine("uploads", now.ToString("yyyy"), now.ToString("MM"));
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir = Path.Combine(webRoot, relativeDir);
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        await using (var target = File.Create(Path.Combine(dir, fileName)))
        await using (var source = file.OpenReadStream())
        {
            await source.CopyToAsync(target, ct);
        }

        return ("/" + Path.Combine(relativeDir, fileName).Replace('\\', '/'), null);
    }

    public void Delete(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("/uploads/")) return;
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var path = Path.GetFullPath(Path.Combine(webRoot, url.TrimStart('/')));
        if (path.StartsWith(Path.GetFullPath(webRoot)) && File.Exists(path)) File.Delete(path);
    }
}

/// <summary>Reusable friendship queries.</summary>
public class FriendService(AppDbContext db)
{
    /// <summary>IDs of the user's accepted friends, as a composable query.</summary>
    public IQueryable<string> FriendIds(string userId) =>
        db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId);

    public Task<Friendship?> FindBetweenAsync(string a, string b, CancellationToken ct = default) =>
        db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterId == a && f.AddresseeId == b) || (f.RequesterId == b && f.AddresseeId == a), ct);

    public async Task<(FriendshipState State, int? PendingRequestId)> StateAsync(string me, string other,
        CancellationToken ct = default)
    {
        if (me == other) return (FriendshipState.Self, null);
        var f = await FindBetweenAsync(me, other, ct);
        if (f is null) return (FriendshipState.None, null);
        if (f.Status == FriendshipStatus.Accepted) return (FriendshipState.Friends, null);
        return f.RequesterId == me
            ? (FriendshipState.RequestSent, f.Id)
            : (FriendshipState.RequestReceived, f.Id);
    }
}
