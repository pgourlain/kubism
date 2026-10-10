using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Security;

/// <summary>A user, group or service account as RBAC bindings name it. <see cref="Namespace"/> is only set for service accounts.</summary>
public sealed record RbacSubject(string Kind, string Name, string? Namespace = null)
{
    public const string User = "User", Group = "Group", ServiceAccount = "ServiceAccount";

    public string Key => Kind == ServiceAccount ? $"{Kind}:{Namespace}/{Name}" : $"{Kind}:{Name}";
    public string Display => Kind == ServiceAccount ? $"ServiceAccount {Namespace}/{Name}" : $"{Kind} {Name}";

    /// <summary>The user name the API server sees for a service account.</summary>
    public string UserName => Kind == ServiceAccount ? $"system:serviceaccount:{Namespace}:{Name}" : Name;

    /// <summary>"ServiceAccount ns/name", "User name", "Group name"; a bare "ns/name" is a service account, a bare name a user.</summary>
    public static RbacSubject? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("system:serviceaccount:", StringComparison.Ordinal))
        {
            var p = text["system:serviceaccount:".Length..].Split(':', 2);
            return p.Length == 2 && p[0].Length > 0 && p[1].Length > 0 ? new RbacSubject(ServiceAccount, p[1], p[0]) : null;
        }
        var parts = text.Split([' ', ':'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string kind, rest;
        if (parts.Length == 2 && parts[0].ToLowerInvariant() is "user" or "group" or "serviceaccount" or "sa")
        {
            kind = parts[0].ToLowerInvariant() switch { "user" => User, "group" => Group, _ => ServiceAccount };
            rest = parts[1];
        }
        else { kind = text.Contains('/') ? ServiceAccount : User; rest = text; }
        if (kind != ServiceAccount) return new RbacSubject(kind, rest);
        if (rest.StartsWith("system:serviceaccount:", StringComparison.Ordinal)) rest = rest["system:serviceaccount:".Length..].Replace(':', '/');
        var slash = rest.IndexOf('/');
        return slash <= 0 ? null : new RbacSubject(ServiceAccount, rest[(slash + 1)..], rest[..slash]);
    }
}

/// <summary>One RBAC PolicyRule. An empty apiGroups entry is the core group.</summary>
public sealed record RbacRule(IReadOnlyList<string> ApiGroups, IReadOnlyList<string> Resources, IReadOnlyList<string> Verbs,
    IReadOnlyList<string> ResourceNames, IReadOnlyList<string> NonResourceUrls)
{
    public bool IsNonResource => NonResourceUrls.Count > 0;

    public string Summary
    {
        get
        {
            var verbs = string.Join(",", Verbs);
            if (IsNonResource) return $"{verbs} {string.Join(", ", NonResourceUrls)}";
            var res = string.Join(", ", Resources.SelectMany(r => ApiGroups.Count == 0 ? [r] : ApiGroups.Select(g => g.Length == 0 ? r : $"{r}.{g}")).Distinct());
            return $"{verbs} {res}{(ResourceNames.Count > 0 ? $" [{string.Join(", ", ResourceNames)}]" : "")}";
        }
    }
}

/// <summary>A Role or ClusterRole with its effective rules (own rules plus those of the ClusterRoles an aggregationRule selects).</summary>
public sealed record RbacRole(string Kind, string? Namespace, string Name, IReadOnlyList<RbacRule> Rules, bool Aggregated, KubeObject Source)
{
    public string Key => $"{Kind}/{Namespace}/{Name}";
    public string Display => Namespace is null ? $"{Kind} {Name}" : $"{Kind} {Namespace}/{Name}";
}

/// <summary>A RoleBinding or ClusterRoleBinding. <see cref="Role"/> is null when roleRef points at nothing that exists (an orphan).</summary>
public sealed record RbacBinding(string Kind, string? Namespace, string Name, string RoleKind, string RoleName, IReadOnlyList<RbacSubject> Subjects, RbacRole? Role, KubeObject Source)
{
    public string Key => $"{Kind}/{Namespace}/{Name}";
    public string Display => Namespace is null ? $"{Kind} {Name}" : $"{Kind} {Namespace}/{Name}";
    public bool IsOrphan => Role is null;
    /// <summary>A RoleBinding that points at a ClusterRole: the cluster role's rules apply to this one namespace only.</summary>
    public bool ClusterRoleInNamespace => Kind == "RoleBinding" && RoleKind == "ClusterRole";
}

/// <summary>What is being asked: a resource request (verb, group, resource, subresource, name, namespace) or a non-resource URL.</summary>
public sealed record RbacRequest(string Verb, string? Group, string Resource, string? Subresource = null, string? Name = null, string? Namespace = null, string? NonResourceUrl = null)
{
    public bool IsNonResource => NonResourceUrl is not null;

    /// <summary>
    /// "pods", "pods/exec", "deployments.apps", "deployments.apps/scale", or a path starting with "/" for a non-resource URL.
    /// Without a group, the well-known group of the resource is used, else any group matches.
    /// </summary>
    public static RbacRequest Parse(string verb, string resource, string? ns = null, string? name = null)
    {
        if (resource.StartsWith('/')) return new RbacRequest(verb, null, "", null, null, null, resource);
        string? sub = null;
        var slash = resource.IndexOf('/');
        if (slash > 0) { sub = resource[(slash + 1)..]; resource = resource[..slash]; }
        string? group = null;
        var dot = resource.IndexOf('.');
        if (dot > 0) { group = resource[(dot + 1)..]; resource = resource[..dot]; }
        else if (RbacGraph.KnownGroups.TryGetValue(resource, out var g)) group = g;
        return new RbacRequest(verb, group, resource, sub, name, ns);
    }

    public string Display => IsNonResource ? $"{Verb} {NonResourceUrl}"
        : $"{Verb} {Resource}{(Subresource is null ? "" : "/" + Subresource)}{(Group is { Length: > 0 } ? "." + Group : "")}{(Name is null ? "" : " " + Name)}{(Namespace is null ? " (all namespaces / cluster)" : $" in {Namespace}")}";
}

/// <summary>Why a rule is dangerous.</summary>
public sealed record RbacRisk(string Code, string Description, FindingSeverity Severity);

/// <summary>One way a subject gets a right: through a binding, to a role, by a rule. <see cref="ViaGroup"/> is set when the subject belongs to the bound group implicitly.</summary>
public sealed record RbacPath(RbacBinding Binding, RbacRole Role, RbacRule Rule, string? ViaGroup = null, string? Note = null)
{
    public string? Scope => Binding.Namespace;
    public string ScopeText => Binding.Namespace is null ? "cluster-wide" : $"namespace {Binding.Namespace}";
    public string Text => $"{Binding.Display} → {Role.Display} → {Rule.Summary} ({ScopeText}){(ViaGroup is null ? "" : $" via group {ViaGroup}")}";
}

public sealed record RbacExplanation(RbacRequest Request, bool Allowed, IReadOnlyList<RbacPath> Paths, IReadOnlyList<string> Notes);

public sealed record WhoCanEntry(RbacSubject Subject, IReadOnlyList<RbacPath> Paths, IReadOnlyList<RbacRisk> Risks)
{
    /// <summary>True when the subject is a group every user (or every service account) belongs to.</summary>
    public bool IsBroad => Subject is { Kind: RbacSubject.Group, Name: "system:authenticated" or "system:unauthenticated" or "system:serviceaccounts" } || Subject is { Kind: RbacSubject.User, Name: "system:anonymous" };
}

/// <summary>One cell of the verbs × resources matrix.</summary>
public enum RightLevel { None, Allowed, Limited }

public sealed record RightCell(RightLevel Level, string? Via, bool Risky);

/// <summary>Resources down, verbs across. <see cref="Namespace"/> null is "cluster-wide only" (what a ClusterRoleBinding grants).</summary>
public sealed record RightsMatrix(string? Namespace, IReadOnlyList<string> Verbs, IReadOnlyList<RightsRow> Rows);

public sealed record RightsRow(string Resource, IReadOnlyList<RightCell> Cells, bool Risky);

public sealed record SubjectRights(RbacSubject Subject, IReadOnlyList<RbacPath> Paths, IReadOnlyList<string> Namespaces, IReadOnlyList<string> Notes);

/// <summary>Which of the four RBAC kinds could be read. A view built from fewer than four says so instead of drawing a partial graph.</summary>
public sealed record RbacKindStatus(string Kind, bool Loaded, string? Error);

public static class RbacJson
{
    public static List<string> Strings(JsonNode? n) => (n as JsonArray ?? []).Select(x => x?.ToString() ?? "").ToList();
}
