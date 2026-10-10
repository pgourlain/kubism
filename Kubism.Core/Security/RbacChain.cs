using Kubism.Core.Api;

namespace Kubism.Core.Security;

public enum RbacNodeKind { Subject, Binding, Role, Rules }

/// <summary>One node of the chain graph. <see cref="Column"/> and <see cref="Row"/> are the layout; drawing is the UI's job.</summary>
public sealed class RbacChainNode(string id, RbacNodeKind kind, string label, string? scope)
{
    public string Id { get; } = id;
    public RbacNodeKind Kind { get; } = kind;
    public string Label { get; } = label;
    /// <summary>The namespace the node belongs to; null is cluster-wide.</summary>
    public string? Scope { get; } = scope;
    public string ScopeText => Scope ?? "cluster";
    public string Detail { get; init; } = "";
    /// <summary>A role that does not exist.</summary>
    public bool Missing { get; init; }
    public IReadOnlyList<RbacRisk> Risks { get; init; } = [];
    public bool Risky => Risks.Count > 0;
    public RbacSubject? Subject { get; init; }
    /// <summary>The RBAC object behind a binding or role node, to show its YAML.</summary>
    public KubeObject? Source { get; init; }
    public IReadOnlyList<RbacRule> Rules { get; init; } = [];
    public int Column { get; set; }
    public int Row { get; set; }
}

/// <param name="Highlight">The special RoleBinding → ClusterRole edge: the cluster role applies to one namespace only.</param>
public sealed record RbacChainEdge(string From, string To, string Label, bool Highlight);

/// <summary>Which part of the RBAC graph to draw. Everything null/false draws the lot (up to <see cref="MaxBindings"/> bindings).</summary>
public sealed record RbacChainFilter
{
    public string? Namespace { get; init; }
    /// <summary>
    /// With a namespace: also draw the cluster-wide bindings that concern it, i.e. those with a subject from that namespace (a ServiceAccount
    /// of it, or the group system:serviceaccounts:&lt;ns&gt;). Only those subjects are drawn. Other cluster-wide bindings are left out.
    /// </summary>
    public bool IncludeCluster { get; init; } = true;
    public IReadOnlySet<string>? SubjectKinds { get; init; }
    public bool RiskyOnly { get; init; }
    public string? SubjectQuery { get; init; }
    /// <summary>Only the bindings that give this subject something, through its implicit groups too.</summary>
    public RbacSubject? Focus { get; init; }
    public int MaxBindings { get; init; } = 150;
}

/// <summary>Subject → Binding → Role → Rules in four columns, rows ordered to limit edge crossings.</summary>
public sealed class RbacChain
{
    public IReadOnlyList<RbacChainNode> Nodes { get; init; } = [];
    public IReadOnlyList<RbacChainEdge> Edges { get; init; } = [];
    public int MaxRows { get; init; }
    public bool Truncated { get; init; }
    public string? Warning { get; init; }

    public static RbacChain Build(RbacGraph graph, RbacChainFilter? filter = null)
    {
        filter ??= new RbacChainFilter();
        var nodes = new Dictionary<string, RbacChainNode>(); var edges = new HashSet<RbacChainEdge>();
        var query = filter.SubjectQuery?.Trim();
        var focusBindings = filter.Focus is { } f ? graph.RightsOf(f).Paths.Select(p => p.Binding.Key).ToHashSet() : null;

        // A subject is drawn when it passes the kind and search filters and, for a cluster-wide binding under a namespace filter, belongs to that namespace.
        bool Shown(RbacBinding b, RbacSubject s) =>
            (filter.SubjectKinds is null || filter.SubjectKinds.Contains(s.Kind))
            && (string.IsNullOrEmpty(query) || s.Display.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (filter.Namespace is null || b.Namespace is not null || s.Namespace == filter.Namespace || s.Name == $"system:serviceaccounts:{filter.Namespace}");

        var selected = new List<RbacBinding>();
        foreach (var b in graph.Bindings)
        {
            if (focusBindings is not null && !focusBindings.Contains(b.Key) && !b.IsOrphan) continue;
            if (filter.Namespace is { } ns && b.Namespace != ns && !(filter.IncludeCluster && b.Namespace is null)) continue;
            var subjects = b.Subjects.Where(s => Shown(b, s)).ToList();
            if (subjects.Count == 0) continue;
            if (filter.RiskyOnly && RbacGraph.RisksOf(b).Count == 0 && (b.Role is null || RbacGraph.RisksOf(b.Role).Count == 0)) continue;
            selected.Add(b);
        }
        var truncated = selected.Count > filter.MaxBindings;
        foreach (var b in selected.OrderBy(b => b.Namespace is null ? 0 : 1).ThenBy(b => b.Display, StringComparer.Ordinal).Take(filter.MaxBindings))
        {
            var bid = "b:" + b.Key;
            nodes.TryAdd(bid, new RbacChainNode(bid, RbacNodeKind.Binding, b.Name, b.Namespace)
            {
                Detail = b.Kind, Risks = RbacGraph.RisksOf(b), Source = b.Source, Missing = b.IsOrphan,
            });
            foreach (var s in b.Subjects.Where(s => Shown(b, s)))
            {
                var sid = "s:" + s.Key;
                nodes.TryAdd(sid, new RbacChainNode(sid, RbacNodeKind.Subject, s.Kind == RbacSubject.ServiceAccount ? $"{s.Namespace}/{s.Name}" : s.Name, s.Namespace)
                {
                    Detail = s.Kind, Subject = s, Risks = RbacGraph.RisksOf(new RbacBinding(b.Kind, b.Namespace, b.Name, b.RoleKind, b.RoleName, [s], b.Role, b.Source)).Where(r => r.Code.EndsWith("subject", StringComparison.Ordinal)).ToList(),
                });
                edges.Add(new RbacChainEdge(sid, bid, "", false));
            }
            if (b.Role is { } role)
            {
                var rid = "r:" + role.Key;
                var risks = RbacGraph.RisksOf(role);
                nodes.TryAdd(rid, new RbacChainNode(rid, RbacNodeKind.Role, role.Name, role.Namespace)
                {
                    Detail = role.Aggregated ? $"{role.Kind} (aggregated)" : role.Kind, Risks = risks, Source = role.Source,
                });
                var label = b.ClusterRoleInNamespace ? $"ClusterRole applied in {b.Namespace} only" : "";
                edges.Add(new RbacChainEdge(bid, rid, label, b.ClusterRoleInNamespace));
                var uid = "u:" + role.Key;
                nodes.TryAdd(uid, new RbacChainNode(uid, RbacNodeKind.Rules, role.Rules.Count == 1 ? "1 rule" : $"{role.Rules.Count} rules", role.Namespace) { Rules = role.Rules, Risks = risks, Detail = $"rules of {role.Name}" });
                edges.Add(new RbacChainEdge(rid, uid, "", false));
            }
            else
            {
                var rid = $"m:{b.RoleKind}/{b.Namespace}/{b.RoleName}";
                nodes.TryAdd(rid, new RbacChainNode(rid, RbacNodeKind.Role, b.RoleName, b.RoleKind == "ClusterRole" ? null : b.Namespace) { Detail = $"{b.RoleKind} (missing)", Missing = true });
                edges.Add(new RbacChainEdge(bid, rid, "role does not exist", true));
            }
        }
        foreach (var n in nodes.Values) n.Column = (int)n.Kind;
        var list = nodes.Values.ToList();
        var maxRows = Util.ColumnLayout.Arrange(list, 4, n => n.Id, n => n.Column, (n, r) => n.Row = r, n => n.Label, edges.Select(e => (e.From, e.To)), n => n.Scope is null ? 0 : 1);
        return new RbacChain { Nodes = nodes.Values.ToList(), Edges = edges.ToList(), MaxRows = maxRows, Truncated = truncated, Warning = graph.IncompleteWarning };
    }

}
