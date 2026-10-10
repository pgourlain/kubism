using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Security;

/// <summary>One permission a subject holds, with where it comes from. <see cref="Namespace"/> is null for cluster-wide grants.</summary>
public sealed record GrantedRule(string? Namespace, string Binding, string Role, string ApiGroups, string Resources, string Verbs, string? ResourceNames)
{
    public string Scope => Namespace ?? "cluster";
}

/// <summary>What the cluster's RBAC objects say: risky grants, and the rules a given user, group or service account ends up with.</summary>
public sealed class RbacAnalysis
{
    private static readonly HashSet<string> BuiltinAdminRoles = ["cluster-admin", "admin", "edit", "view"];
    private static readonly HashSet<string> AnonymousSafeRoles = ["system:basic-user", "system:discovery", "system:public-info-viewer", "system:service-account-issuer-discovery"];

    private readonly List<KubeObject> _roles, _clusterRoles, _bindings, _clusterBindings;

    public RbacAnalysis(IEnumerable<KubeObject> roles, IEnumerable<KubeObject> clusterRoles, IEnumerable<KubeObject> roleBindings, IEnumerable<KubeObject> clusterRoleBindings)
    {
        _roles = roles.ToList(); _clusterRoles = clusterRoles.ToList(); _bindings = roleBindings.ToList(); _clusterBindings = clusterRoleBindings.ToList();
    }

    // ---------- findings ----------

    public IReadOnlyList<Finding> Findings()
    {
        var findings = new List<Finding>();
        void Add(FindingSeverity sev, string rule, string title, string detail, KubeObject o) =>
            findings.Add(new Finding(sev, rule, title, detail, o.ToString(), o.Kind, o.Namespace, o.Name));

        foreach (var b in _clusterBindings.Concat(_bindings))
        {
            var roleName = b.StringAt("roleRef", "name") ?? "";
            var subjects = Subjects(b).ToList();
            var scope = b.Namespace is null ? "cluster-wide" : $"in {b.Namespace}";
            foreach (var s in subjects)
            {
                if (roleName == "cluster-admin" && !IsSystemSubject(s))
                    Add(b.Namespace is null ? FindingSeverity.High : FindingSeverity.Medium, "cluster-admin-binding", $"{Describe(s)} is cluster-admin ({scope})", "cluster-admin allows every action on every resource.", b);
                if (s.Name is "system:anonymous" or "system:unauthenticated")
                {
                    if (!AnonymousSafeRoles.Contains(roleName))
                        Add(FindingSeverity.High, "anonymous-access", $"{Describe(s)} is bound to {roleName}", "Anyone who can reach the API server, with no credentials, gets these permissions.", b);
                }
                else if (s.Name == "system:authenticated" && !roleName.StartsWith("system:"))
                {
                    var broad = roleName is "cluster-admin" or "admin" or "edit";
                    Add(broad ? FindingSeverity.High : FindingSeverity.Medium, "authenticated-binding", $"Every authenticated user is bound to {roleName} ({scope})",
                        "system:authenticated includes every service account in the cluster.", b);
                }
            }
        }

        foreach (var role in _clusterRoles.Concat(_roles))
        {
            if ((role.Name.StartsWith("system:") || BuiltinAdminRoles.Contains(role.Name)) && role.Namespace is null) continue;
            foreach (var rule in Rules(role))
            {
                var wildVerbs = rule.Verbs.Contains("*"); var wildRes = rule.Resources.Contains("*");
                if (wildVerbs && wildRes) Add(FindingSeverity.High, "wildcard-role", $"{role.Kind} {role.Name} allows everything", "Verbs and resources are both '*': this is cluster-admin in all but name.", role);
                else if (wildVerbs) Add(FindingSeverity.Medium, "wildcard-verbs", $"{role.Kind} {role.Name} allows every verb on {string.Join(", ", rule.Resources)}", "A wildcard verb includes delete, escalate and bind.", role);
                else if (wildRes && rule.Verbs.Any(v => v is "delete" or "create" or "update" or "patch" or "deletecollection")) Add(FindingSeverity.Medium, "wildcard-resources", $"{role.Kind} {role.Name} can {string.Join("/", rule.Verbs)} any resource", "A wildcard resource also covers Secrets and future kinds.", role);
                var escalates = rule.Verbs.Where(v => v is "escalate" or "bind" or "impersonate").ToList();
                if (escalates.Count > 0 && !wildVerbs) Add(FindingSeverity.High, "privilege-escalation", $"{role.Kind} {role.Name} can {string.Join("/", escalates)}", "These verbs allow granting permissions the subject does not hold, or acting as someone else.", role);
                if (rule.Resources.Contains("secrets") && rule.Verbs.Any(v => v is "get" or "list" or "watch") && role.Kind == "ClusterRole" && !wildVerbs && !wildRes)
                    Add(FindingSeverity.Medium, "read-secrets", $"ClusterRole {role.Name} can read Secrets in every namespace", "Prefer a Role limited to the namespaces and names that need it.", role);
                if (rule.Resources.Any(r => r is "pods/exec" or "pods/attach") && rule.Verbs.Any(v => v is "create" or "*") && !wildRes)
                    Add(FindingSeverity.Medium, "exec-pods", $"{role.Kind} {role.Name} can exec into pods", "A shell in a pod reaches its secrets, tokens and network.", role);
            }
        }
        return findings.DistinctBy(f => (f.Rule, f.Display, f.Title)).OrderByDescending(f => f.Severity).ThenBy(f => f.Display, StringComparer.Ordinal).ToList();
    }

    // ---------- who can do what ----------

    /// <summary>Everything a subject is granted through bindings, including through the groups it belongs to (service accounts are in system:serviceaccounts and their namespace's group).</summary>
    public IReadOnlyList<GrantedRule> RulesFor(string kind, string name, string? ns = null)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        if (kind == "ServiceAccount") { groups.Add("system:serviceaccounts"); if (ns is not null) groups.Add($"system:serviceaccounts:{ns}"); }
        if (name != "system:anonymous") groups.Add("system:authenticated");
        bool Matches(Subject s) =>
            s.Kind == kind && s.Name == name && (kind != "ServiceAccount" || s.Namespace == ns)
            || s.Kind == "Group" && groups.Contains(s.Name);

        var result = new List<GrantedRule>();
        foreach (var b in _clusterBindings.Concat(_bindings))
        {
            if (!Subjects(b).Any(Matches)) continue;
            var role = RoleOf(b);
            if (role is null) continue;
            foreach (var rule in Rules(role))
                result.Add(new GrantedRule(b.Namespace, b.Name, role.Name, Join(rule.ApiGroups), Join(rule.Resources), Join(rule.Verbs), rule.ResourceNames.Count == 0 ? null : Join(rule.ResourceNames)));
        }
        return result.OrderBy(r => r.Namespace is null ? 0 : 1).ThenBy(r => r.Namespace, StringComparer.Ordinal).ThenBy(r => r.Role, StringComparer.Ordinal).ToList();
    }

    /// <summary>The subjects bound anywhere, for completion in the UI.</summary>
    public IReadOnlyList<string> KnownSubjects() =>
        _clusterBindings.Concat(_bindings).SelectMany(Subjects).Select(s => s.Kind == "ServiceAccount" ? $"ServiceAccount {s.Namespace}/{s.Name}" : $"{s.Kind} {s.Name}").Distinct().Order(StringComparer.Ordinal).ToList();

    private KubeObject? RoleOf(KubeObject binding)
    {
        var kind = binding.StringAt("roleRef", "kind"); var name = binding.StringAt("roleRef", "name");
        if (kind == "ClusterRole") return _clusterRoles.FirstOrDefault(r => r.Name == name);
        return _roles.FirstOrDefault(r => r.Name == name && r.Namespace == binding.Namespace);
    }

    private sealed record Rule(IReadOnlyList<string> ApiGroups, IReadOnlyList<string> Resources, IReadOnlyList<string> Verbs, IReadOnlyList<string> ResourceNames);
    private sealed record Subject(string Kind, string Name, string? Namespace);

    private static IEnumerable<Rule> Rules(KubeObject role) =>
        (role.At("rules") as JsonArray ?? []).OfType<JsonObject>().Select(r => new Rule(Strings(r["apiGroups"], keepEmpty: true), Strings(r["resources"]), Strings(r["verbs"]), Strings(r["resourceNames"])))
            .Where(r => r.Resources.Count > 0 || r.Verbs.Count > 0);

    private static IEnumerable<Subject> Subjects(KubeObject binding) =>
        (binding.At("subjects") as JsonArray ?? []).OfType<JsonObject>().Select(s => new Subject(s["kind"]?.ToString() ?? "", s["name"]?.ToString() ?? "", s["namespace"]?.ToString()));

    /// <summary>The core API group is the empty string in RBAC rules, so it must survive for apiGroups.</summary>
    private static List<string> Strings(JsonNode? n, bool keepEmpty = false) => (n as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => keepEmpty || x.Length > 0).ToList();
    private static string Join(IEnumerable<string> v) => string.Join(", ", v.Select(x => x.Length == 0 ? "core" : x));
    private static bool IsSystemSubject(Subject s) => s.Name.StartsWith("system:") && s.Kind != "ServiceAccount" || s is { Kind: "ServiceAccount", Namespace: "kube-system" };
    private static string Describe(Subject s) => s.Kind == "ServiceAccount" ? $"ServiceAccount {s.Namespace}/{s.Name}" : $"{s.Kind} {s.Name}";
}
