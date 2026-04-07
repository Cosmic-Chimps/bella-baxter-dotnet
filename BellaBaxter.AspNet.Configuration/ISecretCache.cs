namespace BellaBaxter.AspNet.Configuration;

/// <summary>
/// Persistent cache for Bella secrets. Implement this interface to store secrets on-device
/// so the app can start and run without a network connection to Baxter.
///
/// The built-in implementation for .NET MAUI is <c>BellaBaxter.Maui.MauiSecureSecretCache</c>,
/// which uses <c>Microsoft.Maui.Storage.SecureStorage</c> (Keychain on iOS/macOS,
/// EncryptedSharedPreferences on Android, DPAPI on Windows).
///
/// <example>
/// <code>
/// builder.Configuration.AddBellaSecrets(o =>
/// {
///     o.ApiKey = "bax-...";
///     o.Cache = new MauiSecureSecretCache(); // enables offline startup
/// });
/// </code>
/// </example>
/// </summary>
public interface ISecretCache
{
    /// <summary>
    /// Reads all cached secrets. Returns null if the cache is empty or unavailable.
    /// </summary>
    Task<Dictionary<string, string>?> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes all secrets to the cache, replacing any previously cached values.
    /// </summary>
    Task WriteAsync(Dictionary<string, string> secrets, CancellationToken ct = default);

    /// <summary>
    /// Clears all cached secrets.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
}
