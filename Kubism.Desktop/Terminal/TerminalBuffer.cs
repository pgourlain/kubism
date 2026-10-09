using System.Text;

namespace Kubism.Desktop.Terminal;

[Flags]
public enum CellAttr : byte { None = 0, Bold = 1, Underline = 2, Inverse = 4, Dim = 8, Italic = 16 }

public readonly record struct TermCell(char Char, int Fg, int Bg, CellAttr Attr)
{
    public static readonly TermCell Blank = new(' ', -1, -1, CellAttr.None);
}

/// <summary>
/// Minimal xterm-compatible screen model: cursor movement, erase, scroll regions, SGR (16/256/true color), alternate screen, UTF-8.
/// Enough for shells, top, htop-style apps and vim. Pure logic: unit-tested without UI.
/// </summary>
public sealed class TerminalBuffer
{
    private TermCell[][] _screen;
    private TermCell[][]? _saved; // main screen while the alternate one is active
    private readonly List<TermCell[]> _scrollback = [];
    private int _top, _bottom;
    private int _fg = -1, _bg = -1;
    private CellAttr _attr;
    private int _savedX, _savedY;
    private bool _wrapPending;
    private readonly StringBuilder _esc = new();
    private enum Mode { Text, Esc, Csi, Osc, Charset }
    private Mode _mode;
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

    public TerminalBuffer(int cols = 80, int rows = 24)
    {
        Cols = cols; Rows = rows;
        _screen = NewScreen(rows, cols);
        _top = 0; _bottom = rows - 1;
    }

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AlternateScreen => _saved is not null;
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public string Title { get; private set; } = "";
    public int ScrollbackCount => _scrollback.Count;
    public int MaxScrollback { get; set; } = 5000;
    public int Version { get; private set; }
    /// <summary>Responses the terminal must send back to the application (cursor position reports…).</summary>
    public event Action<string>? Response;

    private static TermCell[][] NewScreen(int rows, int cols)
    {
        var s = new TermCell[rows][];
        for (var i = 0; i < rows; i++) s[i] = NewRow(cols);
        return s;
    }

    private static TermCell[] NewRow(int cols) { var r = new TermCell[cols]; Array.Fill(r, TermCell.Blank); return r; }

    public TermCell Get(int row, int col) => row >= 0 && row < Rows && col >= 0 && col < Cols ? _screen[row][col] : TermCell.Blank;
    public TermCell[] ScrollbackLine(int i) => _scrollback[i];

    public string LineText(int row) => new string(_screen[row].Select(c => c.Char).ToArray()).TrimEnd();
    public string ScreenText() => string.Join("\n", Enumerable.Range(0, Rows).Select(LineText)).TrimEnd('\n');

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols); rows = Math.Max(2, rows);
        if (cols == Cols && rows == Rows) return;
        var ns = NewScreen(rows, cols);
        // Keep the bottom-most content when shrinking vertically.
        var shift = Math.Max(0, CursorY - rows + 1);
        for (var y = 0; y < rows; y++)
        {
            var sy = y + shift;
            if (sy >= Rows) break;
            Array.Copy(_screen[sy], ns[y], Math.Min(cols, Cols));
        }
        for (var i = 0; i < shift; i++) _scrollback.Add(_screen[i]);
        _screen = ns; Cols = cols; Rows = rows;
        _top = 0; _bottom = rows - 1;
        CursorY = Math.Max(0, CursorY - shift); CursorX = Math.Min(CursorX, cols - 1);
        _wrapPending = false;
        Version++;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length + 4];
        var n = _utf8.GetChars(data, chars, false);
        Write(chars.AsSpan(0, n));
    }

    public void Write(string text) => Write(text.AsSpan());

    public void Write(ReadOnlySpan<char> text)
    {
        foreach (var ch in text) Feed(ch);
        Version++;
    }

    private void Feed(char c)
    {
        switch (_mode)
        {
            case Mode.Text: FeedText(c); break;
            case Mode.Esc: FeedEsc(c); break;
            case Mode.Csi:
                _esc.Append(c);
                if (c >= '@' && c <= '~') { var seq = _esc.ToString(); _esc.Clear(); _mode = Mode.Text; HandleCsi(seq); }
                else if (_esc.Length > 64) { _esc.Clear(); _mode = Mode.Text; }
                break;
            case Mode.Osc:
                if (c == '\a' || c == '\\' && _esc.Length > 0 && _esc[^1] == '\u001b') { HandleOsc(_esc.ToString().TrimEnd('\u001b')); _esc.Clear(); _mode = Mode.Text; }
                else _esc.Append(c);
                if (_esc.Length > 512) { _esc.Clear(); _mode = Mode.Text; }
                break;
            case Mode.Charset: _mode = Mode.Text; break;
        }
    }

    private void FeedText(char c)
    {
        switch (c)
        {
            case '\u001b': _mode = Mode.Esc; return;
            case '\r': CursorX = 0; _wrapPending = false; return;
            case '\n' or '\v' or '\f': LineFeed(); return;
            case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; return;
            case '\t': CursorX = Math.Min(Cols - 1, (CursorX / 8 + 1) * 8); return;
            case '\a': return;
            case < ' ': return;
        }
        if (_wrapPending) { CursorX = 0; LineFeed(); _wrapPending = false; }
        _screen[CursorY][CursorX] = new TermCell(c, _fg, _bg, _attr);
        if (CursorX == Cols - 1) _wrapPending = true; else CursorX++;
    }

    private void FeedEsc(char c)
    {
        _mode = Mode.Text;
        switch (c)
        {
            case '[': _mode = Mode.Csi; _esc.Clear(); break;
            case ']': _mode = Mode.Osc; _esc.Clear(); break;
            case '(' or ')' or '*' or '+': _mode = Mode.Charset; break;
            case '7': _savedX = CursorX; _savedY = CursorY; break;
            case '8': CursorX = _savedX; CursorY = _savedY; break;
            case 'M': ReverseIndex(); break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'c': FullReset(); break;
            case '=' or '>': break;
        }
    }

    private void HandleOsc(string s)
    {
        var semi = s.IndexOf(';');
        if (semi > 0 && (s[..semi] is "0" or "2")) Title = s[(semi + 1)..];
    }

    private void LineFeed()
    {
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        if (CursorY == _top) ScrollDown(1); else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int n)
    {
        for (var i = 0; i < n; i++)
        {
            if (_top == 0 && _saved is null && _bottom == Rows - 1)
            {
                _scrollback.Add(_screen[0]);
                if (_scrollback.Count > MaxScrollback) _scrollback.RemoveAt(0);
            }
            for (var y = _top; y < _bottom; y++) _screen[y] = _screen[y + 1];
            _screen[_bottom] = NewRow(Cols);
        }
    }

    private void ScrollDown(int n)
    {
        for (var i = 0; i < n; i++)
        {
            for (var y = _bottom; y > _top; y--) _screen[y] = _screen[y - 1];
            _screen[_top] = NewRow(Cols);
        }
    }

    private void FullReset()
    {
        _screen = NewScreen(Rows, Cols); _saved = null; CursorX = CursorY = 0; _fg = _bg = -1; _attr = CellAttr.None; _top = 0; _bottom = Rows - 1; _wrapPending = false;
    }

    private static int[] Params(string body, int defaultValue = 0)
    {
        if (body.Length == 0) return [defaultValue];
        return body.Split(';').Select(p => int.TryParse(p, out var v) ? v : defaultValue).ToArray();
    }

    private void HandleCsi(string seq)
    {
        var final = seq[^1];
        var body = seq[..^1];
        var priv = body.StartsWith('?') || body.StartsWith('>') || body.StartsWith('=');
        var prefix = priv ? body[0] : '\0';
        if (priv) body = body[1..];
        var p = Params(body);
        int P(int i, int def = 1) => i < p.Length && p[i] != 0 ? p[i] : def;
        _wrapPending = false;
        switch (final)
        {
            case 'A': CursorY = Math.Max(_top, CursorY - P(0)); break;
            case 'B' or 'e': CursorY = Math.Min(_bottom, CursorY + P(0)); break;
            case 'C' or 'a': CursorX = Math.Min(Cols - 1, CursorX + P(0)); break;
            case 'D': CursorX = Math.Max(0, CursorX - P(0)); break;
            case 'E': CursorX = 0; CursorY = Math.Min(Rows - 1, CursorY + P(0)); break;
            case 'F': CursorX = 0; CursorY = Math.Max(0, CursorY - P(0)); break;
            case 'G' or '`': CursorX = Math.Clamp(P(0) - 1, 0, Cols - 1); break;
            case 'H' or 'f': CursorY = Math.Clamp(P(0) - 1, 0, Rows - 1); CursorX = Math.Clamp(P(1) - 1, 0, Cols - 1); break;
            case 'd': CursorY = Math.Clamp(P(0) - 1, 0, Rows - 1); break;
            case 'J': EraseDisplay(p.Length > 0 ? p[0] : 0); break;
            case 'K': EraseLine(p.Length > 0 ? p[0] : 0); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'P': DeleteChars(P(0)); break;
            case '@': InsertChars(P(0)); break;
            case 'X': for (var i = 0; i < P(0) && CursorX + i < Cols; i++) _screen[CursorY][CursorX + i] = TermCell.Blank; break;
            case 'S': ScrollUp(P(0)); break;
            case 'T': ScrollDown(P(0)); break;
            case 'm': Sgr(p); break;
            case 'r':
                _top = Math.Clamp(P(0) - 1, 0, Rows - 1); _bottom = Math.Clamp(P(1, Rows) - 1, _top, Rows - 1);
                CursorX = 0; CursorY = _top; break;
            case 's': _savedX = CursorX; _savedY = CursorY; break;
            case 'u': CursorX = _savedX; CursorY = _savedY; break;
            case 'h' or 'l': if (priv) SetMode(p, final == 'h'); break;
            case 'n':
                if (!priv && p.Length > 0 && p[0] == 6) Response?.Invoke($"\u001b[{CursorY + 1};{CursorX + 1}R");
                else if (!priv && p.Length > 0 && p[0] == 5) Response?.Invoke("\u001b[0n");
                break;
            case 'c': if (prefix != '>') Response?.Invoke("\u001b[?1;2c"); break;
        }
    }

    private void SetMode(int[] modes, bool on)
    {
        foreach (var m in modes)
            switch (m)
            {
                case 1: ApplicationCursorKeys = on; break;
                case 25: CursorVisible = on; break;
                case 2004: BracketedPaste = on; break;
                case 47 or 1047 or 1049:
                    if (on && _saved is null) { _saved = _screen; _screen = NewScreen(Rows, Cols); _savedX = CursorX; _savedY = CursorY; CursorX = CursorY = 0; _top = 0; _bottom = Rows - 1; }
                    else if (!on && _saved is not null) { _screen = _saved; _saved = null; CursorX = _savedX; CursorY = _savedY; _top = 0; _bottom = Rows - 1; }
                    break;
            }
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var y = CursorY + 1; y < Rows; y++) _screen[y] = NewRow(Cols);
                break;
            case 1:
                EraseLine(1);
                for (var y = 0; y < CursorY; y++) _screen[y] = NewRow(Cols);
                break;
            case 2 or 3:
                for (var y = 0; y < Rows; y++) _screen[y] = NewRow(Cols);
                if (mode == 3) _scrollback.Clear();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = _screen[CursorY];
        var (from, to) = mode switch { 0 => (CursorX, Cols - 1), 1 => (0, CursorX), _ => (0, Cols - 1) };
        for (var x = from; x <= to && x < Cols; x++) row[x] = TermCell.Blank;
    }

    private void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        for (var i = 0; i < n; i++)
        {
            for (var y = _bottom; y > CursorY; y--) _screen[y] = _screen[y - 1];
            _screen[CursorY] = NewRow(Cols);
        }
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        for (var i = 0; i < n; i++)
        {
            for (var y = CursorY; y < _bottom; y++) _screen[y] = _screen[y + 1];
            _screen[_bottom] = NewRow(Cols);
        }
    }

    private void DeleteChars(int n)
    {
        var row = _screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(row, CursorX + n, row, CursorX, Cols - CursorX - n);
        for (var x = Cols - n; x < Cols; x++) row[x] = TermCell.Blank;
    }

    private void InsertChars(int n)
    {
        var row = _screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(row, CursorX, row, CursorX + n, Cols - CursorX - n);
        for (var x = CursorX; x < CursorX + n; x++) row[x] = TermCell.Blank;
    }

    private void Sgr(int[] p)
    {
        for (var i = 0; i < p.Length; i++)
        {
            var v = p[i];
            switch (v)
            {
                case 0: _fg = _bg = -1; _attr = CellAttr.None; break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Dim; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4: _attr |= CellAttr.Underline; break;
                case 7: _attr |= CellAttr.Inverse; break;
                case 22: _attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 27: _attr &= ~CellAttr.Inverse; break;
                case >= 30 and <= 37: _fg = v - 30; break;
                case 39: _fg = -1; break;
                case >= 40 and <= 47: _bg = v - 40; break;
                case 49: _bg = -1; break;
                case >= 90 and <= 97: _fg = v - 90 + 8; break;
                case >= 100 and <= 107: _bg = v - 100 + 8; break;
                case 38 or 48:
                    var isFg = v == 38;
                    if (i + 1 < p.Length && p[i + 1] == 5 && i + 2 < p.Length) { Set(isFg, p[i + 2]); i += 2; }
                    else if (i + 1 < p.Length && p[i + 1] == 2 && i + 4 < p.Length) { Set(isFg, 0x1000000 | (p[i + 2] & 255) << 16 | (p[i + 3] & 255) << 8 | (p[i + 4] & 255)); i += 4; }
                    break;
            }
        }
        void Set(bool fg, int color) { if (fg) _fg = color; else _bg = color; }
    }

    /// <summary>Palette index (0-255) or true color (0x1RRGGBB) to ARGB.</summary>
    public static uint ToArgb(int color, bool dark = true)
    {
        if (color >= 0x1000000) return 0xFF000000 | (uint)(color & 0xFFFFFF);
        if (color < 16)
        {
            uint[] basic = [0xFF1E1E1E, 0xFFCD3131, 0xFF0DBC79, 0xFFE5E510, 0xFF2472C8, 0xFFBC3FBC, 0xFF11A8CD, 0xFFE5E5E5,
                            0xFF666666, 0xFFF14C4C, 0xFF23D18B, 0xFFF5F543, 0xFF3B8EEA, 0xFFD670D6, 0xFF29B8DB, 0xFFFFFFFF];
            return basic[color];
        }
        if (color < 232)
        {
            var c = color - 16; int r = c / 36, g = c / 6 % 6, b = c % 6;
            static uint L(int x) => x == 0 ? 0u : (uint)(55 + x * 40);
            return 0xFF000000 | L(r) << 16 | L(g) << 8 | L(b);
        }
        var gray = (uint)(8 + (color - 232) * 10);
        return 0xFF000000 | gray << 16 | gray << 8 | gray;
    }
}
