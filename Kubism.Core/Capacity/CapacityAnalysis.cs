using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Views;

namespace Kubism.Core.Capacity;

public sealed record NodeCapacity(string Name, double CpuAllocatable, double MemoryAllocatable, int PodsAllocatable, double CpuRequested, double MemoryRequested, int Pods, bool Unschedulable, bool Ready)
{
    public double CpuFree => Math.Max(0, CpuAllocatable - CpuRequested);
    public double MemoryFree => Math.Max(0, MemoryAllocatable - MemoryRequested);
    public double CpuRatio => CpuAllocatable <= 0 ? 0 : CpuRequested / CpuAllocatable;
    public double MemoryRatio => MemoryAllocatable <= 0 ? 0 : MemoryRequested / MemoryAllocatable;
    public bool Accepts => Ready && !Unschedulable;
}

/// <summary>A pod nobody scheduled, with what it asks for, why the scheduler said no, and whether any node could actually take it.</summary>
public sealed record PendingPod(string Namespace, string Name, double CpuRequest, double MemoryRequest, string Reason, IReadOnlyList<string> FitsOn, string Verdict)
{
    public string Display => $"{Namespace}/{Name}";
}

/// <summary>"Cost" of a group of pods: what they request, priced, and their share of what the cluster requests.</summary>
public sealed record CostLine(string Key, int Pods, double CpuRequested, double MemoryRequested, double? MonthlyCost, double Share);

public sealed record CapacityReport(IReadOnlyList<NodeCapacity> Nodes, IReadOnlyList<PendingPod> Pending)
{
    public double CpuAllocatable => Nodes.Sum(n => n.CpuAllocatable);
    public double MemoryAllocatable => Nodes.Sum(n => n.MemoryAllocatable);
    public double CpuRequested => Nodes.Sum(n => n.CpuRequested);
    public double MemoryRequested => Nodes.Sum(n => n.MemoryRequested);
    public double CpuFree => Nodes.Where(n => n.Accepts).Sum(n => n.CpuFree);
    public double MemoryFree => Nodes.Where(n => n.Accepts).Sum(n => n.MemoryFree);
    public double LargestFreeCpu => Nodes.Where(n => n.Accepts).Select(n => n.CpuFree).DefaultIfEmpty(0).Max();
    public double LargestFreeMemory => Nodes.Where(n => n.Accepts).Select(n => n.MemoryFree).DefaultIfEmpty(0).Max();
    /// <summary>Share of the free CPU that no single node holds: 0 when it is all on one node, near 1 when it is spread thin.</summary>
    public double CpuFragmentation => CpuFree <= 0 ? 0 : 1 - LargestFreeCpu / CpuFree;
    public double MemoryFragmentation => MemoryFree <= 0 ? 0 : 1 - LargestFreeMemory / MemoryFree;
}

/// <summary>
/// How full the nodes are by what pods <i>request</i> (what the scheduler counts, not what they use), which pending pods cannot be placed and why,
/// what each namespace or label costs, and what the right-sizing recommendations would free.
/// </summary>
public static class CapacityAnalysis
{
    public const double HoursPerMonth = 730;
    private const double Gib = 1024d * 1024 * 1024;

    public static CapacityReport Analyze(IEnumerable<KubeObject> nodes, IEnumerable<KubeObject> pods)
    {
        var podList = pods.ToList();
        var scheduled = podList.Where(p => CountsAgainstNode(p)).GroupBy(p => p.StringAt("spec", "nodeName")!).ToDictionary(g => g.Key, g => g.ToList());
        var caps = new List<NodeCapacity>();
        foreach (var n in nodes)
        {
            var v = new NodeView(n);
            var onNode = scheduled.GetValueOrDefault(n.Name) ?? [];
            var pods_ = (int)Quantities.Parse(n.StringAt("status", "allocatable", "pods") ?? "110");
            caps.Add(new NodeCapacity(n.Name, v.CpuAllocatable, v.MemoryAllocatable, pods_, onNode.Sum(p => Requests(p).cpu), onNode.Sum(p => Requests(p).memory), onNode.Count,
                n.At("spec", "unschedulable")?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true, v.IsReady));
        }
        caps = caps.OrderByDescending(c => Math.Max(c.CpuRatio, c.MemoryRatio)).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        var pending = podList.Where(p => p.StringAt("status", "phase") == "Pending" && p.StringAt("spec", "nodeName") is null && p.DeletionTimestamp is null)
            .Select(p => Explain(p, caps)).OrderBy(p => p.Display, StringComparer.Ordinal).ToList();
        return new CapacityReport(caps, pending);
    }

    /// <summary>Pods that hold room on their node: assigned, and neither finished nor failed.</summary>
    private static bool CountsAgainstNode(KubeObject p) =>
        p.StringAt("spec", "nodeName") is not null && p.StringAt("status", "phase") is not ("Succeeded" or "Failed");

    /// <summary>What the scheduler reserves for a pod: the sum of its containers, or an init container if one asks for more.</summary>
    public static (double cpu, double memory) Requests(KubeObject pod)
    {
        double cpu = 0, mem = 0, initCpu = 0, initMem = 0;
        foreach (var c in pod.At("spec", "containers") as JsonArray ?? [])
        {
            cpu += Quantities.ParseCpu(c?["resources"]?["requests"]?["cpu"]?.ToString());
            mem += Quantities.ParseBytes(c?["resources"]?["requests"]?["memory"]?.ToString());
        }
        foreach (var c in pod.At("spec", "initContainers") as JsonArray ?? [])
        {
            initCpu = Math.Max(initCpu, Quantities.ParseCpu(c?["resources"]?["requests"]?["cpu"]?.ToString()));
            initMem = Math.Max(initMem, Quantities.ParseBytes(c?["resources"]?["requests"]?["memory"]?.ToString()));
        }
        return (Math.Max(cpu, initCpu), Math.Max(mem, initMem));
    }

    private static PendingPod Explain(KubeObject pod, IReadOnlyList<NodeCapacity> nodes)
    {
        var (cpu, mem) = Requests(pod);
        var condition = (pod.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => c["type"]?.ToString() == "PodScheduled");
        var reason = condition?["message"]?.ToString() ?? condition?["reason"]?.ToString() ?? "Waiting to be scheduled";
        var fits = nodes.Where(n => n.Accepts && n.CpuFree >= cpu && n.MemoryFree >= mem && n.Pods < n.PodsAllocatable).Select(n => n.Name).ToList();
        string verdict;
        var accepting = nodes.Where(n => n.Accepts).ToList();
        if (fits.Count > 0) verdict = $"Fits on {string.Join(", ", fits.Take(3))}: look at taints, affinity, volumes or the scheduler";
        else if (accepting.Count == 0) verdict = "No node accepts pods (all cordoned or not ready)";
        else
        {
            var totalCpu = accepting.Sum(n => n.CpuFree); var totalMem = accepting.Sum(n => n.MemoryFree);
            var needs = $"needs {Quantities.FormatCpu(cpu)} CPU and {Quantities.FormatBytes(mem)}";
            var biggest = $"the freest node has {Quantities.FormatCpu(accepting.Max(n => n.CpuFree))} CPU and {Quantities.FormatBytes(accepting.Max(n => n.MemoryFree))} free";
            verdict = totalCpu >= cpu && totalMem >= mem ? $"Fragmented: {needs}, {biggest}; the free room is spread over several nodes"
                : cpu > accepting.Max(n => n.CpuAllocatable) || mem > accepting.Max(n => n.MemoryAllocatable) ? $"Too big for any node: {needs}, the largest node allows {Quantities.FormatCpu(accepting.Max(n => n.CpuAllocatable))} CPU and {Quantities.FormatBytes(accepting.Max(n => n.MemoryAllocatable))}"
                : $"Cluster is full: {needs}, {biggest}";
        }
        return new PendingPod(pod.Namespace ?? "", pod.Name, cpu, mem, reason, fits, verdict);
    }

    // ---------- cost ----------

    /// <summary>Monthly cost of what pods request, grouped by a key (namespace, a label…), biggest first. Prices are per vCPU-hour and per GiB-hour; null prices give no cost.</summary>
    public static IReadOnlyList<CostLine> CostBy(IEnumerable<KubeObject> pods, Func<KubeObject, string> key, double? pricePerVcpuHour, double? pricePerGibHour)
    {
        var groups = pods.Where(p => p.StringAt("status", "phase") is not ("Succeeded" or "Failed")).GroupBy(key)
            .Select(g => (key: g.Key, pods: g.Count(), cpu: g.Sum(p => Requests(p).cpu), mem: g.Sum(p => Requests(p).memory))).ToList();
        var priced = pricePerVcpuHour is not null || pricePerGibHour is not null;
        double Cost(double cpu, double mem) => (cpu * (pricePerVcpuHour ?? 0) + mem / Gib * (pricePerGibHour ?? 0)) * HoursPerMonth;
        var total = groups.Sum(g => priced ? Cost(g.cpu, g.mem) : g.cpu);
        return groups.Select(g => new CostLine(g.key, g.pods, g.cpu, g.mem, priced ? Cost(g.cpu, g.mem) : null, total <= 0 ? 0 : (priced ? Cost(g.cpu, g.mem) : g.cpu) / total))
            .OrderByDescending(l => l.MonthlyCost ?? l.CpuRequested).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
    }

    public static double MonthlyCost(double cpuCores, double memoryBytes, double? pricePerVcpuHour, double? pricePerGibHour) =>
        (cpuCores * (pricePerVcpuHour ?? 0) + memoryBytes / Gib * (pricePerGibHour ?? 0)) * HoursPerMonth;

    // ---------- what if ----------

    public sealed record NamespaceSimulation(string Namespace, double CpuNow, double CpuAfter, double MemoryNow, double MemoryAfter, double? MonthlySaving);

    public sealed record Simulation(IReadOnlyList<NamespaceSimulation> Namespaces, double CpuNow, double CpuAfter, double MemoryNow, double MemoryAfter, double? MonthlySaving, int NodesNow, int NodesNeeded, int ContainersChanged)
    {
        public double CpuSaved => CpuNow - CpuAfter;
        public double MemorySaved => MemoryNow - MemoryAfter;
        public int NodesFreed => Math.Max(0, NodesNow - NodesNeeded);
    }

    /// <param name="Replicas">Requests are per replica; the simulation multiplies them back.</param>
    public sealed record Container(string Namespace, int Replicas, double? CpuRequest, double? MemoryRequest, double? CpuRecommended, double? MemoryRecommended);

    /// <summary>
    /// Applies the recommendations to the requests of the containers that have one (the others keep theirs), and estimates how many nodes of the
    /// current average size the new total needs. Requests only grow when a recommendation says so: a container under-provisioned today costs more after.
    /// </summary>
    public static Simulation Simulate(IEnumerable<Container> containers, CapacityReport capacity, double? pricePerVcpuHour, double? pricePerGibHour)
    {
        var list = containers.ToList();
        double CpuAfter(Container c) => (c.CpuRecommended ?? c.CpuRequest ?? 0) * c.Replicas;
        double MemAfter(Container c) => (c.MemoryRecommended ?? c.MemoryRequest ?? 0) * c.Replicas;
        var perNs = list.GroupBy(c => c.Namespace).Select(g =>
        {
            var cpuNow = g.Sum(c => (c.CpuRequest ?? 0) * c.Replicas); var memNow = g.Sum(c => (c.MemoryRequest ?? 0) * c.Replicas);
            var cpuAfter = g.Sum(CpuAfter); var memAfter = g.Sum(MemAfter);
            double? saving = pricePerVcpuHour is null && pricePerGibHour is null ? null : MonthlyCost(cpuNow - cpuAfter, memNow - memAfter, pricePerVcpuHour, pricePerGibHour);
            return new NamespaceSimulation(g.Key, cpuNow, cpuAfter, memNow, memAfter, saving);
        }).OrderByDescending(n => n.MonthlySaving ?? n.CpuNow - n.CpuAfter).ThenBy(n => n.Namespace, StringComparer.Ordinal).ToList();

        var cpuNowTotal = perNs.Sum(n => n.CpuNow); var cpuAfterTotal = perNs.Sum(n => n.CpuAfter);
        var memNowTotal = perNs.Sum(n => n.MemoryNow); var memAfterTotal = perNs.Sum(n => n.MemoryAfter);
        // Requests the report does not cover (pods without a workload, other namespaces) stay as they are.
        var otherCpu = Math.Max(0, capacity.CpuRequested - cpuNowTotal); var otherMem = Math.Max(0, capacity.MemoryRequested - memNowTotal);
        var accepting = capacity.Nodes.Where(n => n.Accepts).ToList();
        var nodesNeeded = accepting.Count;
        if (accepting.Count > 0)
        {
            var avgCpu = accepting.Average(n => n.CpuAllocatable); var avgMem = accepting.Average(n => n.MemoryAllocatable);
            nodesNeeded = Math.Max(1, (int)Math.Ceiling(Math.Max(avgCpu <= 0 ? 0 : (cpuAfterTotal + otherCpu) / avgCpu, avgMem <= 0 ? 0 : (memAfterTotal + otherMem) / avgMem)));
            nodesNeeded = Math.Min(nodesNeeded, accepting.Count);
        }
        double? total = pricePerVcpuHour is null && pricePerGibHour is null ? null : perNs.Sum(n => n.MonthlySaving ?? 0);
        return new Simulation(perNs, cpuNowTotal, cpuAfterTotal, memNowTotal, memAfterTotal, total, accepting.Count, nodesNeeded,
            list.Count(c => c.CpuRecommended is not null && Math.Abs(c.CpuRecommended.Value - (c.CpuRequest ?? 0)) > 1e-9 || c.MemoryRecommended is not null && Math.Abs(c.MemoryRecommended.Value - (c.MemoryRequest ?? 0)) > 1));
    }
}
