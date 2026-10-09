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

    public static ServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = AppOptions.Current;
            AppServicesOptions? so = null;
            if (options.Demo)
            {
                _demo = DemoEnvironment.StartAsync().GetAwaiter().GetResult();
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
            if (Services.GetRequiredService<ISettingsStore>().Current.CheckForUpdates) _ = vm.CheckForUpdatesAsync();
            desktop.Exit += (_, _) =>
            {
                try { vm.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
                try { _demo?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
