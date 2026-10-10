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
/// (outside the cluster, in-cluster only, unknown), a stripe marks a problem, a dashed border an object that does not exist. An Ingress box holds what it
/// matches on the way in (host and paths); the arrows carry nothing but a small rectangle with the ports at the entry of the box they end on (the
/// controller's ports into an Ingress, the Service ports into a Service, the container ports into a workload). Nodes of another namespace kept for context
/// are dimmed. Click a node to run <see cref="NodeCommand"/>.
/// </summary>
public sealed class NetworkGraphControl : Control
{
    public static readonly StyledProperty<NetworkView?> ViewDataProperty = AvaloniaProperty.Register<NetworkGraphControl, NetworkView?>(nameof(ViewData));
    public static readonly StyledProperty<PlacedNode?> SelectedNodeProperty = AvaloniaProperty.Register<NetworkGraphControl, PlacedNode?>(nameof(SelectedNode));
    public static readonly StyledProperty<ICommand?> NodeCommandProperty = AvaloniaProperty.Register<NetworkGraphControl, ICommand?>(nameof(NodeCommand));

    public const double BoxW = 250, BoxH = 60, GapX = 100, GapY = 12, Pad = 14, HeaderH = 22, LineH = 13, MaxFilterLines = 6, BadgeH = 16;
    private static readonly Typeface Face = new("Inter, Segoe UI, Helvetica Neue, sans-serif");
    private static readonly string[] Headers = ["Exposure", "Ingresses (what they match)", "Services", "Workloads"];

    private NetworkView? _layoutFor;
    private double[] _tops = [];
    private Dictionary<string, Rect> _boxes = [];
    private Dictionary<string, (string Ok, string Bad, string Tip)> _badges = [];

    static NetworkGraphControl()
    {
        AffectsRender<NetworkGraphControl>(ViewDataProperty, SelectedNodeProperty);
        AffectsMeasure<NetworkGraphControl>(ViewDataProperty);
    }

    public NetworkView? ViewData { get => GetValue(ViewDataProperty); set => SetValue(ViewDataProperty, value); }
    public PlacedNode? SelectedNode { get => GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public ICommand? NodeCommand { get => GetValue(NodeCommandProperty); set => SetValue(NodeCommandProperty, value); }

    /// <summary>An Ingress box grows with the filters it lists; the others have one height. A row is as tall as its tallest box, so the columns stay aligned.</summary>
    public static double HeightOf(NetworkNode n)
    {
        if (n.Kind != NetworkNodeKind.Ingress) return BoxH;
        var lines = Math.Min(n.FilterLines.Count, (int)MaxFilterLines) + (n.FilterLines.Count > MaxFilterLines ? 1 : 0);
        return Math.Max(BoxH, 36 + Math.Max(1, lines) * LineH + 6);
    }

    private void EnsureLayout()
    {
        if (ReferenceEquals(_layoutFor, ViewData)) return;
        _layoutFor = ViewData;
        _boxes = []; _badges = []; _tops = [];
        if (ViewData is not { Nodes.Count: > 0 } v) return;
        var rowH = new double[v.MaxRows];
        foreach (var n in v.Nodes) rowH[n.Row] = Math.Max(rowH[n.Row], HeightOf(n.Node));
        _tops = new double[v.MaxRows];
        var y = Pad + HeaderH;
        for (var r = 0; r < v.MaxRows; r++) { _tops[r] = y; y += rowH[r] + GapY; }
        foreach (var n in v.Nodes) _boxes[n.Id] = new Rect(Pad + n.Column * (BoxW + GapX), _tops[n.Row], BoxW, HeightOf(n.Node));
        // The ports at the entry of each box: every arrow ending there, merged.
        var byId = v.Nodes.ToDictionary(n => n.Id);
        foreach (var grp in v.Edges.Where(e => e.Ports.Count > 0 && byId.ContainsKey(e.To)).GroupBy(e => e.To))
        {
            // Ports that line up and ports that do not (a missing Service port, an unresolved target port) are separate rectangles: one wrong port must not tint the good ones.
            var good = grp.Where(e => !e.Problem).SelectMany(e => e.Ports).Distinct().ToList();
            var bad = grp.Where(e => e.Problem).SelectMany(e => e.Ports).Distinct().Where(p => !good.Contains(p)).ToList();
            var tip = $"Entry ports of {byId[grp.Key].Node.Label}:\n" + string.Join("\n", grp.Where(e => e.Label.Length > 0).Select(e => $"• {byId.GetValueOrDefault(e.From)?.Node.Label}: {e.Label}{(e.Problem ? "  ⚠" : "")}"));
            _badges[grp.Key] = (string.Join(", ", good), string.Join(", ", bad), tip);
        }
    }

    public Rect BoxOf(PlacedNode n) { EnsureLayout(); return _boxes.TryGetValue(n.Id, out var r) ? r : default; }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureLayout();
        if (ViewData is not { Nodes.Count: > 0 } v) return new Size(0, 0);
        var bottom = v.Nodes.Max(n => _boxes[n.Id].Bottom);
        return new Size(Pad * 2 + 4 * BoxW + 3 * GapX, bottom + Pad);
    }

    private static IBrush B(string key, IBrush fallback) => Converters.Brush(key) ?? fallback;
    private static IBrush Soft(IBrush b, double a = 0.14) => b is ISolidColorBrush s ? new SolidColorBrush(s.Color, a) : b;

    public override void Render(DrawingContext ctx)
    {
        EnsureLayout();
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

        // arrows: no text on them, the ports are in the small rectangles at the entry of each box
        foreach (var e in view.Edges)
        {
            if (!byId.TryGetValue(e.From, out var from) || !byId.TryGetValue(e.To, out var to)) continue;
            var a = _boxes[from.Id]; var b = _boxes[to.Id];
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
        }

        foreach (var n in view.Nodes)
        {
            var r = _boxes[n.Id];
            var node = n.Node;
            var level = LevelBrush(node.Exposure);
            var isSel = SelectedNode?.Id == n.Id;
            using (ctx.PushOpacity(n.IsContext ? 0.55 : 1))
            {
                var pen = new Pen(node.Missing ? error : isSel ? text : level, isSel ? 2.4 : 1.4) { DashStyle = node.Missing ? DashStyle.Dash : null };
                ctx.DrawRectangle(Soft(level), pen, r, 6);
                if (node.HasProblem) ctx.DrawRectangle(node.Worst == FindingSeverity.High ? error : outside, null, new Rect(r.X, r.Y + 5, 4, r.Height - 10), 2);
                var ns = node.Namespace is null ? (node.Exposure == ExposureLevel.Outside ? "outside the cluster" : "no controller found") : node.Namespace + (n.IsContext ? " (context)" : "");
                ctx.DrawText(new FormattedText($"{node.KindText} · {ns}".ToUpperInvariant(), culture, FlowDirection.LeftToRight, Face, 8.5, muted) { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, new Point(r.X + 10, r.Y + 5));
                ctx.DrawText(new FormattedText(node.Missing ? $"{node.Label} (missing)" : node.Label, culture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, FontWeight.SemiBold), 12, text)
                { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, new Point(r.X + 10, r.Y + 19));

                if (node.Kind == NetworkNodeKind.Ingress)
                {
                    // What this Ingress matches on the way in: the host in the text colour, its paths in muted.
                    var y = r.Y + 36;
                    foreach (var line in node.FilterLines.Take((int)MaxFilterLines))
                    {
                        var ft = new FormattedText(line, culture, FlowDirection.LeftToRight, Face, 10, muted) { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                        var colon = line.IndexOf(':');
                        ft.SetForegroundBrush(text, 0, colon >= 0 ? colon : line.Length);
                        ctx.DrawText(ft, new Point(r.X + 10, y));
                        y += LineH;
                    }
                    if (node.FilterLines.Count > MaxFilterLines)
                        ctx.DrawText(new FormattedText($"+{node.FilterLines.Count - (int)MaxFilterLines} more (see the details)", culture, FlowDirection.LeftToRight, Face, 9, muted), new Point(r.X + 10, y));
                }
                else
                    ctx.DrawText(new FormattedText(node.Detail, culture, FlowDirection.LeftToRight, Face, 9.5, muted) { MaxTextWidth = BoxW - 16, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis }, new Point(r.X + 10, r.Y + 36));
            }
        }

        // the ports at the entry of each box, on the arrow just before it
        foreach (var (id, badge) in _badges)
        {
            if (!_boxes.TryGetValue(id, out var box)) continue;
            foreach (var (rect, label, problem) in BadgesOf(box, badge))
            {
                var ft = new FormattedText(label, culture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, FontWeight.SemiBold), 9.5, problem ? error : text) { MaxTextWidth = GapX - 26, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                var brush = problem ? error : LevelBrush(byId[id].Node.Exposure);
                // the rectangle was sized for an estimate of the text; fit it to the real width
                var fitted = new Rect(box.X - 8 - (Math.Min(ft.Width, GapX - 26) + 10), rect.Y, Math.Min(ft.Width, GapX - 26) + 10, rect.Height);
                ctx.DrawRectangle(panel, new Pen(brush, problem ? 1.8 : 1.2), fitted, 4);
                ctx.DrawText(ft, new Point(fitted.X + 5, fitted.Y + (fitted.Height - ft.Height) / 2));
            }
        }
    }

    /// <summary>The good ports' rectangle, and below it the wrong ports' one when there are both; each sits on the arrow just before the box.</summary>
    private static IEnumerable<(Rect Rect, string Text, bool Problem)> BadgesOf(Rect box, (string Ok, string Bad, string Tip) badge)
    {
        var both = badge.Ok.Length > 0 && badge.Bad.Length > 0;
        var y = box.Center.Y - BadgeH / 2 - (both ? (BadgeH + 2) / 2 : 0);
        if (badge.Ok.Length > 0) { yield return (BadgeRect(box, y, badge.Ok), badge.Ok, false); y += BadgeH + 2; }
        if (badge.Bad.Length > 0) yield return (BadgeRect(box, y, badge.Bad), badge.Bad, true);
    }

    private static Rect BadgeRect(Rect box, double y, string text)
    {
        var w = Math.Min(text.Length * 6.0, GapX - 26) + 10;
        return new Rect(box.X - 8 - w, y, w, BadgeH);
    }

    private PlacedNode? HitTest(Point p) { EnsureLayout(); return ViewData?.Nodes.FirstOrDefault(n => _boxes[n.Id].Contains(p)); }

    private string? BadgeTipAt(Point p)
    {
        EnsureLayout();
        foreach (var (id, badge) in _badges)
        {
            if (!_boxes.TryGetValue(id, out var box)) continue;
            // generous: the rectangle widened a little so a small target is easy to hit
            if (BadgesOf(box, badge).Any(b => b.Rect.Inflate(3).Contains(p))) return badge.Tip;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HitTest(e.GetPosition(this)) is { } hit && NodeCommand is { } cmd && cmd.CanExecute(hit)) cmd.Execute(hit);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        Cursor = hit is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        ToolTip.SetTip(this, BadgeTipAt(p) ?? (hit is null ? null : Tip(hit.Node)));
    }

    private static string Tip(NetworkNode n)
    {
        var lines = new List<string> { $"{n.KindText}: {n.Label}{(n.Namespace is null ? "" : " (" + n.Namespace + ")")}", n.Detail };
        lines.AddRange(n.Lines.Take(10));
        lines.AddRange(n.Problems.Select(p => "⚠ " + p.Description));
        return string.Join("\n", lines.Where(l => l.Length > 0));
    }
}
