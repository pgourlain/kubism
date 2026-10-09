using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Core.Events;
using Kubism.Core.Helm;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class DiscoveryTests
{
    private sealed class BrokenGroupTransport : IKubeTransport
    {
        public TimeSpan ClockSkew => default;
        public Task<JsonNode?> GetAsync(string path, CancellationToken ct, string? accept = null) => path switch
        {
            "/api" => Task.FromResult<JsonNode?>(new JsonObject { ["kind"] = "APIVersions", ["versions"] = new JsonArray((JsonNode?)"v1") }),
            "/apis" => Task.FromResult<JsonNode?>(JsonNode.Parse("""{"kind":"APIGroupList","groups":[{"name":"apps","preferredVersion":{"version":"v1"}},{"name":"metrics.k8s.io","preferredVersion":{"version":"v1beta1"}}]}""")),
            "/api/v1" => Task.FromResult<JsonNode?>(JsonNode.Parse("""{"resources":[{"name":"pods","kind":"Pod","namespaced":true,"verbs":["get","list","watch"],"shortNames":["po"]},{"name":"pods/log","kind":"Pod","namespaced":true,"verbs":["get"]}]}""")),
            "/apis/apps/v1" => Task.FromResult<JsonNode?>(JsonNode.Parse("""{"resources":[{"name":"deployments","kind":"Deployment","namespaced":true,"verbs":["list"]},{"name":"deployments/scale","kind":"Scale","namespaced":true,"verbs":["get"]}]}""")),
            _ => throw new KubeApiException(503, "ServiceUnavailable", "the server is currently unable to handle the request"), // broken APIService (metrics-server down)
        };
        public Task<KubeList> ListAsync(Gvr gvr, string? ns, ListOptions options, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<WatchEvent> WatchAsync(Gvr gvr, string? ns, string? resourceVersion, bool asTable, CancellationToken ct) => throw new NotSupportedException();
        public Task<KubeResponse> SendAsync(string method, string path, string? body, string contentType, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> StreamAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<IKubeSocket> OpenSocketAsync(string path, IReadOnlyList<string> subProtocols, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public async Task A_broken_api_group_is_reported_once_and_does_not_break_the_rest_legacy_format()
    {
        var d = new ApiDiscovery(new BrokenGroupTransport());
        await d.RefreshAsync(default);
        Assert.False(d.UsedAggregated);
        Assert.Equal(["metrics.k8s.io/v1beta1"], d.FailedGroups);
        Assert.NotNull(d.FindByName("po"));
        var pods = d.FindByName("pods")!;
        Assert.True(pods.Has("log"));
        Assert.True(d.FindByName("deployments")!.Has("scale"));
        Assert.Null(d.FindByName("nodes"));
    }

    [Fact]
    public void Parses_aggregated_discovery_with_subresources_and_preferred_version()
    {
        var json = JsonNode.Parse("""
            {"kind":"APIGroupDiscoveryList","items":[{"metadata":{"name":"batch"},"versions":[
              {"version":"v1","resources":[{"resource":"cronjobs","responseKind":{"kind":"CronJob"},"scope":"Namespaced","verbs":["list","get"],"shortNames":["cj"],"categories":["all"],"subresources":[{"subresource":"status"}]}]},
              {"version":"v1beta1","resources":[{"resource":"cronjobs","responseKind":{"kind":"CronJob"},"scope":"Namespaced","verbs":["list"]}]}]}]}
            """)!.AsObject();
        var k = Assert.Single(ApiDiscovery.ParseAggregated(json));
        Assert.Equal(("batch", "v1", "cronjobs", "CronJob", true), (k.Group, k.Version, k.Resource, k.Kind, k.Namespaced));
        Assert.Equal(["cj"], k.ShortNames); Assert.Equal(["status"], k.Subresources); Assert.Equal(["all"], k.Categories);
    }
}

public class HelmActionTests
{
    private sealed class FakeHelm(bool available) : IHelmRunner
    {
        public List<string[]> Calls { get; } = [];
        public bool IsAvailable => available;
        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct) { Calls.Add([.. args]); return Task.FromResult("ok"); }
    }

    private static ClusterActions Make(FakeHelm helm, SafetyMode mode, FakeKubeTransport? t = null)
    {
        t ??= new FakeKubeTransport();
        var a = new ClusterActions("c", new ApiDiscovery(t), t, new ClusterEventsService(), new ActivityLog(), () => mode);
        a.Helm = helm;
        return a;
    }

    [Fact]
    public async Task Rollback_and_uninstall_shell_out_through_cluster_actions_with_the_safety_rules()
    {
        var helm = new FakeHelm(true);
        var a = Make(helm, SafetyMode.Normal);
        await a.RollbackHelmReleaseAsync("web", "nginx", 2, default);
        await a.UninstallHelmReleaseAsync("web", "nginx", default);
        Assert.Equal(["rollback", "nginx", "2", "-n", "web"], helm.Calls[0]);
        Assert.Equal(["uninstall", "nginx", "-n", "web"], helm.Calls[1]);

        var prod = Make(helm, SafetyMode.Production);
        await Assert.ThrowsAsync<SafetyException>(() => prod.UninstallHelmReleaseAsync("web", "nginx", default));
        await prod.UninstallHelmReleaseAsync("web", "nginx", default, confirmation: "nginx");
        var ro = Make(helm, SafetyMode.ReadOnly);
        await Assert.ThrowsAsync<SafetyException>(() => ro.RollbackHelmReleaseAsync("web", "nginx", 1, default));
        Assert.Equal(3, helm.Calls.Count);
    }

    [Fact]
    public async Task Without_the_helm_binary_the_actions_are_disabled_with_a_clear_message()
    {
        var a = Make(new FakeHelm(false), SafetyMode.Normal);
        Assert.False(a.CanRunHelm);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => a.RollbackHelmReleaseAsync("web", "nginx", 1, default));
        Assert.Contains("helm", ex.Message);
    }
}
