using Microsoft.AspNetCore.Http;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>Mutable builder for constructing <see cref="SpiffeOptions"/>.</summary>
public sealed class SpiffeOptionsBuilder
{
    /// <summary>
    /// Bella Baxter API base URL e.g. "https://api.bella.example.com". Must be https; plain http is
    /// accepted only for a loopback host (localhost, 127.0.0.0/8, ::1). No opt-out.
    /// </summary>
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
        // Backlog §2.31 — the trust bundle fetched from here is this relying party's TRUST ANCHOR: over
        // plain http off this machine, whoever is on the path chooses which SVIDs it accepts.
        if (BellaApiAddress.Problem(BellaBaseUrl, "[BellaSpiffe] BellaBaseUrl") is { } problem)
            throw new InvalidOperationException(problem);
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
