using Avalonia.Controls;
using Avalonia.Interactivity;
using Kubism.Core.Helm;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class HelmView : UserControl
{
    public HelmView() => InitializeComponent();

    private void OnRollback(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HelmViewModel vm && HistoryGrid.SelectedItem is HelmRelease r) vm.RollbackCommand.Execute(r);
    }
}
