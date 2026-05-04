namespace DynamoCopilot.Server.Models;

// One row per user per day — written by RateLimitMiddleware before it resets the daily counters.
// This gives us a permanent history so the dashboard can show daily/monthly breakdowns.
public class UsageLog
{
    public Guid     Id           { get; set; }
    public Guid     UserId       { get; set; }
    public User     User         { get; set; } = null!;
    public DateOnly Date         { get; set; }
    public int      RequestCount { get; set; }
    public int      TokenCount   { get; set; }
}
