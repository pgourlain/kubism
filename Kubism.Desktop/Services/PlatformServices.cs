using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Kubism.Desktop.Services;

internal static class WindowHelper
{
    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(WindowHelper.MainWindow)?.Clipboard;
        if (clipboard is not null) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
    }
}

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch (Exception ex) { Kubism.Core.AppLog.Warn($"Cannot open browser: {ex.Message}"); }
    }
}
