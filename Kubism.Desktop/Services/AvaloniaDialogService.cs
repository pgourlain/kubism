using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kubism.Core.Catalog;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Services;

/// <summary>Dialogs built in code so they work the same in the app and in the headless tests.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private static Window? Owner => WindowHelper.MainWindow;

    private static Window Make(string title, double width, double height, Control content, bool resizable = false)
    {
        var w = new Window
        {
            Title = title, Width = width, Height = height, CanResize = resizable, Content = content,
            WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        return w;
    }

    private static async Task<T?> ShowAsync<T>(Window w)
    {
        if (Owner is { } o) return await w.ShowDialog<T?>(o);
        var tcs = new TaskCompletionSource<T?>();
        w.Closed += (_, _) => tcs.TrySetResult(default);
        w.Show();
        return await tcs.Task;
    }

    private static StackPanel Buttons(Window w, string ok, bool okEnabled = true, object? okResult = null, Button? okOut = null, bool cancel = true, bool destructive = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        if (cancel)
        {
            var c = new Button { Content = "Cancel".T(), MinWidth = 80 };
            c.Click += (_, _) => w.Close(null);
            panel.Children.Add(c);
        }
        var b = new Button { Content = ok, MinWidth = 80, IsEnabled = okEnabled, IsDefault = true };
        if (destructive) b.Background = new SolidColorBrush(Color.Parse("#B91C1C")) { };
        if (destructive) b.Foreground = Brushes.White;
        b.Click += (_, _) => w.Close(okResult ?? true);
        panel.Children.Add(b);
        if (okOut is not null) { }
        return panel;
    }

    public async Task<bool> ConfirmAsync(ConfirmRequest r)
    {
        var w = new Window();
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = r.Message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Buttons(w, r.ConfirmText, destructive: r.Destructive));
        var win = Make(r.Title, 420, 180, panel);
        return await ShowAsync<bool?>(win) == true;
    }

    public async Task<string?> ConfirmTypedAsync(string title, string message, string expected)
    {
        var w = new Window();
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"Type \"{expected}\" to confirm:", FontWeight = FontWeight.SemiBold });
        var box = new TextBox { PlaceholderText = expected };
        panel.Children.Add(box);
        var okPanel = Buttons(w, "Confirm", okEnabled: false, destructive: true);
        var ok = (Button)okPanel.Children[^1];
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text == expected;
        panel.Children.Add(okPanel);
        var win = Make(title, 460, 240, panel);
        ok.Click -= null;
        var result = await ShowAsync<bool?>(win);
        return result == true && box.Text == expected ? box.Text : null;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var w = new Window();
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 220, BorderThickness = new Thickness(0) });
        panel.Children.Add(Buttons(w, "OK", cancel: false));
        await ShowAsync<bool?>(Make(title, 520, 240, panel, true));
    }

    public async Task ShowTextAsync(string title, string text)
    {
        var w = new Window();
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Menlo,Consolas,monospace") });
        var b = Buttons(w, "OK", cancel: false);
        Grid.SetRow(b, 1);
        grid.Children.Add(b);
        await ShowAsync<bool?>(Make(title, 640, 420, grid, true));
    }

    public async Task<string?> PromptAsync(string title, string message, string? initial = null)
    {
        var w = new Window();
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var box = new TextBox { Text = initial ?? "" };
        panel.Children.Add(box);
        panel.Children.Add(Buttons(w, "OK"));
        var win = Make(title, 420, 190, panel);
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await ShowAsync<bool?>(win) == true ? box.Text : null;
    }

    public async Task<string?> ChooseAsync(string title, IReadOnlyList<string> options)
    {
        var w = new Window();
        var list = new ListBox { ItemsSource = options, SelectedIndex = options.Count > 0 ? 0 : -1, MaxHeight = 240 };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(list);
        panel.Children.Add(Buttons(w, "OK"));
        var win = Make(title, 380, 360, panel);
        list.DoubleTapped += (_, _) => win.Close(true);
        return await ShowAsync<bool?>(win) == true ? list.SelectedItem as string : null;
    }

    public async Task<ClusterEditorViewModel?> EditClusterAsync(ClusterEditorViewModel vm)
    {
        var w = new Window();
        var grid = new Grid { Margin = new Thickness(20), ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 8 };
        var row = 0;
        void Add(string label, Control c)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, row); Grid.SetRow(c, row); Grid.SetColumn(c, 1);
            grid.Children.Add(t); grid.Children.Add(c);
            row++;
        }
        TextBox Box(string value, Action<string> set, string? watermark = null) { var b = new TextBox { Text = value, PlaceholderText = watermark }; b.TextChanged += (_, _) => set(b.Text ?? ""); return b; }
        Add("Context", new TextBlock { Text = vm.ContextName, Classes = { "muted" } });
        Add("Display name", Box(vm.Label, s => vm.Label = s));
        Add("Icon letter", Box(vm.IconLetter, s => vm.IconLetter = s));
        var color = new ComboBox { ItemsSource = vm.ColorChoices, SelectedItem = vm.Color, HorizontalAlignment = HorizontalAlignment.Stretch };
        color.SelectionChanged += (_, _) => vm.Color = color.SelectedItem as string ?? "";
        Add("Color", color);
        Add("Tags (comma separated)", Box(vm.Tags, s => vm.Tags = s));
        var safety = new ComboBox { ItemsSource = vm.SafetyModes, SelectedItem = vm.Safety, HorizontalAlignment = HorizontalAlignment.Stretch };
        safety.SelectionChanged += (_, _) => { if (safety.SelectedItem is SafetyMode m) vm.Safety = m; };
        Add("Safety mode", safety);
        Add("Default namespace", Box(vm.DefaultNamespace, s => vm.DefaultNamespace = s));
        Add("Known namespaces", Box(vm.Namespaces, s => vm.Namespaces = s, "when listing namespaces is forbidden"));
        Add("Prometheus URL", Box(vm.PrometheusUrl, s => vm.PrometheusUrl = s, "https://… or empty for auto-detect"));
        var sampling = new CheckBox { Content = "Sample metrics-server locally (for right-sizing)", IsChecked = vm.LocalSampling };
        sampling.IsCheckedChanged += (_, _) => vm.LocalSampling = sampling.IsChecked == true;
        Add("Local sampling", sampling);
        Add("Price per vCPU-hour", Box(vm.PriceCpu, s => vm.PriceCpu = s));
        Add("Price per GiB-hour", Box(vm.PriceMem, s => vm.PriceMem = s));
        var buttons = Buttons(w, "Save");
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(buttons, row); Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        var scroll = new ScrollViewer { Content = grid };
        return await ShowAsync<bool?>(Make("Cluster settings", 560, 560, scroll, true)) == true ? vm : null;
    }

    public async Task<SettingsEditorViewModel?> EditSettingsAsync(SettingsEditorViewModel vm)
    {
        var w = new Window();
        var grid = new Grid { Margin = new Thickness(20), ColumnDefinitions = new ColumnDefinitions("200,*"), RowSpacing = 8 };
        var row = 0;
        void Add(string label, Control c)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, row); Grid.SetRow(c, row); Grid.SetColumn(c, 1);
            grid.Children.Add(t); grid.Children.Add(c);
            row++;
        }
        TextBox Box(string value, Action<string> set) { var b = new TextBox { Text = value }; b.TextChanged += (_, _) => set(b.Text ?? ""); return b; }
        ComboBox Combo(string[] items, string current, Action<string> set) { var c = new ComboBox { ItemsSource = items, SelectedItem = current, HorizontalAlignment = HorizontalAlignment.Stretch }; c.SelectionChanged += (_, _) => set(c.SelectedItem as string ?? current); return c; }
        CheckBox Check(string text, bool value, Action<bool> set) { var c = new CheckBox { Content = text, IsChecked = value }; c.IsCheckedChanged += (_, _) => set(c.IsChecked == true); return c; }
        Add("Theme", Combo(vm.Themes, vm.Theme, v => vm.Theme = v));
        Add("Language", Combo(vm.Languages, vm.Language, v => vm.Language = v));
        Add("Log buffer (lines)", Box(vm.LogBufferLines.ToString(), v => { if (int.TryParse(v, out var n)) vm.LogBufferLines = n; }));
        Add("Max live lists per cluster", Box(vm.MaxInformers.ToString(), v => { if (int.TryParse(v, out var n)) vm.MaxInformers = n; }));
        Add("Production context regex", Box(vm.ProductionRegex, v => vm.ProductionRegex = v));
        Add("Kinds", Check("Dim kinds I cannot list", vm.DimForbiddenKinds, v => vm.DimForbiddenKinds = v));
        Add("Updates", Check("Check GitHub releases once a day (never installs)", vm.CheckForUpdates, v => vm.CheckForUpdates = v));
        Add("Right-sizing CPU percentile", Box(vm.CpuPercentile.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) vm.CpuPercentile = d; }));
        Add("Memory margin (×)", Box(vm.MemoryMargin.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) vm.MemoryMargin = d; }));
        Add("Memory limit factor (×)", Box(vm.MemoryLimitFactor.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) vm.MemoryLimitFactor = d; }));
        Add("CPU limit", Check("Keep the current CPU limit (default: none)", vm.KeepCpuLimit, v => vm.KeepCpuLimit = v));
        var buttons = Buttons(w, "Save");
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(buttons, row); Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        return await ShowAsync<bool?>(Make("Settings", 600, 560, new ScrollViewer { Content = grid }, true)) == true ? vm : null;
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var sp = Owner?.StorageProvider;
        if (sp is null) return null;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var sp = Owner?.StorageProvider;
        if (sp is null) return null;
        var f = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return f.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        var sp = Owner?.StorageProvider;
        if (sp is null) return null;
        var f = await sp.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName });
        return f?.TryGetLocalPath();
    }

    public SignInHandle ShowSignIn(string cluster)
    {
        var cts = new CancellationTokenSource();
        var log = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Menlo,Consolas,monospace"), TextWrapping = TextWrapping.Wrap };
        var w = new Window();
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(new TextBlock { Text = $"Signing in to {cluster}", FontWeight = FontWeight.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 8) });
        Grid.SetRow(log, 1); grid.Children.Add(log);
        var cancel = new Button { Content = "Cancel".T(), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        cancel.Click += (_, _) => { cts.Cancel(); w.Close(); };
        Grid.SetRow(cancel, 2); grid.Children.Add(cancel);
        var win = Make("Sign in", 560, 300, grid, true);
        if (Owner is { } o) win.Show(o); else win.Show();
        return new SignInHandle(line => Dispatcher.UIThread.Post(() => log.Text += line + "\n"), cts.Token, () => Dispatcher.UIThread.Post(win.Close));
    }
}
