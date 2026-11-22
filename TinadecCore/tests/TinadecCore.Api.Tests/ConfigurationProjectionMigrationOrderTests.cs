using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Regression for the 2026-10-21 RouterAPI mount failure. The configuration
/// projection read runs before the coordinated migrations, and reconciling
/// columns from the current model there added <c>i18n_json</c> to the five agent
/// pack tables while leaving <c>202610210001_AgentPackI18n</c> unrecorded. The
/// migration is a bare <c>alter table ... add column</c> (SQLite has no
/// <c>ADD COLUMN IF NOT EXISTS</c>), so every later mount died on
/// "duplicate column name: i18n_json" and the scope never opened again.
/// </summary>
public sealed class ConfigurationProjectionMigrationOrderTests
{
    private const string MigrationsAssembly = "TinadecCore.Storage.Migrations.Sqlite";
    private const string I18nMigration = "202610210001_AgentPackI18n";
    private static readonly string[] I18nTables =
        ["agent_definitions", "agent_modes", "mode_nodes", "prompt_pipelines", "prompt_versions"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-projection-order", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProjectionReadOnLegacyDatabaseAppliesPendingMigrationsInsteadOfPreAddingTheirColumns()
    {
        var databasePath = await CreateLegacyDatabaseAsync();

        // The upgraded database: migrations stop right before the i18n batch.
        Assert.False(await HasColumnAsync(databasePath, "agent_definitions", "i18n_json"));
        Assert.False(await HasMigrationAsync(databasePath, I18nMigration));

        await using var services = Build(databasePath);
        var factory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();

        // Production path: the projection wrapper attaches (migrations first, then
        // the additive reconciliation) before the caller ever sees the context.
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Empty(await db.AgentDefinitions.ToListAsync());

        // The migration must have run and recorded itself; the column must be the
        // migration's, not an unrecorded pre-add that poisons every later mount.
        Assert.True(await HasMigrationAsync(databasePath, I18nMigration));
        Assert.True(await HasColumnAsync(databasePath, "agent_definitions", "i18n_json"));
        Assert.True(await HasColumnAsync(databasePath, "prompt_versions", "i18n_json"));

        // A second open reproduces the failure mode when the order is wrong: it
        // would try to apply the i18n migration again against an existing column.
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Empty(await db.AgentDefinitions.ToListAsync());
    }

    [Fact]
    public async Task OrphanedSqliteEfMigrationsLockIsClearedBeforeMigrationSoStartupDoesNotHang()
    {
        var databasePath = await CreateLegacyDatabaseAsync();
        await using (var conn = new SqliteConnection($"Data Source={databasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsLock" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
                    "Timestamp" TEXT NOT NULL
                );
                INSERT OR REPLACE INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, '2026-11-22T00:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var services = Build(databasePath);
        var participant = new DbContextMigrationParticipant<AgentConfigurationDbContext>(
            services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>());
        await participant.MigrateAsync(cts.Token);

        Assert.True(await HasMigrationAsync(databasePath, I18nMigration));
        await using (var conn = new SqliteConnection($"Data Source={databasePath}"))
        {
            await conn.OpenAsync(cts.Token);
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsLock\";";
            Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync(cts.Token)));
        }
    }

    private async Task<string> CreateLegacyDatabaseAsync()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "storage.toml"), "[storage]\nbackend = \"sqlite\"\n");
        await File.WriteAllTextAsync(Path.Combine(config, "logging.toml"), "[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");

        var databasePath = Path.Combine(_root, "data", "test.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = new DbContextOptionsBuilder<AgentConfigurationDbContext>()
            .UseSqlite($"Data Source={databasePath}", builder => builder.MigrationsAssembly(MigrationsAssembly))
            .Options;
        await using var db = new AgentConfigurationDbContext(options);
        await db.Database.MigrateAsync();

        // Rewind to the upgraded-database state RouterAPI was in: the five i18n
        // columns are gone and their migration is still pending. Building the state
        // backwards keeps the test independent of target-migration bookkeeping.
        foreach (var table in I18nTables)
            await db.Database.ExecuteSqlRawAsync($"alter table {table} drop column i18n_json");
        await db.Database.ExecuteSqlRawAsync($"delete from __EFMigrationsHistory where MigrationId = '{I18nMigration}'");
        return databasePath;
    }

    private ServiceProvider Build(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScopeStorageLocations>(new StorageScopeDescriptor("project", "project", _root));
        services.AddSingleton<ITenantContextAccessor>(new FixedTenantAccessor());
        services.AddSingleton<IContentStore>(new TempContentStore(_root));
        services.AddDbContextFactory<AgentConfigurationDbContext>(options => options
            .UseSqlite($"Data Source={databasePath}", builder => builder.MigrationsAssembly(MigrationsAssembly)));
        services.AddTinadecConfigurationFiles();
        return services.BuildServiceProvider();
    }

    private static async Task<bool> HasColumnAsync(string databasePath, string table, string column)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> HasMigrationAsync(string databasePath, string migrationId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = $id";
        command.Parameters.AddWithValue("$id", migrationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private sealed class FixedTenantAccessor : ITenantContextAccessor
    {
        private static readonly TenantContext Actor = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");
        public TenantContext Current => Actor;
    }

    private sealed class TempContentStore(string root) : IContentStore
    {
        public async Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var relative = Path.Combine("content", request.Kind.ToString(), hash);
            var absolute = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllBytesAsync(absolute, bytes, cancellationToken);
            return new(relative.Replace('\\', '/'), hash, bytes.Length, request.MediaType);
        }

        public Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(File.OpenRead(Path.Combine(root, reference.Value.Replace('/', Path.DirectorySeparatorChar))));

        public Task<bool> ExistsAsync(ContentReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(File.Exists(Path.Combine(root, reference.Value.Replace('/', Path.DirectorySeparatorChar))));

        public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken = default)
        {
            File.Delete(Path.Combine(root, reference.Value.Replace('/', Path.DirectorySeparatorChar)));
            return Task.CompletedTask;
        }
    }
}
