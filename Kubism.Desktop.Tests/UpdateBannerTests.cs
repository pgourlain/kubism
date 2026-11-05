using System.Net;
using Avalonia.Headless.XUnit;
using Kubism.Core.Catalog;
using Kubism.Desktop.Services;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UpdateBannerTests
{
    private static readonly (string, string)[] Assets =
    [
        ("Kubism-0.4.0-osx-arm64.dmg", "https://example.test/Kubism-0.4.0-osx-arm64.dmg"),
        ("Kubism-0.4.0-osx-arm64.zip", "https://example.test/Kubism-0.4.0-osx-arm64.zip"),
        ("Kubism-0.4.0-win-x64.zip", "https://example.test/Kubism-0.4.0-win-x64.zip"),
        ("Kubism-0.4.0-linux-x64.tar.gz", "https://example.test/Kubism-0.4.0-linux-x64.tar.gz"),
        ("Kubism-0.4.0-win-x64.zip.sha256", "https://example.test/Kubism-0.4.0-win-x64.zip.sha256"),
        ("PKGBUILD", "https://example.test/PKGBUILD"),
    ];

    [Theory]
    [InlineData("osx-arm64", "https://example.test/Kubism-0.4.0-osx-arm64.dmg")]
    [InlineData("osx-x64", null)]   // no Intel build any more: the banner falls back to the release page
    [InlineData("win-x64", "https://example.test/Kubism-0.4.0-win-x64.zip")]
    [InlineData("linux-x64", "https://example.test/Kubism-0.4.0-linux-x64.tar.gz")]
    [InlineData("linux-arm64", null)]
    public void The_download_is_the_file_of_this_platform(string rid, string? expected) =>
        Assert.Equal(expected, UpdateChecker.PickAsset(Assets, rid));

    [Fact]
    public void Only_https_downloads_are_offered() =>
        Assert.Null(UpdateChecker.PickAsset([("Kubism-1-win-x64.zip", "http://example.test/a.zip"), ("Kubism-1-win-x64.zip", "javascript:alert(1)")], "win-x64"));

    [Fact]
    public async Task A_check_keeps_what_it_found_so_the_next_launch_needs_no_network()
    {
        var settings = new SettingsStore();
        settings.Current.CheckForUpdates = true;
        var assets = string.Join(",", Assets.Select(a => $$"""{"name":"{{a.Item1}}","browser_download_url":"{{a.Item2}}"}"""));
        var handler = new StubHandler($$"""{"tag_name":"v0.4.0","html_url":"https://example.test/r","assets":[{{assets}}]}""");
        var info = await new UpdateChecker(settings, handler).CheckAsync(new Version(0, 3, 9));
        Assert.True(info!.IsNewer);
        Assert.Equal(("0.4.0", "https://example.test/r"), (settings.Current.LatestVersion, settings.Current.LatestUrl));

        var offline = new UpdateChecker(settings);   // no handler: a network call would fail
        Assert.Equal("0.4.0", offline.Known(new Version(0, 3, 9, 0))!.Version);
        Assert.Null(offline.Known(new Version(0, 4, 0, 0)));   // updated since
        settings.Current.CheckForUpdates = false;
        Assert.Null(offline.Known(new Version(0, 3, 9)));      // opt-out: nothing shown
    }

    [AvaloniaFact]
    public async Task Banner_shows_the_known_version_downloads_and_stays_closed_for_it()
    {
        await using var h = await UiHarness.StartAsync();
        var s = h.Main.Settings.Current;
        s.CheckForUpdates = true; s.LatestVersion = "9.9.9"; s.LatestUrl = "https://example.test/notes"; s.LatestAssetUrl = "https://example.test/Kubism.dmg";
        h.Main.ShowKnownUpdate();
        Assert.True(h.Main.HasUpdate);
        Assert.Contains("9.9.9", h.Main.UpdateText);
        Assert.True(h.Main.CanDownloadUpdate);
        h.Screenshot("63-update-banner");

        h.Main.DownloadUpdateCommand.Execute(null);
        h.Main.OpenReleaseNotesCommand.Execute(null);
        Assert.Equal(["https://example.test/Kubism.dmg", "https://example.test/notes"], h.Browser.Opened);

        // Closing hides this version, also at the next launch.
        h.Main.DismissUpdateCommand.Execute(null);
        Assert.False(h.Main.HasUpdate);
        h.Main.ShowKnownUpdate();
        Assert.False(h.Main.HasUpdate);

        // A newer release brings it back.
        s.LatestVersion = "9.9.10";
        h.Main.ShowKnownUpdate();
        Assert.True(h.Main.HasUpdate);

        // Without a file for this platform the download opens the release page.
        s.LatestAssetUrl = null;
        h.Main.ShowKnownUpdate();
        Assert.False(h.Main.CanDownloadUpdate);
    }

    [AvaloniaFact]
    public async Task Nothing_is_shown_when_checks_are_off_or_the_app_is_current()
    {
        await using var h = await UiHarness.StartAsync();
        var s = h.Main.Settings.Current;
        s.LatestVersion = "9.9.9";
        s.CheckForUpdates = false;
        h.Main.ShowKnownUpdate();
        Assert.False(h.Main.HasUpdate);
        s.CheckForUpdates = true; s.LatestVersion = "0.0.1";
        h.Main.ShowKnownUpdate();
        Assert.False(h.Main.HasUpdate);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
