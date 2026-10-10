using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Security;

/// <summary>
/// What the cluster's RBAC objects say, with the semantics of the API server's RBAC authorizer (the one behind <c>kubectl auth can-i</c>):
/// wildcards in verbs, apiGroups and resources, subresources (<c>pods/exec</c>, <c>*/scale</c>, <c>pods/*</c>), <c>resourceNames</c>,
/// <c>nonResourceURLs</c>, aggregated ClusterRoles, implicit groups (<c>system:authenticated</c>, <c>system:serviceaccounts[:ns]</c>),
/// RoleBinding → ClusterRole (the cluster role applies to one namespace only) and bindings to roles that do not exist.
/// Rights are additive: there is no deny. Pure and immutable: build a new graph when an RBAC object changes (see <see cref="RbacStore"/>).
/// Not covered: authorizers other than RBAC (Node, webhook, Azure RBAC for Kubernetes, ABAC). See docs/adr/0004-rbac-graph.md.
/// </summary>
public sealed class RbacGraph
{
    private const string RbacGroup = "rbac.authorization.k8s.io";
    private static readonly string[] StandardVerbs = ["get", "list", "watch", "create", "update", "patch", "delete", "deletecollection"];

    /// <summary>The API group of well-known resources, used when a request names a resource without its group.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownGroups = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pods"] = "", ["services"] = "", ["configmaps"] = "", ["secrets"] = "", ["namespaces"] = "", ["nodes"] = "", ["events"] = "",
        ["persistentvolumeclaims"] = "", ["persistentvolumes"] = "", ["serviceaccounts"] = "", ["endpoints"] = "", ["resourcequotas"] = "", ["limitranges"] = "",
        ["replicationcontrollers"] = "", ["users"] = "", ["groups"] = "",
        ["deployments"] = "apps", ["replicasets"] = "apps", ["statefulsets"] = "apps", ["daemonsets"] = "apps", ["controllerrevisions"] = "apps",
        ["jobs"] = "batch", ["cronjobs"] = "batch",
        ["ingresses"] = "networking.k8s.io", ["networkpolicies"] = "networking.k8s.io", ["ingressclasses"] = "networking.k8s.io",
        ["roles"] = RbacGroup, ["rolebindings"] = RbacGroup, ["clusterroles"] = RbacGroup, ["clusterrolebindings"] = RbacGroup,
        ["storageclasses"] = "storage.k8s.io", ["horizontalpodautoscalers"] = "autoscaling", ["poddisruptionbudgets"] = "policy",
        ["customresourcedefinitions"] = "apiextensions.k8s.io", ["selfsubjectaccessreviews"] = "authorization.k8s.io", ["subjectaccessreviews"] = "authorization.k8s.io",
    };

    /// <summary>Requests that make a rule dangerous. A rule that already allows everything is reported once as <c>wildcard-all</c> instead.</summary>
    private static readonly (string Code, string Description, FindingSeverity Severity, string Verb, string Group, string Resource)[] Probes =
    [
        ("escalate", "Can edit roles beyond its own permissions (escalate).", FindingSeverity.High, "escalate", RbacGroup, "clusterroles"),
        ("escalate", "Can edit roles beyond its own permissions (escalate).", FindingSeverity.High, "escalate", RbacGroup, "roles"),
        ("bind", "Can bind roles it does not hold (bind).", FindingSeverity.High, "bind", RbacGroup, "clusterroles"),
        ("bind", "Can bind roles it does not hold (bind).", FindingSeverity.High, "bind", RbacGroup, "roles"),
        ("impersonate", "Can act as other users, groups or service accounts (impersonate).", FindingSeverity.High, "impersonate", "", "users"),
        ("impersonate", "Can act as other users, groups or service accounts (impersonate).", FindingSeverity.High, "impersonate", "", "groups"),
        ("impersonate", "Can act as other users, groups or service accounts (impersonate).", FindingSeverity.High, "impersonate", "", "serviceaccounts"),
        ("exec-pods", "Can open a shell in pods (create pods/exec): reaches their secrets, tokens and network.", FindingSeverity.Medium, "create", "", "pods/exec"),
        ("attach-pods", "Can attach to running containers (create pods/attach).", FindingSeverity.Medium, "create", "", "pods/attach"),
        ("read-secrets", "Can read Secrets (get, list or watch).", FindingSeverity.Medium, "get", "", "secrets"),
        ("read-secrets", "Can read Secrets (get, list or watch).", FindingSeverity.Medium, "list", "", "secrets"),
        ("read-secrets", "Can read Secrets (get, list or watch).", FindingSeverity.Medium, "watch", "", "secrets"),
        ("create-pods", "Can create pods: a privileged pod is a way around other restrictions.", FindingSeverity.Medium, "create", "", "pods"),
    ];

    private readonly List<RbacRole> _roles = [];
    private readonly List<RbacRole> _clusterRoles = [];
    private readonly List<RbacBinding> _bindings = [];

    private RbacGraph(IReadOnlyList<RbacKindStatus> availability) => Availability = availability;

    public IReadOnlyList<RbacRole> Roles => _roles;
    public IReadOnlyList<RbacRole> ClusterRoles => _clusterRoles;
    public IReadOnlyList<RbacBinding> Bindings => _bindings;
    public IReadOnlyList<RbacKindStatus> Availability { get; }

    /// <summary>False when some of the four RBAC kinds could not be read: every answer is then partial and must be shown as such.</summary>
    public bool IsComplete => Availability.All(a => a.Loaded);

    public IReadOnlyList<string> MissingKinds => Availability.Where(a => !a.Loaded).Select(a => a.Kind).ToList();

    public string? IncompleteWarning => IsComplete ? null
        : $"Incomplete data: cannot read {string.Join(", ", MissingKinds)}. What follows is not the full picture.{Availability.Where(a => !a.Loaded && a.Error is not null).Select(a => $" {a.Kind}: {a.Error}").FirstOrDefault()}";

    public IReadOnlyList<RbacBinding> OrphanBindings => _bindings.Where(b => b.IsOrphan).ToList();

    // ---------- construction ----------

    public static RbacGraph Build(IEnumerable<KubeObject> roles, IEnumerable<KubeObject> clusterRoles, IEnumerable<KubeObject> roleBindings, IEnumerable<KubeObject> clusterRoleBindings,
        IReadOnlyList<RbacKindStatus>? availability = null)
    {
        var graph = new RbacGraph(availability ?? [new("roles", true, null), new("clusterroles", true, null), new("rolebindings", true, null), new("clusterrolebindings", true, null)]);
        var clusterObjects = clusterRoles.ToList();
        foreach (var o in clusterObjects) graph._clusterRoles.Add(new RbacRole("ClusterRole", null, o.Name, EffectiveRules(o, clusterObjects), o.At("aggregationRule") is not null, o));
        foreach (var o in roles) graph._roles.Add(new RbacRole("Role", o.Namespace, o.Name, OwnRules(o), false, o));
        var roleByKey = graph._roles.ToDictionary(r => (r.Namespace, r.Name), r => r);
        var clusterByName = graph._clusterRoles.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var o in roleBindings) graph._bindings.Add(MakeBinding("RoleBinding", o, roleByKey, clusterByName));
        foreach (var o in clusterRoleBindings) graph._bindings.Add(MakeBinding("ClusterRoleBinding", o, roleByKey, clusterByName));
        return graph;
    }

    private static RbacBinding MakeBinding(string kind, KubeObject o, Dictionary<(string?, string), RbacRole> roles, Dictionary<string, RbacRole> clusterRoles)
    {
        var roleKind = o.StringAt("roleRef", "kind") ?? "";
        var roleName = o.StringAt("roleRef", "name") ?? "";
        RbacRole? role = null;
        if (roleKind == "ClusterRole") clusterRoles.TryGetValue(roleName, out role);
        // A ClusterRoleBinding can only reference a ClusterRole; a RoleBinding references a Role of its own namespace or any ClusterRole.
        else if (roleKind == "Role" && kind == "RoleBinding") roles.TryGetValue((o.Namespace, roleName), out role);
        var subjects = (o.At("subjects") as JsonArray ?? []).OfType<JsonObject>().Select(Subject).Where(s => s is not null).Select(s => s!).ToList();
        return new RbacBinding(kind, kind == "RoleBinding" ? o.Namespace : null, o.Name, roleKind, roleName, subjects, role, o);
    }

    private static RbacSubject? Subject(JsonObject s)
    {
        var kind = KubeObject.Str(s, "kind") ?? ""; var name = KubeObject.Str(s, "name") ?? "";
        if (name.Length == 0) return null;
        return kind switch
        {
            "ServiceAccount" => new RbacSubject(RbacSubject.ServiceAccount, name, KubeObject.Str(s, "namespace") ?? ""),
            "Group" => new RbacSubject(RbacSubject.Group, name),
            "User" => new RbacSubject(RbacSubject.User, name),
            _ => null,
        };
    }

    private static List<RbacRule> OwnRules(KubeObject role) =>
        (role.At("rules") as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => new RbacRule(RbacJson.Strings(r["apiGroups"]), RbacJson.Strings(r["resources"]), RbacJson.Strings(r["verbs"]), RbacJson.Strings(r["resourceNames"]), RbacJson.Strings(r["nonResourceURLs"])))
            .Where(r => r.Verbs.Count > 0 && (r.Resources.Count > 0 || r.NonResourceUrls.Count > 0)).ToList();

    /// <summary>Own rules plus the rules of every ClusterRole an aggregationRule selects (recursively, cycle-safe, no duplicates).</summary>
    private static IReadOnlyList<RbacRule> EffectiveRules(KubeObject role, List<KubeObject> clusterRoles)
    {
        var result = new List<RbacRule>(); var seen = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Add(KubeObject r)
        {
            if (!visited.Add(r.Name)) return;
            foreach (var rule in OwnRules(r)) if (seen.Add(rule.Summary + "|" + string.Join(",", rule.ApiGroups))) result.Add(rule);
            if (r.At("aggregationRule", "clusterRoleSelectors") is not JsonArray selectors) return;
            foreach (var other in clusterRoles)
                if (other != r && selectors.OfType<JsonObject>().Any(s => SelectorMatches(s, other.Labels))) Add(other);
        }
        Add(role);
        return result;
    }

    /// <summary>A Kubernetes label selector: every matchLabels entry and every matchExpressions entry must hold; an empty selector matches everything.</summary>
    internal static bool SelectorMatches(JsonObject selector, IReadOnlyDictionary<string, string> labels)
    {
        if (selector["matchLabels"] is JsonObject ml)
            foreach (var kv in ml) if (!labels.TryGetValue(kv.Key, out var v) || v != kv.Value?.ToString()) return false;
        foreach (var e in (selector["matchExpressions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = KubeObject.Str(e, "key") ?? ""; var values = RbacJson.Strings(e["values"]);
            var has = labels.TryGetValue(key, out var val);
            var ok = KubeObject.Str(e, "operator") switch
            {
                "In" => has && values.Contains(val!), "NotIn" => !has || !values.Contains(val!),
                "Exists" => has, "DoesNotExist" => !has, _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    // ---------- matching (the RBAC authorizer) ----------

    public static bool VerbMatches(RbacRule rule, string verb) => rule.Verbs.Contains("*") || rule.Verbs.Contains(verb);

    private static bool GroupMatches(RbacRule rule, string? group) => group is null || rule.ApiGroups.Contains("*") || rule.ApiGroups.Contains(group);

    /// <summary>The authorizer's ResourceMatches: <c>*</c>, an exact <c>resource[/sub]</c>, <c>*/sub</c> for a subresource of anything, <c>resource/*</c> for any subresource.</summary>
    public static bool ResourceMatches(RbacRule rule, string resource, string? subresource)
    {
        var combined = string.IsNullOrEmpty(subresource) ? resource : $"{resource}/{subresource}";
        foreach (var r in rule.Resources)
        {
            if (r == "*" || r == combined) return true;
            if (!string.IsNullOrEmpty(subresource) && r.StartsWith("*/", StringComparison.Ordinal) && r[2..] == subresource) return true;
            if (!string.IsNullOrEmpty(subresource) && r.EndsWith("/*", StringComparison.Ordinal) && r[..^2] == resource) return true;
        }
        return false;
    }

    private static bool NameMatches(RbacRule rule, string? name) => rule.ResourceNames.Count == 0 || (name is not null && rule.ResourceNames.Contains(name));

    private static bool UrlMatches(RbacRule rule, string url)
    {
        foreach (var u in rule.NonResourceUrls)
            if (u == "*" || u == url || (u.EndsWith('*') && url.StartsWith(u[..^1], StringComparison.Ordinal))) return true;
        return false;
    }

    /// <summary>Whether one rule covers the request, ignoring where the rule came from. Rules with resourceNames only match requests that name an object.</summary>
    public static bool RuleMatches(RbacRule rule, RbacRequest r)
    {
        if (!VerbMatches(rule, r.Verb)) return false;
        if (r.IsNonResource) return rule.IsNonResource && UrlMatches(rule, r.NonResourceUrl!);
        if (rule.Resources.Count == 0) return false;
        return GroupMatches(rule, r.Group) && ResourceMatches(rule, r.Resource, r.Subresource) && NameMatches(rule, r.Name);
    }

    /// <summary>
    /// A RoleBinding grants inside its namespace only; a ClusterRoleBinding grants everywhere, to cluster-scoped resources and to non-resource URLs.
    /// A request with no namespace is a cluster-level request (cluster-scoped resource, or a namespaced one across all namespaces).
    /// </summary>
    private static bool ScopeCovers(RbacBinding b, RbacRequest r) => b.Namespace is null || (!r.IsNonResource && r.Namespace == b.Namespace);

    private static bool Applies(RbacPath p, RbacRequest r) => ScopeCovers(p.Binding, r) && RuleMatches(p.Rule, r);

    // ---------- identity ----------

    private sealed record Identity(string? UserName, HashSet<string> Groups, RbacSubject Subject)
    {
        public bool IsSuperuser => Groups.Contains("system:masters");
    }

    private static Identity IdentityOf(RbacSubject s, IEnumerable<string>? extraGroups)
    {
        var groups = new HashSet<string>(extraGroups ?? [], StringComparer.Ordinal);
        switch (s.Kind)
        {
            case RbacSubject.ServiceAccount:
                groups.Add("system:serviceaccounts"); groups.Add($"system:serviceaccounts:{s.Namespace}"); groups.Add("system:authenticated");
                return new Identity(s.UserName, groups, s);
            case RbacSubject.Group:
                groups.Add(s.Name);
                return new Identity(null, groups, s);
            default:
                groups.Add(s.Name is "system:anonymous" ? "system:unauthenticated" : "system:authenticated");
                return new Identity(s.Name, groups, s);
        }
    }

    /// <summary>The group a subject matched through, when it matched at all.</summary>
    private static (bool Match, string? ViaGroup) Matches(RbacSubject bound, Identity id)
    {
        switch (bound.Kind)
        {
            case RbacSubject.Group:
                if (!id.Groups.Contains(bound.Name)) return (false, null);
                return (true, id.Subject is { Kind: RbacSubject.Group } s && s.Name == bound.Name ? null : bound.Name);
            case RbacSubject.ServiceAccount:
                return (id.UserName == bound.UserName, null);
            default:
                return (id.UserName is not null && id.UserName == bound.Name, null);
        }
    }

    private IEnumerable<RbacPath> PathsOf(Identity id)
    {
        foreach (var b in _bindings)
        {
            if (b.Role is null) continue;
            string? via = null; var hit = false;
            foreach (var s in b.Subjects)
            {
                var (m, g) = Matches(s, id);
                if (!m) continue;
                hit = true; via = g;
                if (g is null) break;
            }
            if (!hit) continue;
            var note = b.ClusterRoleInNamespace ? $"ClusterRole applied in namespace {b.Namespace} only" : null;
            foreach (var rule in b.Role.Rules) yield return new RbacPath(b, b.Role, rule, via, note);
        }
    }

    // ---------- queries ----------

    /// <summary>"Can this subject do this?" with every binding → role → rule path that says yes. Extra groups come from the real identity (SelfSubjectReview), when known.</summary>
    public RbacExplanation Explain(RbacSubject subject, RbacRequest request, IEnumerable<string>? extraGroups = null)
    {
        var id = IdentityOf(subject, extraGroups);
        var paths = PathsOf(id).Where(p => Applies(p, request)).ToList();
        var notes = new List<string>();
        if (id.IsSuperuser) notes.Add("The group system:masters is always authorized by the API server, outside RBAC.");
        if (request.Group is null && !request.IsNonResource) notes.Add($"No API group given for '{request.Resource}': a rule in any group matched.");
        if (IncompleteWarning is { } w) notes.Add(w);
        foreach (var o in _bindings.Where(b => b.IsOrphan && b.Subjects.Any(s => Matches(s, id).Match)))
            notes.Add($"{o.Display} refers to {o.RoleKind} {o.RoleName}, which does not exist: it grants nothing.");
        return new RbacExplanation(request, paths.Count > 0 || id.IsSuperuser, paths, notes);
    }

    /// <summary>Who can do this: every bound subject whose binding grants it, with the paths. A group such as system:authenticated is listed as the group, not expanded.</summary>
    public IReadOnlyList<WhoCanEntry> WhoCan(RbacRequest request)
    {
        var byKey = new Dictionary<string, (RbacSubject Subject, List<RbacPath> Paths)>();
        foreach (var b in _bindings)
        {
            if (b.Role is null || !ScopeCovers(b, request)) continue;
            foreach (var rule in b.Role.Rules.Where(r => RuleMatches(r, request)))
                foreach (var s in b.Subjects)
                {
                    if (!byKey.TryGetValue(s.Key, out var e)) byKey[s.Key] = e = (s, []);
                    e.Paths.Add(new RbacPath(b, b.Role, rule, null, b.ClusterRoleInNamespace ? $"ClusterRole applied in namespace {b.Namespace} only" : null));
                }
        }
        var result = byKey.Values.Select(e => new WhoCanEntry(e.Subject, e.Paths, e.Paths.SelectMany(p => RisksOf(p.Rule, p.Role.Name)).DistinctBy(r => r.Code).ToList()))
            .OrderBy(e => e.Subject.Kind, StringComparer.Ordinal).ThenBy(e => e.Subject.Display, StringComparer.Ordinal).ToList();
        result.Add(new WhoCanEntry(new RbacSubject(RbacSubject.Group, "system:masters"), [], []));
        return result;
    }

    /// <summary>Every path that grants anything to the subject (through its bindings and its implicit or given groups), cluster-wide and per namespace.</summary>
    public SubjectRights RightsOf(RbacSubject subject, IEnumerable<string>? extraGroups = null)
    {
        var id = IdentityOf(subject, extraGroups);
        var paths = PathsOf(id).OrderBy(p => p.Binding.Namespace is null ? 0 : 1).ThenBy(p => p.Binding.Namespace, StringComparer.Ordinal).ThenBy(p => p.Role.Name, StringComparer.Ordinal).ToList();
        var notes = new List<string>();
        if (id.IsSuperuser) notes.Add("The group system:masters is always authorized by the API server, outside RBAC.");
        if (IncompleteWarning is { } w) notes.Add(w);
        foreach (var o in _bindings.Where(b => b.IsOrphan && b.Subjects.Any(s => Matches(s, id).Match)))
            notes.Add($"{o.Display} refers to {o.RoleKind} {o.RoleName}, which does not exist: it grants nothing.");
        var namespaces = paths.Select(p => p.Binding.Namespace).Where(n => n is not null).Select(n => n!).Distinct().Order(StringComparer.Ordinal).ToList();
        return new SubjectRights(subject, paths, namespaces, notes);
    }

    /// <summary>
    /// The verbs × resources matrix of what a subject can do in one namespace (ClusterRoleBindings plus that namespace's RoleBindings),
    /// or cluster-wide only when <paramref name="ns"/> is null. Cells with resourceNames are <see cref="RightLevel.Limited"/>.
    /// </summary>
    public RightsMatrix MatrixFor(RbacSubject subject, string? ns, IEnumerable<string>? extraGroups = null)
    {
        var paths = PathsOf(IdentityOf(subject, extraGroups)).Where(p => p.Binding.Namespace is null || p.Binding.Namespace == ns && ns is not null).Where(p => !p.Rule.IsNonResource).ToList();
        var verbs = StandardVerbs.Concat(paths.SelectMany(p => p.Rule.Verbs).Where(v => v != "*" && !StandardVerbs.Contains(v)).Distinct().Order(StringComparer.Ordinal)).ToList();
        var rows = new Dictionary<string, (string Group, string Resource, List<RbacPath> Paths)>();
        foreach (var p in paths)
            foreach (var group in p.Rule.ApiGroups.Count == 0 ? [""] : p.Rule.ApiGroups)
                foreach (var res in p.Rule.Resources)
                {
                    var key = group.Length == 0 || res == "*" ? res : $"{res}.{group}";
                    if (!rows.TryGetValue(key, out var row)) rows[key] = row = (group, res, []);
                    row.Paths.Add(p);
                }
        var result = new List<RightsRow>();
        foreach (var (key, row) in rows.OrderBy(r => r.Key == "*" ? "" : r.Key, StringComparer.Ordinal))
        {
            var cells = new List<RightCell>(); var rowRisky = false;
            foreach (var verb in verbs)
            {
                var granting = row.Paths.Where(p => VerbMatches(p.Rule, verb)).ToList();
                if (granting.Count == 0) { cells.Add(new RightCell(RightLevel.None, null, false)); continue; }
                var full = granting.Any(p => p.Rule.ResourceNames.Count == 0);
                var risky = IsRiskyCell(row.Resource, row.Group, verb, granting.Select(p => p.Rule));
                rowRisky |= risky;
                cells.Add(new RightCell(full ? RightLevel.Allowed : RightLevel.Limited, string.Join(", ", granting.Select(p => p.Binding.Display).Distinct()), risky));
            }
            result.Add(new RightsRow(key, cells, rowRisky));
        }
        return new RightsMatrix(ns, verbs, result);
    }

    private static bool IsRiskyCell(string resource, string group, string verb, IEnumerable<RbacRule> rules)
    {
        if (verb == "*" || resource == "*" || rules.Any(r => r.Verbs.Contains("*") && r.Resources.Contains("*"))) return true;
        // A verb-wildcard or resource-wildcard rule makes the cell as dangerous as the probe it covers.
        var slash = resource.IndexOf('/');
        var baseRes = slash < 0 ? resource : resource[..slash]; var sub = slash < 0 ? null : resource[(slash + 1)..];
        foreach (var pr in Probes)
        {
            if (pr.Verb != verb) continue;
            var ps = pr.Resource.IndexOf('/');
            var pBase = ps < 0 ? pr.Resource : pr.Resource[..ps]; var pSub = ps < 0 ? null : pr.Resource[(ps + 1)..];
            if (pBase == baseRes && pSub == sub && (group == "*" || group == pr.Group)) return true;
        }
        return false;
    }

    // ---------- risk ----------

    /// <summary>Why a rule (of a role of this name) is dangerous, using the same matching as the authorizer so wildcards count.</summary>
    public static IReadOnlyList<RbacRisk> RisksOf(RbacRule rule, string? roleName = null)
    {
        var risks = new List<RbacRisk>();
        if (roleName == "cluster-admin") risks.Add(new("cluster-admin", "cluster-admin allows every action on every resource.", FindingSeverity.High));
        if (rule.IsNonResource)
        {
            if (rule.NonResourceUrls.Contains("*") && rule.Verbs.Contains("*")) risks.Add(new("wildcard-all", "Every verb on every non-resource URL.", FindingSeverity.Medium));
            return risks;
        }
        var allVerbs = rule.Verbs.Contains("*"); var allRes = rule.Resources.Contains("*");
        if (allVerbs && allRes && (rule.ApiGroups.Contains("*") || rule.ApiGroups.Count == 0) && rule.ResourceNames.Count == 0)
        {
            risks.Add(new("wildcard-all", "Every verb on every resource: this is cluster-admin in all but name.", FindingSeverity.High));
            return risks;
        }
        if (allVerbs) risks.Add(new("wildcard-verbs", "A wildcard verb includes delete, escalate, bind and impersonate.", FindingSeverity.Medium));
        if (allRes && (allVerbs || rule.Verbs.Any(v => v is "create" or "update" or "patch" or "delete" or "deletecollection")))
            risks.Add(new("wildcard-resources", "A wildcard resource also covers Secrets and kinds added later.", FindingSeverity.Medium));
        foreach (var pr in Probes)
        {
            var slash = pr.Resource.IndexOf('/');
            var req = new RbacRequest(pr.Verb, pr.Group, slash < 0 ? pr.Resource : pr.Resource[..slash], slash < 0 ? null : pr.Resource[(slash + 1)..]);
            // resourceNames narrow the rule: still a risk, but a smaller one, and only found by naming an object.
            var narrowed = rule.ResourceNames.Count > 0;
            if (!(narrowed ? RuleMatches(rule, req with { Name = rule.ResourceNames[0] }) : RuleMatches(rule, req))) continue;
            if (risks.Any(r => r.Code == pr.Code)) continue;
            risks.Add(new(pr.Code, narrowed ? pr.Description + " (limited to named objects)" : pr.Description, narrowed && pr.Severity == FindingSeverity.Medium ? FindingSeverity.Low : pr.Severity));
        }
        return risks;
    }

    public static IReadOnlyList<RbacRisk> RisksOf(RbacRole role) =>
        role.Rules.SelectMany(r => RisksOf(r, role.Kind == "ClusterRole" ? role.Name : null)).DistinctBy(r => r.Code).OrderByDescending(r => r.Severity).ToList();

    /// <summary>Risks of a binding itself: bound to everyone, or to the superuser role.</summary>
    public static IReadOnlyList<RbacRisk> RisksOf(RbacBinding binding)
    {
        var risks = new List<RbacRisk>();
        var safeAnonymous = binding.RoleName is "system:basic-user" or "system:discovery" or "system:public-info-viewer" or "system:service-account-issuer-discovery";
        foreach (var s in binding.Subjects)
        {
            if (s.Name is "system:unauthenticated" or "system:anonymous" && !safeAnonymous)
                risks.Add(new("anonymous-subject", $"{s.Display} is bound to {binding.RoleName}: anyone who reaches the API server, with no credentials, gets these rights.", FindingSeverity.High));
            else if (s is { Kind: RbacSubject.Group, Name: "system:authenticated" } && !binding.RoleName.StartsWith("system:", StringComparison.Ordinal))
                risks.Add(new("authenticated-subject", $"Every authenticated user (and service account) is bound to {binding.RoleName}.", FindingSeverity.High));
            else if (s is { Kind: RbacSubject.Group, Name: "system:serviceaccounts" } && !binding.RoleName.StartsWith("system:", StringComparison.Ordinal))
                risks.Add(new("all-serviceaccounts-subject", $"Every service account in the cluster is bound to {binding.RoleName}.", FindingSeverity.High));
        }
        if (binding.RoleName == "cluster-admin" && binding.Subjects.Any(s => !(s.Name.StartsWith("system:", StringComparison.Ordinal) && s.Kind != RbacSubject.ServiceAccount)))
            risks.Add(new("cluster-admin", binding.Namespace is null ? "Bound to cluster-admin cluster-wide." : $"Bound to cluster-admin in namespace {binding.Namespace}.", binding.Namespace is null ? FindingSeverity.High : FindingSeverity.Medium));
        return risks.DistinctBy(r => r.Code).ToList();
    }

    public IReadOnlyList<RbacSubject> KnownSubjects() =>
        _bindings.SelectMany(b => b.Subjects).DistinctBy(s => s.Key).OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Display, StringComparer.Ordinal).ToList();

    public RbacRole? FindRole(string kind, string? ns, string name) =>
        kind == "ClusterRole" ? _clusterRoles.FirstOrDefault(r => r.Name == name) : _roles.FirstOrDefault(r => r.Name == name && r.Namespace == ns);
}
