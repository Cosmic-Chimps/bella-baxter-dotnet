using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace BellaBaxter.Spiffe.AspNetCore.Tests;

/// <summary>
/// Spec 001 T038 (US5, FR-022) — <c>[RequireSpiffeId]</c>, the per-endpoint allow-list.
/// </summary>
/// <remarks>
/// <para>The middleware answers "who is calling"; this answers "may they call THIS". It is the second
/// half of the authorisation decision and the one a consumer writes by hand, so its glob semantics have
/// to be exactly what the documentation claims: <c>*</c> within one path segment, <c>**</c> across
/// segments. A <c>*</c> that quietly crossed a <c>/</c> would turn
/// <c>spiffe://acme/payments/prod/*</c> — meant as "any workload in prod payments" — into something
/// that also admitted every other project and environment.</para>
/// </remarks>
public class RequireSpiffeIdTests
{
    private const string Caller = "spiffe://acme/payments/prod/billing-service";

    // ===== the attribute =====

    [Fact]
    public void A_matching_SPIFFE_ID_is_allowed_through()
    {
        var ctx = Authorize("spiffe://acme/payments/prod/*", Caller);
        Assert.Null(ctx.Result);
    }

    [Fact]
    public void A_NON_matching_SPIFFE_ID_is_refused_with_403_not_401()
    {
        // 403, deliberately: the caller authenticated fine, they are simply not permitted here. A 401
        // would tell a correctly-identified workload to go and get credentials it already has.
        var ctx = Authorize("spiffe://acme/ledger/prod/*", Caller);

        var result = Assert.IsType<JsonResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public void An_UNAUTHENTICATED_request_is_refused_too()
    {
        // With AllowMissingClientCert the middleware passes anonymous requests through, so an endpoint
        // carrying this attribute must not treat a missing claim as a pass. This is the interaction
        // between the two halves, and the one place a mixed-mTLS host could leak an endpoint.
        var ctx = Authorize("spiffe://acme/payments/prod/*", spiffeId: null);

        Assert.IsType<JsonResult>(ctx.Result);
    }

    [Fact]
    public void An_EMPTY_spiffe_id_claim_is_refused()
    {
        var ctx = Authorize("spiffe://acme/payments/prod/*", spiffeId: string.Empty);

        Assert.IsType<JsonResult>(ctx.Result);
    }

    [Fact]
    public void The_refusal_names_the_pattern_that_was_required()
    {
        // The operator reading this is usually the person who wrote the pattern. Naming it turns a
        // 403 into a one-line fix; omitting it means reading someone else's source.
        var ctx = Authorize("spiffe://acme/ledger/prod/*", Caller);

        var body = Assert.IsType<JsonResult>(ctx.Result).Value!.ToString()!;
        Assert.Contains("spiffe://acme/ledger/prod/*", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_pattern_is_rejected_at_CONSTRUCTION()
    {
        // Fail at startup, not per request. An empty pattern would otherwise become an attribute that
        // matches nothing and 403s every caller — or, with a different glob implementation, one that
        // matches everything.
        Assert.Throws<ArgumentException>(() => new RequireSpiffeIdAttribute(string.Empty));
        Assert.Throws<ArgumentException>(() => new RequireSpiffeIdAttribute("   "));
    }

    // ===== glob semantics =====

    [Fact]
    public void A_single_star_matches_ONE_segment_and_does_not_cross_a_slash()
    {
        // The load-bearing rule. If `*` crossed `/`, then "any workload in prod payments" would also
        // admit every workload in every other project — a scope error nobody would see in review,
        // because the pattern would still read correctly.
        Assert.True(SpiffeIdValidator.MatchesGlobPattern("spiffe://acme/payments/prod/*", Caller));

        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/*", "spiffe://acme/payments/prod/billing-service"));
        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/payments/*", "spiffe://acme/payments/prod/billing-service"));
    }

    [Fact]
    public void A_double_star_matches_ACROSS_segments()
    {
        Assert.True(SpiffeIdValidator.MatchesGlobPattern("spiffe://acme/**", Caller));
        Assert.True(SpiffeIdValidator.MatchesGlobPattern("spiffe://acme/payments/**", Caller));
    }

    [Fact]
    public void A_pattern_for_one_tenant_never_matches_another()
    {
        // The tenant boundary, which in a shared cluster is the boundary that matters most.
        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/**", "spiffe://evil-corp/payments/prod/billing-service"));

        // Including the prefix trap: a tenant slug that merely STARTS with the allowed one.
        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/**", "spiffe://acme-evil/payments/prod/billing-service"));
    }

    [Fact]
    public void A_pattern_is_anchored_at_BOTH_ends()
    {
        // Unanchored matching would make every pattern a substring test — the most dangerous possible
        // default for an allow-list.
        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/payments/prod/billing-service", $"{Caller}-evil"));
        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/payments/prod/billing-service", $"prefix-{Caller}"));
    }

    [Fact]
    public void Regex_metacharacters_in_a_pattern_are_literal_not_operators()
    {
        // A pattern is a glob, not a regex. If `.` stayed a wildcard, a pattern naming one workload
        // would silently admit near-miss names.
        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/payments/prod/billing.service",
            "spiffe://acme/payments/prod/billingXservice"));

        Assert.True(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/payments/prod/billing.service",
            "spiffe://acme/payments/prod/billing.service"));
    }

    [Fact]
    public void A_trailing_newline_on_the_ID_does_not_satisfy_a_pattern()
    {
        // Hardening rather than a live hole — the SPIFFE ID comes from a CA-issued SAN, so an attacker
        // cannot choose it. But .NET's `$` also matches BEFORE a final newline, so an unanchored-at-\z
        // pattern would treat "…/billing-service\n" as the permitted id. An allow-list is the wrong
        // place to leave that lying around.
        Assert.False(SpiffeIdValidator.MatchesGlobPattern(
            "spiffe://acme/payments/prod/billing-service", $"{Caller}\n"));
    }

    // ===== harness =====

    private static AuthorizationFilterContext Authorize(string pattern, string? spiffeId)
    {
        var http = new DefaultHttpContext();
        if (spiffeId is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(SpiffeClaims.SpiffeId, spiffeId)], "spiffe"));
        }

        var context = new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            []);

        new RequireSpiffeIdAttribute(pattern).OnAuthorization(context);
        return context;
    }
}
