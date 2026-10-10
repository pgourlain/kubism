using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Sessions;
using Kubism.Desktop.ViewModels;
using Kubism.Desktop.Views;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UiTests
{
    [AvaloniaFact]
    public async Task Catalog_lists_the_fake_contexts_with_production_flagged()
    {
        await using var h = await UiHarness.StartAsync();
        Assert.Equal(["demo-production", "demo-staging"], h.Main.Clusters.Select(c => c.Entry.ContextName).OrderBy(x => x));
        Assert.True(h.Cluster("demo-production").IsProduction);
        Assert.False(h.Cluster("demo-staging").IsProduction);
        var list = h.Find<ListBox>(l => l.Name == "CatalogList");
        Assert.NotNull(list);
        Assert.Equal(2, list!.ItemCount);
        h.Screenshot("01-catalog-welcome");
    }

    [AvaloniaFact]
    public async Task Connect_shows_workspace_tab_with_overview()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        Assert.Equal(SessionState.Connected, h.Cluster("demo-staging").State);
        Assert.Single(h.Main.Workspaces);
        Assert.False(h.Main.ShowWelcome);
        await UiHarness.WaitFor(() => ws.CurrentContent is OverviewViewModel o && o.NodesTotal == 2, what: "overview loaded");
        var o = (OverviewViewModel)ws.CurrentContent!;
        Assert.Equal(2, o.NodesReady);
        await UiHarness.WaitFor(() => o.Problems.Count > 0, what: "problem pods");
        Assert.Contains(o.Problems, p => p.Name == "crashy-0" && p.Status == "CrashLoopBackOff");
        Assert.Contains("staging", h.Main.WindowTitle.ToLowerInvariant());
        h.Screenshot("02-overview");
        await h.Main.DisconnectAsync(h.Cluster("demo-staging"));
        Assert.Empty(h.Main.Workspaces);
        Assert.Equal(SessionState.Disconnected, h.Cluster("demo-staging").State);
    }

    [AvaloniaFact]
    public async Task Failing_context_shows_its_error()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.Token = "revoked";
        await h.Main.ConnectAsync(h.Cluster("demo-staging"));
        var item = h.Cluster("demo-staging");
        Assert.Equal(SessionState.Error, item.State);
        Assert.Contains("401", item.ErrorMessage);
        Assert.Empty(h.Main.Workspaces);
        h.Screenshot("03-connection-error");
    }

    [AvaloniaFact]
    public async Task Pods_list_has_server_columns_and_live_updates_in_place()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        var kind = ws.Session.Discovery.FindByName("pods")!;
        ws.Namespaces.SelectAll();
        ws.OpenKind(kind);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 10, what: "pods rows");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        Assert.Contains(list.Columns, c => c.Name == "Status");
        Assert.Contains(list.Columns, c => c.Name == "Restarts");
        Assert.DoesNotContain(list.Columns, c => c.Name == "IP"); // priority 1 columns hidden by default
        var crashy = list.Rows.First(r => r.Name == "crashy-0");
        var statusIdx = list.Columns.ToList().FindIndex(c => c.Name == "Status");
        Assert.Equal("CrashLoopBackOff", crashy.Cells[statusIdx]);
        Assert.Equal(Severity.Error, crashy.Severity);
        await UiHarness.WaitFor(() => list.IsLive, what: "live");

        // A scale event updates the same row object in place (no row replacement).
        list.SelectedRow = crashy;
        var pod = h.Demo.Server.Get(Gvr.Pods, "default", "crashy-0")!;
        pod["status"]!["containerStatuses"]![0]!["restartCount"] = 8;
        pod["status"]!["containerStatuses"]![0]!["state"] = new System.Text.Json.Nodes.JsonObject { ["running"] = new System.Text.Json.Nodes.JsonObject() };
        h.Demo.Server.Upsert(pod);
        var restartsIdx = list.Columns.ToList().FindIndex(c => c.Name == "Restarts");
        await UiHarness.WaitFor(() => crashy.Cells[restartsIdx] == "8", what: "row updated");
        Assert.Same(crashy, list.Rows.First(r => r.Name == "crashy-0"));
        Assert.Same(crashy, list.SelectedRow);

        // New pod appears highlighted, deleted pod goes away.
        var before = list.Rows.Count;
        h.Demo.Server.Upsert(System.Text.Json.Nodes.JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod","metadata":{"name":"fresh","namespace":"default"},"spec":{"containers":[{"name":"main"}]},"status":{"phase":"Pending"}}""")!.AsObject());
        await UiHarness.WaitFor(() => list.Rows.Any(r => r.Name == "fresh"), what: "new row");
        Assert.True(list.Rows.First(r => r.Name == "fresh").IsNew);
        h.Demo.Server.Remove(Gvr.Pods, "default", "fresh");
        await UiHarness.WaitFor(() => list.Rows.All(r => r.Name != "fresh"), what: "row removed");
        Assert.Equal(before, list.Rows.Count);

        var grid = h.Find<DataGrid>(g => g.Name == "Grid");
        Assert.NotNull(grid);
        Assert.True(grid!.Columns.Count >= 5);
        h.Screenshot("04-pods-list");
    }

    [AvaloniaFact]
    public async Task Filter_and_sort_work_on_the_list()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.SelectAll();
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 10);
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.Filter = "checkout";
        await UiHarness.WaitFor(() => list.Rows.Count == 3, what: "filtered");
        Assert.All(list.Rows, r => Assert.StartsWith("checkout", r.Name));
        list.Filter = "app=web";
        await UiHarness.WaitFor(() => list.Rows.Count == 3 && list.Rows.All(r => r.Name.StartsWith("web")));
        list.Filter = "";
        await UiHarness.WaitFor(() => list.Rows.Count > 10);
        var nameIdx = list.Columns.ToList().FindIndex(c => c.Name == "Name");
        list.ToggleSort(nameIdx);
        await UiHarness.WaitFor(() => list.Rows.Select(r => r.Name).SequenceEqual(list.Rows.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), what: "sorted asc");
        list.ToggleSort(nameIdx);
        await UiHarness.WaitFor(() => list.Rows[0].Name.CompareTo(list.Rows[^1].Name) > 0, what: "sorted desc");
    }

    [AvaloniaFact]
    public async Task Custom_resource_appears_under_custom_resources_with_its_printer_columns()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        var cr = ws.NavRoots.First(n => n.Title == "Custom Resources");
        var group = cr.Children.First(c => c.Title == "cert-manager.io");
        var certNode = group.Children.First(c => c.Title == "Certificate");
        ws.Namespaces.SelectAll();
        ws.Navigate(certNode);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count == 2, what: "certificates");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        Assert.Contains(list.Columns, c => c.Name == "Secret");   // CRD additionalPrinterColumns
        Assert.Contains(list.Columns, c => c.Name == "Ready");
        h.Screenshot("05-custom-resource");
    }

    [AvaloniaFact]
    public async Task Command_palette_finds_kinds_by_short_name_and_jumps()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        h.Main.OpenPaletteCommand.Execute(null);
        Assert.True(h.Main.PaletteOpen);
        h.Main.Palette.Query = "deploy";
        Assert.StartsWith("Deployment", h.Main.Palette.Results[0].Title);
        h.Main.Palette.Query = "pods";
        Assert.StartsWith("Pod", h.Main.Palette.Results[0].Title);
        h.Main.Palette.Query = "po";
        Assert.StartsWith("Pod", h.Main.Palette.Results[0].Title);
        await h.Main.Palette.ExecuteAsync();
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Pod");
        Assert.False(h.Main.PaletteOpen);
        h.Main.OpenPaletteCommand.Execute(null);
        h.Main.Palette.Query = "kube-sys";
        Assert.Contains(h.Main.Palette.Results, r => r.Category == "Namespace" && r.Title == "kube-system");
        h.Screenshot("06-command-palette");
    }

    [AvaloniaFact]
    public async Task Namespace_selection_is_remembered_per_cluster()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        await UiHarness.WaitFor(() => ws.Namespaces.Choices.Count >= 4, what: "namespaces");
        ws.Namespaces.Select("production");
        Assert.Equal(["production"], ws.Namespaces.Selected);
        Assert.Equal("production", h.Main.Settings.Current.SelectedNamespaces[ws.Id][0]);
    }

    [AvaloniaFact]
    public async Task Forbidden_namespace_listing_lets_user_type_namespaces()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.Deny("list", "namespaces");
        var ws = await h.ConnectAsync();
        await UiHarness.WaitFor(() => ws.Namespaces.IsForbidden, what: "forbidden flag");
        ws.Namespaces.AddManual("team-a");
        Assert.Contains(ws.Namespaces.Choices, c => c.Name == "team-a" && c.IsSelected);
        Assert.Contains("team-a", ws.Session.Metadata.Namespaces);
    }
}
