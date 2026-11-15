using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Usage;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UsageDeleteTests
{
    private const string Seed = """
        {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"used-cfg","namespace":"del-demo"}}
        ---
        {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"idle-a","namespace":"del-demo"}}
        ---
        {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"idle-b","namespace":"del-demo"}}
        ---
        {"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"app","namespace":"del-demo","uid":"u-app"},"spec":{"replicas":1,"template":{"spec":{"containers":[{"name":"c","image":"x:1","envFrom":[{"configMapRef":{"name":"used-cfg"}}]}]}}}}
        """;

    private static async Task<(UiHarness h, WorkspaceViewModel ws, UsageViewModel vm)> OpenAsync(string cluster)
    {
        var h = await UiHarness.StartAsync();
        h.Demo.Server.SeedYaml(Seed);
        var ws = await h.ConnectAsync(cluster);
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.ConfigUsage)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is UsageViewModel v && v.Rows.Any(r => r.Row.Name == "idle-a"), what: "config usage");
        return (h, ws, (UsageViewModel)ws.CurrentContent!);
    }

    private static bool Exists(UiHarness h, string name) => h.Demo.Server.Get(new Gvr("", "v1", "configmaps"), "del-demo", name) is not null;

    [AvaloniaFact]
    public async Task Selected_not_referenced_objects_are_deleted_and_used_ones_never()
    {
        var (h, ws, vm) = await OpenAsync("demo-staging");
        await using var _ = h;

        Assert.False(vm.CanDeleteSelected);
        // "Select not referenced" asks the view for exactly the Not referenced rows.
        IReadOnlyList<UsageRowViewModel>? asked = null;
        vm.SelectionRequested += r => asked = r;
        vm.SelectUnreferencedCommand.Execute(null);
        Assert.NotNull(asked);
        Assert.All(asked!, r => Assert.Equal(UsageStatus.Unused, r.Row.Status));
        Assert.Contains(asked!, r => r.Row.Name == "idle-a");
        Assert.DoesNotContain(asked!, r => r.Row.Name == "used-cfg");

        // Even when a used row is selected with them, it stays.
        vm.SetSelectedRows(vm.Rows.Where(r => r.Row.Name is "used-cfg" or "idle-a" or "idle-b"));
        Assert.True(vm.CanDeleteSelected);
        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.False(Exists(h, "idle-a"));
        Assert.False(Exists(h, "idle-b"));
        Assert.True(Exists(h, "used-cfg"));
        Assert.Contains(ws.Session.Activity.Entries, e => e.Action == "DeleteMany" && e.Success);
        await UiHarness.WaitFor(() => vm.Rows.All(r => r.Row.Name != "idle-a"), what: "list reloaded");
    }

    [AvaloniaFact]
    public async Task Production_asks_for_the_count_and_read_only_refuses()
    {
        var (h, ws, vm) = await OpenAsync("demo-production");
        await using var _ = h;
        vm.SetSelectedRows(vm.Rows.Where(r => r.Row.Name is "idle-a" or "idle-b"));

        h.Dialogs.TypedResponse = _ => "nope";
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Contains(h.Dialogs.Shown, s => s == "typed:Delete on production:2");
        Assert.True(Exists(h, "idle-a"));

        h.Dialogs.TypedResponse = e => e;
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.False(Exists(h, "idle-a"));
        Assert.False(Exists(h, "idle-b"));
    }

    [AvaloniaFact]
    public async Task Read_only_cluster_deletes_nothing()
    {
        var (h, ws, vm) = await OpenAsync("demo-staging");
        await using var _ = h;
        var m = h.Main.Settings.GetMetadata(ws.Id); m.SafetyMode = SafetyMode.ReadOnly; h.Main.Settings.SetMetadata(ws.Id, m);
        vm.SetSelectedRows(vm.Rows.Where(r => r.Row.Name == "idle-a"));
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.True(Exists(h, "idle-a"));
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("error:Read-only cluster"));
    }

    [AvaloniaFact]
    public async Task The_select_button_selects_the_rows_in_the_grid_and_enables_delete()
    {
        var (h, ws, vm) = await OpenAsync("demo-staging");
        await using var _ = h;
        UiHarness.Pump();
        var grid = h.Window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "RowsGrid");
        Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
        vm.SelectUnreferencedCommand.Execute(null);
        UiHarness.Pump();
        var unused = vm.Rows.Count(r => r.Row.Status == UsageStatus.Unused);
        Assert.True(unused >= 2);
        Assert.Equal(unused, grid.SelectedItems.Count);
        Assert.True(vm.CanDeleteSelected);
        h.Screenshot("72-usage-select-unreferenced");
    }

    /// <summary>The --demo cluster carries storage nothing refers to, so the delete flow can be tried by hand.</summary>
    [AvaloniaFact]
    public async Task The_demo_has_unreferenced_storage_to_clean_up()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.StorageUsage)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is UsageViewModel v && v.Rows.Count > 0, what: "storage usage");
        var vm = (UsageViewModel)ws.CurrentContent!;
        string Unused() => string.Join(", ", vm.Rows.Where(r => r.Row.Status == UsageStatus.Unused).Select(r => r.Row.Display).Order());
        var claims = Unused();
        Assert.Contains("staging/old-uploads", claims);
        Assert.Contains("default/scratch-2023", claims);
        Assert.DoesNotContain("staging/db-data", claims);
        vm.Section = 1;
        Assert.Contains("pv-released-1", Unused());
        Assert.DoesNotContain("pv-db-data", Unused());
        vm.Section = 2;
        Assert.Contains("azurefile-legacy", Unused());
        Assert.DoesNotContain("fast-ssd", Unused());
    }
}
