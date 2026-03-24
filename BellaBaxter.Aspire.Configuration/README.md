# BellaBaxter.Aspire.Configuration

.NET Aspire AppHost integration for [Bella Baxter](https://bella-baxter.io).

Adds Bella Baxter as a named Aspire resource and wires the API connection into your services automatically —
no manual URL or API key configuration needed in each project.

Use this package when you have a **deployed** Bella Baxter instance.
To run the full Bella stack locally inside Aspire, use [`BellaBaxter.Aspire.Host`](../BellaBaxter.Aspire.Host/) instead.

## Installation

```bash
# AppHost project only
dotnet add package BellaBaxter.Aspire.Configuration
```

## Quickstart

**AppHost Program.cs**:
```csharp
var bella = builder.AddBellaBaxter("bella", environmentSlug: "development");

builder.AddProject<Projects.MyApi>("api")
       .WithBellaSecrets(bella);

builder.AddProject<Projects.MyWorker>("worker")
       .WithBellaSecrets(bella);
```

**Set the API key** in AppHost user secrets:
```bash
dotnet user-secrets set "Parameters:bella-api-key" "bax-..."
```

**Target project** (MyApi, MyWorker, etc.) — no extra configuration needed, connection vars are injected:
```csharp
// Api/Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddBellaSecrets(); // reads BellaBaxter__* env vars injected by WithBellaSecrets()
```

## What `WithBellaSecrets` injects

| Variable | Value |
|----------|-------|
| `BellaBaxter__BaxterUrl` | Resolved Bella API endpoint from the Aspire resource |
| `BellaBaxter__EnvironmentSlug` | The slug passed to `AddBellaBaxter(environmentSlug: ...)` |
| `BellaBaxter__ApiKey` | From the Aspire secret parameter |

`AddBellaSecrets()` in the target project reads these variables automatically — the app connects without any hardcoded config.

## When to use this vs `BellaBaxter.Aspire.Host`

| Package | Use when |
|---------|----------|
| **`BellaBaxter.Aspire.Configuration`** (this) | You have a **deployed** Bella instance — just wire the URL into your services |
| [`BellaBaxter.Aspire.Host`](../BellaBaxter.Aspire.Host/) | You want the **full Bella stack running locally** inside Aspire (no deployed instance needed) |

## Aspire dashboard

`AddBellaBaxter` adds a named resource visible in the Aspire dashboard — endpoints, logs, and health status
for the Bella API are shown alongside your own services.
