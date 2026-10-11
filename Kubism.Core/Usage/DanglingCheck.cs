using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Security;

namespace Kubism.Core.Usage;

/// <summary>An autoscaler or disruption budget that is attached to nothing, or that cannot do what it was written for.</summary>
public sealed record DanglingFinding(string Kind, string? Namespace, string Name, string Problem)
{
    public string Display => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
}

/// <summary>
/// HorizontalPodAutoscalers whose target is gone (or scaled to zero, or shared with another autoscaler) and PodDisruptionBudgets that select no pod
/// or forbid every eviction, which stalls node drains and upgrades. Pure logic over objects already loaded; a kind that was not listed is not judged.
/// </summary>
public static class DanglingCheck
{
    private static readonly HashSet<string> WorkloadKinds = ["Deployment", "StatefulSet", "ReplicaSet"];

    public static IReadOnlyList<DanglingFinding> Analyze(IReadOnlyList<KubeObject> all, IReadOnlySet<string> listedKinds)
    {
        var found = new List<DanglingFinding>();
        var workloads = all.Where(o => WorkloadKinds.Contains(o.Kind)).ToDictionary(o => (o.Kind, o.Namespace, o.Name));
        var pods = all.Where(o => o.Kind == "Pod" && o.StringAt("status", "phase") is not ("Succeeded" or "Failed")).ToList();
        var hpas = all.Where(o => o.Kind == "HorizontalPodAutoscaler").ToList();

        foreach (var hpa in hpas)
        {
            DanglingFinding F(string problem) => new(hpa.Kind, hpa.Namespace, hpa.Name, problem);
            var kind = hpa.StringAt("spec", "scaleTargetRef", "kind"); var name = hpa.StringAt("spec", "scaleTargetRef", "name");
            if (kind is null || name is null || !WorkloadKinds.Contains(kind) || !listedKinds.Contains(kind)) continue;
            if (!workloads.TryGetValue((kind, hpa.Namespace, name), out var target)) { found.Add(F($"Targets {kind}/{name}, which does not exist: it scales nothing")); continue; }
            if (target.IntAt("spec", "replicas") == 0) found.Add(F($"{kind}/{name} is scaled to 0 replicas: an autoscaler cannot scale it up from zero"));
            var rivals = hpas.Where(o => o != hpa && o.Namespace == hpa.Namespace && o.StringAt("spec", "scaleTargetRef", "kind") == kind && o.StringAt("spec", "scaleTargetRef", "name") == name).Select(o => o.Name).ToList();
            if (rivals.Count > 0) found.Add(F($"Shares {kind}/{name} with {string.Join(", ", rivals)}: autoscalers on one target fight each other"));
        }

        if (listedKinds.Contains("Pod"))
            foreach (var pdb in all.Where(o => o.Kind == "PodDisruptionBudget"))
            {
                DanglingFinding F(string problem) => new(pdb.Kind, pdb.Namespace, pdb.Name, problem);
                if (pdb.At("spec", "selector") is not JsonObject selector) { found.Add(F("Has no selector: it matches no pod")); continue; }
                var matching = pods.Count(p => p.Namespace == pdb.Namespace && RbacGraph.SelectorMatches(selector, p.Labels));
                if (matching == 0) { found.Add(F("Selects no pod: it protects nothing")); continue; }
                var min = pdb.At("spec", "minAvailable")?.ToString(); var max = pdb.At("spec", "maxUnavailable")?.ToString();
                if (min is not null && (min == "100%" || int.TryParse(min, out var m) && m >= matching))
                    found.Add(F($"minAvailable {min} with {matching} matching pod(s): no pod can ever be evicted, so a node drain stalls"));
                else if (max is not null && (max == "0%" || max == "0"))
                    found.Add(F($"maxUnavailable {max} with {matching} matching pod(s): no pod can ever be evicted, so a node drain stalls"));
            }
        found.Sort((a, b) => string.CompareOrdinal(a.Display, b.Display));
        return found;
    }
}
