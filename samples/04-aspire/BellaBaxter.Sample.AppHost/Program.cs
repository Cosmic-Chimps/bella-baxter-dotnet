using Aspire.Hosting;
using BellaBaxter.Aspire.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// ── Declare the Baxter connection ──────────────────────────────────────────
// AddBaxter() creates a named resource that holds the Baxter URL.
// The API key is read from an Aspire secret parameter
// (store via: dotnet user-secrets set "Parameters:bella-api-key" "bax-...")
//
// The API key already encodes which project+environment it targets —
// no separate environmentSlug is needed.
//
// WithReference(baxter) injects two env vars into the target project:
//   BellaBaxter__BaxterUrl  → https://baxter.example.com
//   BellaBaxter__ApiKey     → bax-...   (from secret parameter, masked in dashboard)
//
// The Api project calls builder.Configuration.AddBellaSecrets() at startup —
// IConfiguration picks up the injected env vars automatically.
var baxter = builder.AddBaxter(name: "baxter", baxterUrl: "https://baxter.example.com");

// ──────────────────────────────────────────────────────────────────────────

var api = builder
    .AddProject<Projects.BellaBaxter_Sample_Api>("api")
    .WithReference(baxter)
    .WaitFor(baxter);

builder.Build().Run();
