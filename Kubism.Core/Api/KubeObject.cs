using System.Text.Json.Nodes;

namespace Kubism.Core.Api;

/// <summary>Thin wrapper over a JSON Kubernetes object. CRDs behave exactly like built-in kinds.</summary>
public sealed class KubeObject(JsonObject json)
{
    public JsonObject Json { get; } = json;

    public string ApiVersion => Str(Json, "apiVersion") ?? "";
    public string Kind => Str(Json, "kind") ?? "";
    public JsonObject Metadata => Json["metadata"] as JsonObject ?? new JsonObject();
    public string Name => Str(Metadata, "name") ?? "";
    public string? Namespace => Str(Metadata, "namespace");
    public string Uid => Str(Metadata, "uid") ?? $"{Namespace}/{Name}";
    public string? ResourceVersion => Str(Metadata, "resourceVersion");
    public DateTimeOffset? CreationTimestamp => Time(Metadata, "creationTimestamp");
    public DateTimeOffset? DeletionTimestamp => Time(Metadata, "deletionTimestamp");
    public IReadOnlyDictionary<string, string> Labels => StringMap(Metadata["labels"]);
    public IReadOnlyDictionary<string, string> Annotations => StringMap(Metadata["annotations"]);

    public IEnumerable<OwnerReference> OwnerReferences =>
        (Metadata["ownerReferences"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new OwnerReference(Str(o, "apiVersion") ?? "", Str(o, "kind") ?? "", Str(o, "name") ?? "", Str(o, "uid") ?? "",
                o["controller"]?.GetValue<bool>() ?? false));

    public JsonNode? this[string key] => Json[key];

    public JsonNode? At(params string[] path)
    {
        JsonNode? n = Json;
        foreach (var p in path)
        {
            if (n is JsonObject o) n = o[p]; else return null;
        }
        return n;
    }

    public string? StringAt(params string[] path) => At(path) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public int? IntAt(params string[] path) => At(path) is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static DateTimeOffset? Time(JsonObject o, string key) =>
        Str(o, key) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static IReadOnlyDictionary<string, string> StringMap(JsonNode? n)
    {
        var d = new Dictionary<string, string>();
        if (n is JsonObject o)
            foreach (var kv in o) d[kv.Key] = kv.Value?.ToString() ?? "";
        return d;
    }

    public KubeObject Clone() => new((JsonObject)Json.DeepClone());
    public override string ToString() => $"{Kind}/{Namespace}/{Name}";
}

public sealed record OwnerReference(string ApiVersion, string Kind, string Name, string Uid, bool Controller);
