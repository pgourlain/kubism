using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Discovery;
using Kubism.Core.Helm;
using Kubism.Core.Views;
using Xunit;

namespace Kubism.Core.Tests;

public class ExplorationTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static (KubeObject deploy, KubeObject rs, KubeObject pod, KubeObject svc, KubeObject ing, KubeObject cm, KubeObject secret) Web() => (
        O("""{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"web","namespace":"d","uid":"u-dep"},"spec":{"replicas":2,"template":{"spec":{"serviceAccountName":"web-sa","containers":[{"name":"c","image":"nginx:1.27","envFrom":[{"configMapRef":{"name":"cfg"}}],"env":[{"name":"P","valueFrom":{"secretKeyRef":{"name":"creds","key":"p"}}}]}]}}},"status":{"readyReplicas":2}}"""),
        O("""{"apiVersion":"apps/v1","kind":"ReplicaSet","metadata":{"name":"web-1","namespace":"d","uid":"u-rs","ownerReferences":[{"kind":"Deployment","name":"web","uid":"u-dep","controller":true}]},"spec":{"replicas":2},"status":{"replicas":2,"readyReplicas":2}}"""),
        O("""{"apiVersion":"v1","kind":"Pod","metadata":{"name":"web-1-a","namespace":"d","uid":"u-pod","labels":{"app":"web"},"ownerReferences":[{"kind":"ReplicaSet","name":"web-1","uid":"u-rs","controller":true}]},"spec":{"containers":[{"name":"c","image":"nginx:1.27","envFrom":[{"configMapRef":{"name":"cfg"}}]}]},"status":{"phase":"Running","containerStatuses":[{"ready":true,"state":{"running":{}}}]}}"""),
        O("""{"apiVersion":"v1","kind":"Service","metadata":{"name":"web","namespace":"d","uid":"u-svc"},"spec":{"selector":{"app":"web"}}}"""),
        O("""{"apiVersion":"networking.k8s.io/v1","kind":"Ingress","metadata":{"name":"web","namespace":"d","uid":"u-ing"},"spec":{"rules":[{"http":{"paths":[{"backend":{"service":{"name":"web"}}}]}}]}}"""),
        O("""{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"cfg","namespace":"d","uid":"u-cm"}}"""),
        O("""{"apiVersion":"v1","kind":"Secret","metadata":{"name":"creds","namespace":"d","uid":"u-s"},"data":{"p":"c2VjcmV0"}}"""));

    [Fact]
    public void Graph_from_a_pod_reaches_owners_service_ingress_and_config()
    {
        var (deploy, rs, pod, svc, ing, cm, secret) = Web();
        var g = RelationGraph.Build(pod, [deploy, rs, svc, ing, cm, secret]);
        var ids = g.Nodes.Select(n => n.Id).ToHashSet();
        Assert.Equal(7, g.Nodes.Count);
        Assert.Contains("Ingress/d/web", ids);
        Assert.Contains("Secret/d/creds", ids);
        Assert.Contains(g.Edges, e => e is { From: "Deployment/d/web", To: "ReplicaSet/d/web-1", Label: "owns" });
        Assert.Contains(g.Edges, e => e is { From: "Service/d/web", To: "Pod/d/web-1-a", Label: "selects" });
        Assert.Contains(g.Edges, e => e is { From: "Ingress/d/web", To: "Service/d/web", Label: "routes to" });
        Assert.Contains(g.Edges, e => e is { From: "Deployment/d/web", To: "ConfigMap/d/cfg" });
        // The pod's own config comes from the workload that owns it: no duplicate edge from the pod.
        Assert.DoesNotContain(g.Edges, e => e.From == "Pod/d/web-1-a" && e.To == "ConfigMap/d/cfg");
        Assert.True(g.Nodes.Single(n => n.IsRoot).Id == "Pod/d/web-1-a");
    }

    [Fact]
    public void Graph_layers_flow_left_to_right_along_edges_and_rows_are_unique_per_layer()
    {
        var (deploy, rs, pod, svc, ing, cm, secret) = Web();
        var g = RelationGraph.Build(deploy, [rs, pod, svc, ing, cm, secret]);
        var n = g.Nodes.ToDictionary(x => x.Id);
        Assert.All(g.Edges, e => Assert.True(n[e.From].Layer < n[e.To].Layer, $"{e.From} -> {e.To}"));
        Assert.All(g.Nodes.GroupBy(x => x.Layer), l => Assert.Equal(l.Count(), l.Select(x => x.Row).Distinct().Count()));
        Assert.Equal(g.Nodes.Max(x => x.Layer) + 1, g.Layers);
    }

    [Fact]
    public void Graph_does_not_expand_through_shared_config()
    {
        var (deploy, rs, pod, svc, ing, cm, secret) = Web();
        var other = O("""{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"batch","namespace":"d","uid":"u-other"},"spec":{"template":{"spec":{"containers":[{"envFrom":[{"configMapRef":{"name":"cfg"}}]}]}}}}""");
        var g = RelationGraph.Build(deploy, [rs, pod, svc, ing, cm, secret, other]);
        Assert.DoesNotContain(g.Nodes, x => x.Name == "batch");
        // ...but a config map as the root shows everyone using it.
        var fromCm = RelationGraph.Build(cm, [deploy, rs, pod, svc, ing, secret, other]);
        Assert.Contains(fromCm.Nodes, x => x.Name == "batch");
        Assert.Contains(fromCm.Nodes, x => x.Name == "web" && x.Kind == "Deployment");
    }

    [Fact]
    public void Graph_ignores_scaled_down_replica_sets_and_reports_health_and_truncation()
    {
        var (deploy, rs, pod, _, _, _, _) = Web();
        var old = O("""{"apiVersion":"apps/v1","kind":"ReplicaSet","metadata":{"name":"web-0","namespace":"d","uid":"u-old","ownerReferences":[{"kind":"Deployment","name":"web","uid":"u-dep","controller":true}]},"spec":{"replicas":0},"status":{"replicas":0}}""");
        var crash = O("""{"apiVersion":"v1","kind":"Pod","metadata":{"name":"web-1-b","namespace":"d","uid":"u-pod2","ownerReferences":[{"kind":"ReplicaSet","name":"web-1","uid":"u-rs","controller":true}]},"status":{"phase":"Running","containerStatuses":[{"ready":false,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]},"spec":{"containers":[{}]}}""");
        var g = RelationGraph.Build(deploy, [rs, pod, old, crash]);
        Assert.DoesNotContain(g.Nodes, n => n.Name == "web-0");
        Assert.Equal(NodeHealth.Error, g.Nodes.Single(n => n.Name == "web-1-b").Health);
        Assert.Equal(NodeHealth.Ok, g.Nodes.Single(n => n.Name == "web-1-a").Health);
        Assert.Equal(NodeHealth.Ok, g.Nodes.Single(n => n.Kind == "Deployment").Health);
        var small = RelationGraph.Build(deploy, [rs, pod, crash], maxNodes: 2);
        Assert.True(small.Truncated);
        Assert.Equal(2, small.Nodes.Count);
    }

    [Fact]
    public void Graph_links_pvc_to_pv_and_hpa_to_its_target()
    {
        var pod = O("""{"apiVersion":"v1","kind":"Pod","metadata":{"name":"db","namespace":"d","uid":"p"},"spec":{"nodeName":"n1","volumes":[{"persistentVolumeClaim":{"claimName":"data"}}],"containers":[{}]}}""");
        var pvc = O("""{"apiVersion":"v1","kind":"PersistentVolumeClaim","metadata":{"name":"data","namespace":"d","uid":"c"},"spec":{"volumeName":"pv-1"},"status":{"phase":"Bound"}}""");
        var pv = O("""{"apiVersion":"v1","kind":"PersistentVolume","metadata":{"name":"pv-1","uid":"v"}}""");
        var node = O("""{"apiVersion":"v1","kind":"Node","metadata":{"name":"n1","uid":"n"},"status":{"conditions":[{"type":"Ready","status":"True"}]}}""");
        var sts = O("""{"apiVersion":"apps/v1","kind":"StatefulSet","metadata":{"name":"s","namespace":"d","uid":"s"},"spec":{"replicas":1},"status":{}}""");
        var hpa = O("""{"apiVersion":"autoscaling/v2","kind":"HorizontalPodAutoscaler","metadata":{"name":"h","namespace":"d","uid":"h"},"spec":{"scaleTargetRef":{"kind":"StatefulSet","name":"s"}}}""");
        var g = RelationGraph.Build(pod, [pvc, pv, node]);
        Assert.Contains(g.Edges, e => e is { From: "PersistentVolumeClaim/d/data", To: "PersistentVolume//pv-1", Label: "bound to" });
        Assert.Contains(g.Edges, e => e is { From: "Pod/d/db", To: "Node//n1", Label: "runs on" });
        Assert.Contains(RelationGraph.Build(sts, [hpa]).Edges, e => e is { From: "HorizontalPodAutoscaler/d/h", Label: "scales" });
    }

    [Fact]
    public void Compare_ignores_server_noise_and_reports_each_side()
    {
        KubeObject Dep(string name, string image, string rv, int replicas = 2) => O($$$"""{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"{{{name}}}","namespace":"d","uid":"{{{rv}}}","resourceVersion":"{{{rv}}}","creationTimestamp":"2026-01-01T00:00:00Z","generation":{{{rv}}},"managedFields":[{}],"annotations":{"deployment.kubernetes.io/revision":"{{{rv}}}"}},"spec":{"replicas":{{{replicas}}},"template":{"spec":{"containers":[{"name":"c","image":"{{{image}}}"}]} } },"status":{"readyReplicas":{{{rv}}} } }""");
        var res = ClusterCompare.Compare([Dep("same", "a:1", "1"), Dep("drift", "a:1", "2"), Dep("left-only", "a:1", "3")], [Dep("same", "a:1", "9"), Dep("drift", "a:2", "8", 3), Dep("right-only", "a:1", "7")]);
        Assert.Equal(CompareStatus.Different, res.Single(e => e.Name == "drift").Status);
        Assert.Equal(CompareStatus.Same, res.Single(e => e.Name == "same").Status);
        Assert.Equal(CompareStatus.OnlyLeft, res.Single(e => e.Name == "left-only").Status);
        Assert.Equal(CompareStatus.OnlyRight, res.Single(e => e.Name == "right-only").Status);
        var drift = res.Single(e => e.Name == "drift");
        Assert.Contains("- ", drift.Diff); Assert.Contains("+ ", drift.Diff);
        Assert.Contains("- ", drift.Diff.Split('\n').Single(l => l.Contains("a:1")));
        Assert.Contains("+ ", drift.Diff.Split('\n').Single(l => l.Contains("a:2")));
        Assert.Equal(2, drift.Added); Assert.Equal(2, drift.Removed);
        Assert.Equal(CompareStatus.Same, res[^1].Status);   // differences first
    }

    [Fact]
    public void Compare_never_puts_a_secret_value_in_the_diff()
    {
        KubeObject S(string a, string b) => O($$$"""{"apiVersion":"v1","kind":"Secret","metadata":{"name":"s","namespace":"d"},"data":{"same":"{{{a}}}","other":"{{{b}}}"}}""");
        var res = ClusterCompare.Compare([S("c2FtZS12YWx1ZQ==", "bGVmdC12YWx1ZQ==")], [S("c2FtZS12YWx1ZQ==", "cmlnaHQtdmFsdWU=")]);
        var e = Assert.Single(res);
        Assert.Equal(CompareStatus.Different, e.Status);
        foreach (var leak in new[] { "c2FtZS12YWx1ZQ==", "bGVmdC12YWx1ZQ==", "cmlnaHQtdmFsdWU=", "same-value", "left-value", "right-value" })
            Assert.DoesNotContain(leak, e.Diff);
        Assert.Contains("•••• A", e.Diff); Assert.Contains("•••• B", e.Diff);
        var only = Assert.Single(ClusterCompare.Compare([S("c2FtZS12YWx1ZQ==", "bGVmdC12YWx1ZQ==")], []));
        Assert.DoesNotContain("c2FtZS12YWx1ZQ==", only.Diff);
    }

    [Fact]
    public void Compare_can_match_by_name_across_namespaces()
    {
        KubeObject Cm(string ns) => O($$$"""{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"app","namespace":"{{{ns}}}"},"data":{"k":"v"} }""");
        Assert.Equal(CompareStatus.Same, Assert.Single(ClusterCompare.Compare([Cm("staging")], [Cm("production")], matchNamespace: false)).Status);
        Assert.Equal(2, ClusterCompare.Compare([Cm("staging")], [Cm("production")], matchNamespace: true).Count);
    }

    [Fact]
    public void LineDiff_compact_keeps_context_around_changes()
    {
        var a = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"l{i}"));
        var b = a.Replace("l10", "X");
        var d = LineDiff.Unified(a, b, context: 2);
        Assert.Contains("- l10", d); Assert.Contains("+ X", d); Assert.Contains("  l8", d); Assert.DoesNotContain("l3\n", d); Assert.Contains("…", d);
        Assert.Equal((1, 1), LineDiff.Count(d));
    }

    [Theory]
    [InlineData("checkout", "Deployment", "production", "nginx:1.27", true)]
    [InlineData("image:nginx", "Deployment", "production", "nginx:1.27", true)]
    [InlineData("image:redis", "Deployment", "production", "nginx:1.27", false)]
    [InlineData("check image:nginx:1.27 ns:production", "Deployment", "production", "nginx:1.27", true)]
    [InlineData("ns:staging", "Deployment", "production", "nginx:1.27", false)]
    [InlineData("nope image:nginx", "Deployment", "production", "nginx:1.27", false)]
    public void Search_query_matches_names_images_and_namespace(string q, string kind, string ns, string image, bool expected)
    {
        var o = O($$$"""{"kind":"{{{kind}}}","metadata":{"name":"checkout","namespace":"{{{ns}}}"},"spec":{"template":{"spec":{"containers":[{"image":"{{{image}}}"}]} } } }""");
        Assert.Equal(expected, SearchQuery.Parse(q).Matches(o));
    }

    [Fact]
    public void Search_query_parses_labels_kinds_and_finds_images_in_every_workload_shape()
    {
        var q = SearchQuery.Parse("app=web tier=fe kind:deploy foo");
        Assert.Equal("app=web,tier=fe", q.LabelSelector);
        Assert.Equal(["foo"], q.Names);
        var deploy = new ResourceKind("apps", "v1", "deployments", "Deployment", true, ["list"], ["deploy"], [], []);
        var pods = new ResourceKind("", "v1", "pods", "Pod", true, ["list"], ["po"], [], []);
        Assert.True(q.WantsKind(deploy, []));
        Assert.False(q.WantsKind(pods, ["pods"]));
        Assert.True(SearchQuery.Parse("image:x").WantsKind(pods, []));
        Assert.Equal(["i:1", "c:2"], SearchQuery.ImagesOf(O("""{"kind":"Pod","spec":{"initContainers":[{"image":"i:1"}],"containers":[{"image":"c:2"}]}}""")));
        Assert.Equal(["j:1"], SearchQuery.ImagesOf(O("""{"kind":"CronJob","spec":{"jobTemplate":{"spec":{"template":{"spec":{"containers":[{"image":"j:1"}]}}}}}}""")));
    }
}

public class HelmGraphTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static KubeObject Managed(string kind, string name, string release, string extra = "") =>
        O("""{"apiVersion":"v1","kind":"KIND","metadata":{"name":"NAME","namespace":"d","uid":"u-NAME","annotations":{"meta.helm.sh/release-name":"REL","meta.helm.sh/release-namespace":"d"} }EXTRA }"""
            .Replace("KIND", kind).Replace("NAME", name).Replace("REL", release).Replace("EXTRA", extra));

    private const string Spec = ""","spec":{"replicas":1},"status":{"readyReplicas":1}""";

    private static KubeObject ReleaseSecret(string name, int rev, string status) =>
        O("""{"apiVersion":"v1","kind":"Secret","type":"helm.sh/release.v1","metadata":{"name":"sh.helm.release.v1.NAME.vREV","namespace":"d","labels":{"owner":"helm","name":"NAME","version":"REV","status":"STATUS"} } }"""
            .Replace("NAME", name).Replace("REV", rev.ToString()).Replace("STATUS", status));

    [Fact]
    public void Release_secrets_give_the_latest_revision_and_status_of_each_release()
    {
        var infos = HelmReleaseInfo.FromSecrets([ReleaseSecret("web", 1, "superseded"), ReleaseSecret("web", 3, "failed"), ReleaseSecret("web", 2, "superseded"), ReleaseSecret("db", 1, "deployed")]);
        Assert.Equal([("db", 1, "deployed"), ("web", 3, "failed")], infos.Select(i => (i.Name, i.Revision, i.Status)));
    }

    [Fact]
    public void A_release_is_recognised_from_annotations_or_the_standard_labels()
    {
        Assert.Equal(("d", "web"), HelmReleaseInfo.OfObject(Managed("Service", "s", "web")));
        var byLabel = O("""{"kind":"Pod","metadata":{"name":"p","namespace":"d","labels":{"app.kubernetes.io/managed-by":"Helm","app.kubernetes.io/instance":"db"}}}""");
        Assert.Equal(("d", "db"), HelmReleaseInfo.OfObject(byLabel));
        Assert.Null(HelmReleaseInfo.OfObject(O("""{"kind":"Pod","metadata":{"name":"p","namespace":"d","labels":{"app.kubernetes.io/instance":"db"}}}""")));
    }

    [Fact]
    public void Graph_puts_the_release_above_the_objects_it_rendered_and_not_above_their_children()
    {
        var deploy = Managed("Deployment", "web", "web", Spec);
        var rs = O("""{"apiVersion":"apps/v1","kind":"ReplicaSet","metadata":{"name":"web-1","namespace":"d","uid":"u-rs","annotations":{"meta.helm.sh/release-name":"web"},"ownerReferences":[{"kind":"Deployment","name":"web","uid":"u-web","controller":true}]},"spec":{"replicas":1},"status":{"replicas":1,"readyReplicas":1}}""");
        var svc = Managed("Service", "web", "web");
        var g = RelationGraph.Build(deploy, [svc, rs, ReleaseSecret("web", 2, "deployed")], releases: HelmReleaseInfo.FromSecrets([ReleaseSecret("web", 2, "deployed")]));
        var release = g.Nodes.Single(n => n.Kind == "HelmRelease");
        Assert.Equal("rev 2 · deployed", release.Status); Assert.Equal(NodeHealth.Ok, release.Health);
        Assert.Equal(0, release.Layer);
        Assert.Contains(g.Edges, e => e is { From: "HelmRelease/d/web", To: "Deployment/d/web", Label: "manages" });
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "Service");   // a release reached from one object is not expanded to its siblings
        Assert.DoesNotContain(g.Edges, e => e.From.StartsWith("HelmRelease") && e.To.StartsWith("ReplicaSet"));   // owned by the deployment already
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "Secret");                                                   // release secrets are not objects of the graph
    }

    [Fact]
    public void A_release_as_root_shows_everything_it_manages_and_what_runs_below_a_release_reached_from_an_object_is_not_expanded()
    {
        var deploy = Managed("Deployment", "web", "web", Spec);
        var cm = Managed("ConfigMap", "cfg", "web");
        var other = Managed("ConfigMap", "other", "elsewhere");
        var root = new HelmReleaseInfo("web", "d", 1, "failed").ToObject();
        var g = RelationGraph.Build(root, [deploy, cm, other]);
        Assert.Equal(["ConfigMap/d/cfg", "Deployment/d/web", "HelmRelease/d/web"], g.Nodes.Select(n => n.Id).Order());
        Assert.Equal(NodeHealth.Error, g.Nodes.Single(n => n.IsRoot).Health);
        // From the config map: the release shows, but its other objects do not.
        var fromCm = RelationGraph.Build(cm, [deploy, other]);
        Assert.Contains(fromCm.Nodes, n => n.Kind == "HelmRelease");
        Assert.DoesNotContain(fromCm.Nodes, n => n.Kind == "Deployment");
    }
}

public class EventTimelineTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static (KubeObject dep, List<KubeObject> rest, List<KubeObject> events) Web()
    {
        var dep = O("""{"kind":"Deployment","metadata":{"name":"web","namespace":"d","uid":"u-dep","creationTimestamp":"2026-10-01T08:00:00Z"},"status":{"conditions":[{"type":"Available","status":"True","lastTransitionTime":"2026-10-10T09:00:00Z","reason":"MinimumReplicasAvailable"},{"type":"Progressing","status":"False","lastTransitionTime":"2026-10-10T09:30:00Z","reason":"ProgressDeadlineExceeded","message":"ReplicaSet web-2 has timed out progressing."}]}}""");
        var rs1 = O("""{"kind":"ReplicaSet","metadata":{"name":"web-1","namespace":"d","uid":"u-rs1","creationTimestamp":"2026-10-01T08:00:00Z","annotations":{"deployment.kubernetes.io/revision":"1"},"ownerReferences":[{"kind":"Deployment","name":"web","uid":"u-dep","controller":true}]},"spec":{"template":{"spec":{"containers":[{"image":"web:1"}]}}}}""");
        var rs2 = O("""{"kind":"ReplicaSet","metadata":{"name":"web-2","namespace":"d","uid":"u-rs2","creationTimestamp":"2026-10-10T09:10:00Z","annotations":{"deployment.kubernetes.io/revision":"2"},"ownerReferences":[{"kind":"Deployment","name":"web","uid":"u-dep","controller":true}]},"spec":{"template":{"spec":{"containers":[{"image":"web:2"}]}}}}""");
        var pod = O("""{"kind":"Pod","metadata":{"name":"web-2-a","namespace":"d","uid":"u-pod","creationTimestamp":"2026-10-10T09:11:00Z","ownerReferences":[{"kind":"ReplicaSet","name":"web-2","uid":"u-rs2","controller":true}]},"spec":{"nodeName":"n1"},"status":{"conditions":[{"type":"Ready","status":"True","lastTransitionTime":"2026-10-10T09:12:00Z"},{"type":"ContainersReady","status":"False","lastTransitionTime":"2026-10-10T09:13:00Z","reason":"ContainersNotReady"}],"containerStatuses":[{"name":"c","restartCount":3,"lastState":{"terminated":{"reason":"OOMKilled","exitCode":137,"finishedAt":"2026-10-10T09:20:00Z"}}},{"name":"ok","lastState":{"terminated":{"reason":"Completed","exitCode":0,"finishedAt":"2026-10-10T09:21:00Z"}}}]}}""");
        var other = O("""{"kind":"Pod","metadata":{"name":"other","namespace":"d","uid":"u-other","creationTimestamp":"2026-10-10T09:00:00Z"}}""");
        var ev = O("""{"kind":"Event","type":"Warning","reason":"BackOff","message":"Back-off restarting","count":5,"lastTimestamp":"2026-10-10T09:25:00Z","involvedObject":{"kind":"Pod","name":"web-2-a","uid":"u-pod"}}""");
        var ev2 = O("""{"kind":"Event","type":"Normal","reason":"ScalingReplicaSet","message":"Scaled up web-2 to 3","lastTimestamp":"2026-10-10T09:10:01Z","involvedObject":{"kind":"Deployment","name":"web","uid":"u-dep"}}""");
        var foreign = O("""{"kind":"Event","type":"Warning","reason":"Nope","lastTimestamp":"2026-10-10T09:26:00Z","involvedObject":{"kind":"Pod","name":"other","uid":"u-other"}}""");
        return (dep, [rs1, rs2, pod, other], [ev, ev2, foreign]);
    }

    [Fact]
    public void Timeline_merges_events_rollouts_pods_terminations_and_conditions_newest_first()
    {
        var (dep, rest, events) = Web();
        var items = EventTimeline.Build(dep, rest, events);
        Assert.True(items.Zip(items.Skip(1)).All(p => p.First.Time >= p.Second.Time));
        Assert.Contains(items, i => i is { Kind: TimelineKind.Rollout, Title: "Rollout to revision 2", Detail: "web:2" });
        Assert.Contains(items, i => i is { Kind: TimelineKind.Rollout, Title: "Rollout to revision 1" });
        Assert.Contains(items, i => i is { Kind: TimelineKind.Created, Source: "Pod/web-2-a", Detail: "scheduled on n1" });
        var oom = items.Single(i => i.Kind == TimelineKind.OomKilled);
        Assert.Equal(TimelineSeverity.Error, oom.Severity); Assert.Contains("3 restart", oom.Detail);
        var backoff = items.Single(i => i.Title == "BackOff");
        Assert.Equal(5, backoff.Count); Assert.True(backoff.IsProblem); Assert.Equal("Pod/web-2-a", backoff.Source);
        Assert.Contains(items, i => i.Title.StartsWith("Progressing: False (ProgressDeadlineExceeded)") && i.IsProblem);
        Assert.Contains(items, i => i.Title.StartsWith("Available: True") && !i.IsProblem);
    }

    [Fact]
    public void Timeline_ignores_other_objects_clean_exits_and_healthy_pod_conditions()
    {
        var (dep, rest, events) = Web();
        var items = EventTimeline.Build(dep, rest, events);
        Assert.DoesNotContain(items, i => i.Source == "Pod/other" || i.Title == "Nope");
        Assert.DoesNotContain(items, i => i.Title.Contains("Completed"));
        Assert.DoesNotContain(items, i => i.Title.StartsWith("Ready: True"));
        Assert.Contains(items, i => i.Title.StartsWith("ContainersReady: False"));
    }

    [Fact]
    public void Timeline_of_a_pod_covers_just_that_pod()
    {
        var (_, rest, events) = Web();
        var pod = rest.Single(r => r.Name == "web-2-a");
        var items = EventTimeline.Build(pod, rest.Where(r => r.Name != "web-2-a"), events);
        Assert.All(items, i => Assert.Equal("Pod/web-2-a", i.Source));
        Assert.Contains(items, i => i.Kind == TimelineKind.OomKilled);
        Assert.DoesNotContain(items, i => i.Kind == TimelineKind.Rollout);
    }
}
