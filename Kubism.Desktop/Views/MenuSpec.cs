using Avalonia.Controls;
using Avalonia.Input;
using Kubism.Core;
using Kubism.Core.Catalog;
using Kubism.Desktop.Services;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

/// <summary>Menu description turned into a macOS NativeMenu or an in-window Menu.</summary>
public sealed record MenuSpec(string Header, Action? Execute = null, KeyGesture? Gesture = null, IReadOnlyList<MenuSpec>? Children = null, bool Separator = false)
{
    public static MenuSpec Sep() => new("", Separator: true);
}

public static class MenuSpecs
{
    private static KeyModifiers Cmd => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public static IReadOnlyList<MenuSpec> Build(MainViewModel vm, Window window) =>
    [
        new("File".T(), Children:
        [
            new("Add kubeconfig…".T(), () => vm.AddKubeconfigCommand.Execute(null)),
            new("Add folder…".T(), () => vm.AddFolderCommand.Execute(null)),
            new("Show hidden clusters", () => vm.RestoreHiddenClustersCommand.Execute(null)),
            new("Reload clusters", () => vm.ReloadCatalogCommand.Execute(null)),
            new("Settings…", () => _ = vm.EditSettingsAsync()),
            MenuSpec.Sep(),
            new("Quit".T(), () => window.Close(), new KeyGesture(Key.Q, Cmd)),
        ]),
        new("Go".T(), Children:
        [
            new("Back".T(), () => vm.SelectedWorkspace?.GoBack(), new KeyGesture(Key.OemOpenBrackets, Cmd)),
            new("Forward".T(), () => vm.SelectedWorkspace?.GoForward(), new KeyGesture(Key.OemCloseBrackets, Cmd)),
        ]),
        new("View".T(), Children:
        [
            new("Command palette".T(), () => vm.OpenPaletteCommand.Execute(null), new KeyGesture(Key.K, Cmd)),
            new("Search all clusters…", () => vm.OpenGlobalSearchCommand.Execute(null), new KeyGesture(Key.F, Cmd | KeyModifiers.Shift)),
            new("Close split view", () => vm.CloseSplitCommand.Execute(null)),
            new("Diagnostics".T(), () => _ = ShowDiagnostics(vm)),
        ]),
        new("Help".T(), Children:
        [
            new("Open Log Folder".T(), () => OpenLogFolder(vm)),
            new("Copy diagnostics".T(), () => _ = vm.Clipboard.SetTextAsync(vm.DiagnosticsText())),
            new("Check for updates", () => _ = vm.CheckForUpdatesAsync(true)),
            MenuSpec.Sep(),
            new("About Kubism".T(), () => _ = vm.ShowAboutAsync()),
        ]),
    ];

    private static async Task ShowDiagnostics(MainViewModel vm) => await vm.Dialogs.ShowTextAsync("Diagnostics", DiagnosticsFull(vm));

    public static string DiagnosticsFull(MainViewModel vm)
    {
        var sb = new System.Text.StringBuilder(vm.DiagnosticsText());
        foreach (var w in vm.Workspaces)
        {
            sb.AppendLine($"\n[{w.Title}]");
            foreach (var i in w.Session.Cache.Informers)
                sb.AppendLine($"  {i.Gvr,-50} ns={i.Namespace ?? "*",-12} {i.State,-12} objects={i.Count,6} refs={i.RefCount} last={(i.LastEventAt is { } t ? t.LocalDateTime.ToString("HH:mm:ss") : "-")}");
        }
        return sb.ToString();
    }

    private static void OpenLogFolder(MainViewModel vm)
    {
        var dir = Path.GetDirectoryName(AppLog.Path ?? Path.Combine(AppPaths.DataFolder, "log.txt"))!;
        Directory.CreateDirectory(dir);
        vm.Browser.Open(dir);
    }
}

public static class MenuBuilder
{
    public static void Apply(Window window, Menu inWindow, IReadOnlyList<MenuSpec> spec)
    {
        if (OperatingSystem.IsMacOS())
        {
            // macOS: mutate the existing native menu, never replace it (replacing breaks the app menu).
            var native = NativeMenu.GetMenu(window) ?? new NativeMenu();
            native.Items.Clear();
            foreach (var s in spec) native.Items.Add(ToNative(s));
            NativeMenu.SetMenu(window, native);
            inWindow.IsVisible = false;
        }
        else
        {
            inWindow.Items.Clear();
            foreach (var s in spec) inWindow.Items.Add(ToMenuItem(s));
        }
    }

    private static NativeMenuItemBase ToNative(MenuSpec s)
    {
        if (s.Separator) return new NativeMenuItemSeparator();
        var item = new NativeMenuItem(s.Header) { Gesture = s.Gesture };
        if (s.Children is not null)
        {
            item.Menu = new NativeMenu();
            foreach (var c in s.Children) item.Menu.Items.Add(ToNative(c));
        }
        else if (s.Execute is { } e) item.Click += (_, _) => e();
        return item;
    }

    private static Control ToMenuItem(MenuSpec s)
    {
        if (s.Separator) return new Separator();
        var item = new MenuItem { Header = s.Header, InputGesture = s.Gesture };
        if (s.Children is not null) foreach (var c in s.Children) item.Items.Add(ToMenuItem(c));
        else if (s.Execute is { } e) item.Click += (_, _) => e();
        return item;
    }
}
