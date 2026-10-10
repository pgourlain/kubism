using System.Text.Json.Nodes;

namespace Kubism.Core.Helm;

/// <summary>Helm values arithmetic: the set a user edits is the chart's defaults merged with their overrides, but only the overrides are sent back.</summary>
public static class HelmValues
{
    /// <summary>
    /// The smallest set of overrides that turns <paramref name="defaults"/> into <paramref name="edited"/>: what differs, with nested maps compared key by key
    /// (lists and scalars are replaced whole). A key the user removed from the defaults is set to <c>null</c>, which is how helm deletes a value.
    /// Sending only overrides keeps the release following the chart's defaults for everything else, so a later chart upgrade still brings its new defaults.
    /// </summary>
    public static JsonObject Overrides(JsonObject defaults, JsonObject edited)
    {
        var result = new JsonObject();
        foreach (var (key, value) in edited)
        {
            if (!defaults.TryGetPropertyValue(key, out var baseValue)) { result[key] = value?.DeepClone(); continue; }
            if (value is JsonObject e && baseValue is JsonObject d)
            {
                var nested = Overrides(d, e);
                if (nested.Count > 0) result[key] = nested;
            }
            else if (!JsonNode.DeepEquals(value, baseValue)) result[key] = value?.DeepClone();
        }
        foreach (var (key, _) in defaults)
            if (!edited.ContainsKey(key)) result[key] = null;
        return result;
    }

    /// <summary>Same values, whatever the key order, comments or number spelling: used to tell whether an edit changed anything.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a ?? new JsonObject(), b ?? new JsonObject());
}
