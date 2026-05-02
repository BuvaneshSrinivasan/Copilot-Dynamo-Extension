namespace DynamoCopilot.Server.Filters;

// =============================================================================
// LicenseFilter — reusable endpoint filter that enforces per-extension licences
// =============================================================================
//
// Usage:
//   app.MapPost("/api/chat/stream", Handle)
//      .RequireAuthorization()
//      .AddEndpointFilter(LicenseFilter.Require(AppConstants.Extensions.Copilot));
//
// The filter reads the "ext" JWT claims populated at login. Each active licence
// adds one "ext" claim (e.g. ext=Copilot). If the required extension is absent
// the request is rejected with 403 before the handler runs.
// =============================================================================

public static class LicenseFilter
{
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>>
        Require(string extensionId) =>
        async (context, next) =>
        {
            var hasClaim = context.HttpContext.User
                .FindAll("ext")
                .Any(c => string.Equals(c.Value, extensionId, StringComparison.Ordinal));

            if (!hasClaim)
                return Results.Json(
                    new { error = "no_license", extension = extensionId },
                    statusCode: 403);

            return await next(context);
        };
}
