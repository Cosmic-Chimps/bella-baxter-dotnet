# BellaBaxter.Maui

Offline-capable secret cache for .NET MAUI apps. Caches secrets from
[Bella Baxter](https://bella-baxter.io) in the device's secure storage so your
app can start and run without a network connection after the first successful sync.

| Platform | Storage |
|----------|---------|
| iOS / macOS | Apple Keychain |
| Android | EncryptedSharedPreferences (AES-256 GCM) |
| Windows | Windows Credential Store (DPAPI) |

## Installation

```bash
dotnet add package BellaBaxter.Maui
dotnet add package BellaBaxter.AspNet.Configuration
```

## Usage

Wire `MauiSecureSecretCache` into `AddBellaSecrets()` in `MauiProgram.cs`:

```csharp
using BellaBaxter.AspNet.Configuration;
using BellaBaxter.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Loads secrets from Bella Baxter + caches them in SecureStorage for offline use
        builder.Configuration.AddBellaSecrets(o =>
        {
            o.BaxterUrl = "https://api.bella-baxter.io";
            o.ApiKey = "bax-...";              // from environment or secure config
            o.EnvironmentSlug = "production";  // or omit to auto-detect from API key
            o.Cache = new MauiSecureSecretCache();  // ← enables offline startup
        });

        // Optionally register a source-generated typed-secrets class
        builder.Services.AddBellaTypedSecrets<BellaAppSecrets>();

        return builder.Build();
    }
}
```

### Accessing secrets

**Option 1 — typed class** (recommended, via source generator):

```csharp
// Injected anywhere via DI
public class MyPage(BellaAppSecrets secrets) : ContentPage
{
    void OnLoad() => label.Text = secrets.DatabaseUrl;
}
```

**Option 2 — IConfiguration** (standard .NET):

```csharp
public class MyPage(IConfiguration config) : ContentPage
{
    void OnLoad() => label.Text = config["DATABASE_URL"];
}
```

## How offline mode works

1. **First launch (online):** Secrets are fetched from Bella Baxter and written to
   `SecureStorage` under a namespace prefix (`bella.default.*`).

2. **Subsequent launches (offline):** If the Bella Baxter API is unreachable and
   `FallbackOnError = true` (the default), `MauiSecureSecretCache.ReadAsync()` is
   called and secrets are served from secure storage.

3. **When connectivity returns:** The polling timer fetches fresh secrets from the API
   and updates both the in-memory configuration and the secure storage cache.

## Android: Auto Backup setup

Android's Auto Backup feature can copy `EncryptedSharedPreferences` to a new device,
but the encryption keys are not transferred — making the restored data unreadable.
`MauiSecureSecretCache` handles this gracefully by catching decryption errors and
clearing the stale cache so fresh secrets can be fetched on the next API call.

To prevent the issue entirely, exclude Bella's preference file from backups in
`Platforms/Android/AndroidManifest.xml`:

```xml
<application
    android:fullBackupContent="@xml/auto_backup_rules"
    ...>
</application>
```

Create `Platforms/Android/Resources/xml/auto_backup_rules.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<full-backup-content>
    <include domain="sharedpref" path="."/>
    <exclude domain="sharedpref" path="${applicationId}.microsoft.maui.essentials.preferences.xml"/>
</full-backup-content>
```

## Multiple environments

Use a different `storageKey` per environment to avoid collisions:

```csharp
o.Cache = new MauiSecureSecretCache("production");  // keys: bella.production.*
o.Cache = new MauiSecureSecretCache("staging");     // keys: bella.staging.*
```

## License

MIT — see [LICENSE](https://github.com/cosmic-chimps/bella-baxter-dotnet/blob/main/LICENSE).
