using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kubism.Core;
using Kubism.Core.Catalog;
using Kubism.Desktop.ViewModels;
using Kubism.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Kubism.Desktop;

public partial class App : Application
{
    private DemoEnvironment? _demo;
    private readonly List<IDisposable> _signals = [];

    public static ServiceProvider? Services { get; private set; }

    private MainViewModel? Vm => Services?.GetService<MainViewModel>();
    private void OnAbout(object? sender, EventArgs e) => _ = Vm?.ShowAboutAsync();
    private void OnSettings(object? sender, EventArgs e) => _ = Vm?.EditSettingsAsync();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = AppOptions.Current;
            AppServicesOptions? so = null;
            if (options.Demo)
            {
                _demo = DemoEnvironment.StartAsync(withDrift: true).GetAwaiter().GetResult();
                so = new AppServicesOptions
                {
                    SettingsPath = Path.Combine(Path.GetDirectoryName(_demo.KubeconfigPath)!, "settings.json"),
                    DataFolder = Path.GetDirectoryName(_demo.KubeconfigPath),
                    KubeconfigEnv = () => _demo.KubeconfigPath,
                    HomeConfig = Path.Combine(Path.GetDirectoryName(_demo.KubeconfigPath)!, "no-home-config"),
                };
            }
            AppLog.Path = Path.Combine(options.DataFolder ?? AppPaths.DataFolder, "log.txt");
            Services = AppServices.Build(so);
            if (_demo is not null)
            {
                var settings = Services.GetRequiredService<ISettingsStore>();
                foreach (var e in Services.GetRequiredService<IKubeconfigCatalog>().Entries)
                {
                    var m = settings.GetMetadata(e.Id);
                    m.PrometheusUrl = _demo.Prometheus.Url;
                    settings.SetMetadata(e.Id, m);
                }
            }
            Shell.ShellEnvironmentSetup.Apply();
            var vm = Services.GetRequiredService<MainViewModel>();
            vm.ApplySettings();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // SIGTERM/SIGINT (kill, Ctrl+C, logout) quit through the same fast path as the menu.
            foreach (var sig in new[] { System.Runtime.InteropServices.PosixSignal.SIGTERM, System.Runtime.InteropServices.PosixSignal.SIGINT })
                _signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(sig, ctx => { ctx.Cancel = true; Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown()); }));
            if (Services.GetRequiredService<ISettingsStore>().Current.CheckForUpdates) _ = vm.CheckForUpdatesAsync();
            desktop.Exit += (_, _) =>
            {
                // Quit must feel instant: give the clean shutdown under a second, then leave regardless
                // (cluster connections and local listeners die with the process anyway).
                var cleanup = Task.Run(async () =>
                {
                    try { await vm.DisposeAsync(); } catch { }
                    try { if (_demo is not null) await _demo.DisposeAsync(); } catch { }
                });
                cleanup.Wait(TimeSpan.FromMilliseconds(800));
                Environment.Exit(0);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
