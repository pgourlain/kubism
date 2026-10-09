using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Kubism.Core;

public static class ServiceExtensions
{
    /// <summary>Registers Core singletons. The host must also register <see cref="Api.IKubeTransportFactory"/> (AddKubismKube).</summary>
    public static IServiceCollection AddKubismCore(this IServiceCollection services, string? settingsPath = null, bool watchKubeconfig = true)
    {
        services.AddSingleton<IClusterEventsService, ClusterEventsService>();
        services.AddSingleton<ISettingsStore>(_ => new SettingsStore(settingsPath ?? SettingsStore.DefaultPath));
        services.AddSingleton<IKubeconfigCatalog>(sp => new KubeconfigCatalog(sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IClusterEventsService>(), watch: watchKubeconfig));
        services.AddSingleton<IClusterSessionManager>(sp => new ClusterSessionManager(sp.GetRequiredService<Api.IKubeTransportFactory>(),
            sp.GetRequiredService<IClusterEventsService>(), sp.GetRequiredService<ISettingsStore>(), sp.GetService<Helm.IHelmRunnerFactory>()));
        return services;
    }
}
