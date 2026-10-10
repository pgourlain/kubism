using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Delivery;

public enum GitOpsTool { ArgoCd, Flux }

public enum GitOpsLevel { Healthy, OutOfSync, Progressing, Degraded, Suspended, Unknown }

/// <summary>One object managed by an application: Argo CD's per-resource sync and health, or an entry of a Flux Kustomization's inventory (no status).</summary>
public sealed record GitOpsResource(string Kind, string? Namespace, string Name, string Sync, string Health, string? Message)
{
    public string Display => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
    public bool IsProblem => Sync == "OutOfSync" || Health is "Degraded" or "Missing";
}

/// <summary>An Argo CD Application or a Flux Kustomization / HelmRelease / source, reduced to what a person wants to see.</summary>
public sealed record GitOpsApp(GitOpsTool Tool, string Kind, Gvr Gvr, string? Namespace, string Name, string Sync, string Health, GitOpsLevel Level,
    string Source, string Destination, string Revision, string Message, bool Suspended, DateTimeOffset? LastChange, IReadOnlyList<GitOpsResource> Resources)
{
    public string Display => Namespace is null ? Name : $"{Namespace}/{Name}";
    public string ToolName => Tool == GitOpsTool.ArgoCd ? "Argo CD" : "Flux";
    public bool IsProblem => Level is GitOpsLevel.Degraded or GitOpsLevel.OutOfSync;
    public int ProblemResources => Resources.Count(r => r.IsProblem);

    public static readonly (string Group, string Resource)[] ArgoKinds = [("argoproj.io", "applications")];
    public static readonly (string Group, string Resource)[] FluxKinds =
    [
        ("kustomize.toolkit.fluxcd.io", "kustomizations"), ("helm.toolkit.fluxcd.io", "helmreleases"),
        ("source.toolkit.fluxcd.io", "gitrepositories"), ("source.toolkit.fluxcd.io", "helmrepositories"), ("source.toolkit.fluxcd.io", "ocirepositories"),
    ];

    public static bool IsGitOpsKind(string group, string resource) =>
        ArgoKinds.Contains((group, resource)) || FluxKinds.Contains((group, resource));

    public static GitOpsApp? From(KubeObject o, Gvr gvr) =>
        gvr.Group == "argoproj.io" && gvr.Resource == "applications" ? FromArgo(o, gvr)
        : gvr.Group.EndsWith(".toolkit.fluxcd.io", StringComparison.Ordinal) ? FromFlux(o, gvr) : null;

    // ---------- Argo CD ----------

    public static GitOpsApp FromArgo(KubeObject o, Gvr gvr)
    {
        var sync = o.StringAt("status", "sync", "status") ?? "Unknown";
        var health = o.StringAt("status", "health", "status") ?? "Unknown";
        var hasError = (o.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().Any(c => c["type"]?.ToString()?.EndsWith("Error", StringComparison.Ordinal) == true);
        var level = hasError && health is "Unknown" or "Healthy" ? GitOpsLevel.Degraded : health switch
        {
            "Degraded" or "Missing" => GitOpsLevel.Degraded,
            "Suspended" => GitOpsLevel.Suspended,
            "Progressing" => GitOpsLevel.Progressing,
            _ when sync == "OutOfSync" => GitOpsLevel.OutOfSync,
            "Healthy" when sync == "Synced" => GitOpsLevel.Healthy,
            _ => GitOpsLevel.Unknown,
        };
        var repo = o.StringAt("spec", "source", "repoURL") ?? (o.At("spec", "sources") as JsonArray)?.FirstOrDefault()?["repoURL"]?.ToString() ?? "";
        var path = o.StringAt("spec", "source", "path") ?? o.StringAt("spec", "source", "chart");
        var dest = o.StringAt("spec", "destination", "name") ?? o.StringAt("spec", "destination", "server") ?? "";
        var destNs = o.StringAt("spec", "destination", "namespace");
        var revision = o.StringAt("status", "sync", "revision") ?? o.StringAt("spec", "source", "targetRevision") ?? "";
        var message = ArgoMessage(o);
        var last = KubeObject.Time((JsonObject?)o.At("status", "operationState") ?? new JsonObject(), "finishedAt") ?? KubeObject.Time((JsonObject?)o.At("status") ?? new JsonObject(), "reconciledAt");
        var resources = (o.At("status", "resources") as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => new GitOpsResource(r["kind"]?.ToString() ?? "", r["namespace"]?.ToString(), r["name"]?.ToString() ?? "", r["status"]?.ToString() ?? "Unknown", r["health"]?["status"]?.ToString() ?? "", r["health"]?["message"]?.ToString()))
            .OrderByDescending(r => r.IsProblem).ThenBy(r => r.Display, StringComparer.Ordinal).ToList();
        return new GitOpsApp(GitOpsTool.ArgoCd, "Application", gvr, o.Namespace, o.Name, sync, health, level,
            path is null ? repo : $"{repo} · {path}", destNs is null ? dest : $"{dest} / {destNs}", Short(revision), message, false, last, resources);
    }

    private static string ArgoMessage(KubeObject o)
    {
        if ((o.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => c["type"]?.ToString()?.EndsWith("Error", StringComparison.Ordinal) == true) is { } err)
            return err["message"]?.ToString() ?? err["type"]?.ToString() ?? "";
        var op = o.StringAt("status", "operationState", "message");
        return op is not null && o.StringAt("status", "operationState", "phase") != "Succeeded" ? op : o.StringAt("status", "health", "message") ?? "";
    }

    // ---------- Flux ----------

    public static GitOpsApp FromFlux(KubeObject o, Gvr gvr)
    {
        var suspended = o.At("spec", "suspend")?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var ready = (o.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => c["type"]?.ToString() == "Ready");
        var status = ready?["status"]?.ToString();
        var (sync, health, level) = suspended ? ("Suspended", "Suspended", GitOpsLevel.Suspended)
            : status switch { "True" => ("Ready", "Healthy", GitOpsLevel.Healthy), "False" => ("Not ready", "Degraded", GitOpsLevel.Degraded), _ => ("Reconciling", "Progressing", GitOpsLevel.Progressing) };
        var revision = o.StringAt("status", "lastAppliedRevision") ?? o.StringAt("status", "artifact", "revision") ?? o.StringAt("status", "lastAttemptedRevision") ?? "";
        var source = o.Kind switch
        {
            "Kustomization" => $"{o.StringAt("spec", "sourceRef", "kind")}/{o.StringAt("spec", "sourceRef", "name")}{(o.StringAt("spec", "path") is { } p ? " · " + p : "")}",
            "HelmRelease" => o.StringAt("spec", "chart", "spec", "chart") is { } chart ? $"{chart}{(o.StringAt("spec", "chart", "spec", "version") is { } v ? "@" + v : "")} from {o.StringAt("spec", "chart", "spec", "sourceRef", "name")}" : o.StringAt("spec", "chartRef", "name") ?? "",
            _ => o.StringAt("spec", "url") ?? "",
        };
        var resources = (o.At("status", "inventory", "entries") as JsonArray ?? []).OfType<JsonObject>().Select(e => ParseInventoryId(e["id"]?.ToString())).Where(r => r is not null).Select(r => r!).OrderBy(r => r.Display, StringComparer.Ordinal).ToList();
        return new GitOpsApp(GitOpsTool.Flux, o.Kind, gvr, o.Namespace, o.Name, sync, health, level, source, "", Short(revision),
            suspended ? "Suspended: reconciliation is paused" : ready?["message"]?.ToString() ?? "", suspended, ready is null ? null : KubeObject.Time(ready, "lastTransitionTime"), resources);
    }

    /// <summary>Flux inventory ids look like <c>namespace_name_group_Kind</c> (the group may be empty).</summary>
    public static GitOpsResource? ParseInventoryId(string? id)
    {
        var parts = id?.Split('_');
        if (parts is not { Length: >= 4 }) return null;
        return new GitOpsResource(parts[^1], parts[0].Length == 0 ? null : parts[0], string.Join('_', parts[1..^2]), "", "", null);
    }

    /// <summary>Commit hashes are cut to 8 characters; Flux's <c>branch@sha1:abcdef…</c> keeps its branch and a short hash.</summary>
    private static string Short(string revision)
    {
        var at = revision.IndexOf('@');
        if (at >= 0)
        {
            var hash = revision[(at + 1)..]; var colon = hash.IndexOf(':');
            return colon >= 0 && hash.Length > colon + 9 ? revision[..(at + 1)] + hash[..(colon + 9)] : revision;
        }
        return revision.Length >= 40 && revision.All(Uri.IsHexDigit) ? revision[..8] : revision;
    }
}
