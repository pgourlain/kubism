using System.Text.Json.Nodes;
using Xunit;

namespace Kubism.Core.Tests;

public class UtilTests
{
    [Theory]
    [InlineData(5, "5s")]
    [InlineData(119, "119s")]
    [InlineData(125, "2m5s")]
    [InlineData(600, "10m")]
    [InlineData(3 * 3600 + 12 * 60, "3h12m")]
    [InlineData(12 * 3600, "12h")]
    [InlineData(4 * 86400, "4d")]
    [InlineData(100 * 86400, "100d")]
    public void Age_formats_like_kubectl(int seconds, string expected) => Assert.Equal(expected, KubeTime.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Yaml_roundtrip_keeps_types()
    {
        var yaml = "a: 1\nb: \"2\"\nc: true\nd: [x, y]\ne:\n  f: 1.5\ng: null\nh: |\n  line1\n  line2\n";
        var json = (JsonObject)YamlJson.Parse(yaml)!;
        Assert.Equal(1, json["a"]!.GetValue<int>());
        Assert.Equal("2", json["b"]!.GetValue<string>());
        Assert.True(json["c"]!.GetValue<bool>());
        Assert.Equal("line1\nline2\n", json["h"]!.GetValue<string>());
        var back = (JsonObject)YamlJson.Parse(YamlJson.ToYaml(json))!;
        Assert.Equal(json.ToJsonString(), back.ToJsonString());
    }

    [Fact]
    public void Yaml_quotes_strings_that_look_like_other_types()
    {
        var o = new JsonObject { ["version"] = "1.20", ["flag"] = "true", ["empty"] = "", ["n"] = 5 };
        var yaml = YamlJson.ToYaml(o);
        var back = (JsonObject)YamlJson.Parse(yaml)!;
        Assert.Equal("1.20", back["version"]!.GetValue<string>());
        Assert.Equal("true", back["flag"]!.GetValue<string>());
        Assert.Equal(5, back["n"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    [InlineData("Null")]
    [InlineData("YES")]
    public void Yaml_quotes_capitalised_booleans_so_condition_status_stays_a_string(string value)
    {
        var back = (JsonObject)YamlJson.Parse(YamlJson.ToYaml(new JsonObject { ["status"] = value }))!;
        Assert.Equal(value, back["status"]!.GetValue<string>());
    }

    [Fact]
    public void Multi_document_yaml_is_split()
    {
        var docs = YamlJson.ParseDocuments("a: 1\n---\nb: 2\n");
        Assert.Equal(2, docs.Count);
    }
}
