using System.Net;
using System.Text;
using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Endpoints;
using DynamoCopilot.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- Bind to PORT env var (Railway sets this at runtime) ---
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// --- CORS ---
// In production, lock this down to known origins once the extension ships.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = builder.Configuration
            .GetSection("AllowedOrigins")
            .Get<string[]>();

        if (allowedOrigins is { Length: > 0 })
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
        else
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddEndpointsApiExplorer();

// --- JWT Authentication ---
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "DynamoCopilot",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "DynamoCopilot",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();

// --- Services ---
builder.Services.AddSingleton<OAuthStateService>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<OAuthService>();
builder.Services.AddHttpClient();

// --- Database ---
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(ResolveConnectionString(builder.Configuration)));

// --- Problem Details (RFC 7807) for consistent error responses ---
builder.Services.AddProblemDetails();

var app = builder.Build();

// --- Global exception handler ---
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async context =>
    {
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "application/json";

        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        if (exceptionFeature != null)
            logger.LogError(exceptionFeature.Error, "Unhandled exception");

        await context.Response.WriteAsJsonAsync(new
        {
            error = "An unexpected error occurred.",
            traceId = context.TraceIdentifier
        });
    });
});

// Note: Railway terminates TLS at the edge — do NOT redirect HTTP→HTTPS internally.
// app.UseHttpsRedirection();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// --- Auto-migrate on startup ---
// Idempotent: safe to run on every deploy. Railway restarts containers on deploy,
// so this guarantees the schema is always up to date without a separate migration step.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// --- Endpoints ---

app.MapAuthEndpoints();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    version = "1.0.0",
    timestamp = DateTime.UtcNow
}))
.WithName("HealthCheck")
.WithTags("Diagnostics");

app.Run();

// --- Helpers ---

// Railway injects DATABASE_URL as a full PostgreSQL URI:
//   postgresql://username:password@host:port/dbname
// Npgsql prefers key=value connection strings, so we convert it here.
// Falls back to ConnectionStrings:DefaultConnection in appsettings for local dev.
static string ResolveConnectionString(IConfiguration config)
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        return $"Host={uri.Host};Port={uri.Port};" +
               $"Username={userInfo[0]};Password={Uri.UnescapeDataString(userInfo[1])};" +
               $"Database={uri.AbsolutePath.TrimStart('/')};" +
               $"SSL Mode=Require;Trust Server Certificate=true";
    }

    return config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "No database connection configured. " +
            "Set DATABASE_URL env var (production) or ConnectionStrings:DefaultConnection in appsettings (local dev).");
}
