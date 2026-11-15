using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class GridSortingTests
{
    /// <summary>Every column with a header can be sorted from its header; a template column needs a SortMemberPath for that.</summary>
    [Fact]
    public void Every_template_column_with_a_header_names_what_it_sorts_on()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Kubism.Desktop", "Views"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(dir!, "Kubism.Desktop", "Views"), "*.axaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "<DataGridTemplateColumn\\b[^>]*>"))
                if (m.Value.Contains("Header=") && !m.Value.Contains("SortMemberPath=")) missing.Add($"{Path.GetFileName(file)}: {m.Value}");
        Assert.Empty(missing);
    }

    [AvaloniaFact]
    public async Task Clicking_a_header_sorts_the_pod_list_and_shows_the_direction()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        h.Main.OpenPaletteCommand.Execute(null);
        h.Main.Palette.Query = "pods";
        await h.Main.Palette.ExecuteAsync();
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Pod" && l.Rows.Count > 5);
        UiHarness.Pump();
        var vm = (ResourceListViewModel)ws.CurrentContent!;
        var grid = h.Window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "Grid");

        void Click(string name)
        {
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(x => x.Content?.ToString()?.StartsWith(name) == true);
            var p = header.TranslatePoint(new Point(10, 10), h.Window)!.Value;
            h.Window.MouseDown(p, MouseButton.Left); h.Window.MouseUp(p, MouseButton.Left);
            UiHarness.Pump();
        }

        Click("Status");
        await UiHarness.WaitFor(() => vm.SortColumn >= 0 && !vm.SortDescending && grid.Columns[vm.SortColumn].Header?.ToString() == "Status ▲", what: "ascending arrow");
        var status = vm.SortColumn;
        Assert.Equal("CrashLoopBackOff", vm.Rows[0].Cells[status]);

        Click("Status");
        await UiHarness.WaitFor(() => vm.SortDescending && grid.Columns[status].Header?.ToString() == "Status ▼", what: "descending arrow");
        Assert.Equal(1, grid.Columns.Count(c => c.Header?.ToString()?.EndsWith("▼") == true || c.Header?.ToString()?.EndsWith("▲") == true));
    }

    [AvaloniaFact]
    public async Task Every_pod_column_sorts_from_its_header()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        h.Main.OpenPaletteCommand.Execute(null);
        h.Main.Palette.Query = "pods";
        await h.Main.Palette.ExecuteAsync();
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Pod" && l.Rows.Count > 5);
        UiHarness.Pump();
        var vm = (ResourceListViewModel)ws.CurrentContent!;
        var grid = h.Window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "Grid");
        var report = new List<string>();
        for (var i = 0; i < vm.Columns.Count; i++)
        {
            var name = vm.Columns[i].Name;
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().FirstOrDefault(x => (x.Content as TextBlock)?.Text?.StartsWith(name) == true || x.Content?.ToString()?.StartsWith(name) == true);
            if (header is null) { report.Add($"{name}: no header"); continue; }
            var p = header.TranslatePoint(new Point(10, 10), h.Window)!.Value;
            h.Window.MouseDown(p, MouseButton.Left); h.Window.MouseUp(p, MouseButton.Left);
            UiHarness.Pump();
            await Task.Delay(50); UiHarness.Pump();
            if (vm.SortColumn != i) { report.Add($"{name}: click did not sort (SortColumn={vm.SortColumn})"); continue; }
            // Usage columns sort on the value the cell starts with, in a common unit.
            if (name is "CPU" or "Memory")
            {
                static double Lead(string t)
                {
                    if (t.Length == 0) return -1;
                    var w = t.Split(' ')[0];
                    return Kubism.Core.Views.Quantities.ParseCpu(w) is var c && w.EndsWith('m') ? c : Kubism.Core.Views.Quantities.ParseBytes(w);
                }
                var vals = vm.Rows.Select(r => Lead(r.Cells[i])).ToList();
                if (!vals.SequenceEqual(vals.Order())) report.Add($"{name}: not in usage order: {string.Join(" | ", vm.Rows.Select(r => r.Cells[i]))}");
            }
        }
        Assert.Empty(report);
    }

    [AvaloniaFact]
    public async Task The_grid_shows_rows_in_the_sorted_order_and_keeps_the_selection()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        h.Main.OpenPaletteCommand.Execute(null);
        h.Main.Palette.Query = "pods";
        await h.Main.Palette.ExecuteAsync();
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Pod" && l.Rows.Count > 5);
        UiHarness.Pump();
        var vm = (ResourceListViewModel)ws.CurrentContent!;
        vm.ShowAllColumns = true;
        UiHarness.Pump();
        var grid = h.Window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "Grid");
        var i = vm.Columns.ToList().FindIndex(c => c.Name == "Node");
        var report = new List<string>();
        for (var k = 0; k < 3; k++)
        {
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(x => ((x.Content as TextBlock)?.Text ?? x.Content?.ToString())?.StartsWith("Node") == true);
            var p = header.TranslatePoint(new Point(10, 10), h.Window)!.Value;
            h.Window.MouseDown(p, MouseButton.Left); h.Window.MouseUp(p, MouseButton.Left);
            UiHarness.Pump(); await Task.Delay(100); UiHarness.Pump();
            var shown = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.Index >= 0 && r.Index < vm.Rows.Count && r.IsVisible).OrderBy(r => r.Index).Select(r => ((ResourceRowViewModel)r.DataContext!).Cells[i]).ToList();
            if (!shown.SequenceEqual(vm.Rows.Select(r => r.Cells[i]))) report.Add($"click{k}: grid [{string.Join(",", shown)}] vm [{string.Join(",", vm.Rows.Select(r => r.Cells[i]))}]");
            if (k == 0) { vm.SelectedRow = vm.Rows[0]; UiHarness.Pump(); }
            else if (!ReferenceEquals(vm.SelectedRow, vm.Rows.FirstOrDefault(r => ReferenceEquals(r, vm.SelectedRow)))) report.Add("selection lost");
        }
        Assert.Empty(report);
    }

    [AvaloniaFact]
    public async Task Clicking_a_header_sorts_the_images_grid()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.SeedYaml("""
            {"apiVersion":"v1","kind":"Pod","metadata":{"name":"a","namespace":"s","uid":"s1"},"spec":{"containers":[{"name":"c","image":"zzz/app:1"}]},"status":{"containerStatuses":[{"name":"c","state":{"running":{}}}]}}
            ---
            {"apiVersion":"v1","kind":"Pod","metadata":{"name":"b","namespace":"s","uid":"s2"},"spec":{"containers":[{"name":"c","image":"aaa/app:1"}]},"status":{"containerStatuses":[{"name":"c","state":{"running":{}}}]}}
            ---
            {"apiVersion":"v1","kind":"Pod","metadata":{"name":"c","namespace":"s","uid":"s3"},"spec":{"containers":[{"name":"c","image":"mmm/app:1"}]},"status":{"containerStatuses":[{"name":"c","state":{"running":{}}}]}}
            """);
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Images)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ImagesViewModel v && v.Rows.Any(r => r.Reference.StartsWith("mmm")), what: "images");
        UiHarness.Pump();
        var vm = (ImagesViewModel)ws.CurrentContent!;
        var grid = h.Window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "RowsGrid");
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(x => x.Content?.ToString() == "Repository");
        List<string> Shown() => grid.GetVisualDescendants().OfType<DataGridRow>().OrderBy(r => r.Bounds.Y).Select(r => ((ImageRowViewModel)r.DataContext!).Repository).ToList();
        var p = header.TranslatePoint(new Point(10, 10), h.Window)!.Value;
        for (var i = 0; i < 2; i++) { h.Window.MouseDown(p, MouseButton.Left); h.Window.MouseUp(p, MouseButton.Left); UiHarness.Pump(); }
        var shown = Shown();
        Assert.Equal(shown.OrderByDescending(x => x, StringComparer.Ordinal).ToList(), shown);
    }

    /// <summary>A narrow header gets its whole width for the title (no room kept for a sort arrow that is not there) and ends with an ellipsis instead of a hard cut.</summary>
    [AvaloniaFact]
    public async Task A_narrow_header_uses_its_width_and_trims_its_title()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        h.Main.OpenPaletteCommand.Execute(null);
        h.Main.Palette.Query = "pods";
        await h.Main.Palette.ExecuteAsync();
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Pod" && l.Rows.Count > 5);
        UiHarness.Pump();
        var grid = h.Window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "Grid");
        var column = grid.Columns.First(c => c.Header?.ToString() == "Restarts");
        column.Width = new DataGridLength(column.MinWidth);
        UiHarness.Pump();

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(x => x.Content?.ToString() == "Restarts");
        var title = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Restarts");
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
        var room = header.Bounds.Width - header.Padding.Left - header.Padding.Right;
        Assert.True(title.Bounds.Width >= room - 2, $"title gets {title.Bounds.Width:0} of {room:0} px");
        h.Screenshot("71-narrow-header");
    }
}
