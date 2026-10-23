using Avalonia.Media;
using AvaloniaEdit;

namespace Kubism.Desktop.Controls;

/// <summary>Selection look shared by every AvaloniaEdit text area: translucent, square, and syntax colors stay readable on top of it.</summary>
public static class EditorSelection
{
    public static void Apply(TextEditor editor)
    {
        var area = editor.TextArea;
        area.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x3B, 0x82, 0xF6));
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;
    }
}
