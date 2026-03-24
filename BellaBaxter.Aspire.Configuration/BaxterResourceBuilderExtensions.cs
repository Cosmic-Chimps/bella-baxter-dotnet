using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for integrating Baxter into an Aspire AppHost.
/// </summary>
public static class BaxterResourceBuilderExtensions
{
    /// <summary>
    /// Declares a Baxter API connection in the Aspire AppHost.
    /// The API key is treated as a secret parameter and encodes the target
    /// project+environment — no separate slug needed.
    ///
    /// <example>
    /// AppHost Program.cs:
    /// <code>
    /// var baxter = builder.AddBaxter("baxter",
    ///     baxterUrl: "https://baxter.example.com");
    ///
    /// builder.AddProject&lt;Projects.MyApi&gt;("myapi")
    ///        .WithReference(baxter);
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="builder">The Aspire distributed application builder.</param>
    /// <param name="name">Logical name for the resource (used in Aspire dashboard).</param>
    /// <param name="baxterUrl">Base URL of the Baxter API.</param>
    /// <param name="apiKeyParameterName">
    /// Name of the Aspire secret parameter holding the API key (bax-...).
    /// Defaults to "bella-api-key". Set via user secrets or environment variable.
    /// </param>
    public static IResourceBuilder<BaxterResource> AddBaxter(
        this IDistributedApplicationBuilder builder,
        string name,
        string baxterUrl,
        string apiKeyParameterName = "bella-api-key")
    {
        var apiKeyParam = builder.AddParameter(apiKeyParameterName, secret: true);

        var resource = new BaxterResource(name, baxterUrl, apiKeyParam.Resource);
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Injects Baxter connection details into a referencing project as environment variables:
    ///   BellaBaxter__BaxterUrl  — base URL of the Baxter API
    ///   BellaBaxter__ApiKey     — secret API key (masked in Aspire dashboard)
    ///
    /// The target project should call builder.Configuration.AddBellaSecrets() at startup.
    /// Project and environment are resolved automatically from the API key via /api/v1/keys/me.
    /// </summary>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<BaxterResource> baxter)
        where TDestination : IResourceWithEnvironment
    {
        var r = baxter.Resource;

        return builder
            .WithEnvironment("BellaBaxter__BaxterUrl", r.BaxterUrl)
            .WithEnvironment(ctx =>
            {
                ctx.EnvironmentVariables["BellaBaxter__ApiKey"] = r.ApiKey;
            });
    }
}
