using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Controls;

/// <summary>Small read-only table for the details Overview (containers, conditions, ports…).</summary>
public sealed class SimpleTable : Border
{
    public static readonly StyledProperty<DetailTable?> TableProperty = AvaloniaProperty.Register<SimpleTable, DetailTable?>(nameof(Table));
    public DetailTable? Table { get => GetValue(TableProperty); set => SetValue(TableProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TableProperty) Rebuild();
    }

    /// <summary>Wide tables (containers…) become one block per row: bold first cell, then label/value lines. Readable in a narrow panel.</summary>
    private static Control BuildStacked(DetailTable t)
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var row in t.Rows)
        {
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(new TextBlock { Text = row.Count > 0 ? row[0] : "", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*") };
            var r = 0;
            for (var c = 1; c < row.Count && c < t.Headers.Count; c++)
            {
                if (string.IsNullOrWhiteSpace(row[c]) || row[c] is "-/-" or "-") continue;
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = t.Headers[c], Opacity = 0.65, FontSize = 12 };
                var value = new SelectableTextBlock { Text = row[c], TextWrapping = TextWrapping.Wrap, FontSize = 12.5, FontFamily = t.Headers[c] is "Image" or "Value" ? new FontFamily("Menlo,Consolas,monospace") : FontFamily.Default };
                Grid.SetRow(label, r); Grid.SetRow(value, r); Grid.SetColumn(value, 1);
                g.Children.Add(label); g.Children.Add(value); r++;
            }
            block.Children.Add(g);
            stack.Children.Add(new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = Converters.Brush("KubismBorderBrush"), Child = block });
        }
        return stack;
    }

    private void Rebuild()
    {
        var t = Table;
        if (t is null || t.Rows.Count == 0) { Child = null; return; }
        if (t.Headers.Count > 4) { Child = BuildStacked(t); return; }
        var grid = new Grid();
        for (var c = 0; c < t.Headers.Count; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(c == t.Headers.Count - 1 ? GridLength.Star : GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < t.Headers.Count; c++)
        {
            var h = new TextBlock { Text = t.Headers[c], FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 14, 4), Opacity = 0.75 };
            Grid.SetColumn(h, c); grid.Children.Add(h);
        }
        for (var r = 0; r < t.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < t.Rows[r].Count && c < t.Headers.Count; c++)
            {
                var tb = new TextBlock { Text = t.Rows[r][c], Margin = new Thickness(0, 1, 14, 1), TextWrapping = c == t.Headers.Count - 1 ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
                Grid.SetRow(tb, r + 1); Grid.SetColumn(tb, c); grid.Children.Add(tb);
            }
        }
        Child = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
}
