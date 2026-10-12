using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kubism.Core.Api;
using Kubism.Core.Security;
using Kubism.Core.Util;

namespace Kubism.Core.Network;

/// <summary>
/// How traffic reaches the pods: what is exposed outside the cluster, through Ingresses and Services, to endpoints and workloads, with the
/// ports of every hop and what does not line up. Built from Ingresses, IngressClasses, Services, EndpointSlices and Pods; read-only, pure and
/// immutable (see <see cref="NetworkStore"/> for the live one). "Outside the cluster" is derived from the objects: a Service of type LoadBalancer or
/// NodePort (or with externalIPs), an Ingress whose controller's Service is such a Service, a controller that runs outside the cluster (Application
/// Gateway, ALB, GCE), a pod with hostPort or hostNetwork. See docs/adr/0005-network-graph.md for what is not visible (DNS, firewalls, WAF, mesh).
/// </summary>
public sealed partial class NetworkGraph
{
    private static readonly string[] InternalLbAnnotations =
    [
        "service.beta.kubernetes.io/azure-load-balancer-internal", "service.beta.kubernetes.io/aws-load-balancer-internal",
        "networking.gke.io/load-balancer-type", "cloud.google.com/load-balancer-type", "service.beta.kubernetes.io/aws-load-balancer-scheme",
    ];

    private readonly List<NetworkNode> _nodes = [];
    private readonly List<NetworkEdge> _edges = [];
    private readonly List<NetworkRoute> _routes = [];

    private NetworkGraph(IReadOnlyList<RbacKindStatus> availability) => Availability = availability;

    public IReadOnlyList<NetworkNode> Nodes => _nodes;
    public IReadOnlyList<NetworkEdge> Edges => _edges;
    public IReadOnlyList<NetworkRoute> Routes => _routes;
    public IReadOnlyList<RbacKindStatus> Availability { get; }
    public bool IsComplete => Availability.All(a => a.Loaded);
    public IReadOnlyList<string> MissingKinds => Availability.Where(a => !a.Loaded).Select(a => a.Kind).ToList();

    public string? IncompleteWarning => IsComplete ? null
        : $"Incomplete data: cannot read {string.Join(", ", MissingKinds)}. Chains may be missing or show wrong problems.{Availability.Where(a => !a.Loaded && a.Error is not null).Select(a => $" {a.Kind}: {a.Error}").FirstOrDefault()}";

    /// <summary>Hosts of every Ingress that has TLS, with the secrets they need: for the store to look them up.</summary>
    public static IReadOnlyList<(string Namespace, string Secret)> TlsSecretsOf(IEnumerable<KubeObject> ingresses) =>
        ingresses.SelectMany(i => (i.At("spec", "tls") as JsonArray ?? []).OfType<JsonObject>().Select(t => (Ns: i.Namespace ?? "", Name: KubeObject.Str(t, "secretName") ?? "")))
            .Where(t => t.Name.Length > 0).Distinct().Select(t => (t.Ns, t.Name)).ToList();

    // ---------- build ----------

    public static NetworkGraph Build(IEnumerable<KubeObject> ingresses, IEnumerable<KubeObject> ingressClasses, IEnumerable<KubeObject> services, IEnumerable<KubeObject> endpointSlices,
        IEnumerable<KubeObject> pods, IReadOnlyDictionary<(string Namespace, string Secret), TlsSecretStatus>? tls = null, IReadOnlyList<RbacKindStatus>? availability = null, DateTimeOffset? now = null,
        IEnumerable<KubeObject>? nodes = null)
    {
        var g = new NetworkGraph(availability ?? [new("ingresses", true, null), new("ingressclasses", true, null), new("services", true, null), new("endpointslices", true, null), new("pods", true, null)]);
        // Nodes are optional: a namespaced user often cannot list them. Null means "not readable" (the node boxes then say their status is unknown), not "no nodes".
        new Builder(g, ingresses.ToList(), ingressClasses.ToList(), services.ToList(), endpointSlices.ToList(), pods.ToList(), tls ?? new Dictionary<(string, string), TlsSecretStatus>(), now ?? DateTimeOffset.UtcNow, nodes?.ToList()).Run();
        return g;
    }

    private sealed record Port(string? Name, int Number, string Protocol, string TargetRaw, int? NodePort);
    private sealed record ContainerPort(string? Name, int Number, string Protocol, int? HostPort, string Container = "");
    private sealed record WorkloadRef(string Namespace, string Kind, string Name)
    {
        public string Id => $"w:{Namespace}/{Kind}/{Name}";
    }

    private sealed class ServiceInfo
    {
        public required KubeObject Obj { get; init; }
        public string Type => Obj.StringAt("spec", "type") ?? "ClusterIP";
        public string Ns => Obj.Namespace ?? "";
        public string Id => $"s:{Ns}/{Obj.Name}";
        public List<Port> Ports { get; } = [];
        public Dictionary<string, string> Selector { get; init; } = [];
        public List<KubeObject> Pods { get; } = [];
        public int Ready, NotReady, Slices;
        public bool IsController;
        public string[] ExternalIps { get; init; } = [];
        public string Address { get; set; } = "";
        public bool Exposed => Type is "LoadBalancer" or "NodePort" || ExternalIps.Length > 0;
    }

    private sealed class Builder(NetworkGraph g, List<KubeObject> ingresses, List<KubeObject> classes, List<KubeObject> services, List<KubeObject> slices, List<KubeObject> pods,
        IReadOnlyDictionary<(string Namespace, string Secret), TlsSecretStatus> tls, DateTimeOffset now, List<KubeObject>? nodeObjects)
    {
        private readonly Dictionary<string, ServiceInfo> _svc = [];
        private readonly Dictionary<string, NetworkNode> _nodes = [];
        private readonly Dictionary<(string, string), (List<string> labels, List<string> ports, bool problem)> _edges = [];
        private readonly Dictionary<string, List<NetworkProblem>> _problems = [];
        private readonly Dictionary<string, List<string>> _lines = [];
        private readonly HashSet<string> _workloadsBuilt = [];
        private readonly List<NetworkRoute> _routeList = [];
        // node name → the workloads with pods there (workload id, label, pods)
        private readonly Dictionary<string, List<(string Wid, string Label, List<KubeObject> Pods)>> _onNode = [];

        public void Run()
        {
            IndexServices();
            var controllers = Controllers();
            foreach (var ing in ingresses.OrderBy(i => i.Namespace, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)) AddIngress(ing, controllers);
            foreach (var s in _svc.Values.Where(s => !s.IsController).OrderBy(s => s.Ns, StringComparer.Ordinal).ThenBy(s => s.Obj.Name, StringComparer.Ordinal)) AddService(s);
            AddNodes();
            Propagate();
            foreach (var n in _nodes.Values) g._nodes.Add(n with { Problems = Problems(n.Id), Lines = Lines(n.Id) });
            foreach (var ((from, to), (labels, ports, problem)) in _edges) g._edges.Add(new NetworkEdge(from, to, Join(labels), problem) { Ports = ports });
            g._routes.AddRange(_routeList);
        }

        private List<NetworkProblem> Problems(string id) => _problems.TryGetValue(id, out var p) ? p.DistinctBy(x => x.Code + x.Description).ToList() : [];
        private List<string> Lines(string id) => _lines.TryGetValue(id, out var l) ? l : [];
        private void Problem(string id, string code, string text, FindingSeverity sev) { if (!_problems.TryGetValue(id, out var l)) _problems[id] = l = []; l.Add(new NetworkProblem(code, text, sev)); }
        private void Line(string id, string text) { if (!_lines.TryGetValue(id, out var l)) _lines[id] = l = []; if (!l.Contains(text)) l.Add(text); }

        private static string Join(List<string> labels) => labels.Count <= 3 ? string.Join("; ", labels) : string.Join("; ", labels.Take(3)) + $"; +{labels.Count - 3} more";

        private void Edge(string from, string to, string label, bool problem = false, IEnumerable<string>? ports = null)
        {
            if (!_edges.TryGetValue((from, to), out var e)) _edges[(from, to)] = e = ([], [], false);
            if (label.Length > 0 && !e.labels.Contains(label)) e.labels.Add(label);
            foreach (var p in ports ?? []) if (!e.ports.Contains(p)) e.ports.Add(p);
            _edges[(from, to)] = (e.labels, e.ports, e.problem || problem);
        }

        private void Node(NetworkNode n) { if (!_nodes.ContainsKey(n.Id)) _nodes[n.Id] = n; }

        // ---------- services ----------

        private void IndexServices()
        {
            var podsByNs = pods.GroupBy(p => p.Namespace ?? "").ToDictionary(x => x.Key, x => x.ToList());
            var slicesByService = slices.Where(s => s.Labels.ContainsKey("kubernetes.io/service-name")).GroupBy(s => (s.Namespace ?? "", s.Labels["kubernetes.io/service-name"])).ToDictionary(x => x.Key, x => x.ToList());
            foreach (var o in services)
            {
                var info = new ServiceInfo { Obj = o, Selector = KubeObject.StringMap(o.At("spec", "selector")).ToDictionary(k => k.Key, k => k.Value), ExternalIps = RbacJson.Strings(o.At("spec", "externalIPs")).ToArray() };
                foreach (var p in (o.At("spec", "ports") as JsonArray ?? []).OfType<JsonObject>())
                {
                    var number = p["port"]?.GetValue<int>() ?? 0;
                    var target = p["targetPort"] is JsonValue tv ? tv.ToString() : number.ToString();
                    info.Ports.Add(new Port(KubeObject.Str(p, "name"), number, KubeObject.Str(p, "protocol") ?? "TCP", target, p["nodePort"] is JsonValue np && np.TryGetValue<int>(out var n) ? n : null));
                }
                var lb = (o.At("status", "loadBalancer", "ingress") as JsonArray ?? []).OfType<JsonObject>().Select(i => KubeObject.Str(i, "ip") ?? KubeObject.Str(i, "hostname")).Where(x => x is not null).ToList();
                info.Address = string.Join(", ", lb);
                if (info.Selector.Count > 0 && podsByNs.TryGetValue(info.Ns, out var inNs))
                    info.Pods.AddRange(inNs.Where(p => info.Selector.All(kv => p.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value)));
                if (slicesByService.TryGetValue((info.Ns, o.Name), out var sl))
                {
                    info.Slices = sl.Count;
                    foreach (var ep in sl.SelectMany(s => (s.At("endpoints") as JsonArray ?? []).OfType<JsonObject>()))
                    {
                        var ready = ep["conditions"]?["ready"] is JsonValue rv && rv.TryGetValue<bool>(out var b) ? b : true;
                        if (ready) info.Ready++; else info.NotReady++;
                        // A Service without selector has its pods only through endpoint targets.
                        if (info.Selector.Count == 0 && ep["targetRef"] is JsonObject tr && KubeObject.Str(tr, "kind") == "Pod" && podsByNs.TryGetValue(info.Ns, out var cand)
                            && cand.FirstOrDefault(p => p.Name == KubeObject.Str(tr, "name")) is { } pod && !info.Pods.Contains(pod)) info.Pods.Add(pod);
                    }
                }
                _svc[info.Id] = info;
            }
        }

        // ---------- ingress controllers ----------

        private sealed record Controller(string Kind, string Label, bool External, List<ServiceInfo> Services);

        private Dictionary<string, Controller> Controllers()
        {
            var result = new Dictionary<string, Controller>();
            foreach (var c in classes) result[c.Name] = Describe(c.Name, c.StringAt("spec", "controller") ?? "");
            return result;
        }

        private Controller Describe(string className, string controller)
        {
            var key = (controller + " " + className).ToLowerInvariant();
            if (key.Contains("application-gateway") || key.Contains("agic")) return new("agic", "Azure Application Gateway (AGIC)", true, []);
            if (key.Contains("ingress.k8s.aws") || key.Contains("alb")) return new("alb", "AWS load balancer (ALB)", true, []);
            if (key.Contains("gce")) return new("gce", "Google Cloud load balancer", true, []);
            var (kind, selector) = key switch
            {
                _ when key.Contains("k8s.io/ingress-nginx") || key.Contains("ingress-nginx") => ("ingress-nginx", (Func<ServiceInfo, bool>)(s => Is(s, "ingress-nginx"))),
                _ when key.Contains("nginx.org") => ("nginx-ingress", s => Is(s, "nginx-ingress")),
                _ when key.Contains("traefik") => ("traefik", s => Is(s, "traefik")),
                _ when key.Contains("haproxy") => ("haproxy", s => Is(s, "haproxy-ingress") || Is(s, "kubernetes-ingress")),
                _ when key.Contains("contour") => ("contour", s => Is(s, "contour") || Is(s, "envoy")),
                _ when key.Contains("istio") => ("istio", s => s.Obj.Labels.TryGetValue("istio", out var v) && v == "ingressgateway"),
                _ => ("unknown", (Func<ServiceInfo, bool>)(_ => false)),
            };
            var candidates = _svc.Values.Where(s => selector(s) && !Regex.IsMatch(s.Obj.Name, "(admission|metrics|webhook)")).ToList();
            // Several controllers of one kind (public and internal): prefer the one whose Helm instance is named after the class, else the one whose name contains it.
            if (className.Length > 0 && candidates.Count > 1)
            {
                string[] instanceNames = [className, "ingress-" + className, className + "-ingress"];
                var exact = candidates.Where(s => instanceNames.Contains(s.Obj.Labels.GetValueOrDefault("app.kubernetes.io/instance"), StringComparer.OrdinalIgnoreCase)).ToList();
                var narrowed = candidates.Where(s => s.Obj.Name.Contains(className, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count > 0) candidates = exact;
                else if (narrowed.Count > 0 && narrowed.Count < candidates.Count) candidates = narrowed;
            }
            var exposed = candidates.Where(s => s.Exposed).ToList();
            return new(kind, kind, false, exposed.Count > 0 ? exposed : candidates);
        }

        private static bool Is(ServiceInfo s, string name) => s.Obj.Labels.TryGetValue("app.kubernetes.io/name", out var v) && v == name;

        // ---------- ingress ----------

        private void AddIngress(KubeObject ing, Dictionary<string, Controller> controllers)
        {
            var ns = ing.Namespace ?? "";
            var id = $"i:{ns}/{ing.Name}";
            var className = ing.StringAt("spec", "ingressClassName") ?? ing.Annotations.GetValueOrDefault("kubernetes.io/ingress.class");
            if (className is null)
            {
                var def = classes.FirstOrDefault(c => c.Annotations.GetValueOrDefault("ingressclass.kubernetes.io/is-default-class") == "true");
                className = def?.Name;
            }
            var hosts = new List<string>();
            var ruleLines = new List<string>();
            var ruleObjs = (ing.At("spec", "rules") as JsonArray ?? []).OfType<JsonObject>().ToList();
            // With one host, the host is on the node and the edges carry only the path; with several, the edges carry host + path.
            var singleHost = ruleObjs.Select(r => KubeObject.Str(r, "host") ?? "*").Distinct().Count() <= 1;
            var tlsEntries = (ing.At("spec", "tls") as JsonArray ?? []).OfType<JsonObject>().ToList();
            bool TlsCovers(string host) => tlsEntries.Any(t => RbacJson.Strings(t["hosts"]) is { Count: > 0 } th ? th.Contains(host) : true);
            foreach (var rule in ruleObjs)
            {
                var host = KubeObject.Str(rule, "host") ?? "*";
                if (!hosts.Contains(host)) hosts.Add(host);
                foreach (var path in (rule["http"]?["paths"] as JsonArray ?? []).OfType<JsonObject>())
                    Backend(ing, id, ns, host, KubeObject.Str(path, "path") ?? "/", KubeObject.Str(path, "pathType") ?? "", className, singleHost, TlsCovers(host), path["backend"]?["service"] as JsonObject, ruleLines);
            }
            if (ing.At("spec", "defaultBackend", "service") is JsonObject db) Backend(ing, id, ns, "*", "", "", className, singleHost, false, db, ruleLines, isDefault: true);

            // What the Ingress matches on the way in, one line per host: drawn inside its box.
            var filters = ruleObjs.GroupBy(r => KubeObject.Str(r, "host") ?? "*").Select(gr =>
                $"{gr.Key}: {string.Join(", ", gr.SelectMany(r => (r["http"]?["paths"] as JsonArray ?? []).OfType<JsonObject>().Select(pth => KubeObject.Str(pth, "path") ?? "/")).Distinct().DefaultIfEmpty("/"))}").ToList();
            if (ing.At("spec", "defaultBackend", "service") is not null) filters.Add("default backend");
            Node(new NetworkNode(id, NetworkNodeKind.Ingress, ing.Name, ns, hosts.Count == 0 ? "no host" : string.Join(", ", hosts.Take(3)) + (hosts.Count > 3 ? $" +{hosts.Count - 3}" : ""), ExposureLevel.InCluster, [], [], ing, Filters: filters));
            Line(id, $"class: {className ?? "(none)"}");
            foreach (var l in ruleLines) Line(id, l);

            // TLS
            foreach (var t in (ing.At("spec", "tls") as JsonArray ?? []).OfType<JsonObject>())
            {
                var secret = KubeObject.Str(t, "secretName") ?? "";
                var thosts = string.Join(", ", RbacJson.Strings(t["hosts"]));
                if (secret.Length == 0) { Line(id, $"TLS: {thosts} (controller default certificate)"); continue; }
                if (!tls.TryGetValue((ns, secret), out var st) || st.Exists is null) { Line(id, $"TLS: {thosts} → secret {secret} (not checked)"); continue; }
                if (st.Exists == false) { Line(id, $"TLS: {thosts} → secret {secret} MISSING"); Problem(id, "tls-secret-missing", $"TLS secret {secret} does not exist.", FindingSeverity.High); continue; }
                if (st.NotAfter is { } na)
                {
                    var days = (int)Math.Floor((na - now).TotalDays);
                    Line(id, $"TLS: {thosts} → secret {secret}, certificate {(days < 0 ? $"expired {-days}d ago" : $"expires in {days}d")}");
                    if (CertificateScan.HealthFor(days) is var h && h != CertificateHealth.Ok)
                        Problem(id, h == CertificateHealth.Expired ? "tls-expired" : "tls-expiring", days < 0 ? $"The certificate in {secret} expired {-days} day(s) ago." : $"The certificate in {secret} expires in {days} day(s).",
                            h == CertificateHealth.ExpiresSoon ? FindingSeverity.Medium : FindingSeverity.High);
                }
                else Line(id, $"TLS: {thosts} → secret {secret}");
            }

            // exposure: which controller carries this ingress
            if (className is null)
            {
                Problem(id, "no-ingress-class", "No ingress class: no controller will serve this Ingress (unless it is the only controller and ignores classes).", FindingSeverity.Medium);
                AddExposure($"x:none/{ns}/{ing.Name}", "No ingress class", null, "no controller selected", ExposureLevel.Unknown, id, [], "no ingress class");
                return;
            }
            if (!controllers.TryGetValue(className, out var ctl))
            {
                // Annotation-only classes (kubernetes.io/ingress.class) have no IngressClass object.
                ctl = Describe(className, "");
                controllers[className] = ctl;
            }
            if (ctl.External)
            {
                AddExposure($"x:ext/{ctl.Kind}", ctl.Label, null, "runs outside the cluster", ExposureLevel.Outside, id, [$"{ctl.Label} (outside the cluster)", $"class: {className}"], "");
                return;
            }
            if (ctl.Services.Count == 0)
            {
                var address = string.Join(", ", (ing.At("status", "loadBalancer", "ingress") as JsonArray ?? []).OfType<JsonObject>().Select(i => KubeObject.Str(i, "ip") ?? KubeObject.Str(i, "hostname")).Where(x => x is not null));
                Problem(id, "controller-not-found", $"No controller Service found for class {className}: whether this Ingress is reachable from outside the cluster is unknown.", FindingSeverity.Low);
                AddExposure($"x:unknown/{className}", $"Controller of class {className}", null, address.Length > 0 ? $"address {address}, controller Service not found" : "controller Service not found", ExposureLevel.Unknown, id,
                    [$"class: {className}", "The controller's Service could not be found by its usual labels."], "");
                return;
            }
            foreach (var s in ctl.Services)
            {
                s.IsController = true;
                var level = s.Exposed ? ExposureLevel.Outside : ExposureLevel.InCluster;
                var detail = s.Exposed ? $"{s.Type}{(s.Address.Length > 0 ? " " + s.Address : "")}" : "ClusterIP: in-cluster only";
                var note = InternalLbNote(s);
                var lines = new List<string> { $"Service {s.Ns}/{s.Obj.Name} ({s.Type})", $"ports: {PortsText(s)}", $"controller pods: {s.Pods.Count}" };
                if (s.Address.Length > 0) lines.Add($"address: {s.Address}");
                if (note is not null) lines.Add(note);
                AddExposure($"x:svc/{s.Ns}/{s.Obj.Name}", $"{s.Ns}/{s.Obj.Name}", s.Ns, detail + (note is null ? "" : " · private network"), level, id, lines, "", s.Obj, s.Ports.Select(p => p.Number.ToString()));
                if (!s.Exposed) Line(id, "The ingress controller's Service is ClusterIP: this Ingress is NOT reachable from outside the cluster.");
            }
        }

        private void AddExposure(string xid, string label, string? ns, string detail, ExposureLevel level, string toId, List<string> lines, string edgeLabel, KubeObject? source = null, IEnumerable<string>? ports = null)
        {
            Node(new NetworkNode(xid, NetworkNodeKind.Exposure, label, ns, detail, level, [], [], source));
            foreach (var l in lines) Line(xid, l);
            Edge(xid, toId, edgeLabel, false, ports);
        }

        private static string? InternalLbNote(ServiceInfo s)
        {
            foreach (var a in InternalLbAnnotations)
                if (s.Obj.Annotations.TryGetValue(a, out var v) && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("internal", StringComparison.OrdinalIgnoreCase) || v.Equals("Internal", StringComparison.Ordinal)))
                    return "internal cloud load balancer: outside the cluster, on the private network";
            return null;
        }

        private static string PortsText(ServiceInfo s) => string.Join(", ", s.Ports.Select(p => $"{p.Number}{(p.Protocol == "TCP" ? "" : "/" + p.Protocol)}{(p.NodePort is { } np ? $" (nodePort {np})" : "")}"));

        private void Backend(KubeObject ing, string ingId, string ns, string host, string path, string pathType, string? className, bool singleHost, bool tlsCovered, JsonObject? backend,
            List<string> lines, bool isDefault = false)
        {
            if (backend is null) return;
            var where = isDefault ? "default backend" : $"{(host == "*" ? "" : host)}{path}";
            var edgeWhere = isDefault ? "default backend" : singleHost ? (path.Length == 0 ? "/" : path) : where;
            var name = KubeObject.Str(backend, "name") ?? "";
            var portNode = backend["port"] as JsonObject;
            var portName = portNode is null ? null : KubeObject.Str(portNode, "name");
            var portNumber = portNode?["number"] is JsonValue pv && pv.TryGetValue<int>(out var n) ? n : (int?)null;
            var sid = $"s:{ns}/{name}";
            var portText = portName ?? portNumber?.ToString() ?? "?";
            void Route(string servicePort, bool missing) => _routeList.Add(new NetworkRoute(ingId, ns, ing.Name, className, host, path, pathType, name, servicePort, sid, isDefault, tlsCovered, missing));
            if (!_svc.TryGetValue(sid, out var svc))
            {
                Node(new NetworkNode(sid, NetworkNodeKind.Service, name, ns, "does not exist", ExposureLevel.InCluster, [], [], null, Missing: true));
                Problem(sid, "missing-service", $"Ingress {ing.Name} routes to Service {name}, which does not exist.", FindingSeverity.High);
                Problem(ingId, "missing-service", $"Backend Service {name} does not exist.", FindingSeverity.High);
                Edge(ingId, sid, $"{edgeWhere} → :{portText}", true, [portText]);
                Route(portText, true);
                lines.Add($"{where} → {name}:{portText}  (Service MISSING)");
                return;
            }
            var port = svc.Ports.FirstOrDefault(p => portName is not null ? p.Name == portName : p.Number == portNumber);
            if (port is null)
            {
                // The fault is the Ingress's: the Service is fine and may serve other Ingresses correctly, so only the Ingress and the hop are flagged.
                Problem(ingId, "missing-service-port", $"Service {name} has no port {portText} (it has {PortsText(svc)}).", FindingSeverity.High);
                Edge(ingId, sid, $"{edgeWhere} → :{portText}", true, [portText]);
                Route(portText, false);
                lines.Add($"{where} → {name}:{portText}  (port NOT on the Service)");
                return;
            }
            Edge(ingId, sid, $"{edgeWhere} → :{port.Number}", false, [port.Number.ToString()]);
            Route(port.Number.ToString(), false);
            lines.Add($"{where} → {name}:{port.Number}");
        }

        // ---------- services and workloads ----------

        private void AddService(ServiceInfo s)
        {
            var exposureDetail = s.Exposed ? $"{s.Type}{(s.Address.Length > 0 ? " " + s.Address : "")}" : s.Type == "ExternalName" ? $"ExternalName → {s.Obj.StringAt("spec", "externalName")}" : s.Type;
            Node(new NetworkNode(s.Id, NetworkNodeKind.Service, s.Obj.Name, s.Ns, $"{exposureDetail} · {PortsText(s)}", ExposureLevel.InCluster, [], [], s.Obj));
            Line(s.Id, $"type: {s.Type}");
            Line(s.Id, $"ports: {PortsText(s)}");
            Line(s.Id, $"endpoints: {s.Ready} ready, {s.NotReady} not ready" + (s.Selector.Count > 0 ? $" · selector {string.Join(",", s.Selector.Select(kv => kv.Key + "=" + kv.Value))}" : " · no selector"));
            if (s.Exposed)
            {
                var note = InternalLbNote(s);
                var xid = $"x:svc/{s.Ns}/{s.Obj.Name}";
                var lines = new List<string> { $"{s.Type}{(s.Address.Length > 0 ? " " + s.Address : "")}", $"ports: {PortsText(s)}" };
                if (s.ExternalIps.Length > 0) lines.Add($"externalIPs: {string.Join(", ", s.ExternalIps)}");
                if (note is not null) lines.Add(note);
                AddExposure(xid, $"{s.Type} {s.Ns}/{s.Obj.Name}", s.Ns, (s.Address.Length > 0 ? s.Address : s.Type) + (note is null ? "" : " · private network"), ExposureLevel.Outside, s.Id, lines, PortsText(s), s.Obj, s.Ports.Select(p => p.Number.ToString()));
                Problem(s.Id, "exposed-directly", $"Exposed outside the cluster directly ({s.Type}), not through an Ingress.", FindingSeverity.Low);
            }
            if (s.Type == "ExternalName") return;

            if (s.Ready == 0 && !(s.Selector.Count == 0 && s.Slices == 0))
                Problem(s.Id, "no-ready-endpoints", s.NotReady > 0 ? $"No ready endpoint ({s.NotReady} not ready)." : "No ready endpoint.", s.Exposed ? FindingSeverity.High : FindingSeverity.Medium);
            else if (s.Selector.Count == 0 && s.Slices == 0) { /* manual endpoints not synced yet: nothing to say */ }
            if (s.Selector.Count > 0 && s.Pods.Count == 0)
                Problem(s.Id, "selector-matches-no-pod", $"The selector {string.Join(",", s.Selector.Select(kv => kv.Key + "=" + kv.Value))} matches no pod.", FindingSeverity.Medium);

            foreach (var group in s.Pods.GroupBy(p => WorkloadOf(p)))
            {
                var wid = AddWorkload(group.Key, group.ToList());
                var labels = new List<string>(); var problem = false; var entry = new List<string>();
                foreach (var port in s.Ports)
                {
                    var (text, bad, container) = ResolvePort(port, group.ToList(), s, wid);
                    labels.Add(text); problem |= bad; entry.Add(container);
                }
                Edge(s.Id, wid, string.Join(", ", labels), problem, entry);
            }
        }

        /// <returns>The hop's text, whether it is broken, and the container port it ends on ("?" when a named port matches nothing).</returns>
        private (string Text, bool Problem, string Container) ResolvePort(Port port, List<KubeObject> pods, ServiceInfo s, string wid)
        {
            var declared = pods.SelectMany(ContainerPorts).ToList();
            var proto = port.Protocol == "TCP" ? "" : "/" + port.Protocol;
            if (int.TryParse(port.TargetRaw, out var number))
            {
                if (pods.All(p => ContainerPorts(p).Any()) && declared.All(c => c.Number != number))
                    Problem(wid, "port-not-declared", $"No container declares port {number} (targeted by Service {s.Obj.Name}); it may still listen on it.", FindingSeverity.Low);
                return ($"{port.Number}{proto} → {number}", false, number.ToString());
            }
            var named = declared.FirstOrDefault(c => c.Name == port.TargetRaw);
            if (named is null)
            {
                // The fault is the Service's (and the hop's); the workload may serve other Services fine, so it is not flagged.
                Problem(s.Id, "target-port-unresolved", $"targetPort '{port.TargetRaw}' matches no container port of the selected pods.", FindingSeverity.High);
                return ($"{port.Number}{proto} → {port.TargetRaw} (?)", true, "?");
            }
            return ($"{port.Number}{proto} → {port.TargetRaw} ({named.Number})", false, named.Number.ToString());
        }

        private static IEnumerable<ContainerPort> ContainerPorts(KubeObject pod) =>
            (pod.At("spec", "containers") as JsonArray ?? []).OfType<JsonObject>().SelectMany(c => (c["ports"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => new ContainerPort(KubeObject.Str(p, "name"), p["containerPort"]?.GetValue<int>() ?? 0, KubeObject.Str(p, "protocol") ?? "TCP", p["hostPort"] is JsonValue hv && hv.TryGetValue<int>(out var h) ? h : null, KubeObject.Str(c, "name") ?? "")));

        private static bool PodReady(KubeObject p) =>
            (p.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().Any(c => KubeObject.Str(c, "type") == "Ready" && KubeObject.Str(c, "status") == "True");

        private static WorkloadRef WorkloadOf(KubeObject pod)
        {
            var ns = pod.Namespace ?? "";
            var owner = pod.OwnerReferences.FirstOrDefault(o => o.Controller) ?? pod.OwnerReferences.FirstOrDefault();
            if (owner is null) return new WorkloadRef(ns, "Pod", pod.Name);
            if (owner.Kind == "ReplicaSet")
            {
                // Deployment replica sets are named <deployment>-<pod-template-hash>.
                var hash = pod.Labels.GetValueOrDefault("pod-template-hash");
                return hash is { Length: > 0 } && owner.Name.EndsWith("-" + hash, StringComparison.Ordinal)
                    ? new WorkloadRef(ns, "Deployment", owner.Name[..^(hash.Length + 1)]) : new WorkloadRef(ns, "ReplicaSet", owner.Name);
            }
            if (owner.Kind == "Job" && Regex.Match(owner.Name, @"^(.+)-\d{6,}$") is { Success: true } m) return new WorkloadRef(ns, "CronJob", m.Groups[1].Value);
            return new WorkloadRef(ns, owner.Kind, owner.Name);
        }

        private string AddWorkload(WorkloadRef w, List<KubeObject> workloadPods)
        {
            var id = w.Id;
            if (!_workloadsBuilt.Add(id)) return id;
            var ready = workloadPods.Count(PodReady);
            var ports = workloadPods.SelectMany(ContainerPorts).DistinctBy(p => (p.Name, p.Number, p.Protocol)).ToList();
            var hostNet = workloadPods.Any(p => p.At("spec", "hostNetwork") is JsonValue v && v.TryGetValue<bool>(out var b) && b);
            var hostPorts = ports.Where(p => p.HostPort is not null).ToList();
            Node(new NetworkNode(id, NetworkNodeKind.Workload, $"{w.Kind} {w.Name}", w.Namespace, $"{ready}/{workloadPods.Count} pods ready" + (ports.Count > 0 ? " · " + string.Join(", ", ports.Take(4).Select(p => p.Name is null ? p.Number.ToString() : $"{p.Name}:{p.Number}")) : ""),
                ExposureLevel.InCluster, [], [], null) { DeclaredPorts = ports.Select(p => p.Number.ToString()).Distinct().ToList(), PortOwners = ports.GroupBy(p => p.Number.ToString()).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(p => p.Container).Where(c => c.Length > 0).Distinct())) });
            foreach (var onNode in workloadPods.Where(p => (p.StringAt("spec", "nodeName") ?? "").Length > 0).GroupBy(p => p.StringAt("spec", "nodeName")!))
            {
                if (!_onNode.TryGetValue(onNode.Key, out var uses)) _onNode[onNode.Key] = uses = [];
                uses.Add((id, $"{w.Namespace}/{w.Kind} {w.Name}", onNode.ToList()));
            }
            Line(id, $"{ready} of {workloadPods.Count} pods ready");
            // Which nodes the pods run on: two pods on two nodes, or both on one, is not visible from the traffic path.
            foreach (var onNode in workloadPods.GroupBy(p => p.StringAt("spec", "nodeName") ?? "").OrderBy(x => x.Key.Length == 0 ? 1 : 0).ThenBy(x => x.Key, StringComparer.Ordinal))
                Line(id, $"node {(onNode.Key.Length == 0 ? "(not scheduled)" : onNode.Key)}: " + string.Join(", ", onNode.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + (PodReady(p) ? "" : " (not ready)"))));
            foreach (var p in ports) Line(id, $"container port {(p.Name is null ? "" : p.Name + " ")}{p.Number}/{p.Protocol}{(p.HostPort is { } hp ? $" (hostPort {hp})" : "")}");
            if (ready < workloadPods.Count) Problem(id, "pods-not-ready", $"{workloadPods.Count - ready} of {workloadPods.Count} pod(s) not ready.", ready == 0 ? FindingSeverity.High : FindingSeverity.Medium);
            if (hostNet || hostPorts.Count > 0)
            {
                var text = hostNet ? "hostNetwork: the pods use the node's network" : $"hostPort {string.Join(", ", hostPorts.Select(p => p.HostPort))}";
                AddExposure($"x:host/{w.Namespace}/{w.Kind}/{w.Name}", $"Node port of {w.Name}", w.Namespace, text, ExposureLevel.Outside, id, [text, "Reachable on the node's address, outside the cluster network."], "", null, hostPorts.Select(p => p.HostPort!.Value.ToString()));
                Problem(id, "host-network", text + ": reachable on the node's address.", FindingSeverity.Medium);
            }
            return id;
        }

        // ---------- nodes: where the pods run ----------

        private void AddNodes()
        {
            var byName = nodeObjects?.ToDictionary(n => n.Name, n => n);
            foreach (var (name, uses) in _onNode.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var nid = $"n:{name}";
                var obj = byName?.GetValueOrDefault(name);
                var total = uses.Sum(u => u.Pods.Count);
                string status; var missing = false;
                if (byName is null) status = "status unknown (nodes cannot be read)";
                else if (obj is null) { status = "not in the node list"; missing = true; }
                else
                {
                    var ready = (obj.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => KubeObject.Str(c, "type") == "Ready");
                    var state = KubeObject.Str(ready ?? new JsonObject(), "status");
                    status = state == "True" ? "Ready" : state == "False" ? "NotReady" : "Ready status unknown";
                    if (obj.At("spec", "unschedulable") is JsonValue u && u.TryGetValue<bool>(out var cordoned) && cordoned) status += " · cordoned";
                    if (state != "True") Problem(nid, "node-not-ready", $"Node {name} is not Ready: its pods may not receive traffic.", FindingSeverity.High);
                }
                if (missing) Problem(nid, "node-missing", $"Pods are scheduled on {name}, which is not in the node list.", FindingSeverity.Medium);
                Node(new NetworkNode(nid, NetworkNodeKind.Node, name, null, $"{status} · {total} pod{(total == 1 ? "" : "s")}", ExposureLevel.InCluster, [], [], obj, Missing: missing));
                Line(nid, $"status: {status}");
                foreach (var (wid, label, pods) in uses)
                {
                    Line(nid, $"{label}: {string.Join(", ", pods.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + (PodReady(p) ? "" : " (not ready)")))}");
                    var ready = pods.Count(PodReady);
                    var notReady = ready < pods.Count;
                    Edge(wid, nid, string.Join(", ", pods.Select(p => p.Name)), notReady, [notReady ? $"{ready}/{pods.Count} ready" : $"{pods.Count} pod{(pods.Count == 1 ? "" : "s")}"]);
                }
            }
        }

        // ---------- exposure level, propagated from the exposure nodes along edges ----------

        private void Propagate()
        {
            var level = _nodes.Values.ToDictionary(n => n.Id, n => n.Kind == NetworkNodeKind.Exposure ? n.Exposure : ExposureLevel.InCluster);
            var queue = new Queue<string>(_nodes.Values.Where(n => n.Kind == NetworkNodeKind.Exposure).Select(n => n.Id));
            var next = _edges.Keys.ToLookup(k => k.Item1, k => k.Item2);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                foreach (var to in next[id])
                    // Where a pod runs says nothing about how it is reached: nodes keep their own (in-cluster) level.
                    if (_nodes[to].Kind != NetworkNodeKind.Node && level[to] < level[id]) { level[to] = level[id]; queue.Enqueue(to); }
            }
            foreach (var n in _nodes.Values.ToList()) if (level[n.Id] != n.Exposure) _nodes[n.Id] = n with { Exposure = level[n.Id] };
        }
    }
}
