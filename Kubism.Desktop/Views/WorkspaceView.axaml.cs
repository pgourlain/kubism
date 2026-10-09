using Avalonia.Controls;
using Avalonia.Interactivity;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    private double _dragStartY, _dragStartHeight;

    public WorkspaceView()
    {
        InitializeComponent();
        DockHandle.PointerPressed += (_, e) =>
        {
            if (DataContext is not WorkspaceViewModel ws) return;
            _dragStartY = e.GetPosition(this).Y; _dragStartHeight = ws.Dock.Height;
            e.Pointer.Capture(DockHandle); e.Handled = true;
        };
        DockHandle.PointerMoved += (_, e) =>
        {
            if (DataContext is not WorkspaceViewModel ws || !ReferenceEquals(e.Pointer.Captured, DockHandle)) return;
            // Dragging up grows the panel; keep room for the list above and a usable minimum below.
            var max = Math.Max(160, Bounds.Height - 160);
            ws.Dock.Height = Math.Clamp(_dragStartHeight - (e.GetPosition(this).Y - _dragStartY), 120, max);
        };
        DockHandle.PointerReleased += (_, e) => e.Pointer.Capture(null);
        NavTree.SelectionChanged += (_, e) =>
        {
            // Group nodes toggle instead of navigating.
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is NavNode { IsGroup: true } g) { g.IsExpanded = !g.IsExpanded; }
        };
    }
}
