using System.Text.Json;

namespace BellaBaxter.Client;

/// <summary>
/// What <see cref="E2EEncryptionHandler"/> and <see cref="ZkeDekHandler"/> hand on after decrypting an envelope.
/// One author, so the two handlers cannot disagree about it.
/// </summary>
/// <remarks>
/// <para><b>The decrypted body is handed on unchanged</b> (apps/sdk/SDK_CONTRACT.md, "Rule: the key is presented
/// on every envelope-required read", #1162) — with ONE documented .NET exception, kept because the CLI reads it:
/// the item-shaped reads (<c>listSecrets</c>: an array of <c>{key, value, …}</c>; <c>getSecret</c> /
/// <c>getSecretVersion</c>: one such item) are rewritten to <c>{"secrets":{"KEY":"VALUE"},"version":0}</c>. The
/// OpenAPI document declares those reads as <c>E2EEncryptedPayload</c>, so the generated client cannot type them;
/// callers (<c>bella secrets get --provider</c>, <c>bella secrets list</c>) read the map from
/// <c>AdditionalData["secrets"]</c>.</para>
///
/// <para><b>Why the exception is narrow now.</b> Before #1162 every plaintext that was not an
/// <c>AllEnvironmentSecretsResponse</c> went through that rewrite, and the rewrite only understands items. The two
/// exports (<c>{"KEY":"VALUE"}</c>) and <c>listGlobalSecrets</c> (<c>{projectRef, …, "secrets": [items]}</c>) have no
/// top-level <c>key</c>/<c>value</c>, so they came out as <c>{"secrets":{},"version":0}</c>: an E2EE caller received
/// NO values from them, and <c>bella secrets list</c> silently showed no global secrets (its typed
/// <c>ListGlobalSecretsResponse</c> read failed inside a catch-all). Everything that is not item-shaped is now
/// passed through byte for byte.</para>
/// </remarks>
internal static class DecryptedSecretsBody
{
    /// <summary>The body to hand on for a decrypted <paramref name="plaintext"/>.</summary>
    internal static byte[] HandOn(byte[] plaintext) =>
        IsItemShaped(plaintext) ? ToSecretsMap(plaintext) : plaintext;

    /// <summary>An array (the item list of <c>listSecrets</c>), or an object carrying top-level <c>key</c> and
    /// <c>value</c> (the single item of <c>getSecret</c> / <c>getSecretVersion</c>).</summary>
    private static bool IsItemShaped(byte[] plaintext)
    {
        try
        {
            using var doc = JsonDocument.Parse(plaintext);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Array
                || (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("key", out _)
                    && root.TryGetProperty("value", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] ToSecretsMap(byte[] plaintext)
    {
        using var doc = JsonDocument.Parse(plaintext);
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("key", out var k)
                && item.TryGetProperty("value", out var v)
                && k.GetString() is { } key)
            {
                secrets[key] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            }
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
                Add(item);
        }
        else
        {
            Add(doc.RootElement);
        }

        return JsonSerializer.SerializeToUtf8Bytes(new { secrets, version = 0L });
    }
}
