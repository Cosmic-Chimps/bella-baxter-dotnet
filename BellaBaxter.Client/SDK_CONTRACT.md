# Bella Baxter SDK Wire Contract

> **⚠️ STOP — Read this before changing anything in this file's scope.**
>
> The contracts documented here are implemented in **9 language SDKs**.  
> A silent change here breaks every SDK simultaneously and for every customer using them.

---

## What Is a Wire Contract?

A wire contract is a set of API behaviors that SDK code depends on **at the byte level** — 
field names, types, algorithm constants, header names. Unlike internal APIs, these cannot 
be changed with a simple refactor. Every SDK must be updated, released, and customers must 
upgrade before the old behavior can be removed.

---

## Contracted SDKs

| SDK | Location |
|-----|----------|
| Go | `apps/sdk/go/` |
| TypeScript/JS | `apps/sdk/js/` |
| Dart (+ Flutter) | `apps/sdk/dart/` |
| Java | `apps/sdk/java/` |
| PHP | `apps/sdk/php/` |
| Python | `apps/sdk/python/` |
| Ruby | `apps/sdk/ruby/` |
| .NET (C#) | `apps/sdk/dotnet/BellaBaxter.Client/` |
| Swift (iOS/macOS) | `apps/sdk/swift/` |

---

## Contract 1: `getAllEnvironmentSecrets` Endpoint

### Route
```
GET /api/v1/projects/{projectRef}/environments/{envSlug}/secrets
```

### `operationId`
```
getAllEnvironmentSecrets
```

> **Why `operationId` matters:** Several SDKs (Swift, .NET) match by `operationId` 
> to decide whether to apply E2EE decryption. Renaming it disables E2EE silently.

### Response Schema — `AllEnvironmentSecretsResponse`

```json
{
  "environmentSlug": "dev",
  "environmentName": "Development",
  "secrets": {
    "DATABASE_URL": "postgres://...",
    "API_KEY": "abc123"
  },
  "version": 42,
  "lastModified": "2026-01-15T10:30:00Z"
}
```

| Field | Type | Notes |
|-------|------|-------|
| `environmentSlug` | `string` | **FROZEN** |
| `environmentName` | `string` | **FROZEN** |
| `secrets` | `object { [key: string]: string }` | **FROZEN** — flat dict, NOT an array |
| `version` | `int64` | **FROZEN** — used for polling / cache invalidation |
| `lastModified` | `string (ISO 8601 date-time)` | **FROZEN** |

#### Rules
- `secrets` is a **flat key→value object**, never an array of `{key, value}` items.
- Field names are **camelCase** (serialized with `JsonSerializerOptions.Web`).
- All fields are **required** — SDKs do not guard for missing fields.

---

## Contract 2: E2EE Wire Format

End-to-end encryption is opt-in per request via the `X-E2E-Public-Key` header.

### Trigger Header
```
X-E2E-Public-Key: <base64-encoded SPKI DER public key>
```

- Header name: `X-E2E-Public-Key` (exact casing — some HTTP stacks are case-sensitive)
- Value: Base64-strict (no line breaks) encoded **X.509 SubjectPublicKeyInfo DER** for a P-256 key

### Encrypted Response Shape

When the header is present, the server responds with:

```json
{
  "encrypted": true,
  "algorithm": "ECDH-P256-HKDF-SHA256-AES256GCM",
  "serverPublicKey": "<base64 SPKI DER>",
  "nonce": "<base64 12 bytes>",
  "tag": "<base64 16 bytes>",
  "ciphertext": "<base64 N bytes>"
}
```

| Field | Type | Notes |
|-------|------|-------|
| `encrypted` | `bool` | Always `true` when encrypted. **FROZEN** |
| `algorithm` | `string` | Informational only. Not parsed by SDKs. |
| `serverPublicKey` | `string` | Base64 SPKI DER of server's ephemeral P-256 key. **FROZEN** |
| `nonce` | `string` | Base64, 12 bytes. **FROZEN** |
| `tag` | `string` | Base64, 16 bytes, separate from ciphertext. **FROZEN** |
| `ciphertext` | `string` | Base64, AES-256-GCM ciphertext. **FROZEN** |

### Crypto Algorithm (ALL values FROZEN)

```
1. Client → Server:  X-E2E-Public-Key: base64(SPKI-DER of client P-256 public key)
2. Server generates: ephemeral P-256 key pair per request
3. ECDH:             sharedSecret = ECDH(serverPrivate, clientPublic)
                     → x-coordinate of the resulting EC point (raw bytes)
4. HKDF-SHA-256:     key = HKDF(
                         IKM  = sharedSecret,
                         salt = 0x00 × 32,          ← 32 zero bytes
                         info = "bella-e2ee-v1",    ← UTF-8, no null terminator
                         L    = 32                  ← 32-byte output
                     )
5. AES-256-GCM:      ciphertext, tag = AES-GCM-Encrypt(
                         key   = key (32 bytes),
                         nonce = random 12 bytes,
                         AAD   = "" (empty),
                         plaintext = JSON of AllEnvironmentSecretsResponse
                     )
```

### What Is Encrypted

The **entire `AllEnvironmentSecretsResponse` JSON** is encrypted — not just the secrets dict.

```
plaintext = JSON.serialize(AllEnvironmentSecretsResponse, camelCase)
         = '{"environmentSlug":"dev","environmentName":"Dev","secrets":{...},"version":42,"lastModified":"..."}'
```

> **Why this matters:** SDKs that decrypt must parse the full response object, not just 
> extract a `secrets` sub-key. All 9 SDKs were updated to handle this in March 2026.

### Which Endpoints Support E2EE

Every `GET` that carries secret VALUES encrypts its whole `2xx` body to a presented key. These are the
**envelope-required reads** (paths relative to the API root; `{…}` is one path segment):

| Path | `operationId` | Plaintext the server encrypts |
|------|---------------|-------------------------------|
| `/api/v1/projects/{p}/environments/{e}/secrets` | `getAllEnvironmentSecrets` | full `AllEnvironmentSecretsResponse` |
| `/api/v1/projects/{p}/environments/{e}/secrets/export` | `exportEnvironmentSecrets` | `{key: value}` dict (a dotenv/JSON **file** only when NO key is presented) |
| `/api/v1/projects/{p}/environments/{e}/providers/{v}/secrets` | `listSecrets` | array of secret items |
| `/api/v1/projects/{p}/environments/{e}/providers/{v}/secrets/export` | `exportSecrets` | `{key: value}` dict |
| `/api/v1/projects/{p}/environments/{e}/providers/{v}/secrets/{key}` (`{key}` ≠ `hash`, `export`) | `getSecret` | one secret item |
| `/api/v1/projects/{p}/environments/{e}/providers/{v}/secrets/{key}/versions/{n}` (`{n}` digits) | `getSecretVersion` | one secret version |
| `/api/v1/projects/{p}/secrets` | `listGlobalSecrets` | `ListGlobalSecretsResponse` |

Everything else under `/secrets` (`…/secrets/version`, `…/secrets/manifest`, `…/secrets/certificates`,
`…/secrets/hash`, `…/secrets/{key}/metadata`, `…/secrets/{key}/versions`, `…/secrets/{key}/rotation-policy`,
`…/secrets/import/preview`, and every `POST`/`PUT`/`PATCH`/`DELETE`) carries no value and is answered in plain
JSON even when the key is presented. An SDK MUST NOT require an envelope there.

### Rule: a presented key requires an envelope (#1050) — FROZEN

Once an SDK has sent `X-E2E-Public-Key` on an envelope-required read, a `2xx` answer that is not a
decryptable envelope is an **error**, never a value. There is no plaintext fallback and no opt-out: an SDK
that does not want to require an envelope must not present the key.

| What came back (`2xx`, envelope-required read, key presented) | Error code |
|---|---|
| Not JSON, not a JSON object, or a JSON object without `"encrypted": true` (plain secrets) | `e2ee-plaintext-response` |
| `"encrypted": true` but a field is missing/undecodable, the GCM tag fails (tampered), or it was encrypted to another key | `e2ee-decryption-failed` |

- One error type per SDK, carrying the code: `E2EEResponseError` (Go, JS/TS, Python, Ruby, Dart, Swift),
  `E2EEResponseException` (.NET, Java, PHP). The code strings above are the stable, cross-SDK contract.
- The message is `E2EE response expected but plaintext received for <path>; refusing it (e2ee-plaintext-response)`
  or `E2EE response could not be decrypted for <path>; refusing it (e2ee-decryption-failed)` (an SDK may add a
  short parenthetical reason after `<path>`). It never contains the body, ciphertext or key material.
- Non-`2xx` answers (problem details, `403` from the device gate) are not envelopes and are surfaced as the
  SDK surfaces any API error.
- **Why:** a header-stripping intermediary, a terminating proxy, or a server regression of the #636 class
  would otherwise hand the caller unencrypted secrets that it believes were end-to-end encrypted, and a
  tampered or mis-keyed envelope would be read as ciphertext-shaped "values". The protection is aimed at
  those; an ACTIVE adversary holding the TLS session can still re-encrypt to the presented key (the server's
  ephemeral key is unauthenticated; see #1050).
- **Server side:** every envelope-required read encrypts whenever the header is present (since
  `6bd551e39`, 2026-03-03, before any SDK release), on every `2xx` branch: `listGlobalSecrets` on a project with
  no global provider answered an EMPTY plain list until #1050 routed that branch through the same encryption.
  `EnvelopeRequiredReadsMatchTheSdkContractTests` (BellaBaxter.Tests) fails if the set of `GET`s declaring
  `E2EEncryptedPayload` stops being exactly the table above, and the `…_WithPresentedKey_IsAnEnvelope…` tests
  hold the empty / nothing-configured branches to the rule.
- **Proved by** `apps/sdk/contract-tests/run.sh`: after the key contract, each SDK is run against the stub's
  `plaintext-despite-presented-key`, `tampered-envelope` and `wrong-key-envelope` scenarios and must refuse
  each with the code above. Each SDK also has a unit test of the same four cases.

---

## Change Rules

### ✅ Backward-Compatible (safe to do)
- Adding **new optional fields** to `AllEnvironmentSecretsResponse`
- Adding new endpoints that don't affect existing ones
- Adding new query parameters with defaults

### ❌ Breaking Changes (requires updating ALL 9 SDKs + coordinated release)
- Renaming any field in `AllEnvironmentSecretsResponse`
- Changing `secrets` from a flat dict to an array or nested object
- Changing the `operationId` from `getAllEnvironmentSecrets`
- Changing the route path
- Changing any E2EE crypto constant (curve, KDF params, cipher, HKDF info string)
- Changing the header name `X-E2E-Public-Key`
- Changing any field name in the encrypted payload (`serverPublicKey`, `nonce`, `tag`, `ciphertext`)
- Changing what is encrypted (e.g. encrypting only the secrets dict instead of the full response)

### Process for Breaking Changes
1. Open a discussion issue tagged `sdk-contract-break`
2. Implement changes in server **behind a feature flag or new API version**
3. Update all 9 SDKs
4. Release SDKs
5. Announce migration period
6. Remove old behavior

---

## Server-Side References

| File | Why It's Here |
|------|---------------|
| `BellaBaxter.Crypto/EciesAlgorithm.cs` | The one true implementation of the crypto algorithm |
| `BellaBaxter.Api/Features/…/Secrets/GetAllEnvironmentSecrets.cs` | The contracted endpoint + `AllEnvironmentSecretsResponse` record |
| `BellaBaxter.Api/Infrastructure/Security/E2E/` | E2EE service wiring |
| `BellaBaxter.WebApp/openapi.json` | OpenAPI spec — `x-sdk-contract: frozen` marks contracted operations/schemas |

---

## SDK-Side References

Each SDK has its own E2EE implementation. The key file per SDK:

| SDK | E2EE file | Middleware/Interceptor |
|-----|-----------|----------------------|
| Go | `bellabaxter/client.go` | Kiota `ClientMiddleware` |
| TypeScript | `src/e2ee.ts` | `E2EInterceptor` |
| Dart | `lib/src/e2ee.dart` | `BellaE2eeInterceptor` (Dio) |
| Java | `src/…/E2EEncryption.java` | `E2EEncryptionInterceptor` (OkHttp) |
| PHP | `src/E2EEncryption.php` | `E2EGuzzleMiddleware` |
| Python | `src/bella_baxter/e2ee.py` | `E2EETransport` (httpx) |
| Ruby | `lib/bella_baxter/e2ee.rb` | `E2EEFaradayMiddleware` |
| .NET | `src/E2EEncryptionHandler.cs` | `DelegatingHandler` |
| Swift | `Sources/…/E2EEncryptionMiddleware.swift` | `ClientMiddleware` |

---

*Last updated: October 2026 — #1050: all 9 SDKs refuse a plaintext, tampered or wrong-key answer once they have presented their key.*
