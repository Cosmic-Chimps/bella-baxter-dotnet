# Sample 01: `.env` File Approach (.NET)

**Pattern:** CLI writes secrets to a `.env` file → app reads it with `DotNetEnv`.

Works with any .NET application — console, ASP.NET Core, Worker Service, etc.

---

## How it works

```
bella secrets get -o .env   →   .env file on disk   →   Env.Load()   →   Environment.GetEnvironmentVariable()
```

## Setup

```bash
dotnet restore
```

**With OAuth (local dev, not billed):**
```bash
bella login          # opens browser, writes .bella file (project + env)
bella secrets get -o .env && dotnet run
```

**With API key (CI/CD, production):**
```bash
# API key encodes project + environment — no .bella file needed
bella login --api-key bax-...
bella secrets get -o .env && dotnet run
```

## ASP.NET Core integration

```csharp
// Program.cs — load .env BEFORE builder is created
Env.TraversePath().Load();   // DotNetEnv reads .env → Environment variables

var builder = WebApplication.CreateBuilder(args);
// IConfiguration now picks up all env vars (AddEnvironmentVariables is registered by default)

var dbUrl = builder.Configuration["DATABASE_URL"];

// Or bind to strongly-typed options
builder.Services.Configure<MyOptions>(builder.Configuration.GetSection("MyApp"));
```

## Works with any .NET command

```bash
# ASP.NET Core
bella secrets get -o .env && dotnet run

# EF Core migrations
bella secrets get -e staging -o .env && dotnet ef database update

# Tests
bella secrets get -e test -o .env && dotnet test
```

## Security notes

- Add `.env` to `.gitignore`
- For production, prefer `AddBellaSecrets()` (sample 03) — polling, hot-reload, no file on disk


**Pattern:** CLI writes secrets to a `.env` file → app reads it with `DotNetEnv`.

Works with any .NET application — console, ASP.NET Core, Worker Service, etc.

---

