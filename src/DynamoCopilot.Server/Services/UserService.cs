using DynamoCopilot.Server.Data;
using DynamoCopilot.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Services;

public sealed class UserService
{
    private readonly AppDbContext _db;

    public UserService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Finds an existing user by (provider, subjectId) or creates a new Free-tier user.
    /// Also updates Email and LastSeenAt on every login.
    /// </summary>
    public async Task<User> UpsertAsync(string provider, string subjectId, string email, CancellationToken ct = default)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.OAuthProvider == provider && u.OAuthSubjectId == subjectId, ct);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                OAuthProvider = provider,
                OAuthSubjectId = subjectId,
                Email = email,
                Tier = UserTier.Free,
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            };
            _db.Users.Add(user);
        }
        else
        {
            user.Email = email;
            user.LastSeenAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.Users.FindAsync(new object[] { id }, ct);
}
