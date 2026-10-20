using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Client.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace BellaBaxter.Client.Tests;

/// <summary>
/// Issue #1187 — the value reads declare the PLAINTEXT they return in the OpenAPI document, so the generated client's
/// typed <c>GetAsync()</c> parses what the SDK hands it, WITH a key (decrypted by <see cref="E2EEncryptionHandler"/> /
/// <see cref="ZkeDekHandler"/>) and WITHOUT one (plain over TLS).
/// </summary>
/// <remarks>
/// <para>Before #1187 five of these reads were declared as the E2EE envelope, a shape no client ever receives: the typed
/// call returned an empty model, or failed outright on <c>listSecrets</c> (an array). .NET hid that by rewriting the
/// decrypted item reads to <c>{"secrets":{...}}</c> and the CLI read <c>AdditionalData["secrets"]</c>; that rewrite is
/// gone, so the typed model must read the decrypted bytes directly. The bodies below are the real shapes the API
/// serializes (<c>SecretItem</c>, a string map), and each read goes through the REAL generated request builder.</para>
/// </remarks>
public class TypedReadsParseTheDeclaredPlaintextTests
{
    private const string Origin = "https://api.example.test";
    private const string Sentinel = "postgres://sentinel/app";
    private const string Item = $$"""{"key":"DATABASE_URL","value":"{{Sentinel}}","description":"d","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z","type":"string","tags":{"scope":"ci"},"ignoreInScan":false}""";
    private const string Dict = $$"""{"DATABASE_URL":"{{Sentinel}}"}""";
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Server(string plaintext) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.TryGetValues("X-E2E-Public-Key", out var v) ? v.Single() : null;
            // Without a key the API answers the plain values over TLS alone; with one, the envelope.
            var body = key is null
                ? plaintext
                : JsonSerializer.Serialize(
                    BellaBaxter.Crypto.EciesAlgorithm.Encrypt(Encoding.UTF8.GetBytes(plaintext), key), Wire);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A real generated client whose transport is the SDK's own handler over a stub API.</summary>
    private static BellaClient ClientFor(string mode, string plaintext)
    {
        var server = new Server(plaintext);
        HttpMessageHandler chain = mode switch
        {
            "plain" => server,
            "e2e" => new E2EEncryptionHandler { InnerHandler = server },
            "zke" => new ZkeDekHandler(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)) { InnerHandler = server },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(chain))
        {
            BaseUrl = Origin,
        };
        return new BellaClient(adapter);
    }

    public static TheoryData<string> Modes() => ["plain", "e2e", "zke"];

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task listSecrets_parses_as_a_list_of_SecretItem(string mode)
    {
        var client = ClientFor(mode, "[" + Item + "]");

        var items = await client.Api.V1.Projects["p"].Environments["e"].Providers["v"].Secrets.GetAsync();

        var only = Assert.Single(items!);
        Assert.Equal("DATABASE_URL", only.Key);
        Assert.Equal(Sentinel, only.Value);
        Assert.Equal("ci", only.Tags!.AdditionalData["scope"]);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task getSecret_parses_as_a_SecretItem(string mode)
    {
        var client = ClientFor(mode, Item);

        var item = await client.Api.V1.Projects["p"].Environments["e"].Providers["v"].Secrets["DATABASE_URL"].GetAsync();

        Assert.Equal("DATABASE_URL", item!.Key);
        Assert.Equal(Sentinel, item.Value);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task getSecretVersion_parses_as_a_SecretItem(string mode)
    {
        var client = ClientFor(mode, Item);

        var item = await client.Api.V1.Projects["p"].Environments["e"].Providers["v"].Secrets["DATABASE_URL"].Versions[3].GetAsync();

        Assert.Equal("DATABASE_URL", item!.Key);
        Assert.Equal(Sentinel, item.Value);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_two_exports_parse_as_a_string_map(string mode)
    {
        var provider = await ClientFor(mode, Dict).Api.V1.Projects["p"].Environments["e"].Providers["v"].Secrets.Export
            .GetAsync(c => c.QueryParameters.Format = "json");
        var environment = await ClientFor(mode, Dict).Api.V1.Projects["p"].Environments["e"].Secrets.Export
            .GetAsync(c => c.QueryParameters.Format = "json");

        // The map's values are the entries the model keeps as additional data.
        foreach (var data in new[] { provider!.AdditionalData, environment!.AdditionalData })
        {
            var value = Assert.Contains("DATABASE_URL", data);
            Assert.Equal(Sentinel, value switch
            {
                string text => text,
                Microsoft.Kiota.Abstractions.Serialization.UntypedString untyped => untyped.GetValue(),
                _ => null,
            });
        }
    }
}
