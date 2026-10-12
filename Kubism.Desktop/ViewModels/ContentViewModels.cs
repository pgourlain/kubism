using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Helm;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed partial class ActivityViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;

    public ActivityViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        foreach (var e in ws.Session.Activity.Entries) Entries.Insert(0, e);
        ws.Session.Activity.Added += OnAdded;
    }

    public ObservableCollection<ActivityEntry> Entries { get; } = [];
    private void OnAdded(ActivityEntry e) => _ws.Ui.Post(() => Entries.Insert(0, e));

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = await _ws.Dialogs.SaveFileAsync("Export activity", "kubism-activity.csv");
        if (path is not null) await File.WriteAllTextAsync(path, _ws.Session.Activity.ExportCsv());
    }

    public ValueTask DisposeAsync() { _ws.Session.Activity.Added -= OnAdded; return ValueTask.CompletedTask; }
}

public sealed record CreateResult(string Document, bool Success, string Message);

/// <summary>Create from YAML: multi-document server-side apply with a per-document result list.</summary>
public sealed partial class CreateYamlViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;
    public CreateYamlViewModel(WorkspaceViewModel ws) { _ws = ws; }

    [ObservableProperty] private string _yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: example\ndata:\n  key: value\n";
    [ObservableProperty] private bool _isRunning;
    public ObservableCollection<CreateResult> Results { get; } = [];

    [RelayCommand]
    private async Task ApplyAsync()
    {
        Results.Clear(); IsRunning = true;
        try
        {
            IReadOnlyList<JsonNode?> docs;
            try { docs = YamlJson.ParseDocuments(Yaml); }
            catch (Exception ex) { Results.Add(new CreateResult("(yaml)", false, ex.Message)); return; }
            var ns = _ws.Namespaces.Selected.Count == 1 ? _ws.Namespaces.Selected[0] : null;
            foreach (var doc in docs.OfType<JsonObject>())
            {
                var name = $"{doc["kind"]}/{doc["metadata"]?["name"]}";
                try
                {
                    await _ws.Session.Actions.ApplyYamlAsync(YamlJson.ToYaml(doc), ns, _ws.Session.SessionToken);
                    Results.Add(new CreateResult(name, true, "applied"));
                }
                catch (Exception ex) { Results.Add(new CreateResult(name, false, ex.Message)); }
            }
        }
        finally { IsRunning = false; }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed partial class HelmViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;
    private CancellationTokenSource _cts = new();

    public HelmViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        ws.Namespaces.Changed += OnNamespacesChanged;
        _ = LoadAsync();
    }

    private void OnNamespacesChanged() => _ws.Ui.Post(() => _ = LoadAsync());

    public ObservableCollection<HelmRelease> Releases { get; } = [];
    public ObservableCollection<HelmRelease> History { get; } = [];
    [ObservableProperty] private HelmRelease? _selected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _valuesText = "";
    [ObservableProperty] private string _userValuesText = "";
    [ObservableProperty] private string _manifestText = "";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private int _tab = 1;
    [ObservableProperty] private string _diagHeadline = "";
    [ObservableProperty] private bool _diagIsProblem;
    [ObservableProperty] private bool _diagLoading;
    [ObservableProperty] private string _failingSummary = "";
    [ObservableProperty] private bool _hasFailing;
    public ObservableCollection<DiagEvent> DiagEvents { get; } = [];
    public ObservableCollection<DiagPod> DiagPods { get; } = [];
    public ObservableCollection<HelmRelease> Failing { get; } = [];
    private CancellationTokenSource? _diagCts;
    private CancellationTokenSource? _graphCts;
    private (string ns, string name)? _pendingSelect;
    public const int UpgradeTabIndex = 6;
    public const int GraphTabIndex = 7;
    private System.Text.Json.Nodes.JsonObject? _releaseChart;
    private CancellationTokenSource? _upgradeCts;
    public ObservableCollection<DiffLine> PreviewLines { get; } = [];
    [ObservableProperty] private string _upgradeValues = "";
    [ObservableProperty] private string _chartRef = "";
    [ObservableProperty] private string _chartVersion = "";
    [ObservableProperty] private bool _useInstalledChart = true;
    [ObservableProperty] private bool _wait;
    [ObservableProperty] private bool _atomic;
    [ObservableProperty] private bool _upgradeBusy;
    [ObservableProperty] private string _upgradeStatus = "";
    [ObservableProperty] private string? _upgradeError;
    [ObservableProperty] private string? _rebuildBlockedReason;
    private System.Text.Json.Nodes.JsonObject _baseline = new();
    [ObservableProperty] private bool _valuesChanged;
    [ObservableProperty] private string? _valuesError;
    /// <summary>Upgrade is offered once something would change: the values differ from the installed ones, or another chart is given.</summary>
    public bool CanUpgrade => ValuesError is null && !UpgradeBusy && (ValuesChanged || UseReference && ChartRef.Trim().Length > 0);
    public string ValuesState => ValuesError ?? (ValuesChanged ? "Modified: Upgrade and Preview apply the difference from the installed values." : "Same as the installed values. Change what you need, then Upgrade.");
    partial void OnUpgradeValuesChanged(string value) => EvaluateValues();
    partial void OnUpgradeBusyChanged(bool value) => OnPropertyChanged(nameof(CanUpgrade));
    partial void OnChartRefChanged(string value) => OnPropertyChanged(nameof(CanUpgrade));
    partial void OnValuesChangedChanged(bool value) { OnPropertyChanged(nameof(CanUpgrade)); OnPropertyChanged(nameof(ValuesState)); }
    partial void OnValuesErrorChanged(string? value) { OnPropertyChanged(nameof(CanUpgrade)); OnPropertyChanged(nameof(ValuesState)); }

    private void EvaluateValues()
    {
        try
        {
            var parsed = YamlJson.Parse(UpgradeValues);
            if (parsed is not (null or System.Text.Json.Nodes.JsonObject)) throw new FormatException("The values must be a YAML mapping (key: value).");
            ValuesError = null;
            ValuesChanged = !HelmValues.Same(parsed, _baseline);
        }
        catch (Exception ex) { ValuesError = ex.Message; ValuesChanged = false; }
    }

    public bool CanRebuild => RebuildBlockedReason is null;
    /// <summary>Bound to the "another chart" expander: collapsed means the installed chart, which is the normal case.</summary>
    public bool UseReference { get => !UseInstalledChart; set => UseInstalledChart = !value; }
    partial void OnUseInstalledChartChanged(bool value) { OnPropertyChanged(nameof(UseReference)); OnPropertyChanged(nameof(CanUpgrade)); }
    partial void OnRebuildBlockedReasonChanged(string? value) => OnPropertyChanged(nameof(CanRebuild));
    [ObservableProperty] private RelationGraph? _graph;
    [ObservableProperty] private string _graphStatus = "";
    [ObservableProperty] private bool _graphLoading;
    public bool CanWrite => _ws.Session.Actions.CanRunHelm;
    public string UninstallTooltip => CanWrite ? "Uninstall the release with helm: removes the objects it installed and its history (not volume claims of StatefulSets, nor CRDs)" : WriteTooltip;

    [RelayCommand]
    private void ShowHistory() => Tab = 5;

    public string WriteTooltip => CanWrite ? "" : "The helm binary was not found on PATH: rollback and uninstall are disabled";

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var helm = new HelmReleases(_ws.Session.Transport);
            IReadOnlyList<HelmRelease> all;
            if (_ws.Namespaces.IsAll) all = await helm.ListAsync(null, _cts.Token);
            else
            {
                var merged = new List<HelmRelease>();
                foreach (var ns in _ws.Namespaces.Selected.ToList()) merged.AddRange(await helm.ListAsync(ns, _cts.Token));
                all = merged.OrderBy(r => r.Namespace).ThenBy(r => r.Name).ToList();
            }
            Releases.Clear(); foreach (var r in all) Releases.Add(r);
            var now = DateTimeOffset.UtcNow;
            Failing.Clear(); foreach (var r in all.Where(r => HelmDiagnostics.IsProblem(r, now))) Failing.Add(r);
            HasFailing = Failing.Count > 0;
            FailingSummary = HasFailing ? $"{Failing.Count} release(s) failed or stuck: {string.Join(", ", Failing.Select(f => f.Name))}" : "";
            // One glance: jump straight to the first failing release and its diagnosis.
            if (_pendingSelect is { } p && all.FirstOrDefault(r => r.Namespace == p.ns && r.Name == p.name) is { } wanted) { Selected = wanted; _pendingSelect = null; }
            else if (Selected is null && Failing.Count > 0) Selected = Failing[0];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    /// <summary>Select a release once the list has loaded (used when a graph box opens it).</summary>
    public void SelectWhenLoaded(string ns, string name)
    {
        _pendingSelect = (ns, name);
        if (Releases.FirstOrDefault(r => r.Namespace == ns && r.Name == name) is { } r) { Selected = r; _pendingSelect = null; }
        else _ = LoadAsync();
    }

    /// <summary>After a change helm made (upgrade, rollback): read the list again, even if the release is already in it, and select it.</summary>
    private void ReloadAndSelect(string ns, string name) { _pendingSelect = (ns, name); _ = LoadAsync(); }

    partial void OnTabChanged(int value)
    {
        if (value == GraphTabIndex) _ = LoadGraphAsync();
        if (value == UpgradeTabIndex) _ = PrepareUpgradeAsync();
    }

    // ---------- edit values and upgrade ----------

    [RelayCommand]
    private void EditValues() { Tab = UpgradeTabIndex; if (Tab == UpgradeTabIndex) _ = PrepareUpgradeAsync(); }

    [RelayCommand]
    private void ResetValues() { UpgradeValues = YamlJson.ToYaml(_baseline); PreviewLines.Clear(); UpgradeError = null; UpgradeStatus = ""; }

    private void ResetUpgrade(HelmRelease? r)
    {
        _upgradeCts?.Cancel(); _releaseChart = null;
        PreviewLines.Clear(); UpgradeError = null; UpgradeStatus = ""; RebuildBlockedReason = null; UseInstalledChart = true; ChartRef = "";
        // Like Lens: the editor holds every value (chart defaults with the user's overrides) and the user changes what they want.
        _baseline = r?.ComputedValues ?? new System.Text.Json.Nodes.JsonObject();
        UpgradeValues = YamlJson.ToYaml(_baseline);
        EvaluateValues();
        ChartVersion = r?.ChartVersion ?? "";
    }

    /// <summary>Reads the installed chart (to know whether it can be reused) and, when it cannot, looks for the same chart in the repositories helm knows.</summary>
    private async Task PrepareUpgradeAsync()
    {
        var r = Selected;
        if (r is null || _releaseChart is not null) return;
        _upgradeCts?.Cancel(); _upgradeCts = new CancellationTokenSource();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_upgradeCts.Token, _ws.Session.SessionToken).Token;
        try
        {
            _releaseChart = await new HelmReleases(_ws.Session.Transport).GetChartAsync(r.Namespace, r.Name, r.Revision, ct);
            if (Selected != r) return;
            RebuildBlockedReason = HelmUpgrade.CannotRebuildReason(_releaseChart);
            UseInstalledChart = RebuildBlockedReason is null;
            if (RebuildBlockedReason is not null && ChartRef.Length == 0) ChartRef = await SuggestChartAsync(r, ct) ?? "";
            OnPropertyChanged(nameof(CanUpgrade));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UpgradeError = ex.Message; }
    }

    /// <summary><c>helm search repo</c> for a chart of the same name (best effort: the repository may not be added).</summary>
    private async Task<string?> SuggestChartAsync(HelmRelease r, CancellationToken ct)
    {
        if (_ws.Session.Actions.Helm is not { IsAvailable: true } helm) return null;
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(await helm.RunAsync(["search", "repo", r.Chart, "-o", "json"], ct)) as System.Text.Json.Nodes.JsonArray;
            return json?.Select(x => x?["name"]?.ToString()).FirstOrDefault(n => n is not null && n.EndsWith("/" + r.Chart, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private HelmUpgradeRequest? BuildRequest()
    {
        UpgradeError = null;
        if (Selected is not { } r) return null;
        System.Text.Json.Nodes.JsonObject edited;
        try
        {
            var parsed = YamlJson.Parse(UpgradeValues);
            edited = parsed as System.Text.Json.Nodes.JsonObject ?? (parsed is null ? new System.Text.Json.Nodes.JsonObject() : throw new FormatException("The values must be a YAML mapping (key: value)."));
        }
        catch (Exception ex) { UpgradeError = "Values: " + ex.Message; return null; }
        if (!UseInstalledChart && ChartRef.Trim().Length == 0) { UpgradeError = "Give a chart reference (repo/name, oci://…, or a folder), or close \"another chart\" to use the installed one."; return null; }
        if (UseInstalledChart && RebuildBlockedReason is { } why) { UpgradeError = why; return null; }
        return new HelmUpgradeRequest(r.Namespace, r.Name, UseInstalledChart ? "" : ChartRef.Trim(), UseInstalledChart ? null : ChartVersion.Trim(),
            YamlJson.ToYaml(HelmValues.Overrides(_baseline, edited)), Wait, Atomic, _releaseChart);
    }

    /// <summary>Renders the chart with the new values (no cluster needed) and shows what would change against the installed manifest.</summary>
    [RelayCommand]
    private async Task PreviewUpgradeAsync()
    {
        if (_releaseChart is null) await PrepareUpgradeAsync();
        if (BuildRequest() is not { } req || Selected is not { } r) return;
        if (_ws.Session.Actions.Helm is not { IsAvailable: true } helm) { UpgradeError = WriteTooltip; return; }
        UpgradeBusy = true; UpgradeStatus = "Rendering…"; PreviewLines.Clear();
        try
        {
            var (diff, changed, _) = await HelmUpgrade.PreviewAsync(helm, req, r.Manifest, _ws.Session.SessionToken);
            if (Selected != r) return;
            if (!changed) { UpgradeStatus = "No change: the new values render the same manifests as the installed release."; return; }
            foreach (var l in diff.Split('\n'))
            {
                if (l.Length == 0) continue;
                PreviewLines.Add(l.StartsWith("+ ") ? new DiffLine(l, DiffLineKind.Added) : l.StartsWith("- ") ? new DiffLine(l, DiffLineKind.Removed) : l.TrimStart() == "…" ? new DiffLine("  …", DiffLineKind.Gap) : new DiffLine(l, DiffLineKind.Context));
            }
            var (add, rem) = LineDiff.Count(diff);
            UpgradeStatus = $"The rendered manifests would change: +{add} −{rem} lines (installed revision {r.Revision} → {r.Revision + 1}).";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UpgradeError = ex.Message; UpgradeStatus = ""; }
        finally { UpgradeBusy = false; }
    }

    [RelayCommand]
    private async Task UpgradeAsync()
    {
        if (_releaseChart is null) await PrepareUpgradeAsync();
        if (BuildRequest() is not { } req || Selected is not { } r) return;
        UpgradeBusy = true; UpgradeStatus = "Upgrading… (helm waits for the cluster to accept it)";
        try
        {
            var ok = await _ws.RunActionAsync("Upgrade", $"release {r.Name}", true, r.Name, c => _ws.Session.Actions.UpgradeHelmReleaseAsync(req, _ws.Session.SessionToken, c));
            if (!ok) { UpgradeStatus = ""; return; }
            UpgradeStatus = "Upgraded."; PreviewLines.Clear();
            ReloadAndSelect(r.Namespace, r.Name);
        }
        finally { UpgradeBusy = false; }
    }

    /// <summary>The release at the top, then what it rendered and what runs below.</summary>
    public async Task LoadGraphAsync()
    {
        var r = Selected;
        _graphCts?.Cancel();
        if (r is null) { Graph = null; GraphStatus = ""; return; }
        _graphCts = new CancellationTokenSource();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_graphCts.Token, _ws.Session.SessionToken).Token;
        GraphLoading = true;
        try
        {
            var graph = await GraphLoader.LoadAsync(_ws, new Core.Helm.HelmReleaseInfo(r.Name, r.Namespace, r.Revision, r.Status).ToObject(), ct);
            if (Selected != r) return;
            Graph = graph;
            GraphStatus = graph.Nodes.Count <= 1 ? "This release's objects carry no Helm annotation, so none could be matched" : $"{graph.Nodes.Count} objects" + (graph.Truncated ? " (more not shown)" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GraphStatus = ex.Message; }
        finally { GraphLoading = false; }
    }

    [RelayCommand]
    private void OpenGraphNode(GraphNode? node) { if (node is not null && node.Kind != Core.Helm.HelmReleaseInfo.Kind) _ws.OpenGraphNode(node); }

    partial void OnSelectedChanged(HelmRelease? value)
    {
        History.Clear();
        Graph = null; GraphStatus = "";
        ResetUpgrade(value);
        if (value is null) return;
        if (Tab == UpgradeTabIndex) _ = PrepareUpgradeAsync();
        if (Tab == GraphTabIndex) _ = LoadGraphAsync();
        ValuesText = YamlJson.ToYaml(value.ComputedValues);
        UserValuesText = YamlJson.ToYaml(value.UserValues ?? new JsonObject());
        ManifestText = value.Manifest ?? ""; NotesText = value.Notes ?? "";
        _ = LoadHistoryAsync(value);
        _ = LoadDiagnosisAsync(value);
    }

    private async Task LoadDiagnosisAsync(HelmRelease r)
    {
        _diagCts?.Cancel(); _diagCts = new CancellationTokenSource();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_diagCts.Token, _ws.Session.SessionToken).Token;
        var problem = HelmDiagnostics.IsProblem(r, DateTimeOffset.UtcNow);
        DiagIsProblem = problem; DiagHeadline = HelmDiagnostics.Headline(r, DateTimeOffset.UtcNow);
        if (Tab != GraphTabIndex && Tab != UpgradeTabIndex) Tab = problem ? 0 : 1;
        DiagEvents.Clear(); DiagPods.Clear();
        if (!problem) { DiagHeadline = $"Release {r.Status}: no problem detected."; return; }
        DiagLoading = true;
        try
        {
            var d = await HelmDiagnostics.CollectAsync(_ws.Session.Transport, r, ct);
            if (Selected != r) return;
            foreach (var e in d.Events) DiagEvents.Add(e);
            foreach (var p in d.Pods) DiagPods.Add(p);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DiagHeadline += $"  (diagnosis failed: {ex.Message})"; }
        finally { DiagLoading = false; }
    }

    [RelayCommand]
    private Task RefreshDiagnosis() => Selected is { } r ? LoadDiagnosisAsync(r) : Task.CompletedTask;

    private async Task LoadHistoryAsync(HelmRelease r)
    {
        try
        {
            var h = await new HelmReleases(_ws.Session.Transport).HistoryAsync(r.Namespace, r.Name, _cts.Token);
            _ws.Ui.Post(() => { History.Clear(); foreach (var x in h.OrderByDescending(x => x.Revision)) History.Add(x); });
        }
        catch (Exception) { }
    }

    [RelayCommand]
    private async Task Rollback(HelmRelease? revision)
    {
        if (revision is null || Selected is not { } r) return;
        var ok = await _ws.RunActionAsync("Rollback", $"{r.Name} to revision {revision.Revision}", true, r.Name,
            c => _ws.Session.Actions.RollbackHelmReleaseAsync(r.Namespace, r.Name, revision.Revision, _ws.Session.SessionToken, c));
        // The list shows the revision and status helm just wrote: read it again and stay on the same release.
        if (ok) ReloadAndSelect(r.Namespace, r.Name);
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (Selected is not { } r) return;
        var ok = await _ws.RunActionAsync("Uninstall", r.Name, true, r.Name, c => _ws.Session.Actions.UninstallHelmReleaseAsync(r.Namespace, r.Name, _ws.Session.SessionToken, c));
        if (!ok) return;
        // The release is gone: clear the selection (and its tabs) and reload the list.
        Selected = null;
        await LoadAsync();
    }

    public ValueTask DisposeAsync() { _ws.Namespaces.Changed -= OnNamespacesChanged; _cts.Cancel(); return ValueTask.CompletedTask; }
}
