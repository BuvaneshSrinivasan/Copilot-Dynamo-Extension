using DynamoCopilot.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Endpoints;

public static class ReleaseEndpoints
{
    public static void MapReleaseEndpoints(this WebApplication app)
    {
        // Public — no auth required. Called by the extension on startup to check
        // whether a newer version is available. Returns 404 if no release has been
        // published yet (new installs — extension treats this as "up to date").
        app.MapGet("/api/version/latest", async (AppDbContext db, CancellationToken ct) =>
        {
            var latest = await db.AppReleases
                .OrderByDescending(r => r.PublishedAt)
                .FirstOrDefaultAsync(ct);

            if (latest == null)
                return Results.NotFound(new { error = "no_release" });

            return Results.Json(new
            {
                version      = latest.Version,
                minVersion   = latest.MinVersion,
                releaseNotes = latest.ReleaseNotes,
                dlls = new
                {
                    url       = latest.DllsUrl,
                    sizeBytes = latest.DllsSizeBytes
                },
                nodesDb = latest.DbUrl == null
                    ? (object?)null
                    : new
                    {
                        dbVersion = latest.DbVersion,
                        url       = latest.DbUrl,
                        sizeBytes = latest.DbSizeBytes
                    }
            });
        });
    }
}
