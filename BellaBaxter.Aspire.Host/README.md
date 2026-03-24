# BellaBaxter.Aspire.Host

Aspire hosting integration that spins up the **full Bella Baxter infrastructure stack** inside a local .NET Aspire AppHost:
Postgres, Redis, Keycloak, OpenBao, and the Bella Baxter API — all as named Aspire resources.

Use this package when you want to run Bella locally without a deployed instance (local dev, integration tests).
For connecting to an already-deployed Bella instance, use [`BellaBaxter.Aspire.Configuration`](../BellaBaxter.Aspire.Configuration/) instead.

## Installation

```bash
# AppHost project only
dotnet add package BellaBaxter.Aspire.Host
```

## Quickstart

### Minimal — Bella owns all its infrastructure

```csharp
// AppHost Program.cs
var bella = builder.AddBellaBaxter("bella");

builder.AddProject<Projects.MyApi>("api")
       .WithBellaSecrets(bella);
```

### Bring your own Postgres + Redis

Share infrastructure with your own app to avoid running duplicate containers:

```csharp
// AppHost Program.cs
var postgres = builder.AddPostgres("postgres")
                      .WithPgAdmin();

var redis = builder.AddRedis("redis")
                   .WithRedisCommander();

var bella = builder.AddBellaBaxter("bella",
    postgres: postgres,
    redis: redis);

builder.AddProject<Projects.MyApi>("api")
       .WithBellaSecrets(bella);
```

### What `WithBellaSecrets` injects

`WithBellaSecrets(bella)` injects environment variables into the target project so that
`builder.Configuration.AddBellaSecrets()` can connect automatically — no manual configuration needed:

| Variable | Value |
|----------|-------|
| `BellaBaxter__BaxterUrl` | Resolved endpoint of the Bella API Aspire resource |
| `BellaBaxter__EnvironmentSlug` | The environment slug passed to `AddBellaBaxter` |
| `BellaBaxter__ApiKey` | From the Aspire secret parameter |

### Target project (the API service)

```csharp
// Api/Program.cs
var builder = WebApplication.CreateBuilder(args);

// AddBellaSecrets reads from BellaBaxter__* env vars injected by WithBellaSecrets()
builder.Configuration.AddBellaSecrets();
```

## When to use this vs `BellaBaxter.Aspire.Configuration`

| Package | Use when |
|---------|----------|
| **`BellaBaxter.Aspire.Host`** (this) | You want the full Bella stack running **locally** — no deployed Bella instance needed |
| [`BellaBaxter.Aspire.Configuration`](../BellaBaxter.Aspire.Configuration/) | You have a **deployed** Bella instance and just need Aspire to wire the connection URL into your services |

## Resources added to the Aspire dashboard

When `AddBellaBaxter` is called, the following resources appear in the Aspire dashboard:

| Resource | Type | Purpose |
|----------|------|---------|
| `bella-postgres` | Container (Postgres) | Bella's database (or your shared Postgres) |
| `bella-redis` | Container (Redis) | Session cache + secrets hot-reload cache |
| `bella-keycloak` | Container (Keycloak) | Identity provider for Bella UI and API |
| `bella-openbao` | Container (OpenBao) | Default secrets backend |
| `bella-api` | Container (Bella API) | The Bella Baxter REST API |
