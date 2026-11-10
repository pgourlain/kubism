using System.Text;
using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Capacity;
using Kubism.Core.Security;

namespace Kubism.Core.Drain;

public enum DrainSeverity { Ok, Warning, Blocker }

/// <summary>What will happen to one pod when its node is drained, and why.</summary>
public sealed record PodVerdict(string Namespace, string Name, string Node, string Workload, DrainSeverity Severity, string Reason, string? MovesTo)
{
    public string Display => $"{Namespace}/{Name}";
}

/// <summary>The pre-flight report for draining a set of nodes: one verdict per pod that sits on them. <see cref="CanComplete"/> is false when some pod would be lost or stay Pending.</summary>
public sealed record DrainPlan(IReadOnlyList<string> Nodes, IReadOnlyList<PodVerdict> Pods, IReadOnlyList<string> Notes)
{
    public int Blockers => Pods.Count(p => p.Severity == DrainSeverity.Blocker);
    public int Warnings => Pods.Count(p => p.Severity == DrainSeverity.Warning);
    public int Oks => Pods.Count(p => p.Severity == DrainSeverity.Ok);
    public bool CanComplete => Blockers == 0;
    public string Summary => $"{Pods.Count} pods on {Nodes.Count} node{(Nodes.Count == 1 ? "" : "s")}: {Oks} ok, {Warnings} warning{(Warnings == 1 ? "" : "s")}, {Blockers} blocker{(Blockers == 1 ? "" : "s")}";
}

/// <summary>Everything the pre-flight reads, with what could not be read so the report never says "ok" about something it did not see.</summary>
public sealed record DrainSnapshot(IReadOnlyList<KubeObject> Nodes, IReadOnlyList<KubeObject> Pods, IReadOnlyList<KubeObject> ReplicaSets, IReadOnlyList<KubeObject> Jobs,
    IReadOnlyList<KubeObject> Pdbs, IReadOnlyList<KubeObject> Claims, IReadOnlyList<KubeObject> Volumes, IReadOnlyList<string> Notes);

/// <summary>Refused by <see cref="ClusterActions.DrainManyAsync"/> because the plan has blockers and <see cref="DrainOptions.Force"/> is off.</summary>
public sealed class DrainBlockedException(DrainPlan plan) : Exception(
    $"Drain refused: {plan.Blockers} pod{(plan.Blockers == 1 ? "" : "s")} would not be migrated ({string.Join("; ", plan.Pods.Where(p => p.Severity == DrainSeverity.Blocker).Take(3).Select(p => $"{p.Display}: {p.Reason}"))}). Nothing was changed.")
{
    public DrainPlan Plan { get; } = plan;
}

/// <summary>
/// Predicts, before anything is changed, which pods a drain would not migrate: bare pods (nothing recreates them), pods no remaining node can run
/// (nodeSelector, node affinity, taints, volume affinity, host ports, pod (anti)affinity, topology spread, requests), PodDisruptionBudgets that never allow an
/// eviction, emptyDir data. It approximates kube-scheduler (no preemption, no scoring, no cluster autoscaler) and says "predicted".
/// </summary>
public static class DrainPreflight
{
    private sealed class NodeState(KubeObject node, NodeCapacity cap)
    {
        public KubeObject Node { get; } = node;
        public double CpuFree { get; set; } = cap.CpuFree;
        public double MemoryFree { get; set; } = cap.MemoryFree;
        public int Pods { get; set; } = cap.Pods;
        public int PodsAllocatable { get; } = cap.PodsAllocatable;
        public HashSet<string> HostPorts { get; } = [];
    }

    private sealed record Placed(KubeObject Pod, KubeObject Node);

    private sealed record PdbInfo(string Key, JsonObject? Selector, string Ns, int? Allowed, int? Healthy, int? Desired);

    public static DrainPlan Analyze(DrainSnapshot s, IReadOnlyCollection<string> selected, DrainOptions? options = null)
    {
        options ??= new DrainOptions();
        var sel = selected.ToHashSet(StringComparer.Ordinal);
        var caps = CapacityAnalysis.Analyze(s.Nodes, s.Pods).Nodes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var nodeByName = s.Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
        var states = s.Nodes.Where(n => !sel.Contains(n.Name) && caps.TryGetValue(n.Name, out var c) && c.Accepts)
            .Select(n => new NodeState(n, caps[n.Name])).ToList();
        var stateByName = states.ToDictionary(x => x.Node.Name, StringComparer.Ordinal);

        var live = s.Pods.Where(p => p.StringAt("status", "phase") is not ("Succeeded" or "Failed")).ToList();
        var world = new List<Placed>();   // pods that stay on remaining nodes, plus those placed by the plan
        foreach (var p in live)
            if (p.StringAt("spec", "nodeName") is { } nn && stateByName.TryGetValue(nn, out var st))
            {
                world.Add(new Placed(p, st.Node));
                foreach (var hp in Scheduling.HostPorts(p)) st.HostPorts.Add(hp);
            }

        var rs = s.ReplicaSets.ToDictionary(o => $"{o.Namespace}/{o.Name}", StringComparer.Ordinal);
        var jobs = s.Jobs.ToDictionary(o => $"{o.Namespace}/{o.Name}", StringComparer.Ordinal);
        var claims = s.Claims.ToDictionary(o => $"{o.Namespace}/{o.Name}", StringComparer.Ordinal);
        var volumes = s.Volumes.ToDictionary(o => o.Name, StringComparer.Ordinal);

        string? Workload(KubeObject pod)
        {
            var c = pod.OwnerReferences.FirstOrDefault(o => o.Controller);
            if (c is null) return null;
            var parentMap = c.Kind == "ReplicaSet" ? rs : c.Kind == "Job" ? jobs : null;
            if (parentMap is not null && parentMap.TryGetValue($"{pod.Namespace}/{c.Name}", out var parent) && parent.OwnerReferences.FirstOrDefault(o => o.Controller) is { } up)
                return $"{up.Kind}/{up.Name}";
            return $"{c.Kind}/{c.Name}";
        }

        var workloadCount = live.Select(Workload).Where(w => w is not null).GroupBy(w => w!).ToDictionary(g => g.Key, g => g.Count());

        var evicted = live.Where(p => p.StringAt("spec", "nodeName") is { } nn && sel.Contains(nn) && p.DeletionTimestamp is null).ToList();
        var pdbs = s.Pdbs.Select(p => new PdbInfo($"{p.Namespace}/{p.Name}", p.At("spec", "selector") as JsonObject, p.Namespace ?? "",
            p.IntAt("status", "disruptionsAllowed"), p.IntAt("status", "currentHealthy"), p.IntAt("status", "desiredHealthy"))).ToList();
        var pdbMatches = new Dictionary<string, List<PdbInfo>>(StringComparer.Ordinal);
        var pdbCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pod in evicted)
        {
            if (!Moves(pod, options)) continue;
            var matched = pdbs.Where(d => d.Ns == pod.Namespace && d.Selector is not null && RbacGraph.SelectorMatches(d.Selector, pod.Labels)).ToList();
            pdbMatches[pod.Uid] = matched;
            foreach (var d in matched) pdbCount[d.Key] = pdbCount.GetValueOrDefault(d.Key) + 1;
        }

        var verdicts = new List<PodVerdict>();
        foreach (var pod in evicted.OrderByDescending(p => CapacityAnalysis.Requests(p).cpu).ThenByDescending(p => CapacityAnalysis.Requests(p).memory).ThenBy(p => p.Uid, StringComparer.Ordinal))
        {
            var node = pod.StringAt("spec", "nodeName")!;
            var workload = Workload(pod) ?? "(none)";
            PodVerdict V(DrainSeverity sev, string reason, string? to = null) => new(pod.Namespace ?? "", pod.Name, node, workload, sev, reason, to);

            if (pod.Annotations.ContainsKey("kubernetes.io/config.mirror")) { verdicts.Add(V(DrainSeverity.Ok, "Static (mirror) pod: not evicted")); continue; }
            if (pod.OwnerReferences.Any(o => o.Kind == "DaemonSet"))
            {
                verdicts.Add(options.IgnoreDaemonSets ? V(DrainSeverity.Ok, "DaemonSet pod: not evicted, stays until the node goes away")
                    : V(DrainSeverity.Blocker, "DaemonSet-managed pod: drain refuses it unless DaemonSets are ignored"));
                continue;
            }
            if (!pod.OwnerReferences.Any(o => o.Controller))
            {
                verdicts.Add(V(DrainSeverity.Blocker, "Bare pod (no controller): it would be deleted and nothing recreates it"));
                continue;
            }

            var sev = DrainSeverity.Ok;
            var reasons = new List<string>();
            void Add(DrainSeverity level, string text) { if (level > sev) sev = level; reasons.Add(text); }

            if (HasEmptyDir(pod))
            {
                if (options.DeleteEmptyDirData) Add(DrainSeverity.Warning, "emptyDir data will be lost");
                else Add(DrainSeverity.Blocker, "uses emptyDir: drain refuses unless 'delete emptyDir data' is set");
            }
            if (pod.OwnerReferences.Any(o => o.Controller && o.Kind == "Job")) Add(DrainSeverity.Warning, "Job pod: the work restarts from scratch");
            else if (workload != "(none)" && workloadCount.GetValueOrDefault(workload) == 1) Add(DrainSeverity.Warning, $"only replica of {workload}: unavailable while it restarts");

            foreach (var d in pdbMatches.GetValueOrDefault(pod.Uid) ?? [])
            {
                var many = pdbCount[d.Key];
                if (d.Allowed is null) Add(DrainSeverity.Warning, $"PodDisruptionBudget {d.Key}: status not read");
                else if (d.Allowed == 0 && d.Healthy is { } h && d.Desired is { } w && h >= w)
                    Add(DrainSeverity.Blocker, $"PodDisruptionBudget {d.Key} allows no disruption (all replicas are needed): the eviction is refused until the budget is relaxed");
                else if (d.Allowed == 0) Add(DrainSeverity.Warning, $"PodDisruptionBudget {d.Key} allows no disruption right now (unhealthy pods): the drain waits");
                else if (many > d.Allowed) Add(DrainSeverity.Warning, $"PodDisruptionBudget {d.Key} allows {d.Allowed} at a time for {many} pods: evictions are serialized");
            }

            var (target, why) = Place(pod, states, world, claims, volumes);
            if (target is null) Add(DrainSeverity.Blocker, why);
            verdicts.Add(V(sev, reasons.Count == 0 ? "Will be recreated by its controller" : string.Join("; ", reasons), target?.Name));
        }

        var notes = s.Notes.ToList();
        var missing = sel.Where(n => !nodeByName.ContainsKey(n)).ToList();
        if (missing.Count > 0) notes.Add($"Node not found: {string.Join(", ", missing)}");
        return new DrainPlan(sel.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            verdicts.OrderByDescending(v => v.Severity).ThenBy(v => v.Node, StringComparer.Ordinal).ThenBy(v => v.Display, StringComparer.Ordinal).ToList(), notes);
    }

    private static bool Moves(KubeObject pod, DrainOptions options) =>
        !pod.Annotations.ContainsKey("kubernetes.io/config.mirror") && !pod.OwnerReferences.Any(o => o.Kind == "DaemonSet") && pod.OwnerReferences.Any(o => o.Controller);

    private static bool HasEmptyDir(KubeObject pod) => (pod.At("spec", "volumes") as JsonArray)?.OfType<JsonObject>().Any(v => v.ContainsKey("emptyDir")) == true;

    /// <summary>Finds a remaining node that can run the pod and reserves its room there; otherwise explains what excluded each node.</summary>
    private static (KubeObject? node, string reason) Place(KubeObject pod, List<NodeState> states, List<Placed> world,
        IReadOnlyDictionary<string, KubeObject> claims, IReadOnlyDictionary<string, KubeObject> volumes)
    {
        if (states.Count == 0) return (null, "No schedulable node remains once these nodes are drained (all cordoned, not ready or selected)");
        var (cpu, mem) = CapacityAnalysis.Requests(pod);
        var hostPorts = Scheduling.HostPorts(pod).ToList();
        var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
        var fitting = new List<NodeState>();
        foreach (var st in states)
        {
            var failure = Failure(pod, st, states, world, claims, volumes, cpu, mem, hostPorts);
            if (failure is null) fitting.Add(st);
            else excluded[failure] = excluded.GetValueOrDefault(failure) + 1;
        }
        if (fitting.Count == 0)
            return (null, $"No remaining node can run it ({string.Join("; ", excluded.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} excluded by {kv.Key}"))}): it would stay Pending");

        // Prefer the node with the most room left, as the scheduler's spreading does, among those that fit.
        var chosen = fitting.OrderByDescending(st => st.CpuFree).ThenBy(st => st.Node.Name, StringComparer.Ordinal).First();
        chosen.CpuFree -= cpu; chosen.MemoryFree -= mem; chosen.Pods++;
        foreach (var hp in hostPorts) chosen.HostPorts.Add(hp);
        world.Add(new Placed(pod, chosen.Node));
        return (chosen.Node, "");
    }

    private static string? Failure(KubeObject pod, NodeState st, List<NodeState> states, List<Placed> world, IReadOnlyDictionary<string, KubeObject> claims,
        IReadOnlyDictionary<string, KubeObject> volumes, double cpu, double mem, List<string> hostPorts)
    {
        var node = st.Node;
        if (Scheduling.NodeSelectorMismatch(pod, node) is { } sel) return $"nodeSelector {sel}";
        if (!Scheduling.NodeAffinityMatches(pod, node)) return "node affinity";
        if (Scheduling.UntoleratedTaint(pod, node) is { } taint) return $"taint {taint}";
        if (Scheduling.VolumeMismatch(pod, node, claims, volumes) is { } claim) return $"volume {claim} (bound to another zone or node)";
        if (hostPorts.Any(st.HostPorts.Contains)) return "hostPort already in use";
        if (PodAffinityFailure(pod, node, world) is { } pa) return pa;
        if (TopologySpreadFailure(pod, node, states, world) is { } ts) return ts;
        if (st.CpuFree < cpu) return "CPU requests";
        if (st.MemoryFree < mem) return "memory requests";
        if (st.Pods >= st.PodsAllocatable) return "pod count limit";
        return null;
    }

    private static string? Domain(KubeObject node, string key) =>
        node.Labels.TryGetValue(key, out var v) ? v : key == "kubernetes.io/hostname" ? node.Name : null;

    private static bool TermMatchesPod(JsonObject term, KubeObject incoming, KubeObject other)
    {
        var namespaces = RbacJson.Strings(term["namespaces"]);
        if (namespaces.Count == 0) namespaces = [incoming.Namespace ?? ""];
        if (!namespaces.Contains(other.Namespace ?? "")) return false;
        return term["labelSelector"] is JsonObject sel && RbacGraph.SelectorMatches(sel, other.Labels);
    }

    private static string? PodAffinityFailure(KubeObject pod, KubeObject node, List<Placed> world)
    {
        foreach (var term in (pod.At("spec", "affinity", "podAntiAffinity", "requiredDuringSchedulingIgnoredDuringExecution") as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = KubeObject.Str(term, "topologyKey") ?? "";
            if (Domain(node, key) is not { } domain) continue;
            if (world.Any(w => Domain(w.Node, key) == domain && TermMatchesPod(term, pod, w.Pod))) return "pod anti-affinity";
        }
        foreach (var term in (pod.At("spec", "affinity", "podAffinity", "requiredDuringSchedulingIgnoredDuringExecution") as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = KubeObject.Str(term, "topologyKey") ?? "";
            var domain = Domain(node, key);
            if (domain is null) return "pod affinity (node lacks the topology key)";
            if (world.Any(w => Domain(w.Node, key) == domain && TermMatchesPod(term, pod, w.Pod))) continue;
            var selfMatches = TermMatchesPod(term, pod, pod);
            if (selfMatches && !world.Any(w => TermMatchesPod(term, pod, w.Pod))) continue;   // the first pod of its own group may start it
            return "pod affinity";
        }
        return null;
    }

    private static string? TopologySpreadFailure(KubeObject pod, KubeObject node, List<NodeState> states, List<Placed> world)
    {
        foreach (var c in (pod.At("spec", "topologySpreadConstraints") as JsonArray ?? []).OfType<JsonObject>())
        {
            if (KubeObject.Str(c, "whenUnsatisfiable") != "DoNotSchedule") continue;
            var key = KubeObject.Str(c, "topologyKey") ?? "";
            var maxSkew = c["maxSkew"] is JsonValue v && v.TryGetValue<int>(out var m) ? m : 1;
            if (Domain(node, key) is not { } mine) return "topology spread (node lacks the topology key)";
            var selector = c["labelSelector"] as JsonObject;
            var counts = states.Where(st => Scheduling.NodeSelectorMismatch(pod, st.Node) is null && Scheduling.NodeAffinityMatches(pod, st.Node))
                .Select(st => Domain(st.Node, key)).Where(d => d is not null).Distinct().ToDictionary(d => d!, _ => 0, StringComparer.Ordinal);
            foreach (var w in world)
                if (w.Pod.Namespace == pod.Namespace && (selector is null || RbacGraph.SelectorMatches(selector, w.Pod.Labels)) && Domain(w.Node, key) is { } d && counts.ContainsKey(d)) counts[d]++;
            if (counts.Count == 0) continue;
            if (counts.GetValueOrDefault(mine) + 1 - counts.Values.Min() > maxSkew) return "topology spread (maxSkew)";
        }
        return null;
    }
}

/// <summary>One-shot read of what <see cref="DrainPreflight"/> needs; for callers with no live cache (the MCP server, the dialog).</summary>
public static class DrainPreflightLoader
{
    public const int ListCap = 20000;

    public static async Task<DrainSnapshot> LoadAsync(IKubeTransport transport, CancellationToken ct)
    {
        var notes = new List<string>();

        async Task<List<KubeObject>> ReadAsync(Gvr gvr, string kind, string? warnWhenMissing)
        {
            var items = new List<KubeObject>();
            try
            {
                string? cont = null;
                do
                {
                    var page = await transport.ListAsync(gvr, null, new ListOptions { Limit = 500, Continue = cont }, ct).ConfigureAwait(false);
                    foreach (var item in page.Items)
                    {
                        var o = new KubeObject(item);
                        if (o.Kind.Length == 0) item["kind"] = kind;
                        items.Add(o);
                    }
                    cont = string.IsNullOrEmpty(page.Continue) ? null : page.Continue;
                } while (cont is not null && items.Count < ListCap);
                if (cont is not null) lock (notes) notes.Add($"{gvr.Resource} truncated at {ListCap}: the prediction may miss some constraints.");
            }
            catch (Exception ex) when (warnWhenMissing is not null && ex is KubeApiException or KubeConnectionException)
            {
                lock (notes) notes.Add($"Could not read {gvr.Resource}: {warnWhenMissing}");
                return [];
            }
            return items;
        }

        // Nodes and pods are required (the failure propagates); the rest only refine the prediction.
        var nodes = ReadAsync(Gvr.Nodes, "Node", null);
        var pods = ReadAsync(Gvr.Pods, "Pod", null);
        var sets = ReadAsync(Gvr.ReplicaSets, "ReplicaSet", "workload names may be less precise.");
        var jobs = ReadAsync(Gvr.Jobs, "Job", "workload names may be less precise.");
        var pdbs = ReadAsync(Gvr.PodDisruptionBudgets, "PodDisruptionBudget", "disruption budgets are not checked.");
        var pvcs = ReadAsync(Gvr.PersistentVolumeClaims, "PersistentVolumeClaim", "volume placement is not checked.");
        var pvs = ReadAsync(Gvr.PersistentVolumes, "PersistentVolume", "volume placement is not checked.");
        await Task.WhenAll(nodes, pods, sets, jobs, pdbs, pvcs, pvs).ConfigureAwait(false);
        return new DrainSnapshot(nodes.Result, pods.Result, sets.Result, jobs.Result, pdbs.Result, pvcs.Result, pvs.Result, notes);
    }
}

/// <summary>Exports a plan for a ticket or a change request.</summary>
public static class DrainPlanExport
{
    public static string ToMarkdown(DrainPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Drain pre-flight: {string.Join(", ", plan.Nodes)}").AppendLine().AppendLine(plan.Summary).AppendLine();
        foreach (var n in plan.Notes) sb.AppendLine($"> {n}");
        if (plan.Notes.Count > 0) sb.AppendLine();
        sb.AppendLine("| Severity | Pod | Node | Workload | Moves to | Reason |").AppendLine("|---|---|---|---|---|---|");
        foreach (var p in plan.Pods)
            sb.AppendLine($"| {p.Severity} | {p.Display} | {p.Node} | {p.Workload} | {p.MovesTo ?? ""} | {p.Reason.Replace("|", "\\|")} |");
        return sb.ToString();
    }

    public static string ToCsv(DrainPlan plan)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("severity,namespace,pod,node,workload,moves_to,reason\n");
        foreach (var p in plan.Pods) sb.Append(p.Severity).Append(',').Append(Q(p.Namespace)).Append(',').Append(Q(p.Name)).Append(',').Append(Q(p.Node)).Append(',').Append(Q(p.Workload)).Append(',').Append(Q(p.MovesTo ?? "")).Append(',').Append(Q(p.Reason)).Append('\n');
        return sb.ToString();
    }
}
