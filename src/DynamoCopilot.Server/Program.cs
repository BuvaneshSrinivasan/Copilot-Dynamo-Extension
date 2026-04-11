// =============================================================================
// Program.cs — Application Entry Point
// =============================================================================
// Phase 3 additions vs Phase 2:
//   - JWT Bearer authentication setup
//   - TokenService registration
//   - UseAuthentication() + UseAuthorization() middleware
//   - Auth endpoints mapped
// =============================================================================

using System.Text;
using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Endpoints;
using DynamoCopilot.Server.Middleware;
using DynamoCopilot.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// ── STEP 1: REGISTER SERVICES ─────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient();
builder.Services.AddScoped<ILlmService, GeminiService>();
builder.Services.AddScoped<TokenService>();

// UsageTracker is Scoped so GeminiService and RateLimitMiddleware share the
// same instance within one request — GeminiService writes, middleware reads.
builder.Services.AddScoped<UsageTracker>();

// DATABASE
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(ResolveConnectionString(builder.Configuration)));

// JWT AUTHENTICATION
// ─────────────────────────────────────────────────────────────────────────────
// AddAuthentication registers the authentication services and sets the default scheme.
// "JwtBearer" means: look for a JWT in the "Authorization: Bearer {token}" header.
//
// AddJwtBearer tells ASP.NET Core HOW to validate a JWT:
//   - Verify it was signed by us (using our secret key)
//   - Verify it hasn't expired
//   - Verify the issuer and audience match what we expect
//
// This does NOT protect any endpoints by itself.
// Protection happens in the endpoint via .RequireAuthorization().
// The middleware just populates HttpContext.User if a valid token is present.
// ─────────────────────────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? Environment.GetEnvironmentVariable("JWT__SECRET")
    ?? throw new InvalidOperationException(
        "Jwt:Secret is not configured. " +
        "Set it in appsettings.Development.json (local) or as JWT__SECRET env var in Railway.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,             // reject expired tokens
            ValidateIssuerSigningKey = true,     // reject tampered tokens
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "DynamoCopilot",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "DynamoCopilot",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            // ClockSkew: allow 30 seconds of clock difference between server and client
            // (default is 5 minutes, which is too generous)
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// ── STEP 2: BUILD ─────────────────────────────────────────────────────────────

var app = builder.Build();

// ── AUTO-MIGRATE ON STARTUP ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// ── STEP 3: MIDDLEWARE PIPELINE ───────────────────────────────────────────────
//
// MIDDLEWARE ORDER IS CRITICAL. The rules:
//   1. UseDeveloperExceptionPage — must be first so it catches all exceptions
//   2. UseAuthentication         — reads JWT from header, populates HttpContext.User
//   3. UseAuthorization          — checks if the current user can access the endpoint
//   4. [Phase 4] UseMiddleware<RateLimitMiddleware> — goes AFTER auth so we know who the user is

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// UseAuthentication: reads the "Authorization: Bearer {token}" header on every request.
// If the token is valid, it sets HttpContext.User with the claims from the JWT.
// If the token is missing or invalid, HttpContext.User is anonymous (not authenticated).
// It does NOT reject the request — that's UseAuthorization's job.
app.UseAuthentication();

// UseAuthorization: for endpoints marked with .RequireAuthorization(), it checks
// whether HttpContext.User is authenticated. If not → 401 Unauthorized.
// For endpoints without .RequireAuthorization(), it does nothing.
app.UseAuthorization();

// Rate limiting runs AFTER auth so HttpContext.User is already populated with the
// JWT claims. The middleware skips unauthenticated requests automatically.
app.UseMiddleware<RateLimitMiddleware>();

// ── ENDPOINTS ─────────────────────────────────────────────────────────────────

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    version = "3.0",
    timestamp = DateTime.UtcNow
}));

app.MapAuthEndpoints();   // POST /auth/register, /auth/login, /auth/refresh
app.MapChatEndpoints();   // POST /api/chat/stream (requires JWT)
app.MapUserEndpoints();   // GET  /api/me          (requires JWT)
app.MapAdminEndpoints();  // GET+POST /admin/users/* (requires X-Admin-Key header)

app.Run();

// ── HELPERS ───────────────────────────────────────────────────────────────────

static string ResolveConnectionString(IConfiguration config)
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        return $"Host={uri.Host};" +
               $"Port={uri.Port};" +
               $"Username={userInfo[0]};" +
               $"Password={Uri.UnescapeDataString(userInfo[1])};" +
               $"Database={uri.AbsolutePath.TrimStart('/')};" +
               // Prefer: tries SSL first, falls back to plain TCP if unavailable.
               // Railway's internal hostname (*.railway.internal) does not have SSL
               // configured — Require would cause the connection to be refused.
               $"SSL Mode=Prefer;" +
               $"Trust Server Certificate=true";
    }

    return config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "No database connection configured. " +
            "Add ConnectionStrings:DefaultConnection to appsettings.Development.json.");
}
