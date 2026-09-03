using BellaBaxter.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Spiffe.Client;

/// <summary>
/// Creates <see cref="BellaClient"/> instances that authenticate via SPIFFE JWT-SVIDs
/// obtained from a local bella agent sidecar.
///
/// <para>
/// <b>How it works:</b>
/// <list type="number">
///   <item>The bella agent sidecar exposes a local HTTP endpoint at <c>http://localhost:8088</c>.</item>
///   <item>On first request, <see cref="SpiffeTokenHandler"/> calls <c>GET /v1/svid/jwt?audience=bella-api</c>
///         to obtain a short-lived JWT-SVID signed by OpenBao PKI.</item>
///   <item>The JWT-SVID is exchanged for a scoped <c>bax-</c> lease via
///         <c>POST /api/v1/environments/{envId}/svid/exchange</c>.</item>
///   <item>All subsequent requests are signed with the <c>bax-</c> lease using HMAC-SHA256.</item>
///   <item>The lease is automatically refreshed before expiry (default: 2 minutes before).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pre-requisites:</b>
/// <list type="bullet">
///   <item>bella agent running as sidecar (or daemon) with the workload registered via <c>bella spire add</c>.</item>
///   <item>The <c>WorkloadIdentity</c> registered in Bella Baxter for this workload's selectors.</item>
/// </list>
/// </para>
/// </summary>
public static class BellaSpiffeClientFactory
{
    /// <summary>
    /// Creates a <see cref="BellaClient"/> that obtains credentials automatically via SPIFFE.
    /// No static API keys required — the workload identity is attested by bella agent.
    /// </summary>
    /// <param name="options">SPIFFE client configuration.</param>
    /// <param name="workloadClient">
    ///   Optional custom <see cref="ISpiffeWorkloadClient"/>.
    ///   Defaults to <see cref="BellaAgentHttpClient"/> pointing at <see cref="SpiffeClientOptions.AgentBaseUrl"/>.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory for diagnostics.</param>
    /// <returns>A fully configured <see cref="BellaClient"/>.</returns>
    public static BellaClient Create(
        SpiffeClientOptions options,
        ISpiffeWorkloadClient? workloadClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        // Default to the REAL agent interface (spec 001 T044): the SPIFFE Workload API over a Unix
        // socket. The previous default called an HTTP endpoint at localhost:8088 that no agent has ever
        // served, so this path could not have worked against a live agent.
        var agentClient = workloadClient ?? BuildWorkloadApiClient(options, loggerFactory);

        // Build an anonymous HTTP client for the one-off SVID exchange call
        var exchangeHttp = BuildExchangeHttpClient(options);

        // The SpiffeTokenHandler wraps all outgoing Bella API requests
        var spiffeHandler = new SpiffeTokenHandler(
            agentClient,
            options,
            exchangeHttp,
            loggerFactory.CreateLogger<SpiffeTokenHandler>());

        // Build the Bella API HttpClient (E2EE + SPIFFE auth + resilience)
        var services = new ServiceCollection();
        services.AddHttpClient("BellaSpiffeClient", client =>
            {
                client.BaseAddress = new Uri(options.BellaBaseUrl.TrimEnd('/') + "/");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .AddHttpMessageHandler(() => new E2EEncryptionHandler())
            .AddHttpMessageHandler(() => spiffeHandler)
            .AddStandardResilienceHandler(resilienceOptions =>
            {
                resilienceOptions.Retry.MaxRetryAttempts = 3;
                resilienceOptions.Retry.UseJitter = true;
            });

        var httpClient = services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("BellaSpiffeClient");

        var adapter = new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: httpClient);
        adapter.BaseUrl = options.BellaBaseUrl.TrimEnd('/');
        return new BellaClient(adapter);
    }

    /// <summary>
    /// Creates a client that works unchanged in a pod, on a laptop and in CI (FR-028).
    /// </summary>
    /// <remarks>
    /// <para>Detects how this process can authenticate and uses it: a SPIFFE agent socket if one is
    /// present, otherwise an API key from <c>BELLA_API_KEY</c>. Two rules make that safe rather than
    /// merely convenient, and both are in <see cref="SpiffeAgentDetection"/>:</para>
    /// <list type="number">
    ///   <item>The AGENT WINS when present, even if an API key is also set. Preferring the key would
    ///   let a workload that was deliberately given an attested identity fall back silently to a
    ///   long-lived shared secret — the exact thing this feature removes.</item>
    ///   <item>Neither available is a REFUSAL, not an unauthenticated client. Returning one would defer
    ///   the failure to the first API call, where it arrives as a 401 that looks like a credential
    ///   problem rather than a configuration one.</item>
    /// </list>
    /// </remarks>
    /// <param name="options">SPIFFE client configuration.</param>
    /// <param name="apiKeyClientFactory">
    /// How to build a client from an API key, when that is the mode. Supplied by the caller because
    /// this package deliberately does not own the static-key path — mirroring it here would create a
    /// second implementation of it to keep in step with the main SDK.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <exception cref="InvalidOperationException">Neither an agent nor an API key is available.</exception>
    public static BellaClient CreateAutoDetect(
        SpiffeClientOptions options,
        Func<string, BellaClient> apiKeyClientFactory,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(nameof(BellaSpiffeClientFactory));

        var detection = SpiffeAgentDetection.Detect();

        // Logged at Information, not Debug: WHICH credential a workload ended up using is exactly the
        // fact an incident timeline needs, and it is invisible from the outside.
        logger.LogInformation("{Description}", detection.Description);

        switch (detection.Mode)
        {
            case SpiffeCredentialMode.WorkloadApi:
                return Create(
                    options,
                    new WorkloadApiSvidClient(
                        detection.SocketPath!, loggerFactory.CreateLogger<WorkloadApiSvidClient>()),
                    loggerFactory);

            case SpiffeCredentialMode.ApiKey:
                var key = Environment.GetEnvironmentVariable(SpiffeAgentDetection.ApiKeyVariable)!;
                return apiKeyClientFactory(key);

            default:
                throw new InvalidOperationException(
                    SpiffeAgentDetection.NoCredentialsMessage(detection));
        }
    }

    private static ISpiffeWorkloadClient BuildWorkloadApiClient(
        SpiffeClientOptions options, ILoggerFactory loggerFactory)
    {
        // The socket is resolved the same way every SPIFFE client resolves it, rather than from
        // SpiffeClientOptions: SPIFFE_ENDPOINT_SOCKET is the spec's own portability mechanism, so a
        // package-specific setting would be one more place for the two to disagree.
        var detection = SpiffeAgentDetection.Detect();

        return new WorkloadApiSvidClient(
            detection.SocketPath!, loggerFactory.CreateLogger<WorkloadApiSvidClient>());
    }

    [Obsolete("Retained only for the obsolete BellaAgentHttpClient; see CreateAutoDetect.")]
    private static ISpiffeWorkloadClient BuildAgentHttpClient(
        SpiffeClientOptions options, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("BellaAgent", client =>
        {
            client.BaseAddress = new Uri(options.AgentBaseUrl.TrimEnd('/') + "/");
        });

        var httpClient = services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("BellaAgent");

        return new BellaAgentHttpClient(
            httpClient,
            loggerFactory.CreateLogger<BellaAgentHttpClient>());
    }

    private static HttpClient BuildExchangeHttpClient(SpiffeClientOptions options)
    {
        // Anonymous, single-purpose client for the SVID exchange endpoint
        return new HttpClient
        {
            BaseAddress = new Uri(options.BellaBaseUrl.TrimEnd('/') + "/"),
            DefaultRequestHeaders = { { "Accept", "application/json" } }
        };
    }
}

/// <summary>
/// No-op authentication provider — auth is handled by <see cref="SpiffeTokenHandler"/> in the pipeline.
/// </summary>
internal sealed class AnonymousAuthenticationProvider : IAuthenticationProvider
{
    public Task AuthenticateRequestAsync(
        global::Microsoft.Kiota.Abstractions.RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
