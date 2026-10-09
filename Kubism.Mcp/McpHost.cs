using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.Helm;
using Kubism.Core.RightSizing;
using Kubism.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kubism.Mcp;

/// <summary>`Kubism --mcp`: headless MCP server over stdio. Nothing may be written to stdout outside the protocol.</summary>
public static class McpHost
{
    public static McpOptions ParseOptions(string[] args) => new(args.Contains("--allow-writes"), args.Contains("--allow-production"));

    public static async Task<int> RunAsync(string[] args, IKubeTransportFactory transportFactory, IHelmRunnerFactory? helm = null, ITokenProvider? tokens = null, CancellationToken ct = default)
    {
        var options = ParseOptions(args);
        var dataIdx = Array.IndexOf(args, "--data");
        var data = dataIdx >= 0 && dataIdx + 1 < args.Length ? args[dataIdx + 1] : AppPaths.DataFolder;
        AppLog.Path = Path.Combine(data, "mcp-log.txt");
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stderr only
        builder.Services.AddSingleton<IClusterEventsService, ClusterEventsService>();
        builder.Services.AddSingleton<ISettingsStore>(_ => new SettingsStore(Path.Combine(data, "settings.json")));
        builder.Services.AddSingleton<IKubeconfigCatalog>(sp => new KubeconfigCatalog(sp.GetRequiredService<ISettingsStore>(), null, watch: false));
        builder.Services.AddSingleton(transportFactory);
        builder.Services.AddSingleton<IClusterSessionManager>(sp => new ClusterSessionManager(transportFactory, sp.GetRequiredService<IClusterEventsService>(), sp.GetRequiredService<ISettingsStore>(), helm));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<RightSizing.IUsageStoreAccessor>(new UsageAccessor(new FileUsageStore(Path.Combine(data, "usage")), tokens));
        builder.Services.AddSingleton<McpContext>();
        var mcp = builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "kubism", Version = typeof(McpHost).Assembly.GetName().Version?.ToString() ?? "0.1.0" })
            .WithStdioServerTransport()
            .WithTools<KubismReadTools>()
            .WithResources<KubismResources>();
        if (options.AllowWrites) mcp.WithTools<KubismWriteTools>();
        using var host = builder.Build();
        await host.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    private sealed class UsageAccessor(IUsageStore store, ITokenProvider? tokens) : RightSizing.IUsageStoreAccessor
    {
        public IUsageStore Store { get; } = store;
        public ITokenProvider? Tokens { get; } = tokens;
    }
}
