# BellaBaxter.Spiffe.Client

Zero-static-secret workload authentication for .NET services using SPIFFE/SVID.

Instead of managing static `bax-` API keys in your deployments, this package lets your service **prove its identity** through the bella agent sidecar — the same way SPIRE works.

## How It Works

```
Your .NET service
  │
  ├── BellaSpiffeClientFactory.Create(options)
  │     │
  │     ├── 1. FetchJWTSVID(audience: "bella-api")
  │     │        over the SPIFFE Workload API (gRPC, Unix socket)
  │     │        → JWT-SVID signed by OpenBao PKI
  │     │
  │     ├── 2. POST {bellaBaseUrl}/api/v1/environments/{envId}/svid/exchange
  │     │        → scoped bax- lease (tracked + revocable from WebApp)
  │     │
  │     └── 3. HMAC-sign all secret requests with the bax- lease
  │              (auto-refreshed 2 min before expiry)
  │
  └── client.Api.V1.Environments[envId].Secrets.GetAsync(...)
```

No secrets injected into pods. No env var rotation. The workload **is** the credential.

## Pre-requisites

1. Register the workload in Bella Baxter:
   ```bash
   bella spire add \
     --name billing-service \
     --selector k8s:namespace=payments \
     --selector k8s:sa=billing-sa
   ```

2. Run `bella spiffe agent` as a sidecar. It serves the **SPIFFE Workload API** on a Unix socket —
   the same interface `go-spiffe`, `java-spiffe` and `spiffe-helper` speak:
   ```yaml
   # Kubernetes sidecar
   - name: bella-spiffe-agent
     image: ghcr.io/cosmic-chimps/bella-agent:latest
     args: ["spiffe", "agent"]
     env:
       - name: BELLA_ENVIRONMENT_ID
         value: "your-environment-id"
       - name: BELLA_WORKLOAD_NAME
         value: "billing-service"
       - name: BELLA_BOOTSTRAP_TOKEN
         valueFrom:
           secretKeyRef: { name: bella-bootstrap, key: token }
       - name: SPIFFE_ENDPOINT_SOCKET
         value: /run/bella-spiffe/workload.sock
     volumeMounts:
       - { name: spiffe-socket, mountPath: /run/bella-spiffe }
   ```

   > **Not a localhost HTTP port.** A TCP port on localhost is reachable by every process on the host
   > and, in a shared network namespace, by every container in the pod. The agent's authorisation
   > boundary is the socket's filesystem permission (`0600` in a `0700` directory), because the agent
   > holds one SVID and serves it to whoever asks — unlike SPIRE, it does not attest the caller, so
   > there is no per-caller answer to fall back on.

## Quick Start

```csharp
using BellaBaxter.Spiffe.Client;

var client = BellaSpiffeClientFactory.Create(new SpiffeClientOptions
{
    BellaBaseUrl   = "https://api.bella.example.com",
    EnvironmentId  = Guid.Parse("your-environment-id"),
    // SvidAudience = "bella-api",  // default
    // The agent socket comes from SPIFFE_ENDPOINT_SOCKET, else a per-user default —
    // the SPIFFE spec's own portability mechanism, not a setting on this type.
});

// Fetch secrets — SPIFFE auth is transparent
var secrets = await client.Api.V1.Projects["payments"]
    .Environments["production"]
    .Secrets
    .GetAsync();
```

## One build, three environments (`CreateAutoDetect`)

`CreateAutoDetect` lets the same binary run in a pod beside an agent, on a laptop, and in CI without a
code change:

```csharp
var client = BellaSpiffeClientFactory.CreateAutoDetect(
    options,
    apiKeyClientFactory: key => BuildClientFromApiKey(key));
```

It picks a credential by looking for an agent socket (`SPIFFE_ENDPOINT_SOCKET`, else the per-user
default), and falls back to `BELLA_API_KEY`. Two rules make that safe rather than merely convenient:

- **The agent wins when present**, even if an API key is also set. Preferring the key would let a
  workload that was deliberately given an attested identity fall back silently to a long-lived shared
  secret — the exact thing this package exists to remove — and nothing in any log would say so.
- **Neither available is a refusal**, not an unauthenticated client. Returning one defers the failure
  to the first API call, where it arrives as a 401 that looks like a credential problem rather than the
  configuration problem it is. The exception names both options with their exact spellings.

Which credential was chosen is logged at Information, because it is invisible from outside the process
and it is exactly what an incident timeline needs.

## Custom Workload Client

For testing, or if bella agent exposes a different interface:

```csharp
var client = BellaSpiffeClientFactory.Create(
    options: new SpiffeClientOptions
    {
        BellaBaseUrl  = "https://api.bella.example.com",
        EnvironmentId = Guid.Parse("your-environment-id"),
    },
    workloadClient: new MyCustomSpiffeClient()  // ISpiffeWorkloadClient
);
```

## Dependency Injection

```csharp
// Program.cs
builder.Services.AddSingleton(sp =>
    BellaSpiffeClientFactory.Create(
        new SpiffeClientOptions
        {
            BellaBaseUrl  = builder.Configuration["Bella:BaseUrl"]!,
            EnvironmentId = Guid.Parse(builder.Configuration["Bella:EnvironmentId"]!),
        },
        loggerFactory: sp.GetRequiredService<ILoggerFactory>()
    )
);
```

## Configuration Reference

| Property | Default | Description |
|---|---|---|
| `BellaBaseUrl` | *(required)* | Bella Baxter API base URL |
| `EnvironmentId` | *(required)* | Environment whose WorkloadIdentity registration to use |
| `AgentBaseUrl` | `http://localhost:8088` | **Obsolete.** Only used by the deprecated `BellaAgentHttpClient`; no agent serves it |
| `SvidAudience` | `bella-api` | JWT-SVID audience claim |
| `ExpiryBufferDuration` | `2 minutes` | How early to refresh the bax- lease before it expires |
| `BellaClient` | `bella-dotnet-spiffe-sdk` | Sent as `X-Bella-Client` for audit logs |
| `AppClient` | `null` | Optional app name for audit logs (`X-App-Client`) |

## Compared to Static API Keys

| | Static `bax-` key | SPIFFE (this package) |
|---|---|---|
| Secret in env var | ✅ Yes | ❌ None |
| Auto-rotates | ❌ Manual | ✅ Automatic |
| Lease tracked in WebApp | ❌ | ✅ |
| Works in air-gapped pods | ✅ | ✅ (agent is local) |
| Requires bella agent | ❌ | ✅ sidecar |
