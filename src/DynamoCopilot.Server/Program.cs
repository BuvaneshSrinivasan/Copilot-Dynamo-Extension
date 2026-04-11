// =============================================================================
// Program.cs — Application Entry Point
// =============================================================================
// Phase 2 additions vs Phase 1:
//   - DbContext registration (connects EF Core to PostgreSQL)
//   - Auto-migration on startup (creates/updates the DB schema on every deploy)
//   - ResolveConnectionString helper (handles both local dev and Railway formats)
// =============================================================================

using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Endpoints;
using DynamoCopilot.Server.Services;
using Microsoft.EntityFrameworkCore;

// ── STEP 1: REGISTER SERVICES ─────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Railway sets PORT automatically. Without this, the app binds to the wrong port
// and Railway's health checks fail.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// IHttpClientFactory — reuses sockets to avoid socket exhaustion under load
builder.Services.AddHttpClient();

// AI provider — change this one line to swap to a different LLM in the future
builder.Services.AddScoped<ILlmService, GeminiService>();

// DATABASE
// AddDbContext registers AppDbContext as Scoped (one instance per HTTP request).
// Scoped is the correct lifetime for DbContext — it should not be shared across requests
// because it tracks changes in memory and is not thread-safe.
//
// UseNpgsql tells EF Core to use the Npgsql provider (PostgreSQL).
// We pass the connection string via a helper function (see bottom of this file)
// so that we handle both local development and Railway production formats.
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(ResolveConnectionString(builder.Configuration)));

// ── STEP 2: BUILD ─────────────────────────────────────────────────────────────

var app = builder.Build();

// ── AUTO-MIGRATE ON STARTUP ───────────────────────────────────────────────────
// This block runs any pending EF Core migrations when the app starts.
//
// Why auto-migrate instead of running `dotnet ef database update` manually?
//   - Railway restarts the container on every deploy
//   - Running the migration at startup guarantees the schema is always in sync
//   - EF Core migrations are idempotent (safe to run multiple times — they skip
//     migrations that have already been applied)
//
// How it works:
//   - EF Core keeps a "__EFMigrationsHistory" table in your database
//   - On startup it checks which migrations are listed there
//   - Any migration NOT in that table gets applied
//
// We create a temporary scope because DbContext is Scoped
// (it can't be resolved from the root scope directly)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// ── STEP 3: MIDDLEWARE PIPELINE ───────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// [Phase 3] UseAuthentication() and UseAuthorization() go here
// [Phase 4] app.UseMiddleware<RateLimitMiddleware>() goes here

// ── ENDPOINTS ─────────────────────────────────────────────────────────────────

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    version = "2.0",
    timestamp = DateTime.UtcNow
}));

app.MapChatEndpoints();
// [Phase 3] app.MapAuthEndpoints() goes here
// [Phase 5] app.MapAdminEndpoints() goes here

app.Run();

// =============================================================================
// HELPER: ResolveConnectionString
// =============================================================================
// Why do we need this?
//
// Local development uses a key-value connection string:
//   "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=dynamocopilot_dev"
//
// Railway provides a full PostgreSQL URI in the DATABASE_URL environment variable:
//   "postgresql://username:password@host.railway.internal:5432/railway"
//
// Npgsql (the PostgreSQL driver for .NET) supports both formats, but we need to
// handle both cases and add SSL settings for Railway's managed database.
// =============================================================================
static string ResolveConnectionString(IConfiguration config)
{
    // Railway sets DATABASE_URL automatically when you add a PostgreSQL addon.
    // Check for it first — if it exists, we're running in production on Railway.
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

    if (!string.IsNullOrEmpty(databaseUrl))
    {
        // Convert the URI format to Npgsql key-value format.
        // URI: postgresql://alice:secret@db.railway.internal:5432/mydb
        // Npgsql: Host=db.railway.internal;Port=5432;Username=alice;Password=secret;Database=mydb
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);

        return $"Host={uri.Host};" +
               $"Port={uri.Port};" +
               $"Username={userInfo[0]};" +
               $"Password={Uri.UnescapeDataString(userInfo[1])};" +
               $"Database={uri.AbsolutePath.TrimStart('/')};" +
               $"SSL Mode=Require;" +
               $"Trust Server Certificate=true";
    }

    // Local development: read from ConnectionStrings:DefaultConnection in appsettings.Development.json
    return config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "No database connection configured. " +
            "Add ConnectionStrings:DefaultConnection to appsettings.Development.json.");
}
