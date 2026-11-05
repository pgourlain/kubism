using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Images;

/// <summary>An image reference split the way the container runtime reads it: <c>[registry/]repository[:tag][@digest]</c>.</summary>
public sealed record ImageRef(string Raw, string Registry, string Repository, string? Tag, string? Digest)
{
    /// <summary>Registry plus repository: what stays the same from one version to the next.</summary>
    public string Name => $"{Registry}/{Repository}";

    /// <summary>The version as written: the tag, the digest, or "latest" when neither is given (what the runtime pulls).</summary>
    public string Version => Tag ?? (Digest is null ? "latest" : ShortDigest(Digest));

    public static ImageRef Parse(string raw)
    {
        var rest = raw.Trim();
        string? digest = null;
        var at = rest.IndexOf('@');
        if (at >= 0) { digest = rest[(at + 1)..]; rest = rest[..at]; }

        string registry;
        var slash = rest.IndexOf('/');
        if (slash > 0 && IsRegistryHost(rest[..slash])) { registry = rest[..slash]; rest = rest[(slash + 1)..]; }
        else registry = "docker.io";

        string? tag = null;
        var colon = rest.LastIndexOf(':');
        if (colon > rest.LastIndexOf('/')) { tag = rest[(colon + 1)..]; rest = rest[..colon]; }

        // docker.io official images live under "library/".
        if (registry == "docker.io" && !rest.Contains('/')) rest = "library/" + rest;
        return new ImageRef(raw.Trim(), registry, rest, tag is { Length: > 0 } ? tag : null, digest);
    }

    private static bool IsRegistryHost(string first) => first.Contains('.') || first.Contains(':') || first == "localhost";

    public static string ShortDigest(string digest)
    {
        var colon = digest.IndexOf(':');
        var hex = colon >= 0 ? digest[(colon + 1)..] : digest;
        return (colon >= 0 ? digest[..(colon + 1)] : "") + (hex.Length > 12 ? hex[..12] : hex);
    }
}

[Flags]
public enum ImageIssues
{
    None = 0,
    /// <summary>Tag <c>latest</c>, or no tag and no digest: what runs depends on when the node pulled it.</summary>
    Latest = 1,
    /// <summary>No digest in the spec: the same reference can resolve to different content over time.</summary>
    NotPinned = 2,
    /// <summary>A container waits with ImagePullBackOff, ErrImagePull or InvalidImageName.</summary>
    PullFailing = 4,
    /// <summary>The same repository runs with more than one version in the selected scope.</summary>
    MixedVersions = 8,
}

/// <summary>One container of one workload that uses an image.</summary>
/// <param name="Role">"container", "init" or "ephemeral".</param>
public sealed record ImageUser(string Kind, string? Namespace, string Name, string Container, string Role, string? PullPolicy, int Pods, int PullFailures)
{
    public string Display => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
}

public sealed record ImageRow(ImageRef Image, IReadOnlyList<ImageUser> Users, IReadOnlyList<string> ResolvedDigests, int Versions, ImageIssues Issues)
{
    public string Reference => Image.Raw;
    public int Pods => Users.Sum(u => u.Pods);
    public int Workloads => Users.Select(u => (u.Kind, u.Namespace, u.Name)).Distinct().Count();
    public IReadOnlyList<string> Namespaces => Users.Select(u => u.Namespace ?? "").Where(n => n.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
    public int PullFailures => Users.Sum(u => u.PullFailures);
    public IReadOnlyList<string> PullPolicies => Users.Select(u => u.PullPolicy).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Which images the cluster runs, read from the pods (what actually runs, with the digest the node resolved) and not from the workload
/// templates. Pure: it takes objects already listed. Read-only; environment values and pull secrets are never looked at.
/// </summary>
public static class ImageInventory
{
    private static readonly HashSet<string> PullErrors = ["ImagePullBackOff", "ErrImagePull", "InvalidImageName", "ErrImageNeverPull", "RegistryUnavailable"];

    /// <param name="replicaSets">Optional: resolves Pod → ReplicaSet → Deployment exactly. Without it the pod-template hash is stripped from the name.</param>
    /// <param name="jobs">Optional: resolves Pod → Job → CronJob exactly.</param>
    public static IReadOnlyList<ImageRow> Analyze(IEnumerable<KubeObject> pods, IEnumerable<KubeObject>? replicaSets = null, IEnumerable<KubeObject>? jobs = null)
    {
        var owners = new Dictionary<(string kind, string? ns, string name), (string kind, string name)>();
        foreach (var o in (replicaSets ?? []).Concat(jobs ?? []))
            if (o.OwnerReferences.FirstOrDefault(r => r.Controller) is { } c) owners[(o.Kind, o.Namespace, o.Name)] = (c.Kind, c.Name);

        // image reference -> (workload, container, role) -> counters
        var byImage = new Dictionary<string, Dictionary<(string, string?, string, string, string), Acc>>(StringComparer.Ordinal);
        var digests = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var pod in pods)
        {
            if (pod.Kind != "Pod" && pod.Kind.Length > 0) continue;
            var (wk, wn) = Workload(pod, owners);
            var statuses = new Dictionary<(string, string), (string? imageId, bool failing)>();
            foreach (var (list, role) in new[] { ("containerStatuses", "container"), ("initContainerStatuses", "init"), ("ephemeralContainerStatuses", "ephemeral") })
                foreach (var s in (pod.At("status", list) as JsonArray ?? []).OfType<JsonObject>())
                {
                    var name = KubeObject.Str(s, "name") ?? "";
                    var waiting = (s["state"] as JsonObject)?["waiting"] as JsonObject;
                    var reason = waiting is null ? null : KubeObject.Str(waiting, "reason");
                    statuses[(role, name)] = (KubeObject.Str(s, "imageID"), reason is not null && PullErrors.Contains(reason));
                }

            foreach (var (list, role) in new[] { ("containers", "container"), ("initContainers", "init"), ("ephemeralContainers", "ephemeral") })
                foreach (var c in (pod.At("spec", list) as JsonArray ?? []).OfType<JsonObject>())
                {
                    var image = KubeObject.Str(c, "image");
                    if (string.IsNullOrWhiteSpace(image)) continue;
                    var cname = KubeObject.Str(c, "name") ?? "";
                    if (!byImage.TryGetValue(image, out var users)) byImage[image] = users = [];
                    var key = (wk, pod.Namespace, wn, cname, role);
                    if (!users.TryGetValue(key, out var acc)) users[key] = acc = new Acc(KubeObject.Str(c, "imagePullPolicy"));
                    acc.Pods++;
                    if (statuses.TryGetValue((role, cname), out var st))
                    {
                        if (st.failing) acc.Failures++;
                        if (ResolvedDigest(st.imageId) is { } d)
                        {
                            if (!digests.TryGetValue(image, out var set)) digests[image] = set = [];
                            set.Add(d);
                        }
                    }
                }
        }

        var parsed = byImage.Keys.ToDictionary(k => k, ImageRef.Parse);
        var versionsOf = parsed.Values.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Select(r => r.Version).Distinct().Count());

        var rows = new List<ImageRow>();
        foreach (var (raw, users) in byImage)
        {
            var img = parsed[raw];
            var list = users.Select(kv => new ImageUser(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Key.Item5, kv.Value.Policy, kv.Value.Pods, kv.Value.Failures))
                .OrderBy(u => u.Namespace, StringComparer.Ordinal).ThenBy(u => u.Name, StringComparer.Ordinal).ThenBy(u => u.Container, StringComparer.Ordinal).ToList();
            var issues = ImageIssues.None;
            if (img.Tag is null && img.Digest is null || img.Tag == "latest") issues |= ImageIssues.Latest;
            if (img.Digest is null) issues |= ImageIssues.NotPinned;
            if (list.Any(u => u.PullFailures > 0)) issues |= ImageIssues.PullFailing;
            var versions = versionsOf[img.Name];
            if (versions > 1) issues |= ImageIssues.MixedVersions;
            rows.Add(new ImageRow(img, list, digests.TryGetValue(raw, out var d) ? d.Order(StringComparer.Ordinal).ToList() : [], versions, issues));
        }
        return rows.OrderBy(r => r.Image.Name, StringComparer.Ordinal).ThenBy(r => r.Image.Version, StringComparer.Ordinal).ToList();
    }

    /// <summary>"docker-pullable://repo@sha256:abc" or "repo@sha256:abc" → "sha256:abc".</summary>
    private static string? ResolvedDigest(string? imageId)
    {
        if (string.IsNullOrEmpty(imageId)) return null;
        var at = imageId.IndexOf('@');
        if (at >= 0) return imageId[(at + 1)..];
        var sha = imageId.IndexOf("sha256:", StringComparison.Ordinal);
        return sha >= 0 ? imageId[sha..] : null;
    }

    private static (string kind, string name) Workload(KubeObject pod, Dictionary<(string kind, string? ns, string name), (string kind, string name)> owners)
    {
        var owner = pod.OwnerReferences.FirstOrDefault(r => r.Controller) ?? pod.OwnerReferences.FirstOrDefault();
        if (owner is null) return ("Pod", pod.Name);
        if (owners.TryGetValue((owner.Kind, pod.Namespace, owner.Name), out var up)) return up;
        // No ReplicaSet/Job list: "web-6d4cf56db6" belongs to Deployment "web", "report-28391" to CronJob "report".
        if (owner.Kind is "ReplicaSet" or "Job")
        {
            var dash = owner.Name.LastIndexOf('-');
            if (dash > 0) return (owner.Kind == "ReplicaSet" ? "Deployment" : "CronJob", owner.Name[..dash]);
        }
        return (owner.Kind, owner.Name);
    }

    private sealed class Acc(string? policy)
    {
        public string? Policy { get; } = policy;
        public int Pods;
        public int Failures;
    }
}
