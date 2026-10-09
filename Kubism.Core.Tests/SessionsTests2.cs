using System.Net;
using System.Net.Sockets;
using System.Text;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.Exec;
using Kubism.Core.Forward;
using Kubism.Core.Logs;
using Kubism.Core.Metrics;
using Kubism.Core.Sessions;
using Kubism.Kube;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class StreamingSessionTests : IAsyncLifetime
{
    private FakeApiServer _server = null!;
    private ClusterSession _session = null!;
    private readonly SettingsStore _settings = new();

    public async ValueTask InitializeAsync()
    {
        _server = await FakeApiServer.StartAsync();
        var entry = KubeconfigCatalog.ParseFile(_server.WriteKubeconfig())[0];
        _session = new ClusterSession(entry, new KubeTransportFactory(), new ClusterEventsService(), _settings);
        await _session.ConnectAsync();
    }

    public async ValueTask DisposeAsync() { await _session.DisposeAsync(); await _server.DisposeAsync(); }

    private static async Task WaitFor(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("condition not met"); await Task.Delay(20); }
    }

    private static string FirstPod(FakeApiServer s, string ns) => s.List(Gvr.Pods, ns).First()["metadata"]!["name"]!.GetValue<string>();

    [Fact]
    public async Task Log_session_reads_tail_then_follows_new_lines()
    {
        var pod = FirstPod(_server, "default");
        var buffer = new LogBuffer(1000);
        await using var log = new LogSession(_session.Transport, new LogOptions("default", pod, "main", TailLines: 5), buffer);
        log.Start();
        await WaitFor(() => buffer.Count == 5);
        _server.AppendLog("default", pod, "INFO fresh line");
        await WaitFor(() => buffer.Count == 6);
        var last = buffer.Snapshot()[^1];
        Assert.Equal("INFO fresh line", last.Text);
        Assert.NotNull(last.Time);
        Assert.Contains("tailLines=5", _server.Requests.Last(r => r.Path.EndsWith("/log")).Query);
    }

    [Fact]
    public async Task Log_session_previous_container()
    {
        var pod = FirstPod(_server, "default");
        var buffer = new LogBuffer(1000);
        await using var log = new LogSession(_session.Transport, new LogOptions("default", pod, "main", Follow: false, Previous: true), buffer);
        log.Start();
        await WaitFor(() => log.State == LogState.Ended);
        Assert.Contains(buffer.Snapshot(), l => l.Text.Contains("previous container crashed"));
    }

    [Fact]
    public void Log_buffer_is_bounded_and_counts_dropped()
    {
        var b = new LogBuffer(100);
        for (var i = 0; i < 1000; i++) b.Add(null, $"l{i}", "s");
        Assert.Equal(100, b.Count);
        Assert.Equal(900, b.Dropped);
        Assert.Equal("l999", b.Snapshot()[^1].Text);
        Assert.Equal("l900", b.Snapshot()[0].Text);
    }

    [Fact]
    public void Log_buffer_merges_by_timestamp()
    {
        var b = new LogBuffer(100);
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        b.Add(t0.AddSeconds(1), "b1", "b"); b.Add(t0.AddSeconds(3), "b3", "b");
        b.Add(t0.AddSeconds(2), "a2", "a"); b.Add(t0.AddSeconds(0), "a0", "a");
        Assert.Equal(["a0", "b1", "a2", "b3"], b.Snapshot().Select(l => l.Text));
    }

    [Fact]
    public void Log_line_parsing()
    {
        var (t, text) = LogSession.Parse("2026-01-01T10:20:30.123456789Z hello world");
        Assert.NotNull(t); Assert.Equal("hello world", text);
        var (t2, text2) = LogSession.Parse("no timestamp here");
        Assert.Null(t2); Assert.Equal("no timestamp here", text2);
    }

    [Fact]
    public async Task Workload_logs_merge_all_pods_of_a_deployment()
    {
        var deploy = new KubeObject(_server.Get(Gvr.Deployments, "default", "web")!);
        var buffer = new LogBuffer(5000);
        await using var wl = new WorkloadLogSession(_session.Transport, _session.Cache, "default", WorkloadLogSession.SelectorOf(deploy), buffer, 3);
        wl.Start();
        await WaitFor(() => wl.Sources.Count == 3);
        await WaitFor(() => buffer.Count == 9);
        Assert.Equal(3, buffer.Snapshot().Select(l => l.Source).Distinct().Count());
        var times = buffer.Snapshot().Select(l => l.Time!.Value).ToList();
        Assert.Equal(times.OrderBy(t => t), times);
    }

    [Fact]
    public async Task Exec_sends_stdin_resize_and_reports_exit_status()
    {
        var pod = FirstPod(_server, "default");
        await using var exec = await _session.Actions.StartExecAsync("default", pod, "main", null, default);
        var output = new StringBuilder();
        exec.Output += (data, ch) => { lock (output) output.Append(Encoding.UTF8.GetString(data)); };
        await WaitFor(() => { lock (output) return output.ToString().Contains("/ #"); });
        Assert.StartsWith("v", exec.Protocol);
        await exec.SendInputAsync("ls\n");
        await WaitFor(() => { lock (output) return output.ToString().Contains("ls"); });
        await exec.ResizeAsync(120, 40);
        await WaitFor(() => _server.ExecResizes.Count == 1);
        Assert.Equal((120, 40), _server.ExecResizes[0]);
        ExecResult? result = null;
        exec.Exited += r => result = r;
        await exec.SendInputAsync("exit\n");
        await WaitFor(() => result is not null);
        Assert.True(result!.Success);
        Assert.Contains("command=%2Fbin%2Fsh", _server.Requests.Last(r => r.Path.EndsWith("/exec")).Query);
        Assert.Contains("tty=true", _server.Requests.Last(r => r.Path.EndsWith("/exec")).Query);
    }

    [Fact]
    public async Task Exec_on_missing_pod_fails_with_api_message()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _session.Actions.StartExecAsync("default", "ghost", null, null, default));
    }

    [Fact]
    public async Task Exec_is_refused_in_read_only_mode()
    {
        _settings.SetMetadata(_session.Entry.Id, new ClusterMetadata { SafetyMode = SafetyMode.ReadOnly });
        await Assert.ThrowsAsync<SafetyException>(() => _session.Actions.StartExecAsync("default", FirstPod(_server, "default"), null, null, default));
        await Assert.ThrowsAsync<SafetyException>(() => _session.Actions.StartPortForwardAsync(new ForwardTarget(ForwardTargetKind.Pod, "default", "x", 80), 0, false, default));
    }

    [Fact]
    public void Exec_status_parsing()
    {
        var ok = ExecSession.ParseStatus(Encoding.UTF8.GetBytes("""{"status":"Success"}"""));
        Assert.True(ok.Success);
        var fail = ExecSession.ParseStatus(Encoding.UTF8.GetBytes("""{"status":"Failure","message":"command terminated with exit code 3","details":{"causes":[{"reason":"ExitCode","message":"3"}]}}"""));
        Assert.False(fail.Success); Assert.Equal(3, fail.ExitCode);
        var distroless = ExecSession.ParseStatus(Encoding.UTF8.GetBytes("""{"status":"Failure","message":"OCI runtime exec failed: exec failed: executable file not found in $PATH"}"""));
        Assert.Contains("distroless", distroless.Message);
    }

    [Fact]
    public async Task Port_forward_to_pod_serves_http_through_websocket()
    {
        var pod = FirstPod(_server, "default");
        var pf = await _session.Actions.StartPortForwardAsync(new ForwardTarget(ForwardTargetKind.Pod, "default", pod, 8080), 0, false, default);
        Assert.True(pf.LocalPort > 0);
        using var client = new HttpClient();
        var body = await client.GetStringAsync(pf.Url + "/", TestContext.Current.CancellationToken);
        Assert.Equal($"hello from {pod}:8080", body);
        Assert.Single(_session.PortForwards.Forwards);
        await _session.PortForwards.StopAsync(pf.Id);
        Assert.Empty(_session.PortForwards.Forwards);
    }

    [Fact]
    public async Task Port_forward_to_service_resolves_ready_pod_and_target_port()
    {
        var pf = await _session.Actions.StartPortForwardAsync(new ForwardTarget(ForwardTargetKind.Service, "default", "web", 80), 0, false, default);
        using var client = new HttpClient();
        var body = await client.GetStringAsync(pf.Url + "/", TestContext.Current.CancellationToken);
        Assert.StartsWith("hello from web-", body);
        Assert.EndsWith(":8080", body); // service port 80 → targetPort 8080
        Assert.StartsWith("web-", pf.ResolvedPod);
    }

    [Fact]
    public async Task Port_forwards_stop_when_session_disconnects()
    {
        var pod = FirstPod(_server, "default");
        var pf = await _session.Actions.StartPortForwardAsync(new ForwardTarget(ForwardTargetKind.Pod, "default", pod, 8080), 0, false, default);
        var port = pf.LocalPort;
        await _session.DisconnectAsync();
        await Task.Delay(100);
        using var c = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => c.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Metrics_poll_aggregates_and_keeps_history()
    {
        _server.PodMetrics["default"] = new() { ["web-1"] = ("250m", "100Mi") };
        _server.NodeMetrics["node-1"] = ("1", "2Gi");
        await _session.Metrics.PollOnceAsync(default);
        Assert.True(_session.Metrics.Available);
        Assert.Equal(0.25, _session.Metrics.Pods["default/web-1"].CpuCores, 6);
        Assert.Equal(2d * 1024 * 1024 * 1024, _session.Metrics.Nodes["node-1"].MemoryBytes);
        await _session.Metrics.PollOnceAsync(default);
        Assert.Equal(2, _session.Metrics.History("pod/default/web-1").Count);
    }

    [Fact]
    public async Task Metrics_not_installed_is_reported_once_without_throwing()
    {
        _server.MetricsAvailable = false;
        await _session.Metrics.PollOnceAsync(default);
        Assert.False(_session.Metrics.Available);
        Assert.Contains("not installed", _session.Metrics.Message);
        await _session.Metrics.PollOnceAsync(default);
        Assert.Contains("not installed", _session.Metrics.Message);
    }
}
