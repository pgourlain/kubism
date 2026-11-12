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
