using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Testing;

namespace Kubism.Desktop;

/// <summary>--demo: an in-process fake API server and Prometheus with sample objects; no cluster needed.</summary>
public sealed class DemoEnvironment : IAsyncDisposable
{
    private DemoEnvironment(FakeApiServer server, FakePrometheus prom, string kubeconfigPath)
    {
        Server = server; Prometheus = prom; KubeconfigPath = kubeconfigPath;
    }

    public FakeApiServer Server { get; }
    public FakePrometheus Prometheus { get; }
    public string KubeconfigPath { get; }
    public const double Mi = 1024 * 1024;

    public static async Task<DemoEnvironment> StartAsync()
    {
        var server = await FakeApiServer.StartAsync();
        var prom = await FakePrometheus.StartAsync();
        var rnd = new Random(7);
        foreach (var pod in server.List(Gvr.Pods, null))
        {
            var meta = pod["metadata"]!;
            var ns = meta["namespace"]!.GetValue<string>(); var name = meta["name"]!.GetValue<string>();
            var res = pod["spec"]?["containers"]?[0]?["resources"]?["requests"];
            var cpuReq = Core.Views.Quantities.ParseCpu(res?["cpu"]?.ToString() ?? "100m"); var memReq = Core.Views.Quantities.ParseBytes(res?["memory"]?.ToString() ?? "128Mi");
            // Mostly over-provisioned workloads, one under-provisioned (checkout), crashy OOM.
            var cpuFactor = ns == "production" ? 1.15 : name.StartsWith("web") ? 0.12 : 0.3 + rnd.NextDouble() * 0.2;
            var memFactor = ns == "production" ? 0.95 : name.StartsWith("web") ? 0.2 : 0.5;
            var oom = name.StartsWith("crashy") ? 2 : 0;
            var cpu = Math.Max(0.002, cpuReq * cpuFactor); var mem = Math.Max(8 * Mi, memReq * memFactor);
            prom.Profiles.Add(new Testing.PromProfile(ns, name, "main", t => cpu * (1 + 0.25 * Math.Sin(t / 6.0)), t => mem * (1 + 0.05 * Math.Sin(t / 20.0)), ns == "production" ? 0.18 : 0, oom));
            if (!server.PodMetrics.TryGetValue(ns, out var d)) server.PodMetrics[ns] = d = [];
            d[name] = (Core.Views.Quantities.FormatCpu(cpu), Core.Views.Quantities.FormatMemoryMi(mem));
        }
        server.NodeMetrics["node-1"] = ("1200m", "6Gi"); server.NodeMetrics["node-2"] = ("900m", "5Gi");
        var dir = Path.Combine(Path.GetTempPath(), "kubism-demo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "demo-kubeconfig");
        File.WriteAllText(path, $$"""
            apiVersion: v1
            kind: Config
            current-context: demo-staging
            clusters:
            - name: demo
              cluster:
                server: {{server.Url}}
            users:
            - name: demo-user
              user:
                token: {{server.Token}}
            contexts:
            - name: demo-staging
              context: {cluster: demo, user: demo-user, namespace: default}
            - name: demo-production
              context: {cluster: demo, user: demo-user, namespace: production}
            """.Replace("\r\n", "\n"));
        return new DemoEnvironment(server, prom, path);
    }

    public async ValueTask DisposeAsync()
    {
        await Prometheus.DisposeAsync();
        await Server.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(KubeconfigPath)!, true); } catch { }
    }
}
