using DynamoCopilot.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedOnAdd();

            entity.Property(u => u.Email)
                  .IsRequired()
                  .HasMaxLength(320);

            entity.Property(u => u.OAuthProvider)
                  .IsRequired()
                  .HasMaxLength(32);

            entity.Property(u => u.OAuthSubjectId)
                  .IsRequired()
                  .HasMaxLength(256);

            entity.Property(u => u.Tier).IsRequired();
            entity.Property(u => u.CreatedAt).IsRequired();
            entity.Property(u => u.LastSeenAt).IsRequired();

            // One user per provider + subject combination (prevents duplicate accounts).
            entity.HasIndex(u => new { u.OAuthProvider, u.OAuthSubjectId }).IsUnique();

            entity.HasIndex(u => u.Email);
        });
    }
}
