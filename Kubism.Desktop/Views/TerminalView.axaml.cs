using Avalonia.Controls;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _vm;

    public TerminalView()
    {
        InitializeComponent();
        Term.InputReceived += s => _vm is null ? Task.CompletedTask : _vm.SendAsync(s);
        Term.SizeChanged += (c, r) => _vm is null ? Task.CompletedTask : _vm.ResizeAsync(c, r);
        Term.CopyRequested = async t => { if (_vm is not null) await _vm.CopyAsync(t); };
        Term.PasteRequested = async () => TopLevel.GetTopLevel(this)?.Clipboard is { } cb ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb) : null;
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Redraw -= Term.Invalidate;
            _vm = DataContext as TerminalViewModel;
            if (_vm is not null) _vm.Redraw += Term.Invalidate;
        };
        AttachedToVisualTree += (_, _) => Term.Focus();
    }
}
