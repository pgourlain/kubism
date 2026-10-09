using Kubism.Core;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Sessions;
using ModelContextProtocol;

namespace Kubism.Mcp;

public sealed record McpOptions(bool AllowWrites, bool AllowProduction);

/// <summary>Resolves clusters by id/context/label, connects lazily, and turns Core exceptions into MCP errors. Tool bodies run through <see cref="Run{T}"/>.</summary>
public sealed class McpContext(IKubeconfigCatalog catalog, IClusterSessionManager sessions, ISettingsStore settings, McpOptions options, RightSizing.IUsageStoreAccessor? usage = null)
{
    public McpOptions Options { get; } = options;
    public IKubeconfigCatalog Catalog { get; } = catalog;
    public ISettingsStore Settings { get; } = settings;
    public IClusterSessionManager Sessions { get; } = sessions;
    public RightSizing.IUsageStoreAccessor? Usage { get; } = usage;

    public async Task<T> Run<T>(Func<Task<T>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (McpException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (SafetyException ex) { throw new McpException(ex.Message, ex); }
        catch (KubeApiException ex) { throw new McpException($"Kubernetes API error {ex.StatusCode} {ex.Reason}: {ex.Message}", ex); }
        catch (KubeConnectionException ex) { throw new McpException(ex.Message, ex); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException or FileNotFoundException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    public ClusterEntry ResolveEntry(string cluster)
    {
        var entries = Catalog.Entries;
        var match = entries.FirstOrDefault(e => e.Id == cluster)
                    ?? entries.FirstOrDefault(e => string.Equals(e.ContextName, cluster, StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault(e => string.Equals(Settings.GetMetadata(e.Id).Label, cluster, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var names = string.Join(", ", entries.Select(e => e.ContextName));
            throw new McpException($"Unknown cluster '{cluster}'. Known clusters: {names}");
        }
        return match;
    }

    public async Task<ClusterSession> SessionAsync(string cluster, CancellationToken ct)
    {
        var entry = ResolveEntry(cluster);
        var existing = Sessions.Get(entry.Id);
        if (existing is { State: SessionState.Connected or SessionState.Degraded }) return existing;
        return await Sessions.ConnectAsync(entry, null, ct).ConfigureAwait(false);
    }

    public Core.Discovery.ResourceKind ResolveKind(ClusterSession s, string kind) =>
        s.Discovery.FindByName(kind) ?? throw new McpException($"Unknown kind '{kind}'. Use list_kinds to see what this cluster serves.");

    /// <summary>Write tools: the flag, the cluster's safety mode and (production) the second flag plus typed confirmation all have to agree.</summary>
    public string? RequireWrite(ClusterSession s, string name, string? confirmName)
    {
        if (!Options.AllowWrites) throw new McpException("Write tools are disabled: start the server with --allow-writes.");
        var mode = s.Metadata.SafetyMode;
        if (mode == SafetyMode.Production)
        {
            if (!Options.AllowProduction) throw new McpException("This cluster is marked production: start the server with --allow-production to allow changes.");
            return confirmName;
        }
        return null;
    }
}
