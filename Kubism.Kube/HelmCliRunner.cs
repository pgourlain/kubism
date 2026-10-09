using System.Diagnostics;
using Kubism.Core.Catalog;
using Kubism.Core.Helm;

namespace Kubism.Kube;

/// <summary>Runs the helm binary (write operations only) against the cluster's kubeconfig and context.</summary>
public sealed class HelmCliRunner(ClusterEntry entry) : IHelmRunner
{
    public bool IsAvailable => ShellEnvironment.Which("helm") is not null;

    public async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = ShellEnvironment.Which("helm") ?? throw new InvalidOperationException("helm was not found on PATH");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--kubeconfig"); psi.ArgumentList.Add(entry.FilePath);
        psi.ArgumentList.Add("--kube-context"); psi.ArgumentList.Add(entry.ContextName);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var err = await stderr.ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidOperationException($"helm failed (exit {p.ExitCode}): {err.Trim()}");
        return await stdout.ConfigureAwait(false);
    }
}

public sealed class HelmCliRunnerFactory : IHelmRunnerFactory
{
    public IHelmRunner Create(ClusterEntry entry) => new HelmCliRunner(entry);
}
