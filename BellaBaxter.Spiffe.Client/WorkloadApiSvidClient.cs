using System.Net.Sockets;
using BellaBaxter.Spiffe.Client.WorkloadApi;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BellaBaxter.Spiffe.Client;

// Spec 001 T044 (US6, FR-028) — fetching a JWT-SVID from the agent the way every other SPIFFE client
// does.
//
// THIS REPLACES A PATH THAT COULD NEVER HAVE WORKED. `BellaAgentHttpClient` called
// `GET http://localhost:8088/v1/svid/jwt`, an endpoint no version of the agent has ever served. The
// agent serves the official SPIFFE Workload API — gRPC over a Unix domain socket — so this speaks that
// instead, from the same vendored upstream schema the agent is generated from.
//
// The socket, not a TCP port, and that is a security property rather than a style choice: a localhost
// port is reachable by every process on the host AND, in a shared network namespace, by every container
// in the pod. The agent's authorisation boundary is the socket's filesystem permission (0600 in a 0700
// directory), and a TCP endpoint would have no boundary at all.
//
// NO CACHING. A JWT-SVID is minted per audience immediately before use, and the token handler above
// this already caches the exchanged `bax-` lease. Caching here as well would hand out a token closer to
// expiry than the caller expects and turn a working call into an intermittent 401 at the far end.

/// <summary>Fetches JWT-SVIDs from a local SPIFFE agent over the Workload API.</summary>
public sealed class WorkloadApiSvidClient : ISpiffeWorkloadClient, IDisposable
{
    /// <summary>The metadata header the SPIFFE spec requires on every Workload API call.</summary>
    public const string SecurityHeader = "workload.spiffe.io";

    private readonly GrpcChannel _channel;
    private readonly SpiffeWorkloadAPI.SpiffeWorkloadAPIClient _client;
    private readonly ILogger _logger;
    private readonly string _socketPath;
    private readonly Metadata _headers = new() { { SecurityHeader, "true" } };

    /// <param name="socketPath">Path to the agent's Workload API socket.</param>
    /// <param name="logger">Optional logger.</param>
    public WorkloadApiSvidClient(string socketPath, ILogger? logger = null)
    {
        _socketPath = socketPath;
        _logger = logger ?? NullLogger.Instance;

        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(
                        AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            },
        });

        _client = new SpiffeWorkloadAPI.SpiffeWorkloadAPIClient(_channel);
    }

    /// <inheritdoc />
    public async Task<string> FetchJwtSvidAsync(
        string audience = "bella-api", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            // A token with no audience is accepted by anything that does not check — the failure
            // audience binding exists to prevent. Never defaulted to empty.
            throw new ArgumentException("A JWT-SVID requires an audience.", nameof(audience));
        }

        _logger.LogDebug(
            "Fetching a JWT-SVID for audience {Audience} from the agent at {Socket}", audience, _socketPath);

        JWTSVIDResponse response;
        try
        {
            response = await _client.FetchJWTSVIDAsync(
                new JWTSVIDRequest { Audience = { audience } },
                _headers,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            // Translated into what to DO, because this surfaces inside somebody's application startup
            // and the gRPC status alone ("Unavailable") starts the wrong investigation.
            throw new InvalidOperationException(Describe(ex, audience), ex);
        }

        var svid = response.Svids.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The SPIFFE agent at {_socketPath} returned no JWT-SVID for audience '{audience}'. "
                + "Run 'bella spiffe status' to see whether it holds an identity at all.");

        if (string.IsNullOrWhiteSpace(svid.Svid))
        {
            throw new InvalidOperationException(
                $"The SPIFFE agent at {_socketPath} returned an empty JWT-SVID for audience "
                + $"'{audience}'.");
        }

        _logger.LogDebug("Received a JWT-SVID for {SpiffeId}", svid.SpiffeId);
        return svid.Svid;
    }

    private string Describe(RpcException ex, string audience) => ex.StatusCode switch
    {
        StatusCode.Unavailable =>
            $"No SPIFFE agent is answering at {_socketPath}. Start one with 'bella spiffe agent', or "
            + $"point {SpiffeAgentDetection.EndpointSocketVariable} at the right socket. "
            + $"({ex.Status.Detail})",
        StatusCode.PermissionDenied =>
            $"The SPIFFE agent refused to mint a JWT-SVID: {ex.Status.Detail} This is an attestation "
            + "failure rather than a transport problem — 'bella spiffe whoami' shows the evidence this "
            + "host can present.",
        StatusCode.InvalidArgument =>
            $"The SPIFFE agent rejected the request for audience '{audience}': {ex.Status.Detail}",
        StatusCode.Unimplemented =>
            $"The process listening at {_socketPath} does not implement the SPIFFE Workload API. "
            + "Check that it is a SPIFFE agent and not another service using the same path.",
        _ => $"Fetching a JWT-SVID from the agent at {_socketPath} failed with {ex.StatusCode}: "
             + ex.Status.Detail,
    };

    /// <inheritdoc />
    public void Dispose() => _channel.Dispose();
}
