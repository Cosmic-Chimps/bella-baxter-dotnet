# Sample 03: ASP.NET Core — `AddBellaSecrets()`

**Pattern:** Bella is an `IConfigurationSource` — secrets become first-class `IConfiguration` values before any services are built.

This is the **recommended production pattern** for ASP.NET Core apps.

---

## How it works

```
bella exec -- dotnet run
    │
    ├─ injects BELLA_BAXTER_API_KEY + BELLA_BAXTER_URL into the process
    │
    └─ AddBellaSecrets()
           ├─ reads BaxterUrl + EnvironmentSlug from appsettings.json
           ├─ reads ApiKey from BELLA_BAXTER_API_KEY (bella exec) or BellaBaxter__ApiKey
           ├─ fetches all secrets from Baxter at startup
           ├─ injects secrets into IConfiguration
           └─ polls Baxter every 60 s → hot-reloads via IChangeToken
```

**Configuration priority (lowest → highest):**
```
appsettings.json  →  appsettings.{env}.json  →  Bella secrets  →  Environment variables
```

Environment variables always win, so you can override any secret locally.

---

## Setup

```bash
dotnet restore
```

**With OAuth (local dev, not billed):**
```bash
bella login       # opens browser, writes .bella file (project + env)
# EnvironmentSlug must be in appsettings.Development.json — set it once:
# { "BellaBaxter": { "EnvironmentSlug": "dev" } }
dotnet run
```

**With API key — recommended for local dev too:**
```bash
# bella exec injects BELLA_BAXTER_API_KEY and BELLA_BAXTER_URL automatically.
# BaxterUrl and EnvironmentSlug are already in appsettings.Development.json.
bella exec -- dotnet run
```

**CI/CD / production:**
```bash
export BELLA_BAXTER_API_KEY=bax-...
export BellaBaxter__BaxterUrl=https://baxter.example.com
export BellaBaxter__EnvironmentSlug=production
dotnet run
```

**CI/CD / production with ZKE (Zero-Knowledge Encryption):**

ZKE uses a persistent device key so the server can wrap the encryption key (DEK) for your process
identity. The server never sees your DEK in plaintext; transport is bound to your specific key.

```bash
# 1. Generate a device keypair once (stores private key in ~/.bella/device-key.pem)
bella auth setup

# 2. Supply the private key to the SDK via env var
export BELLA_BAXTER_API_KEY=bax-...
export BELLA_BAXTER_PRIVATE_KEY="$(cat ~/.bella/device-key.pem)"
dotnet run
```

Or bind in appsettings (use environment variables / secrets manager in production — never commit):
```json
{
  "BellaBaxter": {
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\n..."
  }
}
```

---

## Program.cs at a glance

```csharp
builder.Configuration.AddBellaSecrets();                  // 1. secrets → IConfiguration
builder.Services.AddBellaTypedSecrets<BellaAppSecrets>(); // 2. typed class → DI

// Inject BellaAppSecrets directly — idiomatic ASP.NET Core DI, no `new`
app.MapGet("/secrets", (BellaAppSecrets s) => Results.Ok(new
{
    Port        = s.Port,          // int
    DatabaseUrl = s.DatabaseUrl,   // string
    // ...
}));
```

`AddBellaTypedSecrets<T>()` registers the source-generated class as a singleton that reads from
`IConfiguration` (where `AddBellaSecrets()` loads secrets), not from `System.Environment`.
Inject it into any endpoint, controller, or service class.

---

## Configuration reference

**`appsettings.json`** (safe to commit — no secrets):
```json
{
  "BellaBaxter": {
    "BaxterUrl": "https://baxter.example.com",
    "EnvironmentSlug": "production",
    "PollingInterval": "00:01:00",
    "FallbackOnError": true
  }
}
```

**`appsettings.Development.json`** (safe to commit — local overrides):
```json
{
  "BellaBaxter": {
    "BaxterUrl": "http://localhost:5522",
    "EnvironmentSlug": "dev"
  }
}
```

**API key — never in appsettings:**

| Method | Variable |
|--------|---------|
| `bella exec -- dotnet run` | `BELLA_BAXTER_API_KEY` (injected automatically) |
| .NET User Secrets | `dotnet user-secrets set "BellaBaxter:ApiKey" "bax-..."` |
| Environment variable | `BellaBaxter__ApiKey=bax-...` |

---

## Using secrets in your code

Once `AddBellaSecrets()` is called, every secret is just `IConfiguration`:

```csharp
// 1. Raw value
var connStr = builder.Configuration["DATABASE_URL"];

// 2. Strongly-typed options (recommended)
builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));
// → reads Stripe__SecretKey, Stripe__PublishableKey from Bella

// 3. IOptions<T> injection
app.MapGet("/stripe", (IOptions<StripeOptions> opts) =>
    Results.Ok(new { PublishableKey = opts.Value.PublishableKey }));
```

---

## Nested secret keys

.NET `IConfiguration` maps `__` (double underscore) as a section separator:

| Secret key in Bella | IConfiguration path |
|-|-|
| `DATABASE_URL` | `config["DATABASE_URL"]` |
| `ConnectionStrings__Default` | `config["ConnectionStrings:Default"]` |
| `Jwt__Secret` | `config["Jwt:Secret"]` |
| `Smtp__Password` | `config["Smtp:Password"]` |

---

## Hot-reload without restart

The polling provider implements `IChangeToken`. Any service using `IOptionsMonitor<T>` or
`IOptionsSnapshot<T>` will automatically pick up changed secrets on the next poll:

```csharp
// Always reads latest value (hot-reloaded every 60 s)
public class ApiService(IOptionsMonitor<ApiOptions> opts)
{
    public string ApiKey => opts.CurrentValue.Key;
}
```

---

## Comparison

| Pattern | Hot-reload | No SDK dep | Works with IOptions | Secrets on disk |
|-|-|-|-|-|
| `AddBellaSecrets()` ✅ | ✅ Polling | ❌ NuGet pkg | ✅ Full IConfiguration | ❌ Never |
| `.env` file | ❌ | ✅ | ❌ Manual | ⚠️ File |
| `bella run --` | ❌ | ✅ | ✅ Via env vars | ❌ Never |


**Pattern:** Bella is an `IConfigurationSource` — secrets become first-class `IConfiguration` values before any services are built.

This is the **recommended production pattern** for ASP.NET Core apps.

---

## How it works

```
AddBellaSecrets()
  │
  ├─ builder.Build() → reads "BellaBaxter" section from existing config
  ├─ Fetches all secrets from Baxter API at startup
  ├─ Injects secrets into IConfiguration (same layer as appsettings.json)
  └─ Polls Baxter every 60 s → hot-reloads changed secrets via IChangeToken
```

**Priority (lowest → highest):**
```
appsettings.json  →  appsettings.{env}.json  →  Bella secrets  →  Environment variables
```

Environment variables always win, so you can override any secret locally.

