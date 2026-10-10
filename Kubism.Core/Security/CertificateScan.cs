using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Security;

public enum CertificateHealth { Ok, ExpiresSoon, ExpiresVerySoon, Expired }

/// <param name="Source">Where it was found: "TLS secret" or "cert-manager Certificate".</param>
public sealed record CertificateInfo(string Source, string? Namespace, string Name, string Subject, string Issuer, DateTimeOffset NotAfter, int DaysLeft, CertificateHealth Health, string? Detail = null)
{
    public string Display => Namespace is null ? Name : $"{Namespace}/{Name}";
    public string Expires => DaysLeft < 0 ? $"expired {-DaysLeft}d ago" : DaysLeft == 0 ? "expires today" : $"in {DaysLeft}d";
}

/// <summary>
/// Expiry of the certificates a cluster holds: <c>kubernetes.io/tls</c> secrets (only the public certificate is read, never the key, and nothing is kept
/// of the secret) and cert-manager <c>Certificate</c> objects (from their status).
/// </summary>
public static class CertificateScan
{
    public const int SoonDays = 30, VerySoonDays = 7;

    public static CertificateHealth HealthFor(int daysLeft) =>
        daysLeft < 0 ? CertificateHealth.Expired : daysLeft <= VerySoonDays ? CertificateHealth.ExpiresVerySoon : daysLeft <= SoonDays ? CertificateHealth.ExpiresSoon : CertificateHealth.Ok;

    public static bool IsTlsSecret(KubeObject o) => o.Kind == "Secret" && o.StringAt("type") == "kubernetes.io/tls";

    /// <summary>The leaf certificate of a TLS secret, or null when it has none or cannot be parsed.</summary>
    public static CertificateInfo? FromTlsSecret(KubeObject secret, DateTimeOffset? now = null)
    {
        if (!IsTlsSecret(secret) || secret.StringAt("data", "tls.crt") is not { Length: > 0 } b64) return null;
        try
        {
            var pem = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(b64));
            var fields = PemEncoding.Find(pem);   // the first block is the leaf
            var der = Convert.FromBase64String(pem[fields.Base64Data]);
            using var cert = X509CertificateLoader.LoadCertificate(der);
            var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            var days = (int)Math.Floor((notAfter - (now ?? DateTimeOffset.UtcNow)).TotalDays);
            var names = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames().Take(4).ToList();
            return new CertificateInfo("TLS secret", secret.Namespace, secret.Name, cert.GetNameInfo(X509NameType.SimpleName, false), cert.GetNameInfo(X509NameType.SimpleName, true), notAfter, days, HealthFor(days),
                names is { Count: > 0 } ? string.Join(", ", names) : null);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return null; }
    }

    public static CertificateInfo? FromCertManager(KubeObject cert, DateTimeOffset? now = null)
    {
        if (cert.Kind != "Certificate" || cert.StringAt("status", "notAfter") is not { } raw || !DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var notAfter)) return null;
        var days = (int)Math.Floor((notAfter - (now ?? DateTimeOffset.UtcNow)).TotalDays);
        var names = (cert.At("spec", "dnsNames") as JsonArray ?? []).Select(n => n?.ToString()).Where(n => n is not null).Take(4).ToList();
        var issuer = cert.StringAt("spec", "issuerRef", "name") ?? "";
        var ready = (cert.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => c["type"]?.ToString() == "Ready");
        var detail = names.Count > 0 ? string.Join(", ", names) : null;
        if (ready?["status"]?.ToString() == "False") detail = $"not ready: {ready["message"] ?? ready["reason"]}" + (detail is null ? "" : " · " + detail);
        return new CertificateInfo("cert-manager Certificate", cert.Namespace, cert.Name, cert.StringAt("spec", "commonName") ?? names.FirstOrDefault() ?? cert.Name, issuer, notAfter, days, HealthFor(days), detail);
    }
}
