using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Helm;
using Kubism.Core.Views;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class HelmSecretsTests
{
    internal static string EncodeRelease(JsonObject release)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(Encoding.UTF8.GetBytes(release.ToJsonString()));
        var helmB64 = Convert.ToBase64String(ms.ToArray());
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(helmB64));
    }

    internal static JsonObject Release(string name, int rev, string status = "deployed") => new()
    {
        ["name"] = name, ["namespace"] = "web", ["version"] = rev,
        ["info"] = new JsonObject { ["status"] = status, ["last_deployed"] = "2026-03-01T10:00:00Z", ["description"] = "Upgrade complete", ["notes"] = "Thanks" },
        ["chart"] = new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "ingress-nginx", ["version"] = "4.11.0", ["appVersion"] = "1.11.0" },
            ["values"] = new JsonObject { ["replicas"] = 1, ["image"] = new JsonObject { ["tag"] = "v1", ["pull"] = "IfNotPresent" } },
        },
        ["config"] = new JsonObject { ["replicas"] = 3, ["image"] = new JsonObject { ["tag"] = "v2" } },
        ["manifest"] = "---\nkind: Deployment\n",
    };

    private static JsonObject HelmSecret(string name, int rev, string status = "deployed") => new()
    {
        ["apiVersion"] = "v1", ["kind"] = "Secret",
        ["metadata"] = new JsonObject { ["name"] = $"sh.helm.release.v1.{name}.v{rev}", ["namespace"] = "web", ["uid"] = $"h-{name}-{rev}", ["labels"] = new JsonObject { ["owner"] = "helm", ["name"] = name } },
        ["type"] = "helm.sh/release.v1",
        ["data"] = new JsonObject { ["release"] = EncodeRelease(Release(name, rev, status)) },
    };

    [Fact]
    public void Decodes_release_secret_base64_gzip_json()
    {
        var r = HelmReleases.Decode(new KubeObject(HelmSecret("nginx", 2)))!;
        Assert.Equal(("nginx", "web", 2, "deployed"), (r.Name, r.Namespace, r.Revision, r.Status));
        Assert.Equal(("ingress-nginx", "4.11.0", "1.11.0"), (r.Chart, r.ChartVersion, r.AppVersion));
        Assert.Contains("kind: Deployment", r.Manifest);
        Assert.Equal("Thanks", r.Notes);
        Assert.NotNull(r.Updated);
    }

    [Fact]
    public void Computed_values_merge_user_values_over_chart_defaults()
    {
        var c = HelmReleases.Decode(new KubeObject(HelmSecret("nginx", 1)))!.ComputedValues;
        Assert.Equal(3, c["replicas"]!.GetValue<int>());
        Assert.Equal("v2", c["image"]!["tag"]!.GetValue<string>());
        Assert.Equal("IfNotPresent", c["image"]!["pull"]!.GetValue<string>());
    }

    [Fact]
    public async Task Lists_latest_revision_and_history()
    {
        var t = new FakeKubeTransport();
        t.Set(Gvr.Secrets, HelmSecret("nginx", 1, "superseded"), HelmSecret("nginx", 2), HelmSecret("cert-manager", 5));
        var h = new HelmReleases(t);
        var latest = await h.ListAsync(null, default);
        Assert.Equal(["cert-manager", "nginx"], latest.Select(r => r.Name));
        Assert.Equal(2, latest.Single(r => r.Name == "nginx").Revision);
        var hist = await h.HistoryAsync("web", "nginx", default);
        Assert.Equal([1, 2], hist.Select(r => r.Revision));
        Assert.Equal("superseded", hist[0].Status);
    }

    [Fact]
    public async Task Corrupt_revision_is_skipped()
    {
        var t = new FakeKubeTransport();
        var bad = HelmSecret("broken", 1); bad["data"]!["release"] = "bm90IGd6aXA=";
        t.Set(Gvr.Secrets, bad, HelmSecret("ok", 1));
        Assert.Equal(["ok"], (await new HelmReleases(t).ListAsync(null, default)).Select(r => r.Name));
    }

    [Fact]
    public void Secret_values_are_masked_everywhere()
    {
        var secret = new JsonObject
        {
            ["apiVersion"] = "v1", ["kind"] = "Secret",
            ["metadata"] = new JsonObject { ["name"] = "s", ["annotations"] = new JsonObject { ["kubectl.kubernetes.io/last-applied-configuration"] = "{\"data\":{\"password\":\"c3VwZXJzZWNyZXQ=\"}}" } },
            ["data"] = new JsonObject { ["password"] = "c3VwZXJzZWNyZXQ=", ["user"] = "YWRtaW4=" },
            ["stringData"] = new JsonObject { ["token"] = "plain" },
        };
        var masked = SecretMasker.MaskedCopy(secret);
        var text = masked.ToJsonString();
        Assert.DoesNotContain("c3VwZXJzZWNyZXQ=", text);
        Assert.DoesNotContain("YWRtaW4=", text);
        Assert.DoesNotContain("plain", text);
        Assert.Equal(SecretMasker.Mask, masked["data"]!["password"]!.GetValue<string>());
        Assert.Equal("c3VwZXJzZWNyZXQ=", secret["data"]!["password"]!.GetValue<string>()); // original untouched
        Assert.Equal("supersecret", SecretMasker.Reveal(secret, "password"));
        var one = SecretMasker.MaskedCopy(secret, "user");
        Assert.Equal("YWRtaW4=", one["data"]!["user"]!.GetValue<string>());
        Assert.Equal(SecretMasker.Mask, one["data"]!["password"]!.GetValue<string>());
        Assert.Equal(["password", "user", "token"], SecretMasker.Keys(secret));
    }

    [Fact]
    public void Configmaps_are_not_masked_and_managed_fields_are_removed()
    {
        var cm = new JsonObject { ["kind"] = "ConfigMap", ["apiVersion"] = "v1", ["metadata"] = new JsonObject { ["name"] = "c", ["managedFields"] = new JsonArray() }, ["data"] = new JsonObject { ["k"] = "v" } };
        Assert.Equal("v", SecretMasker.MaskedCopy(cm)["data"]!["k"]!.GetValue<string>());
        Assert.False(((JsonObject)SecretMasker.WithoutManagedFields(cm)["metadata"]!).ContainsKey("managedFields"));
    }

    [Fact]
    public void Related_finds_owner_chain_owned_objects_and_selecting_services()
    {
        KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());
        var dep = O("""{"kind":"Deployment","metadata":{"name":"web","namespace":"d","uid":"D"}}""");
        var rs = O("""{"kind":"ReplicaSet","metadata":{"name":"web-1","namespace":"d","uid":"R","ownerReferences":[{"kind":"Deployment","name":"web","uid":"D","controller":true}]}}""");
        var pod = O("""{"kind":"Pod","metadata":{"name":"web-1-a","namespace":"d","uid":"P","labels":{"app":"web"},"ownerReferences":[{"kind":"ReplicaSet","name":"web-1","uid":"R","controller":true}]}}""");
        var svc = O("""{"kind":"Service","metadata":{"name":"web","namespace":"d","uid":"S"},"spec":{"selector":{"app":"web"}}}""");
        var other = O("""{"kind":"Service","metadata":{"name":"db","namespace":"d","uid":"S2"},"spec":{"selector":{"app":"db"}}}""");
        var all = new[] { dep, rs, pod, svc, other };
        var rel = Related.For(pod, (k, ns, n) => all.FirstOrDefault(a => a.Kind == k && a.Name == n), all);
        Assert.Equal(["ReplicaSet", "Deployment"], rel.Where(r => r.Relation == "Owner").Select(r => r.Kind));
        Assert.Equal(["web"], rel.Where(r => r.Relation == "Service").Select(r => r.Name));
        var down = Related.For(dep, (k, ns, n) => null, all);
        Assert.Equal(["web-1"], down.Where(r => r.Relation == "Owned").Select(r => r.Name));
    }
}
