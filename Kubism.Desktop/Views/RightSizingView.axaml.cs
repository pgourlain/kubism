using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class RightSizingView : UserControl
{
    public RightSizingView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not RightSizingViewModel { Selected: not null } vm) return;
        vm.CloseDetail();
        e.Handled = true;
    }
}
