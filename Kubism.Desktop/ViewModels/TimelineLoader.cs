using Kubism.Core.Api;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

/// <summary>Loads what <see cref="EventTimeline"/> needs for a workload or pod: replica sets, jobs, pods and the namespace's events.</summary>
public static class TimelineLoader
{
    public static readonly HashSet<string> SupportedKinds = ["Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "Job", "CronJob", "Pod"];

    public static async Task<IReadOnlyList<TimelineItem>> LoadAsync(WorkspaceViewModel ws, KubeObject root, CancellationToken ct)
    {
        if (root.Namespace is not { } ns) return [];
        var t = ws.Session.Transport;
        var candidates = new List<KubeObject>();
        var events = new List<KubeObject>();
        async Task Load(Gvr gvr, List<KubeObject> into)
        {
            try
            {
                var l = await t.ListAsync(gvr, ns, new ListOptions { Limit = 500 }, ct).ConfigureAwait(false);
                lock (into) into.AddRange(l.Items.Select(i => new KubeObject(i)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* forbidden or missing kinds: fewer lines */ }
        }
        var kinds = ws.Session.Discovery.Kinds;
        bool Has(string r) => kinds.Count == 0 || kinds.Any(k => k.Resource == r && k.CanList);
        var loads = new List<Task> { Load(Gvr.Events, events) };
        if (root.Kind != "Pod")
        {
            loads.Add(Load(Gvr.Pods, candidates));
            if (Has("replicasets")) loads.Add(Load(Gvr.ReplicaSets, candidates));
            if (root.Kind == "CronJob" && Has("jobs")) loads.Add(Load(Gvr.Jobs, candidates));
        }
        await Task.WhenAll(loads).ConfigureAwait(false);
        return EventTimeline.Build(root, candidates, events);
    }
}
