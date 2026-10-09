using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Events;

namespace Kubism.Core.Sessions;

public interface IClusterSessionManager : IAsyncDisposable
{
    IReadOnlyList<ClusterSession> Sessions { get; }
    ClusterSession? Get(string clusterId);
    Task<ClusterSession> ConnectAsync(ClusterEntry entry, IExecPluginObserver? observer = null, CancellationToken ct = default);
    Task DisconnectAsync(string clusterId);
    event Action? SessionsChanged;
}

public sealed class ClusterSessionManager(IKubeTransportFactory factory, IClusterEventsService events, ISettingsStore settings) : IClusterSessionManager
{
    private readonly Dictionary<string, ClusterSession> _sessions = [];
    private readonly object _gate = new();

    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(30);
    public event Action? SessionsChanged;

    public IReadOnlyList<ClusterSession> Sessions
    {
        get { lock (_gate) return [.. _sessions.Values]; }
    }

    public ClusterSession? Get(string clusterId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(clusterId);
    }

    public async Task<ClusterSession> ConnectAsync(ClusterEntry entry, IExecPluginObserver? observer = null, CancellationToken ct = default)
    {
        ClusterSession session;
        lock (_gate)
        {
            if (_sessions.TryGetValue(entry.Id, out var existing) && existing.State is not (SessionState.Error or SessionState.Disconnected))
                return existing;
            session = new ClusterSession(entry, factory, events, settings, HealthInterval, observer);
            _sessions[entry.Id] = session;
        }
        SessionsChanged?.Invoke();
        try { await session.ConnectAsync(ct).ConfigureAwait(false); }
        catch
        {
            // Keep the failed session so the UI can show its error until the user retries or closes it.
            SessionsChanged?.Invoke();
            throw;
        }
        return session;
    }

    public async Task DisconnectAsync(string clusterId)
    {
        ClusterSession? s;
        lock (_gate) { _sessions.Remove(clusterId, out s); }
        if (s is not null) await s.DisposeAsync().ConfigureAwait(false);
        SessionsChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        List<ClusterSession> all;
        lock (_gate) { all = [.. _sessions.Values]; _sessions.Clear(); }
        foreach (var s in all) await s.DisposeAsync().ConfigureAwait(false);
    }
}
