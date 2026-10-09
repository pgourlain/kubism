using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Views;

namespace Kubism.Core.RightSizing;

public static class Stats
{
    /// <summary>Percentile with linear interpolation (p in 0..100). Input is not modified.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];
        var rank = Math.Clamp(p, 0, 100) / 100 * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank); var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}

/// <summary>Detects workloads managed by GitOps or Helm, where a direct patch would be reverted.</summary>
public static class GitOps
{
    public static string? Detect(KubeObject workload)
    {
        var l = workload.Labels; var a = workload.Annotations;
        if (l.ContainsKey("argocd.argoproj.io/instance") || a.ContainsKey("argocd.argoproj.io/tracking-id")) return "Argo CD";
        if (l.Keys.Any(k => k.StartsWith("kustomize.toolkit.fluxcd.io/")) || l.Keys.Any(k => k.StartsWith("helm.toolkit.fluxcd.io/"))) return "Flux";
        if (a.Keys.Any(k => k.StartsWith("kustomize.toolkit.fluxcd.io/"))) return "Flux";
        if (l.TryGetValue("app.kubernetes.io/managed-by", out var m) && m == "Helm") return "Helm";
        return null;
    }
}

/// <summary>Pure right-sizing model: workloads from owner references, aggregation across replicas, verdicts, waste, recommendations.</summary>
public sealed class RightSizingEngine(IRightSizingStrategy strategy, RightSizingSettings settings)
{
    public IRightSizingStrategy Strategy { get; } = strategy;
    public RightSizingSettings Settings { get; } = settings;

    /// <summary>Resolves the top-level workload of a pod: Pod → ReplicaSet → Deployment, Pod → Job → CronJob, StatefulSet, DaemonSet; bare pods are their own workload.</summary>
    public static (string kind, string name, KubeObject? obj) ResolveWorkload(KubeObject pod, Func<string, string, string, KubeObject?> lookup)
    {
        var owner = pod.OwnerReferences.FirstOrDefault(o => o.Controller) ?? pod.OwnerReferences.FirstOrDefault();
        if (owner is null) return ("Pod", pod.Name, pod);
        var ns = pod.Namespace ?? "";
        var kind = owner.Kind; var name = owner.Name;
        var obj = lookup(kind, ns, name);
        for (var depth = 0; depth < 3 && obj is not null; depth++)
        {
            var parent = obj.OwnerReferences.FirstOrDefault(o => o.Controller) ?? obj.OwnerReferences.FirstOrDefault();
            if (parent is null || parent.Kind is not ("Deployment" or "CronJob" or "StatefulSet" or "DaemonSet" or "Rollout")) break;
            var next = lookup(parent.Kind, ns, parent.Name);
            kind = parent.Kind; name = parent.Name; obj = next;
            if (next is null) break;
        }
        return (kind, name, obj);
    }

    public RightSizingReport Analyze(IEnumerable<KubeObject> pods, Func<string, string, string, KubeObject?> lookup, UsageHistory history,
        double nodeCpuAllocatable = 0, double nodeMemoryAllocatable = 0)
    {
        var seriesByContainer = history.Series.GroupBy(s => (s.Key.Namespace, s.Key.Container))
            .ToDictionary(g => g.Key, g => g.ToList());
        var seriesByPod = history.Series.ToLookup(s => (s.Key.Namespace, s.Key.Pod, s.Key.Container));

        var groups = pods.Where(p => p.StringAt("status", "phase") != "Succeeded" && p.StringAt("status", "phase") != "Failed")
            .Select(p => (pod: p, w: ResolveWorkload(p, lookup)))
            .GroupBy(x => (ns: x.pod.Namespace ?? "", x.w.kind, x.w.name));

        var workloads = new List<WorkloadReport>();
        foreach (var g in groups.OrderBy(g => g.Key.ns).ThenBy(g => g.Key.name))
        {
            var members = g.ToList();
            var workloadObj = members[0].w.obj is { } o && o.Kind == g.Key.kind ? o : null;
            var gitops = workloadObj is null ? null : GitOps.Detect(workloadObj);
            var first = new PodView(members[0].pod);
            var containers = new List<ContainerReport>();
            foreach (var c in first.Containers.Where(c => !c.IsInit))
            {
                var points = new List<UsagePoint>();
                var extras = new List<ContainerExtras>();
                var restarts = 0; var oomFromStatus = 0;
                foreach (var (pod, _) in members)
                {
                    foreach (var s in seriesByPod[(g.Key.ns, pod.Name, c.Name)]) points.AddRange(s.Points);
                    if (history.Extras.TryGetValue(new ContainerKey(g.Key.ns, pod.Name, c.Name), out var ex)) extras.Add(ex);
                    var cs = (pod.At("status", "containerStatuses") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => x["name"]?.ToString() == c.Name);
                    if (cs is not null)
                    {
                        restarts += int.TryParse(cs["restartCount"]?.ToString(), out var rc) ? rc : 0;
                        if (cs["lastState"]?["terminated"]?["reason"]?.ToString() == "OOMKilled") oomFromStatus++;
                    }
                }
                var vpa = history.Vpa.FirstOrDefault(v => v.Namespace == g.Key.ns && v.WorkloadName == g.Key.name && v.Container == c.Name);
                containers.Add(BuildContainer(g.Key.ns, g.Key.kind, g.Key.name, c, members.Count, members[0].pod.StringAt("status", "qosClass"),
                    points, extras, restarts, Math.Max(extras.Sum(e => e.OomKills), oomFromStatus), history, gitops, vpa));
            }
            workloads.Add(new WorkloadReport(g.Key.ns, g.Key.kind, g.Key.name, members.Count, containers));
        }
        return Summarize(workloads, history, nodeCpuAllocatable, nodeMemoryAllocatable);
    }

    private ContainerReport BuildContainer(string ns, string kind, string workload, ContainerInfo c, int replicas, string? qos,
        List<UsagePoint> points, List<ContainerExtras> extras, int restarts, int oom, UsageHistory history, string? gitops, VpaTarget? vpa)
    {
        double? cpuReq = c.CpuRequest is null ? null : Quantities.ParseCpu(c.CpuRequest);
        double? cpuLim = c.CpuLimit is null ? null : Quantities.ParseCpu(c.CpuLimit);
        double? memReq = c.MemoryRequest is null ? null : Quantities.ParseBytes(c.MemoryRequest);
        double? memLim = c.MemoryLimit is null ? null : Quantities.ParseBytes(c.MemoryLimit);

        var cpu = points.Select(p => p.CpuCores).ToList();
        var mem = points.Select(p => p.MemoryBytes).ToList();
        var throttled = extras.Count == 0 ? 0 : extras.Average(e => e.ThrottledRatio);
        var haveData = points.Count > 0;
        var enoughData = haveData && history.Coverage >= Settings.MinCoverage && history.DataHours >= Settings.MinDataHours;

        var report = new ContainerReport
        {
            Namespace = ns, WorkloadKind = kind, WorkloadName = workload, Container = c.Name, Replicas = replicas, Qos = qos,
            CpuRequest = cpuReq, CpuLimit = cpuLim, MemoryRequest = memReq, MemoryLimit = memLim,
            CpuP50 = Stats.Percentile(cpu, 50), CpuP95 = Stats.Percentile(cpu, 95), CpuP99 = Stats.Percentile(cpu, 99), CpuMax = cpu.Count == 0 ? 0 : cpu.Max(),
            ThrottledRatio = throttled, MemoryP95 = Stats.Percentile(mem, 95), MemoryMax = mem.Count == 0 ? 0 : mem.Max(),
            OomKills = oom, Restarts = restarts, Coverage = history.Coverage, DataHours = history.DataHours, GitOpsManager = gitops,
        };

        var cpuVerdict = CpuVerdict(report, enoughData);
        var memVerdict = MemoryVerdict(report, enoughData);
        RecommendedResources? rec = null;
        if (haveData)
        {
            var cpuAt = Stats.Percentile(cpu, Settings.CpuPercentile);
            rec = Strategy.Recommend(report, cpuAt, Settings);
        }
        else if (vpa is not null)
        {
            rec = new RecommendedResources(Rounding.Cpu(vpa.CpuTarget), null, Rounding.Memory(vpa.MemoryTarget), Rounding.Memory(vpa.MemoryTarget), "VPA recommender target");
        }

        var wasteCpu = cpuReq is { } cr && haveData ? Math.Max(0, cr - report.CpuP95) * replicas : 0;
        var wasteMem = memReq is { } mr && haveData ? Math.Max(0, mr - report.MemoryP95) * replicas : 0;
        double? cost = Settings.PricePerVcpuHour is null && Settings.PricePerGibHour is null ? null
            : wasteCpu * (Settings.PricePerVcpuHour ?? 0) + wasteMem / (1024d * 1024 * 1024) * (Settings.PricePerGibHour ?? 0);
        return report with { CpuVerdict = cpuVerdict, MemoryVerdict = memVerdict, Recommended = rec, WasteCpuCores = wasteCpu, WasteMemoryBytes = wasteMem, WasteCostPerHour = cost };
    }

    private Verdict CpuVerdict(ContainerReport r, bool enough)
    {
        if (r.CpuRequest is null or 0) return Verdict.NoRequest;
        if (r.ThrottledRatio > Settings.ThrottleThreshold) return Verdict.UnderProvisioned;
        if (!enough) return Verdict.NotEnoughData;
        if (r.CpuP95 > r.CpuRequest) return Verdict.UnderProvisioned;
        if (r.CpuP95 < r.CpuRequest * Settings.OverProvisionedRatio) return Verdict.OverProvisioned;
        return Verdict.Ok;
    }

    private Verdict MemoryVerdict(ContainerReport r, bool enough)
    {
        if (r.MemoryRequest is null or 0) return Verdict.NoRequest;
        if (r.OomKills > 0) return Verdict.UnderProvisioned;
        if (!enough) return Verdict.NotEnoughData;
        if (r.MemoryLimit is { } l && l > 0 && r.MemoryMax >= l * Settings.TightLimitRatio) return Verdict.LimitTooTight;
        if (r.MemoryP95 > r.MemoryRequest) return Verdict.UnderProvisioned;
        if (r.MemoryP95 < r.MemoryRequest * Settings.OverProvisionedRatio) return Verdict.OverProvisioned;
        return Verdict.Ok;
    }

    private RightSizingReport Summarize(List<WorkloadReport> workloads, UsageHistory history, double nodeCpu, double nodeMem)
    {
        var all = workloads.SelectMany(w => w.Containers).ToList();
        var rollups = all.GroupBy(c => c.Namespace).Select(g => new NamespaceRollup(g.Key,
            g.Sum(c => (c.CpuRequest ?? 0) * c.Replicas), g.Sum(c => c.CpuP95 * c.Replicas),
            g.Sum(c => (c.MemoryRequest ?? 0) * c.Replicas), g.Sum(c => c.MemoryP95 * c.Replicas),
            g.Sum(c => c.WasteCpuCores), g.Sum(c => c.WasteMemoryBytes))).OrderByDescending(r => r.WasteCpu).ToList();
        double? monthly = all.Any(c => c.WasteCostPerHour is not null) ? all.Sum(c => c.WasteCostPerHour ?? 0) * 730 : null;
        return new RightSizingReport(history.SourceName, history.Window, history.Coverage, history.DataHours, Strategy.Name, workloads, rollups,
            rollups.Sum(r => r.CpuRequested), rollups.Sum(r => r.CpuUsedP95), rollups.Sum(r => r.MemoryRequested), rollups.Sum(r => r.MemoryUsedP95),
            rollups.Sum(r => r.WasteCpu), rollups.Sum(r => r.WasteMemory), monthly,
            all.Count(c => c.Primary == Verdict.UnderProvisioned), all.Count(c => c.Primary == Verdict.NoRequest), nodeCpu, nodeMem,
            all.Sum(c => (c.CpuLimit ?? 0) * c.Replicas), all.Sum(c => (c.MemoryLimit ?? 0) * c.Replicas));
    }
}
