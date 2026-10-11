using Avalonia.Controls;
using Avalonia.Threading;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class UsageView : UserControl
{
    private UsageViewModel? _vm;

    public UsageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.RevealRequested -= Reveal;
            _vm = DataContext as UsageViewModel;
            if (_vm is not null) _vm.RevealRequested += Reveal;
        };
    }

    /// <summary>After a restore the selected row may be far down a long list: scroll it into view once the grid has laid out.</summary>
    private void Reveal(object item) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var grid in new[] { RowsGrid, DeadGrid, DanglingGrid, BrokenGrid })
            if (grid.IsVisible && grid.SelectedItem == item) { grid.ScrollIntoView(item, grid.Columns.FirstOrDefault()); return; }
    }, DispatcherPriority.Loaded);
}
