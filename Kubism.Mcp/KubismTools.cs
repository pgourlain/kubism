using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Core.Metrics;
using Kubism.Core.RightSizing;
using Kubism.Core.Sessions;
using Kubism.Core.Views;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Kubism.Mcp;

/// <summary>Read-only tools. Secret values never leave: every object goes through <see cref="SecretMasker"/>.</summary>
[McpServerToolType]
public sealed class KubismReadTools(McpContext ctx)
{
    private const int MaxLogBytes = 64 * 1024;

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static string Yaml(JsonObject o) => YamlJson.ToYaml(SecretMasker.WithoutManagedFields(SecretMasker.MaskedCopy(o)));

    [McpServerTool(Name = "list_clusters"), Description("List the clusters (kubeconfig contexts) known to Kubism, with their safety mode.")]
    public Task<string> ListClusters() => ctx.Run(() =>
    {
        var arr = new JsonArray();
        foreach (var e in ctx.Catalog.Entries)
        {
            var m = ctx.Settings.GetMetadata(e.Id);
            var s = ctx.Sessions.Get(e.Id);
            arr.Add((JsonNode?)new JsonObject
            {
                ["id"] = e.Id, ["context"] = e.ContextName, ["name"] = m.Label ?? e.ContextName, ["server"] = e.Server,
                ["safetyMode"] = m.SafetyMode.ToString(), ["connected"] = s?.State is SessionState.Connected or SessionState.Degraded,
            });
        }
        return Task.FromResult(Json(arr));
    });

    [McpServerTool(Name = "list_kinds"), Description("List the resource kinds a cluster serves (built-in and CRDs), with short names and whether they are namespaced.")]
    public Task<string> ListKinds([Description("Cluster context name, label or id")] string cluster, [Description("Optional substring filter on kind, resource, group or short name")] string? filter = null, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var arr = new JsonArray();
            foreach (var k in s.Discovery.Kinds.Where(k => filter is null || k.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase) || k.Resource.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                           || k.Group.Contains(filter, StringComparison.OrdinalIgnoreCase) || k.ShortNames.Any(n => n.Equals(filter, StringComparison.OrdinalIgnoreCase))))
                arr.Add((JsonNode?)new JsonObject
                {
                    ["kind"] = k.Kind, ["group"] = k.Group, ["version"] = k.Version, ["resource"] = k.Resource, ["namespaced"] = k.Namespaced,
                    ["shortNames"] = new JsonArray(k.ShortNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                });
            return Json(arr);
        });

    [McpServerTool(Name = "list_resources"), Description("List objects of a kind as a table (the same columns as kubectl get). Supports label and field selectors. Secret contents are never returned.")]
    public Task<string> ListResources(string cluster, [Description("Kind, plural resource or short name, e.g. pods, deploy, certificates")] string kind, string? @namespace = null,
        string? labelSelector = null, string? fieldSelector = null, [Description("Maximum rows (default 100, max 500)")] int limit = 100, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var k = ctx.ResolveKind(s, kind);
            limit = Math.Clamp(limit, 1, 500);
            var list = await s.Transport.ListAsync(k.Gvr, k.Namespaced ? @namespace : null,
                new ListOptions { Limit = limit, LabelSelector = labelSelector, FieldSelector = fieldSelector, AsTable = true }, ct);
            var cols = list.Columns ?? [new TableColumn("Name", "string", "name", "", 0)];
            var rows = new JsonArray();
            for (var i = 0; i < list.Items.Count; i++)
            {
                var o = new JsonObject();
                var obj = new KubeObject(list.Items[i]);
                if (k.Namespaced) o["namespace"] = obj.Namespace;
                for (var c = 0; c < cols.Count; c++) if (cols[c].Priority == 0) o[cols[c].Name] = list.Cells is null || c >= list.Cells[i].Count ? obj.Name : list.Cells[i][c];
                rows.Add((JsonNode?)o);
            }
            return Json(new JsonObject { ["kind"] = k.Kind, ["count"] = rows.Count, ["truncated"] = !string.IsNullOrEmpty(list.Continue), ["rows"] = rows });
        });

    [McpServerTool(Name = "get_resource"), Description("Get one object as YAML. Secret data values are redacted; managedFields are removed.")]
    public Task<string> GetResource(string cluster, string kind, string name, string? @namespace = null, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var k = ctx.ResolveKind(s, kind);
            var o = await s.Transport.GetAsync(k.Gvr.ObjectPath(k.Namespaced ? @namespace ?? "default" : null, name), ct) as JsonObject
                    ?? throw new McpException("Empty response");
            o["apiVersion"] ??= k.Gvr.ApiVersion; o["kind"] ??= k.Kind;
            return Yaml(o);
        });

    [McpServerTool(Name = "get_events"), Description("Recent events, newest first, optionally for one object.")]
    public Task<string> GetEvents(string cluster, string? @namespace = null, string? involvedName = null, string? involvedKind = null, int limit = 50, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var field = new List<string>();
            if (involvedName is not null) field.Add($"involvedObject.name={involvedName}");
            if (involvedKind is not null) field.Add($"involvedObject.kind={involvedKind}");
            var list = await s.Transport.ListAsync(Gvr.Events, @namespace, new ListOptions { FieldSelector = field.Count == 0 ? null : string.Join(",", field) }, ct);
            var arr = new JsonArray();
            foreach (var e in list.Items.Select(i => new EventView(new KubeObject(i))).OrderByDescending(e => e.LastSeen).Take(Math.Clamp(limit, 1, 200)))
                arr.Add((JsonNode?)new JsonObject
                {
                    ["type"] = e.Type, ["reason"] = e.Reason, ["object"] = $"{e.InvolvedKind}/{e.InvolvedName}", ["message"] = e.Message,
                    ["namespace"] = e.Object.Namespace, ["lastSeen"] = e.LastSeen?.ToString("O"),
                });
            return Json(arr);
        });

    [McpServerTool(Name = "get_logs"), Description("Bounded pod logs (last tailLines, max 64 KiB). Use previous=true for a crashed container.")]
    public Task<string> GetLogs(string cluster, string @namespace, string pod, string? container = null, int tailLines = 200, bool previous = false, int? sinceSeconds = null, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var o = new Core.Logs.LogOptions(@namespace, pod, container, Follow: false, Previous: previous, TailLines: Math.Clamp(tailLines, 1, 2000), SinceSeconds: sinceSeconds);
            await using var stream = await s.Transport.StreamAsync(Core.Logs.LogSession.BuildPath(o).Replace("timestamps=true", "timestamps=false"), ct);
            using var ms = new MemoryStream();
            var buf = new byte[8192]; int n;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length >= MaxLogBytes) break;
            }
            var text = Encoding.UTF8.GetString(ms.ToArray());
            return ms.Length >= MaxLogBytes ? text + "\n… (truncated)" : text;
        });

    [McpServerTool(Name = "top_pods"), Description("Pod CPU/memory usage from metrics-server, highest CPU first.")]
    public Task<string> TopPods(string cluster, string? @namespace = null, int limit = 30, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var path = @namespace is null ? Gvr.PodMetrics.CollectionPath() : Gvr.PodMetrics.CollectionPath(@namespace);
            var list = await s.Transport.GetAsync(path, ct) as JsonObject;
            var arr = new JsonArray();
            foreach (var p in MetricsService.ParsePods(list).OrderByDescending(p => p.CpuCores).Take(Math.Clamp(limit, 1, 200)))
                arr.Add((JsonNode?)new JsonObject { ["namespace"] = p.Namespace, ["pod"] = p.Name, ["cpu"] = Quantities.FormatCpu(p.CpuCores), ["memory"] = Quantities.FormatMemoryMi(p.MemoryBytes) });
            return Json(arr);
        });

    [McpServerTool(Name = "top_nodes"), Description("Node CPU/memory usage from metrics-server.")]
    public Task<string> TopNodes(string cluster, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var list = await s.Transport.GetAsync(Gvr.NodeMetrics.CollectionPath(), ct) as JsonObject;
            var arr = new JsonArray();
            foreach (var n in MetricsService.ParseNodes(list)) arr.Add((JsonNode?)new JsonObject { ["node"] = n.Name, ["cpu"] = Quantities.FormatCpu(n.CpuCores), ["memory"] = Quantities.FormatMemoryMi(n.MemoryBytes) });
            return Json(arr);
        });

    [McpServerTool(Name = "describe"), Description("Object (secrets redacted) with its events and owner chain, like kubectl describe.")]
    public Task<string> Describe(string cluster, string kind, string name, string? @namespace = null, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var k = ctx.ResolveKind(s, kind);
            var ns = k.Namespaced ? @namespace ?? "default" : null;
            var o = await s.Transport.GetAsync(k.Gvr.ObjectPath(ns, name), ct) as JsonObject ?? throw new McpException("Empty response");
            o["apiVersion"] ??= k.Gvr.ApiVersion; o["kind"] ??= k.Kind;
            var obj = new KubeObject(o);
            var sb = new StringBuilder();
            sb.AppendLine($"# {k.Kind} {ns ?? ""}/{name}".Replace(" /", " "));
            if (k.Kind == "Pod") { var pv = new PodView(obj); sb.AppendLine($"Status: {pv.Status}  Ready: {pv.Ready}  Restarts: {pv.Restarts}  Node: {pv.NodeName}"); }
            sb.AppendLine("\n## Owners");
            var cur = obj;
            for (var i = 0; i < 5; i++)
            {
                var owner = cur.OwnerReferences.FirstOrDefault(r => r.Controller) ?? cur.OwnerReferences.FirstOrDefault();
                if (owner is null) break;
                sb.AppendLine($"- {owner.Kind}/{owner.Name}");
                var ok = s.Discovery.FindByKind(owner.ApiVersion, owner.Kind);
                if (ok is null) break;
                try { cur = new KubeObject((JsonObject)(await s.Transport.GetAsync(ok.Gvr.ObjectPath(ok.Namespaced ? ns : null, owner.Name), ct))!); } catch (KubeApiException) { break; }
            }
            sb.AppendLine("\n## Events");
            var ev = await s.Transport.ListAsync(Gvr.Events, null, new ListOptions { FieldSelector = $"involvedObject.uid={obj.Uid}" }, ct);
            foreach (var e in ev.Items.Select(i => new EventView(new KubeObject(i))).OrderByDescending(e => e.LastSeen).Take(20))
                sb.AppendLine($"- {e.Type} {e.Reason}: {e.Message}");
            sb.AppendLine("\n## Object\n");
            sb.Append(Yaml(o));
            return sb.ToString();
        });

    [McpServerTool(Name = "rbac_who_can"), Description("Who can <verb> <resource> in <namespace>, from the cluster's Roles, ClusterRoles and bindings (read-only; same semantics as the API server's RBAC authorizer: wildcards, subresources, resourceNames, aggregated roles, nonResourceURLs). Each subject comes with the binding → role → rule paths that give the right and risk flags. Other authorizers (webhook, Azure RBAC for Kubernetes) are not visible. The result says when some RBAC kinds could not be read.")]
    public Task<string> RbacWhoCan(string cluster, [Description("Verb: get, list, watch, create, update, patch, delete, deletecollection, escalate, bind, impersonate…")] string verb,
        [Description("Resource: secrets, pods/exec, deployments.apps, deployments.apps/scale, or a non-resource URL such as /metrics")] string resource,
        [Description("Namespace; omit for cluster-scoped resources or 'in all namespaces' (only ClusterRoleBindings count)")] string? @namespace = null,
        [Description("Optional object name (matters for roles that use resourceNames)")] string? name = null, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var graph = await Core.Security.RbacLoader.LoadAsync(s.Transport, ct);
            var req = Core.Security.RbacRequest.Parse(verb, resource, string.IsNullOrWhiteSpace(@namespace) ? null : @namespace, name);
            var entries = graph.WhoCan(req);
            var o = RbacHeader(graph);
            o["request"] = req.Display;
            o["subjects"] = new JsonArray(entries.Select(e => (JsonNode?)new JsonObject
            {
                ["subject"] = e.Subject.Display, ["kind"] = e.Subject.Kind, ["everyone"] = e.IsBroad,
                ["risks"] = Risks(e.Risks),
                ["paths"] = Paths(e.Paths),
                ["builtIn"] = e.Paths.Count == 0 ? "the group system:masters is always authorized by the API server, outside RBAC" : null,
            }).ToArray());
            return Json(o);
        });

    [McpServerTool(Name = "rbac_rights_of"), Description("What a subject can do, from the RBAC objects: every binding → role → rule path that grants it (through the groups it implicitly belongs to), per namespace, with risk flags, plus the verbs × resources matrix for one namespace when given. Subject: 'User name', 'Group name', 'ServiceAccount namespace/name' or 'system:serviceaccount:ns:name'. Pass the user's real groups (SelfSubjectReview) in 'groups' if known. Rights are additive; other authorizers are not visible.")]
    public Task<string> RbacRightsOf(string cluster, string subject, [Description("Comma-separated extra groups the subject belongs to (e.g. Entra group object IDs)")] string? groups = null,
        [Description("Namespace for the verbs × resources matrix; omit for cluster-wide grants only")] string? @namespace = null, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var parsed = Core.Security.RbacSubject.Parse(subject) ?? throw new McpException($"Cannot read subject '{subject}'. Use 'User name', 'Group name' or 'ServiceAccount namespace/name'.");
            var extra = (groups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var s = await ctx.SessionAsync(cluster, ct);
            var graph = await Core.Security.RbacLoader.LoadAsync(s.Transport, ct);
            var rights = graph.RightsOf(parsed, extra);
            var o = RbacHeader(graph);
            o["subject"] = parsed.Display;
            o["groups"] = new JsonArray(extra.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray());
            o["namespaces"] = new JsonArray(rights.Namespaces.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            o["notes"] = new JsonArray(rights.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            o["paths"] = new JsonArray(rights.Paths.Select(p => (JsonNode?)new JsonObject
            {
                ["scope"] = p.ScopeText, ["binding"] = p.Binding.Display, ["role"] = p.Role.Display, ["rule"] = p.Rule.Summary,
                ["via"] = p.ViaGroup, ["note"] = p.Note,
                ["risks"] = Risks(Core.Security.RbacGraph.RisksOf(p.Rule, p.Role.Kind == "ClusterRole" ? p.Role.Name : null)),
            }).ToArray());
            var m = graph.MatrixFor(parsed, string.IsNullOrWhiteSpace(@namespace) ? null : @namespace, extra);
            o["matrix"] = new JsonObject
            {
                ["namespace"] = m.Namespace ?? "(cluster-wide)",
                ["resources"] = new JsonArray(m.Rows.Select(r => (JsonNode?)new JsonObject
                {
                    ["resource"] = r.Resource, ["risky"] = r.Risky,
                    ["verbs"] = new JsonArray(m.Verbs.Select((v, i) => (v, c: r.Cells[i])).Where(x => x.c.Level != Core.Security.RightLevel.None)
                        .Select(x => (JsonNode?)JsonValue.Create(x.c.Level == Core.Security.RightLevel.Limited ? x.v + " (named objects only)" : x.v)).ToArray()),
                }).ToArray()),
            };
            return Json(o);
        });

    private static JsonObject RbacHeader(Core.Security.RbacGraph g) => new()
    {
        ["complete"] = g.IsComplete, ["warning"] = g.IncompleteWarning,
        ["orphanBindings"] = new JsonArray(g.OrphanBindings.Select(b => (JsonNode?)JsonValue.Create($"{b.Display} -> missing {b.RoleKind} {b.RoleName}")).ToArray()),
        ["limits"] = "RBAC objects only: webhook authorizers, Azure RBAC for Kubernetes and ABAC are not visible.",
    };

    private static JsonArray Risks(IEnumerable<Core.Security.RbacRisk> risks) =>
        new(risks.Select(r => (JsonNode?)new JsonObject { ["code"] = r.Code, ["severity"] = r.Severity.ToString(), ["description"] = r.Description }).ToArray());

    private static JsonArray Paths(IEnumerable<Core.Security.RbacPath> paths) =>
        new(paths.Select(p => (JsonNode?)new JsonObject
        {
            ["scope"] = p.ScopeText, ["binding"] = p.Binding.Display, ["role"] = p.Role.Display, ["rule"] = p.Rule.Summary, ["via"] = p.ViaGroup, ["note"] = p.Note,
        }).ToArray());

    [McpServerTool(Name = "network_exposure"), Description("How traffic reaches the pods (read-only): what is exposed outside the cluster, through Ingresses and Services, to endpoints and workloads, with hosts, paths and ports on every hop and what does not line up (missing backend, unresolved targetPort, no ready endpoint, missing or expiring TLS secret, no ingress class…). 'Outside the cluster' is judged from Services (LoadBalancer, NodePort, externalIPs), ingress controllers and hostPort, not from DNS, firewalls or WAFs. With a namespace, the controller of another namespace stays as context. The result says when some kinds could not be read.")]
    public Task<string> NetworkExposure(string cluster, [Description("Namespace to show; omit for all. The ingress controller's Service of another namespace is kept as context.")] string? @namespace = null,
        [Description("any, outside (reachable from outside the cluster) or incluster")] string exposure = "any", [Description("Only chains with a problem")] bool problemsOnly = false,
        [Description("Host, path, service, workload or namespace to look for")] string? search = null, [Description("Also list Services nothing routes to")] bool includeInternalServices = false,
        [Description("Add the nodes the pods run on (one node per box, shared by workloads), with an edge per workload and its pod count")] bool showNodes = false, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            var graph = await Core.Network.NetworkLoader.LoadAsync(s.Transport, ct);
            var filter = new Core.Network.NetworkFilter
            {
                Namespaces = string.IsNullOrWhiteSpace(@namespace) ? null : new HashSet<string> { @namespace },
                Exposure = exposure.ToLowerInvariant() switch { "outside" => Core.Network.ExposureFilter.OutsideOnly, "incluster" or "in-cluster" => Core.Network.ExposureFilter.InClusterOnly, "any" => Core.Network.ExposureFilter.Any, _ => throw new McpException("exposure must be any, outside or incluster") },
                ProblemsOnly = problemsOnly, Search = search, IncludeInternalServices = includeInternalServices, ShowNodes = showNodes,
            };
            var view = graph.View(filter);
            var o = new JsonObject
            {
                ["complete"] = graph.IsComplete, ["warning"] = graph.IncompleteWarning,
                ["limits"] = "Exposure is judged from cluster objects only: DNS, firewalls, WAF, service mesh and cloud load balancer rules are not visible.",
                ["chains"] = view.Chains, ["outsideChains"] = view.OutsideChains, ["problemChains"] = view.ProblemChains, ["truncated"] = view.Truncated,
                ["nodes"] = new JsonArray(view.Nodes.Take(300).Select(p => (JsonNode?)new JsonObject
                {
                    ["id"] = p.Id, ["kind"] = p.Node.KindText, ["name"] = p.Node.Label, ["namespace"] = p.Node.Namespace, ["context"] = p.IsContext,
                    ["exposure"] = p.Node.Exposure switch { Core.Network.ExposureLevel.Outside => "outside the cluster", Core.Network.ExposureLevel.Unknown => "unknown", _ => "in-cluster only" },
                    ["detail"] = p.Node.Detail, ["missing"] = p.Node.Missing,
                    ["matches"] = new JsonArray(p.Node.FilterLines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
                    ["lines"] = new JsonArray(p.Node.Lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
                    ["problems"] = new JsonArray(p.Node.Problems.Select(x => (JsonNode?)new JsonObject { ["code"] = x.Code, ["severity"] = x.Severity.ToString(), ["description"] = x.Description }).ToArray()),
                }).ToArray()),
                ["routes"] = new JsonArray(view.Routes.Take(300).Select(r => (JsonNode?)new JsonObject
                {
                    ["url"] = r.Url, ["ingress"] = r.Ingress, ["class"] = r.Route.Class, ["exposure"] = r.ExposureText, ["comesInThrough"] = r.Via,
                    ["backend"] = r.Backend, ["pods"] = r.Target, ["problems"] = r.Problems,
                }).ToArray()),
                ["edges"] = new JsonArray(view.Edges.Select(e => (JsonNode?)new JsonObject { ["from"] = e.From, ["to"] = e.To, ["hop"] = e.Label, ["entryPorts"] = new JsonArray(e.Ports.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["problem"] = e.Problem }).ToArray()),
            };
            return Json(o);
        });

    [McpServerTool(Name = "rightsizing_report"), Description("Requests/limits vs real usage per workload container, with verdicts, waste and recommended resources (and a YAML snippet). Uses Prometheus (cluster setting or in-cluster), else VPA, else local sampling.")]
    public Task<string> RightsizingReport(string cluster, string? @namespace = null, [Description("Window in days (1-30)")] int days = 7, [Description("Maximum workloads returned")] int limit = 40, CancellationToken ct = default) =>
        ctx.Run(async () =>
        {
            var s = await ctx.SessionAsync(cluster, ct);
            days = Math.Clamp(days, 1, 30);
            var t = s.Transport;
            IUsageHistorySource? source = null;
            var meta = s.Metadata;
            if (!string.IsNullOrWhiteSpace(meta.PrometheusUrl) && Uri.TryCreate(meta.PrometheusUrl, UriKind.Absolute, out var uri))
            {
                var azure = uri.Host.EndsWith("prometheus.monitor.azure.com", StringComparison.OrdinalIgnoreCase);
                source = new PrometheusUsageSource(new HttpPromClient(uri, azure ? ctx.Usage?.Tokens : null, azure ? "https://prometheus.monitor.azure.com/.default" : null), "Prometheus");
            }
            source ??= await PrometheusDiscovery.FindAsync(t, ct);
            source ??= ctx.Usage is not null && meta.LocalSampling ? new LocalSamplerSource(s.Entry.Id, ctx.Usage.Store) : new VpaRecommendationSource(t);
            var ns = @namespace is null ? null : new List<string> { @namespace };
            var history = await source.QueryAsync(UsageQuery.Default(days, ns), ct);

            async Task<List<KubeObject>> All(Gvr g)
            {
                var items = new List<KubeObject>(); string? cont = null;
                do { var l = await t.ListAsync(g, @namespace, new ListOptions { Limit = 500, Continue = cont }, ct); items.AddRange(l.Items.Select(i => new KubeObject(i))); cont = l.Continue; } while (!string.IsNullOrEmpty(cont));
                return items;
            }
            var pods = await All(Gvr.Pods);
            var rs = await All(Gvr.ReplicaSets); var dep = await All(Gvr.Deployments); var sts = await All(Gvr.StatefulSets); var ds = await All(Gvr.DaemonSets); var jobs = await All(Gvr.Jobs);
            KubeObject? Lookup(string kind, string n, string name) => (kind switch { "ReplicaSet" => rs, "Deployment" => dep, "StatefulSet" => sts, "DaemonSet" => ds, "Job" => jobs, _ => [] }).FirstOrDefault(o => o.Name == name && o.Namespace == n);
            var st = s.Metadata;
            var engine = new RightSizingEngine(new KrrStrategy(), new RightSizingSettings { PricePerVcpuHour = st.PricePerVcpuHour, PricePerGibHour = st.PricePerGibHour });
            var report = engine.Analyze(pods, Lookup, history);
            var arr = new JsonArray();
            foreach (var w in report.Workloads.OrderByDescending(w => w.WasteCpuCores).Take(Math.Clamp(limit, 1, 200)))
            {
                var cs = new JsonArray();
                foreach (var c in w.Containers)
                    cs.Add((JsonNode?)new JsonObject
                    {
                        ["container"] = c.Container, ["verdict"] = c.Primary.ToString(), ["cpuVerdict"] = c.CpuVerdict.ToString(), ["memoryVerdict"] = c.MemoryVerdict.ToString(),
                        ["cpu"] = new JsonObject { ["request"] = c.CpuRequest is { } r ? Quantities.FormatCpu(r) : null, ["limit"] = c.CpuLimit is { } l ? Quantities.FormatCpu(l) : null, ["p95"] = Quantities.FormatCpu(c.CpuP95), ["max"] = Quantities.FormatCpu(c.CpuMax), ["throttledRatio"] = c.ThrottledRatio },
                        ["memory"] = new JsonObject { ["request"] = c.MemoryRequest is { } mr ? Quantities.FormatMemoryMi(mr) : null, ["limit"] = c.MemoryLimit is { } ml ? Quantities.FormatMemoryMi(ml) : null, ["p95"] = Quantities.FormatMemoryMi(c.MemoryP95), ["max"] = Quantities.FormatMemoryMi(c.MemoryMax), ["oomKills"] = c.OomKills },
                        ["recommended"] = c.Recommended is null ? null : new JsonObject
                        {
                            ["cpuRequest"] = Quantities.FormatCpu(c.Recommended.CpuRequest), ["memoryRequest"] = Quantities.FormatMemoryMi(c.Recommended.MemoryRequest),
                            ["memoryLimit"] = c.Recommended.MemoryLimit is { } rl ? Quantities.FormatMemoryMi(rl) : null, ["strategy"] = c.Recommended.Strategy,
                        },
                        ["gitOpsManager"] = c.GitOpsManager,
                    });
                arr.Add((JsonNode?)new JsonObject { ["namespace"] = w.Namespace, ["kind"] = w.Kind, ["name"] = w.Name, ["replicas"] = w.Replicas, ["containers"] = cs });
            }
            var top = report.Containers.Where(c => c.Recommended is not null).OrderByDescending(c => c.WasteCpuCores).Take(10).ToList();
            return Json(new JsonObject
            {
                ["source"] = report.Source, ["windowDays"] = days, ["coverage"] = report.Coverage, ["dataHours"] = report.DataHours, ["strategy"] = report.Strategy,
                ["totals"] = new JsonObject { ["cpuRequested"] = Quantities.FormatCpu(report.CpuRequested), ["cpuUsedP95"] = Quantities.FormatCpu(report.CpuUsed), ["wasteCpu"] = Quantities.FormatCpu(report.WasteCpu),
                    ["underProvisioned"] = report.UnderProvisioned, ["withoutRequests"] = report.WithoutRequests },
                ["workloads"] = arr, ["yamlSnippet"] = Snippets.Yaml(top),
            });
        });
}

/// <summary>Mutating tools. Only registered with --allow-writes, and always executed through ClusterActions.</summary>
[McpServerToolType]
public sealed class KubismWriteTools(McpContext ctx)
{
    private static string Result(string action, string target) => $"{action} {target}: done";

    [McpServerTool(Name = "apply"), Description("Server-side apply one YAML document (fieldManager=kubism). Production clusters need --allow-production.")]
    public Task<string> Apply(string cluster, string yaml, string? @namespace = null, CancellationToken ct = default) => ctx.Run(async () =>
    {
        var s = await ctx.SessionAsync(cluster, ct);
        ctx.RequireWrite(s, "apply", null);
        var r = await s.Actions.ApplyYamlAsync(yaml, @namespace, ct);
        return r is JsonObject o ? YamlJson.ToYaml(SecretMasker.WithoutManagedFields(SecretMasker.MaskedCopy(o))) : "applied";
    });

    [McpServerTool(Name = "delete"), Description("Delete an object. On production clusters confirmName must equal the object name.")]
    public Task<string> Delete(string cluster, string kind, string name, string? @namespace = null, string? confirmName = null, CancellationToken ct = default) => ctx.Run(async () =>
    {
        var s = await ctx.SessionAsync(cluster, ct);
        var confirmation = ctx.RequireWrite(s, "delete", confirmName);
        var k = ctx.ResolveKind(s, kind);
        await s.Actions.DeleteAsync(k.Gvr, k.Namespaced ? @namespace ?? "default" : null, name, ct, confirmation: confirmation);
        return Result("delete", $"{k.Kind} {name}");
    });

    [McpServerTool(Name = "scale"), Description("Scale a Deployment/StatefulSet/ReplicaSet. Scaling to zero on production needs confirmName.")]
    public Task<string> Scale(string cluster, string kind, string name, string @namespace, int replicas, string? confirmName = null, CancellationToken ct = default) => ctx.Run(async () =>
    {
        var s = await ctx.SessionAsync(cluster, ct);
        var confirmation = ctx.RequireWrite(s, "scale", confirmName);
        var k = ctx.ResolveKind(s, kind);
        await s.Actions.ScaleAsync(k.Gvr, @namespace, name, replicas, ct, confirmation);
        return Result("scale", $"{k.Kind} {name} to {replicas}");
    });

    [McpServerTool(Name = "rollout_restart"), Description("Rolling restart of a Deployment/StatefulSet/DaemonSet.")]
    public Task<string> RolloutRestart(string cluster, string kind, string name, string @namespace, CancellationToken ct = default) => ctx.Run(async () =>
    {
        var s = await ctx.SessionAsync(cluster, ct);
        ctx.RequireWrite(s, "rollout restart", null);
        var k = ctx.ResolveKind(s, kind);
        await s.Actions.RolloutRestartAsync(k.Gvr, @namespace, name, ct);
        return Result("rollout restart", $"{k.Kind} {name}");
    });
}

[McpServerResourceType]
public sealed class KubismResources(McpContext ctx)
{
    [McpServerResource(UriTemplate = "kubism://clusters", Name = "clusters", MimeType = "application/json"), Description("The clusters known to Kubism.")]
    public string Clusters() => new JsonArray(ctx.Catalog.Entries.Select(e => (JsonNode?)new JsonObject
    {
        ["id"] = e.Id, ["context"] = e.ContextName, ["server"] = e.Server, ["safetyMode"] = ctx.Settings.GetMetadata(e.Id).SafetyMode.ToString(),
    }).ToArray()).ToJsonString();

    [McpServerResource(UriTemplate = "kubism://clusters/{id}/kinds", Name = "kinds", MimeType = "application/json"), Description("Resource kinds served by one cluster.")]
    public Task<string> Kinds(string id, CancellationToken ct) => ctx.Run(async () =>
    {
        var s = await ctx.SessionAsync(Uri.UnescapeDataString(id), ct);
        return new JsonArray(s.Discovery.Kinds.Select(k => (JsonNode?)new JsonObject { ["kind"] = k.Kind, ["group"] = k.Group, ["resource"] = k.Resource, ["namespaced"] = k.Namespaced }).ToArray()).ToJsonString();
    });
}
