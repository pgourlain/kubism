using Avalonia.Controls;
using AvaloniaEdit;
using Kubism.Desktop.Services;

namespace Kubism.Desktop.Controls;

/// <summary>Right-click menu shared by every AvaloniaEdit text area (YAML, logs): Copy, Select all, plus Cut/Paste when editable.</summary>
public static class EditorContextMenu
{
    public static void Attach(TextEditor editor)
    {
        var cut = new MenuItem { Header = "Cut".T() };
        var copy = new MenuItem { Header = "Copy".T() };
        var paste = new MenuItem { Header = "Paste".T() };
        var all = new MenuItem { Header = "Select all".T() };
        cut.Click += (_, _) => editor.Cut();
        copy.Click += (_, _) => editor.Copy();
        paste.Click += (_, _) => editor.Paste();
        all.Click += (_, _) => editor.SelectAll();
        var menu = new ContextMenu { ItemsSource = new Control[] { cut, copy, paste, new Separator(), all } };
        menu.Opening += (_, _) =>
        {
            var hasSelection = editor.TextArea.Selection.Length > 0;
            cut.IsVisible = paste.IsVisible = !editor.IsReadOnly;
            cut.IsEnabled = hasSelection;
            copy.IsEnabled = hasSelection;
        };
        editor.ContextMenu = menu;
    }
}
