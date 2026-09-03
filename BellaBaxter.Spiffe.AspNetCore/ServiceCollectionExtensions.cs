using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>
/// Extension methods for registering and using Bella Baxter SPIFFE mTLS validation.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Bella Baxter SPIFFE trust-bundle cache and validation middleware.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure <see cref="SpiffeOptionsBuilder"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddBellaSpiffe(o =>
    /// {
    ///     o.BellaBaseUrl   = "https://api.bella.example.com";
    ///     o.EnvironmentId  = Guid.Parse("...");
    ///     o.TrustBundleRefreshInterval = TimeSpan.FromMinutes(30);
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddBellaSpiffe(
        this IServiceCollection services,
        Action<SpiffeOptionsBuilder> configure)
    {
        var builder = new SpiffeOptionsBuilder();
        configure(builder);
        var options = builder.Build();

        services.AddSingleton(options);
        services.AddHttpClient(SpiffeConstants.HttpClientName);
        services.AddSingleton<ISpiffeTrustBundleCache, SpiffeTrustBundleCache>();
        services.AddHostedService(sp => (SpiffeTrustBundleCache)sp.GetRequiredService<ISpiffeTrustBundleCache>());

        return services;
    }

    /// <summary>
    /// Adds the <see cref="SpiffeMiddleware"/> to the request pipeline.
    /// Must be called after <c>app.UseRouting()</c> and before <c>app.UseAuthorization()</c>.
    /// </summary>
    public static IApplicationBuilder UseSpiffeValidation(this IApplicationBuilder app)
        => app.UseMiddleware<SpiffeMiddleware>();

    /// <summary>
    /// Restricts a Minimal API endpoint to callers whose <c>spiffe-id</c> claim matches
    /// <paramref name="pattern"/>.
    /// </summary>
    /// <param name="builder">The route handler builder.</param>
    /// <param name="pattern">
    /// Glob pattern, e.g. <c>"spiffe://*/payments/prod/*"</c>.
    /// <c>*</c> = single segment, <c>**</c> = multi-segment.
    /// </param>
    public static RouteHandlerBuilder RequireSpiffeId(
        this RouteHandlerBuilder builder,
        string pattern)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var spiffeId = context.HttpContext.User.FindFirst(SpiffeClaims.SpiffeId)?.Value;
            if (string.IsNullOrEmpty(spiffeId) || !SpiffeIdValidator.MatchesGlobPattern(pattern, spiffeId))
            {
                return Results.Json(
                    new
                    {
                        error   = SpiffeValidationError.SpiffeIdNotAllowed.ToString(),
                        message = $"SPIFFE ID not permitted. Required: {pattern}",
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            return await next(context);
        });
    }
}
