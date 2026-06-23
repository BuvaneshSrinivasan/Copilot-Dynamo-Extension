using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Pages.Dashboard;

public class UsersModel : DashboardPageModel
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public UsersModel(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    // New-user form fields
    [BindProperty] public string NewEmail    { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;

    public List<UserRow> Users { get; set; } = [];

    public async Task OnGetAsync()
    {
        var defaultRequestLimit = _config.GetValue<int>("RateLimit:DailyRequestLimit", 200);
        var now   = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var query = _db.Users.Include(u => u.Licenses).AsQueryable();

        if (!string.IsNullOrWhiteSpace(Search))
            query = query.Where(u => u.Email.Contains(Search.Trim().ToLower()));

        if (Status == "active")
            query = query.Where(u => u.IsActive);
        else if (Status == "inactive")
            query = query.Where(u => !u.IsActive);

        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();

        // All-time totals come from UsageLogs (one row per user per day); today's
        // counts live on User.DailyRequestCount/DailyTokenCount until the next
        // lazy reset writes them to UsageLogs, so they must be added on top.
        var usageTotals = await _db.UsageLogs
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Requests = g.Sum(l => l.RequestCount), Tokens = g.Sum(l => l.TokenCount) })
            .ToDictionaryAsync(x => x.UserId, x => x);

        Users = users.Select(u =>
        {
            var todayRequests = u.LastResetDate == today ? u.DailyRequestCount : 0;
            var todayTokens   = u.LastResetDate == today ? u.DailyTokenCount   : 0;
            usageTotals.TryGetValue(u.Id, out var totals);

            return new UserRow(
                Id:                  u.Id,
                Email:               u.Email,
                IsActive:            u.IsActive,
                Licenses:            u.Licenses.Select(l => new LicenceInfo(
                                         l.Extension, l.IsActive,
                                         l.EndDate.HasValue && l.EndDate.Value < now)).ToList(),
                OverallRequestCount: (totals?.Requests ?? 0) + todayRequests,
                OverallTokenCount:   (totals?.Tokens   ?? 0) + todayTokens,
                EffectiveReqLimit:   u.RequestLimit ?? defaultRequestLimit,
                CreatedAt:           u.CreatedAt
            );
        }).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEmail) || !NewEmail.Contains('@'))
        {
            TempData["Error"] = "A valid email address is required.";
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(NewPassword) || NewPassword.Length < 8)
        {
            TempData["Error"] = "Password must be at least 8 characters.";
            return RedirectToPage();
        }

        var email = NewEmail.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Email == email))
        {
            TempData["Error"] = $"An account already exists for '{email}'.";
            return RedirectToPage();
        }

        var user = new User
        {
            Id           = Guid.NewGuid(),
            Email        = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword),
            IsActive     = true
        };

        _db.Users.Add(user);
        _db.UserLicenses.Add(new UserLicense
        {
            UserId    = user.Id,
            Extension = AppConstants.Extensions.SuggestNodes,
            IsActive  = true,
            StartDate = DateTime.UtcNow,
            EndDate   = null
        });

        await _db.SaveChangesAsync();

        TempData["Success"] = $"Account created for {email}.";
        return RedirectToPage();
    }

    public record UserRow(
        Guid Id, string Email, bool IsActive,
        List<LicenceInfo> Licenses,
        int OverallRequestCount, int OverallTokenCount,
        int EffectiveReqLimit,
        DateTime CreatedAt);

    public record LicenceInfo(string Extension, bool IsActive, bool Expired);
}
