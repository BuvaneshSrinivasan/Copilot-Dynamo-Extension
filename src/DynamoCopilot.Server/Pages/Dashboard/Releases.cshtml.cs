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

    public List<AppRelease>        AllReleases    { get; set; } = [];
    public AppRelease?             LatestRelease  { get; set; }
    public List<VersionRow>        Distribution   { get; set; } = [];
    public int                     TotalUsersWithVersion { get; set; }
    public string?                 SuccessMessage { get; set; }
    public string?                 ErrorMessage   { get; set; }

    public record VersionRow(string Version, int UserCount, double Pct, bool IsCurrent);

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    // ── POST: publish new release ─────────────────────────────────────────────

    [BindProperty] public string? NewVersion       { get; set; }
    [BindProperty] public string? NewMinVersion    { get; set; }
    [BindProperty] public string? NewReleaseNotes  { get; set; }
    [BindProperty] public string? NewDllsUrl       { get; set; }
    [BindProperty] public long    NewDllsSizeBytes { get; set; }
    [BindProperty] public string? NewDbVersion     { get; set; }
    [BindProperty] public string? NewDbUrl         { get; set; }
    [BindProperty] public long?   NewDbSizeBytes   { get; set; }

    public async Task<IActionResult> OnPostPublishAsync()
    {
        if (string.IsNullOrWhiteSpace(NewVersion) || string.IsNullOrWhiteSpace(NewDllsUrl) || NewDllsSizeBytes <= 0)
        {
            ErrorMessage = "Version, DLLs URL, and DLLs size are required.";
            await LoadAsync();
            return Page();
        }

        _db.AppReleases.Add(new AppRelease
        {
            Version       = NewVersion.Trim(),
            MinVersion    = string.IsNullOrWhiteSpace(NewMinVersion) ? "1.0.0" : NewMinVersion.Trim(),
            ReleaseNotes  = NewReleaseNotes?.Trim() ?? "",
            DllsUrl       = NewDllsUrl.Trim(),
            DllsSizeBytes = NewDllsSizeBytes,
            DbVersion     = string.IsNullOrWhiteSpace(NewDbVersion) ? null : NewDbVersion.Trim(),
            DbUrl         = string.IsNullOrWhiteSpace(NewDbUrl)     ? null : NewDbUrl.Trim(),
            DbSizeBytes   = NewDbSizeBytes > 0 ? NewDbSizeBytes : null,
            PublishedAt   = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        SuccessMessage = $"Version {NewVersion} published.";
        await LoadAsync();
        return Page();
    }

    // ── POST: update minVersion on an existing release ────────────────────────

    [BindProperty] public Guid?   EditReleaseId  { get; set; }
    [BindProperty] public string? EditMinVersion { get; set; }

    public async Task<IActionResult> OnPostSetMinVersionAsync()
    {
        if (EditReleaseId == null || string.IsNullOrWhiteSpace(EditMinVersion))
        {
            ErrorMessage = "Release ID and minVersion are required.";
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

        // Version distribution — group users by InstalledVersion
        var usersWithVersion = await _db.Users
            .Where(u => u.InstalledVersion != null)
            .GroupBy(u => u.InstalledVersion!)
            .Select(g => new { Version = g.Key, Count = g.Count() })
            .ToListAsync();

        TotalUsersWithVersion = usersWithVersion.Sum(x => x.Count);

        var currentVersion = LatestRelease?.Version;

        Distribution = usersWithVersion
            .OrderByDescending(x => x.Count)
            .Select(x => new VersionRow(
                x.Version,
                x.Count,
                TotalUsersWithVersion > 0 ? Math.Round(x.Count * 100.0 / TotalUsersWithVersion, 1) : 0,
                x.Version == currentVersion))
            .ToList();
    }
}
