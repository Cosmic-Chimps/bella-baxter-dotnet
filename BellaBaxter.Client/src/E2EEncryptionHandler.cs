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
///   <item>Adds <c>X-E2E-Public-Key</c> to every Bella API request (<see cref="ZkePresentedKey.ShouldPresent"/>, #635),
///         so each envelope-required read of apps/sdk/SDK_CONTRACT.md is end-to-end encrypted (#1162).</item>
///   <item>On a <c>/secrets</c> response, if the payload is encrypted (<c>"encrypted": true</c>), decrypts it
///         using <see cref="EciesAlgorithm.Decrypt"/> and hands the plaintext on unchanged — except the item-shaped
///         reads, rewritten to <c>{"secrets":{"KEY":"VALUE"},"version":0}</c> (<see cref="DecryptedSecretsBody"/>).</item>
///   <item>#1050 — throws <see cref="E2EEResponseException"/> when an envelope does not decrypt, and when a read the
///         server always encrypts (<see cref="ZkePresentedKey.RequiresEnvelope"/>) comes back plain.</item>
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
        // #635 — presented on every API call, not only on `/secrets`. Must stay byte-identical to what
        // ZkeDekHandler sends, and on the same set of requests: the server cannot tell the two apart,
        // and a client that presented the key on different paths depending on which handler was wired
        // would fail in only one of its two configurations.
        var presented = ZkePresentedKey.ShouldPresent(request);
        if (presented)
            request.Headers.TryAddWithoutValidation(ZkePresentedKey.HeaderName, PublicKeyBase64);

        var response = await base.SendAsync(request, cancellationToken);

        // #1050 (b) — on a read the server always encrypts to a presented key, a plain answer is REFUSED.
        var envelopeRequired = presented && ZkePresentedKey.RequiresEnvelope(request);

        // Decryption stays on the narrow test: only secrets responses come back encrypted.
        if (ZkePresentedKey.CarriesEncryptedPayload(request)
            && response.IsSuccessStatusCode)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // A body that is not JSON is not an envelope: a plain answer where one was required, otherwise passed through.
            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes); }
            catch (JsonException)
            {
                if (envelopeRequired) throw E2EEResponseException.Plaintext(request);
                return response;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("encrypted", out var enc)
                    && enc.ValueKind == JsonValueKind.True)
                {
                    // An envelope that will not decrypt is REFUSED (#1050): returning it would hand the caller
                    // ciphertext as its secrets. Fail closed, never fall back to the original bytes.
                    byte[] plaintext;
                    try
                    {
                        plaintext = EciesAlgorithm.Decrypt(ParsePayload(doc.RootElement), _ecdh);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw E2EEResponseException.Undecryptable(request, "the transport envelope did not decrypt", ex);
                    }

                    // #1162 — handed on unchanged, except the item-shaped reads (see DecryptedSecretsBody).
                    var responseBytes = DecryptedSecretsBody.HandOn(plaintext);
                    response.Content = new ByteArrayContent(responseBytes);
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                else if (envelopeRequired)
                {
                    throw E2EEResponseException.Plaintext(request);
                }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ecdh.Dispose();
        base.Dispose(disposing);
    }
}
