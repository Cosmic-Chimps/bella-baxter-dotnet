using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using BellaBaxter.Crypto;

namespace BellaBaxter.Client;

/// <summary>
/// DelegatingHandler that enables end-to-end encryption for Bella Baxter secrets responses.
///
/// <para>Behavior:</para>
/// <list type="bullet">
///   <item>Adds <c>X-E2E-Public-Key</c> header to all requests targeting paths containing <c>/secrets</c>.</item>
///   <item>On response, if the payload is encrypted (<c>"encrypted": true</c>), decrypts it
///         using <see cref="EciesAlgorithm.Decrypt"/> and rewrites the content as
///         <c>{"secrets":{"KEY":"VALUE"},"version":0}</c> so Kiota can parse it normally.</item>
/// </list>
///
/// <para>Algorithm: <see cref="EciesAlgorithm.AlgorithmId"/> (shared with API).</para>
/// </summary>
public sealed class E2EEncryptionHandler : DelegatingHandler
{
    private readonly ECDiffieHellman _ecdh;

    /// <summary>Base64-encoded SPKI public key — sent as the <c>X-E2E-Public-Key</c> request header.</summary>
    public string PublicKeyBase64 { get; }

    /// <summary>Creates a new <see cref="E2EEncryptionHandler"/> and generates a P-256 ephemeral keypair.</summary>
    public E2EEncryptionHandler()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKeyBase64 = Convert.ToBase64String(_ecdh.ExportSubjectPublicKeyInfo());
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.Contains("/secrets", StringComparison.OrdinalIgnoreCase) == true)
            request.Headers.TryAddWithoutValidation("X-E2E-Public-Key", PublicKeyBase64);

        var response = await base.SendAsync(request, cancellationToken);

        if (request.RequestUri?.AbsolutePath.Contains("/secrets", StringComparison.OrdinalIgnoreCase) == true
            && response.IsSuccessStatusCode)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("encrypted", out var enc) && enc.GetBoolean())
                {
                    var payload = ParsePayload(doc.RootElement);
                    var plaintext = EciesAlgorithm.Decrypt(payload, _ecdh);

                    // If plaintext is already a full response object (e.g. AllEnvironmentSecretsResponse
                    // with environmentSlug/version/lastModified), pass it through directly.
                    // Otherwise convert legacy list/single-item format to {"secrets":{...},"version":0}.
                    var responseBytes = IsFullResponseObject(plaintext)
                        ? plaintext
                        : BuildSecretsResponse(plaintext);
                    response.Content = new ByteArrayContent(responseBytes);
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
            }
            catch (Exception ex)
            {
                // Surface decryption failures to stderr so they are diagnosable.
                await Console.Error.WriteLineAsync(
                    $"[BellaClient] E2E decryption failed for {request.RequestUri}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return response;
    }

    /// <summary>
    /// Reads the encrypted wire format from the JSON element into a typed payload.
    /// </summary>
    private static E2EEncryptedPayload ParsePayload(JsonElement root) => new(
        Encrypted:       root.GetProperty("encrypted").GetBoolean(),
        Algorithm:       root.GetProperty("algorithm").GetString()!,
        ServerPublicKey: root.GetProperty("serverPublicKey").GetString()!,
        Nonce:           root.GetProperty("nonce").GetString()!,
        Tag:             root.GetProperty("tag").GetString()!,
        Ciphertext:      root.GetProperty("ciphertext").GetString()!
    );

    /// <summary>
    /// Returns <c>true</c> when the plaintext is already a full response object
    /// (e.g. <c>AllEnvironmentSecretsResponse</c>) that Kiota can deserialize directly.
    /// These objects have a "secrets" property that is itself a JSON object (key→value map),
    /// as opposed to provider-specific endpoints whose plaintext is an array of secret items.
    /// </summary>
    private static bool IsFullResponseObject(byte[] plaintext)
    {
        try
        {
            using var doc = JsonDocument.Parse(plaintext);
            // Full response: JSON object with a "secrets" property that is itself an object
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("secrets", out var s)
                && s.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>
    /// Converts legacy encrypted plaintext (array of <c>{key,value,...}</c> or a single
    /// <c>{key,value}</c> object) to <c>{"secrets":{...},"version":0}</c> for Kiota.
    /// </summary>
    private static byte[] BuildSecretsResponse(byte[] plaintext)
    {
        var secrets = ExtractKeyValuePairs(plaintext);
        return JsonSerializer.SerializeToUtf8Bytes(new { secrets, version = 0L });
    }

    /// <summary>
    /// Deserializes the decrypted bytes (a JSON array of <c>{key, value, ...}</c> objects)
    /// into a flat <c>key → value</c> dictionary.
    /// </summary>
    private static Dictionary<string, string> ExtractKeyValuePairs(byte[] plaintext)
    {
        using var doc = JsonDocument.Parse(plaintext);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // List endpoint: plaintext = [{key, value, description, ...}, ...]
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("key", out var k) && item.TryGetProperty("value", out var v))
                {
                    var key = k.GetString();
                    if (key is not null)
                        result[key] = v.GetString() ?? string.Empty;
                }
            }
            return result;
        }

        // Single-secret endpoint: plaintext = {key, value, ...}
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("key", out var sk)
            && doc.RootElement.TryGetProperty("value", out var sv))
        {
            var key = sk.GetString();
            if (key is not null)
                result[key] = sv.GetString() ?? string.Empty;
        }

        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ecdh.Dispose();
        base.Dispose(disposing);
    }
}
