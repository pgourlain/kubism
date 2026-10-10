using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using k8s;
using Kubism.Core;
using Kubism.Core.Api;

namespace Kubism.Kube;

/// <summary>
/// The HTTP handler of one cluster connection, built here and not by KubernetesClient: the library applies tokens only to its own typed calls (never to the raw HTTP
/// client Kubism uses), and leaves the choice of the TLS client certificate to the operating system. On Windows that choice filters by the issuers the server lists,
/// and a kubeconfig's client certificate is issued by the cluster's own CA, which is not in the Windows store: the certificate is then silently not sent and the
/// server answers 401 to everything. Like kubectl, the certificate is always offered.
/// </summary>
public static class KubeTls
{
    /// <summary>The client certificate with its private key, or null when the kubeconfig has none. Throws <see cref="KubeConnectionException"/> when it cannot be read.</summary>
    public static X509Certificate2? LoadClientCertificate(KubernetesClientConfiguration c)
    {
        if (c.ClientCertificateData is null && c.ClientCertificateFilePath is null) return null;
        try
        {
            var certPem = c.ClientCertificateData is { } d ? Encoding.UTF8.GetString(Convert.FromBase64String(d)) : File.ReadAllText(c.ClientCertificateFilePath!);
            var keyPem = c.ClientCertificateKeyData is { } k ? Encoding.UTF8.GetString(Convert.FromBase64String(k)) : c.ClientKeyFilePath is { } kf ? File.ReadAllText(kf) : throw new KubeConnectionException("The kubeconfig has a client certificate but no client key");
            var cert = X509Certificate2.CreateFromPem(certPem, keyPem);
            // A certificate built from PEM has an ephemeral key, which Windows' TLS stack cannot use for client authentication: go through PKCS#12 (the key is
            // then a temporary one, deleted with the certificate: nothing is left in the user's key store).
            if (!OperatingSystem.IsWindows()) return cert;
            using (cert) return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.DefaultKeySet);
        }
        catch (Exception ex) when (ex is not KubeConnectionException and (FormatException or System.Security.Cryptography.CryptographicException or IOException or ArgumentException))
        {
            throw new KubeConnectionException($"Cannot read the client certificate of the kubeconfig: {ex.Message}", ex);
        }
    }

    internal static SocketsHttpHandler CreateHandler(KubernetesClientConfiguration config, X509Certificate2? clientCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            // Many watches share a connection (100 streams): more connections when it is full instead of waiting.
            EnableMultipleHttp2Connections = true,
        };
        var ssl = handler.SslOptions;
        if (!string.IsNullOrEmpty(config.TlsServerName)) ssl.TargetHost = config.TlsServerName;
        if (clientCertificate is not null)
        {
            ssl.ClientCertificates = [clientCertificate];
            // Bypass the issuer filtering: always offer the certificate, as kubectl does. The log says what the server asked for.
            ssl.LocalCertificateSelectionCallback = (_, host, _, _, acceptableIssuers) =>
            {
                AppLog.Info($"TLS: {host} asks for a client certificate (it accepts {acceptableIssuers.Length} issuer(s)); sending {clientCertificate.Subject}");
                return clientCertificate;
            };
        }
        var caCerts = config.SslCaCerts;
        ssl.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
        {
            var ok = config.SkipTlsVerify
                     || (caCerts is { Count: > 0 } ? Kubernetes.CertificateValidationCallBack(sender, caCerts, cert!, chain!, errors) : errors == SslPolicyErrors.None);
            if (!ok) AppLog.Warn($"TLS: the server certificate of {(sender as SslStream)?.TargetHostName} was refused: {errors}{(caCerts is { Count: > 0 } ? " (checked against the kubeconfig's CA)" : " (no CA in the kubeconfig: checked against the system store)")}");
            return ok;
        };
        return handler;
    }
}
