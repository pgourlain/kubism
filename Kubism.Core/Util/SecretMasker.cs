using System.Text;
using System.Text.Json.Nodes;

namespace Kubism.Core;

/// <summary>Secret.data values are never shown, logged or snapshotted unless one key is revealed explicitly.</summary>
public static class SecretMasker
{
    public const string Mask = "••••";

    public static bool IsSecret(JsonObject o) => o["kind"]?.ToString() == "Secret" && (o["apiVersion"]?.ToString() ?? "v1") == "v1";

    /// <summary>Returns a masked deep copy (data, stringData and the last-applied annotation, which embeds the values). Non-secrets are returned as a copy.</summary>
    public static JsonObject MaskedCopy(JsonObject o, string? revealKey = null)
    {
        var copy = (JsonObject)o.DeepClone();
        if (!IsSecret(copy)) return copy;
        foreach (var field in new[] { "data", "stringData" })
            if (copy[field] is JsonObject d)
                foreach (var k in d.Select(kv => kv.Key).ToList())
                    if (k != revealKey) d[k] = Mask;
        if (copy["metadata"]?["annotations"] is JsonObject ann && ann.ContainsKey("kubectl.kubernetes.io/last-applied-configuration"))
            ann["kubectl.kubernetes.io/last-applied-configuration"] = Mask;
        return copy;
    }

    /// <summary>Decoded value of one key (explicit user action).</summary>
    public static string? Reveal(JsonObject secret, string key)
    {
        var v = secret["data"]?[key]?.ToString();
        if (v is null) return secret["stringData"]?[key]?.ToString();
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(v)); } catch (FormatException) { return v; }
    }

    public static IReadOnlyList<string> Keys(JsonObject secret) =>
        [.. (secret["data"] as JsonObject)?.Select(kv => kv.Key) ?? [], .. (secret["stringData"] as JsonObject)?.Select(kv => kv.Key) ?? []];

    /// <summary>Removes managedFields for display.</summary>
    public static JsonObject WithoutManagedFields(JsonObject o)
    {
        var copy = (JsonObject)o.DeepClone();
        (copy["metadata"] as JsonObject)?.Remove("managedFields");
        return copy;
    }
}
