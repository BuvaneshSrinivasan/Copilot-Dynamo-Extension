using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DynamoCopilot.Server.Data;

// Used ONLY by EF CLI tools (e.g. 'dotnet ef migrations add').
// This class is never instantiated at runtime.
// It uses a hardcoded local dev connection string so you don't need
// DATABASE_URL set just to create or inspect migrations.
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=dynamocopilot_dev",
                npgsql => npgsql.MigrationsAssembly("DynamoCopilot.Server"))
            .Options;

        return new AppDbContext(options);
    }
}
