using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Kubism.Desktop.Services;
using Kubism.Desktop.Terminal;

namespace Kubism.Desktop.Controls;

/// <summary>Renders a <see cref="TerminalBuffer"/> and turns keys/paste into terminal input bytes. Behind ITerminalView so the engine can be swapped.</summary>
public interface ITerminalView
{
    event Func<string, Task>? InputReceived;
    event Func<int, int, Task>? TerminalSizeChanged;
    void Invalidate();
}

public sealed class TerminalControl : Control, ITerminalView
{
    private static readonly Typeface Mono = new("Menlo,Consolas,DejaVu Sans Mono,monospace");
    private const double FontSize = 13;
    private double _cw = 8, _ch = 16;
    private TerminalBuffer? _buffer;
    private int _cols, _rows;
    private bool _cursorOn = true;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private (int row, int col)? _selStart, _selEnd;

    public event Func<string, Task>? InputReceived;
    public event Func<int, int, Task>? TerminalSizeChanged;
    public Func<string, Task>? CopyRequested { get; set; }
    public Func<Task<string?>>? PasteRequested { get; set; }

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _blink.Tick += (_, _) => { _cursorOn = !_cursorOn; InvalidateVisual(); };
        MeasureCell();
    }

    public static readonly StyledProperty<TerminalBuffer?> BufferProperty = AvaloniaProperty.Register<TerminalControl, TerminalBuffer?>(nameof(Buffer));
    static TerminalControl() => AffectsRender<TerminalControl>(BufferProperty);
    public TerminalBuffer? Buffer { get => GetValue(BufferProperty); set => SetValue(BufferProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BufferProperty) { _buffer = change.NewValue as TerminalBuffer; CheckSize(); }
    }

    public void Invalidate() { InvalidateVisual(); }

    private void MeasureCell()
    {
        var ft = new FormattedText("W", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, Brushes.White);
        _cw = Math.Max(1, ft.Width); _ch = Math.Max(1, Math.Ceiling(ft.Height));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _blink.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _blink.Stop(); base.OnDetachedFromVisualTree(e); }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = base.ArrangeOverride(finalSize);
        CheckSize(finalSize);
        return s;
    }

    private void CheckSize(Size? size = null)
    {
        var sz = size ?? Bounds.Size;   // Bounds is updated only after arrange, so use the size being arranged
        if (sz.Width <= 0 || sz.Height <= 0 || _buffer is null) return;
        var cols = Math.Max(10, (int)(sz.Width / _cw)); var rows = Math.Max(3, (int)(sz.Height / _ch));
        if (cols == _cols && rows == _rows) return;
        _cols = cols; _rows = rows;
        _buffer.Resize(cols, rows);
        TerminalSizeChanged?.Invoke(cols, rows);
    }

    public override void Render(DrawingContext ctx)
    {
        var bg = Converters.Brush("KubismCatalogBrush");
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var back = new SolidColorBrush(dark ? Color.Parse("#0F1117") : Color.Parse("#1E1E1E"));
        ctx.DrawRectangle(back, null, new Rect(Bounds.Size));
        var b = _buffer;
        if (b is null) return;
        var defaultFg = Color.Parse("#D4D4D4");
        for (var y = 0; y < b.Rows; y++)
        {
            for (var x = 0; x < b.Cols; x++)
            {
                var cell = b.Get(y, x);
                var inv = (cell.Attr & CellAttr.Inverse) != 0;
                var fg = cell.Fg >= 0 ? ToColor(cell.Fg) : defaultFg;
                Color? bgc = cell.Bg >= 0 ? ToColor(cell.Bg) : null;
                if (inv) { var t = bgc ?? Color.Parse("#1E1E1E"); bgc = fg; fg = t; }
                var selected = IsSelected(y, x);
                if (selected) bgc = Color.Parse("#264F78");
                if (bgc is { } bc) ctx.DrawRectangle(new SolidColorBrush(bc), null, new Rect(x * _cw, y * _ch, _cw + 0.5, _ch));
                if (cell.Char != ' ')
                {
                    var weight = (cell.Attr & CellAttr.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal;
                    var ft = new FormattedText(cell.Char.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Mono.FontFamily, FontStyle.Normal, weight), FontSize, new SolidColorBrush(fg));
                    ctx.DrawText(ft, new Point(x * _cw, y * _ch));
                }
                if ((cell.Attr & CellAttr.Underline) != 0) ctx.DrawLine(new Pen(new SolidColorBrush(fg), 1), new Point(x * _cw, (y + 1) * _ch - 1), new Point((x + 1) * _cw, (y + 1) * _ch - 1));
            }
        }
        if (b.CursorVisible && (_cursorOn || !IsFocused))
        {
            var rect = new Rect(b.CursorX * _cw, b.CursorY * _ch, _cw, _ch);
            if (IsFocused) ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#AEAFAD"), 0.8), null, rect);
            else ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#AEAFAD")), 1), rect);
        }
    }

    private static Color ToColor(int c) => Color.FromUInt32(TerminalBuffer.ToArgb(c));

    private static long Pos((int row, int col) p) => p.row * 100000L + p.col;

    private bool IsSelected(int y, int x)
    {
        if (_selStart is not { } a || _selEnd is not { } z) return false;
        var (s, e) = Pos(a) <= Pos(z) ? (a, z) : (z, a);
        var p = y * 100000L + x;
        return p >= Pos(s) && p <= Pos(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { ShowMenu(); e.Handled = true; return; }
        _selStart = _selEnd = ((int)(p.Y / _ch), (int)(p.X / _cw));
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selStart is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _selEnd = (Math.Clamp((int)(p.Y / _ch), 0, (_buffer?.Rows ?? 1) - 1), Math.Clamp((int)(p.X / _cw), 0, (_buffer?.Cols ?? 1) - 1));
        InvalidateVisual();
    }

    private void ShowMenu()
    {
        var copy = new MenuItem { Header = "Copy".T(), IsEnabled = SelectedText().Length > 0 };
        copy.Click += async (_, _) => await CopySelectionAsync();
        var paste = new MenuItem { Header = "Paste".T() };
        paste.Click += async (_, _) => await PasteAsync();
        var all = new MenuItem { Header = "Select all".T() };
        all.Click += (_, _) => { if (_buffer is { } b) { _selStart = (0, 0); _selEnd = (b.Rows - 1, b.Cols - 1); InvalidateVisual(); } };
        new ContextMenu { ItemsSource = new Control[] { copy, paste, new Separator(), all } }.Open(this);
    }

    private async Task CopySelectionAsync()
    {
        var t = SelectedText();
        if (t.Length > 0 && CopyRequested is not null) await CopyRequested(t);
    }

    private async Task PasteAsync()
    {
        if (PasteRequested is not null && await PasteRequested() is { } text)
            await Send(_buffer?.BracketedPaste == true ? $"\u001b[200~{text}\u001b[201~" : text);
    }

    public string SelectedText()
    {
        if (_selStart is not { } a || _selEnd is not { } z || _buffer is null) return "";
        var (s, e) = Pos(a) <= Pos(z) ? (a, z) : (z, a);
        var sb = new System.Text.StringBuilder();
        for (var y = s.row; y <= e.row && y < _buffer.Rows; y++)
        {
            var line = _buffer.LineText(y);
            var from = y == s.row ? Math.Min(s.col, line.Length) : 0;
            var to = y == e.row ? Math.Min(e.col + 1, line.Length) : line.Length;
            sb.Append(line[from..Math.Max(from, to)]);
            if (y < e.row) sb.Append('\n');
        }
        return sb.ToString();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var mods = e.KeyModifiers;
        var cmd = mods.HasFlag(KeyModifiers.Meta) || (mods.HasFlag(KeyModifiers.Control) && mods.HasFlag(KeyModifiers.Shift));
        if ((cmd && e.Key == Key.C) || (mods == KeyModifiers.Control && e.Key == Key.Insert)) { await CopySelectionAsync(); e.Handled = true; return; }
        if ((cmd && e.Key == Key.V) || (mods == KeyModifiers.Shift && e.Key == Key.Insert)) { await PasteAsync(); e.Handled = true; return; }
        var app = _buffer?.ApplicationCursorKeys == true;
        string? seq = e.Key switch
        {
            Key.Enter => "\r", Key.Back => "\u007f", Key.Tab => mods.HasFlag(KeyModifiers.Shift) ? "\u001b[Z" : "\t", Key.Escape => "\u001b",
            Key.Up => app ? "\u001bOA" : "\u001b[A", Key.Down => app ? "\u001bOB" : "\u001b[B", Key.Right => app ? "\u001bOC" : "\u001b[C", Key.Left => app ? "\u001bOD" : "\u001b[D",
            Key.Home => "\u001b[H", Key.End => "\u001b[F", Key.PageUp => "\u001b[5~", Key.PageDown => "\u001b[6~", Key.Delete => "\u001b[3~", Key.Insert => "\u001b[2~",
            Key.F1 => "\u001bOP", Key.F2 => "\u001bOQ", Key.F3 => "\u001bOR", Key.F4 => "\u001bOS", Key.F5 => "\u001b[15~", Key.F6 => "\u001b[17~", Key.F7 => "\u001b[18~",
            Key.F8 => "\u001b[19~", Key.F9 => "\u001b[20~", Key.F10 => "\u001b[21~", Key.F11 => "\u001b[23~", Key.F12 => "\u001b[24~",
            _ => null,
        };
        if (mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Shift) && e.Key is >= Key.A and <= Key.Z)
            seq = ((char)(e.Key - Key.A + 1)).ToString();
        if (seq is null) return;
        _selStart = _selEnd = null;
        await Send(seq);
        e.Handled = true;
    }

    protected override async void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text)) { _selStart = _selEnd = null; await Send(e.Text); e.Handled = true; }
    }

    private Task Send(string s) => InputReceived?.Invoke(s) ?? Task.CompletedTask;
}
