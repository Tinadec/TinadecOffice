using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Persistence;

/// <summary>Small adapter allowing each business module to participate in coordinated provider migrations.</summary>
public sealed class DbContextMigrationParticipant<TContext> : IStorageMigrationParticipant where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    public DbContextMigrationParticipant(IDbContextFactory<TContext> factory) => _factory = factory;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var migrations = db.Database.GetMigrations().ToArray();
        if (migrations.Length != 0)
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
    }
}

public static class DbContextSchemaBootstrapper
{
    public static async Task EnsureTablesAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(script)) await db.Database.ExecuteSqlRawAsync(script, cancellationToken).ConfigureAwait(false);
    }
}
