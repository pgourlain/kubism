using Avalonia.Controls;
using Avalonia.Interactivity;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    public ColumnDefinition DetailsColumn => MainGrid.ColumnDefinitions[4];
    private ColumnDefinition DetailsCol => DetailsColumn;
    private double _detailsWidth = 420;

    /// <summary>The details column is a real, user-resizable column: remembered width when open, zero when closed.</summary>
    private void SyncDetailsColumn(WorkspaceViewModel w)
    {
        if (w.Details.IsOpen) DetailsCol.Width = new GridLength(_detailsWidth);
        else
        {
            if (DetailsCol.Width.IsAbsolute && DetailsCol.Width.Value > 0) _detailsWidth = DetailsCol.Width.Value;
            DetailsCol.Width = new GridLength(0);
        }
    }

    private double _dragStartY, _dragStartHeight;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not WorkspaceViewModel w) return;
            w.Details.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DetailsViewModel.IsOpen)) SyncDetailsColumn(w); };
            SyncDetailsColumn(w);
        };
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
