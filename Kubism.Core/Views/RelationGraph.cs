using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Helm;

namespace Kubism.Core.Views;

public enum NodeHealth { Ok, Warn, Error, Unknown }

public sealed class GraphNode(string id, string kind, string? ns, string name)
{
    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string? Namespace { get; } = ns;
    public string Name { get; } = name;
    public string DisplayKind => Kind == HelmReleaseInfo.Kind ? "Helm" : Kind;
    public string Status { get; init; } = "";
    public NodeHealth Health { get; init; } = NodeHealth.Unknown;
    public bool IsRoot { get; init; }
    /// <summary>Level in the top-to-bottom layout (0 = top: ingresses, workloads…).</summary>
    public int Layer { get; set; }
    /// <summary>Position inside the level, left to right.</summary>
    public int Row { get; set; }
}

/// <summary>"From uses or owns To".</summary>
public sealed record GraphEdge(string From, string To, string Label);

/// <summary>
/// The neighbourhood of one object: owners and owned objects, services selecting pods, ingresses routing to services,
/// config maps / secrets / volume claims / service accounts used by workloads, HPAs and nodes. Built from objects already
/// loaded, with a layered layout. Shared config (ConfigMap, Secret…) is shown but never expanded, so one popular ConfigMap
/// does not pull in the whole namespace. A Helm release is a node too, at the top: it manages the objects it rendered
/// (recognised by Helm's own annotations) that no controller in the graph already owns.
/// </summary>
public sealed class RelationGraph
{
    private static readonly HashSet<string> LeafKinds = ["ConfigMap", "Secret", "ServiceAccount", "Node", "PersistentVolume", "PersistentVolumeClaim"];
    private const int MaxDepth = 5;

    public IReadOnlyList<GraphNode> Nodes { get; private init; } = [];
    public IReadOnlyList<GraphEdge> Edges { get; private init; } = [];
    public int Layers { get; private init; }
    public int MaxRows { get; private init; }
    /// <summary>More related objects exist than were drawn.</summary>
    public bool Truncated { get; private init; }

    public static string IdOf(KubeObject o) => $"{o.Kind}/{o.Namespace}/{o.Name}";

    public static RelationGraph Build(KubeObject root, IEnumerable<KubeObject> candidates, int maxNodes = 40, IEnumerable<HelmReleaseInfo>? releases = null)
    {
        var rootId = IdOf(root);
        var byId = new Dictionary<string, KubeObject> { [rootId] = root };
        foreach (var c in candidates)
        {
            var id = IdOf(c);
            if (id == rootId || HelmReleaseInfo.IsReleaseSecret(c)) continue;
            // Old replica sets (scaled to zero by a rollout) are noise unless they are the object being looked at.
            if (c.Kind == "ReplicaSet" && (c.IntAt("spec", "replicas") ?? 1) == 0 && (c.IntAt("status", "replicas") ?? 0) == 0) continue;
            byId.TryAdd(id, c);
        }
        AddReleaseNodes(byId, releases);
        var edges = ComputeEdges(byId);

        // Breadth-first from the root, bounded in depth and size.
        var depth = new Dictionary<string, int> { [rootId] = 0 };
        var queue = new Queue<string>([rootId]);
        var truncated = false;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            // A release reached from one of its objects is shown, not expanded: it would pull in every sibling.
            if (id != rootId && byId[id].Kind == HelmReleaseInfo.Kind) continue;
            var leaf = id != rootId && LeafKinds.Contains(byId[id].Kind);
            if (depth[id] >= MaxDepth) { truncated |= edges.Any(e => Other(e, id) is { } o && !depth.ContainsKey(o)); continue; }
            foreach (var e in edges)
            {
                var other = Other(e, id);
                if (other is null || depth.ContainsKey(other)) continue;
                if (leaf && e.From != id) continue;
                if (depth.Count >= maxNodes) { truncated = true; continue; }
                depth[other] = depth[id] + 1;
                queue.Enqueue(other);
            }
        }
        var included = depth.Keys.ToHashSet();
        var kept = edges.Where(e => included.Contains(e.From) && included.Contains(e.To)).DistinctBy(e => (e.From, e.To, e.Label)).ToList();
        var nodes = included.Select(id => Describe(byId[id], id == rootId)).ToList();
        Layout(nodes, kept);
        return new RelationGraph
        {
            Nodes = nodes, Edges = kept, Truncated = truncated,
            Layers = nodes.Count == 0 ? 0 : nodes.Max(n => n.Layer) + 1,
            MaxRows = nodes.Count == 0 ? 0 : nodes.GroupBy(n => n.Layer).Max(g => g.Count()),
        };
    }

    /// <summary>One synthetic node per release that rendered an object in the graph; status comes from the release secrets when known.</summary>
    private static void AddReleaseNodes(Dictionary<string, KubeObject> byId, IEnumerable<HelmReleaseInfo>? releases)
    {
        var known = (releases ?? []).ToDictionary(r => (r.Namespace, r.Name));
        foreach (var key in byId.Values.Select(HelmReleaseInfo.OfObject).Where(k => k is not null).Select(k => k!.Value).Distinct().ToList())
        {
            var info = known.GetValueOrDefault(key) ?? new HelmReleaseInfo(key.Name, key.Namespace, 0, "");
            byId.TryAdd(IdOf(info.ToObject()), info.ToObject());
        }
    }

    private static string? Other(GraphEdge e, string id) => e.From == id ? e.To : e.To == id ? e.From : null;

    private static List<GraphEdge> ComputeEdges(Dictionary<string, KubeObject> byId)
    {
        var edges = new List<GraphEdge>();
        var all = byId.Values.ToList();
        var byUid = all.Where(o => o.Metadata["uid"] is not null).GroupBy(o => o.Uid).ToDictionary(g => g.Key, g => g.First());
        string? Find(string kind, string? ns, string name) => byId.ContainsKey($"{kind}/{ns}/{name}") ? $"{kind}/{ns}/{name}" : null;

        foreach (var o in all)
        {
            var id = IdOf(o);
            var hasController = false;
            foreach (var owner in o.OwnerReferences)
            {
                var ownerId = byUid.TryGetValue(owner.Uid, out var ob) ? IdOf(ob) : Find(owner.Kind, o.Namespace, owner.Name);
                if (ownerId is null) continue;
                edges.Add(new GraphEdge(ownerId, id, "owns"));
                if (owner.Controller) hasController = true;
            }
            switch (o.Kind)
            {
                case "Service":
                    var sel = KubeObject.StringMap(o.At("spec", "selector"));
                    if (sel.Count > 0)
                        foreach (var p in all.Where(p => p.Kind == "Pod" && p.Namespace == o.Namespace && sel.All(kv => p.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value)))
                            edges.Add(new GraphEdge(id, IdOf(p), "selects"));
                    break;
                case "Ingress":
                    foreach (var svc in IngressBackends(o))
                        if (Find("Service", o.Namespace, svc) is { } sid) edges.Add(new GraphEdge(id, sid, "routes to"));
                    break;
                case "HorizontalPodAutoscaler":
                    if (o.StringAt("spec", "scaleTargetRef", "kind") is { } tk && o.StringAt("spec", "scaleTargetRef", "name") is { } tn && Find(tk, o.Namespace, tn) is { } tid)
                        edges.Add(new GraphEdge(id, tid, "scales"));
                    break;
                case "PersistentVolumeClaim":
                    if (o.StringAt("spec", "volumeName") is { } pv && Find("PersistentVolume", null, pv) is { } pvId) edges.Add(new GraphEdge(id, pvId, "bound to"));
                    break;
                case "Pod":
                    if (o.StringAt("spec", "nodeName") is { } node && Find("Node", null, node) is { } nid) edges.Add(new GraphEdge(id, nid, "runs on"));
                    break;
            }
            if (!hasController && HelmReleaseInfo.OfObject(o) is { } rel)
            {
                var relId = $"{HelmReleaseInfo.Kind}/{rel.Namespace}/{rel.Name}";
                if (byId.ContainsKey(relId)) edges.Add(new GraphEdge(relId, id, "manages"));
            }
            // Config used by a workload comes from its own spec/template; pods and replica sets it owns would only repeat it.
            if (!hasController && PodSpecOf(o) is { } podSpec)
                foreach (var (kind, name, label) in PodSpecRefs(podSpec))
                    if (Find(kind, o.Namespace, name) is { } rid) edges.Add(new GraphEdge(id, rid, label));
        }
        return edges;
    }

    private static IEnumerable<string> IngressBackends(KubeObject ingress)
    {
        if (ingress.StringAt("spec", "defaultBackend", "service", "name") is { } d) yield return d;
        foreach (var rule in ingress.At("spec", "rules") as JsonArray ?? [])
            foreach (var path in rule?["http"]?["paths"] as JsonArray ?? [])
                if (path?["backend"]?["service"]?["name"]?.ToString() is { } n) yield return n;
    }

    private static JsonObject? PodSpecOf(KubeObject o) => o.Kind switch
    {
        "Pod" => o.At("spec") as JsonObject,
        "CronJob" => o.At("spec", "jobTemplate", "spec", "template", "spec") as JsonObject,
        "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" or "Job" => o.At("spec", "template", "spec") as JsonObject,
        _ => null,
    };

    private static IEnumerable<(string kind, string name, string label)> PodSpecRefs(JsonObject spec)
    {
        foreach (var v in spec["volumes"] as JsonArray ?? [])
        {
            if (v?["configMap"]?["name"]?.ToString() is { } cm) yield return ("ConfigMap", cm, "mounts");
            if (v?["secret"]?["secretName"]?.ToString() is { } s) yield return ("Secret", s, "mounts");
            if (v?["persistentVolumeClaim"]?["claimName"]?.ToString() is { } pvc) yield return ("PersistentVolumeClaim", pvc, "mounts");
            foreach (var src in v?["projected"]?["sources"] as JsonArray ?? [])
            {
                if (src?["configMap"]?["name"]?.ToString() is { } pcm) yield return ("ConfigMap", pcm, "mounts");
                if (src?["secret"]?["name"]?.ToString() is { } ps) yield return ("Secret", ps, "mounts");
            }
        }
        foreach (var field in new[] { "initContainers", "containers" })
            foreach (var c in spec[field] as JsonArray ?? [])
            {
                foreach (var from in c?["envFrom"] as JsonArray ?? [])
                {
                    if (from?["configMapRef"]?["name"]?.ToString() is { } cm) yield return ("ConfigMap", cm, "env from");
                    if (from?["secretRef"]?["name"]?.ToString() is { } s) yield return ("Secret", s, "env from");
                }
                foreach (var env in c?["env"] as JsonArray ?? [])
                {
                    if (env?["valueFrom"]?["configMapKeyRef"]?["name"]?.ToString() is { } cm) yield return ("ConfigMap", cm, "env");
                    if (env?["valueFrom"]?["secretKeyRef"]?["name"]?.ToString() is { } s) yield return ("Secret", s, "env");
                }
            }
        if (spec["serviceAccountName"]?.ToString() is { } sa && sa != "default") yield return ("ServiceAccount", sa, "runs as");
    }

    private static GraphNode Describe(KubeObject o, bool isRoot)
    {
        var (status, health) = o.Kind switch
        {
            "Pod" => PodHealth(new PodView(o)),
            "Node" => NodeHealthOf(new NodeView(o)),
            "Deployment" or "StatefulSet" or "ReplicaSet" => Replicas(new DeploymentView(o)),
            "DaemonSet" => DaemonSetHealth(o),
            "PersistentVolumeClaim" => (o.StringAt("status", "phase") ?? "", o.StringAt("status", "phase") switch { "Bound" => NodeHealth.Ok, "Pending" => NodeHealth.Warn, "Lost" => NodeHealth.Error, _ => NodeHealth.Unknown }),
            "Job" => JobHealth(o),
            HelmReleaseInfo.Kind => ReleaseHealth(o),
            _ => ("", NodeHealth.Unknown),
        };
        return new GraphNode(IdOf(o), o.Kind, o.Namespace, o.Name) { Status = status, Health = health, IsRoot = isRoot };
    }

    private static (string, NodeHealth) PodHealth(PodView p)
    {
        var s = p.Status;
        var health = s switch
        {
            "Running" or "Completed" or "Succeeded" => NodeHealth.Ok,
            "Pending" or "ContainerCreating" or "PodInitializing" or "Terminating" => NodeHealth.Warn,
            _ when s.StartsWith("Init:") && char.IsDigit(s[5]) => NodeHealth.Warn,
            _ => NodeHealth.Error,
        };
        if (health == NodeHealth.Ok && s == "Running" && p.ReadyCount < p.ContainerCount) health = NodeHealth.Warn;
        return (s, health);
    }

    private static (string, NodeHealth) NodeHealthOf(NodeView n) => (n.Status, n.IsReady ? NodeHealth.Ok : NodeHealth.Error);

    private static (string, NodeHealth) Replicas(DeploymentView d) =>
        ($"{d.Ready}/{d.Desired}", d.Ready >= d.Desired ? NodeHealth.Ok : d.Ready == 0 && d.Desired > 0 ? NodeHealth.Error : NodeHealth.Warn);

    private static (string, NodeHealth) DaemonSetHealth(KubeObject o)
    {
        var desired = o.IntAt("status", "desiredNumberScheduled") ?? 0; var ready = o.IntAt("status", "numberReady") ?? 0;
        return ($"{ready}/{desired}", ready >= desired ? NodeHealth.Ok : ready == 0 ? NodeHealth.Error : NodeHealth.Warn);
    }

    private static (string, NodeHealth) ReleaseHealth(KubeObject o)
    {
        var status = o.StringAt("status", "status") ?? ""; var rev = o.IntAt("status", "revision") ?? 0;
        var text = status.Length == 0 ? "" : rev > 0 ? $"rev {rev} · {status}" : status;
        return (text, status switch
        {
            "deployed" => NodeHealth.Ok, "failed" => NodeHealth.Error,
            "pending-install" or "pending-upgrade" or "pending-rollback" or "uninstalling" => NodeHealth.Warn,
            _ => NodeHealth.Unknown,
        });
    }

    private static (string, NodeHealth) JobHealth(KubeObject o)
    {
        if ((o.IntAt("status", "failed") ?? 0) > 0 && (o.IntAt("status", "succeeded") ?? 0) == 0) return ("Failed", NodeHealth.Error);
        if ((o.IntAt("status", "succeeded") ?? 0) > 0) return ("Complete", NodeHealth.Ok);
        return ("Running", NodeHealth.Warn);
    }

    /// <summary>Longest-path layering (edges point downwards), then a few barycenter sweeps to cut crossings.</summary>
    private static void Layout(List<GraphNode> nodes, List<GraphEdge> edges)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        for (var pass = 0; pass <= nodes.Count; pass++)
        {
            var changed = false;
            foreach (var e in edges)
            {
                var from = byId[e.From]; var to = byId[e.To];
                if (to.Layer < from.Layer + 1 && pass < nodes.Count) { to.Layer = from.Layer + 1; changed = true; }
            }
            if (!changed) break;
        }
        var order = nodes.OrderBy(n => n.Kind, StringComparer.Ordinal).ThenBy(n => n.Name, StringComparer.Ordinal).ToList();
        var layers = order.GroupBy(n => n.Layer).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        foreach (var l in layers) for (var i = 0; i < l.Count; i++) l[i].Row = i;
        for (var sweep = 0; sweep < 4; sweep++)
        {
            for (var li = 1; li < layers.Count; li++) Reorder(layers[li], edges, byId, incoming: true);
            for (var li = layers.Count - 2; li >= 0; li--) Reorder(layers[li], edges, byId, incoming: false);
        }
    }

    private static void Reorder(List<GraphNode> layer, List<GraphEdge> edges, Dictionary<string, GraphNode> byId, bool incoming)
    {
        double Bary(GraphNode n)
        {
            var rows = edges.Where(e => incoming ? e.To == n.Id : e.From == n.Id).Select(e => byId[incoming ? e.From : e.To].Row).ToList();
            return rows.Count == 0 ? n.Row : rows.Average();
        }
        var sorted = layer.OrderBy(Bary).ThenBy(n => n.Row).ToList();
        for (var i = 0; i < sorted.Count; i++) sorted[i].Row = i;
        layer.Clear(); layer.AddRange(sorted);
    }
}
