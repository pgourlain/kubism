using Avalonia.Controls;
using Avalonia.Interactivity;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        NavTree.SelectionChanged += (_, e) =>
        {
            // Group nodes toggle instead of navigating.
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is NavNode { IsGroup: true } g) { g.IsExpanded = !g.IsExpanded; }
        };
    }
}
