using System.Linq;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BellaBaxter.Spiffe.Client;

/// <summary>
/// A <see cref="DelegatingHandler"/> that transparently handles SPIFFE-based
/// authentication for the Bella Baxter API.
///
/// On each request:
/// 1. Checks the cached bax- lease (renews early if near expiry).
/// 2. If missing or expired: fetches a JWT-SVID from the bella agent, then
///    exchanges it for a bax- lease via <c>POST /api/v1/environments/{envId}/svid/exchange</c>.
/// 3. Signs the outgoing request with the bax- lease using HMAC-SHA256.
///
/// Thread-safe: a <see cref="SemaphoreSlim"/> ensures only one refresh runs at a time.
/// </summary>
public sealed class SpiffeTokenHandler : DelegatingHandler
{
    private readonly ISpiffeWorkloadClient _workloadClient;
    private readonly SpiffeClientOptions _options;
    private readonly ILogger<SpiffeTokenHandler> _logger;

    // Exchange endpoint: POST {base}/api/v1/environments/{envId}/svid/exchange
    private readonly string _exchangeUrl;

    // Internal http client for the anonymous exchange call (no auth yet)
    private readonly HttpClient _exchangeHttp;

    private string? _cachedBaxToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    internal SpiffeTokenHandler(
        ISpiffeWorkloadClient workloadClient,
        SpiffeClientOptions options,
        HttpClient exchangeHttp,
        ILogger<SpiffeTokenHandler> logger)
    {
        _workloadClient = workloadClient;
        _options = options;
        _exchangeHttp = exchangeHttp;
        _logger = logger;
        _exchangeUrl = $"{options.BellaBaseUrl.TrimEnd('/')}/api/v1/environments/{options.EnvironmentId}/svid/exchange";
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetOrRefreshTokenAsync(cancellationToken);

        // Apply HMAC signing using the cached bax- token (mirrors HmacSigningHandler exactly)
        await ApplyHmacHeadersAsync(request, token, cancellationToken);

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetOrRefreshTokenAsync(CancellationToken cancellationToken)
    {
        // Fast path: token is still valid
        if (_cachedBaxToken is not null &&
            DateTimeOffset.UtcNow < _tokenExpiresAt - _options.ExpiryBufferDuration)
        {
            return _cachedBaxToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_cachedBaxToken is not null &&
                DateTimeOffset.UtcNow < _tokenExpiresAt - _options.ExpiryBufferDuration)
            {
                return _cachedBaxToken;
            }

            _logger.LogDebug("bax- lease missing or near expiry — refreshing via SPIFFE");

            var svid = await _workloadClient.FetchJwtSvidAsync(_options.SvidAudience, cancellationToken);
            var (token, expiresAt) = await ExchangeSvidForBaxTokenAsync(svid, cancellationToken);

            _cachedBaxToken = token;
            _tokenExpiresAt = expiresAt;

            _logger.LogInformation("SPIFFE: obtained bax- lease, expires {ExpiresAt}", expiresAt);
            return token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> ExchangeSvidForBaxTokenAsync(
        string svid, CancellationToken cancellationToken)
    {
        var body = new ExchangeSvidCommandDto(svid);
        using var response = await _exchangeHttp.PostAsJsonAsync(
            _exchangeUrl, body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"JWT-SVID exchange failed ({(int)response.StatusCode}): {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<ExchangeResponseDto>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("SVID exchange returned an empty response.");

        if (string.IsNullOrWhiteSpace(result.Token))
            throw new InvalidOperationException("SVID exchange response missing 'token' field.");

        var expiresAt = result.ExpiresAt ?? DateTimeOffset.UtcNow.AddHours(1);
        return (result.Token, expiresAt);
    }

    private async Task ApplyHmacHeadersAsync(HttpRequestMessage request, string baxToken, CancellationToken cancellationToken)
    {
        // Parse bax-{keyId}-{signingSecret} — secret is hex-encoded (matches HmacSigningHandler)
        var parts = baxToken.Split('-', 3);
        if (parts.Length != 3 || parts[0] != "bax")
            throw new InvalidOperationException(
                "Unexpected bax- token format returned by SVID exchange. " +
                "Expected 'bax-{keyId}-{signingSecret}'.");

        var keyId = parts[1];
        var signingSecretBytes = Convert.FromHexString(parts[2]);

        var method = request.Method.Method.ToUpperInvariant();
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;

        // Build sorted query string (mirrors HmacSigningHandler exactly)
        var query = string.Empty;
        if (!string.IsNullOrEmpty(uri.Query) && uri.Query.Length > 1)
        {
            var rawQuery = uri.Query.TrimStart('?');
            query = string.Join("&",
                rawQuery
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair =>
                    {
                        var idx = pair.IndexOf('=');
                        return idx < 0
                            ? pair
                            : $"{Uri.EscapeDataString(Uri.UnescapeDataString(pair[..idx]))}={Uri.EscapeDataString(Uri.UnescapeDataString(pair[(idx + 1)..]))}";
                    })
                    .OrderBy(x => x, StringComparer.Ordinal));
        }

        byte[] body = [];
        if (request.Content is not null)
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var bodyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        var stringToSign = $"{method}\n{path}\n{query}\n{timestamp}\n{bodyHash}";

        using var hmac = new System.Security.Cryptography.HMACSHA256(signingSecretBytes);
        var sig = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();

        request.Headers.TryAddWithoutValidation("X-Bella-Key-Id", keyId);
        request.Headers.TryAddWithoutValidation("X-Bella-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Bella-Signature", sig);
        request.Headers.TryAddWithoutValidation("X-Bella-Client", _options.BellaClient);

        if (_options.AppClient is not null)
            request.Headers.TryAddWithoutValidation("X-App-Client", _options.AppClient);
    }

    // Minimal DTOs for the anonymous exchange call (avoid Kiota dependency from handler)
    private sealed record ExchangeSvidCommandDto(
        [property: JsonPropertyName("jwtSvid")] string JwtSvid);

    private sealed record ExchangeResponseDto(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);
}
