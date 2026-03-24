using System.Security.Cryptography;
using System.Text;

namespace BellaBaxter.Client;

/// <summary>
/// DelegatingHandler that adds HMAC-SHA256 signing headers to every outgoing request.
/// Implements the <c>bax-{keyId}-{signingSecret}</c> authentication scheme used by
/// the Bella Baxter API.
///
/// Add this to the HttpClient pipeline via <see cref="BellaClientFactory.CreateWithHmacApiKey"/>.
/// </summary>
public sealed class HmacSigningHandler : DelegatingHandler
{
    private readonly string _keyId;
    private readonly byte[] _signingSecret;
    private readonly string _bellaClient;
    private readonly string? _appClient;

    /// <summary>
    /// Creates a new <see cref="HmacSigningHandler"/> for the given bax- API key.
    /// </summary>
    /// <param name="apiKey">The bax- API key.</param>
    /// <param name="bellaClient">Identifies the SDK/tool (e.g. "bella-cli", "bella-dotnet-sdk"). Sent as X-Bella-Client header.</param>
    /// <param name="appClient">Optional user application name (e.g. "my-web-api"). Sent as X-App-Client if provided.</param>
    public HmacSigningHandler(string apiKey, string bellaClient = "bella-dotnet-sdk", string? appClient = null)
    {
        var parts = apiKey.Split('-', 3);
        if (parts.Length != 3 || parts[0] != "bax")
            throw new ArgumentException("ApiKey must be in format bax-{keyId}-{signingSecret}", nameof(apiKey));
        _keyId = parts[1];
        _signingSecret = Convert.FromHexString(parts[2]);
        _bellaClient = bellaClient;
        _appClient = appClient;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;

        // Build sorted query string (same order as server validates)
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
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var stringToSign = $"{method}\n{path}\n{query}\n{timestamp}\n{bodyHash}";

        using var hmac = new HMACSHA256(_signingSecret);
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();

        request.Headers.TryAddWithoutValidation("X-Bella-Key-Id", _keyId);
        request.Headers.TryAddWithoutValidation("X-Bella-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Bella-Signature", sig);
        request.Headers.TryAddWithoutValidation("X-Bella-Client", _bellaClient);
        if (_appClient is not null)
            request.Headers.TryAddWithoutValidation("X-App-Client", _appClient);

        return await base.SendAsync(request, cancellationToken);
    }
}
