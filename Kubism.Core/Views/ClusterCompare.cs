using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Views;

public enum CompareStatus { Same, Different, OnlyLeft, OnlyRight }

/// <summary>One object compared between two clusters (or two namespaces). <see cref="Diff"/> uses LineDiff's "- " (left) / "+ " (right) convention.</summary>
public sealed record CompareEntry(string? Namespace, string Name, CompareStatus Status, string Diff, int Added, int Removed)
{
    public string Display => Namespace is null ? Name : $"{Namespace}/{Name}";
}

/// <summary>Compares the same kind of object across two clusters: what exists on one side only, and what differs once server-generated noise is removed.</summary>
public static class ClusterCompare
{
    private const string Mask = "••••";

    public static IReadOnlyList<CompareEntry> Compare(IEnumerable<KubeObject> left, IEnumerable<KubeObject> right, bool matchNamespace = true)
    {
        string Key(KubeObject o) => matchNamespace ? $"{o.Namespace}/{o.Name}" : o.Name;
        var l = left.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var r = right.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var result = new List<CompareEntry>();
        foreach (var key in l.Keys.Union(r.Keys).Order(StringComparer.Ordinal))
        {
            l.TryGetValue(key, out var a); r.TryGetValue(key, out var b);
            var any = a ?? b!;
            if (a is null) { var y = ToYaml(b!, null, matchNamespace); result.Add(new(any.Namespace, any.Name, CompareStatus.OnlyRight, Prefix("+ ", y), y.Split('\n').Length, 0)); continue; }
            if (b is null) { var y = ToYaml(a, null, matchNamespace); result.Add(new(any.Namespace, any.Name, CompareStatus.OnlyLeft, Prefix("- ", y), 0, y.Split('\n').Length)); continue; }
            var ya = ToYaml(a, b, matchNamespace); var yb = ToYaml(b, a, matchNamespace, swapped: true);
            if (ya == yb) { result.Add(new(any.Namespace, any.Name, CompareStatus.Same, "", 0, 0)); continue; }
            var diff = LineDiff.Unified(ya, yb);
            var (add, rem) = LineDiff.Count(diff);
            result.Add(new(any.Namespace, any.Name, CompareStatus.Different, LineDiff.Unified(ya, yb, 3), add, rem));
        }
        return result.OrderBy(e => e.Status == CompareStatus.Same ? 1 : 0).ThenBy(e => e.Display, StringComparer.Ordinal).ToList();
    }

    /// <summary>Drops what the server generates per object or per cluster, so only intentional differences remain.</summary>
    public static JsonObject Normalize(JsonObject obj)
    {
        var o = (JsonObject)obj.DeepClone();
        if (o["metadata"] is JsonObject m)
        {
            foreach (var k in new[] { "uid", "resourceVersion", "generation", "creationTimestamp", "managedFields", "selfLink", "ownerReferences", "deletionTimestamp", "deletionGracePeriodSeconds" })
                m.Remove(k);
            if (m["annotations"] is JsonObject ann)
            {
                foreach (var k in ann.Select(kv => kv.Key).Where(k => k is "kubectl.kubernetes.io/last-applied-configuration" or "deployment.kubernetes.io/revision" || k.StartsWith("pv.kubernetes.io/")).ToList())
                    ann.Remove(k);
                if (ann.Count == 0) m.Remove("annotations");
            }
        }
        o.Remove("status");
        if (o["spec"] is JsonObject spec)
            foreach (var k in new[] { "clusterIP", "clusterIPs", "volumeName", "nodeName" }) spec.Remove(k);
        return o;
    }

    /// <summary>Secret values never reach the diff: a key is shown as masked, and a key whose value differs between the sides is marked A / B.</summary>
    private static string ToYaml(KubeObject o, KubeObject? other, bool keepNamespace, bool swapped = false)
    {
        var n = Normalize(o.Json);
        if (!keepNamespace) (n["metadata"] as JsonObject)?.Remove("namespace");
        if (SecretMasker.IsSecret(o.Json))
        {
            n.Remove("stringData");
            if (n["data"] is JsonObject data)
            {
                var otherData = other is not null && SecretMasker.IsSecret(other.Json) ? other.Json["data"] as JsonObject : null;
                foreach (var key in data.Select(kv => kv.Key).ToList())
                {
                    var same = otherData is not null && otherData[key]?.ToString() == data[key]?.ToString();
                    data[key] = other is null || same ? Mask : $"{Mask} {(swapped ? "B" : "A")}";
                }
            }
        }
        return YamlJson.ToYaml(n).TrimEnd('\n');
    }

    private static string Prefix(string p, string text) => string.Concat(text.Split('\n').Select(l => p + l + "\n"));
}
