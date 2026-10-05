using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BellaBaxter.Crypto;

namespace BellaBaxter.Client;

/// <summary>
/// DelegatingHandler that combines E2EE transport decryption with at-rest ZKE
/// (Zero-Knowledge Encryption) support.
///
/// <para>On every request to a <c>/secrets</c> endpoint:</para>
/// <list type="number">
///   <item>Sends <c>X-E2E-Public-Key</c> using the provided persistent device/M2M key
///         so the server knows which key to use for BOTH transport encryption and DEK wrapping.</item>
///   <item>On response, decrypts the ECIES-encrypted body (same algorithm as
///         <see cref="E2EEncryptionHandler"/>).</item>
///   <item>Captures the <c>X-Bella-Wrapped-Dek</c> and <c>X-Bella-Lease-Expires</c> headers
///         and fires <see cref="OnWrappedDekReceived"/> so callers can cache the wrapped DEK
///         for future offline decryption.</item>
///   <item>Decrypts any <c>bellabaxter:v1:</c> prefixed values in the response body using
///         the unwrapped DEK. A no-op while the server also decrypts server-side; kept because it is
///         what makes a true zero-knowledge read possible without a client change.</item>
/// </list>
///
/// <para>Use this handler instead of <see cref="E2EEncryptionHandler"/> when:</para>
/// <list type="bullet">
///   <item>The developer has run <c>bella auth setup</c> (persistent device key).</item>
///   <item>M2M callers using <c>--private-key</c> with <c>bella run</c>.</item>
///   <item>Any SDK consumer that wants to participate in zero-knowledge reads.</item>
/// </list>
///
/// <para>For non-ZKE callers, continue using <see cref="E2EEncryptionHandler"/> (ephemeral key).</para>
///
/// <para><b>spec 037 — the wrapped-DEK headers may be absent, and this handler must tolerate that.</b>
/// The two handlers send byte-identical <c>X-E2E-Public-Key</c> values, so the server cannot tell them
/// apart by shape and does not try: it looks the presented key UP. Only a key registered as a device
/// (<c>bella auth setup</c>) or recorded against an API key at creation receives the environment key.
/// An unregistered key still gets the values; <see cref="OnWrappedDekReceived"/> simply never fires.</para>
/// </summary>
public sealed class ZkeDekHandler : DelegatingHandler
{
    private readonly ECDiffieHellman _ecdh;

    /// <summary>
    /// Fired whenever the server includes an <c>X-Bella-Wrapped-Dek</c> response header.
    /// Arguments: (projectSlug, envSlug, wrappedDekBase64, leaseExpires).
    ///
    /// The wrapped DEK is already ECIES-encrypted with this handler's public key — safe to
    /// persist on disk. On the next call the host can pass the cached wrapped DEK back via
    /// <see cref="DecryptWrappedDek"/> to skip the server round-trip for DEK unwrapping.
    /// </summary>
    public Action<string, string, string, DateTimeOffset?>? OnWrappedDekReceived { get; set; }

    /// <summary>Base64-encoded SPKI public key — sent as the <c>X-E2E-Public-Key</c> request header.</summary>
    public string PublicKeyBase64 { get; }

    /// <param name="key">
    ///   Persistent P-256 key for this device or M2M identity.
    ///   The handler does NOT dispose this key — the caller is responsible for its lifetime.
    /// </param>
    /// <param name="onWrappedDekReceived">
    ///   Optional callback fired when the server returns <c>X-Bella-Wrapped-Dek</c>.
    /// </param>
    public ZkeDekHandler(
        ECDiffieHellman key,
        Action<string, string, string, DateTimeOffset?>? onWrappedDekReceived = null)
    {
        _ecdh = key;
        OnWrappedDekReceived = onWrappedDekReceived;
        PublicKeyBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Convenience: decrypts a <c>X-Bella-Wrapped-Dek</c> header value using this handler's
    /// private key. Returns null if decryption fails.
    /// </summary>
    public byte[]? DecryptWrappedDek(string wrappedDekBase64)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(wrappedDekBase64));
            var payload = JsonSerializer.Deserialize<E2EEncryptedPayload>(json);
            if (payload is null) return null;
            return EciesAlgorithm.Decrypt(payload, _ecdh);
        }
        catch
        {
            return null;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // #635 — the key is presented on every API call, not only on `/secrets`. The status call the
        // CLI makes before a read (`GET /api/v1/tenants/me/zke`) went out bare, so the server answered
        // `presentedKeyRegistered: false` and the CLI refused itself before reaching any secret.
        var presented = ZkePresentedKey.ShouldPresent(request);
        if (presented)
            request.Headers.TryAddWithoutValidation(ZkePresentedKey.HeaderName, PublicKeyBase64);

        // Decryption stays on the narrow test: only secrets responses come back encrypted.
        var isSecrets = ZkePresentedKey.CarriesEncryptedPayload(request);
        // #1050 (b) — on a read the server always encrypts to a presented key, a plain answer is REFUSED.
        var envelopeRequired = presented && ZkePresentedKey.RequiresEnvelope(request);

        var response = await base.SendAsync(request, cancellationToken);

        if (!isSecrets || !response.IsSuccessStatusCode)
            return response;

        // ── Capture ZKE headers before consuming body ─────────────────────────
        string? wrappedDekHeader = null;
        DateTimeOffset? leaseExpires = null;

        if (response.Headers.TryGetValues("X-Bella-Wrapped-Dek", out var dekVals))
            wrappedDekHeader = dekVals.FirstOrDefault();

        if (response.Headers.TryGetValues("X-Bella-Lease-Expires", out var expVals))
        {
            var raw = expVals.FirstOrDefault();
            if (raw is not null && DateTimeOffset.TryParse(raw, out var parsed))
                leaseExpires = parsed;
        }

        // ── ECIES transport decryption ────────────────────────────────────────
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        // #1050 — an envelope that will not decrypt throws (E2EEResponseException); a body that is not an envelope
        // comes back null, which is refused where the read requires one and passed through elsewhere. Never the
        // original bytes in place of a failed decryption.
        var decrypted = DecryptEciesBody(request, bytes);
        if (decrypted is null && envelopeRequired)
            throw E2EEResponseException.Plaintext(request);
        var processedBytes = decrypted ?? bytes;

        // ── ZKE at-rest decryption (bellabaxter:v1: prefix) ──────────────────
        if (wrappedDekHeader is not null)
        {
            // Notify caller to cache the wrapped DEK
            if (OnWrappedDekReceived is not null)
            {
                var (project, env) = ExtractSlugs(request.RequestUri!);
                OnWrappedDekReceived(project, env, wrappedDekHeader, leaseExpires);
            }

            // Decrypt any remaining bellabaxter:v1: values in response body.
            // This is a no-op today (server decrypts server-side) but makes the handler
            // correct for future true-ZKE mode where the server skips server-side decrypt.
            // #1050 — a value that stays encrypted is REFUSED, not returned: no plaintext secret can start with
            // the prefix (every writer refuses values beginning `bellabaxter:`), so one that still does is ciphertext.
            processedBytes = DecryptZkeValues(request, processedBytes, wrappedDekHeader);
        }

        response.Content = new ByteArrayContent(processedBytes);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    // ── ECIES helpers (mirrors E2EEncryptionHandler) ──────────────────────────

    private byte[]? DecryptEciesBody(HttpRequestMessage request, byte[] bytes)
    {
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(bytes); }
        catch (JsonException) { return null; } // not JSON (a dotenv export) — not an envelope
        using var doc = parsed;
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("encrypted", out var enc)
            || enc.ValueKind != JsonValueKind.True)
            return null; // Not ECIES-encrypted — return null to use original bytes

        byte[] plaintext;
        try
        {
            plaintext = EciesAlgorithm.Decrypt(ParseEciesPayload(doc.RootElement), _ecdh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw E2EEResponseException.Undecryptable(request, "the transport envelope did not decrypt", ex);
        }

        // #1162 — handed on unchanged, except the item-shaped reads (see DecryptedSecretsBody).
        return DecryptedSecretsBody.HandOn(plaintext);
    }

    private static E2EEncryptedPayload ParseEciesPayload(JsonElement root) => new(
        Encrypted:       root.GetProperty("encrypted").GetBoolean(),
        Algorithm:       root.GetProperty("algorithm").GetString()!,
        ServerPublicKey: root.GetProperty("serverPublicKey").GetString()!,
        Nonce:           root.GetProperty("nonce").GetString()!,
        Tag:             root.GetProperty("tag").GetString()!,
        Ciphertext:      root.GetProperty("ciphertext").GetString()!
    );

    // ── ZKE at-rest helpers ───────────────────────────────────────────────────

    private byte[] DecryptZkeValues(HttpRequestMessage request, byte[] responseBytes, string wrappedDekBase64)
    {
        // Unwrap the DEK using our private key
        var dek = DecryptWrappedDek(wrappedDekBase64);
        if (dek is null)
        {
            // Nothing to decrypt is fine (the server decrypted server-side). Ciphertext we cannot open is not.
            if (HasZkeEncryptedValues(responseBytes))
                throw E2EEResponseException.Undecryptable(request, "the wrapped data key could not be unwrapped with this key");
            return responseBytes;
        }

        try
        {
            return DecryptZkeInJson(responseBytes, dek);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not E2EEResponseException)
        {
            throw E2EEResponseException.Undecryptable(request, "an at-rest value did not decrypt", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Whether the body is a secrets object carrying at least one <c>bellabaxter:v1:</c> value.</summary>
    private static bool HasZkeEncryptedValues(byte[] jsonBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("secrets", out var secrets)
                && secrets.ValueKind == JsonValueKind.Object
                && secrets.EnumerateObject().Any(p =>
                    p.Value.ValueKind == JsonValueKind.String && DekAlgorithm.IsEncrypted(p.Value.GetString()!));
        }
        catch (JsonException)
        {
            return false; // not JSON — no values of any kind
        }
    }

    private static byte[] DecryptZkeInJson(byte[] jsonBytes, byte[] dek)
    {
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(jsonBytes); }
        catch (JsonException) { return jsonBytes; } // not JSON — nothing at rest to decrypt
        using var doc = parsed;

        // Only handle the standard AllEnvironmentSecretsResponse shape: {"secrets":{...}, ...}
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("secrets", out var secretsEl)
            || secretsEl.ValueKind != JsonValueKind.Object)
            return jsonBytes;

        // Check if any value is ZKE-encrypted
        var hasEncrypted = false;
        foreach (var prop in secretsEl.EnumerateObject())
        {
            if (prop.Value.GetString()?.StartsWith(DekAlgorithm.Prefix, StringComparison.Ordinal) == true)
            {
                hasEncrypted = true;
                break;
            }
        }

        if (!hasEncrypted) return jsonBytes;

        // Rebuild the secrets dict with decrypted values
        var decryptedSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in secretsEl.EnumerateObject())
        {
            var val = prop.Value.GetString() ?? string.Empty;
            decryptedSecrets[prop.Name] = DekAlgorithm.IsEncrypted(val)
                ? DekAlgorithm.DecryptToString(val, dek)
                : val;
        }

        // Rebuild the full response object preserving other fields (version, environmentSlug, etc.)
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "secrets")
            {
                writer.WritePropertyName("secrets");
                writer.WriteStartObject();
                foreach (var (k, v) in decryptedSecrets)
                {
                    writer.WriteString(k, v);
                }
                writer.WriteEndObject();
            }
            else
            {
                prop.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    // ── URL slug extraction ───────────────────────────────────────────────────

    private static (string project, string env) ExtractSlugs(Uri uri)
    {
        // /api/v1/projects/{projectSlug}/environments/{envSlug}/secrets[/...]
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // [0]=api [1]=v1 [2]=projects [3]={project} [4]=environments [5]={env} [6]=secrets
        var project = segments.Length > 3 ? segments[3] : "unknown";
        var env = segments.Length > 5 ? segments[5] : "unknown";
        return (project, env);
    }
}
