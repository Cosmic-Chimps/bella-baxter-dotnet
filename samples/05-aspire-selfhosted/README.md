# Sample 05: .NET Aspire — Self-hosted Bella Baxter

**Pattern:** AppHost starts the **entire Bella Baxter infrastructure** (PostgreSQL, Redis, Keycloak, OpenBao, Bella API) as part of your Aspire service graph. One `dotnet run` brings everything up.

Use this when you want a **zero-config local dev environment** where Bella Baxter is an internal concern of your AppHost, not a shared external service.

> For teams with a pre-existing Bella Baxter instance, see **sample 04-aspire** instead.

---

## How it works

```
AppHost/Program.cs
  builder.AddBellaBaxter("bella")
       │
       │  Starts and wires:
       │    bella-postgres  → PostgreSQL 16 (Marten event store)
       │    bella-redis     → Redis (secret cache)
       │    bella-keycloak  → Keycloak (identity provider)
       │    bella-openbao   → OpenBao in dev mode (default vault)
       │    bella-api       → Bella API container (ghcr.io/cosmic-chimps/bella-baxter-api)
       │
  api.WithBellaSecrets(bella)
       │
       │  Injects into api project:
       │    BellaBaxter__BaxterUrl  → http://bella-api (resolved at runtime)
       │    BellaBaxter__ApiKey     → <from Aspire secret parameter, masked>
       ▼
Api/Program.cs
  builder.Configuration.AddBellaSecrets()
  IConfiguration["DATABASE_URL"]  ← fetched from Bella
```

---

## Bring your own Postgres / Redis

If your application already declares Postgres or Redis resources, you can share them with Bella Baxter instead of spinning up duplicates:

```csharp
// AppHost/Program.cs
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("app-postgres-data");

var redis = builder.AddRedis("redis")
    .WithDataVolume("app-redis-data");

// Bella uses YOUR postgres and redis
var bella = builder.AddBellaBaxter("bella",
    postgres: postgres,
    redis: redis);

// Your app also uses the same postgres
builder.AddProject<Projects.MyApi>("api")
    .WithBellaSecrets(bella)
    .WithReference(postgres);
```

Keycloak and OpenBao are always Bella-owned — they are internal concerns not suitable for sharing.

---

## Setup

### 1. Store Aspire secret parameters

```bash
cd BellaBaxter.Selfhosted.AppHost

# OpenBao dev root token (any string is fine for local dev)
dotnet user-secrets set "Parameters:bella-openbao-token" "dev-root-token"

# Bella API key — create one via the WebApp or Bella CLI after first run
dotnet user-secrets set "Parameters:bella-api-key" "bax-..."
```

### 2. Run

```bash
cd BellaBaxter.Selfhosted.AppHost
dotnet run
```

Aspire starts all containers in order, waiting for each to be healthy before starting the next. The dashboard shows all resources and the `WaitFor` dependency chain.

### 3. First-time Bella Baxter setup

After all containers are healthy, Bella Baxter needs one-time configuration (Keycloak realm, OpenBao AppRole). Run the setup script pointed at the Aspire-managed ports:

```bash
# From the Bella Baxter API repository
KEYCLOAK_URL=http://localhost:8080 \
OPENBAO_ADDR=http://localhost:8200 \
./apps/api/baxter-dotnet/scripts/setup-dev.sh
```

This is the equivalent of the 11-step manual setup, automated. You only need to run it once per new data volume.

After setup completes, restart the Aspire AppHost (user secrets are loaded at startup):
```bash
# Stop with Ctrl+C, then:
dotnet run
```

---

## Dashboard

All Bella Baxter components appear in the Aspire dashboard:

| Resource | Type | Role |
|----------|------|------|
| `bella-postgres` | PostgreSQL | Marten event store |
| `bella-redis` | Redis | Secret cache |
| `bella-keycloak` | Container | Identity provider |
| `bella-openbao` | Container | Default secret vault |
| `bella-api` | Container | Bella Baxter API |
| `api` | Project | Your application |

`BellaBaxter__ApiKey` is masked in the environment tab (Aspire secret parameter).

---

## AppHost code

```csharp
// Minimal — Bella owns everything:
var bella = builder.AddBellaBaxter("bella");

builder.AddProject<Projects.MyApi>("api")
       .WithBellaSecrets(bella);
```

```csharp
// Bring your own Postgres + Redis:
var postgres = builder.AddPostgres("postgres");
var redis    = builder.AddRedis("redis");

var bella = builder.AddBellaBaxter("bella",
    postgres: postgres,
    redis: redis);

builder.AddProject<Projects.MyApi>("api")
       .WithBellaSecrets(bella)
       .WithReference(postgres);  // your app also uses postgres directly
```

## Api service code

```csharp
// Api/Program.cs — identical to sample 04
builder.Configuration.AddBellaSecrets();

var connStr = builder.Configuration["DATABASE_URL"];
```

The consumer service code is identical whether Bella is self-hosted (this sample) or external (sample 04). The only difference is in the AppHost.

---

## Secret rotation

✅ **Supported automatically** — identical to sample 04. `AddBellaSecrets()` polls on the configured interval.

---

## Production considerations

This sample runs OpenBao in **dev mode** (in-memory, unsealed, single root token). For production:

- Use a properly initialized and unsealed OpenBao with AppRole authentication
- Store the AppRole credentials in Aspire secret parameters or a secrets manager
- Replace `WithArgs("server", "-dev")` with a production OpenBao config volume

The `setup-dev.sh` script in the API repository automates the AppRole + JWT setup for a production-like OpenBao configuration.

---

## vs sample 04-aspire

| | 04-aspire | 05-aspire-selfhosted |
|---|---|---|
| Bella Baxter location | External (pre-existing) | Started by AppHost |
| Setup complexity | Low — just URL + API key | Medium — full stack |
| Infrastructure ownership | Yours | Bella-managed (or shared) |
| Good for | Teams with shared Bella | Solo devs, CI, ephemeral envs |
| Dashboard visibility | Connection only | Full stack |
