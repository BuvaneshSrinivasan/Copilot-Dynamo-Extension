using DynamoCopilot.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Data;

// =============================================================================
// AppDbContext — The bridge between your C# code and the PostgreSQL database
// =============================================================================
//
// Each DbSet<T> property represents one table. EF Core translates LINQ queries
// on these sets into SQL and executes them against the database.
// =============================================================================

public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── USERS ──────────────────────────────────────────────────────────────
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.HasIndex(u => u.Email).IsUnique();

            // BCrypt hashes are always exactly 60 characters
            entity.Property(u => u.PasswordHash).HasMaxLength(60).IsRequired();

            entity.Property(u => u.Notes).HasMaxLength(1000);
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("NOW()");
        });

        // ── REFRESH TOKENS ─────────────────────────────────────────────────────
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(rt => rt.Id);
            entity.Property(rt => rt.Id).HasDefaultValueSql("gen_random_uuid()");

            // SHA-256 hashes base64-encoded are always 44 characters
            entity.Property(rt => rt.TokenHash).HasMaxLength(64).IsRequired();

            // Index on TokenHash enables fast O(1) lookup in the refresh endpoint
            entity.HasIndex(rt => rt.TokenHash).IsUnique();

            entity.Property(rt => rt.CreatedAt).HasDefaultValueSql("NOW()");

            // Foreign key: RefreshToken → User
            // OnDelete Cascade: when a User is deleted, all their refresh tokens are deleted too
            entity.HasOne(rt => rt.User)
                  .WithMany()
                  .HasForeignKey(rt => rt.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
