namespace BellaBaxter.AspNet.Configuration;

/// <summary>
/// Configuration options for the Bella secrets provider.
/// </summary>
public class BellaOptions
{
    /// <summary>Configuration section name used when binding from appsettings.json.</summary>
    public const string SectionName = "BellaBaxter";

    /// <summary>
    /// Default BaxterUrl used when none is configured.
    /// The extensions layer replaces this with BELLA_BAXTER_URL if present.
    /// </summary>
    internal const string DefaultBaxterUrl = "https://api.bella-baxter.io";

    /// <summary>Base URL of the Baxter API (e.g. https://baxter.example.com).</summary>
    public string BaxterUrl { get; set; } = DefaultBaxterUrl;

    /// <summary>
    /// Project slug (e.g. "my-app", "backend").
    /// Used together with EnvironmentSlug to identify the target environment.
    /// </summary>
    public string ProjectSlug { get; set; } = string.Empty;

    /// <summary>
    /// Environment slug (e.g. "production", "staging").
    /// Used to identify which environment's secrets to load.
    /// </summary>
    public string EnvironmentSlug { get; set; } = string.Empty;

    /// <summary>
    /// Bella Baxter API key (bax-...). Obtain via WebApp or: bella apikeys create.
    /// Treat this like a password — store in user-secrets or a secure environment
    /// variable, never in appsettings.json.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// How often to poll Baxter for secret changes (default: 60 seconds).
    ///
    /// Cost note: Baxter serves secrets from its Redis HybridCache, so polling
    /// does NOT hit AWS/Azure/GCP on every request — only when Baxter's cache
    /// is cold or a secret was updated. This keeps cloud provider costs near zero.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// If true, use cached secrets on HTTP errors instead of throwing (default: true).
    /// Prevents app crash when Baxter is temporarily unavailable.
    /// </summary>
    public bool FallbackOnError { get; set; } = true;

    /// <summary>
    /// Timeout for individual HTTP requests to Baxter (default: 10 seconds).
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Optional prefix to strip from secret keys when loading into IConfiguration.
    /// E.g. if prefix is "myapp_", then "myapp_DATABASE_URL" becomes "DATABASE_URL".
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Optional name of your application, sent as X-App-Client header for audit logging.
    /// Falls back to BELLA_BAXTER_APP_CLIENT environment variable if not set.
    /// Example values: "my-web-api", "github-ci-deploy", "data-pipeline"
    /// </summary>
    public string? AppClient { get; set; }

	/// <summary>
    /// Optional persistent cache for secrets. When set, secrets are written to the cache
    /// after every successful fetch and read from it when the Baxter API is unavailable.
    /// This enables offline startup — the app can launch and run without network access
    /// after the first successful connection.
    ///
    /// For .NET MAUI apps, use <c>BellaBaxter.Maui.MauiSecureSecretCache</c>:
    /// <code>
    /// o.Cache = new MauiSecureSecretCache(); // iOS Keychain / Android EncryptedSharedPreferences / Windows DPAPI
    /// </code>
    /// </summary>
    public ISecretCache? Cache { get; set; }

    /// <summary>
    /// Optional PKCS#8 PEM private key for ZKE (Zero-Knowledge Encryption) transport.
    ///
    /// When set, the SDK sends a persistent device public key as <c>X-E2E-Public-Key</c>
    /// instead of an ephemeral per-poll keypair. This enables:
    /// <list type="bullet">
    ///   <item>Persistent key identity (the server can correlate requests to this M2M identity)</item>
    ///   <item>DEK lease caching — the server wraps the encryption key for this device so future
    ///         requests can reuse the cached key without a round-trip</item>
    ///   <item>Future: true zero-knowledge reads where the server returns ciphertext and the SDK
    ///         decrypts locally</item>
    /// </list>
    ///
    /// Obtain via: <c>bella auth setup</c> (exports <c>~/.bella/device-key.pem</c>).
    /// Supply via the <c>BELLA_BAXTER_PRIVATE_KEY</c> environment variable or appsettings.
    /// Never commit a private key to source control.
    ///
    /// When null (default), the SDK generates a fresh ephemeral P-256 keypair on each poll —
    /// this provides E2EE transport security but no persistent identity or DEK caching.
    /// </summary>
    public string? PrivateKey { get; set; }
}
