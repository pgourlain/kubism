using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;

namespace Kubism.Desktop.Controls;

/// <summary>
/// The one Refresh button of every page: the circular-arrow icon, same size and colour, with a tooltip that says what it refreshes
/// (set <c>ToolTip.Tip</c> to be more specific than "Refresh").
/// </summary>
public sealed class RefreshButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public RefreshButton()
    {
        Padding = new Thickness(9, 6);
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        ToolTip.SetTip(this, "Refresh");
        AutomationProperties.SetName(this, "Refresh");
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M20,12 A8,8 0 1 1 16,5 M20,3 V9 H14"),
        };
        icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("KubismMutedBrush"));
        Content = icon;
    }
}
