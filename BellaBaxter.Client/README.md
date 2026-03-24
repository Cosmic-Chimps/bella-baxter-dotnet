# BellaBaxter.Client

Auto-generated .NET HTTP client for the [Bella Baxter](https://bella-baxter.io) secrets management API.
Includes built-in end-to-end encryption via [`BellaBaxter.Crypto`](https://www.nuget.org/packages/BellaBaxter.Crypto).

## When to use BellaBaxter.Client directly

Most applications should use the higher-level packages instead:

| Goal | Use |
|------|-----|
| Pull secrets into `IConfiguration` (hot-reload) | [`BellaBaxter.AspNet.Configuration`](../BellaBaxter.AspNet.Configuration/) |
| Wire Bella into .NET Aspire orchestration | [`BellaBaxter.Aspire.Configuration`](../BellaBaxter.Aspire.Configuration/) |
| Scripts, tools, custom integrations | **BellaBaxter.Client** (this package) |
| Run the full Bella stack locally in Aspire | [`BellaBaxter.Aspire.Host`](../BellaBaxter.Aspire.Host/) |

## Installation

```bash
dotnet add package BellaBaxter.Client
```

## Quickstart

```csharp
using BellaBaxter.Client;

var client = BellaClientFactory.Create(
    apiUrl: "https://api.bella-baxter.io",
    apiKey: Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")!
);

// List secrets for an environment
var secrets = await client.Environments.GetSecretsAsync(environmentId);

foreach (var secret in secrets)
{
    Console.WriteLine($"{secret.Key} = {secret.Value}");
}
```

## Authentication

### API key (recommended for apps and CI/CD)

```bash
# Generate a key via the CLI
bella api-keys create --env production --name "MyApp Production"
# Returns: bax-<keyId>-<secret>
```

```csharp
var client = BellaClientFactory.Create(
    apiUrl: Environment.GetEnvironmentVariable("BELLA_BAXTER_URL")!,
    apiKey: Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")!
);
```

API keys encode the project and environment slug — no `.bella` file needed.
Billed on pay-as-you-go plans. Generate via `bella api-keys create` or the Bella WebApp.

### OAuth (local dev)

```bash
bella login           # opens browser, stores token in .bella file
bella exec -- dotnet run   # injects BELLA_BAXTER_API_KEY + BELLA_BAXTER_URL automatically
```

## End-to-end encryption

Secret values are encrypted client-side using **ECIES** (Elliptic Curve Integrated Encryption Scheme)
before being sent to the Baxter API. This means secret values are never transmitted or stored in plaintext —
even the Baxter server itself cannot read them.

Decryption happens transparently in `BellaBaxter.Client` using your private key. The encryption is provided
by [`BellaBaxter.Crypto`](https://www.nuget.org/packages/BellaBaxter.Crypto), which is a dependency of this package.

## API coverage

| Resource | Methods |
|----------|---------|
| Projects | List, Get, Create, Update, Delete |
| Environments | List, Get, Create, Update, Delete |
| Secrets | List, Get, Upsert, Delete, Export (.env) |
| Providers | List, Get, Create, Update, Delete |
| Users | List, Get, Invite, Remove |
| API Keys | List, Create, Revoke |

## Regenerating the client

`BellaBaxter.Client` is generated from the Bella Baxter OpenAPI spec using [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/).
To regenerate after an API change:

```bash
cd apps/sdk
./generate.sh
```

## Webhook signature verification

`BellaBaxter.Client` includes a `WebhookSignatureVerifier` for verifying HMAC-signed webhook payloads from Bella:

```csharp
using BellaBaxter.Client;

var verifier = new WebhookSignatureVerifier(signingSecret: "whsec-...");

bool isValid = verifier.Verify(
    payload: requestBody,
    signatureHeader: Request.Headers["X-Bella-Signature"]
);
```
