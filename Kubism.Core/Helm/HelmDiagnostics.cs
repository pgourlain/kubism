using System.Text;
using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Views;

namespace Kubism.Core.Helm;

public sealed record DiagEvent(string Object, string Reason, string Message, string Age);
public sealed record DiagLog(string Container, bool Previous, string Text);
public sealed record DiagPod(string Name, string Status, int Restarts, IReadOnlyList<DiagLog> Logs);

/// <summary>Everything needed to see at a glance why a Helm release failed or timed out.</summary>
public sealed record HelmDiagnosis(HelmRelease Release, string Headline, IReadOnlyList<DiagEvent> Events, IReadOnlyList<DiagPod> Pods)
{
    public bool HasFindings => Events.Count > 0 || Pods.Count > 0;
}

public static class HelmDiagnostics
{
    private static readonly string[] Pending = ["pending-install", "pending-upgrade", "pending-rollback"];

    /// <summary>Failed, or stuck in a pending state long enough that helm --wait has timed out.</summary>
    public static bool IsProblem(HelmRelease r, DateTimeOffset now, TimeSpan? stuckAfter = null) =>
        r.Status == "failed" || (Pending.Contains(r.Status) && r.Updated is { } u && now - u > (stuckAfter ?? TimeSpan.FromMinutes(5)));

    public static string Headline(HelmRelease r, DateTimeOffset now) => r.Status switch
    {
        "failed" => $"Release failed{(string.IsNullOrWhiteSpace(r.Description) ? "" : ": " + r.Description)}",
        _ when Pending.Contains(r.Status) => $"Stuck in {r.Status} since {Core.KubeTime.Age(r.Updated, default, now)} (likely a --wait timeout){(string.IsNullOrWhiteSpace(r.Description) ? "" : ": " + r.Description)}",
        _ => r.Status,
    };

    public static async Task<HelmDiagnosis> CollectAsync(IKubeTransport t, HelmRelease r, CancellationToken ct, int maxPods = 8, int logLines = 40)
    {
        var now = DateTimeOffset.UtcNow;
        var objs = new List<(string kind, string name)>();
        try
        {
            foreach (var d in YamlJson.ParseDocuments(r.Manifest ?? "").OfType<JsonObject>())
                if (d["kind"]?.ToString() is { } k && d["metadata"]?["name"]?.ToString() is { } n) objs.Add((k, n));
        }
        catch (Exception) { /* unreadable manifest: fall back to labels only */ }
        var workloadNames = objs.Where(o => o.kind is "Deployment" or "StatefulSet" or "DaemonSet" or "Job" or "CronJob" or "ReplicaSet").Select(o => o.name).ToList();

        var podList = await t.ListAsync(Gvr.Pods, r.Namespace, new ListOptions(), ct).ConfigureAwait(false);
        var pods = podList.Items.Select(i => new PodView(new KubeObject(i)))
            .Where(p => p.Object.Labels.TryGetValue("app.kubernetes.io/instance", out var v) && v == r.Name
                        || p.Object.Labels.TryGetValue("release", out var v2) && v2 == r.Name
                        || workloadNames.Any(w => p.Object.Name.StartsWith(w + "-") || p.Object.Name == w)).ToList();
        var bad = pods.Where(p => p.Status is not ("Running" or "Completed" or "Succeeded") || p.Restarts > 0 || p.ReadyCount < p.ContainerCount).Take(maxPods).ToList();

        var diagPods = new List<DiagPod>();
        foreach (var p in bad)
        {
            var logs = new List<DiagLog>();
            foreach (var c in p.Containers.Where(c => !c.IsInit || c.State != "Running" && c.Restarts > 0 || c.IsInit && c.State == "Terminated" && c.Reason != "Completed"))
            {
                if (c.State == "Waiting" && c.Restarts == 0 && c.Reason is "ContainerCreating" or "PodInitializing" or "ImagePullBackOff" or "ErrImagePull") continue; // nothing to read yet; events say why
                if (c.Restarts > 0 && await TailAsync(t, p.Object, c.Name, true, logLines, ct).ConfigureAwait(false) is { Length: > 0 } prev) logs.Add(new DiagLog(c.Name, true, prev));
                if (await TailAsync(t, p.Object, c.Name, false, logLines, ct).ConfigureAwait(false) is { Length: > 0 } cur) logs.Add(new DiagLog(c.Name, false, cur));
            }
            diagPods.Add(new DiagPod(p.Object.Name, p.Status, p.Restarts, logs));
        }

        var names = new HashSet<string>(objs.Select(o => o.name).Concat(pods.Select(p => p.Object.Name)));
        var since = (r.Updated ?? now.AddHours(-1)).AddMinutes(-2);
        var evList = await t.ListAsync(Gvr.Events, r.Namespace, new ListOptions(), ct).ConfigureAwait(false);
        var events = evList.Items.Select(i => new EventView(new KubeObject(i)))
            .Where(e => names.Contains(e.InvolvedName) && (e.IsWarning || e.Reason is "FailedCreate" or "BackoffLimitExceeded" or "DeadlineExceeded") && (e.LastSeen is null || e.LastSeen >= since))
            .OrderByDescending(e => e.LastSeen).Take(25)
            .Select(e => new DiagEvent($"{e.InvolvedKind}/{e.InvolvedName}", e.Reason, e.Message, Core.KubeTime.Age(e.LastSeen, default, now))).ToList();

        return new HelmDiagnosis(r, Headline(r, now), events, diagPods);
    }

    private static async Task<string> TailAsync(IKubeTransport t, KubeObject pod, string container, bool previous, int lines, CancellationToken ct)
    {
        try
        {
            var path = $"{Gvr.Pods.ObjectPath(pod.Namespace, pod.Name, "log")}?container={Uri.EscapeDataString(container)}&tailLines={lines}{(previous ? "&previous=true" : "")}";
            await using var s = await t.StreamAsync(path, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[4096]; int n;
            while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0 && ms.Length < 8192) ms.Write(buf, 0, n);
            return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd();
        }
        catch (KubeApiException) { return ""; } // no previous container, still creating…
    }
}
