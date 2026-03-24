# Sample 04: .NET Aspire Integration

**Pattern:** AppHost declares the Bella Baxter connection via `AddBaxter()`. The service project receives the URL and API key as environment variables and calls `AddBellaSecrets()` at startup.

This is the **recommended pattern for Aspire-based apps** — secrets are part of the service graph, visible in the Aspire dashboard.

---

## How it works

```
AppHost/Program.cs
  builder.AddBaxter("baxter", baxterUrl: "https://baxter.example.com")
  api.WithReference(baxter)
       │
       │  Injects into api project:
       │    BellaBaxter__BaxterUrl  → https://baxter.example.com
       │    BellaBaxter__ApiKey     → <from Aspire secret parameter, masked>
       ▼
Api/Program.cs
  builder.Configuration.AddBellaSecrets()   ← reads env vars above automatically
  IConfiguration["DATABASE_URL"]            ← fetched from Baxter
```

The API key already encodes which project+environment it targets — there is no separate `environmentSlug`. Project and environment are resolved automatically from the key via `/api/v1/keys/me`.

---

## Setup

### 1. Add API key as Aspire secret parameter

```bash
cd BellaBaxter.Sample.AppHost

dotnet user-secrets set "Parameters:bella-api-key" "bax-..."
```

### 2. Run with Aspire

```bash
cd BellaBaxter.Sample.AppHost
dotnet run
```

The Aspire dashboard opens automatically. You'll see the `BellaBaxter__*` environment variables injected into the API service. `BellaBaxter__ApiKey` is masked.

---

## AppHost code

```csharp
// AppHost/Program.cs
var baxter = builder.AddBaxter(
    name: "baxter",
    baxterUrl: "https://baxter.example.com");

builder
    .AddProject<Projects.BellaBaxterSampleApi>("api")
    .WithReference(baxter)
    .WaitFor(baxter);
```

## Api service code

```csharp
// Api/Program.cs — no hardcoded connection details needed
// AddBellaSecrets() reads BellaBaxter__BaxterUrl and BellaBaxter__ApiKey
// injected by WithReference(baxter) in the AppHost.
builder.Configuration.AddBellaSecrets();

// Secrets are now in IConfiguration
var connStr = builder.Configuration["DATABASE_URL"];
```

---

## Multiple environments

Each environment gets its own API key (scoped at creation time to a project+environment). Declare multiple Baxter resources pointing to the same Bella Baxter instance:

```csharp
var baxterProd    = builder.AddBaxter("baxter-prod",    baxterUrl: "https://baxter.example.com",
                                      apiKeyParameterName: "bella-api-key-prod");
var baxterStaging = builder.AddBaxter("baxter-staging", baxterUrl: "https://baxter.example.com",
                                      apiKeyParameterName: "bella-api-key-staging");

builder.AddProject<Projects.ApiProd>("api-prod")
    .WithReference(baxterProd);

builder.AddProject<Projects.ApiStaging>("api-staging")
    .WithReference(baxterStaging);
```

Store each key separately:
```bash
dotnet user-secrets set "Parameters:bella-api-key-prod"    "bax-..."
dotnet user-secrets set "Parameters:bella-api-key-staging" "bax-..."
```

## Multi-project monorepo

```csharp
var baxter = builder.AddBaxter("baxter", baxterUrl: "https://baxter.example.com");

builder.AddProject<Projects.OrdersApi>("orders").WithReference(baxter);
builder.AddProject<Projects.PaymentsApi>("payments").WithReference(baxter);
builder.AddProject<Projects.NotificationsWorker>("notifications").WithReference(baxter);
```

---

## Dashboard visibility

In the Aspire dashboard, the `baxter` resource shows as a connected external resource.
The injected `BellaBaxter__BaxterUrl` is visible in the service's environment tab.
`BellaBaxter__ApiKey` is masked (it's an Aspire secret parameter).

---

## Secret rotation

✅ **Supported automatically.** `AddBellaSecrets()` polls Baxter every `PollingInterval` (default: 60 seconds).

- **`IOptionsMonitor<T>`** — updated on the next poll tick, no restart needed
- **`IOptionsSnapshot<T>`** — refreshed per-request scope
- **`IConfiguration["KEY"]`** — always reads the latest value

Configure polling in the Api project's `appsettings.json`:
```json
{
  "BellaBaxter": {
    "PollingInterval": "00:00:30",
    "FallbackOnError": true
  }
}
```

**`FallbackOnError: true`** (default) — if Baxter is temporarily unreachable, the app keeps the last known secrets instead of throwing. Important for production resilience.

---

## What this sample does NOT do

This sample treats Bella Baxter as an **external service** you already have running. The AppHost only declares _how to connect_ to it, not how to run it.

If you want the full Bella Baxter stack (PostgreSQL + Redis + Keycloak + OpenBao + Bella API) to start as part of your Aspire AppHost, see **sample 05-aspire-selfhosted**.

