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
    [ObservableProperty] private IReadOnlyList<double> _memorySeries = [];
    public int SelectedCount => Rows.Count(r => r.IsSelected);

    private void ResetSources()
    {
        Sources.Clear();
        var meta = _ws.Session.Metadata;
        var tokens = _ws.Main.AzureTokens;
        if (!string.IsNullOrWhiteSpace(meta.PrometheusUrl) && Uri.TryCreate(meta.PrometheusUrl, UriKind.Absolute, out var uri))
        {
            var azure = uri.Host.EndsWith("prometheus.monitor.azure.com", StringComparison.OrdinalIgnoreCase);
            Sources.Add(new SourceChoice(azure ? "Azure Monitor managed Prometheus" : "Prometheus",
                () => new PrometheusUsageSource(new HttpPromClient(uri, azure ? tokens : null, azure ? "https://prometheus.monitor.azure.com/.default" : null), azure ? "Azure Monitor managed Prometheus" : "Prometheus")));
        }
        Sources.Add(new SourceChoice("Prometheus (in-cluster, API server proxy)", () => AutoDetectPrometheus()));
        Sources.Add(new SourceChoice("VPA recommender", () => new VpaRecommendationSource(_ws.Session.Transport)));
        Sources.Add(new SourceChoice("Local sampling", () => new LocalSamplerSource(_ws.Session.Entry.Id, _ws.Main.UsageStore)));
        SelectedSource = Sources[0].Name.StartsWith("Prometheus (in-cluster") && !meta.LocalSampling ? Sources[0] : meta.LocalSampling && string.IsNullOrWhiteSpace(meta.PrometheusUrl) ? Sources[^1] : Sources[0];
    }

    private IUsageHistorySource AutoDetectPrometheus() =>
        PrometheusDiscovery.FindAsync(_ws.Session.Transport, _cts.Token).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("No in-cluster Prometheus found. Set a Prometheus URL in the cluster settings, or use local sampling.");

    partial void OnWindowDaysChanged(int value) { if (_started) _ = LoadAsync(); }
    partial void OnSelectedSourceChanged(SourceChoice? value) { if (_started) _ = LoadAsync(); }
    partial void OnVerdictFilterChanged(string value) => ApplyView();
    partial void OnSortByChanged(string value) => ApplyView();
    partial void OnKindFilterChanged(string value) => ApplyView();
    partial void OnMinWasteCoresChanged(double value) => ApplyView();
    partial void OnOverrideGitOpsChanged(bool value) => UpdateSelection();

    private RightSizingEngine Engine()
    {
        var s = _ws.Main.Settings.Current;
        var meta = _ws.Session.Metadata;
        return new RightSizingEngine(new KrrStrategy(), new RightSizingSettings
        {
            CpuPercentile = s.CpuPercentile, MemoryMargin = s.MemoryMargin, MemoryLimitFactor = s.MemoryLimitFactor, KeepCpuLimit = s.KeepCpuLimit,
            PricePerVcpuHour = meta.PricePerVcpuHour, PricePerGibHour = meta.PricePerGibHour,
        });
    }

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
            var transport = _ws.Session.Transport;
            var kinds = new[] { Gvr.Pods, Gvr.ReplicaSets, Gvr.Deployments, Gvr.StatefulSets, Gvr.DaemonSets, Gvr.Jobs, Gvr.CronJobs };
            var objects = new Dictionary<Gvr, List<KubeObject>>();
            foreach (var g in kinds)
            {
                var items = new List<KubeObject>();
                string? cont = null;
                do
                {
                    var l = await transport.ListAsync(g, null, new ListOptions { Limit = 500, Continue = cont }, ct);
                    items.AddRange(l.Items.Select(i => new KubeObject(i)));
                    cont = l.Continue;
                } while (!string.IsNullOrEmpty(cont));
                objects[g] = items;
            }
            var nodes = await transport.ListAsync(Gvr.Nodes, null, new ListOptions(), ct);
            var nodeViews = nodes.Items.Select(n => new NodeView(new KubeObject(n))).ToList();

            var src = await Task.Run(source.Create, ct);
            var history = await src.QueryAsync(UsageQuery.Default(WindowDays, ns), ct);
            var pods = objects[Gvr.Pods].Where(p => ns is null || ns.Contains(p.Namespace ?? "")).ToList();
            KubeObject? Lookup(string kind, string n, string name)
            {
                var gvr = kind switch { "ReplicaSet" => Gvr.ReplicaSets, "Deployment" => Gvr.Deployments, "StatefulSet" => Gvr.StatefulSets, "DaemonSet" => Gvr.DaemonSets, "Job" => Gvr.Jobs, "CronJob" => Gvr.CronJobs, _ => default };
                return gvr == default ? null : objects[gvr].FirstOrDefault(o => o.Name == name && o.Namespace == n);
            }
            var engine = Engine();
            _history = history;
            _report = engine.Analyze(pods, Lookup, history, nodeViews.Sum(n => n.CpuAllocatable), nodeViews.Sum(n => n.MemoryAllocatable));
            SourceInfo = $"{history.SourceName} · {WindowDays} d window · coverage {history.Coverage:P0} ({history.DataHours:0.#} h of data)" + (history.Note is null ? "" : $" · {history.Note}");
            StrategyInfo = $"Strategy: {_report.Strategy}";
            Summarize(_report);
            ApplyView();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; _report = null; Rows.Clear(); }
        finally { IsLoading = false; }
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
        Rows.Clear();
        foreach (var r in q) { r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ContainerRowViewModel.IsSelected)) UpdateSelection(); }; Rows.Add(r); }
        UpdateSelection();
    }

    partial void OnSelectedChanged(ContainerRowViewModel? value)
    {
        if (value is null || _history is null) { CpuSeries = []; MemorySeries = []; return; }
        var pts = _history.Series.Where(s => s.Key.Namespace == value.Report.Namespace && s.Key.Container == value.Report.Container
                                              && s.Key.Pod.StartsWith(value.Report.WorkloadName)).SelectMany(s => s.Points).GroupBy(p => p.Time).OrderBy(g => g.Key).ToList();
        CpuSeries = pts.Select(g => g.Average(p => p.CpuCores)).ToList();
        MemorySeries = pts.Select(g => g.Average(p => p.MemoryBytes)).ToList();
    }

    private List<ContainerReport> SelectedReports() => Rows.Where(r => r.IsSelected && r.Report.Recommended is not null).Select(r => r.Report).ToList();

    private void UpdateSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        var sel = SelectedReports();
        PatchPreview = string.Join("\n", sel.Select(c => $"{c.Namespace}/{c.WorkloadKind}/{c.WorkloadName}\n{Snippets.Diff(c)}"));
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
