# .NET SDK Samples

Samples showing how to integrate Bella Baxter secrets into .NET applications.

## Authentication

**OAuth (local dev, not billed):**
```bash
bella login          # opens browser, writes .bella file
```
The `.bella` file (project + environment) is required for OAuth — the token doesn't encode them.

**API key (CI/CD, production, billed):**
```bash
# API key encodes project + environment — no .bella file or -p/-e flags needed
bella exec -- dotnet run                 # injects BELLA_BAXTER_API_KEY + BELLA_BAXTER_URL
bella login --api-key bax-...            # persist key in .bella config
bella run -- dotnet run                  # same, inject secrets as env vars
```

See the [CLI README](../../../../apps/cli-dotnet/README.md#authentication--billing) for details.

---

## Quick decision guide

| I want to... | Use |
|---|---|
| Simplest setup, any app type, no SDK dependency | [`01-dotenv-file`](01-dotenv-file/) |
| Zero files on disk, any app type, no SDK dependency | [`02-process-inject`](02-process-inject/) |
| Production-grade: hot-reload, IOptions, IConfiguration | [`03-aspnet`](03-aspnet/) |
| Using .NET Aspire orchestration | [`04-aspire`](04-aspire/) |

---

## Samples

### [01 — `.env` file](01-dotenv-file/)
```bash
bella secrets get -o .env && dotnet run
```
- `DotNetEnv` loads `.env` → `System.Environment`
- `IConfiguration` reads env vars automatically
- Zero runtime SDK dependency

### [02 — Process inject](02-process-inject/)
```bash
bella run -- dotnet run
```
- `bella run` spawns your app with secrets as real environment variables
- No files written to disk
- Zero runtime SDK dependency

### [03 — ASP.NET Core `AddBellaSecrets()`](03-aspnet/)
```bash
bella exec -- dotnet run
```
```csharp
builder.Configuration.AddBellaSecrets();
```
- Bella is a proper `IConfigurationSource` — works with `IOptions<T>`, `@Value`, `IConfiguration`
- Polls Baxter every 60 s → hot-reload without restart (via `IChangeToken`)
- `IOptionsMonitor<T>` sees changes automatically
- Requires `BellaBaxter.AspNet.Configuration` NuGet package

### [04 — .NET Aspire](04-aspire/)
```csharp
// AppHost
var bella = builder.AddBellaBaxter("bella", environmentSlug: "development");
api.WithBellaSecrets(bella);

// Api service (same as sample 03)
builder.Configuration.AddBellaSecrets();
```
- Baxter is a named Aspire resource — visible in the dashboard
- `WithBellaSecrets()` injects connection env vars into each service
- Requires `BellaBaxter.Aspire.Configuration` NuGet package

---

## Framework startup hook equivalents

| Framework | Hook | `AddBellaSecrets()` runs... |
|---|---|---|
| ASP.NET Core Minimal API | `builder.Configuration` | Before any services are built |
| ASP.NET Core MVC/Razor | Same — `WebApplicationBuilder` | Before any services are built |
| Worker Service | `HostApplicationBuilder` | Before any services |
| Console app | `HostBuilder.ConfigureAppConfiguration()` | Before `BuildServiceProvider()` |
| Aspire service | `WebApplicationBuilder` (via injected env vars) | Same as ASP.NET Core |

---

## Typed secrets (compile-time safe)

Sample 03 (ASP.NET Core) uses `BellaAppSecrets` — a **source-generated class** that gives you typed, IDE-friendly access:

```csharp
// Injected via DI — fully typed, no magic strings
app.MapGet("/", (BellaAppSecrets s) => new {
    s.DatabaseUrl,        // string
    s.Port,               // int
    s.EnableFeatures,     // bool
    s.AppId,              // Guid
    s.ConnectionStrings.Postgres   // string (ConnectionStrings__Postgres)
});
```

### How `BellaAppSecrets` is generated

`bella-secrets.manifest.json` (committed to the repo) lists all secret keys + types. At build time:

1. **MSBuild task** (`BellaSecretsManifestTask`) fetches key metadata from Bella API → writes `bella-secrets.manifest.json`
2. **Roslyn source generator** reads `bella-secrets.manifest.json` → emits `BellaAppSecrets.g.cs`

```
dotnet build
  ├── BeforeBuild: BellaSecretsManifestTask
  │     → GET /api/v1/projects/{slug}/environments/{slug}/secrets?keys-only=true
  │     → writes bella-secrets.manifest.json
  └── Compile: BellaSecretsSourceGenerator
        → reads bella-secrets.manifest.json (AdditionalFiles)
        → emits BellaAppSecrets.g.cs
```

To refresh the manifest (e.g. after adding a new secret):
```bash
BELLA_BAXTER_URL=http://localhost:5522 BELLA_API_KEY=bax-... dotnet build /p:BellaSkipFetch=false
git add bella-secrets.manifest.json
git commit -m "chore: update bella secrets manifest"
```

CI builds use the committed manifest with `BellaSkipFetch=true` (set in `.csproj`) — no credentials needed.

Full source generator documentation: [`BellaBaxter.SourceGenerator/README.md`](../../BellaBaxter.SourceGenerator/README.md)

---

## Key: `__` double underscore = section separator

.NET's `IConfiguration` maps `__` to `:`:

| Bella secret key | IConfiguration path |
|---|---|
| `DATABASE_URL` | `config["DATABASE_URL"]` |
| `ConnectionStrings__Default` | `config["ConnectionStrings:Default"]` |
| `Jwt__Secret` | `config["Jwt:Secret"]` |
| `Smtp__Password` | `config["Smtp:Password"]` |

This means you can use `builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"))` and all `Jwt__*` secrets from Bella map directly to the options class.


Samples showing how to integrate Bella Baxter secrets into .NET applications.

