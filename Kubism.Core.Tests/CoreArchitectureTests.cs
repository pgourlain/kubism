using System.Reflection;
using Kubism.Core;
using Xunit;

namespace Kubism.Core.Tests;

public class CoreArchitectureTests
{
    private static readonly string[] Forbidden = ["Avalonia", "SkiaSharp", "System.Drawing", "KubernetesClient", "k8s", "ModelContextProtocol", "System.Windows"];

    [Fact]
    public void Core_references_no_ui_or_client_assembly()
    {
        var refs = typeof(AppLog).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        foreach (var f in Forbidden)
            Assert.DoesNotContain(refs, r => r == f || r.StartsWith(f + ".", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_source_has_no_platform_directives()
    {
        var dir = FindCoreDir();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("#if WINDOWS", text);
            Assert.DoesNotContain("DllImport", text);
            Assert.DoesNotContain("using Avalonia", text);
        }
    }

    [Fact]
    public void Mutating_requests_only_go_through_ClusterActions()
    {
        // Rule 2: no SendAsync with a mutating verb outside ClusterActions. The access reviews (SelfSubjectAccessReview, SelfSubjectReview,
        // SubjectAccessReview) are POSTs that create nothing: AccessReview and RbacVerifier ask questions only.
        var root = Directory.GetParent(FindCoreDir())!.FullName;
        var allowed = new[] { "ClusterActions.cs", "AccessReview.cs", "RbacVerifier.cs", "SessionHealth" };
        foreach (var project in new[] { "Kubism.Core", "Kubism.Desktop", "Kubism.Mcp" })
        {
            var dir = Path.Combine(root, project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                if (allowed.Any(a => file.EndsWith(a, StringComparison.Ordinal))) continue;
                var text = File.ReadAllText(file);
                foreach (var verb in new[] { "\"POST\"", "\"PUT\"", "\"PATCH\"", "\"DELETE\"" })
                    Assert.False(text.Contains($"SendAsync({verb}"), $"{file} sends {verb} outside ClusterActions");
            }
        }
    }

    internal static string FindCoreDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Kubism.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "Kubism.Core");
    }
}
