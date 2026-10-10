using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Testing;

namespace Kubism.Desktop;

/// <summary>--demo: an in-process fake API server and Prometheus with sample objects; no cluster needed.</summary>
public sealed class DemoEnvironment : IAsyncDisposable
{
    private DemoEnvironment(FakeApiServer server, FakePrometheus prom, string kubeconfigPath, FakeApiServer? drifted)
    {
        Server = server; Prometheus = prom; KubeconfigPath = kubeconfigPath; Drifted = drifted;
    }

    /// <summary>A third cluster ("demo-dev") that drifted from the other two: another image tag, fewer replicas, a missing deployment, extra config. Only with <c>withDrift</c>.</summary>
    public FakeApiServer? Drifted { get; }

    public FakeApiServer Server { get; }
    public FakePrometheus Prometheus { get; }
    public string KubeconfigPath { get; }
    public const double Mi = 1024 * 1024;

    public static async Task<DemoEnvironment> StartAsync(bool withDrift = false)
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
        SeedTimeline(server);
        SeedSecurity(server);
        SeedGitOps(server);
        server.NodeMetrics["node-1"] = ("1200m", "6Gi"); server.NodeMetrics["node-2"] = ("900m", "5Gi");
        var drifted = withDrift ? await StartDriftedAsync() : null;
        var devCluster = drifted is null ? "" : "- {name: demo-dev, cluster: {server: " + drifted.Url + "}}";
        var devContext = drifted is null ? "" : "- {name: demo-dev, context: {cluster: demo-dev, user: demo-user, namespace: default}}";
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
            {{devCluster}}
            users:
            - name: demo-user
              user:
                token: {{server.Token}}
            contexts:
            - name: demo-staging
              context: {cluster: demo, user: demo-user, namespace: default}
            - name: demo-production
              context: {cluster: demo, user: demo-user, namespace: production}
            {{devContext}}
            """.Replace("\r\n", "\n"));
        return new DemoEnvironment(server, prom, path, drifted);
    }

    /// <summary>A story for the Timeline tab: "web" rolled out two hours ago (the old revision is still around, scaled to zero), and crashy-0 was OOM-killed minutes ago.</summary>
    private static void SeedTimeline(FakeApiServer server)
    {
        var now = DateTimeOffset.UtcNow;
        string At(TimeSpan ago) => now.Subtract(ago).ToString("yyyy-MM-ddTHH:mm:ssZ");
        JsonObject Template(string image) => new() { ["spec"] = new JsonObject { ["containers"] = new JsonArray(new JsonObject { ["name"] = "main", ["image"] = image }) } };
        if (server.Get(Gvr.ReplicaSets, "default", "web-5d8f7c") is { } rs)
        {
            rs["metadata"]!["creationTimestamp"] = At(TimeSpan.FromHours(2));
            rs["metadata"]!["annotations"] = new JsonObject { ["deployment.kubernetes.io/revision"] = "2" };
            rs["spec"]!["template"] = Template("nginx:1.27");
            server.Upsert(rs, silent: true);
        }
        server.Upsert(new JsonObject
        {
            ["apiVersion"] = "apps/v1", ["kind"] = "ReplicaSet",
            ["metadata"] = new JsonObject
            {
                ["name"] = "web-7c4b8d", ["namespace"] = "default", ["uid"] = "rs-default-web-old", ["creationTimestamp"] = At(TimeSpan.FromDays(3)),
                ["labels"] = new JsonObject { ["app"] = "web" }, ["annotations"] = new JsonObject { ["deployment.kubernetes.io/revision"] = "1" },
                ["ownerReferences"] = new JsonArray(new JsonObject { ["apiVersion"] = "apps/v1", ["kind"] = "Deployment", ["name"] = "web", ["uid"] = "dep-default-web", ["controller"] = true }),
            },
            ["spec"] = new JsonObject { ["replicas"] = 0, ["template"] = Template("nginx:1.25") }, ["status"] = new JsonObject { ["replicas"] = 0 },
        }, silent: true);
        var minute = 0;
        foreach (var pod in server.List(Gvr.Pods, "default").Where(p => p["metadata"]!["name"]!.GetValue<string>().StartsWith("web-")))
        {
            pod["metadata"]!["creationTimestamp"] = At(TimeSpan.FromMinutes(119 - minute++));
            server.Upsert(pod, silent: true);
        }
        void Event(string name, string reason, string message, TimeSpan ago, string uid = "dep-default-web", string kind = "Deployment", string objName = "web") =>
            server.Upsert(new JsonObject
            {
                ["apiVersion"] = "v1", ["kind"] = "Event", ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = "default" },
                ["type"] = "Normal", ["reason"] = reason, ["message"] = message, ["lastTimestamp"] = At(ago),
                ["involvedObject"] = new JsonObject { ["kind"] = kind, ["name"] = objName, ["namespace"] = "default", ["uid"] = uid },
            }, silent: true);
        Event("web.scale-up", "ScalingReplicaSet", "Scaled up replica set web-5d8f7c to 3", TimeSpan.FromHours(2));
        Event("web.scale-down", "ScalingReplicaSet", "Scaled down replica set web-7c4b8d to 0", TimeSpan.FromMinutes(117));
        if (server.Get(Gvr.Pods, "default", "crashy-0") is { } crashy)
        {
            crashy["status"]!["containerStatuses"]![0]!["lastState"]!["terminated"]!["finishedAt"] = At(TimeSpan.FromMinutes(9));
            server.Upsert(crashy, silent: true);
        }
    }

    /// <summary>Something for the Security page: a sloppy worker, a hardened checkout, risky RBAC, and certificates that expire soon, expired and fine.</summary>
    private static void SeedSecurity(FakeApiServer server)
    {
        JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;
        void EditSpec(string ns, string name, Action<JsonObject> edit)
        {
            if (server.Get(Gvr.Deployments, ns, name) is not { } d) return;
            edit((JsonObject)d["spec"]!["template"]!["spec"]!);
            server.Upsert(d, silent: true);
        }
        EditSpec("staging", "worker", spec =>
        {
            spec["hostNetwork"] = true;
            spec["volumes"] = new JsonArray(J("{\"name\":\"docker\",\"hostPath\":{\"path\":\"/var/run/docker.sock\"}}"));
            var c = (JsonObject)spec["containers"]![0]!;
            c["image"] = "registry.example.com/worker";
            c["securityContext"] = J("{\"privileged\":true,\"runAsUser\":0,\"capabilities\":{\"add\":[\"SYS_ADMIN\"]}}");
        });
        EditSpec("production", "checkout", spec =>
        {
            spec["serviceAccountName"] = "checkout";
            var c = (JsonObject)spec["containers"]![0]!;
            c["image"] = "registry.example.com/checkout:2.8.1";
            c["securityContext"] = J("{\"runAsNonRoot\":true,\"allowPrivilegeEscalation\":false,\"capabilities\":{\"drop\":[\"ALL\"]}}");
        });

        void Upsert(string json) => server.Upsert(J(json), silent: true);
        Upsert("{\"apiVersion\":\"v1\",\"kind\":\"ServiceAccount\",\"metadata\":{\"name\":\"ci-bot\",\"namespace\":\"default\"}}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRole\",\"metadata\":{\"name\":\"cluster-admin\"},\"rules\":[{\"apiGroups\":[\"*\"],\"resources\":[\"*\"],\"verbs\":[\"*\"]}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRole\",\"metadata\":{\"name\":\"view\"},\"rules\":[{\"apiGroups\":[\"\"],\"resources\":[\"pods\",\"services\",\"configmaps\"],\"verbs\":[\"get\",\"list\",\"watch\"]}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRole\",\"metadata\":{\"name\":\"secret-reader\"},\"rules\":[{\"apiGroups\":[\"\"],\"resources\":[\"secrets\"],\"verbs\":[\"get\",\"list\"]}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRole\",\"metadata\":{\"name\":\"ops-all\"},\"rules\":[{\"apiGroups\":[\"apps\"],\"resources\":[\"deployments\",\"statefulsets\"],\"verbs\":[\"*\"]},{\"apiGroups\":[\"\"],\"resources\":[\"pods/exec\"],\"verbs\":[\"create\"]}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"Role\",\"metadata\":{\"name\":\"deployer\",\"namespace\":\"default\"},\"rules\":[{\"apiGroups\":[\"apps\"],\"resources\":[\"deployments\"],\"verbs\":[\"get\",\"list\",\"patch\"]},{\"apiGroups\":[\"\"],\"resources\":[\"pods\",\"pods/log\"],\"verbs\":[\"get\",\"list\"]}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"RoleBinding\",\"metadata\":{\"name\":\"ci-deploys\",\"namespace\":\"default\"},\"roleRef\":{\"kind\":\"Role\",\"name\":\"deployer\"},\"subjects\":[{\"kind\":\"ServiceAccount\",\"name\":\"ci-bot\",\"namespace\":\"default\"}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRoleBinding\",\"metadata\":{\"name\":\"ops-admin\"},\"roleRef\":{\"kind\":\"ClusterRole\",\"name\":\"cluster-admin\"},\"subjects\":[{\"kind\":\"User\",\"name\":\"bob@example.com\"}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRoleBinding\",\"metadata\":{\"name\":\"devs-read-secrets\"},\"roleRef\":{\"kind\":\"ClusterRole\",\"name\":\"secret-reader\"},\"subjects\":[{\"kind\":\"Group\",\"name\":\"developers\"}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRoleBinding\",\"metadata\":{\"name\":\"ops-everything\"},\"roleRef\":{\"kind\":\"ClusterRole\",\"name\":\"ops-all\"},\"subjects\":[{\"kind\":\"Group\",\"name\":\"sre\"}]}");
        Upsert("{\"apiVersion\":\"rbac.authorization.k8s.io/v1\",\"kind\":\"ClusterRoleBinding\",\"metadata\":{\"name\":\"everyone-views\"},\"roleRef\":{\"kind\":\"ClusterRole\",\"name\":\"view\"},\"subjects\":[{\"kind\":\"Group\",\"name\":\"system:authenticated\"}]}");

        // Certificates (real, self-signed) in TLS secrets; cert-manager reports its own ones from their status.
        string Tls(string cn, int daysLeft)
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={cn}", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder(); san.AddDnsName(cn);
            req.CertificateExtensions.Add(san.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(daysLeft - 90), DateTimeOffset.UtcNow.AddDays(daysLeft));
            return Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(cert.ExportCertificatePem()));
        }
        void TlsSecret(string ns, string name, string cn, int days) => server.Upsert(new JsonObject
        {
            ["apiVersion"] = "v1", ["kind"] = "Secret", ["type"] = "kubernetes.io/tls", ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = ns },
            ["data"] = new JsonObject { ["tls.crt"] = Tls(cn, days), ["tls.key"] = Convert.ToBase64String("demo-not-a-real-key"u8.ToArray()) },
        }, silent: true);
        TlsSecret("production", "shop-tls", "shop.example.com", 210);
        TlsSecret("default", "legacy-tls", "legacy.example.com", 12);
        TlsSecret("default", "web-tls-secret", "web.example.com", 62);
        void CertStatus(string name, int days, string? notReady = null)
        {
            if (server.Get(new Gvr("cert-manager.io", "v1", "certificates"), "default", name) is not { } c) return;
            var status = (JsonObject)c["status"]!;
            status["notAfter"] = DateTimeOffset.UtcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ");
            if (notReady is not null) status["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = "False", ["message"] = notReady });
            ((JsonObject)c["spec"]!)["dnsNames"] = new JsonArray($"{name.Replace("-tls", "")}.example.com");
            server.Upsert(c, silent: true);
        }
        CertStatus("web-tls", 62);
        CertStatus("api-tls", -2, "Issuing certificate as Secret does not exist: ACME challenge failed");
    }

    /// <summary>Argo CD applications (healthy, out of sync and degraded, progressing) and Flux resources (ready, not ready, suspended) for the GitOps page.</summary>
    private static void SeedGitOps(FakeApiServer server)
    {
        JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;
        var now = DateTimeOffset.UtcNow;
        string At(TimeSpan ago) => now.Subtract(ago).ToString("yyyy-MM-ddTHH:mm:ssZ");
        foreach (var ns in new[] { "argocd", "flux-system" }) server.Upsert(J("""{"apiVersion":"v1","kind":"Namespace","metadata":{"name":"NS"},"status":{"phase":"Active"}}""".Replace("NS", ns)), silent: true);
        const string Rev = "9f3c2a1b7d4e5f60718293a4b5c6d7e8f9012345";
        void Argo(string name, string sync, string health, string repo, string path, string resources, string? finished = null, string? error = null) =>
            server.Upsert(J("""{"apiVersion":"argoproj.io/v1alpha1","kind":"Application","metadata":{"name":"NAME","namespace":"argocd"},"spec":{"project":"default","source":{"repoURL":"REPO","path":"PATH","targetRevision":"main"},"destination":{"server":"https://kubernetes.default.svc","namespace":"NAME"}},"status":{"sync":{"status":"SYNC","revision":"REV"},"health":{"status":"HEALTH"},"operationState":{"finishedAt":"FINISHED","phase":"Succeeded"},"conditions":CONDITIONS,"resources":RESOURCES}}"""
                .Replace("NAME", name).Replace("REPO", repo).Replace("PATH", path).Replace("SYNC", sync).Replace("REV", Rev).Replace("HEALTH", health)
                .Replace("FINISHED", finished ?? At(TimeSpan.FromHours(3))).Replace("RESOURCES", resources)
                .Replace("CONDITIONS", error is null ? "[]" : "[{\"type\":\"ComparisonError\",\"message\":\"" + error + "\"}]")), silent: true);
        Argo("shop", "Synced", "Healthy", "https://github.com/example/shop.git", "deploy/prod",
            """[{"kind":"Deployment","namespace":"shop","name":"web","status":"Synced","health":{"status":"Healthy"}},{"kind":"Service","namespace":"shop","name":"web","status":"Synced","health":{"status":"Healthy"}},{"kind":"Ingress","namespace":"shop","name":"web","status":"Synced","health":{"status":"Healthy"}}]""");
        Argo("payments", "OutOfSync", "Degraded", "https://github.com/example/payments.git", "deploy/prod",
            """[{"kind":"Deployment","namespace":"payments","name":"api","status":"OutOfSync","health":{"status":"Degraded","message":"Deployment has 0/2 ready replicas"}},{"kind":"ConfigMap","namespace":"payments","name":"api-config","status":"OutOfSync","health":{"status":"Healthy"}},{"kind":"Service","namespace":"payments","name":"api","status":"Synced","health":{"status":"Healthy"}}]""", At(TimeSpan.FromMinutes(26)));
        Argo("reporting", "Synced", "Progressing", "https://github.com/example/reporting.git", "chart",
            """[{"kind":"StatefulSet","namespace":"reporting","name":"worker","status":"Synced","health":{"status":"Progressing","message":"1/3 replicas ready"}}]""", At(TimeSpan.FromMinutes(4)));
        Argo("legacy", "OutOfSync", "Healthy", "https://github.com/example/legacy.git", "k8s",
            """[{"kind":"Deployment","namespace":"legacy","name":"importer","status":"OutOfSync","health":{"status":"Healthy"}}]""", At(TimeSpan.FromDays(9)));
        Argo("broken-repo", "Unknown", "Unknown", "https://github.com/example/missing.git", "deploy", "[]", At(TimeSpan.FromDays(1)), "rpc error: code = Unknown desc = repository not found");

        void Flux(string json) => server.Upsert(J(json.Replace("REV8", Rev[..8]).Replace("REV", Rev)
            .Replace("AGO5H", At(TimeSpan.FromHours(5))).Replace("AGO40M", At(TimeSpan.FromMinutes(40))).Replace("AGO2D", At(TimeSpan.FromDays(2))).Replace("AGO6D", At(TimeSpan.FromDays(6)))), silent: true);
        Flux("""{"apiVersion":"source.toolkit.fluxcd.io/v1","kind":"GitRepository","metadata":{"name":"flux-system","namespace":"flux-system"},"spec":{"url":"ssh://git@github.com/example/fleet"},"status":{"artifact":{"revision":"main@sha1:REV"},"conditions":[{"type":"Ready","status":"True","message":"stored artifact for revision 'main@sha1:REV8'","lastTransitionTime":"AGO5H"}]}}""");
        Flux("""{"apiVersion":"kustomize.toolkit.fluxcd.io/v1","kind":"Kustomization","metadata":{"name":"infra","namespace":"flux-system"},"spec":{"sourceRef":{"kind":"GitRepository","name":"flux-system"},"path":"./infrastructure"},"status":{"lastAppliedRevision":"main@sha1:REV","conditions":[{"type":"Ready","status":"True","message":"Applied revision: main@sha1:REV8","lastTransitionTime":"AGO5H"}],"inventory":{"entries":[{"id":"_ingress-nginx__Namespace"},{"id":"ingress-nginx_ingress-nginx-controller_apps_Deployment"}]}}}""");
        Flux("""{"apiVersion":"kustomize.toolkit.fluxcd.io/v1","kind":"Kustomization","metadata":{"name":"apps","namespace":"flux-system"},"spec":{"sourceRef":{"kind":"GitRepository","name":"flux-system"},"path":"./apps/production"},"status":{"lastAppliedRevision":"main@sha1:REV","conditions":[{"type":"Ready","status":"False","message":"health check failed after 5m0s: timeout waiting for: [Deployment/production/checkout status: 'InProgress']","lastTransitionTime":"AGO40M"}],"inventory":{"entries":[{"id":"production_checkout_apps_Deployment"},{"id":"production_checkout__Service"},{"id":"production_checkout-config__ConfigMap"}]}}}""");
        Flux("""{"apiVersion":"helm.toolkit.fluxcd.io/v2","kind":"HelmRelease","metadata":{"name":"ingress-nginx","namespace":"flux-system"},"spec":{"chart":{"spec":{"chart":"ingress-nginx","version":"4.11.x","sourceRef":{"name":"ingress-nginx"}}}},"status":{"lastAppliedRevision":"4.11.2","conditions":[{"type":"Ready","status":"True","message":"Helm upgrade succeeded for release ingress-nginx/ingress-nginx.v3 with chart ingress-nginx@4.11.2","lastTransitionTime":"AGO2D"}]}}""");
        Flux("""{"apiVersion":"helm.toolkit.fluxcd.io/v2","kind":"HelmRelease","metadata":{"name":"podinfo","namespace":"flux-system"},"spec":{"suspend":true,"chart":{"spec":{"chart":"podinfo","version":"6.x","sourceRef":{"name":"podinfo"}}}},"status":{"conditions":[{"type":"Ready","status":"True","lastTransitionTime":"AGO6D"}]}}""");
    }

    private static async Task<FakeApiServer> StartDriftedAsync()
    {
        var dev = await FakeApiServer.StartAsync();
        void Edit(Gvr gvr, string ns, string name, Action<JsonObject> change)
        {
            if (dev.Get(gvr, ns, name) is { } o) { change(o); dev.Upsert(o, silent: true); }
        }
        // web: older image, fewer replicas, no resource requests.
        Edit(Gvr.Deployments, "default", "web", o =>
        {
            o["spec"]!["replicas"] = 2;
            var c = o["spec"]!["template"]!["spec"]!["containers"]![0]!;
            c["image"] = "nginx:1.25";
            c["resources"] = new JsonObject { ["requests"] = new JsonObject { ["cpu"] = "50m", ["memory"] = "64Mi" } };
        });
        dev.Remove(Gvr.Deployments, "default", "api");
        Edit(Gvr.ConfigMaps, "default", "app-config", o => { var d = o["data"] as JsonObject ?? (JsonObject)(o["data"] = new JsonObject()); d["LOG_LEVEL"] = "debug"; d["FEATURE_X"] = "on"; });
        Edit(Gvr.Secrets, "default", "db-credentials", o => { if (o["data"] is JsonObject d && d.Count > 0) d[d.First().Key] = Convert.ToBase64String("another-value"u8.ToArray()); });
        dev.Upsert(JsonNode.Parse("""{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"legacy-importer","namespace":"default","uid":"dep-dev-legacy","labels":{"app":"legacy-importer"}},"spec":{"replicas":1,"selector":{"matchLabels":{"app":"legacy-importer"}},"template":{"metadata":{"labels":{"app":"legacy-importer"}},"spec":{"containers":[{"name":"main","image":"registry.example.com/importer:0.9"}]}}},"status":{"replicas":1,"readyReplicas":1,"availableReplicas":1,"updatedReplicas":1}}""")!.AsObject(), silent: true);
        return dev;
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
        // A healthy release that rendered the "api" deployment, its service and its config (Helm stamps these annotations on everything it installs),
        // plus the failed "payments" release owning the crash-looping pod: the graph shows both under their release.
        Rev("api", "default", 1, "deployed", "Install complete", now.AddDays(-12), "api", "1.4.2", "3.1.0", J("{\"replicaCount\":2}"), J("{}"), Manifest("Deployment", "api"));
        void Stamp(Gvr gvr, string ns, string name, string release)
        {
            if (server.Get(gvr, ns, name) is not { } o) return;
            var meta = (JsonObject)o["metadata"]!;
            var ann = meta["annotations"] as JsonObject ?? (JsonObject)(meta["annotations"] = new JsonObject());
            ann["meta.helm.sh/release-name"] = release; ann["meta.helm.sh/release-namespace"] = ns;
            var labels = meta["labels"] as JsonObject ?? (JsonObject)(meta["labels"] = new JsonObject());
            labels["app.kubernetes.io/managed-by"] = "Helm";
            server.Upsert(o);
        }
        Stamp(Gvr.Deployments, "default", "api", "api"); Stamp(Gvr.Services, "default", "api", "api"); Stamp(Gvr.ConfigMaps, "default", "app-config", "api");
        Stamp(Gvr.Pods, "default", "crashy-0", "payments");
        // Stuck upgrade (--wait timeout): pending for 20 minutes.
        Rev("reporting", "staging", 1, "pending-upgrade", "Preparing upgrade", now.AddMinutes(-20), "reporting", "1.2.0", "1.2.0", J("{\"replicaCount\":1}"), J("{\"replicaCount\":2}"), Manifest("Deployment", "stuck-pending"));
        var stuck = server.Get(Gvr.Pods, "staging", "stuck-pending");
        if (stuck is not null) { stuck["metadata"]!["labels"]!["app.kubernetes.io/instance"] = "reporting"; server.Upsert(stuck); }
    }

    public async ValueTask DisposeAsync()
    {
        await Prometheus.DisposeAsync();
        await Server.DisposeAsync();
        if (Drifted is not null) await Drifted.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(KubeconfigPath)!, true); } catch { }
    }
}
