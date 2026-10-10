using Avalonia.Headless.XUnit;
using Kubism.Core.Network;
using Kubism.Desktop.Controls;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class NetworkUiTests
{
    private static async Task<(UiHarness h, WorkspaceViewModel ws, NetworkViewModel vm)> OpenAsync(string? ns = null, Action<UiHarness>? beforeConnect = null)
    {
        var h = await UiHarness.StartAsync();
        beforeConnect?.Invoke(h);
        var ws = await h.ConnectAsync("demo-staging");
        if (ns is null) ws.Namespaces.SelectAll(); else ws.Namespaces.Select(ns);
        var node = ws.FindNav(NavTarget.NetworkGraph);
        Assert.NotNull(node);
        ws.Navigate(node!);
        await UiHarness.WaitFor(() => ws.CurrentContent is NetworkViewModel v && !v.IsLoading && v.View is { Nodes.Count: > 0 }, what: "network graph");
        return (h, ws, (NetworkViewModel)ws.CurrentContent!);
    }

    [AvaloniaFact]
    public async Task Page_sits_under_Network_and_draws_exposure_ingress_service_workload_with_ports_and_problems()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        Assert.Contains(ws.NavRoots.Single(n => n.Title == "Network").Children, n => n.Target == NavTarget.NetworkGraph);
        Assert.True(vm.Graph.IsComplete);
        Assert.False(vm.HasWarning);
        var v = vm.View!;
        Assert.Equal(new[] { 0, 1, 2, 3 }, v.Nodes.Select(n => n.Column).Distinct().Order());
        Assert.Contains(v.Nodes, p => p.Id == "x:svc/ingress-nginx/ingress-nginx-controller" && p.Node.Exposure == ExposureLevel.Outside);
        Assert.Contains(v.Edges, e => e.From == "s:shop/web" && e.Label == "80 → http (8080)");
        Assert.Contains(v.Edges, e => e.Problem);
        Assert.Contains(v.Nodes, p => p.Node.Missing);
        Assert.NotNull(h.Find<NetworkGraphControl>());
        Assert.Contains("outside the cluster", vm.ViewSummary);
        h.Screenshot("64-network-all");
    }

    [AvaloniaFact]
    public async Task Namespace_filter_keeps_the_controller_of_another_namespace_as_context()
    {
        var (h, ws, vm) = await OpenAsync("tools");
        await using var _ = h;
        var v = vm.View!;
        Assert.All(v.Nodes.Where(p => p.Node.Kind != NetworkNodeKind.Exposure), p => Assert.Equal("tools", p.Node.Namespace));
        var ctl = v.Nodes.Single(p => p.Id == "x:svc/ingress-nginx/ingress-nginx-controller");
        Assert.True(ctl.IsContext);
        Assert.DoesNotContain(v.Nodes, p => p.Node.Namespace == "shop");
        h.Screenshot("65-network-namespace");

        // changing the shared namespace picker redraws the page
        ws.Namespaces.Select("shop");
        await UiHarness.WaitFor(() => vm.View!.Nodes.Any(p => p.Node.Namespace == "shop") && vm.View.Nodes.All(p => p.Node.Namespace != "tools"), what: "namespace change");
    }

    [AvaloniaFact]
    public async Task Filters_select_outside_problems_and_search_and_clicking_a_node_shows_its_details()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        vm.SelectedExposure = "Outside the cluster only";
        Assert.DoesNotContain(vm.View!.Nodes, p => p.Id == "s:tools/db");
        vm.SelectedExposure = "In-cluster only";
        vm.IncludeInternal = true;
        Assert.Contains(vm.View!.Nodes, p => p.Id == "s:tools/db");
        vm.SelectedExposure = "Any exposure"; vm.IncludeInternal = false;

        vm.ProblemsOnly = true;
        Assert.Contains(vm.View!.Nodes, p => p.Id == "s:shop/legacy");
        vm.ProblemsOnly = false;

        vm.Search = "agw.example.com";
        Assert.Contains(vm.View!.Nodes, p => p.Id == "i:shop/agw");
        Assert.DoesNotContain(vm.View!.Nodes, p => p.Node.Namespace == "tools");
        vm.Search = "";

        var ingress = vm.View!.Nodes.Single(p => p.Id == "i:shop/shop");
        vm.NodeClickedCommand.Execute(ingress);
        Assert.Equal("i:shop/shop", vm.SelectedNode!.Id);
        Assert.Contains(vm.NodeLines, l => l.Contains("shop.example.com/api → api:80"));
        Assert.Contains(vm.NodeLines, l => l.Contains("exposure: outside the cluster"));
        await vm.CopyDetailsCommand.ExecuteAsync(null);
        Assert.Contains("shop.example.com", h.Clipboard.Text);
        h.Screenshot("66-network-selected");
    }

    [AvaloniaFact]
    public async Task An_unreadable_kind_shows_a_warning_instead_of_a_silent_partial_graph()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.Deny("list", "endpointslices");
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.NetworkGraph)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is NetworkViewModel v && !v.IsLoading, what: "network");
        var vm = (NetworkViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => vm.HasWarning, what: "warning");
        Assert.Contains("endpointslices", vm.Warning);
        Assert.Contains("Incomplete", vm.Warning);
        h.Screenshot("67-network-incomplete");
    }
}
