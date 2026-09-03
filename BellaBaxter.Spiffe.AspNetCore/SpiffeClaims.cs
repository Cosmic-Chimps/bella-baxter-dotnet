namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>Claim type constants populated by <see cref="SpiffeMiddleware"/> on each authenticated request.</summary>
public static class SpiffeClaims
{
    /// <summary>The full SPIFFE URI, e.g. "spiffe://acme/payments/prod/billing-service".</summary>
    public const string SpiffeId = "spiffe-id";

    /// <summary>The tenant slug extracted from the SPIFFE URI, e.g. "acme".</summary>
    public const string SpiffeTenant = "spiffe-tenant";

    /// <summary>The workload name extracted from the SPIFFE URI, e.g. "billing-service".</summary>
    public const string SpiffeWorkload = "spiffe-workload";
}
