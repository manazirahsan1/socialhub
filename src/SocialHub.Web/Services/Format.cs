namespace SocialHub.Web.Services;

public static class Format
{
    /// <summary>"just now", "5m", "3h", "2d", or a date. API timestamps are UTC.</summary>
    public static string TimeAgo(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 45) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
        return utc.ToString(utc.Year == DateTime.UtcNow.Year ? "MMM d" : "MMM d, yyyy");
    }

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }

    /// <summary>A stable hue per user, for initials avatars.</summary>
    public static int Hue(string id)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in id) h = h * 31 + c;
            return Math.Abs(h % 360);
        }
    }
}
