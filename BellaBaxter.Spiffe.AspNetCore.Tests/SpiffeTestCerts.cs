using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BellaBaxter.Spiffe.AspNetCore.Tests;

/// <summary>
/// Real X.509 material for the middleware tests — a self-signed CA and SVIDs it signs.
/// </summary>
/// <remarks>
/// Real certificates rather than a stub, because everything under test is exactly the part a stub
/// would have to fake: chain building against a custom root, and URI SANs. A fake that returned "yes,
/// this chains" would leave the one thing the middleware is for untested.
/// </remarks>
internal static class SpiffeTestCerts
{
    /// <summary>Creates a self-signed CA usable as a trust-bundle entry.</summary>
    internal static X509Certificate2 CreateCa(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    /// <summary>Issues an SVID carrying <paramref name="spiffeId"/> as a URI SAN.</summary>
    internal static X509Certificate2 IssueSvid(
        X509Certificate2 ca,
        string spiffeId,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=workload", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(spiffeId));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var from = notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        var to = notAfter ?? DateTimeOffset.UtcNow.AddHours(1);

        // A fresh serial per call: two SVIDs sharing one serial can confuse chain caching, and the
        // resulting flake would look like a middleware bug.
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);

        return request.Create(ca, from, to, serial);
    }

    /// <summary>A certificate with NO URI SAN at all.</summary>
    internal static X509Certificate2 IssueWithoutSpiffeSan(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=plain-client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        return request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1), serial);
    }

    /// <summary>The PEM a trust-bundle response would carry for these CAs, in order.</summary>
    internal static string BundlePem(params X509Certificate2[] cas) =>
        string.Join("\n", cas.Select(c => c.ExportCertificatePem())) + "\n";
}
