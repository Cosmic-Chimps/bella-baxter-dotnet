using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding the full Bella Baxter infrastructure to an Aspire AppHost.
///
/// <example>
/// <code>
/// // Minimal — Bella owns all its infrastructure:
/// var bella = builder.AddBellaBaxter("bella");
///
/// // Bring your own Postgres + Redis (share with your app):
/// var postgres = builder.AddPostgres("postgres");
/// var redis    = builder.AddRedis("redis");
/// var bella    = builder.AddBellaBaxter("bella", postgres: postgres, redis: redis);
///
/// // Wire to your service:
/// builder.AddProject&lt;Projects.MyApi&gt;("api")
///        .WithBellaSecrets(bella);
/// </code>
/// </example>
/// </summary>
public static class BellaBarterStackExtensions
{
    private const string DefaultApiImage = "ghcr.io/cosmic-chimps/bella-baxter-api";
    private const string DefaultApiTag   = "latest";
    private const string DefaultRealm    = "bella-baxter";
    private const int    KeycloakPort    = 8080;
    private const int    OpenBaoPort     = 8200;
    private const int    ApiPort         = 5000;

    /// <summary>
    /// Adds the full Bella Baxter stack to the Aspire AppHost.
    ///
    /// By default, Bella Baxter provisions its own PostgreSQL and Redis containers.
    /// Pass <paramref name="postgres"/> and/or <paramref name="redis"/> to share
    /// existing resources already declared in your AppHost.
    ///
    /// The stack always provisions its own Keycloak and OpenBao instances — they
    /// are Bella-internal concerns and cannot be shared.
    ///
    /// Components:
    /// <list type="bullet">
    ///   <item>PostgreSQL — Marten event store (shared or owned)</item>
    ///   <item>Redis — secret cache (shared or owned)</item>
    ///   <item>Keycloak — identity provider (Bella-owned)</item>
    ///   <item>OpenBao — default secret vault in dev mode (Bella-owned)</item>
    ///   <item>Bella API — Docker image from registry</item>
    /// </list>
    /// </summary>
    /// <param name="builder">The Aspire distributed application builder.</param>
    /// <param name="name">Logical name prefix (e.g. "bella"). All Bella-owned resources
    /// are named <c>{name}-postgres</c>, <c>{name}-redis</c>, etc.</param>
    /// <param name="postgres">
    /// Optional user-provided PostgreSQL resource. When supplied, Bella uses this database
    /// instead of creating its own. A new database named <c>{name}-db</c> is added to it.
    /// </param>
    /// <param name="redis">
    /// Optional user-provided Redis resource. When supplied, Bella uses this cache
    /// instead of creating its own.
    /// </param>
    /// <param name="apiImage">Docker image for the Bella API (default: ghcr.io/cosmic-chimps/bella-baxter-api).</param>
    /// <param name="apiTag">Docker tag (default: latest).</param>
    /// <param name="realm">Keycloak realm (default: bella-baxter).</param>
    public static IResourceBuilder<BellaBarterStackResource> AddBellaBaxter(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<PostgresServerResource>? postgres = null,
        IResourceBuilder<RedisResource>? redis = null,
        string apiImage = DefaultApiImage,
        string apiTag   = DefaultApiTag,
        string realm    = DefaultRealm
    )
    {
        // ── PostgreSQL ────────────────────────────────────────────────────────
        var db = (postgres ?? builder
                .AddPostgres($"{name}-postgres")
                .WithImage("postgres", "16-alpine")
                .WithDataVolume($"{name}-postgres-data"))
            .AddDatabase($"{name}-db");

        // ── Redis ─────────────────────────────────────────────────────────────
        var cache = redis ?? builder
            .AddRedis($"{name}-redis")
            .WithDataVolume($"{name}-redis-data");

        // ── Keycloak (Bella-owned, always) ────────────────────────────────────
        var keycloak = builder
            .AddKeycloak($"{name}-keycloak", port: KeycloakPort)
            .WithDataVolume($"{name}-keycloak-data");

        // ── OpenBao dev mode (Bella-owned, always) ────────────────────────────
        // Uses a dev-mode root token stored as a secret parameter.
        // Real production deployments would use the unsealed setup from setup-dev.sh.
        var openBaoDevToken = builder.AddParameter($"{name}-openbao-token", secret: true);
        var openBaoListenAddr = $"0.0.0.0:{OpenBaoPort}";

        var openbao = builder
            .AddContainer($"{name}-openbao", "ghcr.io/openbao/openbao", "2.5.1")
            .WithHttpEndpoint(port: OpenBaoPort, targetPort: OpenBaoPort, name: "http")
            .WithEnvironment("OPENBAO_DEV_LISTEN_ADDRESS", openBaoListenAddr)
            .WithEnvironment("OPENBAO_DEV_ROOT_TOKEN_ID", openBaoDevToken.Resource)
            .WithArgs("server", "-dev");

        // ── Bella API (Docker container) ──────────────────────────────────────
        var api = builder
            .AddContainer($"{name}-api", apiImage, apiTag)
            .WithHttpEndpoint(port: ApiPort, targetPort: ApiPort, name: "http")
            .WithReference(db)
            .WithReference(cache)
            .WithEnvironment("Keycloak__PublicUrl", keycloak.GetEndpoint("http"))
            .WithEnvironment("Keycloak__Realm", realm)
            .WithEnvironment("OpenBao__ServerUri", openbao.GetEndpoint("http"))
            .WithEnvironment("OpenBao__DevRootToken", openBaoDevToken.Resource)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WaitFor(db)
            .WaitFor(cache)
            .WaitFor(keycloak)
            .WaitFor(openbao);

        // ── Stack resource (façade exposed to callers) ────────────────────────
        var stackResource = new BellaBarterStackResource(
            name,
            (IResourceBuilder<IResourceWithConnectionString>)(object)api
        );

        return builder
            .AddResource(stackResource)
            .WithAnnotation(
                new ManifestPublishingCallbackAnnotation(ctx =>
                {
                    ctx.Writer.WriteStringValue(
                        $"# Bella Baxter stack ({name}) — see BellaBaxter.Aspire.Host"
                    );
                    return Task.CompletedTask;
                })
            );
    }

    /// <summary>
    /// Injects Bella connection details into a project so it can call
    /// <c>builder.Configuration.AddBellaSecrets()</c> at startup.
    ///
    /// Injected environment variables:
    /// <list type="bullet">
    ///   <item><c>BellaBaxter__BaxterUrl</c> — base URL of the local Bella API</item>
    ///   <item><c>BellaBaxter__ApiKey</c> — API key (bax-...) from user secrets</item>
    /// </list>
    ///
    /// Project and environment are resolved automatically from the API key via /api/v1/keys/me.
    /// </summary>
    public static IResourceBuilder<TDestination> WithBellaSecrets<TDestination>(
        this IResourceBuilder<TDestination> projectBuilder,
        IResourceBuilder<BellaBarterStackResource> bella,
        string? apiKeyParameterName = null
    )
        where TDestination : IResourceWithEnvironment
    {
        var stackName = bella.Resource.Name;

        var apiKeyParam = projectBuilder.ApplicationBuilder.AddParameter(
            apiKeyParameterName ?? $"{stackName}-api-key",
            secret: true
        );

        return projectBuilder
            .WithEnvironment("BellaBaxter__BaxterUrl", bella.Resource.GetApiEndpoint())
            .WithEnvironment(ctx =>
            {
                ctx.EnvironmentVariables["BellaBaxter__ApiKey"] = apiKeyParam.Resource;
            });
    }
}
