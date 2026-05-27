using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BellaBaxter.AspNet.Configuration;

/// <summary>
/// Extension methods for adding Bella secrets to IConfigurationBuilder.
/// </summary>
public static class BellaConfigurationExtensions
{
    /// <summary>
    /// Adds Bella secrets as an IConfiguration source.
    /// Secrets are loaded at startup and automatically hot-reloaded when they change.
    ///
    /// <example>
    /// Minimal setup — reads "BellaBaxter" section from existing config:
    /// <code>
    /// // appsettings.json:
    /// // { "BellaBaxter": { "BaxterUrl": "https://baxter.example.com", "EnvironmentSlug": "production", "Token": "bella_ak_..." } }
    ///
    /// builder.Configuration.AddBellaSecrets();
    /// </code>
    /// </example>
    ///
    /// <example>
    /// Explicit setup:
    /// <code>
    /// builder.Configuration.AddBellaSecrets(o =>
    /// {
    ///     o.BaxterUrl = "https://baxter.example.com";
    ///     o.EnvironmentSlug = "production";
    ///     o.Token = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")!;
    ///     o.PollingInterval = TimeSpan.FromSeconds(30);
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IConfigurationBuilder AddBellaSecrets(
        this IConfigurationBuilder builder,
        Action<BellaOptions>? configure = null,
        ILogger? logger = null
    )
    {
        var options = new BellaOptions();

        // 1. Bind from the "BellaBaxter" section of whatever config is already registered
        var existing = builder.Build();
        existing.GetSection(BellaOptions.SectionName).Bind(options);

        // 1b. bella exec injects BELLA_BAXTER_URL and BELLA_BAXTER_API_KEY into the process.
        //     These always override whatever appsettings.json says so that running
        //     `bella exec -- dotnet run` works regardless of environment or appsettings values.
        //     The configure callback (step 2) still takes priority over everything.
        var envApiKey = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY");
        if (!string.IsNullOrEmpty(envApiKey))
            options.ApiKey = envApiKey;

        var envUrl = Environment.GetEnvironmentVariable("BELLA_BAXTER_URL");
        if (!string.IsNullOrEmpty(envUrl))
            options.BaxterUrl = envUrl;

        // BELLA_BAXTER_PRIVATE_KEY enables ZKE mode — persistent device key for transport
        // and DEK lease caching. Generate with: bella auth setup
        var envPrivateKey = Environment.GetEnvironmentVariable("BELLA_BAXTER_PRIVATE_KEY");
        if (!string.IsNullOrEmpty(envPrivateKey))
            options.PrivateKey = envPrivateKey;

        // bella sdk run in JWT mode injects BELLA_BAXTER_ACCESS_TOKEN, BELLA_BAXTER_PROJECT,
        // and BELLA_BAXTER_ENV so the SDK can authenticate with the user's OAuth2 token
        // instead of an API key. These override appsettings values but lose to the callback.
        var envAccessToken = Environment.GetEnvironmentVariable("BELLA_BAXTER_ACCESS_TOKEN");
        if (!string.IsNullOrEmpty(envAccessToken))
            options.AccessToken = envAccessToken;

        var envProject =
            Environment.GetEnvironmentVariable("BELLA_BAXTER_PROJECT")
            ?? Environment.GetEnvironmentVariable("BELLA_PROJECT");
        if (!string.IsNullOrEmpty(envProject))
            options.ProjectSlug = envProject;

        var envEnvironment =
            Environment.GetEnvironmentVariable("BELLA_BAXTER_ENV")
            ?? Environment.GetEnvironmentVariable("BELLA_ENV");
        if (!string.IsNullOrEmpty(envEnvironment))
            options.EnvironmentSlug = envEnvironment;

        // 2. Allow override via configure callback (highest priority)
        configure?.Invoke(options);

        ValidateOptions(options);

        return builder.Add(new BellaConfigurationSource(options, logger));
    }

    private static void ValidateOptions(BellaOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaxterUrl))
            throw new InvalidOperationException(
                "[BellaBaxter] BaxterUrl is required. Set it in appsettings.json under 'BellaBaxter:BaxterUrl' "
                    + "or via the configure callback."
            );

        var hasApiKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        var hasAccessToken = !string.IsNullOrWhiteSpace(options.AccessToken);

        if (!hasApiKey && !hasAccessToken)
            throw new InvalidOperationException(
                "[BellaBaxter] Authentication is required. Either:\n"
                    + "  • Set 'BellaBaxter:ApiKey' (obtain one and store it with: bella login --api-key bax-...), or\n"
                    + "  • Run via 'bella sdk run' with interactive login (bella login) — the CLI injects BELLA_BAXTER_ACCESS_TOKEN automatically."
            );

        if (
            hasAccessToken
            && (
                string.IsNullOrWhiteSpace(options.ProjectSlug)
                || string.IsNullOrWhiteSpace(options.EnvironmentSlug)
            )
        )
            throw new InvalidOperationException(
                "[BellaBaxter] ProjectSlug and EnvironmentSlug are required when using AccessToken (JWT) auth. "
                    + "Run: bella sdk run -p <project> -e <env> -- <command>, or set 'BellaBaxter:ProjectSlug' and 'BellaBaxter:EnvironmentSlug'."
            );

        // Note: when using ApiKey, ProjectSlug and EnvironmentSlug are optional — they are
        // auto-resolved from the API key via GET /api/v1/keys/me at first fetch.
    }
}

/// <summary>
/// Extension methods for registering Bella typed-secrets classes in the DI container.
/// </summary>
public static class BellaServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="T"/> (a source-generated typed-secrets class) as a
    /// singleton in the DI container, resolving secret values from <see cref="IConfiguration"/>.
    ///
    /// <example>
    /// <code>
    /// // Program.cs
    /// builder.Configuration.AddBellaSecrets();          // loads secrets into IConfiguration
    /// builder.Services.AddBellaTypedSecrets&lt;BellaAppSecrets&gt;();  // registers typed class
    ///
    /// // Minimal API endpoint
    /// app.MapGet("/secrets", (BellaAppSecrets s) => Results.Ok(new { s.Port, s.DatabaseUrl }));
    ///
    /// // Or via constructor injection in a service
    /// public class MyService(BellaAppSecrets secrets) { ... }
    /// </code>
    /// </example>
    ///
    /// <typeparam name="T">
    /// A class generated by <c>BellaBaxter.SourceGenerator</c> (or any class with a
    /// public constructor accepting <see cref="IConfiguration"/>).
    /// </typeparam>
    /// </summary>
    public static IServiceCollection AddBellaTypedSecrets<T>(this IServiceCollection services)
        where T : class
    {
        // Let ASP.NET Core DI resolve the IConfiguration constructor automatically via
        // ActivatorUtilities (the same engine used by all AddSingleton<T>() calls).
        // DI selects the longest constructor whose parameters are all registered —
        // which is BellaAppSecrets(IConfiguration), injected from the DI container.
        services.AddSingleton<T>();
        return services;
    }
}
