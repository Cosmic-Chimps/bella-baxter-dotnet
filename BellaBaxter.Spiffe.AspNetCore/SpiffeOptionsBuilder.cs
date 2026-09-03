using Microsoft.AspNetCore.Http;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>Mutable builder for constructing <see cref="SpiffeOptions"/>.</summary>
public sealed class SpiffeOptionsBuilder
{
    /// <summary>Bella Baxter API base URL e.g. "https://api.bella.example.com"</summary>
    public string BellaBaseUrl { get; set; } = string.Empty;

    /// <summary>Environment ID whose trust bundle to fetch.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>How often to refresh the trust bundle (default 1h).</summary>
    public TimeSpan TrustBundleRefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// If true, requests without a client cert are passed through.
    /// Default: false.
    /// </summary>
    public bool AllowMissingClientCert { get; set; } = false;

    /// <summary>Custom error handler.</summary>
    public Func<HttpContext, SpiffeValidationError, Task>? OnValidationFailed { get; set; }

    internal SpiffeOptions Build()
    {
        if (string.IsNullOrWhiteSpace(BellaBaseUrl))
            throw new InvalidOperationException("[BellaSpiffe] BellaBaseUrl is required.");
        if (EnvironmentId == Guid.Empty)
            throw new InvalidOperationException("[BellaSpiffe] EnvironmentId is required.");

        return new SpiffeOptions
        {
            BellaBaseUrl = BellaBaseUrl,
            EnvironmentId = EnvironmentId,
            TrustBundleRefreshInterval = TrustBundleRefreshInterval,
            AllowMissingClientCert = AllowMissingClientCert,
            OnValidationFailed = OnValidationFailed,
        };
    }
}
