using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BellaBaxter.Spiffe.Client;

/// <summary>
/// Calls a local HTTP endpoint at <c>GET /v1/svid/jwt?audience={audience}</c> to fetch a JWT-SVID.
/// </summary>
/// <remarks>
/// <para><b>Obsolete: no agent has ever served this endpoint.</b> The docstring here used to describe
/// "a lightweight HTTP API alongside the standard SPIFFE gRPC Workload API socket" — the agent shipped
/// with only the socket (spec 001 T042), so this client's SPIFFE path could never have worked against
/// a real agent. Use <see cref="WorkloadApiSvidClient"/>, which speaks the actual Workload API.</para>
///
/// <para>Kept rather than deleted because it is public API in a published package, and because the
/// interface it implements is the useful seam: a host that genuinely does expose a token over local
/// HTTP can still plug one in. It is simply not how <c>bella spiffe agent</c> works.</para>
///
/// <para>Also worth noting for anyone tempted to build such an endpoint: a localhost TCP port is
/// reachable by every process on the host and, in a shared network namespace, by every container in the
/// pod. The Unix socket exists because its filesystem permission is the agent's whole authorisation
/// boundary.</para>
/// </remarks>
[Obsolete(
    "No bella agent serves an HTTP JWT-SVID endpoint; the agent implements the SPIFFE Workload API "
    + "over a Unix socket. Use WorkloadApiSvidClient (or BellaSpiffeClientFactory.CreateAutoDetect).")]
public sealed class BellaAgentHttpClient : ISpiffeWorkloadClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BellaAgentHttpClient> _logger;

    /// <summary>Default port the bella agent HTTP endpoint listens on.</summary>
    public const int DefaultAgentPort = 8088;

    public BellaAgentHttpClient(HttpClient http, ILogger<BellaAgentHttpClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> FetchJwtSvidAsync(string audience = "bella-api", CancellationToken cancellationToken = default)
    {
        var url = $"/v1/svid/jwt?audience={Uri.EscapeDataString(audience)}";
        _logger.LogDebug("Fetching JWT-SVID from bella agent at {Url}", url);

        var response = await _http.GetFromJsonAsync<AgentSvidResponse>(url, cancellationToken)
            ?? throw new InvalidOperationException("bella agent returned an empty JWT-SVID response.");

        if (string.IsNullOrWhiteSpace(response.Svid))
            throw new InvalidOperationException(
                "bella agent returned a JWT-SVID response with an empty svid field. " +
                "Ensure the workload is registered via `bella spire add`.");

        _logger.LogDebug("Received JWT-SVID for {SpiffeId}", response.SpiffeId);
        return response.Svid;
    }

    private sealed record AgentSvidResponse(
        [property: JsonPropertyName("spiffeId")] string? SpiffeId,
        [property: JsonPropertyName("svid")] string? Svid
    );
}
