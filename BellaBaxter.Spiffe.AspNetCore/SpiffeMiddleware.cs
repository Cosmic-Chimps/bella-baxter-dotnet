using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that validates incoming X.509 client certificates as SPIFFE SVIDs.
/// <para>
/// Pipeline steps:
/// <list type="number">
///   <item>Read client cert from <c>HttpContext.Connection.ClientCertificate</c></item>
///   <item>Apply <c>AllowMissingClientCert</c> policy</item>
///   <item>Check certificate validity window</item>
///   <item>Validate certificate chain against the cached trust-bundle CA</item>
///   <item>Extract SPIFFE SAN URI from the certificate</item>
///   <item>Parse the SPIFFE ID into its component segments</item>
///   <item>Populate <c>HttpContext.User</c> claims: <c>spiffe-id</c>, <c>spiffe-tenant</c>, <c>spiffe-workload</c></item>
///   <item>Invoke the next middleware</item>
/// </list>
/// </para>
/// </summary>
public sealed class SpiffeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SpiffeOptions _options;
    private readonly ISpiffeTrustBundleCache _trustBundleCache;
    private readonly ILogger<SpiffeMiddleware> _logger;

    public SpiffeMiddleware(
        RequestDelegate next,
        SpiffeOptions options,
        ISpiffeTrustBundleCache trustBundleCache,
        ILogger<SpiffeMiddleware> logger)
    {
        _next = next;
        _options = options;
        _trustBundleCache = trustBundleCache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var cert = context.Connection.ClientCertificate;

        // ── Step 1: missing cert ─────────────────────────────────────────────
        if (cert == null)
        {
            if (_options.AllowMissingClientCert)
            {
                await _next(context);
                return;
            }

            _logger.LogDebug("[BellaSpiffe] No client certificate presented.");
            await HandleFailureAsync(context, SpiffeValidationError.NoCertificate);
            return;
        }

        // ── Step 2: validity window ──────────────────────────────────────────
        // Via CertificateValidityWindow, which is where the local-vs-UTC trap is documented: the
        // certificate's NotBefore/NotAfter are LOCAL time, and comparing them against DateTime.UtcNow
        // (as this did) is wrong by the host's UTC offset — rejecting every fresh SVID east of UTC and
        // accepting expired ones west of it.
        var now = DateTime.UtcNow;
        if (!CertificateValidityWindow.IsCurrentlyValid(cert, now))
        {
            var (notBeforeUtc, notAfterUtc) = CertificateValidityWindow.Resolve(cert);
            _logger.LogWarning(
                "[BellaSpiffe] Client certificate expired or not yet valid. NotBefore={NotBefore:O} NotAfter={NotAfter:O} (UTC now {Now:O})",
                notBeforeUtc, notAfterUtc, now);
            await HandleFailureAsync(context, SpiffeValidationError.CertExpired);
            return;
        }

        // ── Step 3: chain validation against trust bundle ────────────────────
        // The WHOLE bundle, not its first certificate: during a CA rotation the bundle carries both
        // the outgoing and incoming CA, and that overlap is what keeps a rotation from being an
        // outage (T040). Reading only the first would reject every SVID signed by the other one.
        var trustBundle = _trustBundleCache.GetTrustBundleChain();
        if (trustBundle.Count == 0)
        {
            // Fail closed. An unloaded bundle is not an empty allow-list — it is no answer yet, and
            // admitting traffic on it would mean the first requests after a restart are unverified.
            _logger.LogError("[BellaSpiffe] Trust bundle not yet loaded. Rejecting request.");
            await HandleFailureAsync(context, SpiffeValidationError.CertNotTrusted);
            return;
        }

        if (!ValidateChain(cert, trustBundle))
        {
            _logger.LogWarning("[BellaSpiffe] Certificate chain validation failed for subject: {Subject}", cert.Subject);
            await HandleFailureAsync(context, SpiffeValidationError.CertNotTrusted);
            return;
        }

        // ── Step 4: extract SPIFFE SAN URI ───────────────────────────────────
        var sanUris = SpiffeIdValidator.GetSanUris(cert);
        var spiffeUri = sanUris.FirstOrDefault(u => u.StartsWith("spiffe://", StringComparison.Ordinal));
        if (spiffeUri == null)
        {
            _logger.LogWarning("[BellaSpiffe] Certificate has no SPIFFE SAN URI. Subject: {Subject}", cert.Subject);
            await HandleFailureAsync(context, SpiffeValidationError.InvalidSpiffeId);
            return;
        }

        // ── Step 5: parse SPIFFE ID ──────────────────────────────────────────
        var parts = SpiffeIdValidator.Parse(spiffeUri);
        if (parts == null)
        {
            _logger.LogWarning("[BellaSpiffe] Invalid SPIFFE ID format: {SpiffeUri}", spiffeUri);
            await HandleFailureAsync(context, SpiffeValidationError.InvalidSpiffeId);
            return;
        }

        // ── Step 6: populate claims ──────────────────────────────────────────
        var claims = new[]
        {
            new Claim(SpiffeClaims.SpiffeId,       parts.FullUri),
            new Claim(SpiffeClaims.SpiffeTenant,   parts.Tenant),
            new Claim(SpiffeClaims.SpiffeWorkload, parts.Workload),
        };

        var identity   = new ClaimsIdentity(claims, authenticationType: "spiffe");
        var principal  = new ClaimsPrincipal(identity);
        context.User   = principal;

        _logger.LogDebug("[BellaSpiffe] Authenticated SPIFFE workload: {SpiffeUri}", spiffeUri);
        await _next(context);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool ValidateChain(X509Certificate2 cert, X509Certificate2Collection trustBundle)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustBundle);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(cert);
    }

    private async Task HandleFailureAsync(HttpContext context, SpiffeValidationError error)
    {
        if (_options.OnValidationFailed != null)
        {
            await _options.OnValidationFailed(context, error);
            return;
        }

        context.Response.StatusCode  = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new
        {
            error   = error.ToString(),
            message = GetDefaultMessage(error),
        });

        await context.Response.WriteAsync(body, Encoding.UTF8);
    }

    private static string GetDefaultMessage(SpiffeValidationError error) => error switch
    {
        SpiffeValidationError.NoCertificate      => "mTLS client certificate is required.",
        SpiffeValidationError.CertExpired        => "Client certificate has expired.",
        SpiffeValidationError.CertNotTrusted     => "Client certificate is not trusted.",
        SpiffeValidationError.InvalidSpiffeId    => "Certificate does not contain a valid SPIFFE ID.",
        SpiffeValidationError.SpiffeIdNotAllowed => "SPIFFE ID is not permitted on this endpoint.",
        _                                        => "Unauthorized.",
    };
}
