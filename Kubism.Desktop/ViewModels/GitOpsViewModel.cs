using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Delivery;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed record GitOpsRow(GitOpsApp App, string Age)
{
    public string Tool => App.ToolName;
    public string Kind => App.Kind;
    public string Display => App.Display;
    public string Sync => App.Sync;
    public string Health => App.Health;
    public string Revision => App.Revision;
    public string Source => App.Source;
    public Severity Severity => App.Level switch
    {
        GitOpsLevel.Healthy => Severity.Ok, GitOpsLevel.Degraded => Severity.Error, GitOpsLevel.OutOfSync or GitOpsLevel.Progressing => Severity.Warning, _ => Severity.Neutral,
    };
}

/// <summary>
/// Argo CD Applications and Flux Kustomizations, HelmReleases and sources, in one list: sync, health, revision, source, what each one manages
/// and what is out of sync. Actions (sync, refresh, reconcile, suspend) go through ClusterActions. Refreshes itself every few seconds while shown.
/// </summary>
public sealed partial class GitOpsViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _timer;
    private List<GitOpsRow> _all = [];

    public GitOpsViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        _ = RefreshAsync();
        _timer = new Timer(_ => { if (!IsLoading && !_cts.IsCancellationRequested) _ws.Ui.Post(() => _ = RefreshAsync()); }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public ObservableCollection<GitOpsRow> Rows { get; } = [];
    public ObservableCollection<GitOpsResource> Resources { get; } = [];
    public ObservableCollection<ActionItem> Actions { get; } = [];
    [ObservableProperty] private GitOpsRow? _selected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _problemsOnly;

    partial void OnFilterChanged(string value) => Apply();
    partial void OnProblemsOnlyChanged(bool value) => Apply();

    partial void OnSelectedChanged(GitOpsRow? value)
    {
        Resources.Clear(); Actions.Clear();
        if (value is null) return;
        foreach (var r in value.App.Resources) Resources.Add(r);
        foreach (var a in BuildActions(value.App)) Actions.Add(a);
        OnPropertyChanged(nameof(HasMessage));
    }

    public bool HasMessage => Selected?.App.Message.Length > 0;
    public bool HasResources => Resources.Count > 0;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var kinds = _ws.Session.Discovery.Kinds.Where(k => k.CanList && GitOpsApp.IsGitOpsKind(k.Group, k.Resource)).DistinctBy(k => (k.Group, k.Resource)).ToList();
            var lists = await Task.WhenAll(kinds.Select(async k =>
            {
                try { return (k, items: await CompareViewModel.ListAllAsync(_ws, k, null, _cts.Token)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return (k, items: new List<KubeObject>()); }
            }));
            var skew = _ws.Session.ClockSkew;
            var rows = lists.SelectMany(l => l.items.Select(o => GitOpsApp.From(o, l.k.Gvr))).Where(a => a is not null)
                .Select(a => new GitOpsRow(a!, KubeTime.Age(a!.LastChange, skew)))
                .OrderByDescending(r => r.App.IsProblem).ThenBy(r => r.App.Tool).ThenBy(r => r.App.Kind, StringComparer.Ordinal).ThenBy(r => r.Display, StringComparer.Ordinal).ToList();
            _all = rows;
            Apply();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private void Apply()
    {
        var keep = _all.Where(r => (!ProblemsOnly || r.App.IsProblem) && (Filter.Length == 0 || r.Display.Contains(Filter, StringComparison.OrdinalIgnoreCase) || r.Source.Contains(Filter, StringComparison.OrdinalIgnoreCase))).ToList();
        // Keep the selection across the periodic refresh: the list is rebuilt, so find the same object again.
        (Gvr, string?, string)? key = Selected is null ? null : (Selected.App.Gvr, Selected.App.Namespace, Selected.App.Name);
        Rows.Clear();
        foreach (var r in keep) Rows.Add(r);
        Selected = (key is null ? null : Rows.FirstOrDefault(r => (r.App.Gvr, r.App.Namespace, r.App.Name) == key)) ?? Rows.FirstOrDefault();
        var argo = _all.Count(r => r.App.Tool == GitOpsTool.ArgoCd); var flux = _all.Count - argo;
        Summary = _all.Count == 0 ? "No Argo CD or Flux resources found" : $"{_all.Count} resources (Argo CD {argo}, Flux {flux}) · {_all.Count(r => r.App.IsProblem)} need attention · {_all.Count(r => r.App.Suspended)} suspended";
        OnPropertyChanged(nameof(HasResources));
    }

    private IEnumerable<ActionItem> BuildActions(GitOpsApp app)
    {
        var ns = app.Namespace ?? ""; var name = app.Name; var target = $"{app.Kind.ToLowerInvariant()} {name}";
        var actions = _ws.Session.Actions;
        Task Run(string verb, bool destructive, Func<string?, Task> body) => RunAsync(verb, target, destructive, name, body);
        var list = new List<ActionItem>();
        if (app.Tool == GitOpsTool.ArgoCd)
        {
            list.Add(new ActionItem("Sync", () => Run("Sync", false, _ => actions.ArgoSyncAsync(ns, name, false, _ws.Session.SessionToken))) { Verb = "patch" });
            list.Add(new ActionItem("Sync and prune…", () => Run("Sync and prune", true, c => actions.ArgoSyncAsync(ns, name, true, _ws.Session.SessionToken, c))) { Verb = "patch" });
            list.Add(new ActionItem("Refresh from Git", () => Run("Refresh", false, _ => actions.ArgoRefreshAsync(ns, name, false, _ws.Session.SessionToken))) { Verb = "patch" });
            list.Add(new ActionItem("Hard refresh from Git", () => Run("Hard refresh", false, _ => actions.ArgoRefreshAsync(ns, name, true, _ws.Session.SessionToken))) { Verb = "patch" });
        }
        else
        {
            list.Add(new ActionItem("Reconcile", () => Run("Reconcile", false, _ => actions.FluxReconcileAsync(app.Gvr, ns, name, _ws.Session.SessionToken))) { Verb = "patch" });
            list.Add(new ActionItem(app.Suspended ? "Resume" : "Suspend", () => Run(app.Suspended ? "Resume" : "Suspend", false, _ => actions.SetFluxSuspendedAsync(app.Gvr, ns, name, !app.Suspended, _ws.Session.SessionToken))) { Verb = "patch" });
        }
        list.Add(new ActionItem("Open object", () => { OpenObject(app); return Task.CompletedTask; }));
        if (_ws.IsReadOnly) foreach (var a in list.Where(a => a.Verb is not null)) a.Disable("Read-only cluster: changes are refused");
        return list;
    }

    private async Task RunAsync(string verb, string target, bool destructive, string name, Func<string?, Task> body)
    {
        if (await _ws.RunActionAsync(verb, target, destructive, name, body)) { await Task.Delay(800); await RefreshAsync(); }
    }

    private void OpenObject(GitOpsApp app)
    {
        var kind = _ws.Session.Discovery.Kinds.FirstOrDefault(k => k.Gvr == app.Gvr);
        if (kind is null) return;
        if (app.Namespace is not null) _ws.Namespaces.Select(app.Namespace);
        _ws.OpenKind(kind);
        if (_ws.CurrentContent is ResourceListViewModel list) list.Filter = app.Name;
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); _timer.Dispose(); return ValueTask.CompletedTask; }
}
