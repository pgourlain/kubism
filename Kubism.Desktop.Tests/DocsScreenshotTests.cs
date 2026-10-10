using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Kubism.Core.Api;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

/// <summary>
/// The screenshots of the README. They are always rendered into <c>bin/.../screenshots</c>; when <c>KUBISM_DOCS_SCREENSHOTS</c> names a folder
/// (<c>KUBISM_DOCS_SCREENSHOTS=$PWD/docs/screenshots dotnet test --project Kubism.Desktop.Tests --filter-class "*DocsScreenshotTests"</c>) they are copied there too.
/// </summary>
public class DocsScreenshotTests
{
    private static void Shot(UiHarness h, string name)
    {
        var path = h.Screenshot("docs-" + name);
        if (Environment.GetEnvironmentVariable("KUBISM_DOCS_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.Copy(path, Path.Combine(dir, name + ".png"), overwrite: true);
        }
    }

    private static async Task<ResourceListViewModel> OpenListAsync(WorkspaceViewModel ws, string resource, string? ns)
    {
        if (ns is null) ws.Namespaces.SelectAll(); else ws.Namespaces.Select(ns);
        ws.OpenKind(ws.Session.Discovery.FindByName(resource)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Resource == resource && l.Rows.Count > 0, what: resource);
        return (ResourceListViewModel)ws.CurrentContent!;
    }

    [AvaloniaFact]
    public async Task Overview_light_and_dark()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var vm = ws.CurrentContent as OverviewViewModel ?? throw new InvalidOperationException("overview expected");
        await UiHarness.WaitFor(() => vm.NodesTotal > 0 && vm.Problems.Count > 0, what: "overview data");
        Shot(h, "overview");
        h.Main.Settings.Current.Theme = "Dark"; h.Main.ApplySettings();
        try
        {
            // Changing the theme rebuilds the sidebar and the page: wait for the new overview to load.
            await UiHarness.WaitFor(() => ws.CurrentContent is OverviewViewModel o && o.NodesTotal > 0 && o.Problems.Count > 0, what: "overview data (dark)");
            Shot(h, "overview-dark");
        }
        finally { h.Main.Settings.Current.Theme = "System"; h.Main.ApplySettings(); }
    }

    [AvaloniaFact]
    public async Task Pods_with_details()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var list = await OpenListAsync(ws, "pods", null);
        list.SelectedRow = list.Rows.First(r => r.Name == "crashy-0");
        await UiHarness.WaitFor(() => ws.Details.IsOpen && ws.Details.Events.Count > 0, what: "details");
        Shot(h, "pods-details");
    }

    [AvaloniaFact]
    public async Task Relation_graph()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var list = await OpenListAsync(ws, "pods", "default");
        list.SelectedRow = list.Rows.First(r => r.Name.StartsWith("web-"));
        ws.Details.SelectedTab = DetailsViewModel.GraphTab;
        await UiHarness.WaitFor(() => ws.Details.Graph is { Nodes.Count: > 4 }, what: "graph");
        Shot(h, "relation-graph");
    }

    [AvaloniaFact]
    public async Task Compare_clusters()
    {
        await using var h = await UiHarness.StartAsync(withDrift: true);
        var staging = await h.ConnectAsync("demo-staging");
        await h.ConnectAsync("demo-dev");
        h.Main.SelectedWorkspace = staging;
        staging.Namespaces.Select("default");
        staging.Navigate(staging.FindNav(NavTarget.Compare)!);
        await UiHarness.WaitFor(() => staging.CurrentContent is CompareViewModel, what: "compare");
        var cmp = (CompareViewModel)staging.CurrentContent!;
        cmp.Kind = cmp.Kinds.First(k => k.Left.Resource == "deployments");
        await cmp.CompareAsync();
        cmp.Selected = cmp.Rows.First(r => r.Entry.Name == "web");
        Shot(h, "compare-clusters");
    }

    [AvaloniaFact]
    public async Task Global_search()
    {
        await using var h = await UiHarness.StartAsync();
        await h.ConnectAsync("demo-staging");
        await h.ConnectAsync("demo-production");
        h.Main.OpenGlobalSearchCommand.Execute(null);
        h.Main.Search.Query = "image:nginx kind:deploy";
        await h.Main.Search.SearchAsync();
        Shot(h, "global-search");
    }

    [AvaloniaFact]
    public async Task Right_sizing()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.RightSizing)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is RightSizingViewModel, what: "right-sizing");
        var vm = (RightSizingViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => !vm.IsLoading && vm.Rows.Count > 0, what: "right-sizing data");
        Shot(h, "right-sizing");
    }

    [AvaloniaFact]
    public async Task Logs()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var list = await OpenListAsync(ws, "pods", "default");
        var pod = list.Rows.First(r => r.Name.StartsWith("api-"));
        h.Demo.Server.SetLogs("default", pod.Name,
        [
            "2026-10-10T08:12:01.114Z INFO  listening on :8080",
            "2026-10-10T08:12:03.502Z INFO  GET /healthz 200 1ms",
            "2026-10-10T08:12:04.871Z INFO  GET /v1/orders?limit=50 200 18ms",
            "2026-10-10T08:12:05.020Z INFO  GET /v1/orders/9921 200 6ms",
            "2026-10-10T08:12:06.337Z WARN  slow query: orders_by_customer took 912ms",
            "2026-10-10T08:12:07.940Z INFO  POST /v1/orders 201 44ms",
            "2026-10-10T08:12:09.215Z ERROR payment gateway timeout after 3000ms (attempt 1/3)",
            "2026-10-10T08:12:12.402Z INFO  payment gateway recovered",
            "2026-10-10T08:12:13.118Z INFO  GET /healthz 200 1ms",
        ]);
        await ws.OpenPodLogsAsync(pod.Item.Object, "main");
        var logs = Assert.IsType<LogViewModel>(ws.Dock.Selected);
        await UiHarness.WaitFor(() => logs.Buffer.Count >= 9, what: "log lines");
        await Task.Delay(200);
        Shot(h, "logs");
    }

    [AvaloniaFact]
    public async Task Helm_releases()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Helm)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel, what: "helm");
        var vm = (HelmViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => vm.Releases.Any(r => r.Name == "payments"), what: "releases");
        vm.Selected = vm.Releases.First(r => r.Name == "payments" && r.Status == "failed");
        await UiHarness.WaitFor(() => vm.History.Count == 2 && vm.DiagEvents.Count + vm.DiagPods.Count > 0, what: "history and diagnosis");
        Shot(h, "helm");
    }

    [AvaloniaFact]
    public async Task Production_safety_banner()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-production");
        Assert.True(ws.IsProduction);
        await OpenListAsync(ws, "pods", "production");
        Shot(h, "production");
    }

    [AvaloniaFact]
    public async Task Two_clusters_side_by_side()
    {
        await using var h = await UiHarness.StartAsync();
        var staging = await h.ConnectAsync("demo-staging");
        staging.Namespaces.SelectAll();
        staging.OpenKind(staging.Session.Discovery.FindByName("deployments")!);
        var prod = await h.ConnectAsync("demo-production");
        h.Main.SelectedWorkspace = staging;
        h.Main.OpenSideBySide(prod);
        await UiHarness.WaitFor(() => prod.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Deployment" && l.Rows.Count > 0, what: "second pane");
        await UiHarness.WaitFor(() => staging.CurrentContent is ResourceListViewModel l2 && l2.Rows.Count > 0, what: "first pane");
        Shot(h, "split-view");
    }

    [AvaloniaFact]
    public async Task Helm_release_graph()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.Navigate(ws.FindNav(NavTarget.Helm)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel v && v.Releases.Any(r => r.Name == "api"), what: "releases");
        var vm = (HelmViewModel)ws.CurrentContent!;
        vm.Selected = vm.Releases.First(r => r.Name == "api");
        vm.Tab = HelmViewModel.GraphTabIndex;
        await UiHarness.WaitFor(() => vm.Graph is { Nodes.Count: > 3 }, what: "release graph");
        Shot(h, "helm-release-graph");
    }

    [AvaloniaFact]
    public async Task Timeline()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var list = await OpenListAsync(ws, "deployments", "default");
        list.SelectedRow = list.Rows.First(r => r.Name == "web");
        ws.Details.SelectedTab = DetailsViewModel.TimelineTab;
        await UiHarness.WaitFor(() => ws.Details.TimelineRows.Count > 4, what: "timeline");
        Shot(h, "timeline");
    }

    [AvaloniaFact]
    public async Task Security_pages()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Security)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is SecurityViewModel v && v.AuditRows.Count > 0, what: "audit");
        var vm = (SecurityViewModel)ws.CurrentContent!;
        vm.HighOnly = true;
        Shot(h, "security-workloads");
        vm.HighOnly = false;
        vm.Tab = SecurityViewModel.AccessTab;
        await UiHarness.WaitFor(() => vm.AccessRows.Count > 0, what: "rbac");
        vm.SubjectQuery = "ServiceAccount default/ci-bot";
        await vm.LookupCommand.ExecuteAsync(null);
        Shot(h, "security-access");
        vm.Tab = SecurityViewModel.CertificatesTab;
        await UiHarness.WaitFor(() => vm.Certificates.Count > 2, what: "certificates");
        Shot(h, "security-certificates");
    }

    [AvaloniaFact]
    public async Task Gitops()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Navigate(ws.FindNav(NavTarget.GitOps)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is GitOpsViewModel v && v.Rows.Count == 10, what: "gitops rows");
        var vm = (GitOpsViewModel)ws.CurrentContent!;
        vm.Selected = vm.Rows.First(r => r.Display == "argocd/payments");
        Shot(h, "gitops");
    }

    [AvaloniaFact]
    public async Task Capacity_pages()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Navigate(ws.FindNav(NavTarget.Capacity)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is CapacityViewModel v && v.Nodes.Count == 2 && v.Pending.Count > 0, what: "capacity");
        var vm = (CapacityViewModel)ws.CurrentContent!;
        Shot(h, "capacity-nodes");
        vm.PriceCpu = "0.03"; vm.PriceMemory = "0.004";
        vm.ApplyPricesCommand.Execute(null);
        vm.Tab = CapacityViewModel.CostTab;
        Shot(h, "capacity-cost");
        vm.Tab = CapacityViewModel.WhatIfTab;
        await UiHarness.WaitFor(() => vm.SimRows.Count > 0, what: "simulation");
        Shot(h, "capacity-what-if");
    }
}
