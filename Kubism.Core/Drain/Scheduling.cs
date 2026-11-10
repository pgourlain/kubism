using System.Globalization;
using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Security;

namespace Kubism.Core.Drain;

/// <summary>
/// The kube-scheduler's hard predicates that decide whether an evicted pod can land on another node: nodeSelector, required node affinity,
/// taints and tolerations, volume node affinity. Pure functions over JSON objects. An approximation (no preemption, no scoring).
/// </summary>
public static class Scheduling
{
    /// <summary>Labels the managed Kubernetes offerings and node provisioners put on every node of a pool, tried in this order.</summary>
    public static IReadOnlyList<string> NodeGroupLabels { get; } =
    [
        "kubernetes.azure.com/agentpool", "agentpool", "eks.amazonaws.com/nodegroup", "cloud.google.com/gke-nodepool",
        "karpenter.sh/nodepool", "node.kubernetes.io/pool", "pool",
    ];

    /// <summary>The pool-like labels that at least one of the nodes carries.</summary>
    public static IReadOnlyList<string> GroupLabelsPresent(IEnumerable<KubeObject> nodes)
    {
        var all = nodes.ToList();
        return NodeGroupLabels.Where(l => all.Any(n => n.Labels.ContainsKey(l))).ToList();
    }

    /// <summary>The group (pool) a node belongs to under <paramref name="label"/>; with no label given, the first known pool label the node carries. Null when none.</summary>
    public static string? NodeGroup(KubeObject node, string? label = null)
    {
        if (label is not null) return node.Labels.TryGetValue(label, out var v) ? v : null;
        foreach (var l in NodeGroupLabels) if (node.Labels.TryGetValue(l, out var g)) return g;
        return null;
    }

    /// <summary>Resolves "all nodes of this pool" (by <paramref name="poolLabel"/>, or the first known pool label) and/or explicit node names to node names; throws when nothing matches.</summary>
    public static IReadOnlyList<string> SelectNodes(IEnumerable<KubeObject> nodes, string? pool, IEnumerable<string>? names, string? poolLabel = null)
    {
        var all = nodes.ToList();
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(pool))
        {
            var inPool = all.Where(n => string.Equals(NodeGroup(n, poolLabel), pool, StringComparison.Ordinal)).Select(n => n.Name).ToList();
            if (inPool.Count == 0) throw new ArgumentException($"No node belongs to node pool '{pool}' (looked at the label {poolLabel ?? string.Join(", ", NodeGroupLabels)}).");
            result.AddRange(inPool);
        }
        foreach (var name in names ?? [])
        {
            if (!all.Any(n => n.Name == name)) throw new ArgumentException($"Node '{name}' not found.");
            result.Add(name);
        }
        if (result.Count == 0) throw new ArgumentException("Give a node pool or at least one node.");
        return result.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>The first nodeSelector entry the node does not carry, as <c>key=value</c>; null when the node satisfies the selector.</summary>
    public static string? NodeSelectorMismatch(KubeObject pod, KubeObject node)
    {
        if (pod.At("spec", "nodeSelector") is not JsonObject sel) return null;
        var labels = node.Labels;
        foreach (var kv in sel)
            if (!labels.TryGetValue(kv.Key, out var v) || v != kv.Value?.ToString()) return $"{kv.Key}={kv.Value}";
        return null;
    }

    /// <summary>Required node affinity (<c>requiredDuringSchedulingIgnoredDuringExecution</c>): terms are ORed, requirements inside a term are ANDed.</summary>
    public static bool NodeAffinityMatches(KubeObject pod, KubeObject node)
    {
        var req = pod.At("spec", "affinity", "nodeAffinity", "requiredDuringSchedulingIgnoredDuringExecution") as JsonObject;
        return req is null || NodeSelectorMatches(req, node);
    }

    /// <summary>A <c>NodeSelector</c> (also used by a PersistentVolume's <c>nodeAffinity.required</c>): any term matching is enough.</summary>
    public static bool NodeSelectorMatches(JsonObject nodeSelector, KubeObject node)
    {
        if (nodeSelector["nodeSelectorTerms"] is not JsonArray terms) return true;
        return terms.OfType<JsonObject>().Any(t => TermMatches(t, node));
    }

    private static bool TermMatches(JsonObject term, KubeObject node)
    {
        var labels = node.Labels;
        foreach (var e in (term["matchExpressions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = KubeObject.Str(e, "key") ?? "";
            labels.TryGetValue(key, out var val);
            if (!Requirement(KubeObject.Str(e, "operator"), val, RbacJson.Strings(e["values"]))) return false;
        }
        foreach (var e in (term["matchFields"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var val = KubeObject.Str(e, "key") == "metadata.name" ? node.Name : null;
            if (!Requirement(KubeObject.Str(e, "operator"), val, RbacJson.Strings(e["values"]))) return false;
        }
        return true;
    }

    private static bool Requirement(string? op, string? value, List<string> values) => op switch
    {
        "In" => value is not null && values.Contains(value),
        "NotIn" => value is null || !values.Contains(value),
        "Exists" => value is not null,
        "DoesNotExist" => value is null,
        "Gt" => value is not null && values.Count > 0 && Num(value) is { } a && Num(values[0]) is { } b && a > b,
        "Lt" => value is not null && values.Count > 0 && Num(value) is { } a2 && Num(values[0]) is { } b2 && a2 < b2,
        _ => false,
    };

    private static long? Num(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The first <c>NoSchedule</c>/<c>NoExecute</c> taint of the node the pod does not tolerate, as <c>key=value:Effect</c>; null when all are tolerated.</summary>
    public static string? UntoleratedTaint(KubeObject pod, KubeObject node)
    {
        var tolerations = (pod.At("spec", "tolerations") as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var t in (node.At("spec", "taints") as JsonArray ?? []).OfType<JsonObject>())
        {
            var effect = KubeObject.Str(t, "effect");
            if (effect is not ("NoSchedule" or "NoExecute")) continue;
            if (!tolerations.Any(tol => Tolerates(tol, t)))
            {
                var value = KubeObject.Str(t, "value");
                return $"{KubeObject.Str(t, "key")}{(string.IsNullOrEmpty(value) ? "" : "=" + value)}:{effect}";
            }
        }
        return null;
    }

    private static bool Tolerates(JsonObject tol, JsonObject taint)
    {
        var effect = KubeObject.Str(tol, "effect");
        if (!string.IsNullOrEmpty(effect) && effect != KubeObject.Str(taint, "effect")) return false;
        var key = KubeObject.Str(tol, "key");
        var op = KubeObject.Str(tol, "operator") ?? "Equal";
        if (string.IsNullOrEmpty(key)) return op == "Exists";
        if (key != KubeObject.Str(taint, "key")) return false;
        return op == "Exists" || (KubeObject.Str(tol, "value") ?? "") == (KubeObject.Str(taint, "value") ?? "");
    }

    /// <summary>
    /// Names of the volumes' PersistentVolume node affinities that exclude the node: a zonal disk or a local volume only reachable from some nodes.
    /// Claims that are unbound or unknown add no constraint (<c>WaitForFirstConsumer</c> binds where the pod lands).
    /// </summary>
    public static string? VolumeMismatch(KubeObject pod, KubeObject node, IReadOnlyDictionary<string, KubeObject> claims, IReadOnlyDictionary<string, KubeObject> volumes)
    {
        foreach (var v in (pod.At("spec", "volumes") as JsonArray ?? []).OfType<JsonObject>())
        {
            if (v["persistentVolumeClaim"] is not JsonObject pvc || KubeObject.Str(pvc, "claimName") is not { } claim) continue;
            if (!claims.TryGetValue($"{pod.Namespace}/{claim}", out var c) || c.StringAt("spec", "volumeName") is not { } pvName) continue;
            if (!volumes.TryGetValue(pvName, out var pv)) continue;
            if (pv.At("spec", "nodeAffinity", "required") is JsonObject req && !NodeSelectorMatches(req, node)) return claim;
        }
        return null;
    }

    /// <summary>Host ports a pod binds on its node (<c>hostPort</c>), as <c>proto/port</c>.</summary>
    public static IEnumerable<string> HostPorts(KubeObject pod)
    {
        foreach (var c in (pod.At("spec", "containers") as JsonArray ?? []).OfType<JsonObject>())
            foreach (var p in (c["ports"] as JsonArray ?? []).OfType<JsonObject>())
                if (p["hostPort"] is JsonValue hp && hp.TryGetValue<int>(out var port) && port > 0)
                    yield return $"{KubeObject.Str(p, "protocol") ?? "TCP"}/{port}";
    }
}
