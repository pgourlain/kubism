using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Kubism.Desktop.Services;

public sealed record UpdateInfo(string Version, string Url, bool IsNewer);

/// <summary>Opt-in check against GitHub releases, at most once a day. It never downloads or installs anything.</summary>
public sealed class UpdateChecker(Kubism.Core.Catalog.ISettingsStore settings, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/pgourlain/kubism/releases/latest";
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        var s = settings.Current;
        if (!s.CheckForUpdates) return null;
        if (s.LastUpdateCheck is { } last && _now() - last < TimeSpan.FromHours(23)) return null;
        s.LastUpdateCheck = _now(); settings.Save();
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, false);
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kubism", current.ToString()));
            var json = JsonNode.Parse(await http.GetStringAsync(LatestReleaseUrl, ct)) as JsonObject;
            var tag = json?["tag_name"]?.ToString()?.TrimStart('v');
            if (tag is null || !Version.TryParse(tag, out var latest)) return null;
            return new UpdateInfo(tag, json?["html_url"]?.ToString() ?? "", latest > current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }
}
