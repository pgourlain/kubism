using System.Diagnostics;
using Kubism.Core.RightSizing;

namespace Kubism.Kube;

/// <summary>
/// Entra token for Azure Monitor managed Prometheus, taken from the Azure CLI login (<c>az account get-access-token</c>).
/// ADR 0003: the Azure CLI credential was chosen over reusing kubelogin's token cache, which is private to that tool.
/// </summary>
public sealed class AzureCliTokenProvider : ITokenProvider
{
    private readonly Dictionary<string, (string token, DateTimeOffset expires)> _cache = [];

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        if (_cache.TryGetValue(scope, out var c) && c.expires > DateTimeOffset.UtcNow.AddMinutes(2)) return c.token;
        var az = ShellEnvironment.Which("az") ?? throw new InvalidOperationException("The Azure CLI ('az') was not found on PATH. Run 'az login' first.");
        var resource = scope.EndsWith("/.default", StringComparison.Ordinal) ? scope[..^"/.default".Length] : scope;
        var psi = new ProcessStartInfo(az) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "account", "get-access-token", "--resource", resource, "--query", "{t:accessToken,e:expiresOn}", "-o", "json" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidOperationException($"az failed: {(await stderr.ConfigureAwait(false)).Trim()}");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await stdout.ConfigureAwait(false))!;
        var token = json["t"]!.GetValue<string>();
        var exp = DateTimeOffset.TryParse(json["e"]?.GetValue<string>(), out var e) ? e : DateTimeOffset.UtcNow.AddMinutes(30);
        _cache[scope] = (token, exp);
        return token;
    }
}
