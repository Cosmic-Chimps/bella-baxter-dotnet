using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BellaBaxter.AspNet.Configuration;

/// <summary>
/// ECDH-P256-HKDF-SHA256-AES256GCM client-side E2EE for the Bella Baxter API.
///
/// Generate once per client; send <see cref="PublicKeyBase64"/> as the
/// <c>X-E2E-Public-Key</c> request header.  Call <see cref="Decrypt"/> on
/// the raw response body when the server returns an encrypted payload.
///
/// Mirrors the algorithm used by the JS, Python, Go, and Java SDKs.
/// </summary>
internal sealed class E2EEncryption
{
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("bella-e2ee-v1");
    private static readonly byte[] HkdfSalt = new byte[32]; // 32 zeros per RFC 5869 §2.2

    private readonly ECDiffieHellman _ecdh;

    /// <summary>Base64-encoded SPKI public key — value for the <c>X-E2E-Public-Key</c> header.</summary>
    public string PublicKeyBase64 { get; }

    public E2EEncryption()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        // Export as SubjectPublicKeyInfo (SPKI) DER — same format other SDKs use
        var spki = _ecdh.ExportSubjectPublicKeyInfo();
        PublicKeyBase64 = Convert.ToBase64String(spki);
    }

    /// <summary>
    /// Decrypts an encrypted secrets payload from the Bella Baxter API.
    /// Returns a <c>Dictionary&lt;string, string&gt;</c> of the plaintext secrets.
    /// </summary>
    /// <param name="responseBody">Raw JSON bytes from the API response.</param>
    public Dictionary<string, string> Decrypt(byte[] responseBody)
    {
        var payload = JsonSerializer.Deserialize<E2EPayload>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("[BellaBaxter] E2EE: failed to parse response payload.");

        if (!payload.Encrypted)
        {
            // Plain response — deserialize as-is
            return JsonSerializer.Deserialize<Dictionary<string, string>>(responseBody, JsonOptions)
                ?? new Dictionary<string, string>();
        }

        var serverPubBytes = Convert.FromBase64String(payload.ServerPublicKey);
        var nonce          = Convert.FromBase64String(payload.Nonce);
        var tag            = Convert.FromBase64String(payload.Tag);
        var ciphertext     = Convert.FromBase64String(payload.Ciphertext);

        // 1. Import server ephemeral public key (SPKI DER)
        using var serverEcdh = ECDiffieHellman.Create();
        serverEcdh.ImportSubjectPublicKeyInfo(serverPubBytes, out _);

        // 2. ECDH → raw shared secret
        var sharedSecret = _ecdh.DeriveRawSecretAgreement(serverEcdh.PublicKey);

        // 3. HKDF-SHA256 → 32-byte AES key
        var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, HkdfSalt, HkdfInfo);

        // 4. AES-256-GCM decrypt
        using var aesGcm = new AesGcm(aesKey, 16); // tagSizeInBytes = 16
        var plaintext = new byte[ciphertext.Length];
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, JsonOptions)
            ?? new Dictionary<string, string>();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class E2EPayload
    {
        [JsonPropertyName("encrypted")] public bool Encrypted { get; set; }
        [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = string.Empty;
        [JsonPropertyName("serverPublicKey")] public string ServerPublicKey { get; set; } = string.Empty;
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
        [JsonPropertyName("tag")] public string Tag { get; set; } = string.Empty;
        [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = string.Empty;
    }
}
