using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Capacity;
using Xunit;

namespace Kubism.Core.Tests;

public class CapacityTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static KubeObject Node(string name, string cpu, string mem, bool cordoned = false, bool ready = true) =>
        O("""{"kind":"Node","metadata":{"name":"NAME"},"spec":{"unschedulable":CORDON},"status":{"allocatable":{"cpu":"CPU","memory":"MEM","pods":"110"},"conditions":[{"type":"Ready","status":"READY"}]}}"""
            .Replace("NAME", name).Replace("CORDON", cordoned ? "true" : "false").Replace("CPU", cpu).Replace("MEM", mem).Replace("READY", ready ? "True" : "False"));

    private static KubeObject Pod(string name, string? node, string cpu, string mem, string phase = "Running", string ns = "d", string labels = "{}", string extra = "") =>
        O("""{"kind":"Pod","metadata":{"name":"NAME","namespace":"NS","labels":LABELS},"spec":{NODE"containers":[{"name":"c","resources":{"requests":{"cpu":"CPU","memory":"MEM"}}}]},"status":{"phase":"PHASE"EXTRA}}"""
            .Replace("NAME", name).Replace("NS", ns).Replace("LABELS", labels).Replace("NODE", node is null ? "" : "\"nodeName\":\"" + node + "\",")
            .Replace("CPU", cpu).Replace("MEM", mem).Replace("PHASE", phase).Replace("EXTRA", extra));

    [Fact]
    public void Nodes_show_requested_against_allocatable_free_room_and_fragmentation()
    {
        var r = CapacityAnalysis.Analyze(
            [Node("a", "4", "16Gi"), Node("b", "4", "16Gi"), Node("c", "4", "16Gi")],
            [Pod("p1", "a", "3", "12Gi"), Pod("p2", "b", "2", "4Gi"), Pod("p3", "b", "500m", "1Gi"), Pod("done", "c", "3", "8Gi", "Succeeded"), Pod("failed", "c", "3", "8Gi", "Failed")]);
        var a = r.Nodes.Single(n => n.Name == "a"); var b = r.Nodes.Single(n => n.Name == "b"); var c = r.Nodes.Single(n => n.Name == "c");
        Assert.Equal(3, a.CpuRequested); Assert.Equal(0.75, a.CpuRatio);
        Assert.Equal(2.5, b.CpuRequested); Assert.Equal(2, b.Pods);
        Assert.Equal(0, c.CpuRequested);   // finished pods hold no room
        Assert.Equal("a", r.Nodes[0].Name);   // fullest first
        Assert.Equal(6.5, r.CpuFree, 5);   // (4 - 3) + (4 - 2.5) + 4
        Assert.Equal(4, r.LargestFreeCpu);
        Assert.Equal(1 - 4 / 6.5, r.CpuFragmentation, 5);
    }

    [Fact]
    public void Cordoned_and_not_ready_nodes_do_not_count_as_free_room()
    {
        var r = CapacityAnalysis.Analyze([Node("a", "4", "8Gi"), Node("b", "8", "32Gi", cordoned: true), Node("c", "8", "32Gi", ready: false)], []);
        Assert.Equal(4, r.CpuFree); Assert.Equal(4, r.LargestFreeCpu);
        Assert.Equal(20, r.CpuAllocatable);   // they still exist
    }

    [Fact]
    public void Pending_pods_get_a_verdict_that_tells_fits_fragmented_and_full_apart()
    {
        var nodes = new[] { Node("a", "4", "16Gi"), Node("b", "4", "16Gi") };
        var running = new[] { Pod("x", "a", "3", "4Gi"), Pod("y", "b", "3", "4Gi") };
        KubeObject Pending(string name, string cpu, string mem) => Pod(name, null, cpu, mem, "Pending", extra: ""","conditions":[{"type":"PodScheduled","status":"False","reason":"Unschedulable","message":"0/2 nodes are available: 2 Insufficient cpu."}]""");
        var r = CapacityAnalysis.Analyze(nodes, running.Concat([Pending("small", "500m", "1Gi"), Pending("frag", "1500m", "1Gi"), Pending("huge", "6", "1Gi")]));
        var small = r.Pending.Single(p => p.Name == "small");
        Assert.Equal(["a", "b"], small.FitsOn);
        Assert.StartsWith("Fits on", small.Verdict);
        Assert.Contains("Insufficient cpu", small.Reason);
        var frag = r.Pending.Single(p => p.Name == "frag");
        Assert.Empty(frag.FitsOn);
        Assert.StartsWith("Fragmented", frag.Verdict);   // 1 free on each node: 2 in total would fit 1.5, but no single node does
        Assert.StartsWith("Too big for any node", r.Pending.Single(p => p.Name == "huge").Verdict);
    }

    [Fact]
    public void A_full_cluster_and_a_cluster_with_nothing_schedulable_say_so()
    {
        var full = CapacityAnalysis.Analyze([Node("a", "4", "16Gi")], [Pod("x", "a", "3800m", "4Gi"), Pod("w", null, "1", "1Gi", "Pending")]);
        Assert.StartsWith("Cluster is full", full.Pending.Single().Verdict);
        var none = CapacityAnalysis.Analyze([Node("a", "4", "16Gi", cordoned: true)], [Pod("w", null, "1", "1Gi", "Pending")]);
        Assert.StartsWith("No node accepts pods", none.Pending.Single().Verdict);
    }

    [Fact]
    public void Pod_requests_are_the_container_sum_or_the_largest_init_container()
    {
        var p = O("""{"kind":"Pod","metadata":{"name":"p"},"spec":{"initContainers":[{"resources":{"requests":{"cpu":"2","memory":"1Gi"}}}],"containers":[{"resources":{"requests":{"cpu":"500m","memory":"256Mi"}}},{"resources":{"requests":{"cpu":"250m"}}}]}}""");
        var (cpu, mem) = CapacityAnalysis.Requests(p);
        Assert.Equal(2, cpu); Assert.Equal(1024d * 1024 * 1024, mem);
        Assert.Equal((0.0, 0.0), CapacityAnalysis.Requests(O("""{"kind":"Pod","metadata":{"name":"p"},"spec":{"containers":[{}]}}""")));
    }

    [Fact]
    public void Cost_groups_requests_by_namespace_or_label_and_prices_them_per_month()
    {
        var pods = new[]
        {
            Pod("a1", "n", "2", "4Gi", ns: "shop", labels: "{\"team\":\"web\"}"), Pod("a2", "n", "2", "4Gi", ns: "shop", labels: "{\"team\":\"web\"}"),
            Pod("b1", "n", "1", "2Gi", ns: "ops", labels: "{\"team\":\"sre\"}"), Pod("gone", "n", "9", "9Gi", "Succeeded", ns: "ops"),
        };
        var byNs = CapacityAnalysis.CostBy(pods, p => p.Namespace!, 0.03, 0.004);
        Assert.Equal(["shop", "ops"], byNs.Select(l => l.Key));
        Assert.Equal((4 * 0.03 + 8 * 0.004) * 730, byNs[0].MonthlyCost!.Value, 6);
        Assert.Equal(2, byNs[0].Pods); Assert.Equal(4, byNs[0].CpuRequested);
        Assert.Equal(1.0, byNs.Sum(l => l.Share), 6);
        var byTeam = CapacityAnalysis.CostBy(pods, p => p.Labels.GetValueOrDefault("team") ?? "(no team)", 0.03, 0.004);
        Assert.Equal(["web", "sre"], byTeam.Select(l => l.Key));   // finished pods are not counted, so nothing falls in "(no team)"
        var unpriced = CapacityAnalysis.CostBy(pods, p => p.Namespace!, null, null);
        Assert.All(unpriced, l => Assert.Null(l.MonthlyCost));
        Assert.Equal(4d / 5d, unpriced[0].Share, 6);   // without prices the share is by CPU
    }

    [Fact]
    public void Simulation_applies_recommendations_and_estimates_freed_nodes_and_money()
    {
        var capacity = CapacityAnalysis.Analyze([Node("a", "4", "16Gi"), Node("b", "4", "16Gi"), Node("c", "4", "16Gi")],
            [Pod("p1", "a", "3", "8Gi"), Pod("p2", "b", "3", "8Gi"), Pod("p3", "c", "3", "8Gi")]);
        double Gi(double g) => g * 1024 * 1024 * 1024;
        var containers = new[]
        {
            new CapacityAnalysis.Container("shop", 3, 3, Gi(8), 0.5, Gi(2)),   // heavily over-provisioned
            new CapacityAnalysis.Container("ops", 1, 1, Gi(1), null, null),    // no recommendation: unchanged
        };
        var sim = CapacityAnalysis.Simulate(containers, capacity, 0.03, 0.004);
        Assert.Equal(10, sim.CpuNow); Assert.Equal(1.5 + 1, sim.CpuAfter);
        Assert.Equal(7.5, sim.CpuSaved);
        Assert.Equal(1, sim.ContainersChanged);
        Assert.Equal((7.5 * 0.03 + 18 * 0.004) * 730, sim.MonthlySaving!.Value, 6);
        Assert.Equal(3, sim.NodesNow);
        Assert.Equal(1, sim.NodesNeeded);   // the cluster requests 9 cores, the report covers 10 (clamped to 0 left over), so 2.5 cores remain: one node
        Assert.Equal(2, sim.NodesFreed);
        Assert.Equal("shop", sim.Namespaces[0].Namespace);   // the biggest saving first
    }

    [Fact]
    public void Simulation_without_prices_has_no_money_and_a_raised_request_costs_more()
    {
        var capacity = CapacityAnalysis.Analyze([Node("a", "4", "16Gi")], []);
        var sim = CapacityAnalysis.Simulate([new CapacityAnalysis.Container("x", 2, 0.1, 1e8, 0.5, 2e8)], capacity, null, null);
        Assert.Null(sim.MonthlySaving);
        Assert.True(sim.CpuSaved < 0);
    }
}
