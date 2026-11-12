using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Discovery;
using Kubism.Core.Network;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

/// <summary>Gateway API as a source of chains, on Kubism.Testing/Data/gateway.yaml (read together with network.yaml).</summary>
public class NetworkGatewayTests
{
    private static readonly NetworkGraph Graph = NetworkFixture.GatewayGraph();

    private static NetworkNode N(string id) => Graph.Nodes.Single(n => n.Id == id);
    private static IEnumerable<NetworkEdge> Into(string id) => Graph.Edges.Where(e => e.To == id);
    private static string[] Codes(string id) => N(id).Problems.Select(p => p.Code).ToArray();
    private static NetworkRoute[] RoutesOf(string gateway) => Graph.Routes.Where(r => r.IngressId == "g:edge/" + gateway).ToArray();

    // ---------- exposure ----------

    [Fact]
    public void A_gateway_behind_a_load_balancer_service_is_outside_the_cluster_and_the_level_flows_to_its_backends()
    {
        var gw = N("g:edge/public");
        Assert.Equal((NetworkNodeKind.Ingress, "Gateway", ExposureLevel.Outside), (gw.Kind, gw.KindText, gw.Exposure));
        var x = N("x:svc/gateway-system/envoy-edge-public");
        Assert.Equal(ExposureLevel.Outside, x.Exposure);
        Assert.Contains("LoadBalancer 20.4.5.6", x.Detail);
        Assert.Equal(["x:svc/gateway-system/envoy-edge-public"], Into("g:edge/public").Select(e => e.From).ToArray());
        Assert.Equal(ExposureLevel.Outside, N("s:edge/web").Exposure);
        Assert.Equal(ExposureLevel.Outside, N("w:edge/Deployment/gw-web").Exposure);
        // the controller's Service is drawn as the exposure, not as a Service chain
        Assert.DoesNotContain(Graph.Nodes, n => n.Id == "s:gateway-system/envoy-edge-public");
    }

    [Fact]
    public void A_gateway_behind_a_cluster_ip_service_is_in_cluster_and_says_so()
    {
        Assert.Equal(ExposureLevel.InCluster, N("x:svc/gateway-system/envoy-edge-internal").Exposure);
        Assert.Contains(N("g:edge/internal").Lines, l => l.Contains("NOT reachable from outside the cluster"));
    }

    [Fact]
    public void Application_Gateway_for_Containers_runs_outside_the_cluster()
    {
        var x = N("x:ext/alb.networking.azure.io/alb-controller");
        Assert.Equal((NetworkNodeKind.Exposure, ExposureLevel.Outside, null), (x.Kind, x.Exposure, x.Namespace));
        Assert.Contains("Application Gateway for Containers", x.Label);
        Assert.Equal(ExposureLevel.Outside, N("g:edge/azure").Exposure);
    }

    [Fact]
    public void A_gateway_whose_class_is_not_accepted_has_no_controller_and_says_what_the_controller_reported()
    {
        var codes = Codes("g:edge/lost");
        Assert.Contains("gatewayclass-not-accepted", codes);
        Assert.Contains("gateway-not-accepted", codes);
        Assert.Contains("gateway-not-programmed", codes);
        Assert.Contains("listener-not-ready", codes);
        Assert.Contains("no-routes", codes);
        Assert.Contains("controller-not-found", codes);
        Assert.Contains(N("g:edge/lost").Problems, p => p.Description.Contains("parametersRef not found"));
        Assert.Equal(ExposureLevel.Unknown, N("g:edge/lost").Exposure);
    }

    [Fact]
    public void A_healthy_gateway_has_no_problem()
    {
        Assert.Empty(Codes("g:edge/public"));
        Assert.Contains(N("g:edge/public").Lines, l => l.Contains("TLS: shop.gw.example.com → secret gw-tls, certificate expires in 90d"));
        Assert.Contains(N("g:edge/public").Lines, l => l == "address: 20.4.5.6");
    }

    // ---------- routes and backends ----------

    [Fact]
    public void Routes_become_rows_and_edges_with_the_path_the_port_and_the_route_that_carries_them()
    {
        var rows = RoutesOf("public");
        Assert.Equal(["/", "/api", "/legacy"], rows.Select(r => r.Path).Order().ToArray());
        Assert.All(rows, r => Assert.Equal(("shop.gw.example.com", "envoy", "HTTPRoute edge/web", true), (r.Host, r.Class, r.RouteName, r.TlsCovered)));
        Assert.Equal("Exact", rows.Single(r => r.Path == "/legacy").PathType);
        var v = Graph.View(new NetworkFilter { Search = "shop.gw.example.com" });
        Assert.Contains(v.Routes, r => r.Url == "https://shop.gw.example.com/api" && r.Backend == "api:8080" && r.ExposureText == "outside the cluster");
        var edge = Graph.Edges.Single(e => e.From == "g:edge/public" && e.To == "s:edge/api");
        Assert.Equal(["8080"], edge.Ports);
        Assert.Contains("/api → :8080", edge.Label);
    }

    [Fact]
    public void A_backend_in_another_namespace_is_followed_when_a_reference_grant_allows_it()
    {
        Assert.Contains(Graph.Edges, e => e.From == "g:edge/public" && e.To == "s:partners/legacy" && !e.Problem);
        Assert.DoesNotContain("reference-not-permitted", Codes("g:edge/public"));
        Assert.Equal(ExposureLevel.Outside, N("w:partners/Deployment/gw-legacy").Exposure);
    }

    [Fact]
    public void A_cross_namespace_backend_needs_a_grant_for_the_kind_of_the_route()
    {
        // the grant in partners lets HTTPRoutes of edge reach Service legacy; the GRPCRoute of edge is not covered
        Assert.Contains("reference-not-permitted", Codes("g:edge/azure"));
        Assert.Contains(N("g:edge/azure").Problems, p => p.Description.Contains("GRPCRoute") && p.Description.Contains("partners/legacy"));
        Assert.True(Graph.Edges.Single(e => e.From == "g:edge/azure" && e.To == "s:partners/legacy").Problem);
    }

    [Fact]
    public void A_missing_backend_and_what_the_controller_said_about_the_route_are_flagged_on_the_gateway()
    {
        var codes = Codes("g:edge/internal");
        Assert.Contains("missing-service", codes);
        Assert.Contains("route-refs-unresolved", codes);
        Assert.True(N("s:edge/ghost").Missing);
        Assert.True(Graph.Edges.Single(e => e.From == "g:edge/internal" && e.To == "s:edge/ghost").Problem);
        Assert.Contains(N("g:edge/internal").Problems, p => p.Description.Contains("Service edge/ghost not found"));
    }

    [Fact]
    public void A_route_from_a_namespace_the_listener_does_not_accept_is_flagged_when_the_controller_reports_nothing()
    {
        Assert.Contains("route-not-allowed", Codes("g:edge/internal"));
        Assert.Contains(N("g:edge/internal").Problems, p => p.Description.Contains("HTTPRoute partners/intruder"));
    }

    [Fact]
    public void A_listener_that_terminates_tls_is_checked_like_an_ingress_secret()
    {
        Assert.Contains("tls-secret-missing", Codes("g:edge/azure"));
        Assert.Contains(N("g:edge/azure").Lines, l => l.Contains("agc-tls MISSING"));
    }

    [Fact]
    public void Tcp_and_grpc_routes_get_a_url_that_fits_them()
    {
        var tcp = Graph.View(new NetworkFilter { Search = "internal" }).Routes.Single(r => r.Route.RouteName == "TCPRoute edge/pg");
        Assert.Equal("tcp://*:5432", tcp.Url);
        Assert.Equal("pg:5432", tcp.Backend);
        var grpc = RoutesOf("azure").Single(r => r.RouteName == "GRPCRoute edge/rpc" && r.Service == "api");
        Assert.Equal("grpcs://rpc.agc.example.com/acme.Api/Get", Graph.View(new NetworkFilter { Search = "agc" }).Routes.Single(r => r.Route == grpc).Url);
    }

    [Fact]
    public void The_ports_of_a_gateway_backend_are_checked_against_the_service()
    {
        var objects = NetworkFixture.GatewayYaml().Select(o => new KubeObject((JsonObject)o.Json.DeepClone())).ToList();
        var web = objects.Single(o => o.Kind == "HTTPRoute" && o.Name == "web");
        web.Json["spec"]!["rules"]![0]!["backendRefs"]![0]!["port"] = 8443;
        var g = NetworkFixture.GatewayGraph(skip: o => o.Kind == "HTTPRoute" && o.Name == "web" && o.Namespace == "edge");
        Assert.DoesNotContain("missing-service-port", g.Nodes.Single(n => n.Id == "g:edge/public").Problems.Select(p => p.Code));
        var all = NetworkFixture.Objects().Concat(objects).ToList();
        IEnumerable<KubeObject> Of(string kind) => all.Where(o => o.Kind == kind);
        var bad = NetworkGraph.Build(Of("Ingress"), Of("IngressClass"), Of("Service"), Of("EndpointSlice"), Of("Pod"), NetworkFixture.Tls(), null, NetworkFixture.Now, NetworkFixture.Nodes(),
            new GatewayObjects(Of("GatewayClass").ToList(), Of("Gateway").ToList(), GatewayApi.RouteKinds.SelectMany(Of).ToList(), Of("ReferenceGrant").ToList()));
        Assert.Contains("missing-service-port", bad.Nodes.Single(n => n.Id == "g:edge/public").Problems.Select(p => p.Code));
    }

    // ---------- views and plumbing ----------

    [Fact]
    public void The_namespace_filter_and_the_search_apply_to_gateway_chains()
    {
        var edge = Graph.View(new NetworkFilter { Namespaces = new HashSet<string> { "edge" } });
        Assert.Contains(edge.Nodes, p => p.Id == "g:edge/public");
        Assert.Contains(edge.Nodes, p => p.Id == "x:svc/gateway-system/envoy-edge-public" && p.IsContext);   // the controller of another namespace stays as context
        // Gateways that route to the same Service are one chain (as Ingresses are): a search keeps the whole chain
        var agc = Graph.View(new NetworkFilter { Search = "agc.example.com" });
        Assert.Contains(agc.Nodes, p => p.Id == "g:edge/azure");
        Assert.DoesNotContain(agc.Nodes, p => p.Id == "g:edge/lost");
        Assert.Contains(Graph.View(new NetworkFilter { Exposure = ExposureFilter.OutsideOnly, Search = "azure" }).Nodes, p => p.Id == "g:edge/azure");
        // a Gateway nothing routes through is its own chain, and a problem is enough to find it
        Assert.Contains(Graph.View(new NetworkFilter { ProblemsOnly = true }).Nodes, p => p.Id == "g:edge/lost");
        Assert.DoesNotContain(Graph.View(new NetworkFilter { Exposure = ExposureFilter.OutsideOnly }).Nodes, p => p.Id == "g:edge/lost");
    }

    [Fact]
    public void Ingresses_are_not_changed_by_a_gateway_in_the_same_cluster()
    {
        var plain = NetworkFixture.Graph();
        foreach (var n in plain.Nodes.Where(n => n.Kind == NetworkNodeKind.Ingress))
            Assert.Equal(n.Problems.Select(p => p.Code), Graph.Nodes.Single(g => g.Id == n.Id).Problems.Select(p => p.Code));
        Assert.Equal(plain.Routes.Count, Graph.Routes.Count(r => r.RouteName is null));
        Assert.DoesNotContain(plain.Nodes, n => n.IsGateway);
    }

    [Fact]
    public void Tls_secrets_named_by_gateway_listeners_are_looked_up_too()
    {
        var secrets = NetworkGraph.TlsSecretsOf(NetworkFixture.Objects().Where(o => o.Kind == "Ingress"), NetworkFixture.GatewayYaml().Where(o => o.Kind == "Gateway"));
        Assert.Contains(("edge", "gw-tls"), secrets);
        Assert.Contains(("edge", "agc-tls"), secrets);
        Assert.Contains(("shop", "shop-tls"), secrets);
    }

    [Fact]
    public void Gateway_kinds_are_found_at_their_best_version_and_only_when_they_can_be_listed()
    {
        ResourceKind K(string group, string version, string resource, string kind, params string[] verbs) => new(group, version, resource, kind, true, verbs, [], [], []);
        var kinds = GatewayApi.Find(
        [
            K(GatewayApi.Group, "v1beta1", "gateways", "Gateway", "list"), K(GatewayApi.Group, "v1", "gateways", "Gateway", "list"),
            K(GatewayApi.Group, "v1", "httproutes", "HTTPRoute"),                                  // cannot be listed
            K("networking.istio.io", "v1", "gateways", "Gateway", "list"),                         // another group's Gateway
            K(GatewayApi.Group, "v1alpha2", "tcproutes", "TCPRoute", "list"),
        ]);
        Assert.Equal(["Gateway:v1", "TCPRoute:v1alpha2"], kinds.Select(k => $"{k.Kind}:{k.Gvr.Version}").ToArray());
    }

    // ---------- the live store and the one-shot loader ----------

    private static readonly InformerOptions Fast = new() { Coalesce = TimeSpan.FromMilliseconds(10), BackoffBase = TimeSpan.FromMilliseconds(20), BackoffMax = TimeSpan.FromMilliseconds(50) };

    private static (FakeKubeTransport Transport, List<ResourceKind> Kinds) Seed()
    {
        var t = new FakeKubeTransport();
        var all = NetworkFixture.Objects().Concat(NetworkFixture.GatewayYaml()).ToList();
        JsonObject[] Of(string kind) => all.Where(o => o.Kind == kind).Select(o => (JsonObject)o.Json.DeepClone()).ToArray();
        t.Set(new Gvr("", "v1", "nodes"), NetworkFixture.Nodes().Select(n => (JsonObject)n.Json.DeepClone()).ToArray());
        t.Set(new Gvr("networking.k8s.io", "v1", "ingresses"), Of("Ingress")); t.Set(new Gvr("networking.k8s.io", "v1", "ingressclasses"), Of("IngressClass"));
        t.Set(new Gvr("", "v1", "services"), Of("Service")); t.Set(new Gvr("discovery.k8s.io", "v1", "endpointslices"), Of("EndpointSlice")); t.Set(new Gvr("", "v1", "pods"), Of("Pod"));
        var kinds = new List<ResourceKind>();
        foreach (var (kind, version) in new[] { ("GatewayClass", "v1"), ("Gateway", "v1"), ("HTTPRoute", "v1"), ("GRPCRoute", "v1"), ("TCPRoute", "v1alpha2"), ("ReferenceGrant", "v1beta1") })
        {
            var gvr = new Gvr(GatewayApi.Group, version, GatewayApi.Resource(kind));
            t.Set(gvr, Of(kind));
            kinds.Add(new ResourceKind(GatewayApi.Group, version, gvr.Resource, kind, kind != "GatewayClass", ["get", "list", "watch"], [], [], []));
        }
        t.SetGet("/api/v1/namespaces/edge/secrets/gw-tls", new JsonObject { ["kind"] = "Secret", ["type"] = "kubernetes.io/tls", ["metadata"] = new JsonObject { ["name"] = "gw-tls", ["namespace"] = "edge" } });
        return (t, kinds);
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }

    [Fact]
    public async Task The_store_follows_gateway_api_kinds_where_the_cluster_serves_them()
    {
        var (t, kinds) = Seed();
        await using var cache = new ResourceCache(t, "c", options: Fast);
        using var store = new NetworkStore(cache, t, TimeSpan.FromMilliseconds(20), kinds);
        await store.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(store.Graph.IsComplete);
        Assert.Equal(ExposureLevel.Outside, store.Graph.Nodes.Single(n => n.Id == "g:edge/public").Exposure);
        Assert.Contains(store.Graph.Routes, r => r.RouteName == "HTTPRoute edge/web");
        // the certificate lookup of a listener's secret arrives after the first graph: gw-tls exists, agc-tls (404 on the fake) does not
        Assert.True(await Until(() => store.Graph.Nodes.Single(n => n.Id == "g:edge/azure").Problems.Any(p => p.Code == "tls-secret-missing")));
        Assert.DoesNotContain(store.Graph.Nodes.Single(n => n.Id == "g:edge/public").Problems, p => p.Code.StartsWith("tls"));
    }

    [Fact]
    public async Task A_gateway_kind_the_user_cannot_list_is_reported_and_a_cluster_without_gateway_api_is_not_asked()
    {
        var (t, kinds) = Seed();
        t.ListFailure = (gvr, _) => gvr.Resource == "httproutes" ? new KubeApiException(403, "Forbidden", "httproutes is forbidden") : null;
        await using var cache = new ResourceCache(t, "c", options: Fast);
        using var store = new NetworkStore(cache, t, TimeSpan.FromMilliseconds(20), kinds);
        await store.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(["httproutes"], store.Graph.MissingKinds);

        var plain = await NetworkLoader.LoadAsync(Seed().Transport, CancellationToken.None, checkTls: false);   // no discovery: Gateway API is not read
        Assert.DoesNotContain(plain.Nodes, n => n.IsGateway);
        Assert.True(plain.IsComplete);
    }

    [Fact]
    public async Task The_one_shot_loader_reads_gateway_api_through_the_discovered_kinds()
    {
        var (t, kinds) = Seed();
        var g = await NetworkLoader.LoadAsync(t, CancellationToken.None, checkTls: false, discovered: kinds);
        Assert.True(g.IsComplete);
        Assert.Contains(g.Nodes, n => n.Id == "g:edge/public" && n.Exposure == ExposureLevel.Outside);
        Assert.Contains(g.Edges, e => e.From == "g:edge/public" && e.To == "s:partners/legacy" && !e.Problem);
    }
}
