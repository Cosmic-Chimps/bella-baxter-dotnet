# BellaBaxter .NET SDK

Five NuGet packages that integrate Bella Baxter secrets into .NET applications.

## Packages

| Package | Purpose |
|---------|---------|
| [`BellaBaxter.Client`](BellaBaxter.Client/) | Generated HTTP client for the Bella Baxter API — the foundation all other packages build on |
| [`BellaBaxter.AspNet.Configuration`](BellaBaxter.AspNet.Configuration/) | `IConfiguration` provider — polls Baxter, hot-reloads secrets into any ASP.NET Core or Worker Service app |
| [`BellaBaxter.Aspire.Configuration`](BellaBaxter.Aspire.Configuration/) | Aspire AppHost integration — injects Baxter connection into Aspire-hosted services |
| [`BellaBaxter.Aspire.Host`](BellaBaxter.Aspire.Host/) | Spins up the full Bella Baxter infrastructure stack (Postgres, Redis, Keycloak, OpenBao, API) in a local Aspire AppHost |
| [`BellaBaxter.SourceGenerator`](BellaBaxter.SourceGenerator/) | Roslyn source generator + MSBuild task — generates typed `AppSecrets` class from your secrets manifest at compile time |

---

## BellaBaxter.Client

The low-level generated HTTP client. Most apps should use `BellaBaxter.AspNet.Configuration` instead — but `BellaBaxter.Client` is useful for custom integrations, scripts, and tools.

```bash
dotnet add package BellaBaxter.Client
```

```csharp
using BellaBaxter.Client;

var client = BellaClientFactory.Create(
    apiUrl: "https://api.bella-baxter.io",
    apiKey: Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")!
);

// List secrets
var secrets = await client.Environments.GetSecretsAsync(environmentId);

// Create/update a secret
await client.Secrets.UpsertAsync(environmentId, "DATABASE_URL", "postgres://...");
```

Includes built-in **end-to-end encryption** — secret values are encrypted client-side using ECIES (via `BellaBaxter.Crypto`) so they are never transmitted in plaintext even to the Baxter API.

See [`BellaBaxter.Client/README.md`](BellaBaxter.Client/README.md) for full API reference.

---

## Authentication

### OAuth (for humans — recommended for local dev)
```bash
bella login   # opens browser, creates .bella file
```
Requires a `.bella` file with `project` and `environment` to identify the target.
**Not billed** on pay-as-you-go plans.

### API key (for machines, CI/CD, production)
```bash
bella login --api-key bax-<keyId>-<secret>
# or: bella exec -- dotnet run  (injects BELLA_BAXTER_API_KEY + BELLA_BAXTER_URL)
```
API keys encode the project and environment — no `.bella` file required.
**Billed** on pay-as-you-go plans. Generate via `bella api-keys create` or the WebApp.

---

## BellaBaxter.AspNet.Configuration

### Quickstart

```bash
dotnet add package BellaBaxter.AspNet.Configuration
```

**appsettings.json** (non-secret config — safe to commit):
```json
{
  "BellaBaxter": {
    "BaxterUrl": "https://baxter.example.com",
    "EnvironmentSlug": "production",
    "PollingInterval": "00:01:00"
  }
}
```

**Credentials — never in appsettings.json:**

| Method | How to set |
|--------|-----------|
| `bella exec -- dotnet run` (recommended) | Injects `BELLA_BAXTER_API_KEY` + `BELLA_BAXTER_URL` automatically |
| .NET User Secrets | `dotnet user-secrets set "BellaBaxter:ApiKey" "bax-..."` |
| Environment variable | `BellaBaxter__ApiKey=bax-...` |

**Program.cs**:
```csharp
var builder = WebApplication.CreateBuilder(args);

// Step 1 — Add Bella secrets as an IConfiguration source
// Reads BaxterUrl + EnvironmentSlug from appsettings; ApiKey from BELLA_BAXTER_API_KEY or BellaBaxter__ApiKey.
builder.Configuration.AddBellaSecrets();

// Step 2 — Register the source-generated typed class in DI (reads from IConfiguration, not System.Environment)
builder.Services.AddBellaTypedSecrets<BellaAppSecrets>();

// Step 3 — Inject anywhere: endpoints, controllers, services
app.MapGet("/", (BellaAppSecrets s) => Results.Ok(new { s.Port, s.DatabaseUrl }));
// Or use raw IConfiguration:
app.MapGet("/raw", (IConfiguration config) => Results.Ok(config["DATABASE_URL"]));
```

### Explicit configuration

```csharp
builder.Configuration.AddBellaSecrets(o =>
{
    o.BaxterUrl        = "https://baxter.example.com";
    o.EnvironmentSlug  = "production";
    o.ApiKey           = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")!;
    o.PollingInterval  = TimeSpan.FromSeconds(30);
    o.FallbackOnError  = true; // keep serving cached secrets if Baxter is down
});
```

### How it works

1. On startup: calls `GET /api/v1/environments/{slug}/secrets` → loads all secrets
2. Every `PollingInterval`: checks `GET /api/v1/environments/{slug}/secrets/version` (lightweight)
3. If version changed: fetches full secrets and calls `OnReload()` → triggers `IOptionsMonitor<T>.OnChange()`
4. `IConfiguration["MY_SECRET"]` always reflects the latest value

### Cost note

Baxter serves secrets from a **Redis HybridCache** — polling does NOT hit AWS/Azure/GCP/Vault on every request. Cloud provider costs remain near zero regardless of polling frequency.

---

## BellaBaxter.Aspire.Configuration

### Quickstart

```bash
dotnet add package BellaBaxter.Aspire.Configuration  # AppHost only
```

**AppHost Program.cs**:
```csharp
var bella = builder.AddBellaBaxter("bella", environmentSlug: "development");

builder.AddProject<Projects.MyApi>("api")
       .WithBellaSecrets(bella);  // injects BellaBaxter__ env vars into MyApi
```

**AppHost user secrets** (set the API key):
```bash
dotnet user-secrets set "Parameters:bella-api-key" "bax-..."
```

`WithBellaSecrets(bella)` injects into the target project:
- `BellaBaxter__BaxterUrl` — resolved from the Aspire resource endpoint
- `BellaBaxter__EnvironmentSlug` — from `AddBellaBaxter(environmentSlug: ...)`
- `BellaBaxter__ApiKey` — from the Aspire secret parameter

The target project only needs `builder.Configuration.AddBellaSecrets()` — all values arrive via environment variables.

---

## Generating an API key

```bash
bella api-keys create --env production --name "MyApi Production"
# Returns: bax-<keyId>-<secret>
```

The key encodes the project slug and environment slug. Store it in user secrets, a secrets manager, or inject it via `bella exec`.

---

## Typed Secrets Code Generation

`bella secrets generate dotnet` fetches the **secrets manifest** (key names + type hints, no values) from the Bella API and generates a strongly-typed partial class. Each property reads from the environment at runtime — **no secret values are ever embedded in generated code**.

```bash
bella secrets generate dotnet \
  --project my-app \
  --environment production \
  --namespace MyApp.Config \
  --class-name AppSecrets \
  --output AppSecrets.g.cs
```

**Generated `AppSecrets.g.cs`:**

```csharp
// Auto-generated by bella secrets generate dotnet — do not edit manually.
namespace MyApp.Config;

public partial class AppSecrets
{
    public string DatabaseUrl    => GetRequired("DATABASE_URL");
    public int    Port           => int.Parse(GetRequired("PORT"));
    public bool   EnableFeatureX => bool.Parse(GetRequired("ENABLE_FEATURE_X"));
}
```

### Using with the SDK

```csharp
// Program.cs
builder.Configuration.AddBellaSecrets(); // loads DATABASE_URL, PORT, etc.

// Anywhere in the app — compile-time type safety, no magic strings:
var s    = new AppSecrets();
var url  = s.DatabaseUrl;   // string, throws if missing
var port = s.Port;          // int, parsed automatically
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `-p, --project <slug>` | `.bella` context | Project slug |
| `-e, --environment <slug>` | `.bella` context | Environment slug |
| `--provider <slug>` | `default` | Provider slug |
| `-o, --output <path>` | `AppSecrets.g.cs` | Output file path |
| `--class-name <name>` | `AppSecrets` | Class name |
| `--namespace <ns>` | `AppSecrets` | C# namespace |
| `--dry-run` | — | Print to stdout without writing |

---

## BellaBaxter.Aspire.Host

### Quickstart

```bash
dotnet add package BellaBaxter.Aspire.Host
```

**AppHost Program.cs** — spin up the full Bella Baxter stack alongside your app:

```csharp
// Minimal — Bella owns all its infrastructure:
var bella = builder.AddBellaBaxter("bella");

// Bring your own Postgres + Redis (shared with your app):
var postgres = builder.AddPostgres("postgres");
var redis    = builder.AddRedis("redis");
var bella    = builder.AddBellaBaxter("bella", postgres: postgres, redis: redis);

// Wire Bella secrets into your API service:
builder.AddProject<Projects.MyApi>("api")
       .WithBellaSecrets(bella);
```

`WithBellaSecrets(bella)` injects the Baxter API URL into your service so `builder.Configuration.AddBellaSecrets()` can connect automatically.

### When to use this vs `BellaBaxter.Aspire.Configuration`

| Package | Use when |
|---------|----------|
| `BellaBaxter.Aspire.Host` | You want Bella's full stack running **locally** (dev/testing without a deployed Bella instance) |
| `BellaBaxter.Aspire.Configuration` | You have a **deployed** Bella instance and just need Aspire to wire the connection URL |

---

## Origin

Migrated from [BellaGotNet](../../BellaGotNet) — the original prototype built in November 2025.
Key changes from prototype:
- Auth: API key (`bax-...`) instead of X-API-Key/X-Secret-Key headers
- Endpoints: new .NET API (GUIDs/slugs, not int IDs)
- Removed WebSocket provider (planned for future — polling + Redis cache is sufficient for MVP)
- Aspire: uses `BellaBarterStackResource` and `AddBellaBaxter()` / `WithBellaSecrets()`


