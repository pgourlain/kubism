using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Kubism.Core;

/// <summary>YAML ⇄ JSON through YamlDotNet's representation model (no reflection, AOT-safe).</summary>
public static class YamlJson
{
    public static IReadOnlyList<JsonNode?> ParseDocuments(string yaml)
    {
        var stream = new YamlStream();
        using (var r = new StringReader(yaml)) stream.Load(r);
        return stream.Documents.Select(d => ToJson(d.RootNode)).ToList();
    }

    public static JsonNode? Parse(string yaml) => ParseDocuments(yaml).FirstOrDefault();

    public static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode m => ToObject(m),
        YamlSequenceNode s => new JsonArray(s.Children.Select(ToJson).ToArray()),
        YamlScalarNode sc => Scalar(sc),
        _ => null,
    };

    private static JsonObject ToObject(YamlMappingNode m)
    {
        var o = new JsonObject();
        foreach (var kv in m.Children) o[((YamlScalarNode)kv.Key).Value ?? ""] = ToJson(kv.Value);
        return o;
    }

    private static JsonNode? Scalar(YamlScalarNode sc)
    {
        var v = sc.Value;
        if (sc.Style != ScalarStyle.Plain) return JsonValue.Create(v ?? "");
        if (v is null || v == "" || v == "~" || v == "null" || v == "Null" || v == "NULL") return null;
        if (v is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (v is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) && !(v.Length > 1 && v[0] == '0')) return l is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)l) : JsonValue.Create(l);
        if (v.Contains('.') && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsInfinity(d) && char.IsDigit(v[^1])) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    public static string ToYaml(JsonNode? node)
    {
        var doc = new YamlDocument(ToYamlNode(node));
        var stream = new YamlStream(doc);
        using var w = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(w, false);
        var text = w.ToString();
        // YamlDotNet ends documents with "...": strip it.
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l != "...").ToArray();
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    private static YamlNode ToYamlNode(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                var m = new YamlMappingNode();
                foreach (var kv in o) m.Add(new YamlScalarNode(kv.Key), ToYamlNode(kv.Value));
                return m;
            case JsonArray a:
                var s = new YamlSequenceNode();
                foreach (var x in a) s.Add(ToYamlNode(x));
                return s;
            case null:
                return new YamlScalarNode("null");
            case JsonValue v:
                if (v.TryGetValue<string>(out var str))
                {
                    var needsQuote = str.Length == 0 || str == "~"
                        || str.ToLowerInvariant() is "true" or "false" or "null" or "yes" or "no" or "on" or "off" or "y" or "n"
                        || double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        || str.StartsWith(' ') || str.EndsWith(' ') || str.Contains(": ") || str.Contains(" #") || str.StartsWith('#')
                        || ":{}[],&*!|>'\"%@`-?".Contains(str[0]);
                    if (str.Contains('\n')) return new YamlScalarNode(str) { Style = ScalarStyle.Literal };
                    return new YamlScalarNode(str) { Style = needsQuote ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain };
                }
                if (v.TryGetValue<bool>(out var b)) return new YamlScalarNode(b ? "true" : "false");
                return new YamlScalarNode(v.ToJsonString());
        }
        return new YamlScalarNode(n.ToJsonString());
    }
}
