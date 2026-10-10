using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Kubism.Core.Security;

namespace Kubism.Desktop.Controls;

/// <summary>
/// Draws an <see cref="RbacChain"/> left to right: one column each for subjects, bindings, roles and rules, from the layout computed in Core.
/// Colour shows the scope (namespace or cluster), a red stripe a risk, a dashed red border a role that does not exist, and the
/// RoleBinding → ClusterRole edge is drawn thick and labelled. Click a node to run <see cref="NodeCommand"/>.
/// </summary>
public sealed class RbacGraphControl : Control
{
    public static readonly StyledProperty<RbacChain?> ChainProperty = AvaloniaProperty.Register<RbacGraphControl, RbacChain?>(nameof(Chain));
    public static readonly StyledProperty<RbacChainNode?> SelectedNodeProperty = AvaloniaProperty.Register<RbacGraphControl, RbacChainNode?>(nameof(SelectedNode));
    public static readonly StyledProperty<ICommand?> NodeCommandProperty = AvaloniaProperty.Register<RbacGraphControl, ICommand?>(nameof(NodeCommand));

    public const double BoxW = 190, BoxH = 46, GapX = 74, GapY = 12, Pad = 14, HeaderH = 22;
    private static readonly Typeface Face = new("Inter, Segoe UI, Helvetica Neue, sans-serif");
    private static readonly string[] Headers = ["Subjects", "Bindings", "Roles", "Rules"];

    static RbacGraphControl()
    {
        AffectsRender<RbacGraphControl>(ChainProperty, SelectedNodeProperty);
        AffectsMeasure<RbacGraphControl>(ChainProperty);
    }

    public RbacChain? Chain { get => GetValue(ChainProperty); set => SetValue(ChainProperty, value); }
    public RbacChainNode? SelectedNode { get => GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public ICommand? NodeCommand { get => GetValue(NodeCommandProperty); set => SetValue(NodeCommandProperty, value); }

    public static Rect BoxOf(RbacChainNode n) => new(Pad + n.Column * (BoxW + GapX), Pad + HeaderH + n.Row * (BoxH + GapY), BoxW, BoxH);

    public static Size SizeFor(RbacChain c) => c.Nodes.Count == 0 ? new Size(0, 0)
        : new Size(Pad * 2 + 4 * BoxW + 3 * GapX, Pad * 2 + HeaderH + c.MaxRows * BoxH + Math.Max(0, c.MaxRows - 1) * GapY);

    protected override Size MeasureOverride(Size availableSize) => Chain is { } c ? SizeFor(c) : new Size(0, 0);

    private static IBrush B(string key, IBrush fallback) => Converters.Brush(key) ?? fallback;
    private static IBrush Soft(IBrush b) => b is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.14) : b;

    public override void Render(DrawingContext ctx)
    {
        if (Chain is not { Nodes.Count: > 0 } chain) return;
        var panel = B("KubismPanelBrush", Brushes.White);
        var border = B("KubismBorderBrush", Brushes.Gray);
        var muted = B("KubismMutedBrush", Brushes.Gray);
        var ns = B("KubismAccentBrush", Brushes.Teal);
        var cluster = B("KubismHelmBrush", Brushes.RoyalBlue);
        var error = B("KubismErrorBrush", Brushes.Red);
        var warn = B("KubismWarnBrush", Brushes.Orange);
        var text = this.TryFindResource("SystemControlForegroundBaseHighBrush", ActualThemeVariant, out var tv) && tv is IBrush tb ? tb : Brushes.Black;
        var byId = chain.Nodes.ToDictionary(n => n.Id);
        var culture = System.Globalization.CultureInfo.CurrentUICulture;

        for (var c = 0; c < 4; c++)
            ctx.DrawText(new FormattedText(Headers[c].ToUpperInvariant(), culture, FlowDirection.LeftToRight, Face, 10, muted), new Point(Pad + c * (BoxW + GapX), Pad - 2));

        foreach (var e in chain.Edges)
        {
            if (!byId.TryGetValue(e.From, out var from) || !byId.TryGetValue(e.To, out var to)) continue;
            var a = BoxOf(from); var b = BoxOf(to);
            var p1 = new Point(a.Right, a.Center.Y); var p2 = new Point(b.X, b.Center.Y);
            var dx = Math.Max(GapX / 2, (p2.X - p1.X) / 2);
            var geo = new StreamGeometry();
            using (var g = geo.Open()) { g.BeginFigure(p1, false); g.CubicBezierTo(new Point(p1.X + dx, p1.Y), new Point(p2.X - dx, p2.Y), p2); }
            var selected = SelectedNode is { } sel && (sel.Id == e.From || sel.Id == e.To);
            var brush = e.Highlight ? (to.Missing ? error : cluster) : selected ? ns : muted;
            ctx.DrawGeometry(null, new Pen(brush, e.Highlight ? 2.4 : selected ? 1.8 : 1.1), geo);
            var arrow = new StreamGeometry();
            using (var g = arrow.Open()) { g.BeginFigure(p2, true); g.LineTo(new Point(p2.X - 7, p2.Y - 3.5)); g.LineTo(new Point(p2.X - 7, p2.Y + 3.5)); g.EndFigure(true); }
            ctx.DrawGeometry(brush, null, arrow);
            if (e.Label.Length > 0)
            {
                var ft = new FormattedText(e.Label, culture, FlowDirection.LeftToRight, Face, 9, brush) { MaxTextWidth = GapX + 40, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
                var mid = new Point((p1.X + p2.X) / 2 - ft.Width / 2, (p1.Y + p2.Y) / 2 - ft.Height - 3);
                ctx.DrawRectangle(panel, null, new Rect(mid.X - 2, mid.Y, ft.Width + 4, ft.Height), 2);
                ctx.DrawText(ft, mid);
            }
        }

        foreach (var n in chain.Nodes)
        {
            var r = BoxOf(n);
            var scope = n.Scope is null ? cluster : ns;
            var isSel = SelectedNode?.Id == n.Id;
            var pen = new Pen(n.Missing ? error : isSel ? text : scope, isSel ? 2.4 : 1.4) { DashStyle = n.Missing ? DashStyle.Dash : null };
            ctx.DrawRectangle(Soft(scope), pen, r, 6);
            if (n.Risky) ctx.DrawRectangle(n.Risks.Any(x => x.Severity == FindingSeverity.High) ? error : warn, null, new Rect(r.X, r.Y + 5, 4, r.Height - 10), 2);
            var kind = new FormattedText($"{n.Detail} · {n.ScopeText}".ToUpperInvariant(), culture, FlowDirection.LeftToRight, Face, 8.5, muted) { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            ctx.DrawText(kind, new Point(r.X + 10, r.Y + 6));
            var name = new FormattedText(n.Missing && n.Kind == RbacNodeKind.Role ? $"{n.Label} (missing)" : n.Label, culture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, FontWeight.SemiBold), 12, text)
            { MaxTextWidth = BoxW - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            ctx.DrawText(name, new Point(r.X + 10, r.Y + 21));
        }
    }

    private RbacChainNode? HitTest(Point p) => Chain?.Nodes.FirstOrDefault(n => BoxOf(n).Contains(p));

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
        ToolTip.SetTip(this, hit is null ? null : Tip(hit));
    }

    private static string Tip(RbacChainNode n)
    {
        var lines = new List<string> { $"{n.Detail}: {n.Label} ({n.ScopeText})" };
        if (n.Kind == RbacNodeKind.Rules) lines.AddRange(n.Rules.Take(12).Select(r => "• " + r.Summary));
        lines.AddRange(n.Risks.Select(r => "⚠ " + r.Description));
        return string.Join("\n", lines);
    }
}
