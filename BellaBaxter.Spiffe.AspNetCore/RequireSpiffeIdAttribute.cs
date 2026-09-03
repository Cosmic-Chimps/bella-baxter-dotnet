using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>
/// Authorization attribute that validates the <c>spiffe-id</c> claim on the current request
/// against a glob pattern.
/// <para>
/// Use on MVC controllers or actions. For Minimal API endpoints use the
/// <c>RouteHandlerBuilder.RequireSpiffeId(pattern)</c> extension method instead.
/// </para>
/// <example>
/// <code>
/// [RequireSpiffeId("spiffe://acme/payments/prod/*")]
/// public IActionResult GetPayments() { ... }
/// </code>
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireSpiffeIdAttribute : Attribute, IAuthorizationFilter
{
    private readonly string _pattern;

    /// <param name="pattern">
    /// Glob pattern to match against the <c>spiffe-id</c> claim.
    /// <c>*</c> matches a single path segment; <c>**</c> matches across segments.
    /// </param>
    public RequireSpiffeIdAttribute(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern must not be empty.", nameof(pattern));
        _pattern = pattern;
    }

    /// <inheritdoc />
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var spiffeId = context.HttpContext.User.FindFirst(SpiffeClaims.SpiffeId)?.Value;

        if (string.IsNullOrEmpty(spiffeId) || !SpiffeIdValidator.MatchesGlobPattern(_pattern, spiffeId))
        {
            context.Result = new JsonResult(new
            {
                error   = SpiffeValidationError.SpiffeIdNotAllowed.ToString(),
                message = $"SPIFFE ID is not permitted. Required pattern: {_pattern}",
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
