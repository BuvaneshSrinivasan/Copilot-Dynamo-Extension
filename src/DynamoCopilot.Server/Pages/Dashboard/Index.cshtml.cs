using System.Text.Json;
using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Pages.Dashboard;

public class IndexModel : DashboardPageModel
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public IndexModel(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int ActiveLicences { get; set; }
    public long TotalTokensToday { get; set; }
    public int TotalRequestsToday { get; set; }

    public string RegistrationsByMonthJson { get; set; } = "[]";
    public string StatusDataJson { get; set; } = "{}";

    public List<RecentUser> RecentUsers { get; set; } = [];
    public List<TopUser> TopUsers { get; set; } = [];

    public async Task OnGetAsync()
    {
        var users = await _db.Users.Include(u => u.Licenses).ToListAsync();
        var now = DateTime.UtcNow;

        TotalUsers = users.Count;
        ActiveUsers = users.Count(u => u.IsActive);
        ActiveLicences = users.SelectMany(u => u.Licenses).Count(l => l.IsActive && (l.EndDate == null || l.EndDate > now));
        TotalTokensToday = users.Sum(u => (long)u.DailyTokenCount);
        TotalRequestsToday = users.Sum(u => u.DailyRequestCount);

        // Registrations by month — last 6 months including empty months
        var months = Enumerable.Range(0, 6)
            .Select(i => now.AddMonths(-i))
            .OrderBy(d => d)
            .Select(d => new { d.Year, d.Month, Label = d.ToString("MMM yy") })
            .ToList();

        var regCounts = users
            .Where(u => u.CreatedAt >= now.AddMonths(-6))
            .GroupBy(u => new { u.CreatedAt.Year, u.CreatedAt.Month })
            .ToDictionary(g => (g.Key.Year, g.Key.Month), g => g.Count());

        var regData = months.Select(m => new Dictionary<string, object>
        {
            ["label"] = m.Label,
            ["count"] = regCounts.GetValueOrDefault((m.Year, m.Month), 0)
        }).ToList();

        RegistrationsByMonthJson = JsonSerializer.Serialize(regData);
        StatusDataJson = JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["active"] = ActiveUsers,
            ["inactive"] = TotalUsers - ActiveUsers
        });

        RecentUsers = users
            .OrderByDescending(u => u.CreatedAt)
            .Take(6)
            .Select(u => new RecentUser(u.Email, u.IsActive, u.CreatedAt))
            .ToList();

        TopUsers = users
            .Where(u => u.DailyTokenCount > 0 || u.DailyRequestCount > 0)
            .OrderByDescending(u => u.DailyTokenCount)
            .Take(6)
            .Select(u => new TopUser(u.Email, u.DailyTokenCount, u.DailyRequestCount))
            .ToList();
    }

    public record RecentUser(string Email, bool IsActive, DateTime CreatedAt);
    public record TopUser(string Email, int DailyTokenCount, int DailyRequestCount);
}
