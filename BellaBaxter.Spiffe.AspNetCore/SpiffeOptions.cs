using Microsoft.AspNetCore.Http;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>Configuration options for Bella Baxter SPIFFE mTLS validation middleware.</summary>
public record SpiffeOptions
{
    /// <summary>Bella Baxter API base URL e.g. "https://api.bella.example.com"</summary>
    public required string BellaBaseUrl { get; init; }

    /// <summary>Environment ID whose trust bundle to fetch.</summary>
    public required Guid EnvironmentId { get; init; }

    /// <summary>How often to refresh the trust bundle (default 1h).</summary>
    public TimeSpan TrustBundleRefreshInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// If true, requests without a client cert are passed through (allows mixed mTLS + non-mTLS routes).
    /// If false, requests without a client cert are rejected with 401.
    /// Default: false (require cert on all requests).
    /// </summary>
    public bool AllowMissingClientCert { get; init; } = false;

    /// <summary>Custom error handler. If null, returns 401 JSON response.</summary>
    public Func<HttpContext, SpiffeValidationError, Task>? OnValidationFailed { get; init; }
}

/// <summary>Describes why SPIFFE validation failed.</summary>
public enum SpiffeValidationError
{
    /// <summary>No client certificate was presented.</summary>
    NoCertificate,

    /// <summary>The client certificate has expired or is not yet valid.</summary>
    CertExpired,

    /// <summary>The client certificate was not issued by a trusted CA (failed chain validation).</summary>
    CertNotTrusted,

    /// <summary>The certificate does not contain a valid SPIFFE URI in its SAN.</summary>
    InvalidSpiffeId,

    /// <summary>The SPIFFE ID does not match the pattern required by [RequireSpiffeId].</summary>
    SpiffeIdNotAllowed,
}
