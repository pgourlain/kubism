using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Kube;
using Kubism.Testing;
using Xunit;

namespace Kubism.Kube.Tests;

public class ExecAuthTests : IAsyncLifetime
{
    private FakeApiServer _server = null!;
    private string _dir = "";

    public async ValueTask InitializeAsync()
    {
        _server = await FakeApiServer.StartAsync();
        _dir = Path.Combine(Path.GetTempPath(), "kubism-exec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public async ValueTask DisposeAsync() { await _server.DisposeAsync(); try { Directory.Delete(_dir, true); } catch { } }

    private string WritePlugin(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private ClusterEntry EntryWithExec(string command, string expires = "2099-01-01T00:00:00Z", string args = "")
    {
        var cfg = Path.Combine(_dir, "kubeconfig");
        File.WriteAllText(cfg, $$"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: c
              cluster: {server: "{{_server.Url}}"}
            users:
            - name: u
              user:
                exec:
                  apiVersion: client.authentication.k8s.io/v1
                  command: {{command}}
                  {{args}}
                  interactiveMode: Never
            contexts:
            - name: ctx
              context: {cluster: c, user: u}
            current-context: ctx
            """.Replace("\r\n", "\n"));
        return KubeconfigCatalog.ParseFile(cfg)[0];
    }

    private static string Cred(string token, string exp) =>
        $"echo '{{\"apiVersion\":\"client.authentication.k8s.io/v1\",\"kind\":\"ExecCredential\",\"status\":{{\"token\":\"{token}\",\"expirationTimestamp\":\"{exp}\"}}}}'";

    [SkippableOnWindowsFact]
    public async Task Exec_plugin_token_is_used_and_stderr_is_surfaced_for_device_code_login()
    {
        var plugin = WritePlugin("fake-login", "echo 'Open https://microsoft.com/devicelogin and enter code ABCD-1234' >&2\n" + Cred("fake-token", "2099-01-01T00:00:00Z"));
        var lines = new List<string>();
        using var t = new KubeTransport(EntryWithExec(plugin), new Obs(lines));
        var v = await t.GetAsync("/version", default);
        Assert.NotNull(v);
        Assert.Contains(lines, l => l.Contains("ABCD-1234"));
        Assert.Contains(_server.Requests, r => r.Authorization == "Bearer fake-token");
    }

    [SkippableOnWindowsFact]
    public async Task Token_is_cached_until_expiry_then_refreshed_before_it_expires()
    {
        var counter = Path.Combine(_dir, "count");
        // Each run appends a line and returns a token that expires in 30 s (inside the 60 s refresh margin: refreshed on every use).
        var exp = DateTimeOffset.UtcNow.AddSeconds(30).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var plugin = WritePlugin("short-lived", $"echo run >> '{counter}'\n" + Cred("fake-token", exp));
        using var t = new KubeTransport(EntryWithExec(plugin));
        await t.GetAsync("/version", default);
        await t.GetAsync("/version", default);
        Assert.True(File.ReadAllLines(counter).Length >= 2);

        var counter2 = Path.Combine(_dir, "count2");
        var plugin2 = WritePlugin("long-lived", $"echo run >> '{counter2}'\n" + Cred("fake-token", "2099-01-01T00:00:00Z"));
        using var t2 = new KubeTransport(EntryWithExec(plugin2));
        await t2.GetAsync("/version", default);
        await t2.GetAsync("/version", default);
        Assert.Single(File.ReadAllLines(counter2));
    }

    [SkippableOnWindowsFact]
    public async Task A_401_forces_a_refresh_of_the_credential()
    {
        var counter = Path.Combine(_dir, "count");
        // First token is rejected by the server, the refreshed one is accepted.
        var plugin = WritePlugin("rotating", $"n=$(wc -l < '{counter}' 2>/dev/null || echo 0)\necho run >> '{counter}'\nif [ \"$n\" -ge 1 ]; then " + Cred("fake-token", "2099-01-01T00:00:00Z") + "; else " + Cred("stale-token", "2099-01-01T00:00:00Z") + "; fi");
        using var t = new KubeTransport(EntryWithExec(plugin));
        var v = await t.GetAsync("/version", default);
        Assert.NotNull(v);
        Assert.Equal(2, File.ReadAllLines(counter).Length);
    }

    [Fact]
    public async Task Missing_plugin_error_names_the_binary_and_the_searched_path()
    {
        using var t = new KubeTransport(EntryWithExec("kubelogin-does-not-exist"));
        var ex = await Assert.ThrowsAsync<KubeConnectionException>(() => t.GetAsync("/version", default));
        Assert.Contains("kubelogin-does-not-exist", ex.Message);
        Assert.Contains("PATH", ex.Message);
    }

    [SkippableOnWindowsFact]
    public async Task Failing_plugin_error_contains_its_stderr()
    {
        var plugin = WritePlugin("broken", "echo 'AADSTS70043: refresh token expired' >&2\nexit 3");
        using var t = new KubeTransport(EntryWithExec(plugin));
        var ex = await Assert.ThrowsAsync<KubeConnectionException>(() => t.GetAsync("/version", default));
        Assert.Contains("AADSTS70043", ex.Message);
        Assert.Contains("exit 3", ex.Message);
    }

    [Fact]
    public void Login_path_resolution_is_cached_and_bounded()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p1 = ShellEnvironment.ResolveLoginPath();
        var first = sw.Elapsed; sw.Restart();
        var p2 = ShellEnvironment.ResolveLoginPath();
        Assert.Equal(p1, p2);
        Assert.True(first < TimeSpan.FromSeconds(5));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(50));
        if (!OperatingSystem.IsWindows()) Assert.Contains("/usr/bin", p1 ?? "/usr/bin");
    }

    private sealed class Obs(List<string> lines) : IExecPluginObserver
    {
        public void OnPluginOutput(string line) { lock (lines) lines.Add(line); }
    }
}

public sealed class SkippableOnWindowsFactAttribute : FactAttribute
{
    public SkippableOnWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "uses a shell script as exec plugin";
    }
}
