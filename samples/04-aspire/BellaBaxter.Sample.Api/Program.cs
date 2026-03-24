using BellaBaxter.AspNet.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ── Bella secrets ─────────────────────────────────────────────────────────
// When running under Aspire, AppHost.WithReference(baxter) already set:
//   BellaBaxter__BaxterUrl, BellaBaxter__EnvironmentSlug, BellaBaxter__ApiKey
// as environment variables.  AddBellaSecrets() reads those automatically —
// no explicit callback needed.
builder.Configuration.AddBellaSecrets();

// ─────────────────────────────────────────────────────────────────────────

// Service dependencies — all values come from Bella, accessible as IConfiguration
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("ConnectionStrings"));

var app = builder.Build();

app.MapGet(
    "/",
    (IConfiguration config) =>
        Results.Ok(
            new
            {
                Status = "ok",
                Database = Mask(config["DATABASE_URL"]),
                ApiKey = Mask(config["EXTERNAL_API_KEY"]),
            }
        )
);

app.MapGet("/health", () => Results.Ok(new { Status = "healthy" }));

// Typed endpoint — uses compile-time-generated BellaAppSecrets instead of raw IConfiguration
// BellaAppSecrets is emitted by BellaBaxter.SourceGenerator from bella-secrets.manifest.json
app.MapGet(
    "/typed",
    () =>
    {
        var s = new BellaAppSecrets();
        return Results.Ok(
            new
            {
                Port = s.Port, // int  ← not string
                DatabaseUrl = Mask(s.DatabaseUrl),
                ApiKey = Mask(s.ExternalApiKey),
            }
        );
    }
);

app.Run();

static string Mask(string? v) =>
    string.IsNullOrEmpty(v) ? "(not set)"
    : v.Length > 6 ? v[..4] + "***"
    : "***";

record DatabaseOptions
{
    public string Default { get; init; } = string.Empty;
}
