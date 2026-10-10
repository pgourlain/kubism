using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Cache;

public sealed record CacheItem(KubeObject Object, IReadOnlyList<string?>? Cells);

public enum InformerState { Idle, Listing, Live, Reconnecting, Failed, Stopped }

/// <summary>A batch of changes. <see cref="Resynced"/> batches come from a (re)list diff, not from individual watch events.</summary>
public sealed record CacheChange(IReadOnlyList<CacheItem> Added, IReadOnlyList<CacheItem> Modified, IReadOnlyList<CacheItem> Deleted, bool Resynced)
{
    public bool IsEmpty => Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0;
}

public sealed class InformerOptions
{
    public TimeSpan Coalesce { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan Linger { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(30);
    public int PageSize { get; init; } = 500;
}

/// <summary>list → watch → relist informer for one (GVR, namespace-or-all). Reference-counted by <see cref="ResourceCache"/>.</summary>
public sealed class Informer : IAsyncDisposable
{
    private readonly IKubeTransport _transport;
    private readonly InformerOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheItem> _store = [];
    private readonly EventStreamImpl _changes = new();
    private readonly Dictionary<string, (char kind, CacheItem item)> _pending = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _flushScheduled;
    private int _refs;
    private Timer? _lingerTimer;
    private bool _useTable = true;
    private string? _resourceVersion;

    public Informer(IKubeTransport transport, Gvr gvr, string? ns, InformerOptions? options = null)
    {
        _transport = transport;
        Gvr = gvr;
        Namespace = string.IsNullOrEmpty(ns) ? null : ns;
        _options = options ?? new InformerOptions();
    }

    public Gvr Gvr { get; }
    public string? Namespace { get; }
    public InformerState State { get; private set; } = InformerState.Idle;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }
    public IReadOnlyList<TableColumn>? Columns { get; private set; }
    public bool IsListed { get; private set; }
    public int Count { get { lock (_gate) return _store.Count; } }
    public int RefCount => Volatile.Read(ref _refs);
    public IObservable<CacheChange> Changes => _changes;
    public event Action<InformerState>? StateChanged;
    public event Action? Stopped;

    public IReadOnlyList<CacheItem> Snapshot()
    {
        lock (_gate) return [.. _store.Values];
    }

    internal IDisposable Acquire()
    {
        lock (_gate)
        {
            _refs++;
            _lingerTimer?.Dispose();
            _lingerTimer = null;
            if (_loop is null || State == InformerState.Stopped) Start();
        }
        return new Lease(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_refs > 0) return;
            _lingerTimer?.Dispose();
            _lingerTimer = new Timer(_ => StopIfUnused(), null, _options.Linger, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopIfUnused()
    {
        lock (_gate) { if (_refs > 0) return; }
        _ = StopAsync();
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    private async Task StopAsync()
    {
        CancellationTokenSource? cts; Task? loop;
        lock (_gate) { cts = _cts; loop = _loop; _cts = null; _loop = null; }
        try { cts?.Cancel(); } catch { }
        if (loop is not null) { try { await loop.ConfigureAwait(false); } catch { } }
        cts?.Dispose();
        SetState(InformerState.Stopped);
        Stopped?.Invoke();
    }

    private void SetState(InformerState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        var needList = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (needList)
                {
                    SetState(IsListed ? InformerState.Reconnecting : InformerState.Listing);
                    await ListAsync(ct).ConfigureAwait(false);
                    needList = false;
                }
                SetState(InformerState.Live);
                failures = 0;
                var sawEvent = false;
                await foreach (var ev in _transport.WatchAsync(Gvr, Namespace, _resourceVersion, _useTable, ct).ConfigureAwait(false))
                {
                    if (ev.Type == WatchEventType.Error)
                    {
                        if (ev.ErrorCode == 410) { needList = true; break; }
                        throw new KubeApiException(ev.ErrorCode ?? 500, null, ev.Object["message"]?.ToString() ?? "watch error");
                    }
                    sawEvent = true;
                    failures = 0;
                    Apply(ev);
                }
                if (!needList && !sawEvent) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200 * (failures + 1), 2000)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (KubeApiException ex) when (ex.IsGone) { needList = true; }
            catch (KubeApiException ex) when (_useTable && IsTableRefused(ex))
            {
                _useTable = false; needList = true;
            }
            catch (Exception ex)
            {
                failures++;
                LastError = ex.Message;
                AppLog.Warn($"Informer {Gvr.Resource} ns={Namespace ?? "*"} failure #{failures}: {ex.GetType().Name}: {ex.Message}");
                SetState(IsListed ? InformerState.Reconnecting : InformerState.Failed);
                if (failures >= 3) needList = true;
                var delay = Backoff(failures);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                if (!IsListed) SetState(InformerState.Failed);
            }
        }
        FlushNow();
    }

    private static bool IsTableRefused(KubeApiException ex) => ex.StatusCode is 406 or 415;

    private TimeSpan Backoff(int failures)
    {
        var ms = Math.Min(_options.BackoffBase.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 10)), _options.BackoffMax.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms * (0.5 + Random.Shared.NextDouble() * 0.5));
    }

    private async Task ListAsync(CancellationToken ct)
    {
        var items = new List<CacheItem>();
        string? cont = null;
        string? rv;
        List<TableColumn>? columns = null;
        do
        {
            var page = await _transport.ListAsync(Gvr, Namespace,
                new ListOptions { Limit = _options.PageSize, Continue = cont, AsTable = _useTable }, ct).ConfigureAwait(false);
            columns ??= page.Columns;
            for (var i = 0; i < page.Items.Count; i++)
                items.Add(new CacheItem(new KubeObject(page.Items[i]), page.Cells?[i]));
            cont = page.Continue;
            rv = page.ResourceVersion;
        } while (!string.IsNullOrEmpty(cont));

        Columns = columns;
        var first = !IsListed;
        CacheChange diff;
        lock (_gate)
        {
            var next = items.GroupBy(i => i.Object.Uid).ToDictionary(g => g.Key, g => g.Last());
            var added = new List<CacheItem>(); var modified = new List<CacheItem>(); var deleted = new List<CacheItem>();
            foreach (var (uid, item) in next)
            {
                if (!_store.TryGetValue(uid, out var old)) added.Add(item);
                else if (old.Object.ResourceVersion != item.Object.ResourceVersion) modified.Add(item);
            }
            foreach (var (uid, old) in _store) if (!next.ContainsKey(uid)) deleted.Add(old);
            _store.Clear();
            foreach (var (uid, item) in next) _store[uid] = item;
            _pending.Clear();
            diff = new CacheChange(added, modified, deleted, true);
        }
        _resourceVersion = rv;
        IsListed = true;
        LastEventAt = DateTimeOffset.UtcNow;
        if (first || !diff.IsEmpty) _changes.Push(diff);
    }

    private void Apply(WatchEvent ev)
    {
        LastEventAt = DateTimeOffset.UtcNow;
        var rv = ev.ResourceVersion;
        if (rv is not null) _resourceVersion = rv;
        if (ev.Type == WatchEventType.Bookmark) return;

        var obj = new KubeObject(ev.Object);
        var item = new CacheItem(obj, ev.Cells);
        lock (_gate)
        {
            var uid = obj.Uid;
            var known = _store.ContainsKey(uid);
            char kind;
            if (ev.Type == WatchEventType.Deleted)
            {
                if (!_store.Remove(uid, out var old)) return;
                kind = 'D';
                item = old with { Object = obj, Cells = ev.Cells ?? old.Cells };
            }
            else
            {
                _store[uid] = item;
                kind = known ? 'M' : 'A';
            }
            // Merge with what is already pending for this uid in the current batch.
            if (_pending.TryGetValue(uid, out var p))
            {
                if (p.kind == 'A' && kind == 'D') _pending.Remove(uid);
                else if (p.kind == 'A') _pending[uid] = ('A', item);
                else if (kind == 'D') _pending[uid] = ('D', item);
                else _pending[uid] = ('M', item);
            }
            else _pending[uid] = (kind, item);

            if (!_flushScheduled)
            {
                _flushScheduled = true;
                _ = Task.Delay(_options.Coalesce).ContinueWith(_ => FlushNow(), TaskScheduler.Default);
            }
        }
    }

    private void FlushNow()
    {
        CacheChange change;
        lock (_gate)
        {
            _flushScheduled = false;
            if (_pending.Count == 0) return;
            change = new CacheChange(
                [.. _pending.Values.Where(p => p.kind == 'A').Select(p => p.item)],
                [.. _pending.Values.Where(p => p.kind == 'M').Select(p => p.item)],
                [.. _pending.Values.Where(p => p.kind == 'D').Select(p => p.item)], false);
            _pending.Clear();
        }
        _changes.Push(change);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _lingerTimer?.Dispose(); _lingerTimer = null; }
        await StopAsync().ConfigureAwait(false);
    }

    private sealed class Lease(Informer owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(); }
    }

    private sealed class EventStreamImpl : IObservable<CacheChange>
    {
        private readonly Events.EventStream<CacheChange> _s = new();
        public void Push(CacheChange c) => _s.Push(c);
        public IDisposable Subscribe(IObserver<CacheChange> observer) => _s.Subscribe(observer);
    }
}
