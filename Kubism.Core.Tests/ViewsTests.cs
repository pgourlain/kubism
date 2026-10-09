using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Table;
using Kubism.Core.Views;
using Xunit;

namespace Kubism.Core.Tests;

public class ViewsTests
{
    private static PodView Pod(string json) => new(new KubeObject(JsonNode.Parse(json)!.AsObject()));

    [Theory]
    [InlineData("""{"status":{"phase":"Running","containerStatuses":[{"ready":true,"state":{"running":{}}}]},"spec":{"containers":[{}]}}""", "Running")]
    [InlineData("""{"status":{"phase":"Pending"},"spec":{"containers":[{}]}}""", "Pending")]
    [InlineData("""{"status":{"phase":"Running","containerStatuses":[{"ready":false,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]},"spec":{"containers":[{}]}}""", "CrashLoopBackOff")]
    [InlineData("""{"status":{"phase":"Pending","containerStatuses":[{"ready":false,"state":{"waiting":{"reason":"ImagePullBackOff"}}}]},"spec":{"containers":[{}]}}""", "ImagePullBackOff")]
    [InlineData("""{"status":{"phase":"Succeeded","containerStatuses":[{"ready":false,"state":{"terminated":{"reason":"Completed","exitCode":0}}}]},"spec":{"containers":[{}]}}""", "Completed")]
    [InlineData("""{"status":{"phase":"Failed","containerStatuses":[{"state":{"terminated":{"reason":"OOMKilled","exitCode":137}}}]},"spec":{"containers":[{}]}}""", "OOMKilled")]
    [InlineData("""{"status":{"phase":"Failed","containerStatuses":[{"state":{"terminated":{"exitCode":2}}}]},"spec":{"containers":[{}]}}""", "ExitCode:2")]
    [InlineData("""{"status":{"phase":"Failed","containerStatuses":[{"state":{"terminated":{"signal":9,"exitCode":137}}}]},"spec":{"containers":[{}]}}""", "Signal:9")]
    [InlineData("""{"status":{"phase":"Failed","reason":"Evicted"},"spec":{"containers":[{}]}}""", "Evicted")]
    [InlineData("""{"status":{"phase":"Pending","initContainerStatuses":[{"state":{"running":{}}}]},"spec":{"initContainers":[{}],"containers":[{}]}}""", "Init:0/1")]
    [InlineData("""{"status":{"phase":"Pending","initContainerStatuses":[{"state":{"terminated":{"exitCode":0,"reason":"Completed"}}},{"state":{"waiting":{"reason":"PodInitializing"}}}]},"spec":{"initContainers":[{},{}],"containers":[{}]}}""", "Init:1/2")]
    [InlineData("""{"status":{"phase":"Pending","initContainerStatuses":[{"state":{"terminated":{"exitCode":1,"reason":"Error"}}}]},"spec":{"initContainers":[{}],"containers":[{}]}}""", "Init:Error")]
    [InlineData("""{"status":{"phase":"Pending","initContainerStatuses":[{"state":{"waiting":{"reason":"ImagePullBackOff"}}}]},"spec":{"initContainers":[{}],"containers":[{}]}}""", "Init:ImagePullBackOff")]
    [InlineData("""{"metadata":{"deletionTimestamp":"2026-01-01T00:00:00Z"},"status":{"phase":"Running","containerStatuses":[{"ready":true,"state":{"running":{}}}]},"spec":{"containers":[{}]}}""", "Terminating")]
    [InlineData("""{"metadata":{"deletionTimestamp":"2026-01-01T00:00:00Z"},"status":{"phase":"Running","reason":"NodeLost"},"spec":{"containers":[{}]}}""", "Unknown")]
    public void Pod_status_matches_kubectl(string json, string expected) => Assert.Equal(expected, Pod(json).Status);

    [Fact]
    public void Pod_ready_and_restarts()
    {
        var p = Pod("""{"spec":{"containers":[{},{}]},"status":{"phase":"Running","containerStatuses":[{"ready":true,"restartCount":3,"state":{"running":{}}},{"ready":false,"restartCount":4,"state":{"running":{}}}]}}""");
        Assert.Equal("1/2", p.Ready);
        Assert.Equal(7, p.Restarts);
    }

    [Fact]
    public void Pod_containers_include_resources_and_ports()
    {
        var p = Pod("""{"spec":{"initContainers":[{"name":"i","image":"busybox"}],"containers":[{"name":"c","image":"nginx","ports":[{"containerPort":80}],"resources":{"requests":{"cpu":"100m"},"limits":{"memory":"1Gi"}}}]},"status":{"containerStatuses":[{"name":"c","ready":true,"restartCount":1,"state":{"running":{}}}]}}""");
        Assert.Equal(2, p.Containers.Count);
        Assert.True(p.Containers[0].IsInit);
        var c = p.Containers[1];
        Assert.Equal("100m", c.CpuRequest); Assert.Equal("1Gi", c.MemoryLimit); Assert.Equal([80], c.Ports); Assert.Equal("Running", c.State);
    }

    [Theory]
    [InlineData("250m", 0.25)]
    [InlineData("2", 2)]
    [InlineData("1500m", 1.5)]
    [InlineData("100n", 1e-7)]
    [InlineData("128Mi", 128d * 1024 * 1024)]
    [InlineData("1Gi", 1073741824d)]
    [InlineData("1G", 1e9)]
    [InlineData("12e3", 12000)]
    [InlineData("", 0)]
    public void Quantities_parse(string q, double expected) => Assert.Equal(expected, Quantities.Parse(q), 9);

    [Fact]
    public void Quantities_format()
    {
        Assert.Equal("250m", Quantities.FormatCpu(0.25));
        Assert.Equal("2", Quantities.FormatCpu(2));
        Assert.Equal("512Mi", Quantities.FormatMemoryMi(512d * 1024 * 1024));
        Assert.Equal("2Gi", Quantities.FormatMemoryMi(2d * 1024 * 1024 * 1024));
        Assert.Equal("1500Mi", Quantities.FormatMemoryMi(1500d * 1024 * 1024));
    }

    [Fact]
    public void Node_status_and_roles()
    {
        var n = new NodeView(new KubeObject(JsonNode.Parse("""{"metadata":{"labels":{"node-role.kubernetes.io/control-plane":""}},"spec":{"unschedulable":true},"status":{"conditions":[{"type":"Ready","status":"True"}],"capacity":{"cpu":"4","memory":"16Gi"}}}""")!.AsObject()));
        Assert.Equal("Ready,SchedulingDisabled", n.Status);
        Assert.Equal("control-plane", n.Roles);
        Assert.Equal(4, n.CpuCapacity);
    }

    [Fact]
    public void Table_columns_fallback_when_server_gives_none()
    {
        var cols = TableService.Columns(null, true);
        Assert.Equal(["Name", "Namespace", "Age"], cols.Select(c => c.Name));
        var item = new CacheItem(new KubeObject(JsonNode.Parse("""{"metadata":{"name":"x","namespace":"ns","creationTimestamp":"2026-01-01T00:00:00Z"}}""")!.AsObject()), null);
        Assert.Equal("x", TableService.Cell(item, cols[0]));
        Assert.Equal("ns", TableService.Cell(item, cols[1]));
    }

    [Fact]
    public void Table_server_columns_get_namespace_inserted_and_age_is_computed_locally()
    {
        var server = new List<TableColumn> { new("Name", "string", "name", "", 0), new("Ready", "string", "", "", 0), new("Age", "string", "", "", 0) };
        var cols = TableService.Columns(server, true);
        Assert.Equal(["Name", "Namespace", "Ready", "Age"], cols.Select(c => c.Name));
        var item = new CacheItem(new KubeObject(JsonNode.Parse("""{"metadata":{"name":"x","namespace":"ns","creationTimestamp":"2026-01-01T00:00:00Z"}}""")!.AsObject()), ["x", "1/1", "stale"]);
        Assert.Equal("1/1", TableService.Cell(item, cols[2]));
        var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
        Assert.Equal("4d", TableService.Cell(item, cols[3], default, now));
    }

    [Fact]
    public void Sort_keys_are_numeric_and_natural()
    {
        var col = new ColumnDef("Restarts", "integer", 0, false, 0);
        CacheItem I(string n) => new(new KubeObject(new JsonObject { ["metadata"] = new JsonObject { ["name"] = n } }), [n]);
        var sorted = new[] { "10", "9", "100", "2" }.Select(I).OrderBy(i => TableService.SortKey(i, col)).Select(i => i.Cells![0]).ToList();
        Assert.Equal(["2", "9", "10", "100"], sorted);
        var nameCol = new ColumnDef("Name", "string", 0, false, -1);
        var names = new[] { "pod-10", "pod-2", "pod-1" }.Select(I).OrderBy(i => TableService.SortKey(i, nameCol)).Select(i => i.Object.Name).ToList();
        Assert.Equal(["pod-1", "pod-2", "pod-10"], names);
    }

    [Fact]
    public void Filter_matches_name_and_labels()
    {
        var item = new CacheItem(new KubeObject(JsonNode.Parse("""{"metadata":{"name":"web-1","labels":{"app":"web","tier":"fe"}}}""")!.AsObject()), null);
        Assert.True(TableService.Matches(item, "web"));
        Assert.True(TableService.Matches(item, "app=web"));
        Assert.True(TableService.Matches(item, "app=web WEB"));
        Assert.False(TableService.Matches(item, "app=api"));
        Assert.False(TableService.Matches(item, "zzz"));
    }
}
