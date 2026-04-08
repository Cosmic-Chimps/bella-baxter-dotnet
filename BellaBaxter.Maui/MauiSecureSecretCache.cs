using BellaBaxter.AspNet.Configuration;
using Microsoft.Maui.Storage;

namespace BellaBaxter.Maui;

/// <summary>
/// Bella Baxter secret cache backed by <see cref="SecureStorage"/> (.NET MAUI).
///
/// Platform storage:
/// <list type="bullet">
///   <item>iOS / macOS → Apple Keychain</item>
///   <item>Android → EncryptedSharedPreferences (AES-256 GCM)</item>
///   <item>Windows → Windows Credential Store (DPAPI)</item>
/// </list>
///
/// Secrets are stored as individual key/value pairs under a namespace prefix so they
/// can coexist with other SecureStorage entries in the same app:
/// <c>bella.{storageKey}.{secretKey}</c> and an index entry
/// <c>bella.{storageKey}.__keys__</c> listing all stored secret keys.
///
/// <example>
/// Typical setup in MauiProgram.cs:
/// <code>
/// builder.Configuration.AddBellaSecrets(o =>
/// {
///     o.ApiKey = "bax-...";
///     o.Cache = new MauiSecureSecretCache(); // enables offline startup
/// });
/// builder.Services.AddBellaTypedSecrets&lt;BellaAppSecrets&gt;();
/// </code>
/// </example>
///
/// <example>
/// Per-environment cache (different key namespace for staging vs production):
/// <code>
/// o.Cache = new MauiSecureSecretCache("production");
/// </code>
/// </example>
/// </summary>
public sealed class MauiSecureSecretCache : ISecretCache
{
    private const string KeysSuffix = "__keys__";
    private const string KeySeparator = "\n";

    private readonly string _namespace;

    /// <param name="storageKey">
    /// Namespace prefix for this cache instance (default: <c>"default"</c>).
    /// Use a different value per environment to avoid collisions (e.g. <c>"production"</c>).
    /// </param>
    public MauiSecureSecretCache(string storageKey = "default")
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            throw new ArgumentException("storageKey must not be empty", nameof(storageKey));
        _namespace = $"bella.{storageKey}";
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var indexKey = $"{_namespace}.{KeysSuffix}";
            var indexRaw = await SecureStorage.Default.GetAsync(indexKey);
            if (string.IsNullOrEmpty(indexRaw))
                return null;

            var keys = indexRaw.Split(KeySeparator, StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length == 0)
                return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var value = await SecureStorage.Default.GetAsync($"{_namespace}.{key}");
                if (value is not null)
                    result[key] = value;
            }

            return result.Count > 0 ? result : null;
        }
        catch
        {
            // Android: EncryptedSharedPreferences may be corrupt after a device restore
            // (backup/restore transfers the encrypted data but not the encryption keys).
            // Clear all secure storage so secrets can be re-fetched on next API success.
            SecureStorage.Default.RemoveAll();
            return null;
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(Dictionary<string, string> secrets, CancellationToken ct = default)
    {
        if (secrets is null)
            throw new ArgumentNullException(nameof(secrets));

        // Remove stale keys from a previous write that are no longer present.
        await RemoveStaleKeysAsync(secrets.Keys);

        // Write each secret individually and build the new index.
        var keyList = new List<string>(secrets.Count);
        foreach (var (key, value) in secrets)
        {
            await SecureStorage.Default.SetAsync($"{_namespace}.{key}", value);
            keyList.Add(key);
        }

        // Store the index so ReadAsync can enumerate all keys.
        await SecureStorage.Default.SetAsync(
            $"{_namespace}.{KeysSuffix}",
            string.Join(KeySeparator, keyList)
        );
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default)
    {
        // SecureStorage.RemoveAll() removes ALL entries for the app, not just ours.
        // Remove only the keys we own by reading the index first.
        return RemoveOwnedKeysAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task RemoveOwnedKeysAsync()
    {
        var indexKey = $"{_namespace}.{KeysSuffix}";
        try
        {
            var indexRaw = await SecureStorage.Default.GetAsync(indexKey);
            if (!string.IsNullOrEmpty(indexRaw))
            {
                foreach (
                    var key in indexRaw.Split(KeySeparator, StringSplitOptions.RemoveEmptyEntries)
                )
                    SecureStorage.Default.Remove($"{_namespace}.{key}");
            }
        }
        catch
        { /* ignore — might already be gone */
        }

        SecureStorage.Default.Remove(indexKey);
    }

    private async Task RemoveStaleKeysAsync(IEnumerable<string> currentKeys)
    {
        var indexKey = $"{_namespace}.{KeysSuffix}";
        try
        {
            var indexRaw = await SecureStorage.Default.GetAsync(indexKey);
            if (string.IsNullOrEmpty(indexRaw))
                return;

            var currentSet = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);
            foreach (
                var oldKey in indexRaw.Split(KeySeparator, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                if (!currentSet.Contains(oldKey))
                    SecureStorage.Default.Remove($"{_namespace}.{oldKey}");
            }
        }
        catch
        { /* ignore */
        }
    }
}
