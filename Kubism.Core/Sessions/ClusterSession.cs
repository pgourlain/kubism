using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Core.Events;
using Kubism.Core.Exec;
using Kubism.Core.Forward;
using Kubism.Core.Metrics;

namespace Kubism.Core.Sessions;

public enum SessionState { Disconnected, Connecting, Connected, Degraded, Error }

/// <summary>One connected context. Owns transport, discovery, cache and a root cancellation token.</summary>
public sealed class ClusterSession : IAsyncDisposable
{
    private readonly IKubeTransportFactory _factory;
    private readonly IClusterEventsService _events;
    private readonly ISettingsStore _settings;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _healthLoop;
    private IKubeTransport? _transport;
    private IExecPluginObserver? _observer;
    private readonly Helm.IHelmRunnerFactory? _helm;

    public ClusterSession(ClusterEntry entry, IKubeTransportFactory factory, IClusterEventsService events, ISettingsStore settings,
        TimeSpan? healthInterval = null, IExecPluginObserver? observer = null, Helm.IHelmRunnerFactory? helm = null)
    {
        Entry = entry;
        _factory = factory;
        _events = events;
        _settings = settings;
        _observer = observer;
        _helm = helm;
        HealthInterval = healthInterval ?? TimeSpan.FromSeconds(30);
        Activity = new ActivityLog();
    }

    public ClusterEntry Entry { get; }
    public TimeSpan HealthInterval { get; }
    public SessionState State { get; private set; } = SessionState.Disconnected;
    public string? ErrorMessage { get; private set; }
    public string? ServerVersion { get; private set; }
    public ActivityLog Activity { get; }
    public ClusterMetadata Metadata => _settings.GetMetadata(Entry.Id);
    /// <summary>Server clock minus local clock; zero when disconnected (UI timers may fire after a disconnect).</summary>
    public TimeSpan ClockSkew => _transport?.ClockSkew ?? default;
    public IKubeTransport Transport => _transport ?? throw new InvalidOperationException("Not connected");
    public ApiDiscovery Discovery { get; private set; } = null!;
    public ResourceCache Cache { get; private set; } = null!;
    public ClusterActions Actions { get; private set; } = null!;
    public AccessReview Access { get; private set; } = null!;
    public PortForwardManager PortForwards { get; private set; } = null!;
    public MetricsService Metrics { get; private set; } = null!;
    /// <summary>Cancelled when the session disconnects; views link their own tokens to it.</summary>
    public CancellationToken SessionToken => _cts?.Token ?? new CancellationToken(true);

    public event Action<SessionState>? StateChanged;

    private void SetState(SessionState s, string? error = null)
    {
        if (State == s && ErrorMessage == error) return;
        State = s;
        ErrorMessage = error;
        StateChanged?.Invoke(s);
        _events.Push(new SessionStateChanged(Entry.Id, s, error));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State is SessionState.Connected or SessionState.Connecting or SessionState.Degraded) return;
        SetState(SessionState.Connecting);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            _transport = _factory.Create(Entry, _observer);
            var version = await _transport.GetAsync("/version", linked.Token).ConfigureAwait(false);
            ServerVersion = version?["gitVersion"]?.GetValue<string>();
            Discovery = new ApiDiscovery(_transport);
            Cache = new ResourceCache(_transport, Entry.Id, _events) { MaxInformers = _settings.Current.MaxInformersPerCluster };
            Access = new AccessReview(_transport);
            PortForwards = new PortForwardManager(_transport, Entry.Id, _events);
            Metrics = new MetricsService(_transport);
            Actions = new ClusterActions(Entry.Id, Discovery, _transport, _events, Activity, () => Metadata.SafetyMode, () => PortForwards);
            Cache.HealthChanged += OnCacheHealth;
            if (_helm is not null) Actions.Helm = _helm.Create(Entry);
            await Discovery.RefreshAsync(linked.Token).ConfigureAwait(false);
            SetState(SessionState.Connected);
            _healthLoop = Task.Run(() => HealthLoopAsync(token));
        }
        catch (Exception ex)
        {
            var msg = Describe(ex);
            await TearDownAsync().ConfigureAwait(false);
            SetState(SessionState.Error, msg);
            throw;
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        AggregateException { InnerException: { } inner } => Describe(inner),
        OperationCanceledException => "Cancelled",
        KubeApiException { StatusCode: 401 } a => $"Authentication failed (401): {a.Message}",
        KubeApiException a => $"{a.StatusCode} {a.Reason}: {a.Message}",
        _ => ex.InnerException is not null && ex.Message.Length < 20 ? ex.InnerException.Message : ex.Message,
    };

    private void OnCacheHealth(bool healthy)
    {
        if (State == SessionState.Connected && !healthy) SetState(SessionState.Degraded, "Watch connection lost");
        else if (State == SessionState.Degraded && healthy) SetState(SessionState.Connected);
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var delay = failures == 0 ? HealthInterval : Backoff(failures, HealthInterval);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                var r = await _transport!.SendAsync("GET", "/readyz", null, "", ct).ConfigureAwait(false);
                if (r.StatusCode is >= 200 and < 300)
                {
                    failures = 0;
                    if (State == SessionState.Degraded) SetState(SessionState.Connected);
                }
                else throw new KubeApiException(r.StatusCode, null, $"readyz returned {r.StatusCode}");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                failures++;
                if (State == SessionState.Connected) SetState(SessionState.Degraded, ex.Message);
            }
        }
    }

    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay)
    {
        var ms = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 6)), 60_000);
        return TimeSpan.FromMilliseconds(ms * (0.5 + Random.Shared.NextDouble() * 0.5));
    }

    public async Task DisconnectAsync()
    {
        await TearDownAsync().ConfigureAwait(false);
        SetState(SessionState.Disconnected);
    }

    private async Task TearDownAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate) { cts = _cts; loop = _healthLoop; _cts = null; _healthLoop = null; }
        try { cts?.Cancel(); } catch { }
        if (PortForwards is not null) { await PortForwards.DisposeAsync().ConfigureAwait(false); PortForwards = null!; }
        if (Metrics is not null) { await Metrics.DisposeAsync().ConfigureAwait(false); Metrics = null!; }
        if (Cache is not null) { Cache.HealthChanged -= OnCacheHealth; await Cache.DisposeAsync().ConfigureAwait(false); }
        if (loop is not null) { try { await loop.ConfigureAwait(false); } catch { } }
        _transport?.Dispose();
        _transport = null;
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync().ConfigureAwait(false);
        if (State != SessionState.Disconnected) SetState(SessionState.Disconnected);
    }
}
