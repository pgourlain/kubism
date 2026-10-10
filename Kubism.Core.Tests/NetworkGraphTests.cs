using Kubism.Core.Network;
using Kubism.Core.Security;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

/// <summary>The exposure rules, port resolution, problems and the namespace filter, on the realistic fixture Kubism.Testing/Data/network.yaml.</summary>
public class NetworkGraphTests
{
    private static readonly NetworkGraph Graph = NetworkFixture.Graph();

    private static NetworkNode N(string id) => Graph.Nodes.Single(n => n.Id == id);
    private static NetworkEdge E(string from, string to) => Graph.Edges.Single(e => e.From == from && e.To == to);
    private static IEnumerable<NetworkEdge> Into(string id) => Graph.Edges.Where(e => e.To == id);
    private static string[] Codes(string id) => N(id).Problems.Select(p => p.Code).ToArray();

    // ---------- exposure ----------

    [Fact]
    public void An_ingress_behind_a_load_balancer_controller_in_another_namespace_is_outside_the_cluster()
    {
        var ctl = N("x:svc/ingress-nginx/ingress-nginx-controller");
        Assert.Equal((NetworkNodeKind.Exposure, ExposureLevel.Outside, "ingress-nginx"), (ctl.Kind, ctl.Exposure, ctl.Namespace));
        Assert.Contains("LoadBalancer 20.1.2.3", ctl.Detail);
        Assert.Equal(ExposureLevel.Outside, N("i:shop/shop").Exposure);
        // the level flows down the chain
        Assert.Equal(ExposureLevel.Outside, N("s:shop/web").Exposure);
        Assert.Equal(ExposureLevel.Outside, N("w:shop/Deployment/web").Exposure);
        // only the controller whose instance is named after the class feeds it, not the internal one
        Assert.Equal(["x:svc/ingress-nginx/ingress-nginx-controller"], Into("i:shop/shop").Select(e => e.From).ToArray());
        // the admission webhook Service is never taken for the controller
        Assert.DoesNotContain(Graph.Nodes, n => n.Kind == NetworkNodeKind.Exposure && n.Id.Contains("admission"));
    }

    [Fact]
    public void An_internal_load_balancer_is_outside_the_cluster_with_a_private_network_note()
    {
        var x = N("x:svc/ingress-nginx/ingress-nginx-internal-controller");
        Assert.Equal(ExposureLevel.Outside, x.Exposure);
        Assert.Contains("private network", x.Detail);
        Assert.Equal(["x:svc/ingress-nginx/ingress-nginx-internal-controller"], Into("i:shop/admin").Select(e => e.From).ToArray());
    }

    [Fact]
    public void An_ingress_whose_controller_is_cluster_ip_is_in_cluster_and_says_so()
    {
        var x = N("x:svc/ingress-nginx/ingress-nginx-private-controller");
        Assert.Equal(ExposureLevel.InCluster, x.Exposure);
        Assert.Contains("in-cluster only", x.Detail);
        var ing = N("i:tools/intranet");
        Assert.Equal(ExposureLevel.InCluster, ing.Exposure);
        Assert.Contains(ing.Lines, l => l.Contains("NOT reachable from outside the cluster"));
        // the Service behind it is also reached through an exposed ingress, so it is outside overall
        Assert.Equal(ExposureLevel.Outside, N("s:tools/grafana").Exposure);
    }

    [Fact]
    public void A_controller_running_outside_the_cluster_is_an_exposure_node_without_a_service()
    {
        var x = N("x:ext/agic");
        Assert.Equal(ExposureLevel.Outside, x.Exposure);
        Assert.Contains("Application Gateway", x.Label);
        Assert.Equal("x:ext/agic", Into("i:shop/agw").Single().From);
    }

    [Fact]
    public void A_missing_controller_or_class_leaves_the_exposure_unknown_and_is_flagged()
    {
        Assert.Equal(ExposureLevel.Unknown, N("x:unknown/traefik").Exposure);
        Assert.Contains("controller-not-found", Codes("i:tools/unknown-ctl"));
        Assert.Equal(ExposureLevel.Unknown, N("x:none/tools/noclass").Exposure);
        Assert.Contains("no-ingress-class", Codes("i:tools/noclass"));
    }

    [Fact]
    public void An_annotation_only_class_is_understood()
    {
        // docs has kubernetes.io/ingress.class: nginx and no ingressClassName
        Assert.Equal("x:svc/ingress-nginx/ingress-nginx-controller", Into("i:shop/docs").Single().From);
    }

    [Fact]
    public void Services_exposed_directly_and_host_ports_are_exposure_nodes()
    {
        var lb = N("x:svc/shop/worker-lb");
        Assert.Equal(ExposureLevel.Outside, lb.Exposure);
        Assert.Contains("30900", E("x:svc/shop/worker-lb", "s:shop/worker-lb").Label);
        Assert.Contains("exposed-directly", Codes("s:shop/worker-lb"));

        var host = N("x:host/tools/DaemonSet/node-agent");
        Assert.Equal(ExposureLevel.Outside, host.Exposure);
        Assert.Contains("hostPort 9100", host.Detail);
        Assert.Contains("host-network", Codes("w:tools/DaemonSet/node-agent"));
    }

    // ---------- ports ----------

    [Fact]
    public void Ports_are_shown_on_every_hop_with_named_target_ports_resolved()
    {
        Assert.Contains("shop.example.com/ → :80", E("i:shop/shop", "s:shop/web").Label);
        Assert.Contains("shop.example.com/api → :80", E("i:shop/shop", "s:shop/api").Label);
        Assert.Equal("80 → http (8080)", E("s:shop/web", "w:shop/Deployment/web").Label);         // named targetPort resolved against the containers
        Assert.Equal("80 → 3000", E("s:shop/api", "w:shop/Deployment/api").Label);                   // numeric
        Assert.DoesNotContain("port-not-declared", Codes("w:shop/Deployment/api"));
        // an Ingress backend by port name resolves to the Service's port number
        Assert.Contains("admin.example.com/ → :80", E("i:shop/admin", "s:shop/web").Label);
    }

    [Fact]
    public void Workloads_come_from_the_owner_chain_with_ready_counts()
    {
        var web = N("w:shop/Deployment/web");
        Assert.Equal("Deployment web", web.Label);
        Assert.Contains("2/2 pods ready", web.Detail);
        Assert.Contains("http:8080", web.Detail);
        Assert.Contains("w:shop/StatefulSet/worker", Graph.Nodes.Select(n => n.Id));
    }

    // ---------- problems ----------

    [Fact]
    public void Problems_are_flagged_where_they_are()
    {
        // missing backend service
        Assert.True(N("s:shop/docs-svc").Missing);
        Assert.Contains("missing-service", Codes("s:shop/docs-svc"));
        Assert.Contains("missing-service", Codes("i:shop/docs"));
        Assert.True(E("i:shop/docs", "s:shop/docs-svc").Problem);
        // a port the Service does not define
        Assert.Contains("missing-service-port", Codes("i:shop/portbad"));
        Assert.True(E("i:shop/portbad", "s:shop/web").Problem);
        Assert.Contains(":9999", E("i:shop/portbad", "s:shop/web").Label);
        // unresolved named targetPort
        Assert.Contains("target-port-unresolved", Codes("s:shop/broken-port"));
        Assert.True(E("s:shop/broken-port", "w:shop/Deployment/web").Problem);
        Assert.Contains("(?)", E("s:shop/broken-port", "w:shop/Deployment/web").Label);
        // selector that matches nothing, and no endpoint
        Assert.Contains("selector-matches-no-pod", Codes("s:shop/legacy"));
        Assert.Contains("no-ready-endpoints", Codes("s:shop/legacy"));
        // a pod not ready
        var worker = N("w:shop/StatefulSet/worker");
        Assert.Contains("pods-not-ready", Codes(worker.Id));
        Assert.Contains("1/2 pods ready", worker.Detail);
        // healthy things carry none
        Assert.True(Codes("s:shop/web").Length == 0, string.Join(",", Codes("s:shop/web")));
        Assert.True(Codes("w:shop/Deployment/web").Length == 0, string.Join(",", Codes("w:shop/Deployment/web")));
        Assert.True(Codes("i:shop/shop").All(c => c.StartsWith("tls")), string.Join(",", Codes("i:shop/shop")));
    }

    [Fact]
    public void Tls_secret_problems_come_from_the_lookup_and_unchecked_secrets_are_not_problems()
    {
        Assert.Contains("tls-expiring", Codes("i:shop/shop"));
        Assert.Equal(FindingSeverity.Medium, N("i:shop/shop").Problems.Single(p => p.Code == "tls-expiring").Severity);
        Assert.Contains("tls-secret-missing", Codes("i:shop/admin"));
        Assert.Equal(FindingSeverity.High, N("i:shop/admin").Problems.Single(p => p.Code == "tls-secret-missing").Severity);
        Assert.Contains(N("i:shop/shop").Lines, l => l.Contains("expires in 10d"));
        var unchecked_ = NetworkFixture.Graph(tls: false);
        Assert.DoesNotContain(unchecked_.Nodes.SelectMany(n => n.Problems), p => p.Code.StartsWith("tls"));
        Assert.Contains(unchecked_.Nodes.Single(n => n.Id == "i:shop/shop").Lines, l => l.Contains("not checked"));
        Assert.Equal([("shop", "shop-tls"), ("shop", "admin-tls")], NetworkGraph.TlsSecretsOf(NetworkFixture.Objects().Where(o => o.Kind == "Ingress")).ToArray());
    }

    // ---------- view: filters, namespace, layout ----------

    [Fact]
    public void Services_nothing_routes_to_are_hidden_unless_asked_for()
    {
        Assert.DoesNotContain(Graph.View().Nodes, p => p.Id == "s:tools/db");
        Assert.Contains(Graph.View(new NetworkFilter { IncludeInternalServices = true }).Nodes, p => p.Id == "s:tools/db");
        // an in-cluster ingress is never hidden: seeing it is the point
        Assert.Contains(Graph.View().Nodes, p => p.Id == "i:tools/intranet");
    }

    [Fact]
    public void The_namespace_filter_keeps_the_controller_of_another_namespace_as_context()
    {
        var tools = Graph.View(new NetworkFilter { Namespaces = new HashSet<string> { "tools" } });
        Assert.All(tools.Nodes.Where(p => p.Node.Kind != NetworkNodeKind.Exposure), p => Assert.Equal("tools", p.Node.Namespace));
        var ctl = tools.Nodes.Single(p => p.Id == "x:svc/ingress-nginx/ingress-nginx-controller");
        Assert.True(ctl.IsContext);                                              // not in the selection, but kept: the path is not cut
        Assert.Contains(tools.Edges, e => e.From == ctl.Id && e.To == "i:tools/grafana");
        Assert.DoesNotContain(tools.Edges, e => e.To == "i:shop/shop");           // it feeds shop's ingress too, which is not pulled in
        Assert.DoesNotContain(tools.Nodes, p => p.Node.Namespace == "shop");

        var shop = Graph.View(new NetworkFilter { Namespaces = new HashSet<string> { "shop" } });
        Assert.DoesNotContain(shop.Nodes, p => p.Node.Namespace == "tools");
        Assert.Contains(shop.Nodes, p => p.Id == "x:ext/agic" && !p.IsContext);   // outside the cluster: no namespace, not "another namespace"

        var onlyController = Graph.View(new NetworkFilter { Namespaces = new HashSet<string> { "ingress-nginx" } });
        Assert.Empty(onlyController.Nodes);
    }

    [Fact]
    public void Exposure_problem_and_search_filters_select_chains()
    {
        var outside = Graph.View(new NetworkFilter { Exposure = ExposureFilter.OutsideOnly });
        Assert.Contains(outside.Nodes, p => p.Id == "i:shop/shop");
        Assert.DoesNotContain(outside.Nodes, p => p.Id == "s:tools/db");

        var inCluster = Graph.View(new NetworkFilter { Exposure = ExposureFilter.InClusterOnly, IncludeInternalServices = true });
        Assert.Contains(inCluster.Nodes, p => p.Id == "s:tools/db");
        Assert.DoesNotContain(inCluster.Nodes, p => p.Id == "i:shop/shop");

        var problems = Graph.View(new NetworkFilter { ProblemsOnly = true, IncludeInternalServices = true });
        Assert.DoesNotContain(problems.Nodes, p => p.Id == "s:tools/db");        // healthy
        Assert.Contains(problems.Nodes, p => p.Id == "s:shop/legacy");
        // looking for problems also surfaces a broken in-cluster Service without ticking "internal services"
        Assert.Contains(Graph.View(new NetworkFilter { ProblemsOnly = true }).Nodes, p => p.Id == "s:shop/legacy");
        Assert.DoesNotContain(Graph.View(new NetworkFilter { ProblemsOnly = true }).Nodes, p => p.Id == "s:tools/db");

        var agw = Graph.View(new NetworkFilter { Search = "agw.example.com" });
        Assert.Contains(agw.Nodes, p => p.Id == "i:shop/agw");
        Assert.DoesNotContain(agw.Nodes, p => p.Node.Namespace == "tools");
        Assert.Contains(Graph.View(new NetworkFilter { Search = "/api" }).Nodes, p => p.Id == "s:shop/api");
    }

    [Fact]
    public void The_view_is_capped_and_laid_out_in_four_columns_with_dense_rows()
    {
        var capped = Graph.View(new NetworkFilter { MaxChains = 1 });
        Assert.True(capped.Truncated);
        Assert.Equal(1, capped.Chains);

        var v = Graph.View(new NetworkFilter { IncludeInternalServices = true });
        Assert.All(v.Nodes, p => Assert.Equal((int)p.Node.Kind, p.Column));
        Assert.Equal(v.Nodes.Count, v.Nodes.Select(p => (p.Column, p.Row)).Distinct().Count());
        foreach (var col in v.Nodes.GroupBy(p => p.Column)) Assert.Equal(Enumerable.Range(0, col.Count()), col.Select(p => p.Row).Order());
        Assert.True(v.Nodes.All(p => p.Row < v.MaxRows));
        var ids = v.Nodes.Select(p => p.Id).ToHashSet();
        Assert.All(v.Edges, e => { Assert.Contains(e.From, ids); Assert.Contains(e.To, ids); });
        Assert.True(v.OutsideChains > 0 && v.ProblemChains > 0);
    }

    [Fact]
    public void Unreadable_kinds_are_reported_in_every_view()
    {
        var status = new RbacKindStatus[] { new("ingresses", true, null), new("ingressclasses", false, "forbidden"), new("services", true, null), new("endpointslices", true, null), new("pods", true, null) };
        var g = NetworkFixture.Graph(skip: o => o.Kind == "IngressClass", availability: status);
        Assert.False(g.IsComplete);
        Assert.Contains("ingressclasses", g.View().Warning);
        Assert.Null(Graph.View().Warning);
    }
}
