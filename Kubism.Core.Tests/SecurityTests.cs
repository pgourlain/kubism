using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Security;
using Xunit;

namespace Kubism.Core.Tests;

public class SecurityTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    // ---------- workload audit ----------

    private static KubeObject Deploy(string podSpec) =>
        O("""{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"app","namespace":"d"},"spec":{"template":{"spec":SPEC}}}""".Replace("SPEC", podSpec));

    [Fact]
    public void Audit_flags_privileged_host_access_capabilities_root_and_unpinned_images()
    {
        var f = WorkloadAudit.Check(Deploy("""{"hostNetwork":true,"volumes":[{"name":"s","hostPath":{"path":"/var/run/docker.sock"}},{"name":"l","hostPath":{"path":"/var/log"}}],"containers":[{"name":"c","image":"nginx","securityContext":{"privileged":true,"runAsUser":0,"capabilities":{"add":["NET_ADMIN","CHOWN"]}}}]}"""));
        Assert.Contains(f, x => x is { Rule: "hostnetwork", Severity: FindingSeverity.High });
        Assert.Contains(f, x => x is { Rule: "hostpath", Severity: FindingSeverity.High } && x.Title.Contains("docker.sock"));
        Assert.Contains(f, x => x is { Rule: "hostpath", Severity: FindingSeverity.Medium } && x.Title.Contains("/var/log"));
        Assert.Contains(f, x => x is { Rule: "privileged", Severity: FindingSeverity.High });
        Assert.Contains(f, x => x.Rule == "run-as-root");
        Assert.Contains(f, x => x.Rule == "capability" && x.Title.Contains("NET_ADMIN"));
        Assert.DoesNotContain(f, x => x.Title.Contains("CHOWN"));
        Assert.Contains(f, x => x.Rule == "latest-tag");
        Assert.Contains(f, x => x.Rule == "default-serviceaccount");
        Assert.All(f, x => Assert.Equal("Deployment/d/app", x.Subject));
    }

    [Fact]
    public void Audit_is_quiet_for_a_hardened_workload()
    {
        var f = WorkloadAudit.Check(Deploy("""{"serviceAccountName":"app","containers":[{"name":"c","image":"registry/app:1.4.2","securityContext":{"runAsNonRoot":true,"allowPrivilegeEscalation":false,"capabilities":{"drop":["ALL"]}}},{"name":"d","image":"x/y@sha256:abc","securityContext":{"runAsUser":1000,"allowPrivilegeEscalation":false}}]}"""));
        Assert.Empty(f);
    }

    [Theory]
    [InlineData("nginx", true)]
    [InlineData("nginx:latest", true)]
    [InlineData("registry:5000/team/app", true)]
    [InlineData("registry:5000/team/app:1.2", false)]
    [InlineData("app@sha256:deadbeef", false)]
    public void Audit_unpinned_image_detection(string image, bool flagged)
    {
        var f = WorkloadAudit.Check(Deploy("{\"serviceAccountName\":\"a\",\"containers\":[{\"name\":\"c\",\"image\":\"" + image + "\",\"securityContext\":{\"runAsNonRoot\":true,\"allowPrivilegeEscalation\":false}}]}"));
        Assert.Equal(flagged, f.Any(x => x.Rule == "latest-tag"));
    }

    [Fact]
    public void Audit_covers_cronjobs_and_pods_but_skips_pods_a_controller_owns()
    {
        var cron = O("""{"kind":"CronJob","metadata":{"name":"c","namespace":"d"},"spec":{"jobTemplate":{"spec":{"template":{"spec":{"containers":[{"name":"x","image":"a:1","securityContext":{"privileged":true}}]}}}}}}""");
        Assert.Contains(WorkloadAudit.Check(cron), x => x.Rule == "privileged");
        var owned = O("""{"kind":"Pod","metadata":{"name":"p","namespace":"d","ownerReferences":[{"kind":"ReplicaSet","name":"r","uid":"u","controller":true}]},"spec":{"containers":[{"name":"x","image":"a:1"}]}}""");
        var bare = O("""{"kind":"Pod","metadata":{"name":"p","namespace":"d"},"spec":{"containers":[{"name":"x","image":"a:1"}]}}""");
        Assert.False(WorkloadAudit.ShouldAudit(owned));
        Assert.True(WorkloadAudit.ShouldAudit(bare));
        Assert.False(WorkloadAudit.ShouldAudit(O("""{"kind":"ReplicaSet","metadata":{"name":"r"}}""")));
    }

    // ---------- RBAC ----------

    private static RbacAnalysis Rbac()
    {
        KubeObject Role(string kind, string name, string? ns, string rules) => O($$$"""{"kind":"{{{kind}}}","metadata":{"name":"{{{name}}}"{{{(ns is null ? "" : ",\"namespace\":\"" + ns + "\"")}}}},"rules":{{{rules}}} }""");
        KubeObject Bind(string kind, string name, string? ns, string role, string roleKind, string subjects) => O($$$"""{"kind":"{{{kind}}}","metadata":{"name":"{{{name}}}"{{{(ns is null ? "" : ",\"namespace\":\"" + ns + "\"")}}}},"roleRef":{"kind":"{{{roleKind}}}","name":"{{{role}}}"},"subjects":{{{subjects}}} }""");
        return new RbacAnalysis(
            [Role("Role", "pod-reader", "d", """[{"apiGroups":[""],"resources":["pods","pods/log"],"verbs":["get","list"]}]""")],
            [Role("ClusterRole", "everything", null, """[{"apiGroups":["*"],"resources":["*"],"verbs":["*"]}]"""),
             Role("ClusterRole", "secret-reader", null, """[{"apiGroups":[""],"resources":["secrets"],"verbs":["get","list"]}]"""),
             Role("ClusterRole", "granter", null, """[{"apiGroups":["rbac.authorization.k8s.io"],"resources":["clusterroles"],"verbs":["bind","escalate"]}]"""),
             Role("ClusterRole", "execer", null, """[{"apiGroups":[""],"resources":["pods/exec"],"verbs":["create"]}]"""),
             Role("ClusterRole", "system:node", null, """[{"apiGroups":["*"],"resources":["*"],"verbs":["*"]}]"""),
             Role("ClusterRole", "cluster-admin", null, """[{"apiGroups":["*"],"resources":["*"],"verbs":["*"]}]"""),
             Role("ClusterRole", "view", null, """[{"apiGroups":[""],"resources":["pods","services"],"verbs":["get","list","watch"]}]""")],
            [Bind("RoleBinding", "readers", "d", "pod-reader", "Role", """[{"kind":"ServiceAccount","name":"bot","namespace":"d"},{"kind":"User","name":"alice"}]"""),
             Bind("RoleBinding", "ns-viewers", "d", "view", "ClusterRole", """[{"kind":"Group","name":"system:serviceaccounts:d"}]""")],
            [Bind("ClusterRoleBinding", "ops-admin", null, "cluster-admin", "ClusterRole", """[{"kind":"User","name":"bob"},{"kind":"Group","name":"system:masters"}]"""),
             Bind("ClusterRoleBinding", "open-door", null, "secret-reader", "ClusterRole", """[{"kind":"User","name":"system:anonymous"}]"""),
             Bind("ClusterRoleBinding", "all-auth", null, "granter", "ClusterRole", """[{"kind":"Group","name":"system:authenticated"}]"""),
             Bind("ClusterRoleBinding", "discovery", null, "system:discovery", "ClusterRole", """[{"kind":"Group","name":"system:unauthenticated"}]""")]);
    }

    [Fact]
    public void Rbac_findings_flag_cluster_admin_wildcards_escalation_secrets_exec_and_anonymous_access()
    {
        var f = Rbac().Findings();
        Assert.Contains(f, x => x is { Rule: "cluster-admin-binding", Severity: FindingSeverity.High } && x.Title.StartsWith("User bob"));
        Assert.DoesNotContain(f, x => x.Title.Contains("system:masters"));              // built-in subjects are expected to be cluster-admin
        Assert.Contains(f, x => x is { Rule: "wildcard-role", Name: "everything" });
        Assert.DoesNotContain(f, x => x.Name is "system:node" or "cluster-admin" or "view");   // built-in roles are not reported
        Assert.Contains(f, x => x is { Rule: "privilege-escalation", Name: "granter" } && x.Title.Contains("bind/escalate"));
        Assert.Contains(f, x => x is { Rule: "read-secrets", Name: "secret-reader" });
        Assert.Contains(f, x => x is { Rule: "exec-pods", Name: "execer" });
        Assert.Contains(f, x => x is { Rule: "anonymous-access", Severity: FindingSeverity.High, Name: "open-door" });
        Assert.DoesNotContain(f, x => x.Name == "discovery");                           // system:unauthenticated may discover
        Assert.Contains(f, x => x is { Rule: "authenticated-binding", Name: "all-auth" });
        Assert.True(f.Zip(f.Skip(1)).All(p => p.First.Severity >= p.Second.Severity));
    }

    [Fact]
    public void Rbac_rules_for_a_subject_include_its_groups_and_the_binding_scope()
    {
        var r = Rbac();
        var bot = r.RulesFor("ServiceAccount", "bot", "d");
        Assert.Contains(bot, g => g is { Namespace: "d", Binding: "readers", Role: "pod-reader", Resources: "pods, pods/log", Verbs: "get, list", ApiGroups: "core" });
        Assert.Contains(bot, g => g is { Binding: "ns-viewers", Role: "view" });   // through the namespace's service account group
        Assert.DoesNotContain(r.RulesFor("ServiceAccount", "bot", "other"), g => g.Binding == "readers");
        var alice = r.RulesFor("User", "alice");
        Assert.Contains(alice, g => g.Binding == "readers");
        Assert.DoesNotContain(alice, g => g.Binding == "ns-viewers");
        Assert.Contains(r.RulesFor("User", "bob"), g => g is { Namespace: null, Scope: "cluster", Role: "cluster-admin" });
        Assert.Contains(r.RulesFor("User", "carol"), g => g.Binding == "all-auth");   // everyone authenticated
        Assert.DoesNotContain(r.RulesFor("User", "system:anonymous"), g => g.Binding == "all-auth");
        Assert.Contains("ServiceAccount d/bot", r.KnownSubjects());
    }

    // ---------- certificates ----------

    private static string TlsSecretJson(DateTimeOffset notAfter, string cn = "web.example.com")
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(cn); san.AddDnsName("www." + cn);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(notAfter.AddDays(-90), notAfter);
        var pem = cert.ExportCertificatePem();
        return """{"kind":"Secret","type":"kubernetes.io/tls","metadata":{"name":"web-tls","namespace":"d"},"data":{"tls.crt":"CRT","tls.key":"S0VZ"}}""".Replace("CRT", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pem)));
    }

    [Theory]
    [InlineData(100, CertificateHealth.Ok)]
    [InlineData(20, CertificateHealth.ExpiresSoon)]
    [InlineData(3, CertificateHealth.ExpiresVerySoon)]
    [InlineData(-2, CertificateHealth.Expired)]
    public void Tls_secret_expiry_is_read_from_the_certificate(int days, CertificateHealth health)
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var c = CertificateScan.FromTlsSecret(O(TlsSecretJson(now.AddDays(days).AddHours(1))), now)!;
        Assert.Equal(health, c.Health);
        Assert.Equal(days, c.DaysLeft);
        Assert.Equal("web.example.com", c.Subject);
        Assert.Equal("web.example.com, www.web.example.com", c.Detail);
        Assert.Equal("d/web-tls", c.Display);
    }

    [Fact]
    public void Tls_secret_scan_never_surfaces_the_key_and_ignores_garbage_and_other_secrets()
    {
        var now = DateTimeOffset.UtcNow;
        var c = CertificateScan.FromTlsSecret(O(TlsSecretJson(now.AddDays(50))), now)!;
        Assert.DoesNotContain("S0VZ", c.ToString());
        Assert.Null(CertificateScan.FromTlsSecret(O("""{"kind":"Secret","type":"kubernetes.io/tls","metadata":{"name":"x","namespace":"d"},"data":{"tls.crt":"bm90IGEgY2VydA=="}}"""), now));
        Assert.Null(CertificateScan.FromTlsSecret(O("""{"kind":"Secret","type":"Opaque","metadata":{"name":"x","namespace":"d"},"data":{"tls.crt":"AAAA"}}"""), now));
    }

    [Fact]
    public void Cert_manager_certificates_use_their_status()
    {
        var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var cert = O("""{"kind":"Certificate","metadata":{"name":"api","namespace":"d"},"spec":{"dnsNames":["api.example.com"],"issuerRef":{"name":"letsencrypt"}},"status":{"notAfter":"2026-10-15T00:00:00Z","conditions":[{"type":"Ready","status":"False","message":"renewal failed"}]}}""");
        var c = CertificateScan.FromCertManager(cert, now)!;
        Assert.Equal(5, c.DaysLeft); Assert.Equal(CertificateHealth.ExpiresVerySoon, c.Health);
        Assert.Equal("letsencrypt", c.Issuer); Assert.Contains("renewal failed", c.Detail);
        Assert.Equal("in 5d", c.Expires);
        Assert.Null(CertificateScan.FromCertManager(O("""{"kind":"Certificate","metadata":{"name":"n"},"status":{}}"""), now));
    }
}
