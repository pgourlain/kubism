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

    /// <summary>The graph tab needs room: grow the panel to fit the drawing, within what the window can spare.</summary>
    private void WidenFor(double wanted)
    {
        var max = Math.Min(900, Math.Max(300, Bounds.Width - MainGrid.ColumnDefinitions[0].ActualWidth - 260));
        var target = Math.Min(wanted, max);
        if (DetailsCol.Width.IsAbsolute && DetailsCol.Width.Value < target) { DetailsCol.Width = new GridLength(target); _detailsWidth = target; }
    }

    private double _dragStartX, _dragStartWidth;
    private double _dragStartY, _dragStartHeight;

    public WorkspaceView()
    {
        InitializeComponent();
        // A flyout is a popup: it does not inherit the view's data context, so the history list gets it when it opens.
        if (HistoryButton.Flyout is Flyout history) history.Opening += (_, _) => { if (history.Content is Control c) c.DataContext = DataContext; };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not WorkspaceViewModel w) return;
            w.Details.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DetailsViewModel.IsOpen)) SyncDetailsColumn(w);
                else if (e.PropertyName == nameof(DetailsViewModel.Graph) && w.Details is { IsOpen: true, Graph: { } g }) WidenFor(Controls.RelationGraphControl.WidthFor(g));
            };
            SyncDetailsColumn(w);
        };
        // Own drag handle (not a GridSplitter): the splitter turns the star column into a fixed width, which left a gap when the panel closed.
        DetailsHandle.PointerPressed += (_, e) => { _dragStartX = e.GetPosition(this).X; _dragStartWidth = DetailsColumn.Width.Value; e.Pointer.Capture(DetailsHandle); e.Handled = true; };
        DetailsHandle.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, DetailsHandle)) return;
            var max = Math.Max(300, Bounds.Width - MainGrid.ColumnDefinitions[0].ActualWidth - 260);
            DetailsColumn.Width = new GridLength(Math.Clamp(_dragStartWidth - (e.GetPosition(this).X - _dragStartX), 280, Math.Min(900, max)));
        };
        DetailsHandle.PointerReleased += (_, e) => e.Pointer.Capture(null);
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
