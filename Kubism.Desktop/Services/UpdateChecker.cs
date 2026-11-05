using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Kubism.Desktop.Services;

/// <param name="Url">The release page (notes).</param>
/// <param name="AssetUrl">Direct download for this platform, or null when the release has no matching file.</param>
public sealed record UpdateInfo(string Version, string Url, bool IsNewer, string? AssetUrl = null);

/// <summary>Opt-in check against GitHub releases, at most once a day. It never downloads or installs anything.</summary>
public sealed class UpdateChecker(Kubism.Core.Catalog.ISettingsStore settings, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/pgourlain/kubism/releases/latest";
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>What the last check found, without asking GitHub: lets the banner show at every launch. Null when nothing newer is known or the check is off.</summary>
    public UpdateInfo? Known(Version current)
    {
        var s = settings.Current;
        if (!s.CheckForUpdates || s.LatestVersion is not { } tag || !Version.TryParse(tag, out var latest)) return null;
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
        return Norm(latest) > Norm(current) ? new UpdateInfo(tag, s.LatestUrl ?? "", true, s.LatestAssetUrl) : null;
    }

    /// <summary>The runtime identifier the release files are named after: win-x64, linux-x64, osx-arm64, osx-x64.</summary>
    public static string Rid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            return $"{os}-{arch}";
        }
    }

    /// <summary>Release files are named <c>Kubism-&lt;version&gt;-&lt;rid&gt;.&lt;ext&gt;</c>; the disk image is preferred on macOS, the archive elsewhere. Checksums and signatures are never offered.</summary>
    public static string? PickAsset(IEnumerable<(string Name, string Url)> assets, string rid)
    {
        static int Rank(string name) => name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ? 0 : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? 1 : 9;
        return assets.Where(a => a.Name.StartsWith("Kubism-", StringComparison.OrdinalIgnoreCase) && a.Name.Contains($"-{rid}.", StringComparison.OrdinalIgnoreCase) && Rank(a.Name) < 9
                                 && Uri.TryCreate(a.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            .OrderBy(a => Rank(a.Name)).Select(a => a.Url).FirstOrDefault();
    }

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
            var url = json?["html_url"]?.ToString() ?? "";
            var asset = PickAsset((json?["assets"] as JsonArray ?? []).OfType<JsonObject>().Select(a => (a["name"]?.ToString() ?? "", a["browser_download_url"]?.ToString() ?? "")), Rid);
            s.LatestVersion = tag; s.LatestUrl = url; s.LatestAssetUrl = asset;
            settings.Save();
            return new UpdateInfo(tag, url, Norm(latest) > Norm(current), asset);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Kubism.Core.AppLog.Warn("Update check failed", ex);
            if (manual) throw;
            return null;
        }
    }
}
