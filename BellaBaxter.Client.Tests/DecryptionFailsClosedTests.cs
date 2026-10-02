using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Crypto;
using Xunit;

namespace BellaBaxter.Client.Tests;

/// <summary>
/// Issue #1050 (a) — a secrets response the .NET SDK cannot decrypt is an ERROR, never a 2xx built from bytes that
/// were not decrypted.
/// </summary>
/// <remarks>
/// <para><c>E2EEncryptionHandler</c> and <c>ZkeDekHandler</c> caught a failed decryption, wrote one line to stderr and
/// returned the response as-is — so the caller received the still-encrypted envelope (or, for the at-rest layer, the
/// <c>bellabaxter:v1:</c> ciphertext) as if it were the secrets. Constitution Principle I: "A <c>catch</c> that swallows
/// a verification failure and continues is forbidden."</para>
/// <para>The controls matter as much: a body that is not an encrypted envelope at all (a dotenv export on a
/// <c>/secrets</c> path) passes through untouched, and a genuine envelope still decrypts.</para>
/// </remarks>
public class DecryptionFailsClosedTests
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

    private static string Envelope(string plaintext, string clientPublicKey, bool tamper)
    {
        var payload = EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(plaintext), clientPublicKey);
        if (tamper)
        {
            var tag = Convert.FromBase64String(payload.Tag);
            tag[0] ^= 0xFF;
            payload = payload with { Tag = Convert.ToBase64String(tag) };
        }
        return JsonSerializer.Serialize(payload, Wire);
    }

    private const string Plain = """{"secrets":{"DATABASE_URL":"postgres://db/app"},"version":7}""";

    // ── E2EEncryptionHandler ─────────────────────────────────────────────────

    [Fact]
    public async Task E2E_a_tampered_envelope_throws_instead_of_returning_ciphertext()
    {
        var handler = new E2EEncryptionHandler();
        handler.InnerHandler = new Stub(() => Json(Envelope(Plain, handler.PublicKeyBase64, tamper: true)));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<E2EDecryptionException>(() => client.GetAsync(SecretsUrl));
    }

    [Fact]
    public async Task E2E_a_genuine_envelope_still_decrypts()
    {
        var handler = new E2EEncryptionHandler();
        handler.InnerHandler = new Stub(() => Json(Envelope(Plain, handler.PublicKeyBase64, tamper: false)));
        using var client = new HttpClient(handler);

        var body = await (await client.GetAsync(SecretsUrl)).Content.ReadAsStringAsync();
        Assert.Contains("postgres://db/app", body);
    }

    [Fact]
    public async Task E2E_a_body_that_is_not_an_envelope_passes_through_untouched()
    {
        // A dotenv export lives on a /secrets path and is not JSON at all; that is not a decryption failure.
        var handler = new E2EEncryptionHandler { InnerHandler = new Stub(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("DATABASE_URL=postgres://db/app\n", Encoding.UTF8, "text/plain"),
        }) };
        using var client = new HttpClient(handler);

        var body = await (await client.GetAsync(SecretsUrl + "/export")).Content.ReadAsStringAsync();
        Assert.Equal("DATABASE_URL=postgres://db/app\n", body);
    }

    // ── ZkeDekHandler: transport layer ───────────────────────────────────────

    [Fact]
    public async Task Zke_a_tampered_envelope_throws_instead_of_returning_ciphertext()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ZkeDekHandler(key);
        handler.InnerHandler = new Stub(() => Json(Envelope(Plain, handler.PublicKeyBase64, tamper: true)));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<E2EDecryptionException>(() => client.GetAsync(SecretsUrl));
    }

    [Fact]
    public async Task Zke_a_genuine_envelope_still_decrypts()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ZkeDekHandler(key);
        handler.InnerHandler = new Stub(() => Json(Envelope(Plain, handler.PublicKeyBase64, tamper: false)));
        using var client = new HttpClient(handler);

        var body = await (await client.GetAsync(SecretsUrl)).Content.ReadAsStringAsync();
        Assert.Contains("postgres://db/app", body);
    }

    // ── ZkeDekHandler: at-rest layer (bellabaxter:v1:) ───────────────────────

    private static string WrappedDek(byte[] dek, string clientPublicKey) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(EciesAlgorithm.Encrypt(dek, clientPublicKey))));

    [Fact]
    public async Task Zke_an_at_rest_value_that_will_not_decrypt_throws()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ZkeDekHandler(key);
        var dek = DekAlgorithm.GenerateDek();
        var otherDek = DekAlgorithm.GenerateDek();
        var cipher = DekAlgorithm.Encrypt("postgres://db/app", otherDek); // encrypted under a DIFFERENT key
        handler.InnerHandler = new Stub(() => Json(
            $$"""{"secrets":{"DATABASE_URL":"{{cipher}}"},"version":7}""",
            ("X-Bella-Wrapped-Dek", WrappedDek(dek, handler.PublicKeyBase64))));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<E2EDecryptionException>(() => client.GetAsync(SecretsUrl));
    }

    [Fact]
    public async Task Zke_an_at_rest_value_with_a_wrapped_DEK_it_cannot_unwrap_throws()
    {
        // The DEK was wrapped for someone else; the values stay ciphertext and must not be handed back as secrets.
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var someoneElse = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ZkeDekHandler(key);
        var dek = DekAlgorithm.GenerateDek();
        var cipher = DekAlgorithm.Encrypt("postgres://db/app", dek);
        var wrappedForOther = WrappedDek(dek, Convert.ToBase64String(someoneElse.ExportSubjectPublicKeyInfo()));
        handler.InnerHandler = new Stub(() => Json(
            $$"""{"secrets":{"DATABASE_URL":"{{cipher}}"},"version":7}""",
            ("X-Bella-Wrapped-Dek", wrappedForOther)));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<E2EDecryptionException>(() => client.GetAsync(SecretsUrl));
    }

    [Fact]
    public async Task Zke_at_rest_values_under_the_right_DEK_decrypt_and_plain_values_pass()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ZkeDekHandler(key);
        var dek = DekAlgorithm.GenerateDek();
        var cipher = DekAlgorithm.Encrypt("postgres://db/app", dek);
        handler.InnerHandler = new Stub(() => Json(
            $$"""{"secrets":{"DATABASE_URL":"{{cipher}}","PORT":"8080"},"version":7}""",
            ("X-Bella-Wrapped-Dek", WrappedDek(dek, handler.PublicKeyBase64))));
        using var client = new HttpClient(handler);

        var body = await (await client.GetAsync(SecretsUrl)).Content.ReadAsStringAsync();
        Assert.Contains("postgres://db/app", body);
        Assert.Contains("8080", body);
        Assert.DoesNotContain(DekAlgorithm.Prefix, body);
    }
}
