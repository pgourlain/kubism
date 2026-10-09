using Xunit;

namespace Kubism.Kube.Tests;

public class KubeArchitectureTests
{
    [Fact]
    public void Kube_references_no_ui_assembly()
    {
        var refs = typeof(KubeTransport).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        foreach (var f in new[] { "Avalonia", "SkiaSharp", "System.Drawing", "CommunityToolkit.Mvvm" })
            Assert.DoesNotContain(refs, r => r == f || r.StartsWith(f + ".", StringComparison.Ordinal));
    }
}
