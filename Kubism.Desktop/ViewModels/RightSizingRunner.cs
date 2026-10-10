using Kubism.Core.Api;
using Kubism.Core.RightSizing;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

/// <summary>Builds a right-sizing report for a cluster: lists the workloads, reads usage history from a source, and runs the engine with the cluster's settings and prices.</summary>
public static class RightSizingRunner
{
    /// <summary>Where a cluster's usage history can come from, most specific first: its Prometheus URL, in-cluster Prometheus, the VPA recommender, local sampling.</summary>
    public static IReadOnlyList<SourceChoice> Sources(WorkspaceViewModel ws)
    {
        var list = new List<SourceChoice>();
        var meta = ws.Session.Metadata;
        var tokens = ws.Main.AzureTokens;
        if (!string.IsNullOrWhiteSpace(meta.PrometheusUrl) && Uri.TryCreate(meta.PrometheusUrl, UriKind.Absolute, out var uri))
        {
            var azure = uri.Host.EndsWith("prometheus.monitor.azure.com", StringComparison.OrdinalIgnoreCase);
            list.Add(new SourceChoice(azure ? "Azure Monitor managed Prometheus" : "Prometheus",
                () => new PrometheusUsageSource(new HttpPromClient(uri, azure ? tokens : null, azure ? "https://prometheus.monitor.azure.com/.default" : null), azure ? "Azure Monitor managed Prometheus" : "Prometheus")));
        }
        list.Add(new SourceChoice("Prometheus (in-cluster, API server proxy)", () =>
            PrometheusDiscovery.FindAsync(ws.Session.Transport, ws.Session.SessionToken).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("No in-cluster Prometheus found. Set a Prometheus URL in the cluster settings, or use local sampling.")));
        list.Add(new SourceChoice("VPA recommender", () => new VpaRecommendationSource(ws.Session.Transport)));
        list.Add(new SourceChoice("Local sampling", () => new LocalSamplerSource(ws.Session.Entry.Id, ws.Main.UsageStore)));
        return list;
    }

    public static RightSizingEngine Engine(WorkspaceViewModel ws)
    {
        var s = ws.Main.Settings.Current;
        var meta = ws.Session.Metadata;
        return new RightSizingEngine(new KrrStrategy(), new RightSizingSettings
        {
            CpuPercentile = s.CpuPercentile, MemoryMargin = s.MemoryMargin, MemoryLimitFactor = s.MemoryLimitFactor, KeepCpuLimit = s.KeepCpuLimit,
            PricePerVcpuHour = meta.PricePerVcpuHour, PricePerGibHour = meta.PricePerGibHour,
        });
    }

    public static async Task<(RightSizingReport report, UsageHistory history)> RunAsync(WorkspaceViewModel ws, IUsageHistorySource source, int days, IReadOnlyList<string>? namespaces, CancellationToken ct)
    {
        var transport = ws.Session.Transport;
        var kinds = new[] { Gvr.Pods, Gvr.ReplicaSets, Gvr.Deployments, Gvr.StatefulSets, Gvr.DaemonSets, Gvr.Jobs, Gvr.CronJobs };
        var objects = new Dictionary<Gvr, List<KubeObject>>();
        foreach (var g in kinds)
        {
            var items = new List<KubeObject>();
            string? cont = null;
            do
            {
                var l = await transport.ListAsync(g, null, new ListOptions { Limit = 500, Continue = cont }, ct);
                items.AddRange(l.Items.Select(i => new KubeObject(i)));
                cont = l.Continue;
            } while (!string.IsNullOrEmpty(cont));
            objects[g] = items;
        }
        var nodes = await transport.ListAsync(Gvr.Nodes, null, new ListOptions(), ct);
        var nodeViews = nodes.Items.Select(n => new NodeView(new KubeObject(n))).ToList();
        var history = await source.QueryAsync(UsageQuery.Default(days, namespaces), ct);
        var pods = objects[Gvr.Pods].Where(p => namespaces is null || namespaces.Contains(p.Namespace ?? "")).ToList();
        KubeObject? Lookup(string kind, string n, string name)
        {
            var gvr = kind switch { "ReplicaSet" => Gvr.ReplicaSets, "Deployment" => Gvr.Deployments, "StatefulSet" => Gvr.StatefulSets, "DaemonSet" => Gvr.DaemonSets, "Job" => Gvr.Jobs, "CronJob" => Gvr.CronJobs, _ => default };
            return gvr == default ? null : objects[gvr].FirstOrDefault(o => o.Name == name && o.Namespace == n);
        }
        var report = Engine(ws).Analyze(pods, Lookup, history, nodeViews.Sum(n => n.CpuAllocatable), nodeViews.Sum(n => n.MemoryAllocatable));
        return (report, history);
    }
}
