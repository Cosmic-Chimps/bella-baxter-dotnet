# Sample 02: Process Inject — `bella run`

**Pattern:** `bella run` fetches secrets and spawns your app with them already in its environment.

Zero SDK code needed. Works with **every** .NET application type — Minimal API, MVC, Worker, console, tests.

---

## How it works

```
bella run -- dotnet run
    │
    ├─ bella fetches all secrets from Baxter
    ├─ bella spawns: dotnet run
    │   with { ...System.Environment, ...secrets }
    └─ app reads secrets via Environment.GetEnvironmentVariable() or IConfiguration
```

## Usage

**With OAuth (local dev, not billed):**
```bash
bella login          # opens browser, writes .bella file (project + env)
bella run -- dotnet run
```

**With API key (CI/CD, production):**
```bash
# API key encodes project + environment — no .bella file or -p/-e flags needed
bella run --api-key bax-... -- dotnet run

# Or after bella login --api-key:
bella run -- dotnet run
```

Other examples:
```bash
# Published binary
bella run -- ./out/MyApp

# With dotnet arguments
bella run -- dotnet run --urls http://+:8080

# EF Core migrations
bella run -- dotnet ef database update
```

## ASP.NET Core — secrets are just IConfiguration

```csharp
var builder = WebApplication.CreateBuilder(args);

// IConfiguration reads env vars by default — secrets are already there!
var connectionString = builder.Configuration["DATABASE_URL"];         // flat
var nested           = builder.Configuration["Database:Host"];         // for Database__Host env var

// Strongly-typed options also work
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
```

## .NET env var name convention

.NET's `IConfiguration` maps `__` (double underscore) to section separators:
- `Database__Host=localhost` → `IConfiguration["Database:Host"]`
- `App__Jwt__Secret=abc`    → `IConfiguration["App:Jwt:Secret"]`

## Comparison vs dotenv file

| | `bella run --` | `bella secrets get -o .env` |
|--|--|--|
| Secrets on disk | ❌ Never | ✅ Written to disk |
| Hot-reload | ❌ Process restart needed | ❌ Process restart needed |
| Zero SDK code | ✅ Yes | ✅ Yes |
| Works with all commands | ✅ Yes | ✅ Yes |
| Security | ✅ Better (no file) | ⚠️ .env file exists |


**Pattern:** `bella run` fetches secrets and spawns your app with them already in its environment.

Zero SDK code needed. Works with **every** .NET application type — Minimal API, MVC, Worker, console, tests.

---

