using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Views;

public enum TimelineKind { Event, Rollout, Created, Restart, OomKilled, Condition }

public enum TimelineSeverity { Normal, Warning, Error }

/// <param name="Source">The object the item is about ("Pod/web-abc"), so a workload's timeline tells its pods apart.</param>
public sealed record TimelineItem(DateTimeOffset Time, TimelineKind Kind, TimelineSeverity Severity, string Title, string Detail, string Source, int Count = 1)
{
    public bool IsProblem => Severity != TimelineSeverity.Normal;
}

/// <summary>
/// One time axis for a workload (or a pod): its events and those of its replica sets and pods, rollouts (one replica set per revision),
/// pod creations, the last container termination of each pod (OOM kills, crashes) and its conditions, newest first.
/// </summary>
public static class EventTimeline
{
    private const int MaxItems = 300;

    public static IReadOnlyList<TimelineItem> Build(KubeObject root, IEnumerable<KubeObject> candidates, IEnumerable<KubeObject> events)
    {
        var all = candidates.Where(c => c.Uid != root.Uid).ToList();
        var members = Descendants(root, all);
        var uids = members.Select(m => m.Uid).Append(root.Uid).ToHashSet();
        var items = new List<TimelineItem>();

        foreach (var e in events)
        {
            var io = e.At("involvedObject") as JsonObject;
            if (io?["uid"]?.ToString() is not { } uid || !uids.Contains(uid)) continue;
            var ev = new EventView(e);
            if (ev.LastSeen is not { } at) continue;
            var count = e.IntAt("count") ?? 1;
            items.Add(new TimelineItem(at, TimelineKind.Event, ev.IsWarning ? TimelineSeverity.Warning : TimelineSeverity.Normal, ev.Reason, ev.Message, $"{ev.InvolvedKind}/{ev.InvolvedName}", count));
        }

        foreach (var obj in members.Prepend(root))
        {
            var source = $"{obj.Kind}/{obj.Name}";
            if (obj.Kind == "ReplicaSet" && obj.Uid != root.Uid && obj.CreationTimestamp is { } rsAt)
            {
                var rev = obj.Annotations.GetValueOrDefault("deployment.kubernetes.io/revision");
                var images = string.Join(", ", ImagesOf(obj).Distinct());
                items.Add(new TimelineItem(rsAt, TimelineKind.Rollout, TimelineSeverity.Normal, rev is null ? $"Replica set {obj.Name} created" : $"Rollout to revision {rev}", images, source));
            }
            if (obj.Kind == "Pod" && obj.CreationTimestamp is { } podAt)
                items.Add(new TimelineItem(podAt, TimelineKind.Created, TimelineSeverity.Normal, "Pod created", obj.StringAt("spec", "nodeName") is { } n ? $"scheduled on {n}" : "", source));
            if (obj.Kind == "Pod") items.AddRange(Terminations(obj, source));
            items.AddRange(Conditions(obj, source));
        }
        return items.OrderByDescending(i => i.Time).Take(MaxItems).ToList();
    }

    /// <summary>Everything the root owns, down through replica sets and jobs to pods.</summary>
    private static List<KubeObject> Descendants(KubeObject root, List<KubeObject> all)
    {
        var result = new List<KubeObject>();
        var frontier = new List<KubeObject> { root };
        for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
        {
            var uids = frontier.Select(f => f.Uid).ToHashSet();
            var next = all.Where(c => !result.Contains(c) && c.OwnerReferences.Any(o => o.Uid.Length > 0 && uids.Contains(o.Uid))).ToList();
            result.AddRange(next);
            frontier = next;
        }
        return result;
    }

    private static IEnumerable<string> ImagesOf(KubeObject o) =>
        (o.At("spec", "template", "spec", "containers") as JsonArray ?? []).Select(c => c?["image"]?.ToString() ?? "").Where(i => i.Length > 0);

    private static IEnumerable<TimelineItem> Terminations(KubeObject pod, string source)
    {
        foreach (var cs in pod.At("status", "containerStatuses") as JsonArray ?? [])
        {
            var t = cs?["lastState"]?["terminated"] as JsonObject;
            if (t is null || KubeObject.Time(t, "finishedAt") is not { } at) continue;
            var reason = t["reason"]?.ToString() ?? "Error"; var exit = t["exitCode"]?.ToString() ?? "?";
            if (reason == "Completed" && exit == "0") continue;
            var name = cs!["name"]?.ToString() ?? "";
            var restarts = cs["restartCount"]?.ToString() ?? "0";
            var oom = reason == "OOMKilled";
            yield return new TimelineItem(at, oom ? TimelineKind.OomKilled : TimelineKind.Restart, oom ? TimelineSeverity.Error : TimelineSeverity.Warning,
                oom ? $"Container {name} killed: out of memory" : $"Container {name} terminated: {reason}", $"exit code {exit}, {restarts} restart(s) so far", source);
        }
    }

    private static IEnumerable<TimelineItem> Conditions(KubeObject o, string source)
    {
        if (o.Kind is not ("Deployment" or "StatefulSet" or "DaemonSet" or "Pod")) yield break;
        foreach (var c in o.At("status", "conditions") as JsonArray ?? [])
        {
            if (c is not JsonObject cond || KubeObject.Time(cond, "lastTransitionTime") is not { } at) continue;
            var type = cond["type"]?.ToString() ?? ""; var status = cond["status"]?.ToString() ?? "";
            // A pod's ready/scheduled conditions are noise when true; only what went wrong, or a deployment's progress, is worth a line.
            var bad = status == "False" && type is "Available" or "Progressing" or "Ready" or "ContainersReady" or "PodScheduled";
            if (o.Kind == "Pod" && !bad) continue;
            var reason = cond["reason"]?.ToString(); var message = cond["message"]?.ToString() ?? "";
            yield return new TimelineItem(at, TimelineKind.Condition, bad ? TimelineSeverity.Warning : TimelineSeverity.Normal,
                $"{type}: {status}{(reason is null ? "" : " (" + reason + ")")}", message, source);
        }
    }
}
