using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BellaBaxter.Crypto;
using Xunit;

namespace BellaBaxter.Client.Tests;

/// <summary>
/// Issue #1162 — the key is presented on EVERY envelope-required read (apps/sdk/SDK_CONTRACT.md, "Rule: the key is
/// presented on every envelope-required read"), each in the shape the API encrypts for that read, and what the
/// handler hands on is that plaintext — except the item-shaped reads, which .NET rewrites to
/// <c>{"secrets":{…}}</c> because the CLI reads them that way (<see cref="DecryptedSecretsBody"/>).
/// </summary>
/// <remarks>
/// Before #1162 the two exports and <c>listGlobalSecrets</c> went through that item rewrite too and came out as
/// <c>{"secrets":{},"version":0}</c> — decrypted correctly, and every value dropped. The <c>Unchanged</c> rows below
/// were red against that code.
/// </remarks>
public class EveryEnvelopeRequiredReadTests
{
    private const string Api = "https://api.example.test/api/v1/projects/p";
    private const string Env = Api + "/environments/e";
    private const string Sentinel = "postgres://sentinel/app";
    private const string Item = $$"""{"key":"DATABASE_URL","value":"{{Sentinel}}","description":null,"createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","type":null}""";
    private const string Dict = $$"""{"DATABASE_URL":"{{Sentinel}}"}""";
    private const string ItemAsMap = $$"""{"secrets":{"DATABASE_URL":"{{Sentinel}}"},"version":0}""";
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Server(string plaintext) : HttpMessageHandler
    {
        public string? PresentedKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            PresentedKey = request.Headers.TryGetValues("X-E2E-Public-Key", out var v) ? v.Single() : null;
            // Without a key the API answers the plain values over TLS alone; with one, the envelope.
            var body = PresentedKey is null
                ? plaintext
                : JsonSerializer.Serialize(EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(plaintext), PresentedKey), Wire);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // (operationId, url, plaintext the server encrypts, what the handler must hand on)
    public static TheoryData<bool, string, string, string, string> Reads()
    {
        var reads = new (string Op, string Url, string Plain, string HandedOn)[]
        {
            ("getAllEnvironmentSecrets", Env + "/secrets",
                $$"""{"environmentSlug":"e","environmentName":"E","secrets":{{Dict}},"version":7,"lastModified":"2026-10-04T00:00:00Z"}""", "Unchanged"),
            ("exportEnvironmentSecrets", Env + "/secrets/export?format=json", Dict, "Unchanged"),
            ("listSecrets", Env + "/providers/vault/secrets", "[" + Item + "]", ItemAsMap),
            ("exportSecrets", Env + "/providers/vault/secrets/export?format=dotenv", Dict, "Unchanged"),
            ("getSecret", Env + "/providers/vault/secrets/DATABASE_URL", Item, ItemAsMap),
            ("getSecretVersion", Env + "/providers/vault/secrets/DATABASE_URL/versions/3", Item, ItemAsMap),
            ("listGlobalSecrets", Api + "/secrets",
                $$"""{"projectRef":"p","projectSlug":"p","globalSecretProviderId":null,"secrets":[{{Item}}]}""", "Unchanged"),
        };
        var data = new TheoryData<bool, string, string, string, string>();
        foreach (var zke in new[] { false, true })
            foreach (var r in reads)
                data.Add(zke, r.Op, r.Url, r.Plain, r.HandedOn);
        return data;
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task The_key_is_presented_and_the_plaintext_is_handed_on(
        bool zke, string operation, string url, string plaintext, string handedOn)
    {
        var server = new Server(plaintext);
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var client = zke
            ? new HttpClient(new ZkeDekHandler(key) { InnerHandler = server })
            : new HttpClient(new E2EEncryptionHandler { InnerHandler = server });

        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(server.PresentedKey is not null, $"{operation}: X-E2E-Public-Key was not presented");
        var expected = handedOn == "Unchanged" ? plaintext : handedOn;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(body)),
            $"{operation}: handed on {body}, expected {expected}");
    }
}
