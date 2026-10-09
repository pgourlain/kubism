using Kubism.Core.Cache;
using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.Sessions;
using Kubism.Kube;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class SessionTests : IAsyncLifetime
{
    private FakeApiServer _server = null!;
    private ClusterEntry _entry = null!;
    private readonly SettingsStore _settings = new();
    private readonly ClusterEventsService _events = new();

    public async ValueTask InitializeAsync()
    {
        _server = await FakeApiServer.StartAsync();
        _entry = KubeconfigCatalog.ParseFile(_server.WriteKubeconfig())[0];
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private ClusterSession Make(TimeSpan? health = null) => new(_entry, new KubeTransportFactory(), _events, _settings, health ?? TimeSpan.FromMilliseconds(100));

    private static async Task WaitFor(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("condition not met"); await Task.Delay(20); }
    }

    [Fact]
    public async Task Connect_reads_version_and_discovers_kinds_with_ordered_events()
    {
        var seen = new List<SessionState>();
        _events.Subscribe(e => { if (e is SessionStateChanged s) lock (seen) seen.Add(s.State); });
        await using var s = Make();
        await s.ConnectAsync();
        Assert.Equal(SessionState.Connected, s.State);
        Assert.Equal("v1.31.0-fake", s.ServerVersion);
        Assert.NotEmpty(s.Discovery.Kinds);
        await s.DisconnectAsync();
        lock (seen) Assert.Equal([SessionState.Connecting, SessionState.Connected, SessionState.Disconnected], seen);
    }

    [Fact]
    public async Task Unreachable_server_ends_in_Error_with_message()
    {
        await _server.DisposeAsync();
        await using var s = Make();
        await Assert.ThrowsAnyAsync<Exception>(() => s.ConnectAsync());
        Assert.Equal(SessionState.Error, s.State);
        Assert.False(string.IsNullOrEmpty(s.ErrorMessage));
        _server = await FakeApiServer.StartAsync();
    }

    [Fact]
    public async Task Bad_token_reports_authentication_failure()
    {
        _server.Token = "different";
        await using var s = Make();
        await Assert.ThrowsAsync<Api.KubeApiException>(() => s.ConnectAsync());
        Assert.Contains("401", s.ErrorMessage);
    }

    [Fact]
    public async Task Server_down_is_Degraded_then_back_to_Connected()
    {
        await using var s = Make(TimeSpan.FromMilliseconds(60));
        await s.ConnectAsync();
        _server.FailAll = true;
        await WaitFor(() => s.State == SessionState.Degraded);
        _server.FailAll = false;
        await WaitFor(() => s.State == SessionState.Connected, 15000);
    }

    [Fact]
    public async Task Disconnect_cancels_session_token_and_pending_work()
    {
        var s = Make();
        await s.ConnectAsync();
        var token = s.SessionToken;
        var pending = Task.Delay(Timeout.Infinite, token);
        using var lease = s.Cache.Subscribe(Api.Gvr.Pods);
        await WaitFor(() => lease.Informer.Count > 0);
        await s.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(SessionState.Disconnected, s.State);
        Assert.Equal(InformerState.Stopped, lease.Informer.State);
    }

    [Fact]
    public async Task Manager_keeps_one_session_per_cluster_and_several_clusters_at_once()
    {
        await using var second = await FakeApiServer.StartAsync();
        var e2 = KubeconfigCatalog.ParseFile(second.WriteKubeconfig())[0];
        await using var mgr = new ClusterSessionManager(new KubeTransportFactory(), _events, _settings);
        var a = await mgr.ConnectAsync(_entry);
        var b = await mgr.ConnectAsync(e2);
        var a2 = await mgr.ConnectAsync(_entry);
        Assert.Same(a, a2);
        Assert.Equal(2, mgr.Sessions.Count);
        await mgr.DisconnectAsync(_entry.Id);
        Assert.Single(mgr.Sessions);
        Assert.Equal(SessionState.Connected, b.State);
    }
}
