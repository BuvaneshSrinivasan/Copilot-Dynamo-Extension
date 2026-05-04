using DynamoCopilot.Server.Data;
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

    public List<UserRow> Users { get; set; } = [];

    public async Task OnGetAsync()
    {
        var defaultRequestLimit = int.Parse(_config["RateLimit:DailyRequestLimit"] ?? "30");
        var defaultTokenLimit   = int.Parse(_config["RateLimit:DailyTokenLimit"]   ?? "40000");
        var now = DateTime.UtcNow;

        var query = _db.Users.Include(u => u.Licenses).AsQueryable();

        if (!string.IsNullOrWhiteSpace(Search))
            query = query.Where(u => u.Email.Contains(Search.Trim().ToLower()));

        if (Status == "active")
            query = query.Where(u => u.IsActive);
        else if (Status == "inactive")
            query = query.Where(u => !u.IsActive);

        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();

        Users = users.Select(u => new UserRow(
            Id:                  u.Id,
            Email:               u.Email,
            IsActive:            u.IsActive,
            Licenses:            u.Licenses.Select(l => new LicenceInfo(
                                     l.Extension, l.IsActive,
                                     l.EndDate.HasValue && l.EndDate.Value < now)).ToList(),
            DailyRequestCount:   u.DailyRequestCount,
            DailyTokenCount:     u.DailyTokenCount,
            EffectiveReqLimit:   u.RequestLimit ?? defaultRequestLimit,
            EffectiveTokLimit:   u.TokenLimit   ?? defaultTokenLimit,
            CreatedAt:           u.CreatedAt
        )).ToList();
    }

    public record UserRow(
        Guid Id, string Email, bool IsActive,
        List<LicenceInfo> Licenses,
        int DailyRequestCount, int DailyTokenCount,
        int EffectiveReqLimit, int EffectiveTokLimit,
        DateTime CreatedAt);

    public record LicenceInfo(string Extension, bool IsActive, bool Expired);
}
