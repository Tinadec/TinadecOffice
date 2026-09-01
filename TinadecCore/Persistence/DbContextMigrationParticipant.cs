using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

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
        }
        // Some module control-plane schemas predate provider migration assemblies.
        // The idempotent model bootstrap fills only missing tables while migrations
        // remain authoritative for changes to existing tables.
        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
    }
}

public static class DbContextSchemaBootstrapper
{
    public static async Task EnsureTablesAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        // Column reconciliation must run before the create script: the script's
        // index DDL comes from the current model and can reference columns that
        // a migrations-era table does not have yet, which aborts the whole
        // script before reconciliation ever gets a chance to add them.
        await ReconcileModelColumnsAsync(db, cancellationToken).ConfigureAwait(false);
        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(script)) await db.Database.ExecuteSqlRawAsync(script, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings columns of existing tables up to the current model. The create
    /// script above only adds missing tables, and bootstrap-owned schemas (for
    /// example governance) have no migration history, so columns added to the
    /// model after a database was created would otherwise never reach that
    /// database and fail at runtime with "no column named ..." errors.
    /// Reconciliation is idempotent: freshly created tables already match the
    /// model and produce no statements.
    /// </summary>
    private static async Task ReconcileModelColumnsAsync(DbContext db, CancellationToken cancellationToken)
    {
        var isSqlite = db.Database.IsSqlite();
        var expectedByTable = new Dictionary<(string TableName, string? Schema), List<IProperty>>();
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrEmpty(tableName)) continue;
            var key = (TableName: tableName, Schema: entityType.GetSchema());
            if (!expectedByTable.TryGetValue(key, out var columns))
            {
                expectedByTable[key] = columns = new List<IProperty>();
            }
            var table = StoreObjectIdentifier.Table(tableName, key.Schema);
            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(table);
                if (columnName is not null && columns.All(existing => !string.Equals(existing.GetColumnName(table), columnName, StringComparison.OrdinalIgnoreCase)))
                {
                    columns.Add(property);
                }
            }
        }

        foreach (var entry in expectedByTable)
        {
            var tableName = entry.Key.TableName;
            var actualColumns = await GetTableColumnsAsync(db, tableName, isSqlite, cancellationToken).ConfigureAwait(false);
            if (actualColumns is null) continue;
            var table = StoreObjectIdentifier.Table(tableName, entry.Key.Schema);
            foreach (var property in entry.Value)
            {
                var columnName = property.GetColumnName(table);
                if (columnName is null || actualColumns.Contains(columnName)) continue;
                await AddColumnAsync(db, tableName, columnName, property, isSqlite, cancellationToken).ConfigureAwait(false);
            }
        }

        await DropLegacyCapabilityLeaseNonceAsync(db, isSqlite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-20260822 capability leases persisted nonce material inline in a NOT
    /// NULL <c>capability_leases.nonce</c> column. The current model keeps the
    /// nonce only in the secret store referenced by
    /// <c>nonce_secret_reference</c>; the stale inline column breaks lease
    /// inserts on databases upgraded from that era. Drop it when present.
    /// </summary>
    private static async Task DropLegacyCapabilityLeaseNonceAsync(DbContext db, bool isSqlite, CancellationToken cancellationToken)
    {
        var actualColumns = await GetTableColumnsAsync(db, "capability_leases", isSqlite, cancellationToken).ConfigureAwait(false);
        if (actualColumns is null || !actualColumns.Contains("nonce")) return;
        if (isSqlite)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"capability_leases\" DROP COLUMN \"nonce\"", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE capability_leases DROP COLUMN IF EXISTS nonce", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns the column names of an existing table, or <c>null</c> when the table does not exist.</summary>
    private static async Task<List<string>?> GetTableColumnsAsync(DbContext db, string tableName, bool isSqlite, CancellationToken cancellationToken)
    {
        if (isSqlite)
        {
            var columns = await db.Database
                .SqlQueryRaw<string>("SELECT \"name\" AS \"Value\" FROM pragma_table_info({0})", tableName)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return columns.Count == 0 ? null : columns;
        }
        var postgresColumns = await db.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0}", tableName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return postgresColumns.Count == 0 ? null : postgresColumns;
    }

    private static async Task AddColumnAsync(DbContext db, string tableName, string columnName, IProperty property, bool isSqlite, CancellationToken cancellationToken)
    {
        var columnType = property.GetColumnType() ?? (isSqlite ? "TEXT" : "text");
        var requiredDefault = property.IsNullable ? null : RequiredColumnDefault(property.ClrType);
        var nullability = requiredDefault is null ? string.Empty : $" NOT NULL DEFAULT {requiredDefault}";
        var ddl = isSqlite
            ? $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType}{nullability}"
            : $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType}{nullability}";
        await db.Database.ExecuteSqlRawAsync(ddl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Neutral default so a required column can be added to a table that already holds rows.</summary>
    private static string? RequiredColumnDefault(Type clrType) => Type.GetTypeCode(clrType) switch
    {
        TypeCode.String => "''",
        TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => "0",
        TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "0",
        TypeCode.DateTime => "'0001-01-01 00:00:00'",
        _ => clrType == typeof(Guid) ? "'00000000-0000-0000-0000-000000000000'" : null
    };
}
