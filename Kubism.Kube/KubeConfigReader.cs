using System.Security.Cryptography.X509Certificates;
using System.Text;
using k8s;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using YamlDotNet.RepresentationModel;

namespace Kubism.Kube;

/// <summary>
/// Reads one context of a kubeconfig with YamlDotNet's representation model (no reflection, Native AOT-safe) and
/// builds the <see cref="KubernetesClientConfiguration"/>. The client library's own loader deserializes into types
/// by reflection, which fails under Native AOT.
/// </summary>
public static class KubeConfigReader
{
    public sealed record Result(KubernetesClientConfiguration Config, ExecSpec? Exec);

    public static Result Read(ClusterEntry entry)
    {
        YamlMappingNode root;
        try
        {
            var yaml = new YamlStream();
            using (var r = new StringReader(File.ReadAllText(entry.FilePath))) yaml.Load(r);
            root = (YamlMappingNode)yaml.Documents[0].RootNode;
        }
        catch (Exception ex) { throw new KubeConnectionException($"Cannot read kubeconfig {entry.FilePath}: {ex.Message}", ex); }

        var dir = Path.GetDirectoryName(Path.GetFullPath(entry.FilePath))!;
        var ctx = Sub(Named(root, "contexts", entry.ContextName), "context")
                  ?? throw new KubeConnectionException($"Context '{entry.ContextName}' not found in {entry.FilePath}");
        var cluster = Sub(Named(root, "clusters", S(ctx, "cluster") ?? ""), "cluster")
                      ?? throw new KubeConnectionException($"Cluster '{S(ctx, "cluster")}' of context '{entry.ContextName}' not found");
        var user = Sub(Named(root, "users", S(ctx, "user") ?? ""), "user");

        var config = new KubernetesClientConfiguration
        {
            Host = S(cluster, "server") ?? throw new KubeConnectionException($"Cluster '{S(ctx, "cluster")}' has no server"),
            SkipTlsVerify = string.Equals(S(cluster, "insecure-skip-tls-verify"), "true", StringComparison.OrdinalIgnoreCase),
            TlsServerName = S(cluster, "tls-server-name"),
            Namespace = S(ctx, "namespace"),
        };
        var caData = S(cluster, "certificate-authority-data");
        var caFile = S(cluster, "certificate-authority");
        if (caData is not null || caFile is not null)
        {
            var pem = caData is not null ? Encoding.UTF8.GetString(Convert.FromBase64String(caData)) : File.ReadAllText(Abs(dir, caFile!));
            var certs = new X509Certificate2Collection();
            certs.ImportFromPem(pem);
            config.SslCaCerts = certs;
        }

        ExecSpec? exec = null;
        if (user is not null)
        {
            config.AccessToken = S(user, "token");
            if (S(user, "tokenFile") is { } tf && config.AccessToken is null) config.AccessToken = File.ReadAllText(Abs(dir, tf)).Trim();
            config.ClientCertificateData = S(user, "client-certificate-data");
            config.ClientCertificateKeyData = S(user, "client-key-data");
            if (S(user, "client-certificate") is { } cf) config.ClientCertificateFilePath = Abs(dir, cf);
            if (S(user, "client-key") is { } kf) config.ClientKeyFilePath = Abs(dir, kf);
            config.Username = S(user, "username");
            config.Password = S(user, "password");
            if (Get(user, "exec") is YamlMappingNode e)
            {
                var args = (Get(e, "args") as YamlSequenceNode)?.OfType<YamlScalarNode>().Select(a => a.Value ?? "").ToList() ?? [];
                var env = new Dictionary<string, string>();
                if (Get(e, "env") is YamlSequenceNode envs)
                    foreach (var item in envs.OfType<YamlMappingNode>())
                        if (S(item, "name") is { } n) env[n] = S(item, "value") ?? "";
                var command = S(e, "command") ?? throw new KubeConnectionException("exec plugin has no command");
                // A relative command path is relative to the kubeconfig directory (as kubectl does for paths with a separator).
                if (command.Contains('/') && !Path.IsPathRooted(command)) command = Abs(dir, command);
                exec = new ExecSpec(command, args, env, S(e, "apiVersion"), true);
                // The library refuses a context without credentials; our handler supplies the real token.
                config.AccessToken = "exec-plugin";
            }
        }
        AppLog.Info($"Kubeconfig {entry.FilePath}, context {entry.ContextName}, user '{S(ctx, "user")}': keys {Keys(user)}; {DescribeToken(config.AccessToken, exec is not null)}");
        return new Result(config, exec);
    }

    private static string Keys(YamlMappingNode? user) =>
        user is null ? "(no user)" : string.Join(",", user.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value));

    /// <summary>Length and, for a JWT, the non-secret claims that explain a 401 (expiry, audience, issuer). Never the token itself.</summary>
    internal static string DescribeToken(string? token, bool viaExec)
    {
        if (viaExec) return "token from exec plugin";
        if (string.IsNullOrEmpty(token)) return "no token";
        var parts = token.Split('.');
        if (parts.Length != 3) return $"static token, {token.Length} chars, not a JWT";
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(payload)) as System.Text.Json.Nodes.JsonObject;
            var exp = json?["exp"]?.GetValue<long>() is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : (DateTimeOffset?)null;
            var state = exp is null ? "no exp" : exp < DateTimeOffset.UtcNow ? $"EXPIRED at {exp:O}" : $"expires {exp:O}";
            return $"static JWT, {token.Length} chars, {state}, aud={json?["aud"]}, iss={json?["iss"]}";
        }
        catch (Exception ex) { return $"static token, {token.Length} chars, JWT payload unreadable ({ex.GetType().Name})"; }
    }

    private static YamlMappingNode? Named(YamlMappingNode root, string key, string name) =>
        (Get(root, key) as YamlSequenceNode)?.OfType<YamlMappingNode>().FirstOrDefault(n => S(n, "name") == name);

    private static YamlMappingNode? Sub(YamlMappingNode? n, string key) => n is null ? null : Get(n, key) as YamlMappingNode;

    private static YamlNode? Get(YamlMappingNode n, string key) => n.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? S(YamlMappingNode n, string key) => Get(n, key) is YamlScalarNode s ? s.Value : null;

    private static string Abs(string dir, string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir, path));
}
