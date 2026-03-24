namespace BellaBaxter.AspNet.Configuration;

/// <summary>
/// Aggregated secrets response from Baxter API.
/// GET /api/v1/environments/{slug}/secrets
/// </summary>
internal record BellaSecretsResponse(
    string EnvironmentSlug,
    string EnvironmentName,
    Dictionary<string, string> Secrets,
    long Version,
    DateTimeOffset LastModified
);

/// <summary>
/// Lightweight version-check response used before deciding to fetch full secrets.
/// GET /api/v1/environments/{slug}/secrets/version
/// </summary>
internal record BellaSecretsVersion(
    string EnvironmentSlug,
    long Version,
    DateTimeOffset LastModified
);
