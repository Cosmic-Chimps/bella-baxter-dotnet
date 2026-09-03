namespace BellaBaxter.Spiffe.Client;

/// <summary>
/// Configuration for SPIFFE-based authentication with Bella Baxter.
/// </summary>
public sealed record SpiffeClientOptions
{
    /// <summary>
    /// Base URL of the Bella Baxter API (e.g. "https://api.bella.example.com").
    /// Used to exchange the JWT-SVID for a bax- lease token.
    /// </summary>
    public required string BellaBaseUrl { get; init; }

    /// <summary>
    /// The Bella Baxter environment ID that owns the WorkloadIdentity registration.
    /// </summary>
    public required Guid EnvironmentId { get; init; }

    /// <summary>
    /// Base URL of a local HTTP JWT-SVID endpoint, for the obsolete
    /// <see cref="BellaAgentHttpClient"/> only.
    /// </summary>
    /// <remarks>
    /// <b>Not used by the default path.</b> <c>bella spiffe agent</c> serves the SPIFFE Workload API
    /// over a Unix socket, whose location comes from <c>SPIFFE_ENDPOINT_SOCKET</c> or the per-user
    /// default — see <see cref="SpiffeAgentDetection"/>. This setting only reaches a caller that
    /// explicitly constructs the obsolete HTTP client.
    /// </remarks>
    public string AgentBaseUrl { get; init; } = "http://localhost:8088";

    /// <summary>
    /// Audience string passed to the bella agent when requesting a JWT-SVID.
    /// Must match the audience configured in the WorkloadIdentity registration.
    /// Defaults to <c>"bella-api"</c>.
    /// </summary>
    public string SvidAudience { get; init; } = "bella-api";

    /// <summary>
    /// How early before token expiry to refresh the bax- lease.
    /// Defaults to 2 minutes, giving time for the refresh to complete before
    /// the token actually expires.
    /// </summary>
    public TimeSpan ExpiryBufferDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// SDK identifier sent as <c>X-Bella-Client</c> header for audit logging.
    /// </summary>
    public string BellaClient { get; init; } = "bella-dotnet-spiffe-sdk";

    /// <summary>
    /// Optional caller application name sent as <c>X-App-Client</c> header.
    /// </summary>
    public string? AppClient { get; init; }
}
