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

    private void Rebuild()
    {
        var t = Table;
        if (t is null || t.Rows.Count == 0) { Child = null; return; }
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
