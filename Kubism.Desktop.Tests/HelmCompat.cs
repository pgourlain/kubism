using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Kubism.Desktop.Tests;

/// <summary>Builds a Helm 3 release secret payload (base64 → gzip → JSON) for UI tests.</summary>
public static class HelmCompat
{
    public static string Encode(string name, int rev, string? status = null, string? description = null, DateTimeOffset? updated = null)
    {
        var release = new JsonObject
        {
            ["name"] = name, ["namespace"] = "default", ["version"] = rev,
            ["info"] = new JsonObject { ["status"] = status ?? (rev == 2 ? "deployed" : "superseded"), ["last_deployed"] = (updated ?? DateTimeOffset.Parse("2026-03-01T10:00:00Z")).ToString("O"), ["description"] = description ?? "Upgrade complete", ["notes"] = "Thanks" },
            ["chart"] = new JsonObject { ["metadata"] = new JsonObject { ["name"] = "ingress-nginx", ["version"] = "4.11.0", ["appVersion"] = "1.11.0" }, ["values"] = new JsonObject { ["replicas"] = 1 } },
            ["config"] = new JsonObject { ["replicas"] = 3 },
            ["manifest"] = "---\nkind: Deployment\n",
        };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(Encoding.UTF8.GetBytes(release.ToJsonString()));
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToBase64String(ms.ToArray())));
    }
}
