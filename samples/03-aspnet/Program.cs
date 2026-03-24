using BellaBaxter.AspNet.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ── 1. Bella secrets → IConfiguration ────────────────────────────────────
// AddBellaSecrets() fetches all secrets from Baxter and injects them into
// IConfiguration as a live, polling source (hot-reload every 60 s by default).
//
// Connection details are resolved in priority order:
//   1. configure callback (highest)
//   2. "BellaBaxter" section in appsettings.json / appsettings.{env}.json
//      → BellaBaxter:BaxterUrl, BellaBaxter:EnvironmentSlug, BellaBaxter:ApiKey
//   3. Standard bella exec env vars (fallback):
//      → BELLA_BAXTER_URL, BELLA_BAXTER_API_KEY
//
// Recommended for local dev: bella exec -- dotnet run
// BaxterUrl + EnvironmentSlug are in appsettings.Development.json.
builder.Configuration.AddBellaSecrets();

// ── 2. Typed secrets → DI ────────────────────────────────────────────────
// AddBellaTypedSecrets<T>() registers the source-generated BellaAppSecrets
// class as a singleton, reading from IConfiguration (not System.Environment).
// Inject it directly into endpoints, controllers, or services.
builder.Services.AddBellaTypedSecrets<BellaAppSecrets>();

var app = builder.Build();

// Root endpoint — returns all 8 secrets as strings via IConfiguration
app.MapGet(
    "/",
    (IConfiguration config) =>
        Results.Json(
            new Dictionary<string, string?>
            {
                ["PORT"]                       = config["PORT"],
                ["DATABASE_URL"]               = config["DATABASE_URL"],
                ["EXTERNAL_API_KEY"]           = config["EXTERNAL_API_KEY"],
                ["GLEAP_API_KEY"]              = config["GLEAP_API_KEY"],
                ["ENABLE_FEATURES"]            = config["ENABLE_FEATURES"],
                ["APP_ID"]                     = config["APP_ID"],
                ["ConnectionStrings__Postgres"] = config["ConnectionStrings:Postgres"],
                ["APP_CONFIG"]                 = config["APP_CONFIG"],
            }
        )
);

// Health check
app.MapGet("/health", () => Results.Ok(new { ok = true }));

// Typed endpoint — BellaAppSecrets is injected by DI (reads from IConfiguration).
// This is idiomatic ASP.NET Core: no manual `new`, no static env var calls.
app.MapGet(
    "/typed",
    (BellaAppSecrets s) =>
        Results.Ok(
            new
            {
                Port = s.Port,
                DatabaseUrl = s.DatabaseUrl,
                ExternalApiKey = s.ExternalApiKey,
                GleapApiKey = s.GleapApiKey,
                EnableFeatures = s.EnableFeatures,
                AppId = s.AppId,
                ConnectionstringsPostgres = s.ConnectionStrings.Postgres,
                AppConfig = s.AppConfig,
            }
        )
);

app.Run();
