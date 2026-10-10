using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Discovery;

namespace Kubism.Core.Views;

/// <summary>
/// Global search syntax, tokens separated by spaces and combined with AND:
/// <c>text</c> (name contains), <c>key=value</c> (label selector), <c>image:nginx</c> (a container image contains),
/// <c>ns:staging</c> (namespace), <c>kind:deploy</c> (kind, plural or short name).
/// </summary>
public sealed class SearchQuery
{
    public IReadOnlyList<string> Names { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> Images { get; init; } = [];
    public string? Namespace { get; init; }
    public string? Kind { get; init; }
    public bool IsEmpty => Names.Count == 0 && Labels.Count == 0 && Images.Count == 0 && Namespace is null && Kind is null;
    public string? LabelSelector => Labels.Count == 0 ? null : string.Join(',', Labels);

    /// <summary>Kinds that carry container images.</summary>
    public static readonly string[] ImageResources = ["pods", "deployments", "statefulsets", "daemonsets", "replicasets", "jobs", "cronjobs"];

    public static SearchQuery Parse(string text)
    {
        var names = new List<string>(); var labels = new List<string>(); var images = new List<string>();
        string? ns = null, kind = null;
        foreach (var t in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.StartsWith("image:", StringComparison.OrdinalIgnoreCase) && t.Length > 6) images.Add(t[6..]);
            else if (t.StartsWith("ns:", StringComparison.OrdinalIgnoreCase) && t.Length > 3) ns = t[3..];
            else if (t.StartsWith("kind:", StringComparison.OrdinalIgnoreCase) && t.Length > 5) kind = t[5..];
            else if (t.Contains('=')) labels.Add(t);
            else names.Add(t);
        }
        return new SearchQuery { Names = names, Labels = labels, Images = images, Namespace = ns, Kind = kind };
    }

    /// <summary>The resources worth listing for this query.</summary>
    public bool WantsKind(ResourceKind k, IReadOnlyCollection<string> defaults)
    {
        if (Kind is { } wanted)
            return k.Kind.Equals(wanted, StringComparison.OrdinalIgnoreCase) || k.Resource.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || k.ShortNames.Any(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (Images.Count > 0) return ImageResources.Contains(k.Resource);
        return defaults.Contains(k.Resource);
    }

    /// <summary>Labels are filtered by the server; names, images and namespace here.</summary>
    public bool Matches(KubeObject o)
    {
        if (Namespace is not null && !string.Equals(o.Namespace, Namespace, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Names.All(n => o.Name.Contains(n, StringComparison.OrdinalIgnoreCase))) return false;
        if (Images.Count > 0)
        {
            var imgs = ImagesOf(o).ToList();
            if (!Images.All(i => imgs.Any(x => x.Contains(i, StringComparison.OrdinalIgnoreCase)))) return false;
        }
        return true;
    }

    public static IEnumerable<string> ImagesOf(KubeObject o)
    {
        var spec = o.At("spec") as JsonObject;
        var podSpec = o.Kind switch
        {
            "Pod" => spec,
            "CronJob" => spec?["jobTemplate"]?["spec"]?["template"]?["spec"] as JsonObject,
            _ => spec?["template"]?["spec"] as JsonObject,
        };
        if (podSpec is null) yield break;
        foreach (var field in new[] { "initContainers", "containers" })
            foreach (var c in podSpec[field] as JsonArray ?? [])
                if (c?["image"]?.ToString() is { Length: > 0 } img) yield return img;
    }
}
