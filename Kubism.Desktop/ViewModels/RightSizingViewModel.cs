using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.RightSizing;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

/// <summary>One resource (CPU or memory): what the nodes offer, what is used now and how requests and limits compare.</summary>
public sealed record ResourceCard(string Title, double Capacity, double UsedNow, double UsedP95, double Requested, double Limits, Func<double, string> Format)
{
    public string CapacityText => Capacity > 0 ? Format(Capacity) : "unknown";
    public string UsedNowText => Pct(UsedNow);
    public string RequestedText => Pct(Requested);
    public string LimitsText => Limits > 0 ? Pct(Limits) : "none set";
    public string UsedP95Text => Pct(UsedP95);
    public bool OverCommitted => Capacity > 0 && Requested > Capacity;
    public bool LimitsOver => Capacity > 0 && Limits > Capacity;
    public string Summary =>
        $"{Format(Capacity)} available · {UsedNowText} used now · {RequestedText} requested · limits {LimitsText}";
    private string Pct(double v) => Capacity > 0 ? $"{Format(v)} ({v / Capacity:P0})" : Format(v);
}

public sealed record SourceChoice(string Name, Func<IUsageHistorySource> Create);

public sealed partial class ContainerRowViewModel(ContainerReport r) : ViewModelBase
{
    public ContainerReport Report { get; } = r;
    [ObservableProperty] private bool _isSelected;
    public string Namespace => Report.Namespace;
    public string Workload => $"{Report.WorkloadKind}/{Report.WorkloadName}";
    public string Container => Report.Container;
    public int Replicas => Report.Replicas;
    public string CpuRequest => Report.CpuRequest is { } v ? Quantities.FormatCpu(v) : "—";
    public string CpuLimit => Report.CpuLimit is { } v ? Quantities.FormatCpu(v) : "—";
    public string CpuP95 => Quantities.FormatCpu(Report.CpuP95);
    public string CpuMax => Quantities.FormatCpu(Report.CpuMax);
    public string MemoryRequest => Report.MemoryRequest is { } v ? Quantities.FormatMemoryMi(v) : "—";
    public string MemoryLimit => Report.MemoryLimit is { } v ? Quantities.FormatMemoryMi(v) : "—";
    public string MemoryP95 => Quantities.FormatBytes(Report.MemoryP95);
    public string MemoryMax => Quantities.FormatBytes(Report.MemoryMax);
    public string RecCpu => Report.Recommended is { } r ? Quantities.FormatCpu(r.CpuRequest) : "—";
    public string RecMemory => Report.Recommended is { } r ? Quantities.FormatMemoryMi(r.MemoryRequest) : "—";
    public string Verdict => Report.Primary switch
    {
        Core.RightSizing.Verdict.OverProvisioned => "Over-provisioned", Core.RightSizing.Verdict.UnderProvisioned => "Under-provisioned",
        Core.RightSizing.Verdict.NoRequest => "No request", Core.RightSizing.Verdict.LimitTooTight => "Limit too tight",
        Core.RightSizing.Verdict.NotEnoughData => "Not enough data", _ => "OK",
    };
    public Severity VerdictSeverity => Report.Primary switch
    {
        Core.RightSizing.Verdict.OverProvisioned => Severity.Warning, Core.RightSizing.Verdict.Ok => Severity.Ok,
        Core.RightSizing.Verdict.NotEnoughData => Severity.Neutral, _ => Severity.Error,
    };
    public double CpuRequestV => Report.CpuRequest ?? 0;
    public double CpuLimitV => Report.CpuLimit ?? 0;
    public double CpuP95V => Report.CpuP95;
    public double CpuMaxV => Report.CpuMax;
    public double MemRequestV => Report.MemoryRequest ?? 0;
    public double MemLimitV => Report.MemoryLimit ?? 0;
    public double MemP95V => Report.MemoryP95;
    public double MemMaxV => Report.MemoryMax;
    public string Waste => Report.WasteCpuCores > 0 || Report.WasteMemoryBytes > 0 ? $"{Quantities.FormatCpu(Report.WasteCpuCores)} · {Quantities.FormatBytes(Report.WasteMemoryBytes)}" : "";
    public string? GitOps => Report.GitOpsManager;
    public bool IsProblem => Report.Primary is Core.RightSizing.Verdict.UnderProvisioned or Core.RightSizing.Verdict.LimitTooTight or Core.RightSizing.Verdict.NoRequest or Core.RightSizing.Verdict.OverProvisioned;

    /// <summary>The misconfiguration in one plain sentence.</summary>
    public string Problem
    {
        get
        {
            var r = Report; var p = new List<string>();
            if (r.CpuVerdict == Core.RightSizing.Verdict.NoRequest) p.Add("no CPU request");
            if (r.MemoryVerdict == Core.RightSizing.Verdict.NoRequest) p.Add("no memory request");
            if (r.CpuVerdict == Core.RightSizing.Verdict.UnderProvisioned) p.Add(r.ThrottledRatio > 0.1 ? $"CPU throttled {r.ThrottledRatio:P0}" : $"CPU p95 {CpuP95} is above its request {CpuRequest}");
            if (r.MemoryVerdict == Core.RightSizing.Verdict.UnderProvisioned) p.Add(r.OomKills > 0 ? $"OOMKilled {r.OomKills}×" : $"memory p95 {MemoryP95} is above its request {MemoryRequest}");
            if (r.MemoryVerdict == Core.RightSizing.Verdict.LimitTooTight && r.MemoryLimit is > 0) p.Add($"memory peak {MemoryMax} is {r.MemoryMax / r.MemoryLimit.Value:P0} of the limit {MemoryLimit}");
            if (r.CpuVerdict == Core.RightSizing.Verdict.OverProvisioned) p.Add($"CPU request {CpuRequest}, uses {CpuP95}");
            if (r.MemoryVerdict == Core.RightSizing.Verdict.OverProvisioned) p.Add($"memory request {MemoryRequest}, uses {MemoryP95}");
            return string.Join(" · ", p);
        }
    }

    public string Fix => Report.Recommended is null ? "" : $"CPU {RecCpu} · memory {RecMemory}";
    public double Risk => Report.Primary switch { Core.RightSizing.Verdict.UnderProvisioned => 3, Core.RightSizing.Verdict.LimitTooTight => 2, Core.RightSizing.Verdict.NoRequest => 1, _ => 0 };
}

/// <summary>Requests/limits vs real usage over days, verdicts, waste and the corrected resources block.</summary>
public sealed partial class RightSizingViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;
    private readonly List<InformerLease> _leases = [];
    private CancellationTokenSource _cts = new();
    private RightSizingReport? _report;
    private UsageHistory? _history;
    private bool _started;

    public RightSizingViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        var s = ws.Main.Settings.Current;
        WindowDays = s.RightSizingWindowDays;
        ResetSources();
        _started = true;
        _ = LoadAsync();
    }

    public int[] WindowChoices { get; } = [1, 7, 14, 30];
    public ObservableCollection<SourceChoice> Sources { get; } = [];
    public ObservableCollection<ContainerRowViewModel> Rows { get; } = [];
    /// <summary>The misconfigured containers only, worst first: the default view.</summary>
    public ObservableCollection<ContainerRowViewModel> Problems { get; } = [];
    [ObservableProperty] private ResourceCard? _cpuCard;
    [ObservableProperty] private ResourceCard? _memoryCard;
    [ObservableProperty] private string _problemsTitle = "";
    public ObservableCollection<NamespaceRollup> Rollups { get; } = [];
    public string[] VerdictFilters { get; } = ["All", "Over-provisioned", "Under-provisioned", "No request", "Limit too tight", "Not enough data", "OK"];
    public string[] SortChoices { get; } = ["Waste", "Risk", "Name"];

    [ObservableProperty] private SourceChoice? _selectedSource;
    [ObservableProperty] private int _windowDays = 7;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _sourceInfo = "";
    [ObservableProperty] private string _strategyInfo = "";
    [ObservableProperty] private string _verdictFilter = "All";
    [ObservableProperty] private string _sortBy = "Waste";
    [ObservableProperty] private string _kindFilter = "";
    [ObservableProperty] private double _minWasteCores;
    [ObservableProperty] private ContainerRowViewModel? _selected;
    [ObservableProperty] private string _patchPreview = "";
    [ObservableProperty] private bool _overrideGitOps;
    [ObservableProperty] private string? _applyBlockedReason;
    [ObservableProperty] private string _cpuKpi = "", _memoryKpi = "", _wasteKpi = "", _costKpi = "";
    [ObservableProperty] private int _underCount, _noRequestCount;
    [ObservableProperty] private string _connectionResult = "";
    [ObservableProperty] private IReadOnlyList<double> _cpuSeries = [];
    [ObservableProperty] private string _seriesNote = "";
    [ObservableProperty] private IReadOnlyList<double> _memorySeries = [];
    public int SelectedCount => Rows.Count(r => r.IsSelected);

    private void ResetSources()
    {
        Sources.Clear();
        var meta = _ws.Session.Metadata;
        foreach (var c in RightSizingRunner.Sources(_ws)) Sources.Add(c);
        SelectedSource = Sources[0].Name.StartsWith("Prometheus (in-cluster") && !meta.LocalSampling ? Sources[0] : meta.LocalSampling && string.IsNullOrWhiteSpace(meta.PrometheusUrl) ? Sources[^1] : Sources[0];
    }

    partial void OnWindowDaysChanged(int value) { if (_started) _ = LoadAsync(); }
    partial void OnSelectedSourceChanged(SourceChoice? value) { if (_started) _ = LoadAsync(); }
    partial void OnVerdictFilterChanged(string value) => ApplyView();
    partial void OnSortByChanged(string value) => ApplyView();
    partial void OnKindFilterChanged(string value) => ApplyView();
    partial void OnMinWasteCoresChanged(double value) => ApplyView();
    partial void OnOverrideGitOpsChanged(bool value) => UpdateSelection();

    private RightSizingEngine Engine() => RightSizingRunner.Engine(_ws);

    [RelayCommand]
    public async Task LoadAsync()
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _ws.Session.SessionToken).Token;
        IsLoading = true; Error = null;
        try
        {
            var source = SelectedSource ?? throw new InvalidOperationException("No usage source");
            var ns = _ws.Namespaces.IsAll ? null : _ws.Namespaces.Selected.ToList();
            var src = await Task.Run(source.Create, ct);
            var (report, history) = await RightSizingRunner.RunAsync(_ws, src, WindowDays, ns, ct);
            _history = history;
            _report = report;
            SourceInfo = $"{history.SourceName} · {WindowDays} d window · coverage {history.Coverage:P0} ({history.DataHours:0.#} h of data)" + (history.Note is null ? "" : $" · {history.Note}");
            StrategyInfo = $"Strategy: {_report.Strategy}";
            Summarize(_report);
            await BuildCardsAsync(_report, ct);
            ApplyView();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; _report = null; Rows.Clear(); }
        finally { IsLoading = false; }
    }

    private async Task BuildCardsAsync(RightSizingReport r, CancellationToken ct)
    {
        var m = _ws.Session.Metrics;
        double cpuNow = 0, memNow = 0;
        if (m is not null)
        {
            try { await m.PollOnceAsync(ct); } catch (Exception) { /* metrics-server missing: card shows no live usage */ }
            cpuNow = m.Pods.Values.Sum(p => p.CpuCores); memNow = m.Pods.Values.Sum(p => p.MemoryBytes);
        }
        CpuCard = new ResourceCard("CPU", r.NodeCpuAllocatable, cpuNow, r.CpuUsed, r.CpuRequested, r.CpuLimits, Quantities.FormatCpu);
        MemoryCard = new ResourceCard("Memory", r.NodeMemoryAllocatable, memNow, r.MemoryUsed, r.MemoryRequested, r.MemoryLimits, Quantities.FormatBytes);
    }

    private void Summarize(RightSizingReport r)
    {
        CpuKpi = $"{Quantities.FormatCpu(r.CpuRequested)} requested · {Quantities.FormatCpu(r.CpuUsed)} used (p95)" + (r.NodeCpuAllocatable > 0 ? $" · {Quantities.FormatCpu(r.NodeCpuAllocatable)} allocatable ({r.CpuRequested / r.NodeCpuAllocatable:P0} requested)" : "");
        MemoryKpi = $"{Quantities.FormatBytes(r.MemoryRequested)} requested · {Quantities.FormatBytes(r.MemoryUsed)} used (p95)" + (r.NodeMemoryAllocatable > 0 ? $" · {Quantities.FormatBytes(r.NodeMemoryAllocatable)} allocatable ({r.MemoryRequested / r.NodeMemoryAllocatable:P0} requested)" : "");
        WasteKpi = $"{Quantities.FormatCpu(r.WasteCpu)} cores · {Quantities.FormatBytes(r.WasteMemory)}";
        CostKpi = r.WasteCostPerMonth is { } c ? $"≈ {c:0.##} / month" : "";
        UnderCount = r.UnderProvisioned; NoRequestCount = r.WithoutRequests;
        Rollups.Clear(); foreach (var n in r.Namespaces) Rollups.Add(n);
    }

    private void ApplyView()
    {
        if (_report is null) return;
        IEnumerable<ContainerRowViewModel> q = _report.Containers.Select(c => new ContainerRowViewModel(c));
        if (VerdictFilter != "All") q = q.Where(r => r.Verdict == VerdictFilter);
        if (!string.IsNullOrWhiteSpace(KindFilter)) q = q.Where(r => r.Workload.Contains(KindFilter, StringComparison.OrdinalIgnoreCase) || r.Namespace.Contains(KindFilter, StringComparison.OrdinalIgnoreCase));
        if (MinWasteCores > 0) q = q.Where(r => r.Report.WasteCpuCores >= MinWasteCores);
        q = SortBy switch
        {
            "Risk" => q.OrderByDescending(r => r.Risk).ThenByDescending(r => r.Report.WasteCpuCores),
            "Name" => q.OrderBy(r => r.Namespace).ThenBy(r => r.Workload),
            _ => q.OrderByDescending(r => r.Report.WasteCpuCores).ThenByDescending(r => r.Report.WasteMemoryBytes),
        };
        Rows.Clear(); Problems.Clear();
        foreach (var r in q) { r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ContainerRowViewModel.IsSelected)) UpdateSelection(); }; Rows.Add(r); }
        // Worst first: under-provisioned, tight limits, no request, then wasteful.
        foreach (var r in Rows.Where(r => r.IsProblem).OrderByDescending(r => r.Risk).ThenByDescending(r => r.Report.WasteCpuCores)) Problems.Add(r);
        ProblemsTitle = Problems.Count == 0 ? "Nothing misconfigured" : $"{Problems.Count} misconfigured container(s)";
        UpdateSelection();
    }

    partial void OnSelectedChanged(ContainerRowViewModel? value)
    {
        UpdatePreview();
        if (value is null || _history is null) { CpuSeries = []; MemorySeries = []; SeriesNote = value is null ? "" : "No usage history for this container."; return; }
        var pts = _history.Series.Where(s => s.Key.Namespace == value.Report.Namespace && s.Key.Container == value.Report.Container
                                              && s.Key.Pod.StartsWith(value.Report.WorkloadName)).SelectMany(s => s.Points).GroupBy(p => p.Time).OrderBy(g => g.Key).ToList();
        CpuSeries = pts.Select(g => g.Average(p => p.CpuCores)).ToList();
        MemorySeries = pts.Select(g => g.Average(p => p.MemoryBytes)).ToList();
        SeriesNote = pts.Count < 2 ? "No usage history for this container in the window." : "";
    }

    /// <summary>
    /// The preview follows what the user is looking at: the ticked containers when there are some, else the clicked one. A clicked container
    /// with no recommendation (not enough data) says so instead of leaving the previous container's patch on screen.
    /// </summary>
    private void UpdatePreview()
    {
        var ticked = SelectedReports();
        if (ticked.Count > 0) { PatchPreview = string.Join("\n", ticked.Select(c => $"{c.Namespace}/{c.WorkloadKind}/{c.WorkloadName}\n{Snippets.Diff(c)}")); return; }
        if (Selected is not { } s) { PatchPreview = ""; return; }
        var c = s.Report;
        PatchPreview = c.Recommended is null
            ? $"{c.Namespace}/{c.WorkloadKind}/{c.WorkloadName}\n  container: {c.Container}\n  No recommendation: {s.Verdict.ToLowerInvariant()} (data coverage {c.Coverage:P0}, {c.DataHours:0.#} h)."
            : $"{c.Namespace}/{c.WorkloadKind}/{c.WorkloadName}\n{Snippets.Diff(c)}";
    }

    private List<ContainerReport> SelectedReports() => Rows.Where(r => r.IsSelected && r.Report.Recommended is not null).Select(r => r.Report).ToList();

    private void UpdateSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        UpdatePreview();
        var sel = SelectedReports();
        ApplyBlockedReason = null;
        if (sel.Count == 0) { ApplyBlockedReason = "Select containers to apply"; return; }
        var managed = sel.FirstOrDefault(c => c.GitOpsManager is not null);
        if (managed is not null && !OverrideGitOps)
            ApplyBlockedReason = $"Will be reverted by {managed.GitOpsManager}: copy the snippet instead (or tick override)";
        var unsupported = sel.FirstOrDefault(c => c.WorkloadKind is not ("Deployment" or "StatefulSet" or "DaemonSet"));
        if (unsupported is not null) ApplyBlockedReason = $"Direct apply is not supported for {unsupported.WorkloadKind}: copy the snippet instead";
        if (_ws.Session.Metadata.SafetyMode == Core.Catalog.SafetyMode.ReadOnly) ApplyBlockedReason = "forbidden: this cluster is read-only";
    }

    [RelayCommand]
    private void SelectAllVisible() { foreach (var r in Rows) if (r.Report.Recommended is not null) r.IsSelected = true; }
    [RelayCommand]
    private void ClearSelection() { foreach (var r in Rows) r.IsSelected = false; }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        UpdateSelection();
        if (ApplyBlockedReason is not null) { await _ws.Dialogs.ShowErrorAsync("Cannot apply", ApplyBlockedReason); return; }
        var sel = SelectedReports();
        var workloads = sel.GroupBy(c => (c.Namespace, c.WorkloadKind, c.WorkloadName)).ToList();
        var mode = _ws.Session.Metadata.SafetyMode;
        var summary = $"Set requests/limits on {sel.Count} container(s) in {workloads.Count} workload(s).\nThis triggers a rollout (pods are replaced).";
        string? typed = null;
        if (mode == Core.Catalog.SafetyMode.Production)
        {
            var expected = workloads.Count == 1 ? workloads[0].Key.WorkloadName : workloads.Count.ToString();
            typed = await _ws.Dialogs.ConfirmTypedAsync("Apply right-sizing (production)", summary, expected);
            if (typed is null) return;
        }
        else if (!await _ws.Dialogs.ConfirmAsync(new Services.ConfirmRequest("Apply right-sizing", summary, "Apply", true))) return;

        foreach (var w in workloads)
        {
            var gvr = w.Key.WorkloadKind switch { "Deployment" => Gvr.Deployments, "StatefulSet" => Gvr.StatefulSets, _ => Gvr.DaemonSets };
            var res = w.Select(c => new ContainerResources(c.Container, Quantities.FormatCpu(c.Recommended!.CpuRequest), c.Recommended.CpuLimit is { } cl ? Quantities.FormatCpu(cl) : null,
                Quantities.FormatMemoryMi(c.Recommended.MemoryRequest), c.Recommended.MemoryLimit is { } ml ? Quantities.FormatMemoryMi(ml) : null)).ToList();
            try { await _ws.Session.Actions.SetResourcesAsync(gvr, w.Key.Namespace, w.Key.WorkloadName, res, _ws.Session.SessionToken, typed is null ? null : w.Key.WorkloadName); }
            catch (Exception ex) { await _ws.Dialogs.ShowErrorAsync($"Failed on {w.Key.WorkloadName}", ex.Message); return; }
        }
        _ws.Main.SetStatus($"Applied right-sizing to {workloads.Count} workload(s)");
        await LoadAsync();
    }

    [RelayCommand] private Task CopyYaml() => _ws.Clipboard.SetTextAsync(Snippets.Yaml(SelectedReports()));
    [RelayCommand] private Task CopyHelm() => _ws.Clipboard.SetTextAsync(Snippets.HelmValues(SelectedReports()));
    [RelayCommand] private Task CopyKustomize() => _ws.Clipboard.SetTextAsync(Snippets.KustomizePatch(SelectedReports()));

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (_report is null) return;
        var path = await _ws.Dialogs.SaveFileAsync("Export CSV", "rightsizing.csv");
        if (path is not null) await File.WriteAllTextAsync(path, Snippets.Csv(_report));
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (_report is null) return;
        var path = await _ws.Dialogs.SaveFileAsync("Export Markdown", "rightsizing.md");
        if (path is not null) await File.WriteAllTextAsync(path, Snippets.Markdown(_report, _ws.Title));
    }

    [RelayCommand]
    private async Task CreateVpaAsync()
    {
        var sel = SelectedReports().GroupBy(c => (c.Namespace, c.WorkloadKind, c.WorkloadName)).ToList();
        foreach (var w in sel)
            await _ws.RunActionAsync("Create VPA", w.Key.WorkloadName, false, w.Key.WorkloadName, _ => _ws.Session.Actions.CreateVpaAsync(w.Key.Namespace, w.Key.WorkloadKind, w.Key.WorkloadName, _ws.Session.SessionToken));
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        try
        {
            var src = await Task.Run(() => SelectedSource!.Create());
            ConnectionResult = await src.TestAsync(_cts.Token);
        }
        catch (Exception ex) { ConnectionResult = ex.Message; }
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
}
