using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Images;
using Kubism.Core.Views;
using Xunit;

namespace Kubism.Core.Tests;

public class ImageInventoryTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static KubeObject Pod(string name, string ns, string image, string? owner = null, string ownerKind = "ReplicaSet", string? waiting = null, string? imageId = null, string policy = "IfNotPresent")
    {
        var refs = owner is null ? "" : ",\"ownerReferences\":[{\"apiVersion\":\"apps/v1\",\"kind\":\"" + ownerKind + "\",\"name\":\"" + owner + "\",\"uid\":\"u\",\"controller\":true}]";
        var state = waiting is null ? "{}" : "{\"waiting\":{\"reason\":\"" + waiting + "\"}}";
        var id = imageId is null ? "" : ",\"imageID\":\"" + imageId + "\"";
        return O("{\"kind\":\"Pod\",\"metadata\":{\"name\":\"" + name + "\",\"namespace\":\"" + ns + "\"" + refs + "},\"spec\":{\"containers\":[{\"name\":\"app\",\"image\":\"" + image + "\",\"imagePullPolicy\":\"" + policy + "\"}]},\"status\":{\"containerStatuses\":[{\"name\":\"app\",\"state\":" + state + id + "}]}}");
    }

    [Theory]
    [InlineData("nginx", "docker.io", "library/nginx", null, null)]
    [InlineData("nginx:1.25", "docker.io", "library/nginx", "1.25", null)]
    [InlineData("bitnami/redis:7", "docker.io", "bitnami/redis", "7", null)]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:10.0", "mcr.microsoft.com", "dotnet/aspnet", "10.0", null)]
    [InlineData("localhost:5000/app", "localhost:5000", "app", null, null)]
    [InlineData("reg.io:5000/team/app:v1@sha256:abc", "reg.io:5000", "team/app", "v1", "sha256:abc")]
    [InlineData("quay.io/x/y@sha256:abc", "quay.io", "x/y", null, "sha256:abc")]
    public void Parses_references(string raw, string registry, string repo, string? tag, string? digest)
    {
        var r = ImageRef.Parse(raw);
        Assert.Equal((registry, repo, tag, digest), (r.Registry, r.Repository, r.Tag, r.Digest));
    }

    [Fact]
    public void Pods_of_one_deployment_are_one_user_and_the_owner_is_resolved()
    {
        var rows = ImageInventory.Analyze(
        [
            Pod("web-6d4cf56db6-a", "d", "nginx:1.25", "web-6d4cf56db6"),
            Pod("web-6d4cf56db6-b", "d", "nginx:1.25", "web-6d4cf56db6"),
        ]);
        var row = Assert.Single(rows);
        var user = Assert.Single(row.Users);
        Assert.Equal(("Deployment", "web", 2), (user.Kind, user.Name, user.Pods));
        Assert.Equal(2, row.Pods);
        Assert.Equal(1, row.Workloads);
    }

    [Fact]
    public void Replica_sets_and_jobs_give_the_exact_owner()
    {
        var rs = O("""{"kind":"ReplicaSet","metadata":{"name":"odd","namespace":"d","ownerReferences":[{"apiVersion":"apps/v1","kind":"Deployment","name":"real","uid":"x","controller":true}]}}""");
        var rows = ImageInventory.Analyze([Pod("p", "d", "a/b:1", "odd")], [rs]);
        Assert.Equal("real", rows.Single().Users.Single().Name);
    }

    [Fact]
    public void Flags_latest_unpinned_mixed_versions_and_pull_failures()
    {
        var rows = ImageInventory.Analyze(
        [
            Pod("a", "d", "nginx"), Pod("b", "d", "nginx:latest"), Pod("c", "d", "nginx:1.25"),
            Pod("d", "d", "r.io/x/app@sha256:abc"), Pod("e", "d", "r.io/x/broken:1", waiting: "ImagePullBackOff"),
        ]);
        ImageRow R(string raw) => rows.Single(r => r.Reference == raw);
        Assert.True(R("nginx").Issues.HasFlag(ImageIssues.Latest));
        Assert.True(R("nginx:latest").Issues.HasFlag(ImageIssues.Latest));
        Assert.False(R("nginx:1.25").Issues.HasFlag(ImageIssues.Latest));
        Assert.True(R("nginx:1.25").Issues.HasFlag(ImageIssues.MixedVersions));
        Assert.Equal(ImageIssues.None, R("r.io/x/app@sha256:abc").Issues);
        Assert.True(R("r.io/x/broken:1").Issues.HasFlag(ImageIssues.PullFailing));
        Assert.Equal(1, R("r.io/x/broken:1").PullFailures);
        Assert.False(R("r.io/x/broken:1").Issues.HasFlag(ImageIssues.MixedVersions));
    }

    [Fact]
    public void Resolved_digest_comes_from_the_container_status()
    {
        var row = ImageInventory.Analyze([Pod("a", "d", "nginx:1.25", imageId: "docker-pullable://nginx@sha256:feed")]).Single();
        Assert.Equal(["sha256:feed"], row.ResolvedDigests);
    }

    [Fact]
    public void Init_and_ephemeral_containers_count_and_secrets_are_not_read()
    {
        var pod = O("""{"kind":"Pod","metadata":{"name":"p","namespace":"d"},"spec":{"initContainers":[{"name":"i","image":"busybox:1"}],"containers":[{"name":"c","image":"app:1","env":[{"name":"S","value":"hunter2"}]}]}}""");
        var rows = ImageInventory.Analyze([pod]);
        Assert.Equal(["init", "container"], rows.OrderBy(r => r.Reference == "busybox:1" ? 0 : 1).Select(r => r.Users.Single().Role));
        Assert.DoesNotContain("hunter2", string.Concat(rows.Select(r => r.ToString())));
    }
}

public class ImageReportsTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static KubeObject Pod(string name, string ns, string deploy, string image)
        => O("{\"kind\":\"Pod\",\"metadata\":{\"name\":\"" + name + "\",\"namespace\":\"" + ns + "\",\"ownerReferences\":[{\"apiVersion\":\"apps/v1\",\"kind\":\"ReplicaSet\",\"name\":\"" + deploy + "-5d8f7c\",\"uid\":\"r\",\"controller\":true}]},\"spec\":{\"containers\":[{\"name\":\"main\",\"image\":\"" + image + "\"}]}}");

    [Fact]
    public void Compare_tells_behind_from_missing_and_identical()
    {
        var staging = ImageInventory.Analyze([Pod("a", "d", "web", "r.io/web:1.2"), Pod("b", "d", "api", "r.io/api:3"), Pod("c", "d", "only-staging", "r.io/x:1"), Pod("d", "d", "swap", "r.io/old:1")]);
        var prod = ImageInventory.Analyze([Pod("a", "d", "web", "r.io/web:1.1"), Pod("b", "d", "api", "r.io/api:3"), Pod("c", "d", "only-prod", "r.io/y:1"), Pod("d", "d", "swap", "r.io/new:1")]);
        var diff = ImageComparison.Compare(staging, prod);
        ImageDiffEntry E(string name) => diff.Single(d => d.Name == name);
        Assert.Equal(CompareStatus.Different, E("web").Status);
        Assert.True(E("web").VersionOnly);
        Assert.Equal(["r.io/web:1.2"], E("web").Left);
        Assert.Equal(["r.io/web:1.1"], E("web").Right);
        Assert.Equal(CompareStatus.Same, E("api").Status);
        Assert.Equal(CompareStatus.OnlyLeft, E("only-staging").Status);
        Assert.Equal(CompareStatus.OnlyRight, E("only-prod").Status);
        Assert.False(E("swap").VersionOnly);
    }

    [Fact]
    public void Compare_ignores_namespaces_when_asked_to()
    {
        var left = ImageInventory.Analyze([Pod("a", "team-a", "web", "r.io/web:1")]);
        var right = ImageInventory.Analyze([Pod("a", "team-b", "web", "r.io/web:1")]);
        Assert.Equal([CompareStatus.OnlyLeft, CompareStatus.OnlyRight], ImageComparison.Compare(left, right, matchNamespace: true).Select(d => d.Status).Order());
        Assert.Equal(CompareStatus.Same, Assert.Single(ImageComparison.Compare(left, right, matchNamespace: false)).Status);
    }

    [Fact]
    public void Exports_quote_and_stay_free_of_secrets()
    {
        var rows = ImageInventory.Analyze([Pod("a", "d", "web", "r.io/web:1,2"), Pod("b", "d", "web", "r.io/web:latest")]);
        var csv = ImageExport.Csv(rows, "prod, eu");
        Assert.StartsWith("cluster,image,", csv);
        Assert.Contains("\"prod, eu\",\"r.io/web:1,2\"", csv);
        Assert.Equal(rows.Count + 1, csv.TrimEnd().Split('\n').Length);
        var md = ImageExport.Markdown(rows, "prod");
        Assert.Contains("# Images — prod", md);
        Assert.Contains("`r.io/web:latest`", md);
        Assert.Contains("latest or no tag", md);
    }
}
