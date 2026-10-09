using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kubism.Desktop.Controls;

/// <summary>Line chart of a series with optional horizontal request and limit lines (right-sizing detail, metrics).</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<double> RequestProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Request));
    public static readonly StyledProperty<double> LimitProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Limit));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, RequestProperty, LimitProperty, StrokeProperty);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Request { get => GetValue(RequestProperty); set => SetValue(RequestProperty, value); }
    public double Limit { get => GetValue(LimitProperty); set => SetValue(LimitProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 60 : Math.Min(availableSize.Height, 120));

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        ctx.DrawRectangle(null, new Pen(Converters.Brush("KubismBorderBrush"), 1), new Rect(0, 0, w, h));
        var v = Values;
        if (v is null || v.Count < 2 || w <= 0) return;
        var max = Math.Max(v.Max(), Math.Max(Request, Limit)) * 1.1;
        if (max <= 0) return;
        double Y(double val) => h - 2 - Math.Clamp(val / max, 0, 1) * (h - 4);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, Y(v[0])), false);
            for (var i = 1; i < v.Count; i++) g.LineTo(new Point(i * (w / (v.Count - 1)), Y(v[i])));
        }
        ctx.DrawGeometry(null, new Pen(Stroke ?? Converters.Brush("KubismAccentBrush"), 1.5), geo);
        if (Request > 0) ctx.DrawLine(new Pen(Converters.Brush("KubismWarnBrush"), 1.5), new Point(0, Y(Request)), new Point(w, Y(Request)));
        if (Limit > 0) ctx.DrawLine(new Pen(Converters.Brush("KubismErrorBrush"), 1.5, DashStyle.Dash), new Point(0, Y(Limit)), new Point(w, Y(Limit)));
    }
}
