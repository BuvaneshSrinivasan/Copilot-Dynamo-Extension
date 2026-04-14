using DynamoCopilot.Server.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

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
    public DbSet<DynamoNode> DynamoNodes { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Register the pgvector extension so EF Core migrations emit
        // "CREATE EXTENSION IF NOT EXISTS vector" in the generated SQL.
        // UseVector() is registered on NpgsqlDbContextOptionsBuilder in Program.cs.
        modelBuilder.HasPostgresExtension("vector");

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

        // ── DYNAMO NODES ──────────────────────────────────────────────────────
        modelBuilder.Entity<DynamoNode>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.Property(n => n.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.Property(n => n.Name).HasMaxLength(512).IsRequired();
            entity.Property(n => n.PackageName).HasMaxLength(256).IsRequired();
            entity.Property(n => n.NodeType).HasMaxLength(32).IsRequired();
            entity.Property(n => n.Description).HasMaxLength(4000);
            entity.Property(n => n.Category).HasMaxLength(512);
            entity.Property(n => n.PackageDescription).HasMaxLength(4000);
            entity.Property(n => n.IndexedAt).HasDefaultValueSql("NOW()");

            // PostgreSQL native arrays — stored as text[] columns
            entity.Property(n => n.Keywords).HasColumnType("text[]");
            entity.Property(n => n.InputPorts).HasColumnType("text[]");
            entity.Property(n => n.OutputPorts).HasColumnType("text[]");

            // 768-dimensional vector for Gemini text-embedding-004
            entity.Property(n => n.Embedding).HasColumnType("vector(768)");

            // (PackageName, Name) must be unique — the indexer uses this to skip
            // already-indexed nodes so the tool is safely re-runnable.
            entity.HasIndex(n => new { n.PackageName, n.Name }).IsUnique();

            // IVFFlat index: partitions the vector space into lists (here 100).
            // Trades a small accuracy loss for much faster queries on large tables.
            // Rule of thumb: lists ≈ sqrt(row_count). 100 is right for ~10k–100k rows.
            // probes (set at query time) controls the accuracy/speed trade-off.
            entity.HasIndex(n => n.Embedding)
                  .HasMethod("ivfflat")
                  .HasOperators("vector_cosine_ops")
                  .HasStorageParameter("lists", 100);
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
