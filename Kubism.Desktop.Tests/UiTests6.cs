using Avalonia.Headless.XUnit;
using Kubism.Core.Views;
using Kubism.Desktop.Controls;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UiExplorationTests
{
    [AvaloniaFact]
    public async Task Graph_tab_draws_the_neighbourhood_of_a_pod_and_navigates_on_click()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "pods");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name.StartsWith("web-"));
        var d = ws.Details;
        d.SelectedTab = DetailsViewModel.GraphTab;
        await UiHarness.WaitFor(() => d.Graph is { Nodes.Count: > 1 }, what: "graph");
        var g = d.Graph!;
        Assert.Contains(g.Nodes, n => n.Kind == "Deployment" && n.Name == "web");
        Assert.Contains(g.Nodes, n => n.Kind == "ReplicaSet");
        Assert.Contains(g.Nodes, n => n.Kind == "Service" && n.Name == "web");
        Assert.Contains(g.Edges, e => e.Label == "selects");
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "Secret" && n.Name != "db-credentials");
        Assert.NotNull(h.Find<RelationGraphControl>());
        h.Screenshot("30-relation-graph");

        d.OpenGraphNodeCommand.Execute(g.Nodes.First(n => n.Kind == "Deployment"));
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l2 && l2.Kind.Kind == "Deployment", what: "deployments list");
        Assert.Equal("web", ((ResourceListViewModel)ws.CurrentContent!).Filter);
    }

    [AvaloniaFact]
    public async Task Graph_never_loads_secret_values()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("secrets")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "secrets");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First();
        ws.Details.SelectedTab = DetailsViewModel.GraphTab;
        await UiHarness.WaitFor(() => ws.Details.Graph is not null, what: "graph");
        Assert.Contains(ws.Details.Graph!.Nodes, n => n.IsRoot && n.Kind == "Secret");
        Assert.DoesNotContain("data", ws.Details.GraphStatus);
    }

    [AvaloniaFact]
    public async Task Global_search_by_image_lists_workloads_with_the_matching_image()
    {
        await using var h = await UiHarness.StartAsync();
        await h.ConnectAsync("demo-staging");
        h.Main.OpenGlobalSearchCommand.Execute(null);
        h.Main.Search.Query = "image:nginx";
        await h.Main.Search.SearchAsync();
        var group = Assert.Single(h.Main.Search.Groups);
        Assert.Contains(group.Hits, x => x.Kind == "Deployment" && x.Name == "web" && x.Detail!.Contains("nginx"));
        Assert.Contains(group.Hits, x => x.Kind == "Pod");
        Assert.All(group.Hits, x => Assert.Contains("nginx", x.Detail!));
        h.Main.Search.Query = "ns:production kind:deploy";
        await h.Main.Search.SearchAsync();
        Assert.All(h.Main.Search.Groups.SelectMany(g => g.Hits), x => { Assert.Equal("Deployment", x.Kind); Assert.Equal("production", x.Namespace); });
        Assert.Contains(h.Main.Search.Groups.SelectMany(g => g.Hits), x => x.Name == "checkout");
        h.Main.Search.Query = "image:nginx kind:deploy";
        await h.Main.Search.SearchAsync();
        h.Screenshot("32-search-by-image");
    }

    [AvaloniaFact]
    public async Task Compare_shows_drift_between_two_clusters_without_leaking_secrets()
    {
        await using var h = await UiHarness.StartAsync(withDrift: true);
        var staging = await h.ConnectAsync("demo-staging");
        await h.ConnectAsync("demo-dev");
        h.Main.SelectedWorkspace = staging;
        staging.Namespaces.Select("default");
        staging.Navigate(staging.FindNav(NavTarget.Compare)!);
        await UiHarness.WaitFor(() => staging.CurrentContent is CompareViewModel, what: "compare view");
        var cmp = (CompareViewModel)staging.CurrentContent!;
        Assert.Equal("demo-dev", cmp.Target.Title);
        cmp.Kind = cmp.Kinds.First(k => k.Left.Resource == "deployments");
        await cmp.CompareAsync();
        Assert.Contains(cmp.Rows, r => r.Entry is { Name: "web", Status: CompareStatus.Different });
        Assert.Contains(cmp.Rows, r => r.Entry is { Name: "api", Status: CompareStatus.OnlyLeft });
        Assert.Contains(cmp.Rows, r => r.Entry is { Name: "legacy-importer", Status: CompareStatus.OnlyRight });
        cmp.Selected = cmp.Rows.First(r => r.Entry.Name == "web");
        Assert.Contains(cmp.DiffLines, l => l.Kind == DiffLineKind.Removed && l.Text.Contains("nginx:1.27"));
        Assert.Contains(cmp.DiffLines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("nginx:1.25"));
        h.Screenshot("31-compare-clusters");

        cmp.Kind = cmp.Kinds.First(k => k.Left.Resource == "secrets");
        await cmp.CompareAsync();
        cmp.ShowIdentical = true;
        var secret = cmp.Rows.First(r => r.Entry.Name == "db-credentials");
        Assert.Equal(CompareStatus.Different, secret.Entry.Status);
        cmp.Selected = secret;
        Assert.DoesNotContain(cmp.DiffLines, l => l.Text.Contains("another-value") || l.Text.Contains("YW5vdGhlci12YWx1ZQ"));
        Assert.Contains(cmp.DiffLines, l => l.Text.Contains("•••• A"));
    }

    [AvaloniaFact]
    public async Task Compare_from_an_object_prefills_kind_namespace_and_name()
    {
        await using var h = await UiHarness.StartAsync(withDrift: true);
        var staging = await h.ConnectAsync("demo-staging");
        await h.ConnectAsync("demo-dev");
        h.Main.SelectedWorkspace = staging;
        staging.Namespaces.Select("default");
        staging.OpenKind(staging.Session.Discovery.FindByName("configmaps")!);
        await UiHarness.WaitFor(() => staging.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "configmaps");
        var list = (ResourceListViewModel)staging.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "app-config");
        await UiHarness.WaitFor(() => staging.Details.Actions.Any(a => a.Header == "Compare…"), what: "compare action");
        await staging.Details.Actions.First(a => a.Header == "Compare…").ExecuteAsync();
        await UiHarness.WaitFor(() => staging.CurrentContent is CompareViewModel c && c.Rows.Count > 0, what: "compare rows");
        var cmp = (CompareViewModel)staging.CurrentContent!;
        Assert.Equal("ConfigMap", cmp.Kind!.Left.Kind);
        Assert.Equal("app-config", cmp.NameFilter);
        var row = Assert.Single(cmp.Rows);
        Assert.Equal(CompareStatus.Different, row.Entry.Status);
        Assert.Contains(cmp.DiffLines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("LOG_LEVEL"));
    }

    [AvaloniaFact]
    public async Task Graph_shows_the_helm_release_of_an_object_and_opens_it()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "pods");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "crashy-0");
        ws.Details.SelectedTab = DetailsViewModel.GraphTab;
        await UiHarness.WaitFor(() => ws.Details.Graph is { Nodes.Count: > 1 }, what: "graph");
        var release = ws.Details.Graph!.Nodes.Single(n => n.Kind == "HelmRelease");
        Assert.Equal("payments", release.Name);
        Assert.Equal(NodeHealth.Error, release.Health);
        Assert.StartsWith("rev 2", release.Status);
        Assert.DoesNotContain(ws.Details.Graph.Nodes, n => n.Kind == "Secret");   // release payloads are not graph objects
        h.Screenshot("33-graph-helm");

        ws.Details.OpenGraphNodeCommand.Execute(release);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel v && v.Selected?.Name == "payments", what: "helm page");
    }

    [AvaloniaFact]
    public async Task Helm_release_graph_lists_what_it_rendered_and_what_runs_below()
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
        var g = vm.Graph!;
        Assert.True(g.Nodes.Single(n => n.IsRoot).Kind == "HelmRelease");
        Assert.Contains(g.Edges, e => e is { From: "HelmRelease/default/api", To: "Deployment/default/api", Label: "manages" });
        Assert.Contains(g.Edges, e => e is { From: "HelmRelease/default/api", To: "ConfigMap/default/app-config", Label: "manages" });
        Assert.Contains(g.Nodes, n => n.Kind == "Pod");
        Assert.DoesNotContain(g.Nodes, n => n.Name == "crashy-0");
        h.Screenshot("34-helm-release-graph");
    }

    [AvaloniaFact]
    public async Task Timeline_tab_shows_rollouts_events_and_oom_kills_on_one_axis()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("deployments")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "deployments");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "web");
        var d = ws.Details;
        d.SelectedTab = DetailsViewModel.TimelineTab;
        await UiHarness.WaitFor(() => d.TimelineRows.Count > 4, what: "timeline");
        Assert.Contains(d.TimelineRows, r => r.Title == "Rollout to revision 2" && r.Detail == "nginx:1.27");
        Assert.Contains(d.TimelineRows, r => r.Title == "Rollout to revision 1" && r.Detail == "nginx:1.25");
        Assert.Contains(d.TimelineRows, r => r.Title == "ScalingReplicaSet" && r.Source == "Deployment/web");
        Assert.Contains(d.TimelineRows, r => r.KindLabel == "created" && r.Source.StartsWith("Pod/web-"));
        h.Screenshot("35-timeline");

        list = (ResourceListViewModel)ws.CurrentContent!;
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l2 && l2.Kind.Kind == "Pod" && l2.Rows.Count > 0, what: "pods");
        var pods = (ResourceListViewModel)ws.CurrentContent!;
        pods.SelectedRow = pods.Rows.First(r => r.Name == "crashy-0");
        d.SelectedTab = DetailsViewModel.TimelineTab;
        await UiHarness.WaitFor(() => d.TimelineRows.Any(r => r.KindLabel == "oom"), what: "oom entry");
        d.ProblemsOnly = true;
        Assert.All(d.TimelineRows, r => Assert.NotEqual(Severity.Neutral, r.Severity));
        Assert.Contains(d.TimelineRows, r => r.Title == "BackOff");
        h.Screenshot("36-timeline-problems");
    }
}
