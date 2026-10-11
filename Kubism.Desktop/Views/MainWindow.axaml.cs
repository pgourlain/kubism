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
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Kubism/Assets/kubism.png"))); } catch (Exception) { }
        KeyDown += OnKeyDown;
        // The mouse's back and forward buttons.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (Vm?.SelectedWorkspace is not { } nav) return;
            if (props.IsXButton1Pressed) { nav.GoBack(); e.Handled = true; }
            else if (props.IsXButton2Pressed) { nav.GoForward(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => { BuildMenus(); if (Vm is { } v) v.SettingsApplied += BuildMenus; };
        CatalogList.DoubleTapped += (_, _) => { if (DataContext is MainViewModel vm && vm.SelectedCluster is { } c) _ = vm.ConnectAsync(c); };
        Opened += (_, _) => { FitToScreen(); BuildMenus(); };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) { } };
    }

    /// <summary>The default size (1440x900) is larger than small or scaled screens: shrink to the work area and center, so the bottom-right corner stays reachable.</summary>
    private void FitToScreen()
    {
        if (WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        double maxW = area.Width / scale, maxH = area.Height / scale;
        if (Width <= maxW && Height <= maxH) return;
        MinWidth = Math.Min(MinWidth, maxW);
        MinHeight = Math.Min(MinHeight, maxH);
        Width = Math.Min(Width, maxW);
        Height = Math.Min(Height, maxH);
        Position = new PixelPoint(area.X + (int)((area.Width - Width * scale) / 2), area.Y + (int)((area.Height - Height * scale) / 2));
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
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right && vm.SelectedWorkspace is { } nav)
        {
            if (e.Key == Key.Left) nav.GoBack(); else nav.GoForward();
            e.Handled = true; return;
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
