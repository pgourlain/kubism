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
        SeedHelm(server);
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

    /// <summary>Helm release secrets: healthy releases with history, one failed upgrade (crashing pod) and one stuck in pending-upgrade.</summary>
    private static void SeedHelm(FakeApiServer server)
    {
        var now = DateTimeOffset.UtcNow;
        string Manifest(string kind, string name) => $"---\napiVersion: apps/v1\nkind: {kind}\nmetadata:\n  name: {name}\n";
        void Rev(string name, string ns, int rev, string status, string desc, DateTimeOffset at, string chart, string chartVersion, string app, JsonObject chartValues, JsonObject user, string manifest, string? notes = null)
        {
            var release = new JsonObject
            {
                ["name"] = name, ["namespace"] = ns, ["version"] = rev,
                ["info"] = new JsonObject { ["status"] = status, ["description"] = desc, ["first_deployed"] = at.AddDays(-30).ToString("O"), ["last_deployed"] = at.ToString("O"), ["notes"] = notes ?? $"{name} installed from chart {chart}." },
                ["chart"] = new JsonObject { ["metadata"] = new JsonObject { ["name"] = chart, ["version"] = chartVersion, ["appVersion"] = app }, ["values"] = chartValues.DeepClone() },
                ["config"] = user.DeepClone(), ["manifest"] = manifest,
            };
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(System.Text.Encoding.UTF8.GetBytes(release.ToJsonString()));
            var payload = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(ms.ToArray())));
            server.Upsert(new JsonObject
            {
                ["apiVersion"] = "v1", ["kind"] = "Secret",
                ["metadata"] = new JsonObject { ["name"] = $"sh.helm.release.v1.{name}.v{rev}", ["namespace"] = ns, ["labels"] = new JsonObject { ["owner"] = "helm", ["name"] = name, ["status"] = status, ["version"] = rev.ToString() } },
                ["type"] = "helm.sh/release.v1", ["data"] = new JsonObject { ["release"] = payload },
            });
        }
        JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

        // ingress-nginx: three revisions, last one deployed.
        var nginxDefaults = J("{\"controller\":{\"replicaCount\":1,\"image\":{\"tag\":\"v1.10.0\"},\"service\":{\"type\":\"LoadBalancer\"}},\"defaultBackend\":{\"enabled\":false}}");
        Rev("ingress-nginx", "default", 1, "superseded", "Install complete", now.AddDays(-20), "ingress-nginx", "4.10.0", "1.10.0", nginxDefaults, J("{}"), Manifest("Deployment", "ingress-nginx-controller"));
        Rev("ingress-nginx", "default", 2, "superseded", "Upgrade complete", now.AddDays(-9), "ingress-nginx", "4.10.1", "1.10.1", nginxDefaults, J("{\"controller\":{\"replicaCount\":2}}"), Manifest("Deployment", "ingress-nginx-controller"));
        Rev("ingress-nginx", "default", 3, "deployed", "Upgrade complete", now.AddDays(-2), "ingress-nginx", "4.11.0", "1.11.0", nginxDefaults, J("{\"controller\":{\"replicaCount\":3,\"service\":{\"annotations\":{\"service.beta.kubernetes.io/azure-load-balancer-internal\":\"true\"}}}}"), Manifest("Deployment", "ingress-nginx-controller"));
        // cert-manager: one healthy revision in its own namespace.
        Rev("cert-manager", "cert-manager", 1, "deployed", "Install complete", now.AddDays(-40), "cert-manager", "v1.15.1", "v1.15.1",
            J("{\"installCRDs\":false,\"replicaCount\":1,\"prometheus\":{\"enabled\":true}}"), J("{\"installCRDs\":true}"), Manifest("Deployment", "cert-manager"));
        // Failed upgrade: its pod (crashy-0, labelled below) crash-loops, so the Diagnostics tab has events and logs to show.
        Rev("payments", "default", 1, "superseded", "Install complete", now.AddDays(-6), "payments", "0.4.0", "2.3.0", J("{\"replicaCount\":2,\"image\":{\"tag\":\"2.3.0\"}}"), J("{}"), Manifest("Deployment", "payments"));
        Rev("payments", "default", 2, "failed", "Upgrade \"payments\" failed: context deadline exceeded", now.AddMinutes(-25), "payments", "0.5.0", "2.4.0", J("{\"replicaCount\":2,\"image\":{\"tag\":\"2.4.0\"}}"), J("{\"image\":{\"tag\":\"2.4.0\"},\"resources\":{\"limits\":{\"memory\":\"64Mi\"}}}"), Manifest("Deployment", "payments"));
        var crashy = server.Get(Gvr.Pods, "default", "crashy-0");
        if (crashy is not null) { crashy["metadata"]!["labels"]!["app.kubernetes.io/instance"] = "payments"; server.Upsert(crashy); }
        var ev = server.Get(Gvr.Events, "default", "crashy-0.1");
        if (ev is not null) { ev["lastTimestamp"] = now.AddMinutes(-4).ToString("O"); server.Upsert(ev); }
        // Stuck upgrade (--wait timeout): pending for 20 minutes.
        Rev("reporting", "staging", 1, "pending-upgrade", "Preparing upgrade", now.AddMinutes(-20), "reporting", "1.2.0", "1.2.0", J("{\"replicaCount\":1}"), J("{\"replicaCount\":2}"), Manifest("Deployment", "stuck-pending"));
        var stuck = server.Get(Gvr.Pods, "staging", "stuck-pending");
        if (stuck is not null) { stuck["metadata"]!["labels"]!["app.kubernetes.io/instance"] = "reporting"; server.Upsert(stuck); }
    }

    public async ValueTask DisposeAsync()
    {
        await Prometheus.DisposeAsync();
        await Server.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(KubeconfigPath)!, true); } catch { }
    }
}
