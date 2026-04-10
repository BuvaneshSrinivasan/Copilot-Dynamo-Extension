// =============================================================================
// Program.cs — Application Entry Point
// =============================================================================
// This file bootstraps the entire ASP.NET Core application.
//
// Every ASP.NET Core app follows the same three-step pattern:
//
//   STEP 1 — Register services into the DI container
//             Services are objects your app needs (e.g. GeminiService, HttpClient).
//             You register them here and ASP.NET Core creates + provides them
//             automatically to any class that asks for them in its constructor.
//
//   STEP 2 — builder.Build()
//             This locks in your services and creates the WebApplication object.
//
//   STEP 3 — Configure the middleware pipeline, then call app.Run()
//             Middleware = code that runs on EVERY request before hitting your endpoint.
//             The pipeline runs top-to-bottom in the order you add it.
//             [To learn more: Microsoft Docs — "ASP.NET Core Middleware"]
// =============================================================================

using DynamoCopilot.Server.Endpoints;
using DynamoCopilot.Server.Services;

// ── STEP 1: REGISTER SERVICES ─────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Railway (and most cloud platforms) tell your app which port to listen on
// via the PORT environment variable. We read it here so the app binds correctly.
// Without this, the app would default to port 5000/5001 and Railway wouldn't route to it.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// IHttpClientFactory manages a pool of reusable HttpClient instances.
// Why use a factory instead of `new HttpClient()`?
// Creating a new HttpClient() per request opens a new socket each time.
// Sockets aren't freed immediately after use — under load, you exhaust the OS limit.
// The factory reuses underlying sockets safely. Always use this pattern in ASP.NET Core.
builder.Services.AddHttpClient();

// Register GeminiService as the concrete implementation of ILlmService.
// "Scoped" lifetime = one GeminiService instance is created per HTTP request,
// then disposed when the request ends.
//
// Because we register the INTERFACE (ILlmService), not the class (GeminiService),
// ChatEndpoints.cs never directly references GeminiService. This means:
// - Swapping to a different AI provider = changing this one line only.
// - Writing unit tests = inject a fake ILlmService, no real API calls needed.
builder.Services.AddScoped<ILlmService, GeminiService>();

// ── STEP 2: BUILD ─────────────────────────────────────────────────────────────

var app = builder.Build();

// ── STEP 3: MIDDLEWARE PIPELINE ───────────────────────────────────────────────
//
// Think of middleware as a series of checkpoints every request passes through.
// Each checkpoint can:
//   a) Process the request and pass it to the next checkpoint  (e.g. log the URL)
//   b) Short-circuit and return early                          (e.g. return 401)
//   c) Do work after the inner handler returns                 (e.g. log response time)
//
//   Request ──▶ [Middleware A] ──▶ [Middleware B] ──▶ [Your Endpoint]
//                                                             │
//   Response ◀── [Middleware A] ◀── [Middleware B] ◀──────────┘
//
// ORDER MATTERS — middleware runs in the exact order it is added here.
// We will add more middleware in later phases:
//   Phase 3: UseAuthentication() + UseAuthorization() (JWT validation)
//   Phase 4: A custom RateLimitMiddleware

// In Development: show a detailed error page with the full stack trace in Postman/browser.
// In Production: never expose stack traces. ASP.NET Core handles this automatically
// based on the ASPNETCORE_ENVIRONMENT environment variable.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// ── ENDPOINTS ─────────────────────────────────────────────────────────────────
// These extension methods (defined in the Endpoints/ folder) register the actual
// URL routes. Keeping them in separate files keeps Program.cs readable as the
// project grows.

// GET /health — used by Railway to verify the container is running
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    version = "1.0",
    timestamp = DateTime.UtcNow
}));

// POST /api/chat/stream — the main chat endpoint
// [Phase 3] .RequireAuthorization() will be added here once JWT auth is set up
app.MapChatEndpoints();

app.Run();
