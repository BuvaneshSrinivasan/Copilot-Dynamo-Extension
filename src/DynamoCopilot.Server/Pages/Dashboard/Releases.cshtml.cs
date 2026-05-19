using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Pages.Dashboard;

public class ReleasesModel : DashboardPageModel
{
    private readonly AppDbContext _db;

    public ReleasesModel(AppDbContext db) { _db = db; }

    // ── View data ─────────────────────────────────────────────────────────────

    public List<AppRelease>                    AllReleases           { get; set; } = [];
    public AppRelease?                         LatestRelease         { get; set; }
    public List<VersionRow>                    Distribution          { get; set; } = [];
    public Dictionary<string, List<UserEntry>> UsersByVersion        { get; set; } = new();
    public int                                 TotalUsersWithVersion { get; set; }
    public string?                             SuccessMessage        { get; set; }
    public string?                             ErrorMessage          { get; set; }

    public record VersionRow(string Version, int UserCount, double Pct, bool IsCurrent);
    public record UserEntry(string Email, Guid Id);

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task OnGetAsync() => await LoadAsync();

    // ── POST: set minVersion on the latest release ────────────────────────────

    [BindProperty] public string GlobalMinVersion { get; set; } = "1.0.0";

    public async Task<IActionResult> OnPostSetGlobalMinVersionAsync()
    {
        if (string.IsNullOrWhiteSpace(GlobalMinVersion) ||
            !System.Version.TryParse(GlobalMinVersion.Trim(), out _))
        {
            ErrorMessage = "Invalid version format. Use x.y.z (e.g. 1.0.0).";
            await LoadAsync();
            return Page();
        }

        var latest = await _db.AppReleases
            .OrderByDescending(r => r.PublishedAt)
            .FirstOrDefaultAsync();

        if (latest is null)
        {
            ErrorMessage = "No release published yet. Run publish-update.ps1 first.";
            await LoadAsync();
            return Page();
        }

        latest.MinVersion = GlobalMinVersion.Trim();
        await _db.SaveChangesAsync();

        SuccessMessage = $"Minimum required version set to {latest.MinVersion} on v{latest.Version}.";
        await LoadAsync();
        return Page();
    }

    // ── POST: edit minVersion on a specific release (via history table modal) ──

    [BindProperty] public Guid?   EditReleaseId  { get; set; }
    [BindProperty] public string? EditMinVersion { get; set; }

    public async Task<IActionResult> OnPostSetMinVersionAsync()
    {
        if (EditReleaseId == null || string.IsNullOrWhiteSpace(EditMinVersion) ||
            !System.Version.TryParse(EditMinVersion.Trim(), out _))
        {
            ErrorMessage = "Release ID and minVersion are required. Use x.y.z (e.g. 1.0.0).";
            await LoadAsync();
            return Page();
        }

        var release = await _db.AppReleases.FindAsync(EditReleaseId);
        if (release is null)
        {
            ErrorMessage = "Release not found.";
            await LoadAsync();
            return Page();
        }

        release.MinVersion = EditMinVersion.Trim();
        await _db.SaveChangesAsync();

        SuccessMessage = $"minVersion for v{release.Version} updated to {release.MinVersion}.";
        await LoadAsync();
        return Page();
    }

    // ── Data load ─────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        AllReleases   = await _db.AppReleases.OrderByDescending(r => r.PublishedAt).ToListAsync();
        LatestRelease = AllReleases.FirstOrDefault();

        var users = await _db.Users
            .Where(u => u.InstalledVersion != null)
            .Select(u => new { u.InstalledVersion, u.Email, u.Id })
            .ToListAsync();

        UsersByVersion = users
            .GroupBy(u => u.InstalledVersion!)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(u => u.Email).Select(u => new UserEntry(u.Email, u.Id)).ToList());

        TotalUsersWithVersion = users.Count;

        var currentVersion = LatestRelease?.Version;

        Distribution = UsersByVersion
            .Select(kv => new VersionRow(
                kv.Key,
                kv.Value.Count,
                TotalUsersWithVersion > 0 ? Math.Round(kv.Value.Count * 100.0 / TotalUsersWithVersion, 1) : 0,
                kv.Key == currentVersion))
            .OrderByDescending(r => r.UserCount)
            .ToList();

        // Default the minVersion input to the current latest release value (or 1.0.0)
        GlobalMinVersion = LatestRelease?.MinVersion ?? "1.0.0";
    }
}
