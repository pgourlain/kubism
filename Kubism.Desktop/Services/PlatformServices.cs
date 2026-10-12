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
        // Windows: the clipboard can be locked by another process (CLIPBRD_E_CANT_OPEN); retry briefly, never let it crash the app.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(WindowHelper.MainWindow)?.Clipboard;
                if (clipboard is not null) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 3) Kubism.Core.AppLog.Warn($"Cannot write to the clipboard: {ex.Message}");
                else await Task.Delay(100);
            }
        }
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
