using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Api;
using Kubism.Core.Discovery;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed record SearchHit(string Kind, string? Namespace, string Name, WorkspaceViewModel Workspace, ResourceKind ResourceKind, string? Detail = null)
{
    public string Display => (Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}") + (Detail is null ? "" : $"   {Detail}");
}

public sealed class SearchGroup(string cluster, IReadOnlyList<SearchHit> hits)
{
    public string Cluster { get; } = cluster;
    public IReadOnlyList<SearchHit> Hits { get; } = hits;
    public string Header => $"{Cluster} ({Hits.Count})";
}

/// <summary>Search across every connected cluster, results grouped by cluster. Syntax: see <see cref="SearchQuery"/> (name, key=value, image:, ns:, kind:).</summary>
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
            var query = SearchQuery.Parse(q);
            var perCluster = await Task.WhenAll(_main.Workspaces.ToList().Select(ws => SearchClusterAsync(ws, query, ct)));
            foreach (var hits in perCluster.Where(h => h.Count > 0)) Groups.Add(new SearchGroup(hits[0].Workspace.Title, hits));
            Summary = Groups.Count == 0 ? "No results" : $"{Groups.Sum(g => g.Hits.Count)} results in {Groups.Count} cluster(s)";
        }
        catch (OperationCanceledException) { }
        finally { IsSearching = false; }
    }

    private const int MaxHitsPerCluster = 500;

    private static async Task<List<SearchHit>> SearchClusterAsync(WorkspaceViewModel ws, SearchQuery query, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        foreach (var kind in ws.Session.Discovery.Kinds.Where(k => k.CanList && query.WantsKind(k, Resources)).DistinctBy(k => k.Gvr))
        {
            ct.ThrowIfCancellationRequested();
            if (hits.Count >= MaxHitsPerCluster) break;
            try
            {
                var ns = kind.Namespaced ? query.Namespace : null;
                var list = await ws.Session.Transport.ListAsync(kind.Gvr, ns, new ListOptions { Limit = 500, LabelSelector = query.LabelSelector }, ct);
                foreach (var o in list.Items.Select(i => new KubeObject(i)))
                {
                    if (!query.Matches(o)) continue;
                    var detail = query.Images.Count > 0 ? string.Join(", ", SearchQuery.ImagesOf(o).Where(img => query.Images.Any(i => img.Contains(i, StringComparison.OrdinalIgnoreCase))).Distinct()) : null;
                    hits.Add(new SearchHit(kind.Kind, o.Namespace, o.Name, ws, kind, detail));
                }
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
