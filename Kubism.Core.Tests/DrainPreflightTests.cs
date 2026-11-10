using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Drain;
using Xunit;

namespace Kubism.Core.Tests;

public class DrainPreflightTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json.Replace('\'', '"'))!.AsObject());

    // JSON is written with single quotes in this file (O() and the fragments turn them into double quotes).
    private static string J(string s) => s.Replace('\'', '"');

    private static KubeObject Node(string name, string pool, string cpu = "4", string mem = "16Gi", string extraSpec = "", string extraLabels = "", string zone = "z1") =>
        O("{'kind':'Node','metadata':{'name':'" + name + "','labels':{'kubernetes.azure.com/agentpool':'" + pool + "','topology.kubernetes.io/zone':'" + zone + "'" + extraLabels + "}},'spec':{" + extraSpec + "},"
          + "'status':{'allocatable':{'cpu':'" + cpu + "','memory':'" + mem + "','pods':'110'},'conditions':[{'type':'Ready','status':'True'}]}}");

    private static KubeObject Pod(string name, string node, string spec = "", string owner = "[{'apiVersion':'apps/v1','kind':'ReplicaSet','name':'rs1','controller':true}]",
        string labels = "{'app':'web'}", string cpu = "100m") =>
        O("{'kind':'Pod','metadata':{'name':'" + name + "','namespace':'d','labels':" + labels + ",'ownerReferences':" + owner + "},"
          + "'spec':{'nodeName':'" + node + "','containers':[{'name':'c','resources':{'requests':{'cpu':'" + cpu + "','memory':'64Mi'}}}]" + spec + "},'status':{'phase':'Running'}}");

    private static readonly KubeObject Rs = O("{'kind':'ReplicaSet','metadata':{'name':'rs1','namespace':'d','ownerReferences':[{'apiVersion':'apps/v1','kind':'Deployment','name':'web','controller':true}]}}");

    private static DrainSnapshot Snap(KubeObject[] nodes, KubeObject[] pods, KubeObject[]? pdbs = null, KubeObject[]? pvcs = null, KubeObject[]? pvs = null) =>
        new(nodes, pods, [Rs], [], pdbs ?? [], pvcs ?? [], pvs ?? [], []);

    private static KubeObject[] TwoPools() => [Node("old-1", "old"), Node("old-2", "old"), Node("new-1", "new"), Node("new-2", "new")];

    private static PodVerdict V(DrainPlan p, string name) => p.Pods.Single(x => x.Name == name);

    [Fact]
    public void Plain_controlled_pods_move_to_the_other_pool_and_the_plan_can_complete()
    {
        var plan = DrainPreflight.Analyze(Snap(TwoPools(), [Pod("a", "old-1"), Pod("b", "old-2")]), ["old-1", "old-2"]);
        Assert.True(plan.CanComplete);
        Assert.All(plan.Pods, p => Assert.StartsWith("new-", p.MovesTo));
        Assert.Equal("Deployment/web", V(plan, "a").Workload);   // ReplicaSet resolved to its Deployment
    }

    [Fact]
    public void Bare_pod_is_a_blocker()
    {
        var plan = DrainPreflight.Analyze(Snap(TwoPools(), [Pod("bare", "old-1", owner: "[]")]), ["old-1", "old-2"]);
        Assert.False(plan.CanComplete);
        Assert.Contains("Bare pod", V(plan, "bare").Reason);
    }

    [Fact]
    public void Node_selector_pinned_to_the_drained_pool_is_a_blocker()
    {
        var plan = DrainPreflight.Analyze(Snap(TwoPools(), [Pod("pinned", "old-1", spec: ",'nodeSelector':{'kubernetes.azure.com/agentpool':'old'}")]), ["old-1", "old-2"]);
        var v = V(plan, "pinned");
        Assert.Equal(DrainSeverity.Blocker, v.Severity);
        Assert.Contains("nodeSelector kubernetes.azure.com/agentpool=old", v.Reason);
        Assert.Null(v.MovesTo);
    }

    [Fact]
    public void Same_pod_is_fine_when_a_remaining_node_matches_the_selector()
    {
        var plan = DrainPreflight.Analyze(Snap([Node("old-1", "old"), Node("old-2", "old"), Node("keep", "old")], [Pod("p", "old-1", spec: ",'nodeSelector':{'kubernetes.azure.com/agentpool':'old'}")]), ["old-1", "old-2"]);
        Assert.Equal("keep", V(plan, "p").MovesTo);
    }

    [Fact]
    public void Taint_without_toleration_excludes_the_node_and_a_toleration_fixes_it()
    {
        var nodes = new[] { Node("old-1", "old"), Node("new-1", "new", extraSpec: "'taints':[{'key':'dedicated','value':'gpu','effect':'NoSchedule'}]") };
        var blocked = DrainPreflight.Analyze(Snap(nodes, [Pod("p", "old-1")]), ["old-1"]);
        Assert.Contains("taint dedicated=gpu:NoSchedule", V(blocked, "p").Reason);
        var tolerated = DrainPreflight.Analyze(Snap(nodes, [Pod("p", "old-1", spec: ",'tolerations':[{'key':'dedicated','operator':'Equal','value':'gpu','effect':'NoSchedule'}]")]), ["old-1"]);
        Assert.Equal("new-1", V(tolerated, "p").MovesTo);
        var all = DrainPreflight.Analyze(Snap(nodes, [Pod("p", "old-1", spec: ",'tolerations':[{'operator':'Exists'}]")]), ["old-1"]);
        Assert.Equal("new-1", V(all, "p").MovesTo);
    }

    [Fact]
    public void Node_affinity_supports_in_notin_gt_and_fields()
    {
        string Affinity(string expr) => ",'affinity':{'nodeAffinity':{'requiredDuringSchedulingIgnoredDuringExecution':{'nodeSelectorTerms':[{'matchExpressions':[" + expr + "]}]}}}";
        var nodes = new[] { Node("old-1", "old"), Node("new-1", "new", extraLabels: ",'gen':'7'") };
        Assert.Equal("new-1", V(DrainPreflight.Analyze(Snap(nodes, [Pod("a", "old-1", Affinity("{'key':'kubernetes.azure.com/agentpool','operator':'In','values':['new']}"))]), ["old-1"]), "a").MovesTo);
        Assert.Null(V(DrainPreflight.Analyze(Snap(nodes, [Pod("b", "old-1", Affinity("{'key':'kubernetes.azure.com/agentpool','operator':'NotIn','values':['new']}"))]), ["old-1"]), "b").MovesTo);
        Assert.Equal("new-1", V(DrainPreflight.Analyze(Snap(nodes, [Pod("c", "old-1", Affinity("{'key':'gen','operator':'Gt','values':['5']}"))]), ["old-1"]), "c").MovesTo);
        Assert.Null(V(DrainPreflight.Analyze(Snap(nodes, [Pod("d", "old-1", Affinity("{'key':'gen','operator':'Lt','values':['5']}"))]), ["old-1"]), "d").MovesTo);
    }

    [Fact]
    public void Volume_bound_to_a_zone_of_a_drained_node_is_a_blocker()
    {
        var nodes = new[] { Node("old-1", "old"), Node("new-1", "new", zone: "z2") };
        var pvc = O("{'kind':'PersistentVolumeClaim','metadata':{'name':'data','namespace':'d'},'spec':{'volumeName':'pv1'}}");
        var pv = O("{'kind':'PersistentVolume','metadata':{'name':'pv1'},'spec':{'nodeAffinity':{'required':{'nodeSelectorTerms':[{'matchExpressions':[{'key':'topology.kubernetes.io/zone','operator':'In','values':['z1']}]}]}}}}");
        var plan = DrainPreflight.Analyze(Snap(nodes, [Pod("db", "old-1", spec: ",'volumes':[{'name':'v','persistentVolumeClaim':{'claimName':'data'}}]")], pvcs: [pvc], pvs: [pv]), ["old-1"]);
        Assert.Contains("volume data", V(plan, "db").Reason);
    }

    [Fact]
    public void Pdb_with_no_room_blocks_and_a_pdb_with_room_only_serializes()
    {
        string Pdb(int allowed, int healthy, int desired) => "{'kind':'PodDisruptionBudget','metadata':{'name':'web','namespace':'d'},'spec':{'selector':{'matchLabels':{'app':'web'}}},'status':{'disruptionsAllowed':" + allowed + ",'currentHealthy':" + healthy + ",'desiredHealthy':" + desired + "}}";
        var pods = new[] { Pod("a", "old-1"), Pod("b", "old-1") };
        var stuck = DrainPreflight.Analyze(Snap(TwoPools(), pods, [O(Pdb(0, 2, 2))]), ["old-1"]);
        Assert.False(stuck.CanComplete);
        Assert.Contains("PodDisruptionBudget d/web allows no disruption", V(stuck, "a").Reason);
        var roomy = DrainPreflight.Analyze(Snap(TwoPools(), pods, [O(Pdb(1, 3, 2))]), ["old-1"]);
        Assert.True(roomy.CanComplete);
        Assert.Equal(DrainSeverity.Warning, V(roomy, "a").Severity);
        Assert.Contains("serialized", V(roomy, "a").Reason);
        var unhealthy = DrainPreflight.Analyze(Snap(TwoPools(), pods, [O(Pdb(0, 1, 2))]), ["old-1"]);
        Assert.True(unhealthy.CanComplete);   // transient, the drain waits
    }

    [Fact]
    public void Empty_dir_blocks_unless_data_loss_is_accepted()
    {
        var p = Pod("cache", "old-1", spec: ",'volumes':[{'name':'t','emptyDir':{}}]");
        Assert.False(DrainPreflight.Analyze(Snap(TwoPools(), [p]), ["old-1"]).CanComplete);
        var ok = DrainPreflight.Analyze(Snap(TwoPools(), [p]), ["old-1"], new DrainOptions(DeleteEmptyDirData: true));
        Assert.True(ok.CanComplete);
        Assert.Equal(DrainSeverity.Warning, V(ok, "cache").Severity);
    }

    [Fact]
    public void Daemonset_and_mirror_pods_are_not_evicted()
    {
        var ds = Pod("ds", "old-1", owner: "[{'apiVersion':'apps/v1','kind':'DaemonSet','name':'agent','controller':true}]");
        var mirror = O("{'kind':'Pod','metadata':{'name':'static','namespace':'kube-system','annotations':{'kubernetes.io/config.mirror':'x'}},'spec':{'nodeName':'old-1','containers':[]},'status':{'phase':'Running'}}");
        var plan = DrainPreflight.Analyze(Snap(TwoPools(), [ds, mirror]), ["old-1"]);
        Assert.True(plan.CanComplete);
        Assert.All(plan.Pods, p => Assert.Equal(DrainSeverity.Ok, p.Severity));
        Assert.False(DrainPreflight.Analyze(Snap(TwoPools(), [ds]), ["old-1"], new DrainOptions(IgnoreDaemonSets: false)).CanComplete);
    }

    [Fact]
    public void Capacity_is_consumed_as_pods_are_placed()
    {
        var nodes = new[] { Node("old-1", "old"), Node("new-1", "new", cpu: "1") };
        var plan = DrainPreflight.Analyze(Snap(nodes, [Pod("a", "old-1", cpu: "600m"), Pod("b", "old-1", cpu: "600m")]), ["old-1"]);
        Assert.Single(plan.Pods, p => p.Severity == DrainSeverity.Blocker);
        Assert.Contains("CPU requests", plan.Pods.Single(p => p.Severity == DrainSeverity.Blocker).Reason);
    }

    [Fact]
    public void No_remaining_node_blocks_everything()
    {
        var plan = DrainPreflight.Analyze(Snap([Node("only", "old")], [Pod("a", "only")]), ["only"]);
        Assert.Contains("No schedulable node remains", V(plan, "a").Reason);
    }

    [Fact]
    public void Required_anti_affinity_and_topology_spread_use_the_pods_that_stay_or_were_placed()
    {
        var anti = ",'affinity':{'podAntiAffinity':{'requiredDuringSchedulingIgnoredDuringExecution':[{'labelSelector':{'matchLabels':{'app':'web'}},'topologyKey':'kubernetes.io/hostname'}]}}";
        var nodes = new[] { Node("old-1", "old"), Node("new-1", "new") };
        // One replica already on new-1: its twin on old-1 cannot join it.
        var plan = DrainPreflight.Analyze(Snap(nodes, [Pod("a", "old-1", spec: anti), Pod("b", "new-1", spec: anti)]), ["old-1"]);
        Assert.Contains("pod anti-affinity", V(plan, "a").Reason);

        var spread = ",'topologySpreadConstraints':[{'maxSkew':1,'topologyKey':'kubernetes.io/hostname','whenUnsatisfiable':'DoNotSchedule','labelSelector':{'matchLabels':{'app':'web'}}}]";
        var three = new[] { Node("old-1", "old"), Node("new-1", "new"), Node("new-2", "new") };
        var ok = DrainPreflight.Analyze(Snap(three, [Pod("a", "old-1", spec: spread), Pod("b", "new-1", spec: spread)]), ["old-1"]);
        Assert.Equal("new-2", V(ok, "a").MovesTo);   // the empty domain keeps the skew at 1
    }

    [Fact]
    public void Single_replica_and_job_pods_get_a_warning_and_node_pool_comes_from_the_azure_label()
    {
        var plan = DrainPreflight.Analyze(Snap(TwoPools(), [Pod("solo", "old-1", labels: "{'app':'solo'}", owner: "[{'apiVersion':'apps/v1','kind':'StatefulSet','name':'solo','controller':true}]")]), ["old-1"]);
        Assert.Equal(DrainSeverity.Warning, V(plan, "solo").Severity);
        Assert.Equal("old", Scheduling.NodeGroup(TwoPools()[0]));
        Assert.Null(Scheduling.NodeGroup(O("{'kind':'Node','metadata':{'name':'x'}}")));
        // Other providers' labels, and a label chosen by hand.
        var eks = O("{'kind':'Node','metadata':{'name':'e','labels':{'eks.amazonaws.com/nodegroup':'ng-1','team':'blue'}}}");
        Assert.Equal("ng-1", Scheduling.NodeGroup(eks));
        Assert.Equal("blue", Scheduling.NodeGroup(eks, "team"));
        Assert.Equal(["eks.amazonaws.com/nodegroup"], Scheduling.GroupLabelsPresent([eks]));
        Assert.Equal(["old-1", "old-2"], Scheduling.SelectNodes(TwoPools(), "old", null));
    }

    [Fact]
    public void Export_lists_every_pod()
    {
        var plan = DrainPreflight.Analyze(Snap(TwoPools(), [Pod("a", "old-1"), Pod("bare", "old-1", owner: "[]")]), ["old-1"]);
        var md = DrainPlanExport.ToMarkdown(plan);
        Assert.Contains("| Blocker | d/bare |", md);
        Assert.Contains("severity,namespace,pod", DrainPlanExport.ToCsv(plan));
    }
}
