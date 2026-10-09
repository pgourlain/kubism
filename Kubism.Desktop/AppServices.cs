using Kubism.Core;
using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.RightSizing;
using Kubism.Core.Sessions;
using Kubism.Desktop.Services;
using Kubism.Desktop.ViewModels;
using Kubism.Kube;
using Microsoft.Extensions.DependencyInjection;

namespace Kubism.Desktop;

public sealed class AppServicesOptions
{
    public string? SettingsPath { get; init; }
    public string? DataFolder { get; init; }
    public Func<string?>? KubeconfigEnv { get; init; }
    public string? HomeConfig { get; init; }
    public bool WatchKubeconfig { get; init; } = true;
    public IDialogService? Dialogs { get; init; }
    public IClipboardService? Clipboard { get; init; }
    public IBrowserLauncher? Browser { get; init; }
    public IUiThread? Ui { get; init; }
    public Action<IServiceCollection>? Configure { get; init; }
}

/// <summary>Composition root used by <see cref="App"/> and the UI tests.</summary>
public static class AppServices
{
    public static ServiceProvider Build(AppServicesOptions? options = null)
    {
        options ??= new AppServicesOptions();
        var data = options.DataFolder ?? AppOptions.Current.DataFolder ?? AppPaths.DataFolder;
        var services = new ServiceCollection();
        services.AddKubismCore(options.SettingsPath ?? Path.Combine(data, "settings.json"), options.WatchKubeconfig);
        services.AddKubismKube();
        services.AddSingleton<IKubeconfigCatalog>(sp => new KubeconfigCatalog(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IClusterEventsService>(),
            options.KubeconfigEnv, options.HomeConfig, watch: options.WatchKubeconfig));
        services.AddSingleton<IUsageStore>(_ => new FileUsageStore(Path.Combine(data, "usage")));
        services.AddSingleton<ITokenProvider, Kube.AzureCliTokenProvider>();
        services.AddSingleton<IUiThread>(options.Ui ?? new AvaloniaUiThread());
        services.AddSingleton<IDialogService>(options.Dialogs ?? new AvaloniaDialogService());
        services.AddSingleton<IClipboardService>(options.Clipboard ?? new AvaloniaClipboardService());
        services.AddSingleton<IBrowserLauncher>(options.Browser ?? new SystemBrowserLauncher());
        services.AddSingleton<MainViewModel>();
        options.Configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
