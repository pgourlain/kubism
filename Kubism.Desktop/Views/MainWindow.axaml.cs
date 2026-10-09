using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kubism.Desktop.Services;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += (_, _) => { BuildMenus(); if (Vm is { } v) v.SettingsApplied += BuildMenus; };
        CatalogList.DoubleTapped += (_, _) => { if (DataContext is MainViewModel vm && vm.SelectedCluster is { } c) _ = vm.ConnectAsync(c); };
        Opened += (_, _) => BuildMenus();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) { } };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void BuildMenus()
    {
        if (Vm is null) return;
        var spec = MenuSpecs.Build(Vm, this);
        MenuBuilder.Apply(this, MainMenu, spec);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var vm = Vm;
        if (vm is null) return;
        if (cmd && e.Key == Key.K) { vm.OpenPaletteCommand.Execute(null); Dispatcher.UIThread.Post(() => PaletteBox.Focus()); e.Handled = true; return; }
        if (cmd && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.F) { vm.OpenGlobalSearchCommand.Execute(null); Dispatcher.UIThread.Post(() => SearchBox.Focus()); e.Handled = true; return; }
        if (vm.GlobalSearchOpen && e.Key == Key.Escape) { vm.CloseGlobalSearchCommand.Execute(null); e.Handled = true; return; }
        if (vm.PaletteOpen)
        {
            switch (e.Key)
            {
                case Key.Escape: vm.ClosePaletteCommand.Execute(null); e.Handled = true; break;
                case Key.Down: vm.Palette.Move(1); e.Handled = true; break;
                case Key.Up: vm.Palette.Move(-1); e.Handled = true; break;
                case Key.Enter: _ = vm.Palette.ExecuteAsync(); e.Handled = true; break;
            }
            return;
        }
        if (cmd && e.Key is >= Key.D1 and <= Key.D9) { vm.SelectWorkspaceByIndex(e.Key - Key.D1); e.Handled = true; }
    }

    private void OnPaletteBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender) Vm?.ClosePaletteCommand.Execute(null);
    }

    private void OnSearchBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender) Vm?.CloseGlobalSearchCommand.Execute(null);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = Vm?.Search.SearchAsync(); e.Handled = true; }
    }

    private void OnSearchHitClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SearchHit hit }) Vm?.Search.OpenCommand.Execute(hit);
    }

    private void OnPaletteDoubleTapped(object? sender, TappedEventArgs e) => _ = Vm?.Palette.ExecuteAsync();
}
