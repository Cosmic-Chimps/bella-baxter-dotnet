using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Crypto;
using Xunit;

namespace BellaBaxter.Client.Tests;

/// <summary>
/// spec 077 (T034) — the bound <c>bellabaxter:v2:</c> envelope and the narrowed wrapped keys, as the .NET
/// SDK (and through it the CLI) receives them.
/// </summary>
/// <remarks>
/// The server returns every value decrypted, so a bound value never reaches a client in practice. Opening
/// one needs the tenant, project and environment identifiers, which the frozen secrets response does not
/// carry, so the client never tries: a bound value that does arrive is refused, never handed back as the
/// secret. The narrowed headers keep the shape older clients unwrap.
/// </remarks>
public class ZkeDekHandlerBoundFormatTests
{
    private const string SecretsUrl = "https://api.example.test/api/v1/projects/p/environments/e/secrets";
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    private static HttpResponseMessage Json(string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private static string Envelope(string plaintext, string clientPublicKey) =>
        JsonSerializer.Serialize(EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(plaintext), clientPublicKey), Wire);

    private static string Wrapped(byte[] key, string clientPublicKey) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(EciesAlgorithm.Encrypt(key, clientPublicKey))));

    private static readonly SecretBinding Binding =
        SecretBinding.ForEnvironment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "DATABASE_URL");

    [Fact]
    public async Task A_bound_value_in_a_response_is_refused_not_returned_as_the_secret()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ZkeDekHandler(key);
        var environmentKey = DekAlgorithm.GenerateDek();
        var bound = DekAlgorithm.EncryptBound("postgres://db/app", environmentKey, Binding);
        handler.InnerHandler = new Stub(() => Json(
            Envelope($$"""{"secrets":{"DATABASE_URL":"{{bound}}"},"version":7}""", handler.PublicKeyBase64),
            ("X-Bella-Wrapped-Dek", Wrapped(environmentKey, handler.PublicKeyBase64))));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<E2EEResponseException>(() => client.GetAsync(SecretsUrl));
        Assert.Equal(E2EEResponseException.DecryptionFailed, ex.Code);
    }

    [Fact]
    public async Task Plain_values_with_both_narrowed_headers_read_as_before()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        string? cachedEnvironmentKey = null;
        var handler = new ZkeDekHandler(key, (_, _, wrapped, _) => cachedEnvironmentKey = wrapped);
        var environmentKey = DekAlgorithm.GenerateDek();
        var sharedKey = DekAlgorithm.GenerateDek();
        handler.InnerHandler = new Stub(() => Json(
            Envelope("""{"secrets":{"DATABASE_URL":"postgres://db/app"},"version":7}""", handler.PublicKeyBase64),
            ("X-Bella-Wrapped-Dek", Wrapped(environmentKey, handler.PublicKeyBase64)),
            ("X-Bella-Wrapped-Shared-Dek", Wrapped(sharedKey, handler.PublicKeyBase64))));
        using var client = new HttpClient(handler);

        var body = await (await client.GetAsync(SecretsUrl)).Content.ReadAsStringAsync();

        Assert.Contains("postgres://db/app", body);
        // The header keeps its shape: one 32-byte key, unwrapped with the same call as before.
        Assert.Equal(environmentKey, handler.DecryptWrappedDek(cachedEnvironmentKey!));
    }

    [Fact]
    public async Task A_response_with_only_the_original_header_still_reads()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ZkeDekHandler(key);
        handler.InnerHandler = new Stub(() => Json(
            Envelope("""{"secrets":{"DATABASE_URL":"postgres://db/app"},"version":7}""", handler.PublicKeyBase64),
            ("X-Bella-Wrapped-Dek", Wrapped(DekAlgorithm.GenerateDek(), handler.PublicKeyBase64))));
        using var client = new HttpClient(handler);

        var body = await (await client.GetAsync(SecretsUrl)).Content.ReadAsStringAsync();
        Assert.Contains("postgres://db/app", body);
    }
}
