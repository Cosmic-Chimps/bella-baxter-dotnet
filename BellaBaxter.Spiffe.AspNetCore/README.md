# BellaBaxter.Spiffe.AspNetCore

ASP.NET Core middleware that validates incoming X.509 client certificates as [SPIFFE](https://spiffe.io/) SVIDs issued by Bella Baxter's OpenBao PKI. It extracts the workload identity from the certificate's SAN URI and makes it available as `HttpContext` claims for zero-trust service-to-service authentication.

## What it does

1. **Fetches** the CA trust bundle from `GET /api/v1/environments/{envId}/trust-bundle` on startup
2. **Refreshes** the trust bundle on a configurable background schedule (default: every 1 hour)
3. **Validates** each incoming TLS client certificate against the trust-bundle CA via X.509 chain validation
4. **Extracts** the SPIFFE ID from the certificate's X.509 SAN URI field (`spiffe://…`)
5. **Validates** the SPIFFE ID format: `spiffe://{tenant}/{project}/{env}/{workload}`
6. **Populates** `HttpContext.User` with three claims:
   - `spiffe-id` — full SPIFFE URI
   - `spiffe-tenant` — tenant slug
   - `spiffe-workload` — workload name
7. **Authorizes** specific endpoints via `[RequireSpiffeId("pattern")]` or `.RequireSpiffeId("pattern")` using glob pattern matching

## Trust-bundle caching and CA rotation

The bundle is fetched once at startup — `StartAsync` blocks until it succeeds, so the middleware never
serves requests without a trust anchor — and refreshed on `TrustBundleRefreshInterval` (default 1h)
thereafter.

Three properties of that cache are load-bearing, and each exists because the alternative fails badly:

**Every certificate in the bundle is trusted, not just the first.** A CA rotation works by publishing
BOTH the outgoing and the incoming CA for an overlap window, so workloads holding an SVID from either
one keep working while they re-attest. The middleware builds each chain against the whole set, and the
bundle's ORDER decides nothing. (An earlier version read the PEM with
`X509Certificate2.CreateFromPem`, which keeps only the first certificate — so during a rotation every
SVID signed by the other CA was rejected as untrusted. Nothing in your service changed; half of it
simply stopped being able to call the other half.)

**A failed refresh keeps the previous bundle.** A transient 500 or a network blip must not empty the
trust set, because an empty trust set rejects everything. Failures are logged at Error and the last
good bundle stays in force. Equally, a response that parses to zero certificates is treated as a failed
read, not as "trust nothing".

**An unloaded bundle rejects, it does not admit.** If the very first fetch has not completed, requests
get 401 `CertNotTrusted`. Absence of an answer is never a pass.

**Sizing the refresh interval.** The interval is the worst-case delay before this service learns about
a newly added CA. It must be comfortably shorter than the rotation overlap window — with the default
1h, an overlap of a few hours is safe, and a rotation that publishes and retires within an hour is not.
Lowering it costs one small HTTP request per interval.

## Certificate validity window

Checked in UTC on both sides. Worth stating because `X509Certificate2.NotBefore`/`NotAfter` return
LOCAL time, and comparing them against `DateTime.UtcNow` — as this middleware once did — is wrong by
the host's UTC offset: east of UTC a freshly issued SVID reads as not-yet-valid (every request
refused, reported as an expiry problem), west of UTC an expired certificate stays accepted for the
length of the offset. `CertificateValidityWindow` is the single place that conversion happens.

There is no clock-skew allowance here. The issuing PKI backdates `notBefore` (OpenBao's
`not_before_duration`, 30s by default) and that is the margin; a second tolerance in the middleware
would extend every certificate's effective life by an amount nobody configured.

## Quick Start

### 1. Register services

```csharp
// Program.cs
builder.Services.AddBellaSpiffe(o =>
{
    o.BellaBaseUrl  = "https://api.bella.example.com";
    o.EnvironmentId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    // Optional:
    o.TrustBundleRefreshInterval = TimeSpan.FromMinutes(30);
    o.AllowMissingClientCert = false; // reject non-mTLS requests (default)
});
```

### 2. Add middleware

```csharp
// Must come after UseRouting() and before UseAuthorization()
app.UseSpiffeValidation();
```

### 3. Use claims downstream

```csharp
app.MapGet("/me", (HttpContext ctx) =>
{
    var spiffeId  = ctx.User.FindFirst("spiffe-id")?.Value;
    var tenant    = ctx.User.FindFirst("spiffe-tenant")?.Value;
    var workload  = ctx.User.FindFirst("spiffe-workload")?.Value;
    return Results.Ok(new { spiffeId, tenant, workload });
});
```

## `RequireSpiffeId` usage

### Minimal APIs (endpoint filter)

```csharp
app.MapGet("/internal/data", () => Results.Ok("secret"))
   .RequireSpiffeId("spiffe://acme/payments/prod/*");

// Multi-segment wildcard
app.MapGet("/admin", () => Results.Ok("admin"))
   .RequireSpiffeId("spiffe://acme/**/admin-service");
```

### MVC controllers / actions (authorization filter)

```csharp
[RequireSpiffeId("spiffe://acme/payments/prod/*")]
public class PaymentsController : ControllerBase { ... }

// Override at action level
[RequireSpiffeId("spiffe://acme/payments/prod/billing-service")]
public IActionResult SensitiveAction() { ... }
```

### Pattern syntax

| Pattern | Matches | Does not match |
|---|---|---|
| `spiffe://acme/payments/prod/*` | `spiffe://acme/payments/prod/billing` | `spiffe://acme/payments/staging/billing` |
| `spiffe://*/payments/prod/*` | Any tenant's payments prod workloads | Other envs/projects |
| `spiffe://acme/**` | Any SPIFFE ID under the `acme` tenant | Other tenants |

## Integration with the Bella Agent sidecar

The [bella agent](https://docs.bella-baxter.io/agent) runs as a sidecar and provisions SPIFFE SVIDs for workloads via SPIRE. Each workload receives a short-lived X.509 certificate with a SAN URI of the form `spiffe://{tenant}/{project}/{env}/{workload}`.

To connect to a service protected by this middleware:
1. Configure mTLS on the outbound `HttpClient` using the SVID cert provided by the bella agent socket
2. The receiving service validates the cert against its Bella Baxter trust bundle
3. The SPIFFE ID flows through as `HttpContext.User` claims for fine-grained authorization

### Environment variables

| Variable | Description |
|---|---|
| _(none required)_ | Config is provided programmatically via `AddBellaSpiffe(...)` |

For local development without a running Bella agent, set `AllowMissingClientCert = true` and the middleware will pass through requests without client certificates.

## Options reference

| Property | Type | Default | Description |
|---|---|---|---|
| `BellaBaseUrl` | `string` | _(required)_ | Bella Baxter API base URL |
| `EnvironmentId` | `Guid` | _(required)_ | Environment whose trust bundle to fetch |
| `TrustBundleRefreshInterval` | `TimeSpan` | `1h` | How often to refresh the CA cert |
| `AllowMissingClientCert` | `bool` | `false` | Pass through requests without a cert |
| `OnValidationFailed` | `Func<HttpContext, SpiffeValidationError, Task>?` | `null` | Custom error handler (default: 401 JSON) |
