using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Kube;
using Kubism.Testing;
using Xunit;

namespace Kubism.Kube.Tests;

public class TransportTests : IAsyncLifetime
{
    private FakeApiServer _server = null!;
    private KubeTransport _t = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeApiServer.StartAsync();
        var path = _server.WriteKubeconfig();
        _t = new KubeTransport(KubeconfigCatalog.ParseFile(path)[0]);
    }

    public async ValueTask DisposeAsync() { _t.Dispose(); await _server.DisposeAsync(); }

    [Fact]
    public async Task Version_is_read_with_token_auth()
    {
        var v = await _t.GetAsync("/version", TestContext.Current.CancellationToken);
        Assert.Equal("v1.31.0-fake", v!["gitVersion"]!.GetValue<string>());
        Assert.All(_server.Requests, r => Assert.Equal("Bearer fake-token", r.Authorization));
    }

    [Fact]
    public async Task Unauthorized_maps_to_api_exception()
    {
        _server.Token = "other";
        var ex = await Assert.ThrowsAsync<KubeApiException>(() => _t.GetAsync("/version", TestContext.Current.CancellationToken));
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task List_as_table_returns_columns_and_objects()
    {
        var l = await _t.ListAsync(Gvr.Pods, "default", new ListOptions { AsTable = true }, TestContext.Current.CancellationToken);
        Assert.NotNull(l.Columns);
        Assert.Contains(l.Columns!, c => c.Name == "Status");
        Assert.Equal(l.Items.Count, l.Cells!.Count);
        Assert.All(l.Items, i => Assert.Equal("Pod", i["kind"]!.GetValue<string>()));
        Assert.Contains(_server.Requests, r => r.Accept?.Contains("as=Table") == true);
    }

    [Fact]
    public async Task List_plain_restores_kind_on_items()
    {
        var l = await _t.ListAsync(Gvr.Deployments, null, new ListOptions(), TestContext.Current.CancellationToken);
        Assert.Null(l.Columns);
        Assert.NotEmpty(l.Items);
        Assert.All(l.Items, i => Assert.Equal("Deployment", i["kind"]!.GetValue<string>()));
    }

    [Fact]
    public async Task List_paginates_with_continue()
    {
        var first = await _t.ListAsync(Gvr.Pods, null, new ListOptions { Limit = 5, AsTable = true }, TestContext.Current.CancellationToken);
        Assert.Equal(5, first.Items.Count);
        Assert.False(string.IsNullOrEmpty(first.Continue));
        var second = await _t.ListAsync(Gvr.Pods, null, new ListOptions { Limit = 5, Continue = first.Continue, AsTable = true }, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Items[0]["metadata"]!["name"]!.ToString(), second.Items[0]["metadata"]!["name"]!.ToString());
    }

    [Fact]
    public async Task Watch_streams_added_modified_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var list = await _t.ListAsync(Gvr.ConfigMaps, "default", new ListOptions(), ct);
        var events = new List<WatchEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(async () =>
        {
            await foreach (var e in _t.WatchAsync(Gvr.ConfigMaps, "default", list.ResourceVersion, false, cts.Token))
            {
                events.Add(e);
                if (events.Count == 3) { cts.Cancel(); }
            }
        }, ct);
        await WaitFor(() => _server.WatchCount == 1);
        var cm = JsonNode.Parse("""{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"w1","namespace":"default"},"data":{"a":"1"}}""")!.AsObject();
        _server.Upsert(cm);
        _server.Upsert((JsonObject)_server.Get(Gvr.ConfigMaps, "default", "w1")!);
        _server.Remove(Gvr.ConfigMaps, "default", "w1");
        try { await task; } catch (OperationCanceledException) { }
        Assert.Equal([WatchEventType.Added, WatchEventType.Modified, WatchEventType.Deleted], events.Select(e => e.Type));
    }

    [Fact]
    public async Task Watch_reports_410_error_event_for_old_resource_version()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.ExpireResourceVersions();
        var events = new List<WatchEvent>();
        await foreach (var e in _t.WatchAsync(Gvr.Pods, null, "1001", false, ct)) events.Add(e);
        var ev = Assert.Single(events);
        Assert.Equal(WatchEventType.Error, ev.Type);
        Assert.Equal(410, ev.ErrorCode);
    }

    [Fact]
    public async Task Watch_as_table_carries_cells()
    {
        var ct = TestContext.Current.CancellationToken;
        var list = await _t.ListAsync(Gvr.Pods, "default", new ListOptions { AsTable = true }, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        WatchEvent? got = null;
        var task = Task.Run(async () =>
        {
            await foreach (var e in _t.WatchAsync(Gvr.Pods, "default", list.ResourceVersion, true, cts.Token)) { got = e; cts.Cancel(); }
        }, ct);
        await WaitFor(() => _server.WatchCount == 1);
        var pod = _server.List(Gvr.Pods, "default")[0];
        pod["metadata"]!["labels"] ??= new JsonObject();
        pod["metadata"]!["labels"]!["touched"] = "yes";
        _server.Upsert(pod);
        try { await task; } catch (OperationCanceledException) { }
        Assert.NotNull(got);
        Assert.NotNull(got!.Cells);
        Assert.Equal("Pod", got.Object["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Discovery_aggregated_finds_pods_and_crd()
    {
        var d = new ApiDiscovery(_t);
        await d.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(d.UsedAggregated);
        var pods = d.FindByName("po")!;
        Assert.Equal("Pod", pods.Kind);
        Assert.True(pods.Namespaced);
        Assert.True(pods.Has("log"));
        Assert.NotNull(d.FindByName("certificates"));
    }

    [Fact]
    public async Task Discovery_legacy_fallback()
    {
        _server.AggregatedDiscovery = false;
        var d = new ApiDiscovery(_t);
        await d.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(d.UsedAggregated);
        Assert.NotNull(d.FindByName("deploy"));
        Assert.True(d.FindByName("pods")!.Has("eviction"));
    }

    [Fact]
    public async Task Connection_refused_maps_to_connection_exception()
    {
        var path = _server.WriteKubeconfig();
        await _server.DisposeAsync();
        using var t = new KubeTransport(KubeconfigCatalog.ParseFile(path)[0]);
        await Assert.ThrowsAsync<KubeConnectionException>(() => t.GetAsync("/version", TestContext.Current.CancellationToken));
        _server = await FakeApiServer.StartAsync(); // for DisposeAsync
    }

    internal static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("condition not met"); await Task.Delay(20); }
    }
}
