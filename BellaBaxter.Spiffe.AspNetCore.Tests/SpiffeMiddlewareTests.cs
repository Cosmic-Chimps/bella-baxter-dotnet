using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BellaBaxter.Spiffe.AspNetCore.Tests;

/// <summary>
/// Spec 001 T038 (US5, FR-022/023) — what <c>SpiffeMiddleware</c> admits and what it refuses.
/// </summary>
/// <remarks>
/// <para>This middleware is the consuming side of the whole feature: it is what a customer's service
/// runs to turn an mTLS client certificate into an identity it can authorise on. Everything it does is
/// a security decision, and until now none of it had a test — including the two failure modes that
/// matter most, an untrusted issuer and an unloaded trust bundle.</para>
///
/// <para>Real certificates throughout, because a stub would have to fake precisely the parts under
/// test: chain building against a custom root, and URI-SAN extraction.</para>
/// </remarks>
public class SpiffeMiddlewareTests
{
    private const string SpiffeId = "spiffe://acme/payments/prod/billing-service";

    [Fact]
    public async Task A_valid_SVID_from_the_trusted_CA_is_admitted_and_becomes_claims()
    {
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var svid = SpiffeTestCerts.IssueSvid(ca, SpiffeId);

        var ctx = await RunAsync(svid, [ca]);

        Assert.True(ctx.NextCalled);
        Assert.Equal(SpiffeId, ctx.Http.User.FindFirst(SpiffeClaims.SpiffeId)?.Value);

        // The tenant claim is what a multi-tenant consumer authorises on, so a transposition here
        // would be the most dangerous kind of quiet bug: every request attributed to the wrong tenant.
        Assert.Equal("acme", ctx.Http.User.FindFirst(SpiffeClaims.SpiffeTenant)?.Value);
        Assert.Equal("billing-service", ctx.Http.User.FindFirst(SpiffeClaims.SpiffeWorkload)?.Value);
    }

    [Fact]
    public async Task An_SVID_from_an_UNTRUSTED_CA_is_refused()
    {
        // The core claim. The attacker's CA is perfectly well-formed and its SVID carries a perfectly
        // well-formed SPIFFE ID naming the victim's workload — the only thing wrong with it is who
        // signed it, and that has to be enough.
        using var trusted = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var attacker = SpiffeTestCerts.CreateCa("Attacker CA");
        using var forged = SpiffeTestCerts.IssueSvid(attacker, SpiffeId);

        var ctx = await RunAsync(forged, [trusted]);

        Assert.False(ctx.NextCalled);
        Assert.Equal((int)HttpStatusCode.Unauthorized, ctx.Http.Response.StatusCode);
        Assert.Equal(SpiffeValidationError.CertNotTrusted.ToString(), ctx.ErrorCode);

        // And nothing was attributed: a rejected request must not leave an identity behind for a later
        // middleware to read.
        Assert.Null(ctx.Http.User.FindFirst(SpiffeClaims.SpiffeId));
    }

    [Fact]
    public async Task An_EXPIRED_SVID_is_refused_even_though_it_chains_correctly()
    {
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var expired = SpiffeTestCerts.IssueSvid(
            ca, SpiffeId,
            notBefore: DateTimeOffset.UtcNow.AddHours(-2),
            notAfter: DateTimeOffset.UtcNow.AddMinutes(-1));

        var ctx = await RunAsync(expired, [ca]);

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.CertExpired.ToString(), ctx.ErrorCode);
    }

    [Fact]
    public async Task A_NOT_YET_VALID_SVID_is_refused_too()
    {
        // Both ends of the window. Checking only expiry would admit a certificate minted with a future
        // notBefore — which is how a clock-skew workaround turns into an accepted pre-dated credential.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var future = SpiffeTestCerts.IssueSvid(
            ca, SpiffeId,
            notBefore: DateTimeOffset.UtcNow.AddHours(1),
            notAfter: DateTimeOffset.UtcNow.AddHours(2));

        var ctx = await RunAsync(future, [ca]);

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.CertExpired.ToString(), ctx.ErrorCode);
    }

    [Fact]
    public async Task A_certificate_with_no_SPIFFE_SAN_is_refused_with_a_DIFFERENT_reason()
    {
        // Trusted, in date, and simply not an SVID. Reported as InvalidSpiffeId rather than
        // CertNotTrusted so the operator is not sent to audit their CA over a client presenting an
        // ordinary TLS certificate.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var plain = SpiffeTestCerts.IssueWithoutSpiffeSan(ca);

        var ctx = await RunAsync(plain, [ca]);

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.InvalidSpiffeId.ToString(), ctx.ErrorCode);
    }

    [Fact]
    public async Task A_malformed_SPIFFE_ID_is_refused_rather_than_partially_parsed()
    {
        // Too few segments to be a Bella SPIFFE ID. Admitting it would populate a tenant claim from
        // whatever happened to be in the first position.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var odd = SpiffeTestCerts.IssueSvid(ca, "spiffe://acme/only-two");

        var ctx = await RunAsync(odd, [ca]);

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.InvalidSpiffeId.ToString(), ctx.ErrorCode);
    }

    [Fact]
    public async Task An_UNLOADED_trust_bundle_refuses_everything_rather_than_admitting_it()
    {
        // Fail closed, and the reason it needs its own test: an empty trust set is easy to treat as
        // "no restrictions", and that reading would leave every request in the window between process
        // start and the first successful bundle fetch completely unverified.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var svid = SpiffeTestCerts.IssueSvid(ca, SpiffeId);

        var ctx = await RunAsync(svid, trustBundle: []);

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.CertNotTrusted.ToString(), ctx.ErrorCode);
    }

    [Fact]
    public async Task No_client_certificate_is_refused_by_default()
    {
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");

        var ctx = await RunAsync(clientCert: null, [ca]);

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.NoCertificate.ToString(), ctx.ErrorCode);
    }

    [Fact]
    public async Task No_client_certificate_passes_through_when_the_host_opted_in()
    {
        // The mixed-mTLS escape hatch. It must pass through WITHOUT an identity — a consumer that
        // enabled this for a health endpoint must not find an unauthenticated request carrying claims.
        using var ca = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");

        var ctx = await RunAsync(clientCert: null, [ca], allowMissingClientCert: true);

        Assert.True(ctx.NextCalled);
        Assert.Null(ctx.Http.User.FindFirst(SpiffeClaims.SpiffeId));
    }

    [Fact]
    public async Task A_custom_failure_handler_replaces_the_default_response_entirely()
    {
        using var trusted = SpiffeTestCerts.CreateCa("Acme SPIFFE CA");
        using var attacker = SpiffeTestCerts.CreateCa("Attacker CA");
        using var forged = SpiffeTestCerts.IssueSvid(attacker, SpiffeId);

        SpiffeValidationError? seen = null;
        var ctx = await RunAsync(forged, [trusted], onValidationFailed: (http, error) =>
        {
            seen = error;
            http.Response.StatusCode = StatusCodes.Status418ImATeapot;
            return Task.CompletedTask;
        });

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.CertNotTrusted, seen);
        Assert.Equal(StatusCodes.Status418ImATeapot, ctx.Http.Response.StatusCode);
    }

    // ===== T040: CA rotation overlap (FR-023) =====

    [Fact]
    public async Task During_a_rotation_overlap_SVIDs_from_BOTH_CAs_are_admitted()
    {
        // The defect this closes: the cache read the bundle with X509Certificate2.CreateFromPem, which
        // keeps only the FIRST certificate. A rotation bundle carries the outgoing and incoming CA
        // precisely so both cohorts keep working while they re-attest — so truncating it rejected every
        // SVID signed by the other CA. A total outage for half the fleet, arriving mid-maintenance and
        // looking like a middleware bug rather than a truncated bundle.
        using var oldCa = SpiffeTestCerts.CreateCa("Acme SPIFFE CA (outgoing)");
        using var newCa = SpiffeTestCerts.CreateCa("Acme SPIFFE CA (incoming)");

        using var fromOld = SpiffeTestCerts.IssueSvid(oldCa, SpiffeId);
        using var fromNew = SpiffeTestCerts.IssueSvid(newCa, SpiffeId);

        var overlap = new X509Certificate2Collection { oldCa, newCa };

        Assert.True((await RunAsync(fromOld, overlap)).NextCalled, "the OUTGOING CA's SVID was rejected");
        Assert.True((await RunAsync(fromNew, overlap)).NextCalled, "the INCOMING CA's SVID was rejected");
    }

    [Fact]
    public async Task Order_in_the_bundle_does_not_decide_who_gets_in()
    {
        // Pinned separately because the bug was positional. If a future change reintroduced
        // "first certificate wins", this fails whichever order the server happens to send.
        using var first = SpiffeTestCerts.CreateCa("CA One");
        using var second = SpiffeTestCerts.CreateCa("CA Two");
        using var svid = SpiffeTestCerts.IssueSvid(second, SpiffeId);

        Assert.True((await RunAsync(svid, new X509Certificate2Collection { first, second })).NextCalled);
        Assert.True((await RunAsync(svid, new X509Certificate2Collection { second, first })).NextCalled);
    }

    [Fact]
    public async Task An_overlap_bundle_still_refuses_a_CA_that_is_not_in_it()
    {
        // The control. Without it, the two tests above would pass equally well against a middleware
        // that had stopped checking the chain at all — which is the classic way "we fixed the trust
        // bundle" turns into "we stopped validating".
        using var oldCa = SpiffeTestCerts.CreateCa("Acme SPIFFE CA (outgoing)");
        using var newCa = SpiffeTestCerts.CreateCa("Acme SPIFFE CA (incoming)");
        using var attacker = SpiffeTestCerts.CreateCa("Attacker CA");
        using var forged = SpiffeTestCerts.IssueSvid(attacker, SpiffeId);

        var ctx = await RunAsync(forged, new X509Certificate2Collection { oldCa, newCa });

        Assert.False(ctx.NextCalled);
        Assert.Equal(SpiffeValidationError.CertNotTrusted.ToString(), ctx.ErrorCode);
    }

    // ===== harness =====

    private sealed record RunResult(DefaultHttpContext Http, bool NextCalled, string? ErrorCode);

    private static async Task<RunResult> RunAsync(
        X509Certificate2? clientCert,
        X509Certificate2Collection trustBundle,
        bool allowMissingClientCert = false,
        Func<HttpContext, SpiffeValidationError, Task>? onValidationFailed = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.ClientCertificate = clientCert;
        http.Response.Body = new MemoryStream();

        var nextCalled = false;
        var options = new SpiffeOptions
        {
            BellaBaseUrl = "https://api.test",
            EnvironmentId = Guid.NewGuid(),
            AllowMissingClientCert = allowMissingClientCert,
            OnValidationFailed = onValidationFailed,
        };

        var middleware = new SpiffeMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            options,
            new StubTrustBundleCache(trustBundle),
            NullLogger<SpiffeMiddleware>.Instance);

        await middleware.InvokeAsync(http);

        return new RunResult(http, nextCalled, ReadErrorCode(http));
    }

    private static string? ReadErrorCode(HttpContext http)
    {
        if (http.Response.Body is not MemoryStream ms || ms.Length == 0)
        {
            return null;
        }

        ms.Position = 0;
        var body = Encoding.UTF8.GetString(ms.ToArray());
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class StubTrustBundleCache(X509Certificate2Collection bundle) : ISpiffeTrustBundleCache
    {
        public X509Certificate2? GetTrustBundle() => bundle.Count == 0 ? null : bundle[0];
        public X509Certificate2Collection GetTrustBundleChain() => bundle;
    }
}
