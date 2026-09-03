using System.Security.Cryptography.X509Certificates;

namespace BellaBaxter.Spiffe.AspNetCore;

// Spec 001 T038/T040 — one place that knows a certificate's validity window is expressed in LOCAL
// time, and converts it.
//
// This exists because of a bug, and it is a small class so the bug cannot come back invisibly.
// `X509Certificate2.NotBefore` and `NotAfter` return `DateTimeKind.Local`. `SpiffeMiddleware` compared
// them against `DateTime.UtcNow`, which made the check wrong by the host's UTC offset in whichever
// direction the host sat:
//
//   * East of UTC (Europe/Madrid, +2): a freshly issued SVID read as NOT YET VALID and was refused as
//     CertExpired. With a one-hour SVID lifetime and a +2 offset, NO SVID ever validated — the
//     middleware refused every request while reporting an expiry problem.
//   * West of UTC (America/New_York, -4): an EXPIRED certificate stayed accepted for four hours past
//     its notAfter. Short-lived credentials are the whole security argument for SVIDs, and this
//     extended every one of them silently.
//
// Nothing caught it because a UTC host cannot see it: there, the local value and the UTC value are
// numerically identical. That is exactly why the guard below is a KIND assertion rather than a value
// comparison — `Kind` differs on a UTC host too, so the test fails everywhere if the conversion is
// dropped.

/// <summary>Resolves a certificate's validity window into UTC.</summary>
public static class CertificateValidityWindow
{
    /// <summary>The certificate's window, converted to UTC on both ends.</summary>
    /// <remarks>
    /// Both returned values have <see cref="DateTimeKind.Utc"/>. That is part of the contract, not an
    /// implementation detail: it is the only property of the result that a UTC host can still check.
    /// </remarks>
    public static (DateTime NotBeforeUtc, DateTime NotAfterUtc) Resolve(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return (certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime());
    }

    /// <summary>Whether <paramref name="utcNow"/> falls inside the certificate's window, inclusive.</summary>
    /// <param name="certificate">The certificate to check.</param>
    /// <param name="utcNow">The current instant, in UTC.</param>
    /// <remarks>
    /// Inclusive at both ends, matching how the issuing CA states the window. No clock-skew allowance:
    /// the issuing PKI backdates notBefore (OpenBao's <c>not_before_duration</c>, 30s by default), and
    /// that is the skew margin. A second, independent tolerance here would widen every certificate's
    /// effective life by an amount no operator set and no certificate records.
    /// </remarks>
    public static bool IsCurrentlyValid(X509Certificate2 certificate, DateTime utcNow)
    {
        var (notBefore, notAfter) = Resolve(certificate);
        return utcNow >= notBefore && utcNow <= notAfter;
    }
}
