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
}
