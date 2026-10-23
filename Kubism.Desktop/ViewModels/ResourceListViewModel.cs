using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Discovery;
using Kubism.Core.Table;
using Kubism.Core.Views;
using Kubism.Desktop.Navigation;
using Kubism.Desktop.Services;

namespace Kubism.Desktop.ViewModels;

/// <summary>What a list showed when the user left it.</summary>
public sealed record ListState(string Filter, int SortColumn, bool SortDescending, bool ShowAllColumns, string? SelectedNamespace, string? SelectedName);

/// <summary>Live list of one kind. Rows are updated in place from informer batches; sorting and filtering are client-side.</summary>
public sealed partial class ResourceListViewModel : ViewModelBase, IClosable, INavigablePage
{
    private const int CpuColumn = -10, MemoryColumn = -11;
    private readonly WorkspaceViewModel _ws;
    private readonly IUiThread _ui;
    private readonly Dictionary<string, ResourceRowViewModel> _rows = [];
    private readonly List<InformerLease> _leases = [];
    private readonly List<IDisposable> _subs = [];
    private IDisposable? _metricsLease;
    private bool _closed;
    private int _refreshQueued;
    private IReadOnlyList<ColumnDef> _allColumns = [];
    private (string? ns, string name)? _pendingSelect;
    private bool _hasApplied;

    /// <summary>Raised with the row to scroll to after it was selected by code (Back, or a link from another page).</summary>
    public event Action<ResourceRowViewModel>? RevealRequested;

    public object? CaptureState() => new ListState(Filter, SortColumn, SortDescending, ShowAllColumns, SelectedRow?.Namespace, SelectedRow?.Name);

    /// <summary>Selects (and opens in the details panel) an object as soon as the list has it.</summary>
    public void SelectWhenLoaded(string? ns, string name) { _pendingSelect = (ns, name); TryApplyPendingSelect(); }

    private void TryApplyPendingSelect()
    {
        if (_pendingSelect is not { } p) return;
        var row = Rows.FirstOrDefault(r => r.Name == p.name && r.Namespace == p.ns);
        if (row is not null) { _pendingSelect = null; SelectedRow = row; RevealRequested?.Invoke(row); }
        else if (_hasApplied && IsLive && Rows.Count == _rows.Count) _pendingSelect = null;   // data arrived, and the object is not in it (deleted since)
    }

    public ResourceListViewModel(WorkspaceViewModel ws, ResourceKind kind, ListState? state = null)
    {
        _ws = ws; _ui = ws.Ui; Kind = kind;
        if (state is not null)
        {
            _filter = state.Filter; _sortColumn = state.SortColumn; _sortDescending = state.SortDescending; _showAllColumns = state.ShowAllColumns;
            if (state.SelectedName is not null) _pendingSelect = (state.SelectedNamespace, state.SelectedName);
        }
        ws.Namespaces.Changed += OnNamespacesChanged;
        Subscribe();
        if (kind.Kind is "Pod" or "Node")
        {
            _metricsLease = ws.Session.Metrics.Acquire();
            ws.Session.Metrics.Updated += OnMetrics;
        }
    }

    public ResourceKind Kind { get; }
    public string Title => Kind.Kind switch { "Endpoints" => "Endpoints", _ => Plural(Kind.Kind) };
    public WorkspaceViewModel Workspace => _ws;
    public RangeObservableCollection<ResourceRowViewModel> Rows { get; } = [];
    public IReadOnlyList<ColumnDef> Columns { get; private set; } = [];
    public event Action? ColumnsChanged;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private ResourceRowViewModel? _selectedRow;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isReconnecting;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _showAllColumns;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private int _sortColumn = -1;
    [ObservableProperty] private bool _sortDescending;
    public List<ResourceRowViewModel> SelectedRows { get; } = [];

    /// <summary>Called by the view after it filled <see cref="SelectedRows"/>.</summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(DeleteLabel)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(HasMultiSelection));
    }

    partial void OnSelectedRowChanged(ResourceRowViewModel? value) => NotifySelectionChanged();

    public int SelectedCount => SelectedRows.Count;
    public bool HasMultiSelection => SelectedRows.Count > 1;
    public bool HasSelection => SelectedRows.Count > 0 || SelectedRow is not null;
    public string DeleteLabel => SelectedRows.Count > 1 ? $"Delete {SelectedRows.Count} selected" : "Delete";

    public bool Namespaced => Kind.Namespaced;

    private static string Plural(string kind) => kind.EndsWith("s") ? kind : kind.EndsWith("y") ? kind[..^1] + "ies" : kind + "s";

    private void OnNamespacesChanged()
    {
        if (!Kind.Namespaced) return;
        _ui.Post(() => { if (_closed) return; Unsubscribe(); _rows.Clear(); Rows.ReplaceAll([]); Subscribe(); });
    }

    // ---------- subscription ----------

    private void Subscribe()
    {
        var selected = Kind.Namespaced ? _ws.Namespaces.Selected : [];
        IEnumerable<string?> scopes = selected.Count == 0 ? [null] : selected.Select(s => (string?)s);
        foreach (var ns in scopes)
        {
            InformerLease lease;
            try { lease = _ws.Session.Cache.Subscribe(Kind.Gvr, ns); }
            catch (InformerBudgetException ex) { ErrorText = ex.Message; StateText = "Error"; return; }
            _leases.Add(lease);
            var inf = lease.Informer;
            _subs.Add(Kubism.Core.Events.ObservableExtensions.Subscribe(inf.Changes, c => _ui.Post(() => Apply(inf, c))));
            inf.StateChanged += OnInformerState;
            if (inf.IsListed) _ui.Post(() => Apply(inf, new CacheChange(inf.Snapshot(), [], [], true)));
        }
        UpdateState();
    }

    private void Unsubscribe()
    {
        foreach (var s in _subs) s.Dispose();
        foreach (var l in _leases) { l.Informer.StateChanged -= OnInformerState; l.Dispose(); }
        _subs.Clear(); _leases.Clear();
    }

    private void OnInformerState(InformerState s) => _ui.Post(UpdateState);

    private void UpdateState()
    {
        if (_closed) return;
        var states = _leases.Select(l => l.Informer.State).ToList();
        IsLive = states.Count > 0 && states.All(s => s == InformerState.Live);
        IsReconnecting = states.Any(s => s is InformerState.Reconnecting);
        var failed = _leases.FirstOrDefault(l => l.Informer.State == InformerState.Failed);
        ErrorText = failed?.Informer.LastError;
        StateText = IsLive ? "Live".T() : IsReconnecting ? "Reconnecting…".T() : failed is not null ? "Error" : "Loading…";
    }

    // ---------- applying changes ----------

    private void Apply(Informer source, CacheChange change)
    {
        if (_closed || !_leases.Any(l => l.Informer == source)) return;
        if (change.Resynced)
        {
            // Relist diff: drop rows of this informer that are gone, but only those (other namespaces keep theirs).
            foreach (var d in change.Deleted) _rows.Remove(d.Object.Uid);
        }
        _hasApplied = true;
        EnsureColumns(source);
        foreach (var item in change.Added.Concat(change.Modified))
            Upsert(item, highlight: !change.Resynced);
        if (!change.Resynced)
            foreach (var d in change.Deleted)
                if (_rows.Remove(d.Object.Uid, out var row)) { _ws.Details.MarkDeleted(d.Object.Uid); row.IsDeleting = true; _ = Task.Delay(600).ContinueWith(_ => _ui.Post(() => RemoveRow(row)), TaskScheduler.Default); _deleting.Add(row); }
        UpdateState();
        QueueRefresh();
    }

    private readonly HashSet<ResourceRowViewModel> _deleting = [];

    private void RemoveRow(ResourceRowViewModel row)
    {
        _deleting.Remove(row);
        QueueRefresh();
    }

    private void Upsert(CacheItem item, bool highlight)
    {
        var uid = item.Object.Uid;
        if (_rows.TryGetValue(uid, out var row))
        {
            row.Update(item);
            Compute(row);
            _ws.Details.Update(item.Object);
        }
        else
        {
            row = new ResourceRowViewModel(item);
            Compute(row);
            _rows[uid] = row;
            if (highlight)
            {
                row.IsNew = true;
                _ = Task.Delay(3000).ContinueWith(_ => _ui.Post(() => row.IsNew = false), TaskScheduler.Default);
            }
        }
    }

    private void EnsureColumns(Informer source)
    {
        var server = source.Columns;
        var cols = TableService.Columns(server, Kind.Namespaced).ToList();
        if (Kind.Kind == "Pod") { cols.Add(new ColumnDef("CPU", "string", 0, false, CpuColumn)); cols.Add(new ColumnDef("Memory", "string", 0, false, MemoryColumn)); }
        else if (Kind.Kind == "Node") { cols.Add(new ColumnDef("CPU usage", "string", 0, false, CpuColumn)); cols.Add(new ColumnDef("Memory usage", "string", 0, false, MemoryColumn)); }
        if (_allColumns.Count == cols.Count && _allColumns.Zip(cols).All(p => p.First == p.Second)) return;
        _allColumns = cols;
        ApplyColumnVisibility();
    }

    partial void OnShowAllColumnsChanged(bool value) { ApplyColumnVisibility(); foreach (var r in _rows.Values) Compute(r); QueueRefresh(); }

    private void ApplyColumnVisibility()
    {
        Columns = ShowAllColumns ? _allColumns : _allColumns.Where(c => c.Priority == 0).ToList();
        ColumnsChanged?.Invoke();
        foreach (var r in _rows.Values) Compute(r);
    }

    private void Compute(ResourceRowViewModel row)
    {
        var skew = _ws.Session.ClockSkew;
        var cells = new string[Columns.Count];
        var tips = new string?[Columns.Count];
        for (var i = 0; i < cells.Length; i++)
        {
            var c = Columns[i];
            if (c.Index is CpuColumn or MemoryColumn) (cells[i], tips[i]) = MetricCell(row, c.Index == CpuColumn);
            else cells[i] = TableService.Cell(row.Item, c, skew);
        }
        row.Tips = tips;
        row.Cells = cells;
        row.Severity = ResourceRowViewModel.SeverityOf(Kind.Kind, row.Item);
    }

    /// <summary>Tooltip of a CPU / Memory column header; null for every other column.</summary>
    public string? HeaderTip(ColumnDef col) => col.Index switch
    {
        CpuColumn or MemoryColumn when Kind.Kind == "Pod" => $"Current {(col.Index == CpuColumn ? "CPU" : "memory")} usage (% of the pod's requests, % of its limits). – when none is set.",
        CpuColumn or MemoryColumn when Kind.Kind == "Node" => $"Current {(col.Index == CpuColumn ? "CPU" : "memory")} usage (% of the node's allocatable).",
        _ => null,
    };

    private static string Pct(double used, double of) => of > 0 ? $"{used / of * 100:0}%" : "–";

    private static string Bytes(double b) => b >= 1024 * 1024 && b < 1024.0 * 1024 * 1024 ? $"{b / (1024 * 1024):0}Mi" : Quantities.FormatBytes(b);

    private (string text, string? tip) MetricCell(ResourceRowViewModel row, bool cpu)
    {
        var m = _ws.Session.Metrics;
        if (m is null || m.Available != true) return ("", null);
        if (Kind.Kind == "Pod")
        {
            if (!m.Pods.TryGetValue($"{row.Namespace}/{row.Name}", out var u)) return ("", null);
            var cs = new PodView(row.Item.Object).Containers.Where(c => !c.IsInit).ToList();
            var used = cpu ? u.CpuCores : u.MemoryBytes;
            var req = cs.Sum(c => cpu ? Quantities.ParseCpu(c.CpuRequest) : Quantities.ParseBytes(c.MemoryRequest));
            // A limit only means something when every container has one; otherwise the pod is unbounded.
            var limits = cs.Select(c => cpu ? Quantities.ParseCpu(c.CpuLimit) : Quantities.ParseBytes(c.MemoryLimit)).ToList();
            var lim = limits.Count > 0 && limits.All(l => l > 0) ? limits.Sum() : 0;
            string F(double v) => cpu ? Quantities.FormatCpu(v) : Bytes(v);
            var usedText = F(used);
            if (req <= 0 && lim <= 0) return (usedText, $"{usedText} · no request, no limit");
            var text = $"{usedText} ({Pct(used, req)}, {Pct(used, lim)})";
            var tip = $"{(cpu ? "CPU" : "Memory")} {usedText}\nrequest {(req > 0 ? $"{F(req)} ({Pct(used, req)})" : "–")}\nlimit {(lim > 0 ? $"{F(lim)} ({Pct(used, lim)})" : "–")}";
            return (text, tip);
        }
        if (!m.Nodes.TryGetValue(row.Name, out var n)) return ("", null);
        var nv = new NodeView(row.Item.Object);
        var text2 = cpu
            ? $"{Quantities.FormatCpu(n.CpuCores)} ({(nv.CpuAllocatable > 0 ? n.CpuCores / nv.CpuAllocatable : 0):P0})"
            : $"{Quantities.FormatBytes(n.MemoryBytes)} ({(nv.MemoryAllocatable > 0 ? n.MemoryBytes / nv.MemoryAllocatable : 0):P0})";
        return (text2, null);
    }

    private void OnMetrics() => _ui.Post(() =>
    {
        if (_closed || (!Columns.Any(c => c.Index == CpuColumn))) return;
        foreach (var r in _rows.Values) Compute(r);
    });

    /// <summary>Recomputes age cells; called by the workspace timer for the visible list only.</summary>
    public void TickAges()
    {
        if (_closed) return;
        var idx = new List<int>();
        for (var i = 0; i < Columns.Count; i++) if (Columns[i].IsAge) idx.Add(i);
        if (idx.Count == 0) return;
        var skew = _ws.Session.ClockSkew;
        foreach (var r in _rows.Values)
        {
            var cells = (string[])r.Cells.Clone();
            var changed = false;
            foreach (var i in idx)
            {
                var v = TableService.Cell(r.Item, Columns[i], skew);
                if (cells[i] != v) { cells[i] = v; changed = true; }
            }
            if (changed) r.Cells = cells;
        }
    }

    // ---------- view (filter + sort) ----------

    partial void OnFilterChanged(string value) => QueueRefresh();
    partial void OnSortColumnChanged(int value) => QueueRefresh();
    partial void OnSortDescendingChanged(bool value) => QueueRefresh();

    public void ToggleSort(int columnIndex)
    {
        if (SortColumn == columnIndex) SortDescending = !SortDescending; else { SortColumn = columnIndex; SortDescending = false; }
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() => { Interlocked.Exchange(ref _refreshQueued, 0); RefreshView(); });
    }

    public void RefreshView()
    {
        if (_closed) return;
        IEnumerable<ResourceRowViewModel> q = _rows.Values.Where(r => TableService.Matches(r.Item, Filter));
        // Rows being deleted stay visible (faded) until removed.
        q = q.Concat(_deleting.Where(d => TableService.Matches(d.Item, Filter)));
        var cols = Columns;
        List<ResourceRowViewModel> sorted;
        if (SortColumn >= 0 && SortColumn < cols.Count)
        {
            var col = cols[SortColumn];
            sorted = SortDescending ? q.OrderByDescending(r => SortKeyOf(r, col)).ToList() : q.OrderBy(r => SortKeyOf(r, col)).ToList();
        }
        else
            sorted = q.OrderBy(r => r.Namespace, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        Rows.SyncTo(sorted);
        TryApplyPendingSelect();
        CountText = Filter.Length > 0 ? $"{sorted.Count} of {_rows.Count}" : $"{_rows.Count}";
    }

    private IComparable SortKeyOf(ResourceRowViewModel r, ColumnDef col)
    {
        if (col.Index is CpuColumn or MemoryColumn)
        {
            var text = r.Cells.Length > Columns.ToList().IndexOf(col) ? r.Cells[Columns.ToList().IndexOf(col)] : "";
            return text;
        }
        return TableService.SortKey(r.Item, col);
    }

    // ---------- commands ----------

    [RelayCommand]
    private void Refresh()
    {
        // The informer keeps the data live; Refresh re-subscribes to force a fresh list.
        Unsubscribe(); _rows.Clear(); Rows.ReplaceAll([]); Subscribe();
    }

    [RelayCommand]
    private Task DeleteSelected() => _ws.DeleteAsync(this, SelectedRows.Count > 0 ? [.. SelectedRows] : SelectedRow is null ? [] : [SelectedRow]);

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        _ws.Namespaces.Changed -= OnNamespacesChanged;
        if (_ws.Session.Metrics is { } mm) mm.Updated -= OnMetrics;
        _metricsLease?.Dispose();
        Unsubscribe();
        await Task.CompletedTask;
    }
}
