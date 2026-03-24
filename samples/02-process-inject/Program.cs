// Sample app — secrets are injected as real environment variables by `bella run`.
//
// Workflow:
//   bella run -p my-project -e production -- dotnet run
//
// BellaAppSecrets is generated at compile time from bella-secrets.manifest.json
// by the BellaBaxter.SourceGenerator — no manual BellaAppSecrets.cs needed.

var s = new BellaAppSecrets();

Console.WriteLine("=== Bella Baxter: process-inject sample (.NET) ===");
Console.WriteLine($"PORT={s.Port}");
Console.WriteLine($"DATABASE_URL={s.DatabaseUrl}");
Console.WriteLine($"EXTERNAL_API_KEY={s.ExternalApiKey}");
Console.WriteLine($"GLEAP_API_KEY={s.GleapApiKey}");
Console.WriteLine($"ENABLE_FEATURES={s.EnableFeatures.ToString().ToLower()}");
Console.WriteLine($"APP_ID={s.AppId}");
Console.WriteLine($"ConnectionStrings__Postgres={s.ConnectionstringsPostgres}");
Console.WriteLine($"APP_CONFIG={s.AppConfig}");
