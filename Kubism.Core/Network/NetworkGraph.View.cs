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
        var anchors = _nodes.Where(n => n.Kind != NetworkNodeKind.Exposure && InSelection(n)).ToList();
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
            if (exposure.Count == 0 && !nodes.Any(n => n.Kind == NetworkNodeKind.Ingress) && !filter.IncludeInternalServices) continue;
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
        var edges = _edges.Where(e => chosen.ContainsKey(e.From) && chosen.ContainsKey(e.To)).ToList();
        var placed = chosen.Values.Select(n => new PlacedNode(n, (int)n.Kind, 0, filter.Namespaces is not null && n.Namespace is not null && !filter.Namespaces.Contains(n.Namespace))).ToList();
        var rows = new Dictionary<string, int>();
        var maxRows = ColumnLayout.Arrange(placed, 4, p => p.Id, p => p.Column, (p, r) => rows[p.Id] = r, p => p.Node.Namespace + "/" + p.Node.Label, edges.Select(e => (e.From, e.To)), p => -(int)p.Node.Exposure);
        return new NetworkView
        {
            Nodes = placed.Select(p => p with { Row = rows[p.Id] }).ToList(),
            Edges = edges, MaxRows = maxRows, Truncated = truncated,
            Chains = kept.Count, OutsideChains = kept.Count(c => c.Level == ExposureLevel.Outside), ProblemChains = kept.Count(c => c.Problem),
            Warning = IncompleteWarning,
        };
    }

    private static bool Matches(NetworkNode n, string text) =>
        n.Label.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Detail.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (n.Namespace?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || n.Lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every namespace that holds a node, for the filter's choices.</summary>
    public IReadOnlyList<string> Namespaces() => _nodes.Select(n => n.Namespace).Where(n => n is not null).Select(n => n!).Distinct().Order(StringComparer.Ordinal).ToList();
}
