using BellaBaxter.AspNet.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ── Bella secrets ─────────────────────────────────────────────────────────
// When running under Aspire, AppHost.WithBellaSecrets(bella) already set:
//   BellaBaxter__BaxterUrl  — resolved to the local Bella API container
//   BellaBaxter__ApiKey     — from Aspire secret parameter
// as environment variables. AddBellaSecrets() reads those automatically.
builder.Configuration.AddBellaSecrets();

// ─────────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.MapGet(
    "/",
    (IConfiguration config) =>
        Results.Ok(
            new
            {
                Status   = "ok",
                Database = Mask(config["DATABASE_URL"]),
                ApiKey   = Mask(config["EXTERNAL_API_KEY"]),
            }
        )
);

app.MapGet("/health", () => Results.Ok(new { Status = "healthy" }));

// Typed endpoint — uses compile-time-generated BellaAppSecrets from bella-secrets.manifest.json
app.MapGet(
    "/typed",
    () =>
    {
        var s = new BellaAppSecrets();
        return Results.Ok(
            new
            {
                Port        = s.Port,
                DatabaseUrl = Mask(s.DatabaseUrl),
                ApiKey      = Mask(s.ExternalApiKey),
            }
        );
    }
);

app.Run();

static string Mask(string? v) =>
    string.IsNullOrEmpty(v) ? "(not set)"
    : v.Length > 6 ? v[..4] + "***"
    : "***";
