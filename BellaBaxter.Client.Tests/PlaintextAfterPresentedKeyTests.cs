using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Crypto;
using Xunit;

namespace BellaBaxter.Client.Tests;

/// <summary>
/// Issue #1050 (b) — once the SDK has presented its E2EE key on a read the server always encrypts, the answer is an
/// envelope that decrypts to this key or it is an <see cref="E2EEResponseException"/>. Never a value.
/// </summary>
/// <remarks>
/// <para>The stub is the misbehaving server: it answers the presented key with (1) a genuine envelope, (2) the plain
/// secrets JSON — what a header-stripping intermediary or a server regression of the #636 class would serve, (3) a
/// genuine envelope with one ciphertext byte flipped, (4) an envelope encrypted to a different key. Only (1) may yield
/// the sentinel. Both handlers are driven, because the CLI wires <see cref="ZkeDekHandler"/> and the factory wires
/// <see cref="E2EEncryptionHandler"/>; a rule that held in only one would hold for only half the callers.</para>
/// <para>The controls: reads the server answers in plain JSON even with the key (<c>…/secrets/version</c>,
/// <c>/hash</c>, <c>/{key}/metadata</c>, writes) still pass, and a non-2xx answer stays the API error it was.</para>
/// <para>The same four cases run end to end, against every SDK and the real CLI, in
/// <c>apps/sdk/contract-tests/run.sh</c>.</para>
/// </remarks>
public class PlaintextAfterPresentedKeyTests
{
    private const string Base = "https://api.example.test/api/v1/projects/p/environments/e";
    private const string Sentinel = "postgres://sentinel/app";
    private const string Plain = $$"""{"environmentSlug":"e","environmentName":"E","secrets":{"DATABASE_URL":"{{Sentinel}}"},"version":7,"lastModified":"2026-10-04T00:00:00Z"}""";
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public enum Answer { Envelope, Plaintext, Dotenv, Tampered, WrongKey }

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? PresentedKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            PresentedKey = request.Headers.TryGetValues("X-E2E-Public-Key", out var v) ? v.Single() : null;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body, string mediaType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Serve(Answer answer, string? presentedKey)
    {
        Assert.NotNull(presentedKey); // every case below is about a client that DID present its key
        switch (answer)
        {
            case Answer.Plaintext: return Ok(Plain);
            case Answer.Dotenv: return Ok($"DATABASE_URL={Sentinel}\n", "text/plain");
            case Answer.WrongKey:
                using (var other = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                {
                    var payload = EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(Plain),
                        Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()));
                    return Ok(JsonSerializer.Serialize(payload, Wire));
                }
            default:
                var envelope = EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(Plain), presentedKey);
                if (answer == Answer.Tampered)
                {
                    var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
                    ciphertext[0] ^= 0x01;
                    envelope = envelope with { Ciphertext = Convert.ToBase64String(ciphertext) };
                }
                return Ok(JsonSerializer.Serialize(envelope, Wire));
        }
    }

    /// <summary>Both handlers, each with its server stub behind it.</summary>
    private static (HttpClient Client, Server Server, IDisposable Key) Build(bool zke, Func<HttpRequestMessage, Server, HttpResponseMessage> respond)
    {
        Server server = null!;
        server = new Server(req => respond(req, server));
        if (zke)
        {
            var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            return (new HttpClient(new ZkeDekHandler(key) { InnerHandler = server }), server, key);
        }
        return (new HttpClient(new E2EEncryptionHandler { InnerHandler = server }), server, new MemoryStream());
    }

    public static TheoryData<bool, string> EnvelopeRequiredReads => new()
    {
        { false, Base + "/secrets" },
        { true, Base + "/secrets" },
        { false, Base + "/secrets/export" },
        { true, Base + "/providers/vault/secrets" },
        { false, Base + "/providers/vault/secrets/DATABASE_URL" },
        { true, Base + "/providers/vault/secrets/DATABASE_URL/versions/3" },
        { false, Base + "/providers/vault/secrets/export" },
        { true, "https://api.example.test/api/v1/projects/p/secrets" },
    };

    [Theory]
    [MemberData(nameof(EnvelopeRequiredReads))]
    public async Task A_genuine_envelope_to_the_presented_key_decrypts(bool zke, string url)
    {
        var (client, _, key) = Build(zke, (_, s) => Serve(Answer.Envelope, s.PresentedKey));
        using (client) using (key)
        {
            var response = await client.GetAsync(url);
            Assert.Contains(Sentinel, await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [MemberData(nameof(EnvelopeRequiredReads))]
    public async Task A_plaintext_answer_to_a_presented_key_is_refused(bool zke, string url)
    {
        var (client, _, key) = Build(zke, (_, s) => Serve(Answer.Plaintext, s.PresentedKey));
        using (client) using (key)
        {
            var ex = await Assert.ThrowsAsync<E2EEResponseException>(() => client.GetAsync(url));
            Assert.Equal(E2EEResponseException.PlaintextResponse, ex.Code);
            Assert.Contains("E2EE response expected but plaintext received", ex.Message);
            Assert.Contains("(e2ee-plaintext-response)", ex.Message);
            Assert.DoesNotContain(Sentinel, ex.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dotenv_file_on_an_export_the_key_was_presented_on_is_refused(bool zke)
    {
        // The server answers a file only when NO key is presented; with one, a file is a plain answer.
        var (client, _, key) = Build(zke, (_, s) => Serve(Answer.Dotenv, s.PresentedKey));
        using (client) using (key)
        {
            var ex = await Assert.ThrowsAsync<E2EEResponseException>(() => client.GetAsync(Base + "/secrets/export"));
            Assert.Equal(E2EEResponseException.PlaintextResponse, ex.Code);
        }
    }

    [Theory]
    [InlineData(false, Answer.Tampered)]
    [InlineData(true, Answer.Tampered)]
    [InlineData(false, Answer.WrongKey)]
    [InlineData(true, Answer.WrongKey)]
    public async Task A_tampered_or_wrong_key_envelope_is_refused(bool zke, Answer answer)
    {
        var (client, _, key) = Build(zke, (_, s) => Serve(answer, s.PresentedKey));
        using (client) using (key)
        {
            var ex = await Assert.ThrowsAsync<E2EEResponseException>(() => client.GetAsync(Base + "/secrets"));
            Assert.Equal(E2EEResponseException.DecryptionFailed, ex.Code);
            Assert.Contains("(e2ee-decryption-failed)", ex.Message);
            Assert.NotNull(ex.InnerException);
        }
    }

    [Theory]
    [InlineData(false, "GET", "/secrets/version")]
    [InlineData(true, "GET", "/secrets/manifest")]
    [InlineData(false, "GET", "/providers/vault/secrets/hash")]
    [InlineData(true, "GET", "/providers/vault/secrets/DATABASE_URL/metadata")]
    [InlineData(false, "GET", "/providers/vault/secrets/DATABASE_URL/versions")]
    [InlineData(true, "POST", "/providers/vault/secrets")]
    [InlineData(false, "DELETE", "/providers/vault/secrets/DATABASE_URL")]
    public async Task A_plain_answer_where_the_server_never_encrypts_still_passes(bool zke, string method, string path)
    {
        const string body = """{"version":7,"key":"DATABASE_URL"}""";
        var (client, server, key) = Build(zke, (_, _) => Ok(body));
        using (client) using (key)
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Base + path));
            Assert.NotNull(server.PresentedKey); // the key WAS presented; the rule still does not apply here
            Assert.Contains("DATABASE_URL", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_non_2xx_answer_stays_the_API_error_it_was(bool zke)
    {
        var (client, _, key) = Build(zke, (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"type":"zke-device-not-registered"}""", Encoding.UTF8, "application/problem+json"),
        });
        using (client) using (key)
        {
            var response = await client.GetAsync(Base + "/secrets");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("GET", "/api/v1/projects/p/secrets", true)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/secrets", true)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/secrets/", true)]
    [InlineData("GET", "/gateway/api/v1/projects/p/environments/e/secrets/export", true)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets", true)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/export", true)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/KEY", true)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/KEY/versions/12", true)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/hash", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/KEY/versions", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/KEY/versions/latest", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/KEY/metadata", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/KEY/rotation-policy", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/providers/v/secrets/import/preview", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/secrets/version", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/secrets/manifest", false)]
    [InlineData("GET", "/api/v1/projects/p/environments/e/secrets/certificates", false)]
    [InlineData("POST", "/api/v1/projects/p/environments/e/secrets", false)]
    [InlineData("PUT", "/api/v1/projects/p/environments/e/providers/v/secrets/KEY", false)]
    [InlineData("GET", "/api/v1/tenants/me/zke", false)]
    public void RequiresEnvelope_is_exactly_the_reads_the_server_encrypts(string method, string path, bool expected) =>
        Assert.Equal(expected, ZkePresentedKey.RequiresEnvelope(
            new HttpRequestMessage(new HttpMethod(method), "https://api.example.test" + path)));
}
