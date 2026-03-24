# BellaBaxter.AspNet.Configuration

ASP.NET Core `IConfiguration` provider that polls [Bella Baxter](https://bella-baxter.io) for secrets
and hot-reloads them into your app — no restart required.

Works with any .NET app that uses `IConfiguration`: ASP.NET Core, Worker Service, console apps.

## Installation

```bash
dotnet add package BellaBaxter.AspNet.Configuration
```

## Quickstart

**appsettings.json** (non-secret config — safe to commit):
```json
{
  "BellaBaxter": {
    "BaxterUrl": "https://api.bella-baxter.io",
    "EnvironmentSlug": "production",
    "PollingInterval": "00:01:00"
  }
}
```

**Program.cs**:
```csharp
var builder = WebApplication.CreateBuilder(args);

// Step 1 — Add Bella secrets as an IConfiguration source
// Reads BaxterUrl + EnvironmentSlug from appsettings.json
// Reads ApiKey from BELLA_BAXTER_API_KEY env var or BellaBaxter__ApiKey
builder.Configuration.AddBellaSecrets();

// Step 2 — Register source-generated typed class in DI (optional)
builder.Services.AddBellaTypedSecrets<BellaAppSecrets>();

// Step 3 — Inject anywhere (typed class, IConfiguration, IOptions<T>)
app.MapGet("/", (BellaAppSecrets s) => Results.Ok(new { s.Port, s.DatabaseUrl }));
app.MapGet("/raw", (IConfiguration config) => Results.Ok(config["DATABASE_URL"]));
```

**Credentials — never in appsettings.json:**

| Method | How |
|--------|-----|
| `bella exec -- dotnet run` (recommended for local dev) | Injects `BELLA_BAXTER_API_KEY` + `BELLA_BAXTER_URL` automatically |
| .NET User Secrets | `dotnet user-secrets set "BellaBaxter:ApiKey" "bax-..."` |
| Environment variable | `BellaBaxter__ApiKey=bax-...` |

## Explicit configuration

```csharp
builder.Configuration.AddBellaSecrets(o =>
{
    o.BaxterUrl        = "https://api.bella-baxter.io";
    o.EnvironmentSlug  = "production";
    o.ApiKey           = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")!;
    o.PollingInterval  = TimeSpan.FromSeconds(30);
    o.FallbackOnError  = true; // keep serving cached secrets if Baxter is temporarily unreachable
});
```

## How it works

1. On startup: `GET /api/v1/environments/{slug}/secrets` → loads all secrets into `IConfiguration`
2. Every `PollingInterval`: `GET /api/v1/environments/{slug}/secrets/version` (lightweight version check)
3. If version changed: fetches full secrets → calls `OnReload()` → triggers `IOptionsMonitor<T>.OnChange()`
4. `IConfiguration["MY_SECRET"]` always reflects the latest value

Baxter serves secrets from a **Redis HybridCache** — polling does not hit your cloud provider (AWS/Azure/GCP/Vault) on every request. Cloud costs stay near zero regardless of polling frequency.

## Typed secrets with source generator

Combine with [`BellaBaxter.SourceGenerator`](../BellaBaxter.SourceGenerator/) for compile-time type safety:

```csharp
// Injected via DI — typed, IDE-friendly, no magic strings
app.MapGet("/", (BellaAppSecrets s) => new {
    s.DatabaseUrl,   // string
    s.Port,          // int
    s.FeatureFlag,   // bool
});
```

See the [samples](../samples/) for complete working examples.

## `__` double underscore = section separator

.NET's `IConfiguration` maps `__` to `:` in key names:

| Bella secret key | IConfiguration path |
|------------------|---------------------|
| `DATABASE_URL` | `config["DATABASE_URL"]` |
| `ConnectionStrings__Default` | `config["ConnectionStrings:Default"]` |
| `Jwt__Secret` | `config["Jwt:Secret"]` |

This means `builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"))` maps directly to your `Jwt__*` secrets.
