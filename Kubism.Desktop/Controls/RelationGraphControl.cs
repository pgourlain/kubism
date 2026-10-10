using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Kubism.Core.Views;

namespace Kubism.Desktop.Controls;

/// <summary>Draws a <see cref="RelationGraph"/> top to bottom: a box per object (kind, name, status), an arrow per relation with its label. Click a box to open that object.</summary>
public sealed class RelationGraphControl : Control
{
    public static readonly StyledProperty<RelationGraph?> GraphProperty = AvaloniaProperty.Register<RelationGraphControl, RelationGraph?>(nameof(Graph));
    public static readonly StyledProperty<ICommand?> NodeCommandProperty = AvaloniaProperty.Register<RelationGraphControl, ICommand?>(nameof(NodeCommand));

    private const double BoxW = 150, BoxH = 46, GapX = 14, GapY = 42, Pad = 12;
    private static readonly Typeface Face = new("Inter, Segoe UI, Helvetica Neue, sans-serif");

    static RelationGraphControl()
    {
        AffectsRender<RelationGraphControl>(GraphProperty);
        AffectsMeasure<RelationGraphControl>(GraphProperty);
    }

    public RelationGraph? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public ICommand? NodeCommand { get => GetValue(NodeCommandProperty); set => SetValue(NodeCommandProperty, value); }

    /// <summary>Width needed to draw the widest level; the details panel widens to this.</summary>
    public static double WidthFor(RelationGraph g) => g.Nodes.Count == 0 ? 0 : Pad * 2 + g.MaxRows * BoxW + (g.MaxRows - 1) * GapX + 24;

    protected override Size MeasureOverride(Size availableSize)
    {
        var g = Graph;
        if (g is null || g.Nodes.Count == 0) return new Size(0, 0);
        return new Size(WidthFor(g) - 24, Pad * 2 + g.Layers * BoxH + (g.Layers - 1) * GapY);
    }

    /// <summary>Each level is a row of boxes, centred horizontally on the widest level.</summary>
    private Rect BoxOf(GraphNode n, RelationGraph g)
    {
        var inLayer = g.Nodes.Count(x => x.Layer == n.Layer);
        var levelWidth = inLayer * BoxW + (inLayer - 1) * GapX;
        var total = g.MaxRows * BoxW + (g.MaxRows - 1) * GapX;
        var left = Pad + (total - levelWidth) / 2 + n.Row * (BoxW + GapX);
        return new Rect(left, Pad + n.Layer * (BoxH + GapY), BoxW, BoxH);
    }

    private static IBrush HealthBrush(NodeHealth h) => Converters.Brush(h switch
    {
        NodeHealth.Ok => "KubismOkBrush", NodeHealth.Warn => "KubismWarnBrush", NodeHealth.Error => "KubismErrorBrush", _ => "KubismMutedBrush",
    }) ?? Brushes.Gray;

    public override void Render(DrawingContext ctx)
    {
        var g = Graph;
        if (g is null) return;
        var panel = Converters.Brush("KubismPanelBrush") ?? Brushes.White;
        var side = Converters.Brush("KubismSidebarBrush") ?? Brushes.LightGray;
        var border = Converters.Brush("KubismBorderBrush") ?? Brushes.Gray;
        var muted = Converters.Brush("KubismMutedBrush") ?? Brushes.Gray;
        var accent = Converters.Brush("KubismAccentBrush") ?? Brushes.Teal;
        var text = this.TryFindResource("SystemControlForegroundBaseHighBrush", ActualThemeVariant, out var tv) && tv is IBrush tb ? tb : Brushes.Black;
        var byId = g.Nodes.ToDictionary(n => n.Id);
        var edgePen = new Pen(muted, 1.2);
        // Helm releases and the links that start from them have their own colour, so "managed by" reads at a glance.
        var helm = Converters.Brush("KubismHelmBrush") ?? Brushes.RoyalBlue;
        var helmPen = new Pen(helm, 1.6);
        var helmSoft = helm is ISolidColorBrush hs ? new SolidColorBrush(hs.Color, 0.12) : helm;

        foreach (var e in g.Edges)
        {
            var fromHelm = byId[e.From].Kind == Core.Helm.HelmReleaseInfo.Kind;
            var a = BoxOf(byId[e.From], g); var b = BoxOf(byId[e.To], g);
            var p1 = new Point(a.Center.X, a.Bottom); var p2 = new Point(b.Center.X, b.Top);
            var dy = Math.Max(GapY / 2, (p2.Y - p1.Y) / 2);
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(p1, false);
                c.CubicBezierTo(new Point(p1.X, p1.Y + dy), new Point(p2.X, p2.Y - dy), p2);
            }
            ctx.DrawGeometry(null, fromHelm ? helmPen : edgePen, geo);
            var arrow = new StreamGeometry();
            using (var c = arrow.Open())
            {
                c.BeginFigure(p2, true);
                c.LineTo(new Point(p2.X - 3.5, p2.Y - 7)); c.LineTo(new Point(p2.X + 3.5, p2.Y - 7));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(fromHelm ? helm : muted, null, arrow);
            // Label on the curve, only when the gap between levels is tall enough to read it.
            if (p2.Y - p1.Y >= GapY - 1)
            {
                var ft = new FormattedText(e.Label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 9.5, fromHelm ? helm : muted);
                var mid = new Point((p1.X + p2.X) / 2 + 4, (p1.Y + p2.Y) / 2 - ft.Height / 2);
                ctx.DrawRectangle(panel, null, new Rect(mid.X - 2, mid.Y, ft.Width + 4, ft.Height), 2);
                ctx.DrawText(ft, mid);
            }
        }

        foreach (var n in g.Nodes)
        {
            var r = BoxOf(n, g);
            var isHelm = n.Kind == Core.Helm.HelmReleaseInfo.Kind;
            ctx.DrawRectangle(isHelm ? helmSoft : n.IsRoot ? Converters.Brush("KubismAccentSoftBrush") : side,
                new Pen(isHelm ? helm : n.IsRoot ? accent : border, n.IsRoot || isHelm ? 2 : 1), r, 6);
            ctx.DrawRectangle(HealthBrush(n.Health), null, new Rect(r.X, r.Y + 6, 4, r.Height - 12), 2);
            var kind = new FormattedText(n.DisplayKind.ToUpperInvariant(), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 9, isHelm ? helm : muted) { MaxTextWidth = BoxW - 20, MaxLineCount = 1 };
            ctx.DrawText(kind, new Point(r.X + 12, r.Y + 6));
            var name = new FormattedText(n.Name, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, n.IsRoot ? FontWeight.Bold : FontWeight.SemiBold), 12, text)
            { MaxTextWidth = BoxW - 20, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            ctx.DrawText(name, new Point(r.X + 12, r.Y + 18));
            if (n.Status.Length > 0)
            {
                var st = new FormattedText(n.Status, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 9.5, HealthBrush(n.Health)) { MaxTextWidth = BoxW - 20, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                ctx.DrawText(st, new Point(r.Right - 8 - st.Width, r.Y + 6));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Graph is not { } g || NodeCommand is not { } cmd) return;
        var p = e.GetPosition(this);
        var hit = g.Nodes.FirstOrDefault(n => BoxOf(n, g).Contains(p));
        if (hit is not null && !hit.IsRoot && cmd.CanExecute(hit)) cmd.Execute(hit);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Graph is not { } g) return;
        var p = e.GetPosition(this);
        var hit = g.Nodes.FirstOrDefault(n => BoxOf(n, g).Contains(p));
        Cursor = hit is { IsRoot: false } ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        ToolTip.SetTip(this, hit is null ? null : $"{hit.Kind} {(hit.Namespace is null ? "" : hit.Namespace + "/")}{hit.Name}{(hit.Status.Length > 0 ? " · " + hit.Status : "")}");
    }
}
