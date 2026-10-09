using Avalonia.Controls;
using Avalonia.Interactivity;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class DockView : UserControl
{
    public DockView() => InitializeComponent();

    private async void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DockItemViewModel item } && DataContext is DockViewModel dock) await dock.CloseAsync(item);
        e.Handled = true;
    }
}
