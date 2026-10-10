using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Kubism.Core.Network;
using Kubism.Core.Security;

namespace Kubism.Desktop.Controls;

/// <summary>
/// Draws a <see cref="NetworkView"/> left to right: exposure, ingresses, services, workloads, from the layout computed in Core. Colour is the exposure level
/// (outside the cluster, in-cluster only, unknown), a stripe marks a problem, a dashed border an object that does not exist, and the ports are written on the edges.
/// Nodes of another namespace kept for context are dimmed. Click a node to run <see cref="NodeCommand"/>.
/// </summary>
public sealed class NetworkGraphControl : Control
{
    public static readonly StyledProperty<NetworkView?> ViewDataProperty = AvaloniaProperty.Register<NetworkGraphControl, NetworkView?>(nameof(ViewData));
    public static readonly StyledProperty<PlacedNode?> SelectedNodeProperty = AvaloniaProperty.Register<NetworkGraphControl, PlacedNode?>(nameof(SelectedNode));
    public static readonly StyledProperty<ICommand?> NodeCommandProperty = AvaloniaProperty.Register<NetworkGraphControl, ICommand?>(nameof(NodeCommand));

    public const double BoxW = 220, BoxH = 54, GapX = 96, GapY = 12, Pad = 14, HeaderH = 22;
    private static readonly Typeface Face = new("Inter, Segoe UI, Helvetica Neue, sans-serif");
    private static readonly string[] Headers = ["Exposure", "Ingresses", "Services", "Workloads"];

    static NetworkGraphControl()
    {
        AffectsRender<NetworkGraphControl>(ViewDataProperty, SelectedNodeProperty);
        AffectsMeasure<NetworkGraphControl>(ViewDataProperty);
    }

    public NetworkView? ViewData { get => GetValue(ViewDataProperty); set => SetValue(ViewDataProperty, value); }
    public PlacedNode? SelectedNode { get => GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public ICommand? NodeCommand { get => GetValue(NodeCommandProperty); set => SetValue(NodeCommandProperty, value); }

    public static Rect BoxOf(PlacedNode n) => new(Pad + n.Column * (BoxW + GapX), Pad + HeaderH + n.Row * (BoxH + GapY), BoxW, BoxH);

    public static Size SizeFor(NetworkView v) => v.Nodes.Count == 0 ? new Size(0, 0)
        : new Size(Pad * 2 + 4 * BoxW + 3 * GapX, Pad * 2 + HeaderH + v.MaxRows * BoxH + Math.Max(0, v.MaxRows - 1) * GapY);

    protected override Size MeasureOverride(Size availableSize) => ViewData is { } v ? SizeFor(v) : new Size(0, 0);

    private static IBrush B(string key, IBrush fallback) => Converters.Brush(key) ?? fallback;
    private static IBrush Soft(IBrush b, double a = 0.14) => b is ISolidColorBrush s ? new SolidColorBrush(s.Color, a) : b;

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    public override void Render(DrawingContext ctx)
    {
        if (ViewData is not { Nodes.Count: > 0 } view) return;
        var panel = B("KubismPanelBrush", Brushes.White);
        var muted = B("KubismMutedBrush", Brushes.Gray);
        var outside = B("KubismWarnBrush", Brushes.Orange);
        var inside = B("KubismAccentBrush", Brushes.Teal);
        var error = B("KubismErrorBrush", Brushes.Red);
        var text = this.TryFindResource("SystemControlForegroundBaseHighBrush", ActualThemeVariant, out var tv) && tv is IBrush tb ? tb : Brushes.Black;
        var byId = view.Nodes.ToDictionary(n => n.Id);
        var culture = System.Globalization.CultureInfo.CurrentUICulture;

        for (var c = 0; c < 4; c++)
            ctx.DrawText(new FormattedText(Headers[c].ToUpperInvariant(), culture, FlowDirection.LeftToRight, Face, 10, muted), new Point(Pad + c * (BoxW + GapX), Pad - 2));

        IBrush LevelBrush(ExposureLevel l) => l switch { ExposureLevel.Outside => outside, ExposureLevel.Unknown => muted, _ => inside };

        foreach (var e in view.Edges)
        {
            if (!byId.TryGetValue(e.From, out var from) || !byId.TryGetValue(e.To, out var to)) continue;
            var a = BoxOf(from); var b = BoxOf(to);
            var p1 = new Point(a.Right, a.Center.Y); var p2 = new Point(b.X, b.Center.Y);
            var dx = Math.Max(GapX / 2, (p2.X - p1.X) / 2);
            var geo = new StreamGeometry();
            using (var g = geo.Open()) { g.BeginFigure(p1, false); g.CubicBezierTo(new Point(p1.X + dx, p1.Y), new Point(p2.X - dx, p2.Y), p2); }
            var selected = SelectedNode is { } sel && (sel.Id == e.From || sel.Id == e.To);
            var brush = e.Problem ? error : selected ? text : LevelBrush(to.Node.Exposure);
            ctx.DrawGeometry(null, new Pen(brush, e.Problem ? 2.4 : selected ? 1.8 : 1.2), geo);
            var arrow = new StreamGeometry();
            using (var g = arrow.Open()) { g.BeginFigure(p2, true); g.LineTo(new Point(p2.X - 7, p2.Y - 3.5)); g.LineTo(new Point(p2.X - 7, p2.Y + 3.5)); g.EndFigure(true); }
            ctx.DrawGeometry(brush, null, arrow);
            if (e.Label.Length > 0)
            {
                var ft = new FormattedText(Trim(e.Label, 44), culture, FlowDirection.LeftToRight, Face, 9, brush) { MaxTextWidth = GapX + 60, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
                var mid = new Point((p1.X + p2.X) / 2 - ft.Width / 2, (p1.Y + p2.Y) / 2 - ft.Height - 3);
                ctx.DrawRectangle(panel, null, new Rect(mid.X - 2, mid.Y, ft.Width + 4, ft.Height), 2);
                ctx.DrawText(ft, mid);
            }
        }

        foreach (var n in view.Nodes)
        {
            var r = BoxOf(n);
            var node = n.Node;
            var level = LevelBrush(node.Exposure);
            var isSel = SelectedNode?.Id == n.Id;
            using (ctx.PushOpacity(n.IsContext ? 0.55 : 1))
            {
                var pen = new Pen(node.Missing ? error : isSel ? text : level, isSel ? 2.4 : 1.4) { DashStyle = node.Missing ? DashStyle.Dash : null };
                ctx.DrawRectangle(Soft(level), pen, r, 6);
                if (node.HasProblem) ctx.DrawRectangle(node.Worst == FindingSeverity.High ? error : outside, null, new Rect(r.X, r.Y + 5, 4, r.Height - 10), 2);
                var ns = node.Namespace is null ? (node.Exposure == ExposureLevel.Outside ? "outside the cluster" : "no controller found") : node.Namespace + (n.IsContext ? " (context)" : "");
                var kind = new FormattedText($"{node.KindText} · {ns}".ToUpperInvariant(), culture, FlowDirection.LeftToRight, Face, 8.5, muted) { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                ctx.DrawText(kind, new Point(r.X + 10, r.Y + 5));
                var name = new FormattedText(node.Missing ? $"{node.Label} (missing)" : node.Label, culture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, FontWeight.SemiBold), 12, text)
                { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                ctx.DrawText(name, new Point(r.X + 10, r.Y + 19));
                var detail = new FormattedText(node.Detail, culture, FlowDirection.LeftToRight, Face, 9.5, muted) { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                ctx.DrawText(detail, new Point(r.X + 10, r.Y + 36));
            }
        }
    }

    private PlacedNode? HitTest(Point p) => ViewData?.Nodes.FirstOrDefault(n => BoxOf(n).Contains(p));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HitTest(e.GetPosition(this)) is { } hit && NodeCommand is { } cmd && cmd.CanExecute(hit)) cmd.Execute(hit);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        Cursor = hit is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        ToolTip.SetTip(this, hit is null ? null : Tip(hit.Node));
    }

    private static string Tip(NetworkNode n)
    {
        var lines = new List<string> { $"{n.KindText}: {n.Label}{(n.Namespace is null ? "" : " (" + n.Namespace + ")")}", n.Detail };
        lines.AddRange(n.Lines.Take(10));
        lines.AddRange(n.Problems.Select(p => "⚠ " + p.Description));
        return string.Join("\n", lines.Where(l => l.Length > 0));
    }
}
