using DynamoCopilot.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace DynamoCopilot.Server.Data;

// =============================================================================
// AppDbContext — The bridge between your C# code and the PostgreSQL database
// =============================================================================
//
// DbContext is EF Core's central class. It does three things:
//
//   1. SCHEMA DEFINITION
//      The DbSet<T> properties tell EF Core which C# classes map to which tables.
//      DbSet<User> Users → a "Users" table in the database.
//
//   2. QUERYING
//      You query the database using LINQ on these DbSet properties:
//        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
//      EF Core translates that LINQ into SQL and executes it.
//
//   3. CHANGE TRACKING + SAVING
//      When you modify a User object and call SaveChangesAsync(), EF Core
//      generates the appropriate UPDATE SQL and runs it.
//
// Think of AppDbContext as a "unit of work" — it represents one session with the DB.
// That's why we register it as Scoped in Program.cs (one instance per HTTP request).
// =============================================================================

public class AppDbContext : DbContext
{
    // DbSet<User> represents the Users table.
    // You'll use this to query, insert, update and delete users:
    //   await db.Users.ToListAsync()                       → SELECT * FROM "Users"
    //   db.Users.Add(newUser); await db.SaveChangesAsync() → INSERT INTO "Users" ...
    //   await db.Users.FindAsync(id)                       → SELECT ... WHERE "Id" = @id
    public DbSet<User> Users { get; set; } = null!;

    // DbContextOptions carries the connection string and provider (Npgsql for PostgreSQL).
    // We don't configure this here — it's passed in from Program.cs where we call
    // builder.Services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(...))
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // OnModelCreating is where you fine-tune the table/column configuration
    // using the "Fluent API" — a chain of method calls that reads like a sentence.
    // You can also use [Attributes] on the entity class, but Fluent API is more powerful
    // and keeps configuration out of your domain models.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            // PRIMARY KEY
            entity.HasKey(u => u.Id);

            // Tell PostgreSQL to auto-generate the Guid using its built-in function.
            // gen_random_uuid() is PostgreSQL's UUID v4 generator — no app-side generation needed.
            entity.Property(u => u.Id)
                  .HasDefaultValueSql("gen_random_uuid()");

            // EMAIL — unique index so two users can't register with the same email.
            // HasMaxLength sets the column type to VARCHAR(256) instead of TEXT,
            // which is required for PostgreSQL to create an index on it efficiently.
            entity.Property(u => u.Email)
                  .HasMaxLength(256)
                  .IsRequired();

            entity.HasIndex(u => u.Email)
                  .IsUnique();

            // PASSWORD HASH — BCrypt hashes are always 60 characters
            entity.Property(u => u.PasswordHash)
                  .HasMaxLength(60)
                  .IsRequired();

            // NOTES — optional free text, cap at 1000 chars to avoid runaway storage
            entity.Property(u => u.Notes)
                  .HasMaxLength(1000);

            // TIMESTAMP — store as UTC in PostgreSQL's timestamptz type
            entity.Property(u => u.CreatedAt)
                  .HasDefaultValueSql("NOW()");
        });
    }
}
