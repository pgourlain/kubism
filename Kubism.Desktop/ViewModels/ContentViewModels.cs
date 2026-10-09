using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Helm;

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

    public HelmViewModel(WorkspaceViewModel ws) { _ws = ws; _ = LoadAsync(); }

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
    public bool CanWrite => _ws.Session.Actions.CanRunHelm;
    public string WriteTooltip => CanWrite ? "" : "The helm binary was not found on PATH: rollback and uninstall are disabled";

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var all = await new HelmReleases(_ws.Session.Transport).ListAsync(_ws.Namespaces.IsAll ? null : _ws.Namespaces.Selected[0], _cts.Token);
            Releases.Clear(); foreach (var r in all) Releases.Add(r);
            var now = DateTimeOffset.UtcNow;
            Failing.Clear(); foreach (var r in all.Where(r => HelmDiagnostics.IsProblem(r, now))) Failing.Add(r);
            HasFailing = Failing.Count > 0;
            FailingSummary = HasFailing ? $"{Failing.Count} release(s) failed or stuck: {string.Join(", ", Failing.Select(f => f.Name))}" : "";
            // One glance: jump straight to the first failing release and its diagnosis.
            if (Selected is null && Failing.Count > 0) Selected = Failing[0];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnSelectedChanged(HelmRelease? value)
    {
        History.Clear();
        if (value is null) return;
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
        Tab = problem ? 0 : 1;
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
    private Task Rollback(HelmRelease? revision)
    {
        if (revision is null || Selected is null) return Task.CompletedTask;
        return _ws.RunActionAsync("Rollback", $"{Selected.Name} to revision {revision.Revision}", true, Selected.Name,
            c => _ws.Session.Actions.RollbackHelmReleaseAsync(Selected.Namespace, Selected.Name, revision.Revision, _ws.Session.SessionToken, c));
    }

    [RelayCommand]
    private Task Uninstall()
    {
        if (Selected is null) return Task.CompletedTask;
        var r = Selected;
        return _ws.RunActionAsync("Uninstall", r.Name, true, r.Name, c => _ws.Session.Actions.UninstallHelmReleaseAsync(r.Namespace, r.Name, _ws.Session.SessionToken, c));
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
}
