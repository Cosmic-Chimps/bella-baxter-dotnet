// Sample app — reads secrets written to a .env file by the Bella CLI.
//
// Workflow:
//   1. bella secrets generate dotnet -p my-project -e production   ← regenerate manifest (once)
//   2. bella secrets get -p my-project -e production -o .env       ← write runtime values
//   3. dotnet run

using DotNetEnv;

// Load .env file into System.Environment before accessing secrets
Env.TraversePath().Load();

// BellaAppSecrets is generated at compile time from bella-secrets.manifest.json
// by the BellaBaxter.SourceGenerator — no manual BellaAppSecrets.cs needed.
var s = new BellaAppSecrets();

Console.WriteLine("=== Bella Baxter: .env file sample (.NET) ===");
Console.WriteLine($"PORT={s.Port}");
Console.WriteLine($"DATABASE_URL={s.DatabaseUrl}");
Console.WriteLine($"EXTERNAL_API_KEY={s.ExternalApiKey}");
Console.WriteLine($"GLEAP_API_KEY={s.GleapApiKey}");
Console.WriteLine($"ENABLE_FEATURES={s.EnableFeatures.ToString().ToLower()}");
Console.WriteLine($"APP_ID={s.AppId}");
Console.WriteLine($"ConnectionStrings__Postgres={s.ConnectionstringsPostgres}");
Console.WriteLine($"APP_CONFIG={s.AppConfig}");
