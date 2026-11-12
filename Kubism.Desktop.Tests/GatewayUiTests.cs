using Avalonia.Headless.XUnit;
using Kubism.Core.Network;
using Kubism.Core.Usage;
using Kubism.Desktop.Controls;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

/// <summary>Gateway API on the Network section: its kinds in the sidebar, Gateways in the graph and the Routes table, classes and backends in Usage.</summary>
public class GatewayUiTests
{
    [AvaloniaFact]
    public async Task Gateway_api_kinds_are_listed_under_network_and_not_under_custom_resources()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var network = ws.NavRoots.Single(n => n.Title == "Network").Children.Select(c => c.Title).ToList();
        string[] expected = ["Gateway Classes", "Gateways", "HTTP Routes", "gRPC Routes", "TCP Routes", "Reference Grants"];
        Assert.All(expected, t => Assert.Contains(t, network));
        // in the order of the traffic, before the graph and usage pages
        Assert.True(network.IndexOf("Gateway Classes") < network.IndexOf("Gateways") && network.IndexOf("Gateways") < network.IndexOf("HTTP Routes") && network.IndexOf("HTTP Routes") < network.IndexOf("Network graph"));
        Assert.DoesNotContain(ws.NavRoots.First(n => n.Title == "Custom Resources").Children, c => c.Title == "gateway.networking.k8s.io");

        ws.Namespaces.SelectAll();
        ws.Navigate(ws.NavRoots.Single(n => n.Title == "Network").Children.Single(c => c.Title == "Gateways"));
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Gateway" && l.Rows.Count == 6, what: "gateways");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        Assert.Contains(list.Columns, c => c.Name == "Class");       // the server's printer columns
        Assert.Contains(list.Columns, c => c.Name == "Programmed");
    }

    [AvaloniaFact]
    public async Task The_graph_draws_gateways_like_ingresses_and_the_routes_table_lists_their_urls()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("edge");
        ws.Navigate(ws.FindNav(NavTarget.NetworkGraph)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is NetworkViewModel v && !v.IsLoading && v.View is { } view && view.Nodes.Any(n => n.Id == "g:edge/public") && view.Routes.Count > 5, what: "gateways in the graph");
        var vm = (NetworkViewModel)ws.CurrentContent!;
        Assert.True(vm.Graph.IsComplete, vm.Graph.IncompleteWarning);
        var v2 = vm.View!;
        var gw = v2.Nodes.Single(n => n.Id == "g:edge/public");
        Assert.Equal(("Gateway", 1, ExposureLevel.Outside), (gw.Node.KindText, gw.Column, gw.Node.Exposure));
        Assert.Contains(v2.Nodes, p => p.Id == "x:svc/gateway-system/envoy-edge-public" && p.IsContext);   // the controller's Service is in another namespace
        Assert.Contains("gateway(s)", vm.Summary);
        Assert.NotNull(h.Find<NetworkGraphControl>());
        h.Screenshot("72-network-gateways");

        vm.Tab = NetworkViewModel.RoutesTab;
        UiHarness.Pump();
        var api = vm.Routes.Single(r => r.Url == "https://shop.gw.example.com/api");
        Assert.Equal(("edge/public", "envoy", "api:8080", "outside the cluster"), (api.Ingress, api.Class, api.Backend, api.Exposure));
        Assert.Contains("envoy-edge-public · LoadBalancer 20.4.5.6", api.Via);
        Assert.Contains(vm.Routes, r => r.Url == "tcp://*:5432" && r.Backend == "pg:5432");
        Assert.Contains(vm.Routes, r => r.Url.StartsWith("grpcs://rpc.agc.example.com") && r.Problems.Contains("ReferenceGrant"));
        h.Screenshot("73-network-gateway-routes");

        // selecting a row selects its Gateway, which opens like any object
        vm.SelectedRoute = api;
        await UiHarness.WaitFor(() => vm.SelectedNode?.Id == "g:edge/public", what: "gateway selected");
        Assert.Equal("Gateway", vm.SelectedNode!.Node.KindText);
    }

    /// <summary>What <c>--demo</c> shows for its own workloads: web and api in default, checkout in production.</summary>
    [AvaloniaFact]
    public async Task The_demo_workloads_are_reachable_through_ingresses_and_gateways()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SetSelection(["default", "production"]);
        ws.Navigate(ws.FindNav(NavTarget.NetworkGraph)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is NetworkViewModel v && !v.IsLoading && v.View is { } view && view.Nodes.Any(n => n.Id == "g:production/checkout") && view.Nodes.Any(n => n.Id == "i:default/web"), what: "demo traffic");
        var vm = (NetworkViewModel)ws.CurrentContent!;
        var nodes = vm.View!.Nodes.ToDictionary(n => n.Id);
        Assert.Equal(ExposureLevel.Outside, nodes["i:default/web"].Node.Exposure);                  // ingress-nginx behind a LoadBalancer
        Assert.Equal(ExposureLevel.InCluster, nodes["i:default/api-internal"].Node.Exposure);       // its controller is ClusterIP
        Assert.Equal(ExposureLevel.Outside, nodes["g:production/checkout"].Node.Exposure);          // Envoy behind a LoadBalancer
        Assert.Equal(ExposureLevel.Outside, nodes["g:default/shared"].Node.Exposure);               // Application Gateway for Containers
        Assert.All(new[] { "i:default/web", "i:default/api-internal", "g:production/checkout", "g:default/shared" }, id => Assert.Empty(nodes[id].Node.Problems));
        Assert.Equal(0, vm.View.ProblemChains);                                                       // a demo that shows how it looks when all is well
        Assert.Contains(vm.View.Routes, r => r.Url == "http://demo.example.com/api" && r.Target.Contains("Deployment api"));
        Assert.Contains(vm.View.Routes, r => r.Url == "http://checkout.demo.example.com/" && r.Target.Contains("Deployment checkout"));
        h.Screenshot("75-demo-traffic");
    }

    [AvaloniaFact]
    public async Task Usage_reports_gateway_classes_that_nothing_uses_and_classes_that_do_not_exist()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.SeedYaml("""
            {"apiVersion":"gateway.networking.k8s.io/v1","kind":"GatewayClass","metadata":{"name":"idle"},"spec":{"controllerName":"example.com/idle"}}
            ---
            {"apiVersion":"gateway.networking.k8s.io/v1","kind":"Gateway","metadata":{"name":"vanished","namespace":"edge"},"spec":{"gatewayClassName":"no-such-class","listeners":[{"name":"http","port":80,"protocol":"HTTP"}]}}
            """);
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.NetworkUsage)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is UsageViewModel v && v.Mode == UsageMode.Network && v.Rows.Any(r => r.Row.Name == "idle"), what: "network usage");
        var vm = (UsageViewModel)ws.CurrentContent!;
        Assert.Equal(UsageStatus.Used, vm.Rows.Single(r => r.Row.Name == "envoy").Row.Status);
        Assert.Equal(UsageStatus.Unused, vm.Rows.Single(r => r.Row.Name == "idle").Row.Status);
        vm.Section = 1;
        Assert.Contains(vm.BrokenRows, b => b.Target == "GatewayClass/no-such-class");
        Assert.Contains(vm.BrokenRows, b => b.Target == "Service/edge/ghost");   // a route backend that does not exist
        h.Screenshot("74-usage-gateway-classes");
    }
}
