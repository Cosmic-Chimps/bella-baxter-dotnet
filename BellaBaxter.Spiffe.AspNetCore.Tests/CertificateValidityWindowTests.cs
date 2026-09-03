using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace BellaBaxter.Spiffe.AspNetCore.Tests;

/// <summary>
/// Spec 001 T038 — the local-vs-UTC bug in the certificate validity window, pinned so it cannot
/// return on a machine that cannot see it.
/// </summary>
/// <remarks>
/// <para><b>The bug.</b> <c>X509Certificate2.NotBefore</c> and <c>NotAfter</c> return
/// <c>DateTimeKind.Local</c>. <c>SpiffeMiddleware</c> compared them against <c>DateTime.UtcNow</c>, so
/// the check was wrong by the host's UTC offset — and in a different direction depending on where the
/// host sat. East of UTC a freshly issued SVID read as not-yet-valid and every request was refused
/// (reported as an expiry problem, which is the last place anyone would look). West of UTC an EXPIRED
/// certificate stayed accepted for the length of the offset, silently extending the lifetime of exactly
/// the short-lived credential the whole design rests on.</para>
///
/// <para><b>Why the guard is a Kind assertion.</b> A UTC host cannot see the bug at all: there the
/// local and UTC values are numerically identical, so a value comparison passes with the broken code.
/// <c>Kind</c> does differ on a UTC host — a parsed certificate's timestamps are <c>Local</c> even at
/// offset zero — so asserting the resolved window is <c>Utc</c> fails everywhere if the conversion is
/// dropped. That is the difference between a test that catches this and one that only caught it here.</para>
/// </remarks>
public class CertificateValidityWindowTests
{
    [Fact]
    public void The_resolved_window_is_UTC_on_BOTH_ends()
    {
        // The zone-independent guard. Remove `.ToUniversalTime()` and this fails on every machine,
        // including the UTC CI host where the original bug was invisible.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var svid = SpiffeTestCerts.IssueSvid(ca, "spiffe://acme/payments/prod/billing-service");

        var (notBefore, notAfter) = CertificateValidityWindow.Resolve(svid);

        Assert.Equal(DateTimeKind.Utc, notBefore.Kind);
        Assert.Equal(DateTimeKind.Utc, notAfter.Kind);
    }

    [Fact]
    public void The_certificates_raw_properties_really_are_LOCAL_which_is_the_whole_trap()
    {
        // Asserted so the reason for the conversion is visible in the suite rather than only in a
        // comment. If a future runtime ever returned Utc here, this fails and tells the next person
        // that the conversion has become a no-op rather than leaving them to wonder why it exists.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var svid = SpiffeTestCerts.IssueSvid(ca, "spiffe://acme/payments/prod/billing-service");

        Assert.Equal(DateTimeKind.Local, svid.NotBefore.Kind);
        Assert.Equal(DateTimeKind.Local, svid.NotAfter.Kind);
    }

    [Fact]
    public void The_resolved_window_names_the_same_INSTANTS_the_certificate_does()
    {
        // Converting is only right if it preserves the instant. DateTimeOffset is the independent
        // reading of the same values, so this catches a conversion that shifted rather than relabelled.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var svid = SpiffeTestCerts.IssueSvid(ca, "spiffe://acme/payments/prod/billing-service");

        var (notBefore, notAfter) = CertificateValidityWindow.Resolve(svid);

        Assert.Equal(new DateTimeOffset(svid.NotBefore).UtcDateTime, notBefore);
        Assert.Equal(new DateTimeOffset(svid.NotAfter).UtcDateTime, notAfter);
    }

    [Fact]
    public void A_freshly_issued_certificate_is_valid_NOW()
    {
        // The east-of-UTC symptom, and the one that took the fleet down: five minutes of backdating is
        // less than most UTC offsets, so the broken comparison called this "not yet valid".
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var svid = SpiffeTestCerts.IssueSvid(ca, "spiffe://acme/payments/prod/billing-service");

        Assert.True(CertificateValidityWindow.IsCurrentlyValid(svid, DateTime.UtcNow));
    }

    [Fact]
    public void A_certificate_that_expired_a_MINUTE_ago_is_not_valid()
    {
        // The west-of-UTC symptom. One minute is far less than an offset, so the broken comparison kept
        // accepting this for hours.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var expired = SpiffeTestCerts.IssueSvid(
            ca, "spiffe://acme/payments/prod/billing-service",
            notBefore: DateTimeOffset.UtcNow.AddHours(-2),
            notAfter: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(CertificateValidityWindow.IsCurrentlyValid(expired, DateTime.UtcNow));
    }

    [Fact]
    public void A_certificate_that_becomes_valid_in_a_MINUTE_is_not_valid_yet()
    {
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var future = SpiffeTestCerts.IssueSvid(
            ca, "spiffe://acme/payments/prod/billing-service",
            notBefore: DateTimeOffset.UtcNow.AddMinutes(1),
            notAfter: DateTimeOffset.UtcNow.AddHours(1));

        Assert.False(CertificateValidityWindow.IsCurrentlyValid(future, DateTime.UtcNow));
    }

    [Fact]
    public void The_window_is_INCLUSIVE_at_both_ends()
    {
        // Matching how the CA states the window. An exclusive comparison would shave the boundary
        // second off every certificate — harmless-looking, and a source of intermittent refusals right
        // at issuance for a very short-lived SVID.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var svid = SpiffeTestCerts.IssueSvid(ca, "spiffe://acme/payments/prod/billing-service");

        var (notBefore, notAfter) = CertificateValidityWindow.Resolve(svid);

        Assert.True(CertificateValidityWindow.IsCurrentlyValid(svid, notBefore));
        Assert.True(CertificateValidityWindow.IsCurrentlyValid(svid, notAfter));

        Assert.False(CertificateValidityWindow.IsCurrentlyValid(svid, notBefore.AddSeconds(-1)));
        Assert.False(CertificateValidityWindow.IsCurrentlyValid(svid, notAfter.AddSeconds(1)));
    }
}
