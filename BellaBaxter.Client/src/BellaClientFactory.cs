using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace BellaBaxter.Client;

/// <summary>
/// Factory for creating authenticated BellaClient instances.
/// </summary>
public static class BellaClientFactory
{
    /// <summary>
    /// Creates a BellaClient with a static Bearer token and Polly resilience pipeline.
    /// E2E encryption is always enabled — a P-256 keypair is generated per instance and
    /// <c>X-E2E-Public-Key</c> is sent with every secrets request so the server encrypts
    /// the response. Decryption happens automatically.
    /// Suitable for service-to-service calls where an access token is obtained externally.
    /// </summary>
    public static BellaClient CreateWithBearerToken(string baseUrl, string accessToken,
        DelegatingHandler? outerHandler = null)
    {
        var services = new ServiceCollection();
        var builder = services
            .AddHttpClient(
                "BellaBearerClient",
                client =>
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                }
            );

        if (outerHandler is not null)
            builder.AddHttpMessageHandler(() => outerHandler);

        builder
            .AddHttpMessageHandler(() => new E2EEncryptionHandler())
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.UseJitter = true;
            });

        var httpClient = services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("BellaBearerClient");

        var authProvider = new BaseBearerTokenAuthenticationProvider(
            new StaticAccessTokenProvider(accessToken)
        );
        var adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
        adapter.BaseUrl = baseUrl.TrimEnd('/');
        return new BellaClient(adapter);
    }

    /// <summary>
    /// Creates a <see cref="BellaClient"/> that authenticates with a Bella Baxter bax- API key
    /// using HMAC-SHA256 request signing (the native Bella auth scheme).
    /// End-to-end encryption is always enabled — a P-256 keypair is generated and
    /// <c>X-E2E-Public-Key</c> is sent with every secrets request so the server encrypts
    /// the response. Decryption happens automatically.
    /// </summary>
    /// <param name="baseUrl">Base URL of the Baxter API (e.g. https://baxter.example.com).</param>
    /// <param name="apiKey">A bax- API key obtained from the WebApp or CLI.</param>
    /// <param name="outerHandler">Optional outermost delegating handler (e.g. for debug logging). Inserted before all auth handlers.</param>
    /// <param name="bellaClient">Identifies the SDK/tool for audit logging (default: "bella-dotnet-sdk"). Sent as X-Bella-Client header.</param>
    /// <param name="appClient">Optional user application name for audit logging (e.g. "my-web-api"). Sent as X-App-Client header if provided.</param>
    public static BellaClient CreateWithHmacApiKey(
        string baseUrl,
        string apiKey,
        DelegatingHandler? outerHandler = null,
        string bellaClient = "bella-dotnet-sdk",
        string? appClient = null)
    {
        var services = new ServiceCollection();
        var httpClientBuilder = services
            .AddHttpClient(
                "BellaHmacClient",
                client =>
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"{bellaClient}/1.0");
                }
            );

        if (outerHandler is not null)
            httpClientBuilder.AddHttpMessageHandler(() => outerHandler);

        httpClientBuilder
            .AddHttpMessageHandler(() => new HmacSigningHandler(apiKey, bellaClient, appClient))
            .AddHttpMessageHandler(() => new E2EEncryptionHandler());

        httpClientBuilder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.UseJitter = true;
        });

        var httpClient = services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("BellaHmacClient");

        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
        adapter.BaseUrl = baseUrl.TrimEnd('/');
        return new BellaClient(adapter);
    }

    /// <summary>
    /// Creates a <see cref="BellaClient"/> with Bearer token auth and zero-knowledge encryption (ZKE).
    /// Uses a persistent <paramref name="zkeHandler"/> instead of the ephemeral-key
    /// <see cref="E2EEncryptionHandler"/>, so the server can wrap the project DEK for this identity.
    /// </summary>
    /// <param name="zkeHandler">
    ///   A <see cref="ZkeDekHandler"/> holding the caller's persistent P-256 private key.
    ///   Set <see cref="ZkeDekHandler.OnWrappedDekReceived"/> before passing to cache DEK leases.
    /// </param>
    public static BellaClient CreateWithBearerTokenAndZke(
        string baseUrl,
        string accessToken,
        ZkeDekHandler zkeHandler,
        DelegatingHandler? outerHandler = null)
    {
        var services = new ServiceCollection();
        var builder = services
            .AddHttpClient(
                "BellaBearerZkeClient",
                client =>
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                }
            );

        if (outerHandler is not null)
            builder.AddHttpMessageHandler(() => outerHandler);

        builder
            .AddHttpMessageHandler(() => zkeHandler)
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.UseJitter = true;
            });

        var httpClient = services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("BellaBearerZkeClient");

        var authProvider = new BaseBearerTokenAuthenticationProvider(
            new StaticAccessTokenProvider(accessToken)
        );
        var adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
        adapter.BaseUrl = baseUrl.TrimEnd('/');
        return new BellaClient(adapter);
    }

    /// <summary>
    /// Creates a <see cref="BellaClient"/> with HMAC API key auth and zero-knowledge encryption (ZKE).
    /// Uses a persistent <paramref name="zkeHandler"/> instead of the ephemeral-key
    /// <see cref="E2EEncryptionHandler"/>, so the server can wrap the project DEK for this identity.
    /// </summary>
    /// <param name="zkeHandler">
    ///   A <see cref="ZkeDekHandler"/> holding the caller's persistent P-256 private key.
    ///   Set <see cref="ZkeDekHandler.OnWrappedDekReceived"/> before passing to cache DEK leases.
    /// </param>
    public static BellaClient CreateWithHmacApiKeyAndZke(
        string baseUrl,
        string apiKey,
        ZkeDekHandler zkeHandler,
        DelegatingHandler? outerHandler = null,
        string bellaClient = "bella-dotnet-sdk",
        string? appClient = null)
    {
        var services = new ServiceCollection();
        var httpClientBuilder = services
            .AddHttpClient(
                "BellaHmacZkeClient",
                client =>
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"{bellaClient}/1.0");
                }
            );

        if (outerHandler is not null)
            httpClientBuilder.AddHttpMessageHandler(() => outerHandler);

        httpClientBuilder
            .AddHttpMessageHandler(() => new HmacSigningHandler(apiKey, bellaClient, appClient))
            .AddHttpMessageHandler(() => zkeHandler);

        httpClientBuilder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.UseJitter = true;
        });

        var httpClient = services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("BellaHmacZkeClient");

        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
        adapter.BaseUrl = baseUrl.TrimEnd('/');
        return new BellaClient(adapter);
    }


    public static BellaClient CreateWithApiKey(string baseUrl, string apiKey, string secretKey)
    {
        var httpClient = BuildHttpClient(baseUrl);
        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        httpClient.DefaultRequestHeaders.Add("X-Secret-Key", secretKey);

        var authProvider = new AnonymousAuthenticationProvider();
        var adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
        adapter.BaseUrl = baseUrl.TrimEnd('/');
        return new BellaClient(adapter);
    }

    /// <summary>
    /// Creates a BellaClient registered via Microsoft.Extensions.DependencyInjection,
    /// with Polly standard resilience (retry + circuit breaker) configured on the HttpClient.
    /// </summary>
    public static IServiceCollection AddBellaClient(
        this IServiceCollection services,
        string baseUrl,
        Action<IHttpClientBuilder>? configure = null
    )
    {
        var clientBuilder = services.AddHttpClient(
            "BellaClient",
            client =>
            {
                client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            }
        );

        clientBuilder.AddStandardResilienceHandler(options =>
        {
            // Retry up to 3 times with exponential back-off
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.UseJitter = true;
        });

        configure?.Invoke(clientBuilder);

        services.AddScoped<BellaClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("BellaClient");
            var authProvider = new AnonymousAuthenticationProvider(); // token injected via interceptor
            var adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
            adapter.BaseUrl = baseUrl.TrimEnd('/');
            return new BellaClient(adapter);
        });

        return services;
    }

    /// <summary>
    /// Creates a <see cref="BellaClient"/> with no authentication.
    /// Use for pre-auth calls such as OIDC token exchange, where you don't yet have
    /// a Bella API key or bearer token.
    /// </summary>
    public static BellaClient CreateAnonymous(string baseUrl)
    {
        var httpClient = BuildHttpClient(baseUrl);
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
        adapter.BaseUrl = baseUrl.TrimEnd('/');
        return new BellaClient(adapter);
    }

    private static HttpClient BuildHttpClient(string baseUrl)
    {
        var services = new ServiceCollection();
        services
            .AddHttpClient(
                "BellaClient",
                client =>
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                }
            )
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.UseJitter = true;
            });

        return services
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("BellaClient");
    }
}

/// <summary>
/// Simple token provider for static Bearer tokens.
/// </summary>
internal sealed class StaticAccessTokenProvider(string token) : IAccessTokenProvider
{
    public Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(token);

    public AllowedHostsValidator AllowedHostsValidator { get; } = new();
}
