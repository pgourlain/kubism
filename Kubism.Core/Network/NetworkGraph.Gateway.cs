using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kubism.Core.Api;
using Kubism.Core.Security;

namespace Kubism.Core.Network;

// Gateway API as a second source of chains: a Gateway takes the Ingress column, its routes become the edges to the Services, and the exposure comes from
// the Service of the controller that implements the Gateway (or from the controller itself when it runs outside the cluster). See docs/adr/0005-network-graph.md.
public sealed partial class NetworkGraph
{
    private sealed partial class Builder
    {
        private sealed record Listener(string Name, int Port, string Protocol, string? Hostname, bool Tls, bool Passthrough, JsonObject Json)
        {
            public string Text => $"{Protocol.ToLowerInvariant()} :{Port}{(Hostname is null ? "" : " " + Hostname)}";
        }

        /// <summary>A route attached to a Gateway: the route, the listeners it lands on, the hostnames it answers to and what the controller said about it.</summary>
        private sealed record Attached(KubeObject Route, List<Listener> Listeners, List<string> Hosts, JsonObject? Status);

        private sealed record GatewayController(string Label, bool External, List<ServiceInfo> Services);

        private void AddGateways()
        {
            if (gateway is null) return;
            foreach (var gw in gateway.Gateways.OrderBy(i => i.Namespace, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)) AddGateway(gw);
        }

        private void AddGateway(KubeObject gw)
        {
            var ns = gw.Namespace ?? "";
            var id = $"g:{ns}/{gw.Name}";
            var className = gw.StringAt("spec", "gatewayClassName") ?? "";
            var cls = gateway!.Classes.FirstOrDefault(c => c.Name == className);
            var controllerName = cls?.StringAt("spec", "controllerName") ?? "";
            var listeners = (gw.At("spec", "listeners") as JsonArray ?? []).OfType<JsonObject>().Select(l =>
            {
                var protocol = KubeObject.Str(l, "protocol") ?? "";
                var tlsNode = l["tls"] as JsonObject;
                var passthrough = KubeObject.Str(tlsNode ?? new JsonObject(), "mode") == "Passthrough";
                return new Listener(KubeObject.Str(l, "name") ?? "", l["port"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : 0, protocol, KubeObject.Str(l, "hostname"),
                    protocol is "HTTPS" or "TLS" && !passthrough, passthrough, l);
            }).ToList();

            var attached = AttachedRoutes(gw, listeners);
            var allHosts = attached.SelectMany(a => a.Hosts).Distinct().ToList();
            var singleHost = allHosts.Count <= 1;

            var filters = listeners.Select(l => l.Text).ToList();
            foreach (var grp in attached.SelectMany(a => a.Hosts.Select(h => (Host: h, Route: a.Route))).GroupBy(x => x.Host))
                filters.Add($"{grp.Key}: {string.Join(", ", grp.Select(x => x.Route.Kind).Distinct())}");
            var detailHosts = allHosts.Where(h => h != "*").ToList();
            var detail = detailHosts.Count > 0 ? string.Join(", ", detailHosts.Take(3)) + (detailHosts.Count > 3 ? $" +{detailHosts.Count - 3}" : "")
                : $"{listeners.Count} listener{(listeners.Count == 1 ? "" : "s")}";
            Node(new NetworkNode(id, NetworkNodeKind.Ingress, gw.Name, ns, detail, ExposureLevel.InCluster, [], [], gw, Filters: filters) { Flavor = "Gateway" });
            Line(id, $"class: {(className.Length == 0 ? "(none)" : className)}{(controllerName.Length > 0 ? $" ({controllerName})" : "")}");
            foreach (var l in listeners) Line(id, $"listener {l.Name}: {l.Text}{(l.Passthrough ? " (TLS passthrough)" : "")}");

            // Class and the controller's own verdict.
            if (className.Length == 0) Problem(id, "no-gateway-class", "The Gateway names no GatewayClass: no controller will program it.", FindingSeverity.High);
            else if (cls is null && gateway.Classes.Count > 0) Problem(id, "gatewayclass-missing", $"GatewayClass {className} does not exist.", FindingSeverity.High);
            if (cls is not null && Cond(cls.At("status", "conditions"), "Accepted") is { Status: "False" } ca)
                Problem(id, "gatewayclass-not-accepted", $"GatewayClass {className} is not accepted by its controller{Why(ca)}.", FindingSeverity.High);
            foreach (var type in new[] { "Accepted", "Programmed" })
                if (Cond(gw.At("status", "conditions"), type) is { Status: "False" } c)
                    Problem(id, type == "Accepted" ? "gateway-not-accepted" : "gateway-not-programmed", $"The Gateway is not {type.ToLowerInvariant()}{Why(c)}.", FindingSeverity.High);
            foreach (var ls in (gw.At("status", "listeners") as JsonArray ?? []).OfType<JsonObject>())
                foreach (var c in (ls["conditions"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var type = KubeObject.Str(c, "type") ?? ""; var status = KubeObject.Str(c, "status") ?? "";
                    var bad = type is "Accepted" or "Programmed" or "ResolvedRefs" ? status == "False" : type == "Conflicted" && status == "True";
                    if (bad) Problem(id, "listener-not-ready", $"Listener {KubeObject.Str(ls, "name")}: {type} is {status}{Why((status, KubeObject.Str(c, "reason"), KubeObject.Str(c, "message")))}.", FindingSeverity.Medium);
                }
            var addresses = (gw.At("status", "addresses") as JsonArray ?? []).OfType<JsonObject>().Select(a => KubeObject.Str(a, "value")).Where(v => !string.IsNullOrEmpty(v)).ToList();
            if (addresses.Count > 0) Line(id, $"address: {string.Join(", ", addresses)}");

            // TLS certificates of the listeners that terminate it.
            foreach (var l in listeners.Where(l => l.Tls))
            {
                var refs = (l.Json["tls"]?["certificateRefs"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                if (refs.Count == 0) { Line(id, $"TLS on {l.Name}: no certificateRef"); Problem(id, "listener-no-certificate", $"Listener {l.Name} terminates TLS but names no certificate.", FindingSeverity.High); continue; }
                foreach (var r in refs)
                {
                    if ((KubeObject.Str(r, "kind") ?? "Secret") != "Secret") continue;
                    var secret = KubeObject.Str(r, "name") ?? ""; var sns = KubeObject.Str(r, "namespace") ?? ns;
                    var hostText = l.Hostname ?? "*";
                    if (sns != ns && !Granted("Gateway", ns, sns, "Secret", secret))
                    {
                        Problem(id, "reference-not-permitted", $"Listener {l.Name} uses the certificate {sns}/{secret}, but namespace {sns} has no ReferenceGrant allowing a Gateway of {ns} to read it.", FindingSeverity.High);
                        Line(id, $"TLS: {hostText} → secret {sns}/{secret} NOT PERMITTED (no ReferenceGrant)");
                        continue;
                    }
                    CheckTls(id, sns, secret, hostText);
                }
            }

            // Routes: one edge per backend Service, one row per host and match.
            if (attached.Count == 0 && listeners.Count > 0) { Line(id, "no route attached"); Problem(id, "no-routes", "No route is attached to this Gateway: it serves nothing.", FindingSeverity.Low); }
            foreach (var a in attached) AddRoute(id, gw, className, a, singleHost);

            Expose(id, gw, className, controllerName, addresses);
        }

        // ---------- routes ----------

        private List<Attached> AttachedRoutes(KubeObject gw, List<Listener> listeners)
        {
            var result = new List<Attached>();
            var ns = gw.Namespace ?? "";
            var id = $"g:{ns}/{gw.Name}";
            foreach (var route in gateway!.Routes.OrderBy(r => r.Namespace, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal))
            {
                var rns = route.Namespace ?? "";
                foreach (var pr in (route.At("spec", "parentRefs") as JsonArray ?? []).OfType<JsonObject>())
                {
                    if ((KubeObject.Str(pr, "kind") ?? "Gateway") != "Gateway" || (KubeObject.Str(pr, "group") ?? GatewayApi.Group) != GatewayApi.Group) continue;
                    if (KubeObject.Str(pr, "name") != gw.Name || (KubeObject.Str(pr, "namespace") ?? rns) != ns) continue;
                    var section = KubeObject.Str(pr, "sectionName");
                    int? port = pr["port"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : null;
                    var named = $"{route.Kind} {rns}/{route.Name}";
                    var landing = listeners.Where(l => (section is null || l.Name == section) && (port is null || l.Port == port) && Compatible(route.Kind, l.Protocol)).ToList();
                    if (landing.Count == 0)
                    {
                        Problem(id, "route-no-listener", $"{named} targets {(section is null ? "" : $"listener {section}")}{(port is null ? "" : $" port {port}")}{(section is null && port is null ? "this Gateway" : "")}, which has no compatible listener.", FindingSeverity.Medium);
                        continue;
                    }
                    var status = (route.At("status", "parents") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(sp =>
                        sp["parentRef"] is JsonObject pp && KubeObject.Str(pp, "name") == gw.Name && (KubeObject.Str(pp, "namespace") ?? rns) == ns);
                    var conditions = status?["conditions"];
                    if (status is not null)
                    {
                        if (Cond(conditions, "Accepted") is { Status: "False" } acc) Problem(id, "route-not-accepted", $"{named} is not accepted{Why(acc)}.", FindingSeverity.High);
                        if (Cond(conditions, "ResolvedRefs") is { Status: "False" } res) Problem(id, "route-refs-unresolved", $"{named} has references the controller could not resolve{Why(res)}.", FindingSeverity.High);
                    }
                    else if (!landing.Any(l => NamespaceAllowed(l, route, gw)))
                        Problem(id, "route-not-allowed", $"{named} is in another namespace than the Gateway, and the listener only accepts routes from its own namespace (allowedRoutes.namespaces.from: Same).", FindingSeverity.High);
                    else Line(id, $"{named}: the controller has not reported a status for this Gateway");

                    var hosts = RbacJson.Strings(route.At("spec", "hostnames")).ToList();
                    if (hosts.Count == 0) hosts = landing.Select(l => l.Hostname).Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).Distinct().ToList();
                    if (hosts.Count == 0) hosts = ["*"];
                    var existing = result.FirstOrDefault(r => ReferenceEquals(r.Route, route));
                    if (existing is null) result.Add(new Attached(route, landing, hosts, status));
                    else { foreach (var l in landing) if (!existing.Listeners.Contains(l)) existing.Listeners.Add(l); foreach (var h in hosts) if (!existing.Hosts.Contains(h)) existing.Hosts.Add(h); }
                }
            }
            return result;
        }

        private static bool Compatible(string routeKind, string protocol) => routeKind switch
        {
            "HTTPRoute" or "GRPCRoute" => protocol is "HTTP" or "HTTPS",
            "TLSRoute" => protocol == "TLS",
            "TCPRoute" => protocol == "TCP",
            "UDPRoute" => protocol == "UDP",
            _ => true,
        };

        /// <summary>Without a status to read, what the listener's allowedRoutes says. A namespace selector cannot be evaluated here (no namespace labels), so it is assumed to allow.</summary>
        private static bool NamespaceAllowed(Listener l, KubeObject route, KubeObject gw)
        {
            var from = l.Json["allowedRoutes"]?["namespaces"]?["from"]?.ToString() ?? "Same";
            return from != "Same" || (route.Namespace ?? "") == (gw.Namespace ?? "");
        }

        private void AddRoute(string gid, KubeObject gw, string className, Attached a, bool singleHost)
        {
            var route = a.Route;
            var rns = route.Namespace ?? "";
            var named = $"{route.Kind} {rns}/{route.Name}";
            var tlsCovered = a.Listeners.Any(l => l.Tls || l.Passthrough);
            var rules = (route.At("spec", "rules") as JsonArray ?? []).OfType<JsonObject>().ToList();
            foreach (var rule in rules)
            {
                var matches = Matches(route.Kind, rule, a.Listeners);
                var backends = (rule["backendRefs"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                if (backends.Count == 0) { Line(gid, $"{named}: a rule has no backend"); continue; }
                foreach (var b in backends)
                {
                    var bkind = KubeObject.Str(b, "kind") ?? "Service";
                    var name = KubeObject.Str(b, "name") ?? "";
                    if (bkind != "Service" || (KubeObject.Str(b, "group") ?? "") != "") { Line(gid, $"{named}: backend {bkind} {name} (not followed: only Services are)"); continue; }
                    var bns = KubeObject.Str(b, "namespace") ?? rns;
                    var sid = $"s:{bns}/{name}";
                    int? port = b["port"] is JsonValue pv && pv.TryGetValue<int>(out var pn) ? pn : null;
                    var portText = port?.ToString() ?? "?";
                    var weight = b["weight"] is JsonValue wv && wv.TryGetValue<int>(out var w) ? w : (int?)null;
                    var permitted = bns == rns || Granted(route.Kind, rns, bns, "Service", name);
                    var broken = false; var missing = false;
                    if (!permitted)
                    {
                        Problem(gid, "reference-not-permitted", $"{named} routes to Service {bns}/{name}, but namespace {bns} has no ReferenceGrant allowing {route.Kind} routes of {rns}.", FindingSeverity.High);
                        broken = true;
                    }
                    if (!_svc.TryGetValue(sid, out var svc))
                    {
                        Node(new NetworkNode(sid, NetworkNodeKind.Service, name, bns, "does not exist", ExposureLevel.InCluster, [], [], null, Missing: true));
                        Problem(sid, "missing-service", $"{named} routes to Service {name}, which does not exist.", FindingSeverity.High);
                        Problem(gid, "missing-service", $"Backend Service {bns}/{name} of {named} does not exist.", FindingSeverity.High);
                        broken = true; missing = true;
                    }
                    else if (port is not null && svc.Ports.All(p => p.Number != port))
                    {
                        Problem(gid, "missing-service-port", $"{named}: Service {name} has no port {port} (it has {PortsText(svc)}).", FindingSeverity.High);
                        broken = true;
                    }
                    var shownPort = svc is not null && svc.Ports.FirstOrDefault(p => p.Number == port) is { } sp ? sp.Number.ToString() : portText;

                    foreach (var host in a.Hosts)
                        foreach (var m in matches)
                        {
                            var where = singleHost || host == "*" ? m.Where : Prefix(host, m.Where);
                            var label = $"{(where.Length == 0 ? "/" : where)} → :{shownPort}{(weight is { } wt && wt != 1 ? $" (weight {wt})" : "")}";
                            Edge(gid, sid, label, broken, [shownPort]);
                            Line(gid, $"{named}: {host} {m.Where} → {name}:{shownPort}{(missing ? "  (Service MISSING)" : broken ? "  (see problems)" : "")}");
                            var url = m.Url?.Invoke(host, a.Listeners, tlsCovered);
                            _routeList.Add(new NetworkRoute(gid, gw.Namespace ?? "", gw.Name, className, host, m.Path, m.PathType, name, shownPort, sid, false, tlsCovered, missing)
                                { RouteName = named, UrlOverride = url });
                        }
                }
            }
        }

        private static string Prefix(string host, string where) => where.Length > 0 && "/=~".Contains(where[0]) ? host + where : host + " " + where;

        private sealed record RouteMatch(string Where, string Path, string PathType, Func<string, List<Listener>, bool, string>? Url);

        /// <summary>What a rule matches on: HTTP path (and method/headers noted), gRPC service and method, or just the listener port for TLS/TCP/UDP routes.</summary>
        private static List<RouteMatch> Matches(string kind, JsonObject rule, List<Listener> listeners)
        {
            var result = new List<RouteMatch>();
            var matches = (rule["matches"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            switch (kind)
            {
                case "HTTPRoute":
                    foreach (var m in matches.Count == 0 ? new List<JsonObject> { new() } : matches)
                    {
                        var type = KubeObject.Str(m["path"] as JsonObject ?? new JsonObject(), "type") ?? "PathPrefix";
                        var value = KubeObject.Str(m["path"] as JsonObject ?? new JsonObject(), "value") ?? "/";
                        var extra = (KubeObject.Str(m, "method") is { } method ? $" [{method}]" : "") + (m["headers"] is JsonArray { Count: > 0 } h ? $" [+{h.Count} header(s)]" : "");
                        var shown = type == "Exact" ? "=" + value : type == "RegularExpression" ? "~" + value : value;
                        result.Add(new RouteMatch(shown + extra, value, type, null));
                    }
                    break;
                case "GRPCRoute":
                    foreach (var m in matches.Count == 0 ? new List<JsonObject> { new() } : matches)
                    {
                        var method = m["method"] as JsonObject;
                        var svc = method is null ? null : KubeObject.Str(method, "service"); var meth = method is null ? null : KubeObject.Str(method, "method");
                        var path = svc is null ? "" : "/" + svc + (meth is null ? "" : "/" + meth);
                        result.Add(new RouteMatch(path.Length == 0 ? "gRPC (any call)" : "gRPC " + path, path, "grpc", (host, ls, tls) => $"grpc{(tls ? "s" : "")}://{host}{path}"));
                    }
                    break;
                default:
                    var port = listeners.FirstOrDefault()?.Port ?? 0;
                    var scheme = kind switch { "TLSRoute" => "tls", "UDPRoute" => "udp", _ => "tcp" };
                    result.Add(new RouteMatch($"{scheme} :{port}", "", scheme, (host, ls, tls) => $"{scheme}://{host}:{ls.FirstOrDefault()?.Port ?? port}"));
                    break;
            }
            return result;
        }

        // ---------- exposure ----------

        private void Expose(string id, KubeObject gw, string className, string controllerName, List<string?> addresses)
        {
            var ns = gw.Namespace ?? "";
            var ctl = ControllerOf(gw, controllerName);
            if (ctl.External)
            {
                AddExposure($"x:ext/{controllerName}", ctl.Label, null, "runs outside the cluster", ExposureLevel.Outside, id, [$"{ctl.Label} (outside the cluster)", $"class: {className}"], "");
                return;
            }
            if (ctl.Services.Count == 0)
            {
                var address = string.Join(", ", addresses);
                Problem(id, "controller-not-found", $"No controller Service found for Gateway {gw.Name} (class {className}): whether it is reachable from outside the cluster is unknown.", FindingSeverity.Low);
                AddExposure($"x:unknown/gateway/{ns}/{gw.Name}", $"Controller of Gateway {gw.Name}", null, address.Length > 0 ? $"address {address}, controller Service not found" : "controller Service not found",
                    ExposureLevel.Unknown, id, [$"class: {className}", "The controller's Service could not be found by the labels controllers set on the Services they create."], "");
                return;
            }
            foreach (var s in ctl.Services)
                if (!ControllerService(id, s)) Line(id, "The Gateway's Service is ClusterIP: this Gateway is NOT reachable from outside the cluster.");
        }

        private GatewayController ControllerOf(KubeObject gw, string controllerName)
        {
            var key = controllerName.ToLowerInvariant();
            if (key.Contains("alb.networking.azure.io")) return new("Azure Application Gateway for Containers", true, []);
            if (key.Contains("application-networking.k8s.aws")) return new("AWS VPC Lattice", true, []);
            if (key.Contains("networking.gke.io")) return new("Google Cloud load balancer (GKE Gateway)", true, []);
            var ns = gw.Namespace ?? "";
            string Label(ServiceInfo s, string k) => s.Obj.Labels.GetValueOrDefault(k) ?? "";
            // The Services controllers create for a Gateway carry its name: the standard label (Istio, NGINX Gateway Fabric…), Envoy Gateway's and Cilium's own.
            var candidates = _svc.Values.Where(s => !Regex.IsMatch(s.Obj.Name, "(admission|metrics|webhook)") && (
                (s.Ns == ns && Label(s, "gateway.networking.k8s.io/gateway-name") == gw.Name)
                || (Label(s, "gateway.envoyproxy.io/owning-gateway-name") == gw.Name && Label(s, "gateway.envoyproxy.io/owning-gateway-namespace") == ns)
                || (s.Ns == ns && Label(s, "io.cilium.gateway/owning-gateway") == gw.Name))).ToList();
            // One controller Service for every Gateway (Traefik, Kong, NGINX Gateway Fabric before 2.0).
            if (candidates.Count == 0)
            {
                var shared = key switch
                {
                    _ when key.Contains("traefik") => "traefik",
                    _ when key.Contains("konghq") => "kong",
                    _ when key.Contains("nginx") => "nginx-gateway-fabric",
                    _ => null,
                };
                if (shared is not null) candidates = _svc.Values.Where(s => Is(s, shared) && !Regex.IsMatch(s.Obj.Name, "(admission|metrics|webhook)")).ToList();
            }
            var exposed = candidates.Where(s => s.Exposed).ToList();
            return new(controllerName, false, exposed.Count > 0 ? exposed : candidates);
        }

        // ---------- helpers ----------

        /// <summary>Is there a ReferenceGrant in <paramref name="toNs"/> that lets a <paramref name="fromKind"/> of <paramref name="fromNs"/> reach <paramref name="toKind"/> <paramref name="name"/>?</summary>
        private bool Granted(string fromKind, string fromNs, string toNs, string toKind, string name) =>
            gateway!.Grants.Any(g => (g.Namespace ?? "") == toNs
                && (g.At("spec", "from") as JsonArray ?? []).OfType<JsonObject>().Any(f => (KubeObject.Str(f, "kind") ?? "") == fromKind && (KubeObject.Str(f, "namespace") ?? "") == fromNs
                    && (KubeObject.Str(f, "group") ?? "") == GatewayApi.Group)
                && (g.At("spec", "to") as JsonArray ?? []).OfType<JsonObject>().Any(t => (KubeObject.Str(t, "kind") ?? "") == toKind && (KubeObject.Str(t, "group") ?? "") == ""
                    && (string.IsNullOrEmpty(KubeObject.Str(t, "name")) || KubeObject.Str(t, "name") == name)));

        private static (string Status, string? Reason, string? Message)? Cond(JsonNode? conditions, string type)
        {
            foreach (var c in (conditions as JsonArray ?? []).OfType<JsonObject>())
                if (KubeObject.Str(c, "type") == type) return (KubeObject.Str(c, "status") ?? "", KubeObject.Str(c, "reason"), KubeObject.Str(c, "message"));
            return null;
        }

        private static string Why((string Status, string? Reason, string? Message)? c)
        {
            if (c is not { } v) return "";
            var text = string.Join(": ", new[] { v.Reason, v.Message }.Where(x => !string.IsNullOrEmpty(x)));
            return text.Length == 0 ? "" : $" ({text})";
        }
    }
}
