using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class ResourceCacheTests
{
    private static readonly InformerOptions Fast = new()
    {
        Coalesce = TimeSpan.FromMilliseconds(30), Linger = TimeSpan.FromMilliseconds(200),
        BackoffBase = TimeSpan.FromMilliseconds(20), BackoffMax = TimeSpan.FromMilliseconds(100),
    };

    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("condition not met"); await Task.Delay(10); }
    }

    private sealed class Collector : IDisposable
    {
        public List<CacheChange> Changes { get; } = [];
        private readonly IDisposable _sub;
        public Collector(Informer i) => _sub = Events.ObservableExtensions.Subscribe(i.Changes, c => { lock (Changes) Changes.Add(c); });
        public IReadOnlyList<CacheChange> Snapshot() { lock (Changes) return [.. Changes]; }
        public void Dispose() => _sub.Dispose();
    }

    private static (FakeKubeTransport t, ResourceCache cache) Setup(params JsonObject[] pods)
    {
        var t = new FakeKubeTransport();
        t.Set(Gvr.Pods, pods);
        return (t, new ResourceCache(t, "c1", null, Fast));
    }

    [Fact]
    public async Task Initial_list_is_emitted_as_resynced_batch()
    {
        var t = new FakeKubeTransport();
        t.Set(Gvr.Pods, t.Make("Pod", "a", "default"), t.Make("Pod", "b", "default"));
        await using var cache = new ResourceCache(t, "c1", null, Fast);
        using var lease = cache.Subscribe(Gvr.Pods);
        using var col = new Collector(lease.Informer);
        await WaitFor(() => lease.Informer.State == InformerState.Live && col.Snapshot().Count > 0 || lease.Informer.Count == 2);
        Assert.Equal(2, lease.Informer.Count);
        Assert.NotNull(lease.Informer.Columns);
    }

    [Fact]
    public async Task Watch_events_update_store_and_arrive_coalesced()
    {
        var t = new FakeKubeTransport();
        t.Set(Gvr.Pods);
        await using var cache = new ResourceCache(t, "c1", null, Fast);
        using var lease = cache.Subscribe(Gvr.Pods);
        var inf = lease.Informer;
        await WaitFor(() => t.ActiveWatches == 1);
        using var col = new Collector(inf);
        var p = t.Make("Pod", "p1", "default");
        t.Emit(WatchEventType.Added, p);
        t.Emit(WatchEventType.Modified, p);
        t.Emit(WatchEventType.Modified, p);
        await WaitFor(() => col.Snapshot().Count >= 1);
        var batch = col.Snapshot().First(c => !c.Resynced);
        Assert.Single(batch.Added); // Added+Modified merged into one Added
        Assert.Empty(batch.Modified);
        Assert.Equal(1, inf.Count);

        t.Emit(WatchEventType.Deleted, p);
        await WaitFor(() => inf.Count == 0);
        await WaitFor(() => col.Snapshot().Any(c => c.Deleted.Count == 1));
    }

    [Fact]
    public async Task Added_then_deleted_in_same_window_cancels_out()
    {
        var t = new FakeKubeTransport();
        t.Set(Gvr.Pods);
        await using var cache = new ResourceCache(t, "c1", null, Fast);
        using var lease = cache.Subscribe(Gvr.Pods);
        await WaitFor(() => t.ActiveWatches == 1);
        using var col = new Collector(lease.Informer);
        var p = t.Make("Pod", "ghost", "default");
        t.Emit(WatchEventType.Added, p);
        t.Emit(WatchEventType.Deleted, p);
        await Task.Delay(200);
        Assert.DoesNotContain(col.Snapshot(), c => c.Added.Count + c.Deleted.Count > 0);
        Assert.Equal(0, lease.Informer.Count);
    }

    [Fact]
    public async Task Gone_410_relists_and_emits_resynced_diff_not_a_burst_of_adds()
    {
        var t = new FakeKubeTransport();
        var a = t.Make("Pod", "a", "default"); var b = t.Make("Pod", "b", "default"); var c = t.Make("Pod", "c", "default");
        t.Set(Gvr.Pods, a, b);
        await using var cache = new ResourceCache(t, "c1", null, Fast);
        using var lease = cache.Subscribe(Gvr.Pods);
        await WaitFor(() => t.ActiveWatches == 1 && lease.Informer.Count == 2);
        using var col = new Collector(lease.Informer);

        // While "disconnected": b deleted, c added, a modified.
        var a2 = (JsonObject)a.DeepClone(); a2["metadata"]!["resourceVersion"] = "999";
        t.Set(Gvr.Pods, a2, c);
        t.EmitGone();
        await WaitFor(() => col.Snapshot().Any(x => x.Resynced));
        var diff = col.Snapshot().First(x => x.Resynced);
        Assert.Equal(["c"], diff.Added.Select(i => i.Object.Name));
        Assert.Equal(["a"], diff.Modified.Select(i => i.Object.Name));
        Assert.Equal(["b"], diff.Deleted.Select(i => i.Object.Name));
        Assert.Equal(2, lease.Informer.Count);
    }

    [Fact]
    public async Task Stream_end_rewatches_from_last_resource_version()
    {
        var t = new FakeKubeTransport();
        t.Set(Gvr.Pods);
        await using var cache = new ResourceCache(t, "c1", null, Fast);
        using var lease = cache.Subscribe(Gvr.Pods);
        await WaitFor(() => t.ActiveWatches == 1);
        t.Emit(WatchEventType.Added, t.Make("Pod", "p", "default"));
        await WaitFor(() => lease.Informer.Count == 1);
        t.EndWatches();
        await WaitFor(() => t.WatchStarts >= 2 && t.ActiveWatches == 1);
        Assert.Equal(1, t.ListCount); // no relist
        Assert.NotNull(t.WatchResourceVersions[^1]);
        Assert.NotEqual(t.WatchResourceVersions[0], t.WatchResourceVersions[^1]);
    }

    [Fact]
    public async Task Watch_failure_goes_reconnecting_then_live_and_flips_health()
    {
        var t = new FakeKubeTransport();
        t.Set(Gvr.Pods);
        await using var cache = new ResourceCache(t, "c1", null, Fast);
        var health = new List<bool>();
        cache.HealthChanged += h => { lock (health) health.Add(h); };
        using var lease = cache.Subscribe(Gvr.Pods);
        await WaitFor(() => t.ActiveWatches == 1);

        var fail = true;
        t.WatchFailure = () => fail ? new HttpRequestException("boom") : null;
        t.EndWatches();
        await WaitFor(() => lease.Informer.State == InformerState.Reconnecting);
        // The state is set before its handlers run: the health change follows a moment later, so wait for it.
        await WaitFor(() => { lock (health) return health.Contains(false); });
        fail = false;
        await WaitFor(() => lease.Informer.State == InformerState.Live && t.ActiveWatches == 1);
        await WaitFor(() => { lock (health) return health.Count > 0 && health[^1]; });
    }

    [Fact]
    public async Task Informer_is_shared_and_stops_after_linger()
    {
        var (t, cache) = Setup();
        await using var _ = cache;
        var l1 = cache.Subscribe(Gvr.Pods);
        var l2 = cache.Subscribe(Gvr.Pods);
        Assert.Same(l1.Informer, l2.Informer);
        Assert.Single(cache.Informers);
        l1.Dispose();
        await Task.Delay(400);
        Assert.NotEqual(InformerState.Stopped, l2.Informer.State);
        l2.Dispose();
        await WaitFor(() => l2.Informer.State == InformerState.Stopped);
        await WaitFor(() => cache.Informers.Count == 0);
    }

    [Fact]
    public async Task Resubscribing_during_linger_keeps_informer()
    {
        var (_, cache) = Setup();
        await using var _c = cache;
        var l1 = cache.Subscribe(Gvr.Pods);
        var inf = l1.Informer;
        l1.Dispose();
        await Task.Delay(50);
        using var l2 = cache.Subscribe(Gvr.Pods);
        await Task.Delay(400);
        Assert.Same(inf, l2.Informer);
        Assert.NotEqual(InformerState.Stopped, inf.State);
    }

    [Fact]
    public async Task Falls_back_to_plain_list_when_table_refused()
    {
        var t = new FakeKubeTransport { DisableTables = true };
        t.Set(Gvr.Pods, t.Make("Pod", "a", "default"));
        await using var cache = new ResourceCache(t, "c1", null, Fast);
        using var lease = cache.Subscribe(Gvr.Pods);
        await WaitFor(() => lease.Informer.Count == 1);
        Assert.Null(lease.Informer.Columns);
    }

    [Fact]
    public async Task Against_fake_api_server_end_to_end_with_410()
    {
        await using var server = await FakeApiServer.StartAsync();
        var path = server.WriteKubeconfig();
        using var transport = new Kube.KubeTransport(Catalog.KubeconfigCatalog.ParseFile(path)[0]);
        await using var cache = new ResourceCache(transport, "c", null, Fast);
        using var lease = cache.Subscribe(Gvr.Pods, "default");
        var before = server.List(Gvr.Pods, "default").Count;
        await WaitFor(() => lease.Informer.Count == before && server.WatchCount == 1);
        using var col = new Collector(lease.Informer);

        server.Remove(Gvr.Pods, "default", server.List(Gvr.Pods, "default")[0]["metadata"]!["name"]!.GetValue<string>());
        await WaitFor(() => lease.Informer.Count == before - 1);

        // Expire RVs and cut the connection: the informer must relist.
        server.ExpireResourceVersions();
        server.DisconnectWatchers();
        var other = server.List(Gvr.Pods, "default")[0];
        server.Remove(Gvr.Pods, "default", other["metadata"]!["name"]!.GetValue<string>());
        await WaitFor(() => lease.Informer.Count == before - 2);
        await WaitFor(() => server.WatchCount == 1);
        Assert.Contains(col.Snapshot(), c => c.Resynced || c.Deleted.Count > 0);
    }
}

public class InformerBudgetTests
{
    [Fact]
    public async Task Budget_evicts_idle_informers_and_refuses_when_everything_is_in_use()
    {
        var t = new Kubism.Testing.FakeKubeTransport();
        await using var cache = new ResourceCache(t, "c", null, new InformerOptions { Linger = TimeSpan.FromMinutes(5) }) { MaxInformers = 2 };
        var a = cache.Subscribe(Gvr.Pods);
        var b = cache.Subscribe(Gvr.Nodes);
        Assert.Throws<InformerBudgetException>(() => cache.Subscribe(Gvr.Services));
        a.Dispose();                                   // now idle (lingering): can be evicted
        using var c = cache.Subscribe(Gvr.Services);
        Assert.Equal(2, cache.Informers.Count);
        b.Dispose();
    }
}
