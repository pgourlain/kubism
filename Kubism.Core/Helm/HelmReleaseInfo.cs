using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Helm;

/// <summary>
/// What the relation graph needs to know about a Helm release without decoding it: name, namespace, latest revision and status, all
/// readable from the labels of the release secrets (<c>sh.helm.release.v1.&lt;name&gt;.vN</c>).
/// </summary>
public sealed record HelmReleaseInfo(string Name, string Namespace, int Revision, string Status)
{
    public const string SecretType = "helm.sh/release.v1";
    public const string Kind = "HelmRelease";

    public static bool IsReleaseSecret(KubeObject o) => o.Kind == "Secret" && o.StringAt("type") == SecretType;

    /// <summary>Latest revision of each release found in the given secrets.</summary>
    public static IReadOnlyList<HelmReleaseInfo> FromSecrets(IEnumerable<KubeObject> secrets) =>
        secrets.Where(IsReleaseSecret)
            .Select(s => (s, name: s.Labels.GetValueOrDefault("name"), rev: int.TryParse(s.Labels.GetValueOrDefault("version"), out var v) ? v : 0))
            .Where(x => x.name is not null && x.s.Namespace is not null)
            .GroupBy(x => (ns: x.s.Namespace!, x.name))
            .Select(g => g.OrderByDescending(x => x.rev).First())
            .Select(x => new HelmReleaseInfo(x.name!, x.s.Namespace!, x.rev, x.s.Labels.GetValueOrDefault("status") ?? ""))
            .OrderBy(r => r.Namespace, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

    /// <summary>The release that rendered an object: Helm stamps <c>meta.helm.sh/release-name</c> (and namespace) on everything it installs.</summary>
    public static (string Namespace, string Name)? OfObject(KubeObject o)
    {
        if (o.Kind == Kind) return null;
        if (o.Annotations.TryGetValue("meta.helm.sh/release-name", out var name) && name.Length > 0)
            return (o.Annotations.GetValueOrDefault("meta.helm.sh/release-namespace") ?? o.Namespace ?? "", name);
        // Older charts and some tools only set the standard labels.
        if (o.Labels.GetValueOrDefault("app.kubernetes.io/managed-by") == "Helm" && o.Labels.GetValueOrDefault("app.kubernetes.io/instance") is { Length: > 0 } instance)
            return (o.Namespace ?? "", instance);
        return null;
    }

    /// <summary>A stand-in object, so a release can be a node (or the root) of the graph like any other.</summary>
    public KubeObject ToObject() => new(new JsonObject
    {
        ["apiVersion"] = "helm.sh/v3", ["kind"] = Kind,
        ["metadata"] = new JsonObject { ["name"] = Name, ["namespace"] = Namespace, ["uid"] = $"helm-{Namespace}-{Name}" },
        ["status"] = new JsonObject { ["revision"] = Revision, ["status"] = Status },
    });
}
