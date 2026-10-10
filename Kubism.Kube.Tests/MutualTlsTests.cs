using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Kube;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kubism.Kube.Tests;

/// <summary>
/// An HTTPS API server that requires a client certificate, like AKS with local accounts: the kubeconfig has a client certificate issued by the cluster's own CA
/// (which is in no system store) and a static token. Both must reach the server, and the server must be checked against the kubeconfig's CA.
/// </summary>
public sealed class MutualTlsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _dir = "";
    private X509Certificate2 _ca = null!, _client = null!;
    private RSA _clientKey = null!;
    private string _url = "";
    private int _requests;
    private string? _seenClient, _seenAuth;

    private static X509Certificate2 Issue(string subject, X509Certificate2 ca, bool server, RSA key)
    {
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
        }
        using var issued = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 NewCa(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(365));
    }

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kubism-mtls-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _ca = NewCa("test-cluster-ca");
        using var serverKey = RSA.Create(2048);
        var server = X509CertificateLoader.LoadPkcs12(Issue("CN=localhost", _ca, true, serverKey).Export(X509ContentType.Pkcs12), (string?)null);
        _clientKey = RSA.Create(2048);
        _client = Issue("CN=masterclient, O=system:masters", _ca, false, _clientKey);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(h =>
        {
            h.ServerCertificate = server;
            h.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            h.ClientCertificateValidation = (cert, _, _) => cert.Issuer == _ca.Subject;
        })));
        _app = builder.Build();
        _app.MapGet("/version", (HttpContext c) =>
        {
            Interlocked.Increment(ref _requests);
            _seenClient = c.Connection.ClientCertificate?.Subject;
            _seenAuth = c.Request.Headers.Authorization.ToString();
            return Results.Text("""{"gitVersion":"v1.31.0-mtls"}""", "application/json");
        });
        await _app.StartAsync();
        _url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("[::1]", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _ca.Dispose(); _client.Dispose(); _clientKey.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private ClusterEntry Kubeconfig(X509Certificate2 trustedCa, bool skipVerify = false, bool withCert = true)
    {
        var user = withCert
            ? $"client-certificate-data: {B64(_client.ExportCertificatePem())}\n      client-key-data: {B64(_clientKey.ExportRSAPrivateKeyPem())}\n      token: static-token"
            : "token: static-token";
        var path = Path.Combine(_dir, "config");
        var yaml = string.Join("\n",
            "apiVersion: v1", "kind: Config",
            "clusters:", "- name: c", "  cluster:", $"    server: {_url}", $"    certificate-authority-data: {B64(trustedCa.ExportCertificatePem())}", $"    insecure-skip-tls-verify: {(skipVerify ? "true" : "false")}",
            "users:", "- name: u", "  user:", $"    {user.Replace("\n      ", "\n    ")}",
            "contexts:", "- name: ctx", "  context: {cluster: c, user: u}", "current-context: ctx");
        File.WriteAllText(path, yaml);
        return KubeconfigCatalog.ParseFile(path)[0];
    }

    [Fact]
    public async Task The_client_certificate_and_the_static_token_both_reach_the_server_over_https()
    {
        using var t = new KubeTransport(Kubeconfig(_ca));
        var v = await t.GetAsync("/version", TestContext.Current.CancellationToken);
        Assert.Equal("v1.31.0-mtls", v!["gitVersion"]!.GetValue<string>());
        Assert.Contains("masterclient", _seenClient);                  // the certificate was offered although its issuer is in no store
        Assert.Equal("Bearer static-token", _seenAuth);                // and the token is sent too, as kubectl does
    }

    [Fact]
    public async Task A_server_not_signed_by_the_kubeconfig_ca_is_refused_unless_tls_verification_is_skipped()
    {
        using var other = NewCa("someone-else");
        using (var t = new KubeTransport(Kubeconfig(other)))
            await Assert.ThrowsAsync<KubeConnectionException>(() => t.GetAsync("/version", TestContext.Current.CancellationToken));
        Assert.Equal(0, _requests);
        using var skip = new KubeTransport(Kubeconfig(other, skipVerify: true));
        Assert.NotNull(await skip.GetAsync("/version", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_client_certificate_the_server_that_requires_one_cannot_be_reached()
    {
        using var t = new KubeTransport(Kubeconfig(_ca, withCert: false));
        await Assert.ThrowsAsync<KubeConnectionException>(() => t.GetAsync("/version", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Client_certificates_load_from_rsa_and_ec_keys_with_the_private_key()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=ec-client", ec, HashAlgorithmName.SHA256);
        using var issued = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cfg = new k8s.KubernetesClientConfiguration { ClientCertificateData = B64(issued.ExportCertificatePem()), ClientCertificateKeyData = B64(ec.ExportECPrivateKeyPem()) };
        using var loaded = KubeTls.LoadClientCertificate(cfg)!;
        Assert.True(loaded.HasPrivateKey);
        Assert.Contains("ec-client", loaded.Subject);
        using var rsa = KubeTls.LoadClientCertificate(new k8s.KubernetesClientConfiguration { ClientCertificateData = B64(_client.ExportCertificatePem()), ClientCertificateKeyData = B64(_clientKey.ExportPkcs8PrivateKeyPem()) })!;
        Assert.True(rsa.HasPrivateKey);
        Assert.Null(KubeTls.LoadClientCertificate(new k8s.KubernetesClientConfiguration()));
        Assert.Throws<KubeConnectionException>(() => KubeTls.LoadClientCertificate(new k8s.KubernetesClientConfiguration { ClientCertificateData = B64("not a certificate"), ClientCertificateKeyData = B64("nor a key") }));
    }
}
