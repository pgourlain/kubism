using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Kubism.Core;
using Kubism.Core.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kubism.Testing;

public sealed record RecordedRequest(string Method, string Path, string Query, string? Authorization, string? Accept, string? Body);

/// <summary>
/// In-process Kubernetes API server for tests and --demo: discovery, list (JSON/Table), watch, CRUD, patch,
/// logs, exec and port-forward websockets, metrics. Not a faithful apiserver: just enough shape.
/// </summary>
public sealed class FakeApiServer : IAsyncDisposable
{
    public const string DefaultToken = "fake-token";

    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly Dictionary<(Gvr, string, string), JsonObject> _store = [];
    private readonly List<(long rv, Gvr gvr, string ns, string type, JsonObject obj)> _history = [];
    private readonly List<Watcher> _watchers = [];
    private readonly Dictionary<string, List<string>> _logs = [];
    private readonly Dictionary<string, Channel<string>> _logTails = [];
    private readonly HashSet<string> _blockedEvictions = [];
    private readonly HashSet<string> _denied = [];
    private long _rv = 1000;
    private long _minValidRv;
    private bool _failAll;

    private sealed class Watcher(Gvr gvr, string ns, bool table, Channel<string> channel, CancellationTokenSource cts)
    {
        public Gvr Gvr { get; } = gvr;
        public string Ns { get; } = ns;
        public bool Table { get; } = table;
        public Channel<string> Channel { get; } = channel;
        public CancellationTokenSource Cts { get; } = cts;
    }

    private FakeApiServer(WebApplication app) { _app = app; }

    public string Url { get; private set; } = "";
    public string Token { get; set; } = DefaultToken;
    public bool RequireAuth { get; set; } = true;
    public bool AggregatedDiscovery { get; set; } = true;
    public bool SupportTable { get; set; } = true;
    public string ServerVersion { get; set; } = "v1.31.0-fake";
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public int WatchCount { get { lock (_gate) return _watchers.Count; } }
    public List<string> ExecInputs { get; } = [];
    public List<(int w, int h)> ExecResizes { get; } = [];
    public Dictionary<string, Dictionary<string, (string cpu, string mem)>> PodMetrics { get; } = [];
    public Dictionary<string, (string cpu, string mem)> NodeMetrics { get; } = [];
    public bool MetricsAvailable { get; set; } = true;

    public static async Task<FakeApiServer> StartAsync(bool seed = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeApiServer(app);
        app.UseWebSockets();
        app.Run(server.HandleAsync);
        await app.StartAsync().ConfigureAwait(false);
        server.Url = app.Urls.First().Replace("localhost", "127.0.0.1");
        if (seed) server.SeedDefault();
        return server;
    }

    // ---------- seeding & test hooks ----------

    public void SeedDefault()
    {
        foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.EndsWith(".yaml")).OrderBy(n => n))
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            SeedYaml(r.ReadToEnd());
        }
        SeedLogs();
    }

    public void SeedYaml(string yaml)
    {
        foreach (var doc in YamlJson.ParseDocuments(yaml).OfType<JsonObject>())
        {
            if (KubeObjectKind(doc) == "List")
                foreach (var item in (doc["items"] as JsonArray ?? []).OfType<JsonObject>()) Upsert((JsonObject)item.DeepClone(), silent: true);
            else Upsert(doc, silent: true);
        }
    }

    private static string? KubeObjectKind(JsonObject o) => FakeKinds.S(o, "kind");

    private void SeedLogs()
    {
        foreach (var pod in List(Gvr.Pods, null))
        {
            var name = FakeKinds.S(pod, "metadata", "name")!; var ns = FakeKinds.S(pod, "metadata", "namespace")!;
            var lines = new List<string>();
            for (var i = 0; i < 20; i++) lines.Add($"{DateTimeOffset.UtcNow.AddSeconds(-20 + i):O} INFO {name} request {i} handled");
            lock (_gate) _logs[$"{ns}/{name}"] = lines;
        }
    }

    public IReadOnlyList<JsonObject> List(Gvr gvr, string? ns)
    {
        lock (_gate) return _store.Where(kv => kv.Key.Item1 == gvr && (ns is null || kv.Key.Item2 == ns)).Select(kv => (JsonObject)kv.Value.DeepClone()).ToList();
    }

    public JsonObject? Get(Gvr gvr, string? ns, string name)
    {
        lock (_gate) return _store.TryGetValue((gvr, ns ?? "", name), out var o) ? (JsonObject)o.DeepClone() : null;
    }

    private Gvr GvrOf(JsonObject o)
    {
        var k = FakeKinds.ByKind(FakeKinds.S(o, "apiVersion") ?? "v1", FakeKinds.S(o, "kind") ?? "")
                ?? throw new InvalidOperationException($"Unknown kind {FakeKinds.S(o, "kind")} in fake server");
        return k.Gvr;
    }

    /// <summary>Insert or replace an object and notify watchers.</summary>
    public JsonObject Upsert(JsonObject obj, bool silent = false)
    {
        var gvr = GvrOf(obj);
        var kind = FakeKinds.Find(gvr.Group, gvr.Version, gvr.Resource)!;
        var meta = obj["metadata"] as JsonObject ?? (JsonObject)(obj["metadata"] = new JsonObject());
        var name = meta["name"]!.GetValue<string>();
        var ns = kind.Namespaced ? (meta["namespace"]?.GetValue<string>() ?? "default") : "";
        if (kind.Namespaced) meta["namespace"] = ns;
        lock (_gate)
        {
            var existed = _store.TryGetValue((gvr, ns, name), out var old);
            meta["uid"] ??= old is null ? Guid.NewGuid().ToString() : old["metadata"]!["uid"]!.DeepClone();
            meta["creationTimestamp"] ??= old is null
                ? DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(5, 5000)).ToString("yyyy-MM-ddTHH:mm:ssZ")
                : old["metadata"]!["creationTimestamp"]!.DeepClone();
            meta["resourceVersion"] = (++_rv).ToString();
            obj["apiVersion"] ??= gvr.ApiVersion;
            obj["kind"] ??= kind.Kind;
            _store[(gvr, ns, name)] = obj;
            if (!silent) Publish(gvr, ns, existed ? "MODIFIED" : "ADDED", obj);
        }
        return obj;
    }

    public void Remove(Gvr gvr, string? ns, string name)
    {
        lock (_gate)
        {
            if (!_store.Remove((gvr, ns ?? "", name), out var o)) return;
            o["metadata"]!["resourceVersion"] = (++_rv).ToString();
            Publish(gvr, ns ?? "", "DELETED", o);
        }
    }

    private void Publish(Gvr gvr, string ns, string type, JsonObject obj)
    {
        var rv = long.Parse(obj["metadata"]!["resourceVersion"]!.GetValue<string>());
        _history.Add((rv, gvr, ns, type, (JsonObject)obj.DeepClone()));
        if (_history.Count > 5000) _history.RemoveRange(0, 1000);
        foreach (var w in _watchers.Where(w => w.Gvr == gvr && (w.Ns == "" || w.Ns == ns)).ToList())
            w.Channel.Writer.TryWrite(EventLine(type, obj, w.Table, gvr));
    }

    /// <summary>The next watch resuming from an older resourceVersion gets ERROR 410.</summary>
    public void ExpireResourceVersions() { lock (_gate) _minValidRv = _rv + 1; _rv++; }

    /// <summary>Drops every open watch connection, as a network failure or API server restart would.</summary>
    public void DisconnectWatchers()
    {
        List<Watcher> ws;
        lock (_gate) ws = [.. _watchers];
        // A watch may finish (and dispose its source) at the same moment: cancelling a disposed source is fine.
        foreach (var w in ws) { try { w.Cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    public void SendBookmarks()
    {
        lock (_gate)
            foreach (var w in _watchers)
            {
                var o = new JsonObject { ["type"] = "BOOKMARK", ["object"] = new JsonObject { ["kind"] = "Pod", ["apiVersion"] = "v1", ["metadata"] = new JsonObject { ["resourceVersion"] = _rv.ToString() } } };
                w.Channel.Writer.TryWrite(o.ToJsonString());
            }
    }

    /// <summary>Every request returns 503 while true (server "down").</summary>
    public bool FailAll { get => _failAll; set { _failAll = value; if (value) DisconnectWatchers(); } }
    public void BlockEviction(string ns, string pod) { lock (_gate) _blockedEvictions.Add($"{ns}/{pod}"); }
    public void UnblockEviction(string ns, string pod) { lock (_gate) _blockedEvictions.Remove($"{ns}/{pod}"); }
    public void Deny(string verb, string resource) { lock (_gate) _denied.Add($"{verb}/{resource}"); }

    public void SetLogs(string ns, string pod, IEnumerable<string> lines) { lock (_gate) _logs[$"{ns}/{pod}"] = lines.ToList(); }
    public void AppendLog(string ns, string pod, string line)
    {
        lock (_gate)
        {
            var key = $"{ns}/{pod}";
            if (!_logs.TryGetValue(key, out var l)) _logs[key] = l = [];
            var stamped = $"{DateTimeOffset.UtcNow:O} {line}";
            l.Add(stamped);
            foreach (var kv in _logTails.Where(k => k.Key.StartsWith(key + "#"))) kv.Value.Writer.TryWrite(stamped);
        }
    }

    public string WriteKubeconfig(string? directory = null, string context = "fake", string? fileName = null, string? ns = null)
    {
        directory ??= Path.Combine(Path.GetTempPath(), "kubism-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName ?? "kubeconfig");
        File.WriteAllText(path, Kubeconfig(context, ns));
        return path;
    }

    public string Kubeconfig(string context = "fake", string? ns = null) => $$"""
        apiVersion: v1
        kind: Config
        current-context: {{context}}
        clusters:
        - name: {{context}}-cluster
          cluster:
            server: {{Url}}
        users:
        - name: {{context}}-user
          user:
            token: {{Token}}
        contexts:
        - name: {{context}}
          context:
            cluster: {{context}}-cluster
            user: {{context}}-user
        {{(ns is null ? "" : "    namespace: " + ns)}}
        """.Replace("\r\n", "\n");

    // ---------- HTTP ----------

    private static JsonObject Status(int code, string reason, string message) => new()
    {
        ["kind"] = "Status", ["apiVersion"] = "v1", ["metadata"] = new JsonObject(), ["status"] = "Failure",
        ["message"] = message, ["reason"] = reason, ["code"] = code,
    };

    private static async Task Json(HttpContext ctx, int code, JsonNode node)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(node.ToJsonString()).ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        string? body = null;
        if (req.ContentLength > 0 || req.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var sr = new StreamReader(req.Body);
            body = await sr.ReadToEndAsync().ConfigureAwait(false);
        }
        Requests.Enqueue(new RecordedRequest(req.Method, req.Path.Value ?? "", req.QueryString.Value ?? "", req.Headers.Authorization, req.Headers.Accept, body));

        if (_failAll) { await Json(ctx, 503, Status(503, "ServiceUnavailable", "server is down")); return; }
        if (RequireAuth && req.Headers.Authorization != $"Bearer {Token}")
        {
            await Json(ctx, 401, Status(401, "Unauthorized", "Unauthorized")); return;
        }

        var path = req.Path.Value!.TrimEnd('/');
        try
        {
            switch (path)
            {
                case "/version":
                    await Json(ctx, 200, new JsonObject { ["major"] = "1", ["minor"] = "31", ["gitVersion"] = ServerVersion }); return;
                case "/readyz" or "/livez" or "/healthz":
                    ctx.Response.ContentType = "text/plain"; await ctx.Response.WriteAsync("ok"); return;
                case "/api" or "/apis":
                    await DiscoveryRoot(ctx, path == "/api"); return;
            }
            if (path.StartsWith("/api/") || path.StartsWith("/apis/"))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                string group, version; List<string> rest;
                if (parts[0] == "api") { group = ""; version = parts[1]; rest = parts.Skip(2).ToList(); }
                else
                {
                    if (parts.Count < 3) { await Json(ctx, 200, LegacyGroup(parts[1])); return; }
                    group = parts[1]; version = parts[2]; rest = parts.Skip(3).ToList();
                }
                if (rest.Count == 0) { await Json(ctx, 200, LegacyResourceList(group, version)); return; }
                await ResourceRequest(ctx, group, version, rest, body);
                return;
            }
            await Json(ctx, 404, Status(404, "NotFound", "the server could not find the requested resource"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!ctx.Response.HasStarted) await Json(ctx, 500, Status(500, "InternalError", ex.Message));
        }
    }

    private static IEnumerable<FakeKind> VisibleKinds => FakeKinds.All;

    private async Task DiscoveryRoot(HttpContext ctx, bool core)
    {
        var accept = ctx.Request.Headers.Accept.ToString();
        if (AggregatedDiscovery && accept.Contains("apidiscovery.k8s.io"))
        {
            var items = new JsonArray();
            foreach (var g in VisibleKinds.Where(k => core ? k.Group == "" : k.Group != "").GroupBy(k => k.Group))
            {
                var resources = new JsonArray();
                foreach (var k in g)
                {
                    var subs = new JsonArray();
                    foreach (var s in k.Subresources) subs.Add((JsonNode?)new JsonObject { ["subresource"] = s, ["verbs"] = new JsonArray(Array.ConvertAll(new[] { "get", "create", "update", "patch" }, v => (JsonNode?)JsonValue.Create(v))) });
                    resources.Add((JsonNode?)new JsonObject
                    {
                        ["resource"] = k.Resource,
                        ["responseKind"] = new JsonObject { ["group"] = k.Group, ["version"] = k.Version, ["kind"] = k.Kind },
                        ["scope"] = k.Namespaced ? "Namespaced" : "Cluster",
                        ["singularResource"] = k.Kind.ToLowerInvariant(),
                        ["verbs"] = new JsonArray(k.Verbs.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                        ["shortNames"] = new JsonArray(k.ShortNames.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                        ["categories"] = new JsonArray((k.Categories ?? []).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                        ["subresources"] = subs,
                    });
                }
                var first = g.First();
                items.Add((JsonNode?)new JsonObject
                {
                    ["metadata"] = g.Key.Length == 0 ? new JsonObject() : new JsonObject { ["name"] = g.Key },
                    ["versions"] = new JsonArray((JsonNode?)new JsonObject { ["version"] = first.Version, ["resources"] = resources, ["freshness"] = "Current" }),
                });
            }
            ctx.Response.ContentType = "application/json;g=apidiscovery.k8s.io;v=v2;as=APIGroupDiscoveryList";
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync(new JsonObject { ["kind"] = "APIGroupDiscoveryList", ["apiVersion"] = "apidiscovery.k8s.io/v2", ["metadata"] = new JsonObject(), ["items"] = items }.ToJsonString());
            return;
        }
        if (core) { await Json(ctx, 200, new JsonObject { ["kind"] = "APIVersions", ["versions"] = new JsonArray((JsonNode?)"v1") }); return; }
        var groups = new JsonArray();
        foreach (var g in VisibleKinds.Where(k => k.Group != "").GroupBy(k => k.Group))
        {
            var v = g.First().Version;
            groups.Add((JsonNode?)new JsonObject
            {
                ["name"] = g.Key,
                ["versions"] = new JsonArray((JsonNode?)new JsonObject { ["groupVersion"] = $"{g.Key}/{v}", ["version"] = v }),
                ["preferredVersion"] = new JsonObject { ["groupVersion"] = $"{g.Key}/{v}", ["version"] = v },
            });
        }
        await Json(ctx, 200, new JsonObject { ["kind"] = "APIGroupList", ["groups"] = groups });
    }

    private static JsonObject LegacyGroup(string group) => new() { ["kind"] = "APIGroup", ["name"] = group };

    private JsonObject LegacyResourceList(string group, string version)
    {
        var res = new JsonArray();
        foreach (var k in VisibleKinds.Where(k => k.Group == group && k.Version == version))
        {
            res.Add((JsonNode?)new JsonObject
            {
                ["name"] = k.Resource, ["kind"] = k.Kind, ["namespaced"] = k.Namespaced,
                ["verbs"] = new JsonArray(k.Verbs.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["shortNames"] = new JsonArray(k.ShortNames.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["categories"] = new JsonArray((k.Categories ?? []).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            });
            foreach (var s in k.Subresources)
                res.Add((JsonNode?)new JsonObject { ["name"] = $"{k.Resource}/{s}", ["kind"] = k.Kind, ["namespaced"] = k.Namespaced, ["verbs"] = new JsonArray((JsonNode?)"get") });
        }
        return new JsonObject { ["kind"] = "APIResourceList", ["groupVersion"] = group.Length == 0 ? version : $"{group}/{version}", ["resources"] = res };
    }

    // ---------- resources ----------

    private async Task ResourceRequest(HttpContext ctx, string group, string version, List<string> rest, string? body)
    {
        if (group == "metrics.k8s.io") { await Metrics(ctx, rest); return; }
        if (group == "authorization.k8s.io" && rest[0] == "selfsubjectaccessreviews") { await AccessReview(ctx, body); return; }
        if (group == "authorization.k8s.io" && rest[0] == "subjectaccessreviews") { await SubjectAccessReview(ctx, body); return; }
        if (group == "authentication.k8s.io" && rest[0] == "selfsubjectreviews") { await SelfSubjectReview(ctx); return; }

        string? ns = null;
        if (rest[0] == "namespaces" && rest.Count >= 3) { ns = rest[1]; rest = rest.Skip(2).ToList(); }
        var resource = rest[0];
        var kind = FakeKinds.Find(group, version, resource);
        if (kind is null) { await Json(ctx, 404, Status(404, "NotFound", $"the server could not find the requested resource ({resource})")); return; }
        var name = rest.Count > 1 ? rest[1] : null;
        var sub = rest.Count > 2 ? rest[2] : null;
        var method = ctx.Request.Method;
        var query = ctx.Request.Query;
        var gvr = kind.Gvr;
        var nsKey = kind.Namespaced ? ns ?? "" : "";

        lock (_gate)
        {
            if (method == "GET" && name is null && _denied.Contains($"list/{resource}")) { }
        }
        if (method == "GET" && name is null && IsDenied("list", resource)) { await Json(ctx, 403, Status(403, "Forbidden", $"{resource} is forbidden: User \"fake\" cannot list resource \"{resource}\" in API group \"{group}\"")); return; }

        if (name is null)
        {
            switch (method)
            {
                case "GET" when query["watch"] == "true": await Watch(ctx, kind, ns); return;
                case "GET": await ListObjects(ctx, kind, ns); return;
                case "POST":
                    {
                        var obj = JsonNode.Parse(body ?? "{}") as JsonObject ?? new JsonObject();
                        var meta = obj["metadata"] as JsonObject ?? (JsonObject)(obj["metadata"] = new JsonObject());
                        if (kind.Namespaced) meta["namespace"] = ns ?? "default";
                        obj["kind"] = kind.Kind; obj["apiVersion"] = gvr.ApiVersion;
                        lock (_gate) if (_store.ContainsKey((gvr, nsKey.Length == 0 && kind.Namespaced ? "default" : nsKey, meta["name"]?.GetValue<string>() ?? ""))) { }
                        if (Get(gvr, kind.Namespaced ? ns ?? "default" : null, meta["name"]?.GetValue<string>() ?? "") is not null)
                        { await Json(ctx, 409, Status(409, "AlreadyExists", $"{resource} \"{meta["name"]}\" already exists")); return; }
                        await Json(ctx, 201, Upsert(obj)); return;
                    }
            }
            await Json(ctx, 405, Status(405, "MethodNotAllowed", "method not allowed")); return;
        }

        // pods subresources
        if (sub == "log") { await Log(ctx, ns!, name); return; }
        if (sub == "exec" || sub == "attach") { await ExecSocket(ctx, ns!, name); return; }
        if (sub == "portforward") { await PortForwardSocket(ctx, ns!, name); return; }
        if (sub == "eviction" && method == "POST")
        {
            bool blocked; lock (_gate) blocked = _blockedEvictions.Contains($"{ns}/{name}");
            if (blocked) { await Json(ctx, 429, Status(429, "TooManyRequests", "Cannot evict pod as it would violate the pod's disruption budget.")); return; }
            if (Get(gvr, ns, name) is null) { await Json(ctx, 404, Status(404, "NotFound", $"pods \"{name}\" not found")); return; }
            Remove(gvr, ns, name);
            await Json(ctx, 201, new JsonObject { ["kind"] = "Status", ["status"] = "Success" }); return;
        }

        var existing = Get(gvr, kind.Namespaced ? ns : null, name);
        switch (method)
        {
            case "GET":
                if (existing is null) { await Json(ctx, 404, Status(404, "NotFound", $"{resource} \"{name}\" not found")); return; }
                if (sub == "scale")
                {
                    await Json(ctx, 200, new JsonObject
                    {
                        ["kind"] = "Scale", ["apiVersion"] = "autoscaling/v1", ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = ns },
                        ["spec"] = new JsonObject { ["replicas"] = existing["spec"]?["replicas"]?.DeepClone() },
                    }); return;
                }
                if (wantsTable(ctx)) { await Json(ctx, 200, TableOf(kind, [existing], null, null)); return; }
                await Json(ctx, 200, existing); return;
            case "PUT":
                {
                    if (existing is null) { await Json(ctx, 404, Status(404, "NotFound", $"{resource} \"{name}\" not found")); return; }
                    var obj = JsonNode.Parse(body ?? "{}") as JsonObject ?? new JsonObject();
                    var sent = FakeKinds.S(obj, "metadata", "resourceVersion");
                    var current = FakeKinds.S(existing, "metadata", "resourceVersion");
                    if (sent is not null && sent != current)
                    { await Json(ctx, 409, Status(409, "Conflict", $"Operation cannot be fulfilled on {resource} \"{name}\": the object has been modified; please apply your changes to the latest version and try again")); return; }
                    ((JsonObject)(obj["metadata"] ??= new JsonObject()))["uid"] = existing["metadata"]!["uid"]!.DeepClone();
                    ((JsonObject)obj["metadata"]!)["creationTimestamp"] = existing["metadata"]!["creationTimestamp"]!.DeepClone();
                    await Json(ctx, 200, Upsert(obj)); return;
                }
            case "PATCH":
                {
                    var ct = ctx.Request.ContentType ?? "";
                    var patch = JsonNode.Parse(body ?? "{}") as JsonObject ?? new JsonObject();
                    if (existing is null)
                    {
                        if (ct.Contains("apply-patch"))
                        {
                            patch["kind"] ??= kind.Kind; patch["apiVersion"] ??= gvr.ApiVersion;
                            var m = patch["metadata"] as JsonObject ?? (JsonObject)(patch["metadata"] = new JsonObject());
                            if (kind.Namespaced) m["namespace"] ??= ns;
                            await Json(ctx, 201, Upsert(patch)); return;
                        }
                        await Json(ctx, 404, Status(404, "NotFound", $"{resource} \"{name}\" not found")); return;
                    }
                    if (sub == "scale")
                    {
                        var rep = patch["spec"]?["replicas"]?.GetValue<int>();
                        if (rep is not null)
                        {
                            existing["spec"] ??= new JsonObject();
                            existing["spec"]!["replicas"] = rep;
                            existing["status"] ??= new JsonObject();
                            existing["status"]!["replicas"] = rep; existing["status"]!["readyReplicas"] = rep; existing["status"]!["availableReplicas"] = rep; existing["status"]!["updatedReplicas"] = rep;
                        }
                        Upsert(existing);
                        await Json(ctx, 200, new JsonObject { ["kind"] = "Scale", ["spec"] = new JsonObject { ["replicas"] = rep } }); return;
                    }
                    var strategic = ct.Contains("strategic");
                    var merged = Merge(existing, patch, strategic);
                    await Json(ctx, 200, Upsert((JsonObject)merged!)); return;
                }
            case "DELETE":
                if (existing is null) { await Json(ctx, 404, Status(404, "NotFound", $"{resource} \"{name}\" not found")); return; }
                Remove(gvr, kind.Namespaced ? ns : null, name);
                await Json(ctx, 200, new JsonObject { ["kind"] = "Status", ["status"] = "Success", ["details"] = new JsonObject { ["name"] = name } }); return;
        }
        await Json(ctx, 405, Status(405, "MethodNotAllowed", "method not allowed"));
    }

    private bool IsDenied(string verb, string resource) { lock (_gate) return _denied.Contains($"{verb}/{resource}"); }

    private bool wantsTable(HttpContext ctx) => SupportTable && ctx.Request.Headers.Accept.ToString().Contains("as=Table");

    /// <summary>JSON merge patch; with <paramref name="strategic"/>, lists of objects with "name" merge by name (containers, env…).</summary>
    public static JsonNode? Merge(JsonNode? target, JsonNode? patch, bool strategic)
    {
        if (patch is JsonObject po)
        {
            var to = target as JsonObject ?? new JsonObject();
            var result = (JsonObject)to.DeepClone();
            foreach (var kv in po)
            {
                if (kv.Value is null) { result.Remove(kv.Key); continue; }
                result[kv.Key] = Merge(result[kv.Key], kv.Value, strategic);
            }
            return result;
        }
        if (strategic && patch is JsonArray pa && target is JsonArray ta && pa.All(x => x is JsonObject o && o.ContainsKey("name")))
        {
            var result = (JsonArray)ta.DeepClone();
            foreach (var item in pa.OfType<JsonObject>())
            {
                var n = item["name"]!.GetValue<string>();
                var idx = result.ToList().FindIndex(x => x is JsonObject o && o["name"]?.GetValue<string>() == n);
                if (idx >= 0) result[idx] = Merge(result[idx], item, true);
                else result.Add((JsonNode?)item.DeepClone());
            }
            return result;
        }
        return patch?.DeepClone();
    }

    /// <summary>Who the fake API server says the caller is (SelfSubjectReview); null user answers 404, like a server older than 1.28.</summary>
    public string? FakeUser { get; set; } = "fake";
    public string[] FakeGroups { get; set; } = ["system:authenticated"];
    /// <summary>Answers SubjectAccessReview (spec.user, spec.groups, resourceAttributes); default is "allowed unless denied with Deny".</summary>
    public Func<JsonObject, bool>? SubjectReviewHandler { get; set; }

    private async Task SelfSubjectReview(HttpContext ctx)
    {
        if (FakeUser is null) { await Json(ctx, 404, Status(404, "NotFound", "the server could not find the requested resource")); return; }
        await Json(ctx, 201, new JsonObject
        {
            ["apiVersion"] = "authentication.k8s.io/v1", ["kind"] = "SelfSubjectReview",
            ["status"] = new JsonObject { ["userInfo"] = new JsonObject { ["username"] = FakeUser, ["groups"] = new JsonArray(FakeGroups.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()) } },
        });
    }

    private async Task SubjectAccessReview(HttpContext ctx, string? body)
    {
        var spec = (JsonNode.Parse(body ?? "{}") as JsonObject)?["spec"] as JsonObject ?? new JsonObject();
        var attrs = spec["resourceAttributes"] as JsonObject ?? new JsonObject();
        var allowed = SubjectReviewHandler?.Invoke(spec) ?? !IsDenied(FakeKinds.S(attrs, "verb") ?? "", FakeKinds.S(attrs, "resource") ?? "");
        await Json(ctx, 201, new JsonObject { ["kind"] = "SubjectAccessReview", ["status"] = new JsonObject { ["allowed"] = allowed, ["reason"] = "fake api server" } });
    }

    private async Task AccessReview(HttpContext ctx, string? body)
    {
        var attrs = (JsonNode.Parse(body ?? "{}") as JsonObject)?["spec"]?["resourceAttributes"] as JsonObject;
        var verb = FakeKinds.S(attrs ?? new JsonObject(), "verb") ?? ""; var res = FakeKinds.S(attrs ?? new JsonObject(), "resource") ?? "";
        await Json(ctx, 201, new JsonObject { ["kind"] = "SelfSubjectAccessReview", ["status"] = new JsonObject { ["allowed"] = !IsDenied(verb, res) } });
    }

    private async Task Metrics(HttpContext ctx, List<string> rest)
    {
        if (!MetricsAvailable) { await Json(ctx, 404, Status(404, "NotFound", "the server could not find the requested resource")); return; }
        var items = new JsonArray();
        if (rest.Last() == "nodes" || rest[0] == "nodes")
            foreach (var (n, m) in NodeMetrics) items.Add((JsonNode?)new JsonObject { ["metadata"] = new JsonObject { ["name"] = n }, ["usage"] = new JsonObject { ["cpu"] = m.cpu, ["memory"] = m.mem } });
        else
            foreach (var (nsName, pods) in PodMetrics)
            {
                if (rest[0] == "namespaces" && rest[1] != nsName) continue;
                foreach (var (p, m) in pods)
                    items.Add((JsonNode?)new JsonObject
                    {
                        ["metadata"] = new JsonObject { ["name"] = p, ["namespace"] = nsName },
                        ["containers"] = new JsonArray((JsonNode?)new JsonObject { ["name"] = "main", ["usage"] = new JsonObject { ["cpu"] = m.cpu, ["memory"] = m.mem } }),
                    });
            }
        await Json(ctx, 200, new JsonObject { ["kind"] = "PodMetricsList", ["apiVersion"] = "metrics.k8s.io/v1beta1", ["items"] = items });
    }

    private static bool Matches(JsonObject o, string? labelSelector, string? fieldSelector)
    {
        if (!string.IsNullOrEmpty(labelSelector))
        {
            var labels = o["metadata"]?["labels"] as JsonObject;
            foreach (var term in labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = term.Split('=', 2, StringSplitOptions.TrimEntries);
                var k = kv[0].TrimEnd('!'); var neg = kv[0].EndsWith('!');
                var have = labels?[k]?.GetValue<string>();
                if (kv.Length == 2) { var want = kv[1].TrimStart('='); if ((have == want) == neg) return false; }
                else if (have is null) return false;
            }
        }
        if (!string.IsNullOrEmpty(fieldSelector))
            foreach (var term in fieldSelector.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = term.Split('=', 2);
                if (kv.Length != 2) continue;
                var path = kv[0].Split('.');
                if (path[0] == "metadata" || path[0] == "spec" || path[0] == "involvedObject" || path[0] == "status")
                    if (FakeKinds.S(o, path) != kv[1].TrimStart('=')) return false;
            }
        return true;
    }

    private async Task ListObjects(HttpContext ctx, FakeKind kind, string? ns)
    {
        var q = ctx.Request.Query;
        long rv; List<JsonObject> items;
        lock (_gate)
        {
            rv = _rv;
            items = _store.Where(kv => kv.Key.Item1 == kind.Gvr && (ns is null || !kind.Namespaced || kv.Key.Item2 == ns)).Select(kv => kv.Value)
                .Where(o => Matches(o, q["labelSelector"], q["fieldSelector"]))
                .OrderBy(o => FakeKinds.S(o, "metadata", "namespace")).ThenBy(o => FakeKinds.S(o, "metadata", "name")).Select(o => (JsonObject)o.DeepClone()).ToList();
        }
        string? cont = null;
        if (int.TryParse(q["limit"], out var limit) && limit > 0)
        {
            var offset = int.TryParse(q["continue"], out var o) ? o : 0;
            var page = items.Skip(offset).Take(limit).ToList();
            if (offset + limit < items.Count) cont = (offset + limit).ToString();
            items = page;
        }
        if (wantsTable(ctx)) { await Json(ctx, 200, TableOf(kind, items, rv.ToString(), cont)); return; }
        var list = new JsonObject
        {
            ["kind"] = kind.Kind + "List", ["apiVersion"] = kind.Gvr.ApiVersion,
            ["metadata"] = new JsonObject { ["resourceVersion"] = rv.ToString(), ["continue"] = cont },
            ["items"] = new JsonArray(items.Select(i => (JsonNode?)i).ToArray()),
        };
        await Json(ctx, 200, list);
    }

    private static JsonObject TableOf(FakeKind kind, List<JsonObject> items, string? rv, string? cont)
    {
        var cols = new JsonArray(kind.Columns.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["type"] = c.Type, ["format"] = c.Name == "Name" ? "name" : "", ["description"] = c.Name, ["priority"] = c.Priority }).ToArray());
        var rows = new JsonArray(items.Select(i => (JsonNode?)Row(kind, i)).ToArray());
        return new JsonObject
        {
            ["kind"] = "Table", ["apiVersion"] = "meta.k8s.io/v1",
            ["metadata"] = new JsonObject { ["resourceVersion"] = rv, ["continue"] = cont },
            ["columnDefinitions"] = cols, ["rows"] = rows,
        };
    }

    private static JsonObject Row(FakeKind kind, JsonObject item) => new()
    {
        ["cells"] = new JsonArray(kind.Columns.Select(c => (JsonNode?)(c.Cell(item) is { } s ? JsonValue.Create(s) : null)).ToArray()),
        ["object"] = item,
    };

    // ---------- watch ----------

    private string EventLine(string type, JsonObject obj, bool table, Gvr gvr)
    {
        if (table && FakeKinds.Find(gvr.Group, gvr.Version, gvr.Resource) is { } k)
        {
            var t = TableOf(k, [(JsonObject)obj.DeepClone()], null, null);
            ((JsonObject)t["metadata"]!)["resourceVersion"] = obj["metadata"]!["resourceVersion"]!.DeepClone();
            return new JsonObject { ["type"] = type, ["object"] = t }.ToJsonString();
        }
        return new JsonObject { ["type"] = type, ["object"] = obj.DeepClone() }.ToJsonString();
    }

    private async Task Watch(HttpContext ctx, FakeKind kind, string? ns)
    {
        var table = wantsTable(ctx);
        var q = ctx.Request.Query;
        long.TryParse(q["resourceVersion"], out var fromRv);
        var timeout = int.TryParse(q["timeoutSeconds"], out var ts) ? Math.Min(ts, 600) : 600;
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        await ctx.Response.Body.FlushAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        var channel = Channel.CreateUnbounded<string>();
        var watcher = new Watcher(kind.Gvr, kind.Namespaced ? ns ?? "" : "", table, channel, cts);
        List<string> replay = [];
        bool gone;
        lock (_gate)
        {
            gone = fromRv != 0 && fromRv < _minValidRv;
            if (!gone)
            {
                if (fromRv != 0)
                    replay = _history.Where(h => h.rv > fromRv && h.gvr == kind.Gvr && (watcher.Ns == "" || h.ns == watcher.Ns)).Select(h => EventLine(h.type, h.obj, table, kind.Gvr)).ToList();
                _watchers.Add(watcher);
            }
        }
        try
        {
            if (gone)
            {
                var err = new JsonObject { ["type"] = "ERROR", ["object"] = Status(410, "Expired", $"too old resource version: {fromRv} ({_minValidRv})") };
                await ctx.Response.WriteAsync(err.ToJsonString() + "\n"); await ctx.Response.Body.FlushAsync();
                return;
            }
            foreach (var l in replay) await ctx.Response.WriteAsync(l + "\n");
            await ctx.Response.Body.FlushAsync();
            await foreach (var line in channel.Reader.ReadAllAsync(cts.Token))
            {
                await ctx.Response.WriteAsync(line + "\n", cts.Token);
                await ctx.Response.Body.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (_gate) _watchers.Remove(watcher); }
    }

    // ---------- logs ----------

    private async Task Log(HttpContext ctx, string ns, string pod)
    {
        var q = ctx.Request.Query;
        var ts = q["timestamps"] == "true";
        var follow = q["follow"] == "true";
        var container = q["container"].ToString();
        var key = $"{ns}/{pod}";
        List<string> lines;
        lock (_gate) lines = _logs.TryGetValue(key, out var l) ? [.. l] : [];
        if (q["previous"] == "true") lines = lines.Select(x => x).Take(3).Concat(["panic: previous container crashed"]).ToList();
        if (int.TryParse(q["tailLines"], out var tail)) lines = lines.TakeLast(tail).ToList();
        ctx.Response.ContentType = "text/plain";
        ctx.Response.StatusCode = 200;
        string Fmt(string s) => ts ? s : (s.Length > 28 && s[4] == '-' ? s[(s.IndexOf(' ') + 1)..] : s);
        foreach (var line in lines) await ctx.Response.WriteAsync(Fmt(line) + "\n");
        await ctx.Response.Body.FlushAsync();
        if (!follow) return;

        var channel = Channel.CreateUnbounded<string>();
        var id = $"{key}#{Guid.NewGuid():N}";
        lock (_gate) _logTails[id] = channel;
        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                await ctx.Response.WriteAsync(Fmt(line) + "\n");
                await ctx.Response.Body.FlushAsync();
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (_gate) _logTails.Remove(id); }
    }

    // ---------- exec / port-forward ----------

    private static string PickProtocol(HttpContext ctx, params string[] supported)
    {
        var requested = ctx.WebSockets.WebSocketRequestedProtocols;
        return supported.FirstOrDefault(requested.Contains) ?? requested.FirstOrDefault() ?? "";
    }

    private async Task ExecSocket(HttpContext ctx, string ns, string pod)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { await Json(ctx, 400, Status(400, "BadRequest", "websocket required")); return; }
        if (Get(Gvr.Pods, ns, pod) is null) { await Json(ctx, 404, Status(404, "NotFound", $"pods \"{pod}\" not found")); return; }
        var proto = PickProtocol(ctx, "v5.channel.k8s.io", "v4.channel.k8s.io");
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync(proto);
        var buf = new byte[8192];
        async Task Send(byte channel, string text)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            var msg = new byte[payload.Length + 1]; msg[0] = channel; payload.CopyTo(msg, 1);
            await ws.SendAsync(msg, WebSocketMessageType.Binary, true, ctx.RequestAborted);
        }
        try
        {
            await Send(1, "/ # ");
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream(); WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, ctx.RequestAborted); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                var data = ms.ToArray();
                if (data.Length == 0) continue;
                var payload = data[1..];
                switch (data[0])
                {
                    case 0:
                        var text = Encoding.UTF8.GetString(payload);
                        lock (ExecInputs) ExecInputs.Add(text);
                        if (text.Trim() == "exit")
                        {
                            await Send(3, new JsonObject { ["status"] = "Success", ["metadata"] = new JsonObject() }.ToJsonString());
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                            return;
                        }
                        await Send(1, text); // echo
                        break;
                    case 4:
                        try
                        {
                            var j = JsonNode.Parse(payload);
                            lock (ExecResizes) ExecResizes.Add((j!["Width"]!.GetValue<int>(), j["Height"]!.GetValue<int>()));
                        }
                        catch { }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task PortForwardSocket(HttpContext ctx, string ns, string pod)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { await Json(ctx, 400, Status(400, "BadRequest", "websocket required")); return; }
        if (Get(Gvr.Pods, ns, pod) is null) { await Json(ctx, 404, Status(404, "NotFound", $"pods \"{pod}\" not found")); return; }
        var ports = ctx.Request.Query["ports"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        var proto = PickProtocol(ctx, "v4.channel.k8s.io");
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync(proto);
        var buf = new byte[65536];
        async Task Send(byte channel, byte[] payload)
        {
            var msg = new byte[payload.Length + 1]; msg[0] = channel; payload.CopyTo(msg, 1);
            await ws.SendAsync(msg, WebSocketMessageType.Binary, true, ctx.RequestAborted);
        }
        try
        {
            // Each stream starts with the 2-byte little-endian port number.
            for (var i = 0; i < ports.Count; i++)
            {
                var hdr = new[] { (byte)(ports[i] & 0xff), (byte)(ports[i] >> 8) };
                await Send((byte)(i * 2), hdr);
                await Send((byte)(i * 2 + 1), hdr);
            }
            var firstSeen = new HashSet<int>();
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream(); WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, ctx.RequestAborted); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                var data = ms.ToArray();
                if (data.Length < 2) continue;
                var channel = data[0];
                if (channel % 2 == 1) continue;
                var payload = data[1..];
                if (firstSeen.Add(channel) && payload.Length == 2 && channel < 255 && payload.Length == 2 && (payload[0] | payload[1] << 8) == ports[channel / 2]) continue; // client port header
                var text = Encoding.ASCII.GetString(payload);
                if (text.StartsWith("GET "))
                {
                    var bodyText = $"hello from {pod}:{ports[channel / 2]}";
                    var resp = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bodyText.Length}\r\nConnection: close\r\n\r\n{bodyText}";
                    await Send(channel, Encoding.ASCII.GetBytes(resp));
                }
                else await Send(channel, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        DisconnectWatchers();
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
        await _app.DisposeAsync();
    }
}
