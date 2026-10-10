using Kubism.Core.Util;

namespace Kubism.Core.Network;

public sealed partial class NetworkGraph
{
    /// <summary>
    /// Filter and lay out. A chain is the Ingress → Service → Workload nodes that are connected, with the exposure nodes that feed it. With a namespace filter,
    /// a chain is kept when its Ingress, Service or workload is in a chosen namespace; exposure nodes of another namespace (the ingress controller's Service)
    /// stay as context so the path to the outside is never cut, but no other chain they feed is pulled in.
    /// </summary>
    public NetworkView View(NetworkFilter? filter = null)
    {
        filter ??= new NetworkFilter();
        var byId = _nodes.ToDictionary(n => n.Id);
        bool InSelection(NetworkNode n) => filter.Namespaces is null || (n.Namespace is not null && filter.Namespaces.Contains(n.Namespace));

        // chains: union-find over the non-exposure nodes in the selection
        var anchors = _nodes.Where(n => n.Kind is not (NetworkNodeKind.Exposure or NetworkNodeKind.Node) && InSelection(n)).ToList();
        var parent = anchors.ToDictionary(n => n.Id, n => n.Id);
        string Find(string x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var e in _edges)
            if (parent.ContainsKey(e.From) && parent.ContainsKey(e.To)) parent[Find(e.From)] = Find(e.To);
        var exposureOf = _edges.Where(e => byId.TryGetValue(e.From, out var f) && f.Kind == NetworkNodeKind.Exposure && parent.ContainsKey(e.To)).GroupBy(e => Find(e.To)).ToDictionary(x => x.Key, x => x.Select(e => e.From).Distinct().ToList());

        var chains = new List<(List<NetworkNode> Nodes, List<NetworkNode> Exposure, ExposureLevel Level, bool Problem)>();
        foreach (var grp in anchors.GroupBy(n => Find(n.Id)))
        {
            var nodes = grp.ToList();
            var exposure = (exposureOf.TryGetValue(grp.Key, out var ids) ? ids : []).Select(i => byId[i]).ToList();
            var level = nodes.Concat(exposure).Max(n => n.Exposure);
            var hasProblem = nodes.Concat(exposure).Any(n => n.HasProblem) || _edges.Any(e => e.Problem && nodes.Any(n => n.Id == e.From) && nodes.Any(n => n.Id == e.To));
            // Services nothing routes to are not a way in: hidden unless asked for.
            // ... except when looking for problems: a broken in-cluster Service is one.
            if (exposure.Count == 0 && !nodes.Any(n => n.Kind == NetworkNodeKind.Ingress) && !filter.IncludeInternalServices && !(filter.ProblemsOnly && hasProblem)) continue;
            if (filter.Exposure == ExposureFilter.OutsideOnly && level != ExposureLevel.Outside) continue;
            if (filter.Exposure == ExposureFilter.InClusterOnly && level != ExposureLevel.InCluster) continue;
            if (filter.ProblemsOnly && !hasProblem) continue;
            if (!string.IsNullOrWhiteSpace(filter.Search) && !nodes.Concat(exposure).Any(n => Matches(n, filter.Search.Trim()))) continue;
            chains.Add((nodes, exposure, level, hasProblem));
        }
        var ordered = chains.OrderByDescending(c => c.Problem).ThenByDescending(c => c.Level).ThenBy(c => c.Nodes.Min(n => n.Namespace + "/" + n.Label), StringComparer.Ordinal).ToList();
        var truncated = ordered.Count > filter.MaxChains;
        var kept = ordered.Take(filter.MaxChains).ToList();

        var chosen = new Dictionary<string, NetworkNode>();
        foreach (var c in kept) foreach (var n in c.Nodes.Concat(c.Exposure)) chosen[n.Id] = n;
        // The nodes column: a node is drawn once, with an arrow from every shown workload that has a pod on it.
        if (filter.ShowNodes)
            foreach (var e in _edges.Where(e => chosen.TryGetValue(e.From, out var f) && f.Kind == NetworkNodeKind.Workload && byId.TryGetValue(e.To, out var t) && t.Kind == NetworkNodeKind.Node))
                chosen[e.To] = byId[e.To];
        var columns = filter.ShowNodes ? 5 : 4;
        var edges = _edges.Where(e => chosen.ContainsKey(e.From) && chosen.ContainsKey(e.To)).ToList();
        var placed = chosen.Values.Select(n => new PlacedNode(n, (int)n.Kind, 0, filter.Namespaces is not null && n.Namespace is not null && !filter.Namespaces.Contains(n.Namespace))).ToList();
        var rows = new Dictionary<string, int>();
        var maxRows = ColumnLayout.Arrange(placed, columns, p => p.Id, p => p.Column, (p, r) => rows[p.Id] = r, p => p.Node.Namespace + "/" + p.Node.Label, edges.Select(e => (e.From, e.To)), p => -(int)p.Node.Exposure);
        var ingressIds = chosen.Values.Where(n => n.Kind == NetworkNodeKind.Ingress).Select(n => n.Id).ToHashSet();
        var routeRows = _routes.Where(r => ingressIds.Contains(r.IngressId)).Select(r => RowOf(r, byId)).ToList();
        routeRows.Sort((a, b) =>
        {
            var c = string.Compare(a.Route.Host, b.Route.Host, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.Compare(a.Route.Path, b.Route.Path, StringComparison.Ordinal);
            return c != 0 ? c : string.Compare(a.Ingress, b.Ingress, StringComparison.Ordinal);
        });
        return new NetworkView
        {
            Routes = routeRows,
            Nodes = placed.Select(p => p with { Row = rows[p.Id] }).ToList(),
            Edges = edges, MaxRows = maxRows, Columns = columns, Truncated = truncated,
            Chains = kept.Count, OutsideChains = kept.Count(c => c.Level == ExposureLevel.Outside), ProblemChains = kept.Count(c => c.Problem),
            Warning = IncompleteWarning,
        };
    }

    /// <summary>One URL joined with what serves it: how it reaches the cluster, the backend Service port, the workloads behind it and what is wrong.</summary>
    private NetworkRouteRow RowOf(NetworkRoute r, Dictionary<string, NetworkNode> byId)
    {
        var ingress = byId[r.IngressId];
        var scheme = r.TlsCovered ? "https" : "http";
        var url = r.DefaultBackend ? "(default backend)" : $"{scheme}://{r.Host}{(r.Path.Length == 0 ? "/" : r.Path)}";
        var via = string.Join("; ", _edges.Where(e => e.To == r.IngressId && byId.TryGetValue(e.From, out var x) && x.Kind == NetworkNodeKind.Exposure)
            .Select(e => byId[e.From]).Select(x => x.Detail.Length > 0 ? $"{x.Label} · {x.Detail}" : x.Label));
        var service = byId.GetValueOrDefault(r.ServiceId);
        string target;
        if (r.BackendMissing || service is null || service.Missing) target = "Service missing";
        else
        {
            var behind = _edges.Where(e => e.From == r.ServiceId && byId.TryGetValue(e.To, out var w) && w.Kind == NetworkNodeKind.Workload).ToList();
            target = behind.Count == 0 ? "no pods behind the Service"
                : string.Join("; ", behind.Select(e => $"{byId[e.To].Label} ({byId[e.To].Detail.Split(" · ")[0]}) · {e.Label}"));
        }
        var problems = ingress.Problems.Concat(service?.Problems ?? []).DistinctBy(p => p.Code + p.Description).ToList();
        return new NetworkRouteRow(r, url, ingress.Exposure, via, $"{r.Service}:{r.ServicePort}", target, string.Join(" ", problems.Select(p => p.Description)),
            problems.Count == 0 ? null : problems.Max(p => p.Severity));
    }

    private static bool Matches(NetworkNode n, string text) =>
        n.Label.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Detail.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (n.Namespace?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || n.Lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every namespace that holds a node, for the filter's choices.</summary>
    public IReadOnlyList<string> Namespaces() => _nodes.Select(n => n.Namespace).Where(n => n is not null).Select(n => n!).Distinct().Order(StringComparer.Ordinal).ToList();
}
