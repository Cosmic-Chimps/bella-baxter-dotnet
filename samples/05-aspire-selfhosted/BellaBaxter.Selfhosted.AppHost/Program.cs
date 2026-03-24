using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// ── Option A: Bella owns all its infrastructure (simplest) ────────────────
//
// Bella Baxter starts its own PostgreSQL, Redis, Keycloak, and OpenBao.
// Use this when Bella Baxter is the only service in your AppHost,
// or when you want complete isolation.
//
// Required user-secret:
//   dotnet user-secrets set "Parameters:bella-openbao-token" "dev-only-token"
//   dotnet user-secrets set "Parameters:bella-api-key"       "bax-..."

var bella = builder.AddBellaBaxter("bella");

// ── Option B: Bring your own Postgres + Redis ──────────────────────────────
//
// Uncomment this block (and comment Option A above) if your application
// already declares Postgres and/or Redis resources and wants Bella to
// share them instead of provisioning its own.
//
// var postgres = builder.AddPostgres("postgres")
//     .WithImage("postgres", "16-alpine")
//     .WithDataVolume("app-postgres-data");
//
// var redis = builder.AddRedis("redis")
//     .WithDataVolume("app-redis-data");
//
// var bella = builder.AddBellaBaxter("bella",
//     postgres: postgres,
//     redis: redis);
//
// Your app can then also reference the same Postgres / Redis:
// builder.AddProject<Projects.MyOtherApi>("other-api")
//     .WithReference(postgres)
//     .WithReference(redis);

// ── Wire your service to Bella ────────────────────────────────────────────
//
// WithBellaSecrets() injects:
//   BellaBaxter__BaxterUrl  → Bella API URL (resolved from Aspire endpoint)
//   BellaBaxter__ApiKey     → bax-... (from secret parameter, masked in dashboard)
//
// The API key encodes the target project+environment — no env slug needed.
//
// Required user-secret:
//   dotnet user-secrets set "Parameters:bella-api-key" "bax-..."

builder
    .AddProject<Projects.BellaBaxter_Selfhosted_Api>("api")
    .WithBellaSecrets(bella);

builder.Build().Run();
