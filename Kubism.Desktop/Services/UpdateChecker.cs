using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Kubism.Desktop.Services;

public sealed record UpdateInfo(string Version, string Url, bool IsNewer);

/// <summary>Opt-in check against GitHub releases, at most once a day. It never downloads or installs anything.</summary>
public sealed class UpdateChecker(Kubism.Core.Catalog.ISettingsStore settings, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/pgourlain/kubism/releases/latest";
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <param name="manual">User asked from the menu: ignores the opt-in and the once-a-day limit, and lets failures through so they can be shown.</param>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default, bool manual = false)
    {
        var s = settings.Current;
        if (!manual)
        {
            if (!s.CheckForUpdates) return null;
            if (s.LastUpdateCheck is { } last && _now() - last < TimeSpan.FromHours(23)) return null;
        }
        s.LastUpdateCheck = _now(); settings.Save();
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, false);
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kubism", current.ToString()));
            var json = JsonNode.Parse(await http.GetStringAsync(LatestReleaseUrl, ct)) as JsonObject;
            var tag = json?["tag_name"]?.ToString()?.TrimStart('v');
            if (tag is null || !Version.TryParse(tag, out var latest)) return null;
            // The assembly version is 4 parts (0.2.1.0), a tag is 3 (0.2.1): 0.2.1 < 0.2.1.0 for System.Version, so compare 3 parts.
            static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
            return new UpdateInfo(tag, json?["html_url"]?.ToString() ?? "", Norm(latest) > Norm(current));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Kubism.Core.AppLog.Warn("Update check failed", ex);
            if (manual) throw;
            return null;
        }
    }
}
