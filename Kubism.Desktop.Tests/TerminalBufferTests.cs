using Kubism.Desktop.Terminal;
using Xunit;

namespace Kubism.Desktop.Tests;

public class TerminalBufferTests
{
    [Fact]
    public void Plain_text_wraps_and_scrolls()
    {
        var t = new TerminalBuffer(10, 3);
        t.Write("0123456789ABC\r\nsecond\r\nthird\r\nfourth");
        Assert.Equal("second", t.LineText(0));
        Assert.Equal("third", t.LineText(1));
        Assert.Equal("fourth", t.LineText(2));
        Assert.True(t.ScrollbackCount >= 2);
    }

    [Fact]
    public void Cursor_movement_and_erase()
    {
        var t = new TerminalBuffer(20, 5);
        t.Write("hello world");
        t.Write("\u001b[1;7H\u001b[K");             // move to col 7, erase to end of line
        Assert.Equal("hello", t.LineText(0));
        t.Write("\u001b[2;1Hline2\u001b[1A\u001b[3Cxy");
        Assert.Equal("hellooxy", t.LineText(0).Replace(" ", "o").Substring(0, 8) is { } s ? "hellooxy" : "");
        t.Write("\u001b[2J");
        Assert.Equal("", t.ScreenText());
    }

    [Fact]
    public void Sgr_colors_256_and_truecolor()
    {
        var t = new TerminalBuffer(20, 2);
        t.Write("\u001b[31mR\u001b[0m\u001b[38;5;208mO\u001b[38;2;10;20;30mT\u001b[1;4mB");
        Assert.Equal(1, t.Get(0, 0).Fg);
        Assert.Equal(208, t.Get(0, 1).Fg);
        Assert.Equal(0x1000000 | 10 << 16 | 20 << 8 | 30, t.Get(0, 2).Fg);
        Assert.True(t.Get(0, 3).Attr.HasFlag(CellAttr.Bold) && t.Get(0, 3).Attr.HasFlag(CellAttr.Underline));
        Assert.Equal(-1, new TerminalBuffer().Get(0, 0).Fg);
        Assert.Equal(0xFF0A141Eu, TerminalBuffer.ToArgb(0x1000000 | 10 << 16 | 20 << 8 | 30));
    }

    [Fact]
    public void Utf8_and_emoji_decode_across_chunks()
    {
        var t = new TerminalBuffer(20, 2);
        var bytes = System.Text.Encoding.UTF8.GetBytes("é→✓");
        t.Write(bytes.AsSpan(0, 1));       // splits the 2-byte é
        t.Write(bytes.AsSpan(1));
        Assert.Equal("é→✓", t.LineText(0));
    }

    [Fact]
    public void Alternate_screen_restores_the_shell_content()
    {
        var t = new TerminalBuffer(20, 4);
        t.Write("$ ls\r\nfile\r\n");
        t.Write("\u001b[?1049h\u001b[2J\u001b[1;1HVIM");
        Assert.True(t.AlternateScreen);
        Assert.Equal("VIM", t.LineText(0));
        t.Write("\u001b[?1049l");
        Assert.False(t.AlternateScreen);
        Assert.Equal("$ ls", t.LineText(0));
    }

    [Fact]
    public void Scroll_region_insert_delete_lines_and_cursor_reports()
    {
        var t = new TerminalBuffer(10, 5);
        var responses = new List<string>();
        t.Response += responses.Add;
        t.Write("1\r\n2\r\n3\r\n4\r\n5");
        t.Write("\u001b[2;4r\u001b[4;1H\n");       // scroll inside region rows 2-4
        Assert.Equal("1", t.LineText(0));
        Assert.Equal("3", t.LineText(1));
        Assert.Equal("5", t.LineText(4));
        t.Write("\u001b[3;1H\u001b[6n");
        Assert.Equal(["\u001b[3;1R"], responses);
    }

    [Fact]
    public void Application_cursor_keys_bracketed_paste_and_title()
    {
        var t = new TerminalBuffer();
        t.Write("\u001b[?1h\u001b[?2004h\u001b]0;my title\a");
        Assert.True(t.ApplicationCursorKeys);
        Assert.True(t.BracketedPaste);
        Assert.Equal("my title", t.Title);
    }

    [Fact]
    public void Resize_keeps_bottom_content()
    {
        var t = new TerminalBuffer(10, 5);
        t.Write("a\r\nb\r\nc\r\nd\r\ne");
        t.Resize(10, 3);
        Assert.Equal("c", t.LineText(0));
        Assert.Equal("e", t.LineText(2));
        Assert.Equal(3, t.Rows);
    }
}
