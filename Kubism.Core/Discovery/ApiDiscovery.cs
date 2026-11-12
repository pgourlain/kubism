using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Discovery;

public sealed record ResourceKind(
    string Group, string Version, string Resource, string Kind, bool Namespaced,
    IReadOnlyList<string> Verbs, IReadOnlyList<string> ShortNames, IReadOnlyList<string> Categories,
    IReadOnlyList<string> Subresources)
{
    public Gvr Gvr => new(Group, Version, Resource);
    public bool CanList => Verbs.Contains("list");
    public bool CanWatch => Verbs.Contains("watch");
    public bool Has(string subresource) => Subresources.Contains(subresource);
    public override string ToString() => Group.Length == 0 ? Kind : $"{Kind}.{Group}";
}

public sealed class ApiDiscovery(IKubeTransport transport)
{
    private const string AggregatedAccept = "application/json;g=apidiscovery.k8s.io;v=v2;as=APIGroupDiscoveryList";
    private IReadOnlyList<ResourceKind> _kinds = [];

    public IReadOnlyList<ResourceKind> Kinds => _kinds;
    /// <summary>Groups whose discovery failed (e.g. broken APIService); each reported once.</summary>
    public IReadOnlyList<string> FailedGroups { get; private set; } = [];
    public bool UsedAggregated { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken ct)
    {
        var failed = new List<string>();
        List<ResourceKind>? kinds = null;
        try
        {
            var core = await transport.GetAsync("/api", ct, AggregatedAccept).ConfigureAwait(false);
            if (core is JsonObject co && KubeObject.Str(co, "kind") == "APIGroupDiscoveryList")
            {
                var apis = await transport.GetAsync("/apis", ct, AggregatedAccept).ConfigureAwait(false);
                kinds = [.. ParseAggregated(co), .. ParseAggregated(apis as JsonObject ?? new JsonObject())];
                UsedAggregated = true;
            }
        }
        catch (KubeApiException) { /* fall back to legacy */ }
        kinds ??= await LegacyAsync(failed, ct).ConfigureAwait(false);
        _kinds = kinds;
        FailedGroups = failed;
        Changed?.Invoke();
    }

    public static IEnumerable<ResourceKind> ParseAggregated(JsonObject list)
    {
        foreach (var g in (list["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var group = KubeObject.Str(g["metadata"] as JsonObject ?? new JsonObject(), "name") ?? "";
            // Versions come sorted by priority, the preferred one first. A resource is listed at the first version that serves it: a kind served only by an older
            // version (Gateway API's ReferenceGrant in v1beta1 and TCPRoute in v1alpha2, next to v1 GatewayClass, Gateway and HTTPRoute) is still found.
            var seen = new HashSet<string>();
            foreach (var v in (g["versions"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var version = KubeObject.Str(v, "version") ?? "";
                foreach (var r in (v["resources"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (!seen.Add(KubeObject.Str(r, "resource") ?? "")) continue;
                    var rk = r["responseKind"] as JsonObject;
                    yield return new ResourceKind(group, version, KubeObject.Str(r, "resource") ?? "", KubeObject.Str(rk ?? new JsonObject(), "kind") ?? "",
                        KubeObject.Str(r, "scope") == "Namespaced", Strs(r["verbs"]), Strs(r["shortNames"]), Strs(r["categories"]),
                        (r["subresources"] as JsonArray ?? []).OfType<JsonObject>().Select(s => KubeObject.Str(s, "subresource") ?? "").ToList());
                }
            }
        }
    }

    private async Task<List<ResourceKind>> LegacyAsync(List<string> failed, CancellationToken ct)
    {
        var result = new List<ResourceKind>();
        var groups = new List<(string group, string version)> { ("", "v1") };
        var apiGroups = await transport.GetAsync("/apis", ct).ConfigureAwait(false) as JsonObject;
        foreach (var g in (apiGroups?["groups"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = KubeObject.Str(g, "name") ?? "";
            var pv = g["preferredVersion"] as JsonObject;
            var ver = KubeObject.Str(pv ?? new JsonObject(), "version")
                      ?? KubeObject.Str((g["versions"] as JsonArray)?.FirstOrDefault() as JsonObject ?? new JsonObject(), "version");
            if (ver is not null) groups.Add((name, ver));
            // The other served versions follow the preferred one: they only add the kinds it does not serve.
            foreach (var other in (g["versions"] as JsonArray ?? []).OfType<JsonObject>().Select(x => KubeObject.Str(x, "version")).Where(x => x is not null && x != ver)) groups.Add((name, other!));
        }
        var seenResources = new HashSet<(string, string)>();

        foreach (var (group, version) in groups)
        {
            var path = group.Length == 0 ? $"/api/{version}" : $"/apis/{group}/{version}";
            try
            {
                var rl = await transport.GetAsync(path, ct).ConfigureAwait(false) as JsonObject;
                var all = (rl?["resources"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var subs = all.Where(r => (KubeObject.Str(r, "name") ?? "").Contains('/')).ToList();
                foreach (var r in all.Where(r => !(KubeObject.Str(r, "name") ?? "").Contains('/')))
                {
                    var name = KubeObject.Str(r, "name")!;
                    if (!seenResources.Add((group, name))) continue;
                    var subNames = subs.Select(s => KubeObject.Str(s, "name")!).Where(n => n.StartsWith(name + "/")).Select(n => n[(name.Length + 1)..]).ToList();
                    result.Add(new ResourceKind(group, version, name, KubeObject.Str(r, "kind") ?? "", r["namespaced"]?.GetValue<bool>() ?? false,
                        Strs(r["verbs"]), Strs(r["shortNames"]), Strs(r["categories"]), subNames));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { failed.Add(group.Length == 0 ? version : $"{group}/{version}"); }
        }
        return result;
    }

    private static List<string> Strs(JsonNode? n) => (n as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToList();

    public ResourceKind? Find(Gvr gvr) => _kinds.FirstOrDefault(k => k.Gvr == gvr);

    /// <summary>Find by kind/resource/short name, case-insensitive ("po", "deploy", "Certificate").</summary>
    public ResourceKind? FindByName(string name)
    {
        return _kinds.FirstOrDefault(k => string.Equals(k.Resource, name, StringComparison.OrdinalIgnoreCase))
            ?? _kinds.FirstOrDefault(k => k.ShortNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            ?? _kinds.FirstOrDefault(k => string.Equals(k.Kind, name, StringComparison.OrdinalIgnoreCase));
    }

    public ResourceKind? FindByKind(string apiVersion, string kind)
    {
        var group = apiVersion.Contains('/') ? apiVersion[..apiVersion.IndexOf('/')] : "";
        return _kinds.FirstOrDefault(k => k.Kind == kind && k.Group == group);
    }
}
