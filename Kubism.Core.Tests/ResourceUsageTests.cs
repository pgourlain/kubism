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
}
