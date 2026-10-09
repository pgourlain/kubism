namespace Kubism.Core;

/// <summary>Durations as kubectl prints them: 45s, 5m, 3h12m, 4d, 2y3d.</summary>
public static class KubeTime
{
    public static string Age(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        var s = (long)d.TotalSeconds;
        if (s < 60 * 2) return $"{s}s";
        var m = s / 60;
        if (m < 10) return s % 60 == 0 ? $"{m}m" : $"{m}m{s % 60}s";
        if (m < 60 * 3) return $"{m}m";
        var h = m / 60;
        if (h < 8) return m % 60 == 0 ? $"{h}h" : $"{h}h{m % 60}m";
        if (h < 48) return $"{h}h";
        var days = h / 24;
        if (days < 8) return h % 24 == 0 ? $"{days}d" : $"{days}d{h % 24}h";
        if (days < 365 * 2) return $"{days}d";
        var y = days / 365;
        return days % 365 == 0 ? $"{y}y" : $"{y}y{days % 365}d";
    }

    public static string Age(DateTimeOffset? created, TimeSpan skew = default, DateTimeOffset? now = null) =>
        created is null ? "<unknown>" : Age((now ?? DateTimeOffset.UtcNow) + skew - created.Value);
}
