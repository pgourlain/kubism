using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Views;

public sealed record RelatedItem(string Relation, string Kind, string? Namespace, string Name, string? Uid);

/// <summary>Owner chain up, owned objects down, and services selecting a pod, from objects already in the cache.</summary>
public static class Related
{
    public static IReadOnlyList<RelatedItem> For(KubeObject obj, Func<string, string?, string, KubeObject?> lookupOwner, IEnumerable<KubeObject> candidates)
    {
        var result = new List<RelatedItem>();
        // Up: owner chain.
        var cur = obj; var guard = 0;
        while (guard++ < 5)
        {
            var owner = cur.OwnerReferences.FirstOrDefault(o => o.Controller) ?? cur.OwnerReferences.FirstOrDefault();
            if (owner is null) break;
            result.Add(new RelatedItem("Owner", owner.Kind, cur.Namespace, owner.Name, owner.Uid));
            var next = lookupOwner(owner.Kind, cur.Namespace, owner.Name);
            if (next is null) break;
            cur = next;
        }
        var all = candidates.ToList();
        // Down: objects owned by this one.
        foreach (var c in all.Where(c => c.OwnerReferences.Any(o => o.Uid == obj.Uid && o.Uid.Length > 0)))
            result.Add(new RelatedItem("Owned", c.Kind, c.Namespace, c.Name, c.Uid));
        // Services selecting a pod.
        if (obj.Kind == "Pod")
            foreach (var s in all.Where(c => c.Kind == "Service" && c.Namespace == obj.Namespace))
            {
                var sel = KubeObject.StringMap(s.At("spec", "selector"));
                if (sel.Count > 0 && sel.All(kv => obj.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value))
                    result.Add(new RelatedItem("Service", "Service", s.Namespace, s.Name, s.Uid));
            }
        return result;
    }
}
