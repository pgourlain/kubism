using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Usage;
using Xunit;

namespace Kubism.Core.Tests;

public class ResourceUsageTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());
    private static readonly HashSet<string> Listed = [.. ResourceUsage.AnalyzedKinds];

    private static UsageRow Row(ResourceUsage u, string kind, string name) => u.Rows.Single(r => r.Kind == kind && r.Name == name);

    private const string Deploy = """{"kind":"Deployment","metadata":{"name":"web","namespace":"d"},"spec":{"template":{"spec":{"imagePullSecrets":[{"name":"reg"}],"volumes":[{"name":"v","configMap":{"name":"cfg-vol"}},{"name":"d","persistentVolumeClaim":{"claimName":"data"}},{"name":"p","projected":{"sources":[{"secret":{"name":"proj"}}]}}],"containers":[{"name":"c","envFrom":[{"configMapRef":{"name":"cfg-env"}}],"env":[{"name":"A","valueFrom":{"secretKeyRef":{"name":"creds","key":"k"}}}]}]}}}}""";

    [Fact]
    public void References_from_a_template_are_found_and_orphans_are_unused()
    {
        var u = ResourceUsage.Analyze(
        [
            O(Deploy),
            O("""{"kind":"ConfigMap","metadata":{"name":"cfg-vol","namespace":"d"}}"""), O("""{"kind":"ConfigMap","metadata":{"name":"cfg-env","namespace":"d"}}"""),
            O("""{"kind":"ConfigMap","metadata":{"name":"orphan","namespace":"d"}}"""),
            O("""{"kind":"Secret","metadata":{"name":"creds","namespace":"d"}}"""), O("""{"kind":"Secret","metadata":{"name":"reg","namespace":"d"}}"""),
            O("""{"kind":"Secret","metadata":{"name":"proj","namespace":"d"}}"""), O("""{"kind":"Secret","metadata":{"name":"old","namespace":"d"}}"""),
            O("""{"kind":"PersistentVolumeClaim","metadata":{"name":"data","namespace":"d"},"spec":{"storageClassName":"fast"},"status":{"phase":"Bound"}}"""),
        ], Listed);
        Assert.Equal(UsageStatus.Used, Row(u, "ConfigMap", "cfg-vol").Status);
        Assert.Equal("env", Row(u, "Secret", "creds").Consumers.Single().How.Replace("envFrom", "env"));
        Assert.Equal("envFrom", Row(u, "ConfigMap", "cfg-env").Consumers.Single().How);
        Assert.Equal("imagePullSecrets", Row(u, "Secret", "reg").Consumers.Single().How);
        Assert.Equal(UsageStatus.Used, Row(u, "Secret", "proj").Status);
        Assert.Equal(UsageStatus.Used, Row(u, "PersistentVolumeClaim", "data").Status);
        Assert.Equal(UsageStatus.Unused, Row(u, "ConfigMap", "orphan").Status);
        Assert.Equal(UsageStatus.Unused, Row(u, "Secret", "old").Status);
    }

    [Fact]
    public void Pods_a_controller_owns_do_not_count_twice_but_bare_pods_do()
    {
        var u = ResourceUsage.Analyze(
        [
            O("""{"kind":"Pod","metadata":{"name":"w-1","namespace":"d","ownerReferences":[{"kind":"ReplicaSet","name":"w","controller":true}]},"spec":{"containers":[{"name":"c","envFrom":[{"configMapRef":{"name":"cfg"}}]}]}}"""),
            O("""{"kind":"Pod","metadata":{"name":"bare","namespace":"d"},"spec":{"containers":[{"name":"c","envFrom":[{"configMapRef":{"name":"cfg"}}]}]}}"""),
            O("""{"kind":"ConfigMap","metadata":{"name":"cfg","namespace":"d"}}"""),
        ], Listed);
        Assert.Equal("Pod/d/bare", Row(u, "ConfigMap", "cfg").Consumers.Single().Display);
    }

    [Fact]
    public void Missing_targets_are_broken_unless_optional_or_not_listed()
    {
        var pod = """{"kind":"Pod","metadata":{"name":"p","namespace":"d"},"spec":{"volumes":[{"name":"a","configMap":{"name":"gone"}},{"name":"b","configMap":{"name":"maybe","optional":true}},{"name":"c","secret":{"secretName":"hidden"}}],"containers":[{"name":"c"}]}}""";
        var u = ResourceUsage.Analyze([O(pod)], new HashSet<string> { ResourceUsage.ConfigMap });
        var b = Assert.Single(u.Broken);
        Assert.Equal("gone", b.TargetName);
        Assert.Equal("Pod/d/p", b.Consumer.Display);
    }

    [Fact]
    public void External_secrets_are_not_called_unused()
    {
        var u = ResourceUsage.Analyze(
        [
            O("""{"kind":"Secret","type":"helm.sh/release.v1","metadata":{"name":"sh.helm.release.v1.x.v1","namespace":"d"}}"""),
            O("""{"kind":"Secret","type":"kubernetes.io/service-account-token","metadata":{"name":"tok","namespace":"d"}}"""),
            O("""{"kind":"ConfigMap","metadata":{"name":"kube-root-ca.crt","namespace":"d"}}"""),
            O("""{"kind":"Ingress","metadata":{"name":"i","namespace":"d"},"spec":{"tls":[{"secretName":"site-tls"}]}}"""),
            O("""{"kind":"Secret","type":"kubernetes.io/tls","metadata":{"name":"site-tls","namespace":"d"}}"""),
        ], Listed);
        Assert.Equal(UsageStatus.External, Row(u, "Secret", "sh.helm.release.v1.x.v1").Status);
        Assert.Equal(UsageStatus.External, Row(u, "Secret", "tok").Status);
        Assert.Equal(UsageStatus.External, Row(u, "ConfigMap", "kube-root-ca.crt").Status);
        Assert.Equal("Ingress/d/i", Row(u, "Secret", "site-tls").Consumers.Single().Display);
    }

    [Fact]
    public void Storage_pvc_pv_and_class()
    {
        var u = ResourceUsage.Analyze(
        [
            O("""{"kind":"StatefulSet","metadata":{"name":"db","namespace":"d"},"spec":{"volumeClaimTemplates":[{"metadata":{"name":"data"}}],"template":{"spec":{"containers":[{"name":"c"}]}}}}"""),
            O("""{"kind":"PersistentVolumeClaim","metadata":{"name":"data-db-0","namespace":"d"},"spec":{"storageClassName":"fast","volumeName":"pv-1"},"status":{"phase":"Bound"}}"""),
            O("""{"kind":"PersistentVolumeClaim","metadata":{"name":"lonely","namespace":"d"},"spec":{"storageClassName":"missing"},"status":{"phase":"Bound"}}"""),
            O("""{"kind":"PersistentVolume","metadata":{"name":"pv-1"},"spec":{"storageClassName":"fast","claimRef":{"namespace":"d","name":"data-db-0"}},"status":{"phase":"Bound"}}"""),
            O("""{"kind":"PersistentVolume","metadata":{"name":"pv-old"},"spec":{"storageClassName":"fast","persistentVolumeReclaimPolicy":"Retain","claimRef":{"namespace":"d","name":"deleted"}},"status":{"phase":"Released"}}"""),
            O("""{"kind":"StorageClass","metadata":{"name":"fast"}}"""), O("""{"kind":"StorageClass","metadata":{"name":"slow"}}"""),
        ], Listed);
        Assert.Equal("StatefulSet/d/db", Row(u, "PersistentVolumeClaim", "data-db-0").Consumers.Single().Display);
        Assert.Equal(UsageStatus.Unused, Row(u, "PersistentVolumeClaim", "lonely").Status);
        Assert.Equal(UsageStatus.Used, Row(u, "PersistentVolume", "pv-1").Status);
        Assert.Equal(UsageStatus.Unused, Row(u, "PersistentVolume", "pv-old").Status);
        Assert.Contains("data is still on the volume", Row(u, "PersistentVolume", "pv-old").Note);
        Assert.Equal(UsageStatus.Used, Row(u, "StorageClass", "fast").Status);
        Assert.Equal(UsageStatus.Unused, Row(u, "StorageClass", "slow").Status);
        Assert.Equal("missing", Assert.Single(u.Broken).TargetName);
    }

    [Fact]
    public void Claim_without_class_uses_the_default_class()
    {
        var u = ResourceUsage.Analyze(
        [
            O("""{"kind":"StorageClass","metadata":{"name":"std","annotations":{"storageclass.kubernetes.io/is-default-class":"true"}}}"""),
            O("""{"kind":"PersistentVolumeClaim","metadata":{"name":"c","namespace":"d"},"spec":{}}"""),
        ], Listed);
        Assert.Equal(UsageStatus.Used, Row(u, "StorageClass", "std").Status);
    }

    private static string Pod(string name, string spec) => $$"""{"kind":"Pod","metadata":{"name":"{{name}}","namespace":"d"},"spec":{{spec}}}""";
    private const string Map = """{"kind":"ConfigMap","metadata":{"name":"cfg","namespace":"d"},"data":{"a":"1","b":"2","c":"3"}}""";

    [Fact]
    public void Dead_keys_are_the_keys_no_consumer_reads()
    {
        var u = ResourceUsage.Analyze(
        [
            O(Map),
            O(Pod("p1", """{"containers":[{"name":"c","env":[{"name":"A","valueFrom":{"configMapKeyRef":{"name":"cfg","key":"a"}}}]}]}""")),
            O(Pod("p2", """{"volumes":[{"name":"v","configMap":{"name":"cfg","items":[{"key":"b","path":"b.txt"}]}}],"containers":[{"name":"c"}]}""")),
        ], Listed);
        Assert.Equal(["c"], Row(u, "ConfigMap", "cfg").DeadKeys);
        Assert.Contains("Keys nobody reads: c", Row(u, "ConfigMap", "cfg").Note);
    }

    [Fact]
    public void A_consumer_that_takes_the_whole_map_means_no_dead_key()
    {
        foreach (var whole in new[]
        {
            """{"containers":[{"name":"c","envFrom":[{"configMapRef":{"name":"cfg"}}]}]}""",
            """{"volumes":[{"name":"v","configMap":{"name":"cfg"}}],"containers":[{"name":"c","volumeMounts":[{"name":"v","mountPath":"/etc/cfg"}]}]}""",
        })
        {
            var u = ResourceUsage.Analyze([O(Map), O(Pod("p", whole)), O(Pod("q", """{"containers":[{"name":"c","env":[{"name":"A","valueFrom":{"configMapKeyRef":{"name":"cfg","key":"a"}}}]}]}"""))], Listed);
            Assert.Empty(Row(u, "ConfigMap", "cfg").DeadKeys);
        }
    }

    [Fact]
    public void Volume_mounted_only_through_subPath_reads_those_keys()
    {
        var spec = """{"volumes":[{"name":"v","configMap":{"name":"cfg"}}],"containers":[{"name":"c","volumeMounts":[{"name":"v","mountPath":"/etc/a","subPath":"a"}]}]}""";
        var u = ResourceUsage.Analyze([O(Map), O(Pod("p", spec))], Listed);
        Assert.Equal(["b", "c"], Row(u, "ConfigMap", "cfg").DeadKeys);
    }

    [Fact]
    public void Service_accounts_used_unused_default_and_missing()
    {
        var u = ResourceUsage.Analyze(
        [
            O("""{"kind":"ServiceAccount","metadata":{"name":"app","namespace":"d"}}"""), O("""{"kind":"ServiceAccount","metadata":{"name":"idle","namespace":"d"}}"""),
            O("""{"kind":"ServiceAccount","metadata":{"name":"bound-only","namespace":"d"}}"""), O("""{"kind":"ServiceAccount","metadata":{"name":"default","namespace":"d"}}"""),
            O("""{"kind":"RoleBinding","metadata":{"name":"rb","namespace":"d"},"roleRef":{"name":"edit"},"subjects":[{"kind":"ServiceAccount","name":"bound-only"}]}"""),
            O(Pod("p1", """{"serviceAccountName":"app","containers":[{"name":"c"}]}""")),
            O(Pod("p2", """{"serviceAccountName":"ghost","containers":[{"name":"c"}]}""")),
            O(Pod("p3", """{"containers":[{"name":"c"}]}""")),
        ], Listed);
        Assert.Equal(UsageStatus.Used, Row(u, "ServiceAccount", "app").Status);
        Assert.Equal(UsageStatus.Unused, Row(u, "ServiceAccount", "idle").Status);
        Assert.Equal(UsageStatus.Used, Row(u, "ServiceAccount", "default").Status);   // p3 sets none
        Assert.Equal(UsageStatus.Unused, Row(u, "ServiceAccount", "bound-only").Status);
        Assert.Contains("holds permissions through RoleBinding/rb", Row(u, "ServiceAccount", "bound-only").Note);
        Assert.Equal("ghost", Assert.Single(u.Broken, b => b.TargetKind == "ServiceAccount").TargetName);
    }

    [Fact]
    public void Ingress_classes_and_backends()
    {
        var u = ResourceUsage.Analyze(
        [
            O("""{"kind":"IngressClass","metadata":{"name":"nginx","annotations":{"ingressclass.kubernetes.io/is-default-class":"true"}}}"""), O("""{"kind":"IngressClass","metadata":{"name":"old"}}"""),
            O("""{"kind":"Service","metadata":{"name":"web","namespace":"d"}}"""),
            O("""{"kind":"Ingress","metadata":{"name":"i","namespace":"d"},"spec":{"rules":[{"http":{"paths":[{"backend":{"service":{"name":"web"}}},{"backend":{"service":{"name":"nope"}}}]}}]}}"""),
        ], new HashSet<string>(Listed) { "Service" });
        Assert.Equal(UsageStatus.Used, Row(u, "IngressClass", "nginx").Status);   // no class set: the default one
        Assert.Equal(UsageStatus.Unused, Row(u, "IngressClass", "old").Status);
        Assert.Equal("nope", Assert.Single(u.Broken, b => b.TargetKind == "Service").TargetName);
    }

    private static string Labeled(string name, string app) => """{"kind":"Pod","metadata":{"name":"NAME","namespace":"d","labels":{"app":"APP"}},"status":{"phase":"Running"}}""".Replace("NAME", name).Replace("APP", app);

    [Fact]
    public void Hpa_with_a_missing_zero_or_shared_target()
    {
        var hpa = (string name, string target) => O("""{"kind":"HorizontalPodAutoscaler","metadata":{"name":"NAME","namespace":"d"},"spec":{"scaleTargetRef":{"kind":"Deployment","name":"TARGET"}}}""".Replace("NAME", name).Replace("TARGET", target));
        var u = ResourceUsage.Analyze(
        [
            O("""{"kind":"Deployment","metadata":{"name":"web","namespace":"d"},"spec":{"replicas":2}}"""), O("""{"kind":"Deployment","metadata":{"name":"off","namespace":"d"},"spec":{"replicas":0}}"""),
            hpa("ok", "web"), hpa("rival", "web"), hpa("ghost", "gone"), hpa("zero", "off"),
        ], new HashSet<string>(Listed) { "Deployment" });
        Assert.Contains(u.Dangling, f => f.Name == "ghost" && f.Problem.Contains("does not exist"));
        Assert.Contains(u.Dangling, f => f.Name == "zero" && f.Problem.Contains("scaled to 0"));
        Assert.Contains(u.Dangling, f => f.Name == "ok" && f.Problem.Contains("rival"));
        Assert.DoesNotContain(u.Dangling, f => f.Name == "zero" && f.Problem.Contains("does not exist"));
    }

    [Fact]
    public void Pdb_that_selects_nothing_or_forbids_every_eviction()
    {
        var pdb = (string name, string app, string rule) => O("""{"kind":"PodDisruptionBudget","metadata":{"name":"NAME","namespace":"d"},"spec":{RULE,"selector":{"matchLabels":{"app":"APP"}}}}""".Replace("NAME", name).Replace("APP", app).Replace("RULE", rule));
        var u = ResourceUsage.Analyze(
        [
            O(Labeled("w1", "web")), O(Labeled("w2", "web")),
            pdb("fine", "web", "\"minAvailable\":1"), pdb("empty", "api", "\"minAvailable\":1"),
            pdb("stuck-min", "web", "\"minAvailable\":2"), pdb("stuck-max", "web", "\"maxUnavailable\":0"),
        ], new HashSet<string>(Listed) { "Pod" });
        Assert.DoesNotContain(u.Dangling, f => f.Name == "fine");
        Assert.Contains(u.Dangling, f => f.Name == "empty" && f.Problem.Contains("Selects no pod"));
        Assert.Contains(u.Dangling, f => f.Name == "stuck-min" && f.Problem.Contains("drain stalls"));
        Assert.Contains(u.Dangling, f => f.Name == "stuck-max" && f.Problem.Contains("drain stalls"));
    }
}
