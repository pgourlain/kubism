using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Network;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class NetworkStoreTests
{
    private static readonly InformerOptions Fast = new() { Coalesce = TimeSpan.FromMilliseconds(10), BackoffBase = TimeSpan.FromMilliseconds(20), BackoffMax = TimeSpan.FromMilliseconds(50) };
    private static readonly Gvr Ingresses = new("networking.k8s.io", "v1", "ingresses");
    private static readonly Gvr Classes = new("networking.k8s.io", "v1", "ingressclasses");
    private static readonly Gvr Services = new("", "v1", "services");
    private static readonly Gvr Slices = new("discovery.k8s.io", "v1", "endpointslices");
    private static readonly Gvr Pods = new("", "v1", "pods");

    private static FakeKubeTransport Seed()
    {
        var t = new FakeKubeTransport();
        var all = NetworkFixture.Objects();
        JsonObject[] Of(string kind) => all.Where(o => o.Kind == kind).Select(o => (JsonObject)o.Json.DeepClone()).ToArray();
        t.Set(Ingresses, Of("Ingress")); t.Set(Classes, Of("IngressClass")); t.Set(Services, Of("Service")); t.Set(Slices, Of("EndpointSlice")); t.Set(Pods, Of("Pod"));
        // shop-tls exists (no certificate inside: only existence is known), admin-tls does not (the fake answers 404)
        t.SetGet("/api/v1/namespaces/shop/secrets/shop-tls", new JsonObject { ["kind"] = "Secret", ["type"] = "kubernetes.io/tls", ["metadata"] = new JsonObject { ["name"] = "shop-tls", ["namespace"] = "shop" } });
        return t;
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }

    [Fact]
    public async Task The_graph_is_built_from_the_cache_looks_up_tls_secrets_and_follows_changes()
    {
        var t = Seed();
        await using var cache = new ResourceCache(t, "c", options: Fast);
        using var store = new NetworkStore(cache, t, TimeSpan.FromMilliseconds(20));
        await store.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(store.Graph.IsComplete);
        Assert.Contains(store.Graph.Nodes, n => n.Id == "i:shop/shop");
        Assert.Equal(ExposureLevel.Outside, store.Graph.Nodes.Single(n => n.Id == "i:shop/shop").Exposure);

        // the TLS lookups arrive after the first graph: the missing secret becomes a problem, the existing one does not
        Assert.True(await Until(() => store.Graph.Nodes.Single(n => n.Id == "i:shop/admin").Problems.Any(p => p.Code == "tls-secret-missing")));
        Assert.DoesNotContain(store.Graph.Nodes.Single(n => n.Id == "i:shop/shop").Problems, p => p.Code.StartsWith("tls"));
        Assert.Empty(t.Sent);   // lookups use GET (GetAsync), never a write

        // a new Ingress arrives through the watch, and goes away again
        var ing = t.Make("Ingress", "new", "shop", apiVersion: "networking.k8s.io/v1");
        ing["spec"] = new JsonObject
        {
            ["ingressClassName"] = "nginx",
            ["rules"] = new JsonArray(new JsonObject { ["host"] = "new.example.com", ["http"] = new JsonObject { ["paths"] = new JsonArray(new JsonObject { ["path"] = "/", ["pathType"] = "Prefix",
                ["backend"] = new JsonObject { ["service"] = new JsonObject { ["name"] = "web", ["port"] = new JsonObject { ["number"] = 80 } } } }) } }),
        };
        t.Emit(WatchEventType.Added, ing);
        Assert.True(await Until(() => store.Graph.Nodes.Any(n => n.Id == "i:shop/new")));
        Assert.Contains(store.Graph.Edges, e => e.From == "i:shop/new" && e.To == "s:shop/web");
        t.Emit(WatchEventType.Deleted, ing);
        Assert.True(await Until(() => store.Graph.Nodes.All(n => n.Id != "i:shop/new")));
    }

    [Fact]
    public async Task A_kind_the_user_cannot_list_is_reported()
    {
        var t = Seed();
        t.ListFailure = (gvr, _) => gvr == Slices ? new KubeApiException(403, "Forbidden", "endpointslices.discovery.k8s.io is forbidden") : null;
        await using var cache = new ResourceCache(t, "c", options: Fast);
        using var store = new NetworkStore(cache, t, TimeSpan.FromMilliseconds(20));
        await store.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(store.Graph.IsComplete);
        Assert.Equal(["endpointslices"], store.Graph.MissingKinds);
        Assert.Contains("forbidden", store.Graph.IncompleteWarning);
    }

    [Fact]
    public async Task The_one_shot_loader_lists_every_kind_and_reports_missing_ones()
    {
        var t = Seed();
        var g = await NetworkLoader.LoadAsync(t, CancellationToken.None);
        Assert.True(g.IsComplete);
        Assert.Contains(g.Nodes.Single(n => n.Id == "i:shop/admin").Problems, p => p.Code == "tls-secret-missing");
        t.ListFailure = (gvr, _) => gvr == Pods ? new KubeApiException(403, "Forbidden", "pods is forbidden") : null;
        var partial = await NetworkLoader.LoadAsync(t, CancellationToken.None, checkTls: false);
        Assert.Equal(["pods"], partial.MissingKinds);
        Assert.DoesNotContain(partial.Nodes, n => n.Kind == NetworkNodeKind.Workload);
    }
}
