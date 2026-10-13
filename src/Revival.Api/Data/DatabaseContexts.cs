using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Revival.Api.Data;

public sealed class SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options) : AppDbContext(options) { }

public sealed class PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options) : AppDbContext(options) { }

public sealed class SqliteAppDbContextFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    public SqliteAppDbContext CreateDbContext(string[] args)
    {
        var path = Environment.GetEnvironmentVariable("Database__SqlitePath") ?? "./data/revival.db";
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var options = new DbContextOptionsBuilder<SqliteAppDbContext>()
            .UseSqlite($"Data Source={fullPath};Cache=Shared;Pooling=True;Default Timeout=30")
            .Options;
        return new SqliteAppDbContext(options);
    }
}

public sealed class PostgresAppDbContextFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Database=revival;Username=revival;Password=development-only";
        var options = new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql(connection).Options;
        return new PostgresAppDbContext(options);
    }
}
