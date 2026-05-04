using System.Text.Json;
using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Pages.Dashboard;

public class UserDetailModel : DashboardPageModel
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public UserDetailModel(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public User? DetailUser { get; set; }
    public int DefaultRequestLimit { get; set; }

    // Today (live from User counters)
    public int TodayRequests { get; set; }
    public int TodayTokens   { get; set; }

    // This month (UsageLogs sum + today)
    public int MonthRequests { get; set; }
    public int MonthTokens   { get; set; }

    // Chart — last 30 days (logs + today)
    public string ChartJson { get; set; } = "[]";

    [BindProperty] public string GrantExtension    { get; set; } = string.Empty;
    [BindProperty] public int    GrantMonths       { get; set; } = 12;
    [BindProperty] public string RevokeExtension   { get; set; } = string.Empty;
    [BindProperty] public int?   CustomRequestLimit { get; set; }
    [BindProperty] public string? Notes            { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (DetailUser is null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostActivateAsync()
    {
        var user = await _db.Users.FindAsync(Id);
        if (user is null) return NotFound();
        user.IsActive = true;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Account activated.";
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostDeactivateAsync()
    {
        var user = await _db.Users.FindAsync(Id);
        if (user is null) return NotFound();
        user.IsActive = false;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Account deactivated.";
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostResetUsageAsync()
    {
        var user = await _db.Users.FindAsync(Id);
        if (user is null) return NotFound();
        user.DailyRequestCount = 0;
        user.DailyTokenCount   = 0;
        user.LastResetDate     = DateOnly.FromDateTime(DateTime.UtcNow);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Today's usage counters reset.";
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostGrantAsync()
    {
        if (string.IsNullOrWhiteSpace(GrantExtension) || GrantMonths <= 0)
        {
            TempData["Error"] = "Invalid extension or months value.";
            return RedirectToPage(new { Id });
        }

        var user = await _db.Users.FindAsync(Id);
        if (user is null) return NotFound();

        var now      = DateTime.UtcNow;
        var existing = await _db.UserLicenses
            .FirstOrDefaultAsync(ul => ul.UserId == Id && ul.Extension == GrantExtension);

        if (existing is null)
        {
            _db.UserLicenses.Add(new UserLicense
            {
                UserId    = Id,
                Extension = GrantExtension,
                IsActive  = true,
                StartDate = now,
                EndDate   = now.AddMonths(GrantMonths)
            });
        }
        else
        {
            var baseDate = existing.EndDate.HasValue && existing.EndDate.Value > now
                ? existing.EndDate.Value : now;
            existing.IsActive = true;
            existing.EndDate  = baseDate.AddMonths(GrantMonths);
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = $"{GrantExtension} licence granted for {GrantMonths} month(s).";
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostRevokeAsync()
    {
        if (string.IsNullOrWhiteSpace(RevokeExtension))
        {
            TempData["Error"] = "No extension specified.";
            return RedirectToPage(new { Id });
        }

        var lic = await _db.UserLicenses
            .FirstOrDefaultAsync(ul => ul.UserId == Id && ul.Extension == RevokeExtension);

        if (lic is null)
        {
            TempData["Error"] = $"No {RevokeExtension} licence found.";
            return RedirectToPage(new { Id });
        }

        lic.IsActive = false;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{RevokeExtension} licence revoked.";
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostSaveLimitsAsync()
    {
        var user = await _db.Users.FindAsync(Id);
        if (user is null) return NotFound();
        user.RequestLimit = CustomRequestLimit;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Request limit updated.";
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostClearLimitsAsync()
    {
        var user = await _db.Users.FindAsync(Id);
        if (user is null) return NotFound();
        user.RequestLimit = null;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Request limit reset to global default.";
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostSaveNotesAsync()
    {
        var user = await _db.Users.FindAsync(Id);
        if (user is null) return NotFound();
        user.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        await _db.SaveChangesAsync();
        TempData["Success"] = "Notes saved.";
        return RedirectToPage(new { Id });
    }

    private async Task LoadAsync()
    {
        DefaultRequestLimit = _config.GetValue<int>("RateLimit:DailyRequestLimit", 200);

        DetailUser = await _db.Users
            .Include(u => u.Licenses)
            .FirstOrDefaultAsync(u => u.Id == Id);

        if (DetailUser is null) return;

        CustomRequestLimit = DetailUser.RequestLimit;
        Notes              = DetailUser.Notes;

        // Live today counters
        TodayRequests = DetailUser.DailyRequestCount;
        TodayTokens   = DetailUser.DailyTokenCount;

        // Historical logs — last 30 days + current month
        var today        = DateOnly.FromDateTime(DateTime.UtcNow);
        var thirtyAgo    = today.AddDays(-29);
        var monthStart   = new DateOnly(today.Year, today.Month, 1);

        var logs = await _db.UsageLogs
            .Where(l => l.UserId == Id && l.Date >= thirtyAgo)
            .ToListAsync();

        // Monthly total = all logs this month + today's live counters
        MonthRequests = logs.Where(l => l.Date >= monthStart).Sum(l => l.RequestCount) + TodayRequests;
        MonthTokens   = logs.Where(l => l.Date >= monthStart).Sum(l => l.TokenCount)   + TodayTokens;

        // Build 30-day series (past 29 days from logs + today from live counters)
        var logByDate = logs.ToDictionary(l => l.Date);
        var days = Enumerable.Range(0, 30)
            .Select(i => today.AddDays(-29 + i))
            .ToList();

        var chartPoints = days.Select(d =>
        {
            int req = 0, tok = 0;
            if (d == today)
            {
                req = TodayRequests;
                tok = TodayTokens;
            }
            else if (logByDate.TryGetValue(d, out var log))
            {
                req = log.RequestCount;
                tok = log.TokenCount;
            }
            return new Dictionary<string, object>
            {
                ["date"]     = d.ToString("dd MMM"),
                ["requests"] = req,
                ["tokens"]   = tok
            };
        }).ToList();

        ChartJson = JsonSerializer.Serialize(chartPoints);
    }
}
