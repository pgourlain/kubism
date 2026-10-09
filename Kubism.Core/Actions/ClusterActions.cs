using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Core.Events;

namespace Kubism.Core.Actions;

/// <summary>Marks a public method of <see cref="ClusterActions"/> as a cluster mutation. ClusterActionsTests checks the list is complete.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClusterActionAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public sealed record ConflictResult(JsonObject Fresh, string Diff);

public sealed class ConflictException(JsonObject fresh, string message) : KubeApiException(409, "Conflict", message)
{
    public JsonObject Fresh { get; } = fresh;
}

public sealed record DrainOptions(bool IgnoreDaemonSets = true, bool DeleteEmptyDirData = false, TimeSpan? Timeout = null, bool Force = false);
public sealed record DrainProgress(string Pod, string Status, int Done, int Total);
public sealed record ContainerResources(string Container, string? CpuRequest, string? CpuLimit, string? MemoryRequest, string? MemoryLimit);

/// <summary>The ONLY mutation path. Checks safety mode, logs to the activity log, raises events.</summary>
public sealed class ClusterActions
{
    public const string FieldManager = "kubism";

    private readonly string _clusterId;
    private readonly ApiDiscovery _discovery;
    private readonly IKubeTransport _transport;
    private readonly IClusterEventsService _events;
    private readonly ActivityLog _log;
    private readonly Func<SafetyMode> _mode;

    public ClusterActions(string clusterId, ApiDiscovery discovery, IKubeTransport transport, IClusterEventsService events,
        ActivityLog log, Func<SafetyMode> mode)
    {
        _clusterId = clusterId; _discovery = discovery; _transport = transport; _events = events; _log = log; _mode = mode;
    }

    public SafetyMode Mode => _mode();

    /// <summary>Names of every mutating action, in declaration order.</summary>
    public static IReadOnlyList<string> Actions { get; } = typeof(ClusterActions).GetMethods()
        .Select(m => (m, a: (ClusterActionAttribute?)Attribute.GetCustomAttribute(m, typeof(ClusterActionAttribute))))
        .Where(x => x.a is not null).Select(x => x.a!.Name).ToList();

    // ---------- plumbing ----------

    /// <summary>Authorize + run + log + event. Every action goes through here.</summary>
    public async Task<T> RunAsync<T>(string action, string target, bool destructive, string? confirmation, string? expected, Func<Task<T>> body)
    {
        try
        {
            SafetyPolicy.Check(_mode(), action, target, destructive, confirmation, expected);
        }
        catch (SafetyException ex)
        {
            _log.Add(action, target, false, ex.Message);
            _events.Push(new ActionFailed(_clusterId, action, target, ex.Message));
            throw;
        }
        try
        {
            var r = await body().ConfigureAwait(false);
            _log.Add(action, target, true);
            _events.Push(new ActionCompleted(_clusterId, action, target));
            return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Add(action, target, false, ex.Message);
            _events.Push(new ActionFailed(_clusterId, action, target, ex.Message));
            throw;
        }
        catch (OperationCanceledException)
        {
            _log.Add(action, target, false, "cancelled");
            throw;
        }
    }

    public Task RunAsync(string action, string target, bool destructive, string? confirmation, string? expected, Func<Task> body) =>
        RunAsync<bool>(action, target, destructive, confirmation, expected, async () => { await body().ConfigureAwait(false); return true; });

    public static KubeApiException ToException(KubeResponse r)
    {
        string? reason = null; var message = r.Body;
        try
        {
            if (r.Json is JsonObject o)
            {
                reason = KubeObject.Str(o, "reason");
                message = KubeObject.Str(o, "message") ?? message;
            }
        }
        catch (System.Text.Json.JsonException) { }
        if (string.IsNullOrWhiteSpace(message)) message = $"HTTP {r.StatusCode}";
        return new KubeApiException(r.StatusCode, reason, message);
    }

    private async Task<JsonNode?> SendAsync(string method, string path, JsonNode? body, string contentType, CancellationToken ct)
    {
        var r = await _transport.SendAsync(method, path, body?.ToJsonString(), contentType, ct).ConfigureAwait(false);
        if (r.StatusCode is < 200 or >= 300) throw ToException(r);
        return r.Json;
    }

    private const string Merge = "application/merge-patch+json";
    private const string Strategic = "application/strategic-merge-patch+json";
    private const string Json = "application/json";

    private ResourceKind KindFor(Gvr gvr) => _discovery.Find(gvr) ?? throw new KubeApiException(404, "NotFound", $"Unknown resource {gvr}");

    private static string Target(Gvr gvr, string? ns, string name) => ns is null ? $"{gvr.Resource}/{name}" : $"{gvr.Resource}/{ns}/{name}";

    // ---------- apply / edit ----------

    /// <summary>Server-side apply of one YAML document (create or update).</summary>
    [ClusterAction("Apply")]
    public Task<JsonNode?> ApplyYamlAsync(string yaml, string? defaultNamespace, CancellationToken ct, bool force = false)
    {
        var doc = YamlJson.Parse(yaml) as JsonObject ?? throw new KubeApiException(400, "BadRequest", "YAML document is not an object");
        var (gvr, ns, name, target) = Locate(doc, defaultNamespace);
        return RunAsync("Apply", target, false, null, null, () =>
            SendAsync("PATCH", $"{gvr.ObjectPath(ns, name)}?fieldManager={FieldManager}{(force ? "&force=true" : "")}", doc, "application/apply-patch+yaml", ct));
    }

    /// <summary>Edit flow: PUT with the read resourceVersion. A 409 returns the fresh object in a <see cref="ConflictException"/>, never overwrites.</summary>
    [ClusterAction("Replace")]
    public Task<JsonNode?> ReplaceAsync(string yaml, string? defaultNamespace, CancellationToken ct)
    {
        var doc = YamlJson.Parse(yaml) as JsonObject ?? throw new KubeApiException(400, "BadRequest", "YAML document is not an object");
        var (gvr, ns, name, target) = Locate(doc, defaultNamespace);
        return RunAsync("Replace", target, false, null, null, async () =>
        {
            var r = await _transport.SendAsync("PUT", gvr.ObjectPath(ns, name), doc.ToJsonString(), Json, ct).ConfigureAwait(false);
            if (r.StatusCode == 409)
            {
                var fresh = await _transport.GetAsync(gvr.ObjectPath(ns, name), ct).ConfigureAwait(false) as JsonObject ?? new JsonObject();
                throw new ConflictException(fresh, ToException(r).Message);
            }
            if (r.StatusCode is < 200 or >= 300) throw ToException(r);
            return r.Json;
        });
    }

    private (Gvr gvr, string? ns, string name, string target) Locate(JsonObject doc, string? defaultNs)
    {
        var obj = new KubeObject(doc);
        var kind = _discovery.FindByKind(obj.ApiVersion, obj.Kind) ?? throw new KubeApiException(404, "NotFound", $"The server doesn't have a resource type \"{obj.Kind}\"");
        if (obj.Name.Length == 0) throw new KubeApiException(400, "BadRequest", "metadata.name is required");
        var ns = kind.Namespaced ? (obj.Namespace ?? defaultNs ?? "default") : null;
        return (kind.Gvr, ns, obj.Name, Target(kind.Gvr, ns, obj.Name));
    }

    // ---------- delete ----------

    [ClusterAction("Delete")]
    public Task DeleteAsync(Gvr gvr, string? ns, string name, CancellationToken ct, string propagation = "Background",
        int? gracePeriodSeconds = null, bool force = false, string? confirmation = null)
    {
        var target = Target(gvr, ns, name);
        return RunAsync("Delete", target, true, confirmation, name, async () =>
        {
            var body = new JsonObject { ["apiVersion"] = "v1", ["kind"] = "DeleteOptions", ["propagationPolicy"] = propagation };
            if (force) body["gracePeriodSeconds"] = 0;
            else if (gracePeriodSeconds is not null) body["gracePeriodSeconds"] = gracePeriodSeconds;
            await SendAsync("DELETE", gvr.ObjectPath(ns, name), body, Json, ct).ConfigureAwait(false);
        });
    }

    /// <summary>Bulk delete. Production asks for the count to be typed.</summary>
    [ClusterAction("DeleteMany")]
    public Task<IReadOnlyList<(string target, string? error)>> DeleteManyAsync(Gvr gvr, IReadOnlyList<(string? ns, string name)> items,
        CancellationToken ct, string propagation = "Background", string? confirmation = null)
    {
        var target = $"{items.Count} x {gvr.Resource}";
        return RunAsync<IReadOnlyList<(string, string?)>>("DeleteMany", target, true, confirmation, items.Count.ToString(), async () =>
        {
            var results = new List<(string, string?)>();
            foreach (var (ns, name) in items)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var body = new JsonObject { ["apiVersion"] = "v1", ["kind"] = "DeleteOptions", ["propagationPolicy"] = propagation };
                    await SendAsync("DELETE", gvr.ObjectPath(ns, name), body, Json, ct).ConfigureAwait(false);
                    results.Add((Target(gvr, ns, name), null));
                }
                catch (KubeApiException ex) { results.Add((Target(gvr, ns, name), ex.Message)); }
            }
            return results;
        });
    }

    // ---------- workloads ----------

    [ClusterAction("Scale")]
    public Task ScaleAsync(Gvr gvr, string ns, string name, int replicas, CancellationToken ct, string? confirmation = null)
    {
        var target = Target(gvr, ns, name);
        return RunAsync("Scale", target, replicas == 0, confirmation, name, () =>
            SendAsync("PATCH", gvr.ObjectPath(ns, name, "scale"), new JsonObject { ["spec"] = new JsonObject { ["replicas"] = replicas } }, Merge, ct));
    }

    [ClusterAction("RolloutRestart")]
    public Task RolloutRestartAsync(Gvr gvr, string ns, string name, CancellationToken ct)
    {
        var patch = new JsonObject
        {
            ["spec"] = new JsonObject
            {
                ["template"] = new JsonObject
                {
                    ["metadata"] = new JsonObject
                    {
                        ["annotations"] = new JsonObject { ["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                    },
                },
            },
        };
        return RunAsync("RolloutRestart", Target(gvr, ns, name), false, null, null, () => SendAsync("PATCH", gvr.ObjectPath(ns, name), patch, Strategic, ct));
    }

    [ClusterAction("RolloutPause")]
    public Task SetRolloutPausedAsync(Gvr gvr, string ns, string name, bool paused, CancellationToken ct) =>
        RunAsync(paused ? "RolloutPause" : "RolloutResume", Target(gvr, ns, name), false, null, null,
            () => SendAsync("PATCH", gvr.ObjectPath(ns, name), new JsonObject { ["spec"] = new JsonObject { ["paused"] = paused } }, Merge, ct));

    [ClusterAction("Cordon")]
    public Task SetCordonAsync(string node, bool unschedulable, CancellationToken ct) =>
        RunAsync(unschedulable ? "Cordon" : "Uncordon", $"nodes/{node}", false, null, null,
            () => SendAsync("PATCH", Gvr.Nodes.ObjectPath(null, node), new JsonObject { ["spec"] = new JsonObject { ["unschedulable"] = unschedulable ? true : null } }, Merge, ct));

    [ClusterAction("SuspendCronJob")]
    public Task SetCronJobSuspendedAsync(string ns, string name, bool suspended, CancellationToken ct) =>
        RunAsync(suspended ? "SuspendCronJob" : "ResumeCronJob", $"cronjobs/{ns}/{name}", false, null, null,
            () => SendAsync("PATCH", Gvr.CronJobs.ObjectPath(ns, name), new JsonObject { ["spec"] = new JsonObject { ["suspend"] = suspended } }, Merge, ct));

    [ClusterAction("TriggerCronJob")]
    public Task<string> TriggerCronJobAsync(string ns, string name, CancellationToken ct) =>
        RunAsync("TriggerCronJob", $"cronjobs/{ns}/{name}", false, null, null, async () =>
        {
            var cj = await _transport.GetAsync(Gvr.CronJobs.ObjectPath(ns, name), ct).ConfigureAwait(false) as JsonObject
                     ?? throw new KubeApiException(404, "NotFound", "cronjob not found");
            var jobName = $"{name}-manual-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000000:x}";
            var template = (JsonObject?)cj["spec"]?["jobTemplate"]?.DeepClone() ?? new JsonObject();
            var job = new JsonObject
            {
                ["apiVersion"] = "batch/v1", ["kind"] = "Job",
                ["metadata"] = new JsonObject
                {
                    ["name"] = jobName, ["namespace"] = ns,
                    ["annotations"] = new JsonObject { ["cronjob.kubernetes.io/instantiate"] = "manual" },
                    ["ownerReferences"] = new JsonArray(new JsonObject
                    {
                        ["apiVersion"] = "batch/v1", ["kind"] = "CronJob", ["name"] = name,
                        ["uid"] = cj["metadata"]?["uid"]?.DeepClone(), ["controller"] = true,
                    }),
                },
                ["spec"] = template["spec"]?.DeepClone(),
            };
            await SendAsync("POST", Gvr.Jobs.CollectionPath(ns), job, Json, ct).ConfigureAwait(false);
            return jobName;
        });

    [ClusterAction("SetResources")]
    public Task SetResourcesAsync(Gvr gvr, string ns, string name, IReadOnlyList<ContainerResources> resources, CancellationToken ct, string? confirmation = null)
    {
        var containers = new JsonArray();
        foreach (var r in resources)
        {
            var req = new JsonObject(); var lim = new JsonObject();
            if (r.CpuRequest is not null) req["cpu"] = r.CpuRequest;
            if (r.MemoryRequest is not null) req["memory"] = r.MemoryRequest;
            if (r.CpuLimit is not null) lim["cpu"] = r.CpuLimit;
            if (r.MemoryLimit is not null) lim["memory"] = r.MemoryLimit;
            containers.Add((JsonNode?)new JsonObject { ["name"] = r.Container, ["resources"] = new JsonObject { ["requests"] = req, ["limits"] = lim } });
        }
        var patch = new JsonObject { ["spec"] = new JsonObject { ["template"] = new JsonObject { ["spec"] = new JsonObject { ["containers"] = containers } } } };
        // Triggers a rollout: treated as destructive for production confirmation.
        return RunAsync("SetResources", Target(gvr, ns, name), true, confirmation, name, () => SendAsync("PATCH", gvr.ObjectPath(ns, name), patch, Strategic, ct));
    }

    [ClusterAction("CreateVpa")]
    public Task CreateVpaAsync(string ns, string workloadKind, string workloadName, CancellationToken ct) =>
        RunAsync("CreateVpa", $"vpa/{ns}/{workloadName}", false, null, null, () =>
        {
            var vpa = new JsonObject
            {
                ["apiVersion"] = "autoscaling.k8s.io/v1", ["kind"] = "VerticalPodAutoscaler",
                ["metadata"] = new JsonObject { ["name"] = workloadName, ["namespace"] = ns },
                ["spec"] = new JsonObject
                {
                    ["targetRef"] = new JsonObject { ["apiVersion"] = "apps/v1", ["kind"] = workloadKind, ["name"] = workloadName },
                    ["updatePolicy"] = new JsonObject { ["updateMode"] = "Off" },
                },
            };
            return SendAsync("POST", $"/apis/autoscaling.k8s.io/v1/namespaces/{ns}/verticalpodautoscalers", vpa, Json, ct);
        });

    // ---------- drain ----------

    [ClusterAction("Drain")]
    public Task DrainAsync(string node, DrainOptions options, IProgress<DrainProgress>? progress, CancellationToken ct, string? confirmation = null) =>
        RunAsync("Drain", $"nodes/{node}", true, confirmation, node, async () =>
        {
            await SendAsync("PATCH", Gvr.Nodes.ObjectPath(null, node), new JsonObject { ["spec"] = new JsonObject { ["unschedulable"] = true } }, Merge, ct).ConfigureAwait(false);
            var list = await _transport.ListAsync(Gvr.Pods, null, new ListOptions { FieldSelector = $"spec.nodeName={node}" }, ct).ConfigureAwait(false);
            var pods = new List<KubeObject>();
            foreach (var p in list.Items.Select(i => new KubeObject(i)))
            {
                if (p.Annotations.ContainsKey("kubernetes.io/config.mirror")) continue;
                var ds = p.OwnerReferences.Any(o => o.Kind == "DaemonSet");
                if (ds)
                {
                    if (!options.IgnoreDaemonSets) throw new KubeApiException(400, "BadRequest", $"cannot delete DaemonSet-managed pod {p.Namespace}/{p.Name} (use ignore DaemonSets)");
                    continue;
                }
                if (!options.DeleteEmptyDirData && (p.At("spec", "volumes") as JsonArray)?.OfType<JsonObject>().Any(v => v.ContainsKey("emptyDir")) == true)
                    throw new KubeApiException(400, "BadRequest", $"pod {p.Namespace}/{p.Name} has emptyDir data (enable delete emptyDir data)");
                pods.Add(p);
            }
            var deadline = DateTimeOffset.UtcNow + (options.Timeout ?? TimeSpan.FromMinutes(5));
            var done = 0;
            foreach (var pod in pods)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var ev = new JsonObject
                    {
                        ["apiVersion"] = "policy/v1", ["kind"] = "Eviction",
                        ["metadata"] = new JsonObject { ["name"] = pod.Name, ["namespace"] = pod.Namespace },
                    };
                    var r = await _transport.SendAsync("POST", Gvr.Pods.ObjectPath(pod.Namespace, pod.Name, "eviction"), ev.ToJsonString(), Json, ct).ConfigureAwait(false);
                    if (r.StatusCode is >= 200 and < 300 or 404) break;
                    if (r.StatusCode == 429)
                    {
                        progress?.Report(new DrainProgress($"{pod.Namespace}/{pod.Name}", "blocked by a PodDisruptionBudget, retrying", done, pods.Count));
                        if (DateTimeOffset.UtcNow > deadline) throw new KubeApiException(429, "TooManyRequests", $"drain timed out: {pod.Namespace}/{pod.Name} blocked by a PodDisruptionBudget");
                        await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw ToException(r);
                }
                done++;
                progress?.Report(new DrainProgress($"{pod.Namespace}/{pod.Name}", "evicted", done, pods.Count));
            }
        });

    /// <summary>Delay between eviction retries when a PDB blocks (tests shorten it).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
