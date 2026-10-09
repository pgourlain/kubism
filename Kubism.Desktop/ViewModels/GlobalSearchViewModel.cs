using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Api;
using Kubism.Core.Discovery;

namespace Kubism.Desktop.ViewModels;

public sealed record SearchHit(string Kind, string? Namespace, string Name, WorkspaceViewModel Workspace, ResourceKind ResourceKind)
{
    public string Display => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
}

public sealed class SearchGroup(string cluster, IReadOnlyList<SearchHit> hits)
{
    public string Cluster { get; } = cluster;
    public IReadOnlyList<SearchHit> Hits { get; } = hits;
    public string Header => $"{Cluster} ({Hits.Count})";
}

/// <summary>Search by name or label selector (key=value) across every connected cluster; results grouped by cluster.</summary>
public sealed partial class GlobalSearchViewModel : ViewModelBase
{
    private static readonly string[] Resources = ["pods", "deployments", "statefulsets", "daemonsets", "services", "configmaps", "secrets", "ingresses", "namespaces", "nodes", "jobs", "cronjobs", "persistentvolumeclaims"];
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public GlobalSearchViewModel(MainViewModel main) { _main = main; }

    public ObservableCollection<SearchGroup> Groups { get; } = [];
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _summary = "";

    public void Reset() { Query = ""; Groups.Clear(); Summary = ""; }

    [RelayCommand]
    public async Task SearchAsync()
    {
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var q = Query.Trim();
        Groups.Clear();
        if (q.Length == 0) { Summary = ""; return; }
        IsSearching = true;
        try
        {
            var isLabel = q.Contains('=');
            var perCluster = await Task.WhenAll(_main.Workspaces.ToList().Select(ws => SearchClusterAsync(ws, q, isLabel, ct)));
            foreach (var hits in perCluster.Where(h => h.Count > 0)) Groups.Add(new SearchGroup(hits[0].Workspace.Title, hits));
            Summary = Groups.Count == 0 ? "No results" : $"{Groups.Sum(g => g.Hits.Count)} results in {Groups.Count} cluster(s)";
        }
        catch (OperationCanceledException) { }
        finally { IsSearching = false; }
    }

    private static async Task<List<SearchHit>> SearchClusterAsync(WorkspaceViewModel ws, string q, bool isLabel, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        foreach (var res in Resources)
        {
            ct.ThrowIfCancellationRequested();
            var kind = ws.Session.Discovery.Kinds.FirstOrDefault(k => k.Resource == res && k.CanList);
            if (kind is null) continue;
            try
            {
                var list = await ws.Session.Transport.ListAsync(kind.Gvr, null, new ListOptions { Limit = 500, LabelSelector = isLabel ? q : null }, ct);
                foreach (var o in list.Items.Select(i => new KubeObject(i)))
                    if (isLabel || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new SearchHit(kind.Kind, o.Namespace, o.Name, ws, kind));
            }
            catch (OperationCanceledException) { throw; }
            catch (KubeApiException) { /* forbidden or unavailable kinds are skipped */ }
        }
        return hits;
    }

    [RelayCommand]
    public void Open(SearchHit? hit)
    {
        if (hit is null) return;
        _main.GlobalSearchOpen = false;
        _main.SelectedWorkspace = hit.Workspace;
        if (hit.Namespace is not null) hit.Workspace.Namespaces.Select(hit.Namespace);
        hit.Workspace.OpenKind(hit.ResourceKind);
        if (hit.Workspace.CurrentContent is ResourceListViewModel list) list.Filter = hit.Name;
    }
}
