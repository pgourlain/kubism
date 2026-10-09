using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kubism.Desktop.Controls;

/// <summary>
/// Horizontal bar: p95 usage (solid), max usage (lighter), with request and limit markers on a common scale.
/// Used by the right-sizing dashboard to put requests/limits next to real usage.
/// </summary>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> RequestProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Request));
    public static readonly StyledProperty<double> LimitProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Limit));
    public static readonly StyledProperty<double> P95Property = AvaloniaProperty.Register<UsageBar, double>(nameof(P95));
    public static readonly StyledProperty<double> MaxProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Max));

    static UsageBar() => AffectsRender<UsageBar>(RequestProperty, LimitProperty, P95Property, MaxProperty);

    public double Request { get => GetValue(RequestProperty); set => SetValue(RequestProperty, value); }
    public double Limit { get => GetValue(LimitProperty); set => SetValue(LimitProperty, value); }
    public double P95 { get => GetValue(P95Property); set => SetValue(P95Property, value); }
    public double Max { get => GetValue(MaxProperty); set => SetValue(MaxProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 16);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        var scale = new[] { Request, Limit, P95, Max }.Max();
        if (scale <= 0 || w <= 0) { ctx.DrawRectangle(Converters.Brush("KubismSidebarBrush"), null, new Rect(0, 4, w, h - 8)); return; }
        scale *= 1.1;
        double X(double v) => Math.Clamp(v / scale, 0, 1) * w;
        var track = Converters.Brush("KubismSidebarBrush") ?? Brushes.LightGray;
        var accent = Converters.Brush("KubismAccentBrush") ?? Brushes.SteelBlue;
        var warn = Converters.Brush("KubismWarnBrush") ?? Brushes.Orange;
        var err = Converters.Brush("KubismErrorBrush") ?? Brushes.Red;
        var fg = Converters.Brush("KubismMutedBrush") ?? Brushes.Gray;
        ctx.DrawRectangle(track, null, new Rect(0, 4, w, h - 8), 2, 2);
        var maxBrush = new SolidColorBrush(((accent as ISolidColorBrush)?.Color ?? Colors.SteelBlue), 0.35);
        ctx.DrawRectangle(maxBrush, null, new Rect(0, 4, X(Max), h - 8), 2, 2);
        var over = Request > 0 && P95 > Request;
        ctx.DrawRectangle(over ? err : accent, null, new Rect(0, 5, X(P95), h - 10), 2, 2);
        if (Request > 0) ctx.DrawLine(new Pen(warn, 2), new Point(X(Request), 0), new Point(X(Request), h));
        if (Limit > 0) ctx.DrawLine(new Pen(fg, 2, DashStyle.Dash), new Point(X(Limit), 0), new Point(X(Limit), h));
    }
}
