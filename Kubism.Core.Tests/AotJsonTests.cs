using System.Text.Json;
using Kubism.Core.Catalog;
using Xunit;

namespace Kubism.Core.Tests;

public class AotJsonTests
{
    [Fact]
    public void Our_types_roundtrip_through_the_source_generated_context()
    {
        var s = new AppSettings { Theme = "Dark", LogBufferLines = 123 };
        s.Clusters["a|b"] = new ClusterMetadata { Label = "x", SafetyMode = SafetyMode.ReadOnly, Tags = ["t"] };
        var json = JsonSerializer.Serialize(s, CoreJson.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, CoreJson.Default.AppSettings)!;
        Assert.Equal("Dark", back.Theme);
        Assert.Equal(SafetyMode.ReadOnly, back.Clusters["a|b"].SafetyMode);
        Assert.Contains("\"readOnly\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_reflection_based_serializer_calls_in_our_projects()
    {
        // Reflection-based System.Text.Json calls take no JsonTypeInfo/context argument and break Native AOT.
        var root = Directory.GetParent(CoreArchitectureTests.FindCoreDir())!.FullName;
        foreach (var project in new[] { "Kubism.Core", "Kubism.Kube", "Kubism.Desktop", "Kubism.Mcp" })
        {
            var dir = Path.Combine(root, project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"JsonSerializer\.(Serialize|Deserialize)\(([^;]*)\)"))
                    Assert.True(m.Groups[2].Value.Contains("CoreJson") || m.Groups[2].Value.Contains("Json.Default") || m.Groups[2].Value.Contains("TypeInfo"),
                        $"{Path.GetFileName(file)}: reflection-based {m.Value}");
            }
        }
    }
}
