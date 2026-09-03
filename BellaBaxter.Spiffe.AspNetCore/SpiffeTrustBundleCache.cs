using System.Linq;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>
/// Provides a cached X.509 trust-bundle CA certificate fetched from the Bella Baxter API.
/// Refreshes the bundle in the background according to <see cref="SpiffeOptions.TrustBundleRefreshInterval"/>.
/// </summary>
public interface ISpiffeTrustBundleCache
{
    /// <summary>
    /// Returns the FIRST certificate in the cached trust bundle, or null if not yet loaded.
    /// </summary>
    /// <remarks>
    /// Kept for compatibility with callers written against the original single-certificate shape.
    /// Prefer <see cref="GetTrustBundleChain"/>: a trust bundle is a PEM that may legitimately carry
    /// more than one certificate, and this method silently discards every one after the first.
    /// </remarks>
    X509Certificate2? GetTrustBundle();

    /// <summary>
    /// Returns EVERY certificate in the cached trust bundle. Empty when not yet loaded.
    /// </summary>
    /// <remarks>
    /// <para>Spec 001 T040 (FR-023). This exists because of what a CA rotation actually is: for a
    /// window, the environment's bundle carries BOTH the outgoing and the incoming CA, so workloads
    /// holding an SVID from either one keep working while they re-attest. That overlap is the entire
    /// mechanism that makes rotation non-breaking.</para>
    ///
    /// <para>The original implementation read the bundle with
    /// <c>X509Certificate2.CreateFromPem</c>, which returns only the FIRST certificate in the PEM. So
    /// during a rotation, every workload whose SVID was signed by the other CA was rejected with
    /// <c>CertNotTrusted</c> — a total outage for half the fleet, arriving in the middle of a
    /// maintenance operation and looking like a middleware bug rather than a truncated bundle. The same
    /// truncation would drop an intermediate from a root+intermediate chain.</para>
    /// </remarks>
    X509Certificate2Collection GetTrustBundleChain();
}

/// <inheritdoc />
public sealed class SpiffeTrustBundleCache : BackgroundService, ISpiffeTrustBundleCache
{
    private readonly SpiffeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpiffeTrustBundleCache> _logger;

    // The whole bundle, replaced atomically on refresh. Never mutated in place: the middleware reads
    // it on every request, and adding to a live collection would let a request see a half-updated
    // trust set — the one moment when getting the answer wrong rejects legitimate traffic.
    private volatile X509Certificate2Collection _cachedBundle = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SpiffeTrustBundleCache(
        SpiffeOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<SpiffeTrustBundleCache> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public X509Certificate2? GetTrustBundle()
    {
        var bundle = _cachedBundle;
        return bundle.Count == 0 ? null : bundle[0];
    }

    /// <inheritdoc />
    public X509Certificate2Collection GetTrustBundleChain() => _cachedBundle;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Block app startup until the first trust-bundle fetch completes.
        // This ensures that the middleware has a CA cert before accepting requests.
        _logger.LogInformation("[BellaSpiffe] Fetching initial trust bundle for environment {EnvId}...", _options.EnvironmentId);
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Periodic background refresh after the initial one in StartAsync.
        using var timer = new PeriodicTimer(_options.TrustBundleRefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var url = $"{_options.BellaBaseUrl.TrimEnd('/')}/api/v1/environments/{_options.EnvironmentId}/trust-bundle";
            using var client = _httpClientFactory.CreateClient(SpiffeConstants.HttpClientName);

            var response = await client.GetFromJsonAsync<TrustBundleResponse>(url, cancellationToken);
            if (response?.TrustBundle is null)
            {
                _logger.LogWarning("[BellaSpiffe] Trust bundle response was empty from {Url}", url);
                return;
            }

            // ImportFromPem, NOT CreateFromPem: the latter keeps only the first certificate, which
            // silently halves the trust set during a CA rotation overlap (T040).
            var bundle = new X509Certificate2Collection();
            bundle.ImportFromPem(response.TrustBundle);

            if (bundle.Count == 0)
            {
                // A response that parsed to nothing is not an empty trust set — it is a failed read,
                // and replacing a working bundle with nothing would reject every request. Keep what we
                // have and say so loudly, because this needs a human.
                _logger.LogError(
                    "[BellaSpiffe] Trust bundle from {Url} contained no certificates. Keeping the "
                    + "previously cached bundle ({Count} cert(s)). Check the environment's PKI binding.",
                    url, _cachedBundle.Count);
                return;
            }

            _cachedBundle = bundle;

            if (bundle.Count == 1)
            {
                _logger.LogInformation(
                    "[BellaSpiffe] Trust bundle refreshed. CA subject: {Subject}, expires: {Expiry}",
                    bundle[0].Subject, bundle[0].NotAfter);
            }
            else
            {
                // Worth an Information line rather than Debug: more than one CA means a rotation is
                // in progress, and that is exactly the context someone needs when reading this log
                // during an incident.
                _logger.LogInformation(
                    "[BellaSpiffe] Trust bundle refreshed with {Count} CA certificate(s) — a rotation "
                    + "overlap is in effect and SVIDs from any of them are accepted: {Subjects}",
                    bundle.Count,
                    string.Join(", ", bundle.Cast<X509Certificate2>()
                        .Select(c => $"{c.Subject} (expires {c.NotAfter:yyyy-MM-dd})")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BellaSpiffe] Failed to refresh trust bundle from {BaseUrl}", _options.BellaBaseUrl);
            // Keep the old cached value — don't clear it on transient failures.
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private sealed class TrustBundleResponse
    {
        [JsonPropertyName("trustBundle")]
        public string? TrustBundle { get; set; }
    }
}
