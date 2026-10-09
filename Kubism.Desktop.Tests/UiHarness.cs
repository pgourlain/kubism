using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kubism.Core.Catalog;
using Kubism.Desktop.ViewModels;
using Kubism.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Kubism.Desktop.Tests;

/// <summary>Renders the real MainWindow against the in-process fake API server and saves screenshots.</summary>
public sealed class UiHarness : IAsyncDisposable
{
    public DemoEnvironment Demo { get; private set; } = null!;
    public ServiceProvider Services { get; private set; } = null!;
    public MainViewModel Main { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public FakeDialogService Dialogs { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeBrowser Browser { get; } = new();
    public string DataDir { get; private set; } = "";

    public static string ScreenshotDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static async Task<UiHarness> StartAsync(Action<ISettingsStore>? configure = null, bool showWindow = true)
    {
        var h = new UiHarness();
        h.Demo = await DemoEnvironment.StartAsync();
        h.DataDir = Path.Combine(Path.GetDirectoryName(h.Demo.KubeconfigPath)!, "data");
        Directory.CreateDirectory(h.DataDir);
        h.Services = AppServices.Build(new AppServicesOptions
        {
            SettingsPath = Path.Combine(h.DataDir, "settings.json"), DataFolder = h.DataDir,
            KubeconfigEnv = () => h.Demo.KubeconfigPath, HomeConfig = Path.Combine(h.DataDir, "none"), WatchKubeconfig = false,
            Dialogs = h.Dialogs, Clipboard = h.Clipboard, Browser = h.Browser, Ui = new TestUiThread(),
        });
        var settings = h.Services.GetRequiredService<ISettingsStore>();
        foreach (var e in h.Services.GetRequiredService<IKubeconfigCatalog>().Entries)
        {
            var m = settings.GetMetadata(e.Id);
            m.PrometheusUrl = h.Demo.Prometheus.Url;
            settings.SetMetadata(e.Id, m);
        }
        configure?.Invoke(settings);
        h.Main = h.Services.GetRequiredService<MainViewModel>();
        h.Window = new MainWindow { DataContext = h.Main, Width = 1440, Height = 900 };
        if (showWindow) h.Window.Show();
        Pump();
        return h;
    }

    public ClusterItemViewModel Cluster(string context) => Main.Clusters.First(c => c.Entry.ContextName == context);

    public async Task<WorkspaceViewModel> ConnectAsync(string context = "demo-staging")
    {
        await Main.ConnectAsync(Cluster(context));
        await WaitFor(() => Main.SelectedWorkspace is not null);
        return Main.SelectedWorkspace!;
    }

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    public static async Task WaitFor(Func<bool> cond, int ms = 10000, string? what = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Pump();
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for: {what ?? "condition"}");
            await Task.Delay(25);
        }
        Pump();
    }

    public string Screenshot(string name)
    {
        Pump();
        var path = Path.Combine(ScreenshotDir, name + ".png");
        Window.CaptureRenderedFrame()?.Save(path);
        return path;
    }

    public T? Find<T>(Func<T, bool>? filter = null) where T : Visual =>
        Window.GetVisualDescendants().OfType<T>().FirstOrDefault(v => filter?.Invoke(v) ?? true);

    public async ValueTask DisposeAsync()
    {
        try { Window.Close(); } catch { }
        await Main.DisposeAsync();
        await Services.DisposeAsync();
        await Demo.DisposeAsync();
    }
}
