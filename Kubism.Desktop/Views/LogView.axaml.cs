using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Kubism.Desktop.Terminal;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class LogView : UserControl
{
    private LogViewModel? _vm;
    private readonly List<(int start, int length, int fg)> _colors = [];
    private readonly ColorTransformer _transformer;
    private List<(int index, int length)> _matches = [];
    private int _current = -1;

    public LogView()
    {
        InitializeComponent();
        _transformer = new ColorTransformer(_colors);
        Editor.TextArea.TextView.LineTransformers.Add(_transformer);
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_vm is not null) { _vm.TextChanged -= OnText; _vm.TrimLines -= OnTrim; _vm.PropertyChanged -= OnVmChanged; }
        _vm = DataContext as LogViewModel;
        if (_vm is null) return;
        _vm.TextChanged += OnText; _vm.TrimLines += OnTrim; _vm.PropertyChanged += OnVmChanged;
        Editor.WordWrap = _vm.Wrap;
        _vm.Rebuild();
    }

    private void OnVmChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.Wrap) && _vm is not null) Editor.WordWrap = _vm.Wrap;
        if (e.PropertyName is nameof(LogViewModel.Search) or nameof(LogViewModel.UseRegex)) Search();
    }

    private void OnText(string text, IReadOnlyList<LogColorSpan> spans, bool replace)
    {
        var doc = Editor.Document;
        var atBottom = Editor.VerticalOffset >= Editor.ExtentHeight - Editor.ViewportHeight - 40 || replace;
        if (replace) { _colors.Clear(); doc.Text = text; }
        else
        {
            var offset = doc.TextLength;
            doc.Insert(offset, text);
        }
        var baseOffset = replace ? 0 : doc.TextLength - text.Length;
        foreach (var s in spans) _colors.Add((baseOffset + s.Start, s.Length, s.Fg));
        if (atBottom && _vm is { Paused: false }) Editor.ScrollToEnd();
        if (!string.IsNullOrEmpty(_vm?.Search)) Search();
    }

    private void OnTrim(int lines)
    {
        var doc = Editor.Document;
        if (lines <= 0 || lines >= doc.LineCount) return;
        var end = doc.GetLineByNumber(lines).EndOffset + 1;
        end = Math.Min(end, doc.TextLength);
        doc.Remove(0, end);
        for (var i = _colors.Count - 1; i >= 0; i--)
        {
            var (s, l, f) = _colors[i];
            if (s + l <= end) _colors.RemoveAt(i); else _colors[i] = (Math.Max(0, s - end), l, f);
        }
    }

    private void Search()
    {
        if (_vm is null) return;
        _matches = [.. _vm.FindAll(Editor.Document.Text)];
        _current = _matches.Count > 0 ? 0 : -1;
        Select();
    }

    private void Select()
    {
        if (_current < 0 || _current >= _matches.Count) return;
        var (i, l) = _matches[_current];
        Editor.Select(i, l);
        Editor.TextArea.Caret.Offset = i;
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void OnNext(object? sender, RoutedEventArgs e) { if (_matches.Count == 0) Search(); else { _current = (_current + 1) % _matches.Count; Select(); } }
    private void OnPrev(object? sender, RoutedEventArgs e) { if (_matches.Count == 0) Search(); else { _current = (_current - 1 + _matches.Count) % _matches.Count; Select(); } }

    private sealed class ColorTransformer(List<(int start, int length, int fg)> colors) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (var (s, l, fg) in colors)
            {
                if (s + l <= line.Offset || s >= line.EndOffset) continue;
                var from = Math.Max(s, line.Offset); var to = Math.Min(s + l, line.EndOffset);
                var color = Color.FromUInt32(TerminalBuffer.ToArgb(fg));
                ChangeLinePart(from, to, e => e.TextRunProperties.SetForegroundBrush(new SolidColorBrush(color)));
            }
        }
    }
}
