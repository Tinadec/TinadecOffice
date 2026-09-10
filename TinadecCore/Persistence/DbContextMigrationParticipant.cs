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
        await DropLegacyNotNullColumnsAsync(db, expectedByTable, isSqlite, cancellationToken).ConfigureAwait(false);
        await RelaxModelNullableColumnsAsync(db, expectedByTable, isSqlite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Columns that stay mapped but became nullable in the model (for example
    /// <c>sessions.project_id</c> when free conversations made the session/project
    /// binding optional) keep their old NOT NULL constraint on upgraded databases
    /// and reject NULL inserts forever. Relax them to match the model. PostgreSQL
    /// alters the column in place; SQLite rebuilds the table (nullability cannot
    /// be altered), preserving every live column and letting the create script
    /// that follows reconciliation recreate the model indexes.
    /// </summary>
    private static async Task RelaxModelNullableColumnsAsync(
        DbContext db,
        Dictionary<(string TableName, string? Schema), List<IProperty>> expectedByTable,
        bool isSqlite,
        CancellationToken cancellationToken)
    {
        foreach (var entry in expectedByTable)
        {
            var tableName = entry.Key.TableName;
            var actualColumns = await GetTableColumnsAsync(db, tableName, isSqlite, cancellationToken).ConfigureAwait(false);
            if (actualColumns is null) continue;
            var table = StoreObjectIdentifier.Table(tableName, entry.Key.Schema);
            var targets = new List<string>();
            foreach (var property in entry.Value)
            {
                if (!property.IsNullable) continue;
                var columnName = property.GetColumnName(table);
                if (columnName is null || !actualColumns.Contains(columnName)) continue;
                if (await IsNotNullColumnAsync(db, tableName, columnName, isSqlite, cancellationToken).ConfigureAwait(false))
                    targets.Add(columnName);
            }
            if (targets.Count == 0) continue;
            if (isSqlite)
            {
                await RebuildSqliteTableRelaxingColumnsAsync(db, tableName, targets, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var column in targets)
                {
                    await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{tableName}\" ALTER COLUMN \"{column}\" DROP NOT NULL", cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task RebuildSqliteTableRelaxingColumnsAsync(DbContext db, string tableName, IReadOnlyCollection<string> relaxColumns, CancellationToken cancellationToken)
    {
        // char(31) separates fields so defaults containing ordinary punctuation survive the round-trip.
        var rows = await db.Database
            .SqlQueryRaw<string>("SELECT \"name\" || char(31) || \"type\" || char(31) || \"notnull\" || char(31) || IFNULL(\"dflt_value\", '') || char(31) || \"pk\" AS \"Value\" FROM pragma_table_info({0})", tableName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return;
        var definitions = new List<string>();
        var names = new List<string>();
        // pragma_table_info.pk is the 1-based ordinal of the column inside the
        // primary key (0 when the column is not part of it), not a boolean. A
        // composite key reports pk = 1, 2, ...; reading it as a boolean silently
        // dropped every key column after the first from the rebuilt table.
        var primaryKey = new List<(int Ordinal, string Name)>();
        foreach (var row in rows)
        {
            var parts = row.Split('\u001f');
            var name = parts[0];
            var type = string.IsNullOrWhiteSpace(parts[1]) ? "TEXT" : parts[1];
            var keepNotNull = parts[2] == "1" && !relaxColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
            var defaultValue = parts[3];
            var pkOrdinal = parts.Length > 4 && int.TryParse(parts[4], out var parsedOrdinal) ? parsedOrdinal : 0;
            names.Add(name);
            definitions.Add($"\"{name}\" {type}{(keepNotNull ? " NOT NULL" : "")}{(string.IsNullOrWhiteSpace(defaultValue) ? "" : $" DEFAULT {defaultValue}")}");
            if (pkOrdinal > 0) primaryKey.Add((pkOrdinal, name));
        }
        if (primaryKey.Count > 0)
        {
            // A table-level constraint preserves single and composite keys alike;
            // SQLite still treats a lone INTEGER PRIMARY KEY as a rowid alias.
            var ordered = primaryKey.OrderBy(entry => entry.Ordinal).Select(entry => $"\"{entry.Name}\"");
            definitions.Add($"PRIMARY KEY ({string.Join(", ", ordered)})");
        }
        var temp = tableName + "_relax";
        var columnList = string.Join(", ", names.Select(name => $"\"{name}\""));
        var script = string.Join(";",
            "PRAGMA foreign_keys=off",
            $"CREATE TABLE \"{temp}\" ({string.Join(", ", definitions)})",
            $"INSERT INTO \"{temp}\" ({columnList}) SELECT {columnList} FROM \"{tableName}\"",
            $"DROP TABLE \"{tableName}\"",
            $"ALTER TABLE \"{temp}\" RENAME TO \"{tableName}\"",
            "PRAGMA foreign_keys=on");
        await db.Database.ExecuteSqlRawAsync(script, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Drops columns that a previous schema declared NOT NULL but the current
    /// model no longer maps (for example <c>model_route_versions.provider_id</c>,
    /// superseded by <c>model_route_candidates</c>). EF inserts never populate
    /// such a column, so on upgraded databases every insert dies with a NOT NULL
    /// constraint violation. Nullable legacy columns are inert and kept; SQLite
    /// columns still referenced by an index are left in place because dropping
    /// them would require a table rebuild this bootstrap does not attempt.
    /// </summary>
    private static async Task DropLegacyNotNullColumnsAsync(
        DbContext db,
        Dictionary<(string TableName, string? Schema), List<IProperty>> expectedByTable,
        bool isSqlite,
        CancellationToken cancellationToken)
    {
        foreach (var entry in expectedByTable)
        {
            var tableName = entry.Key.TableName;
            var actualColumns = await GetTableColumnsAsync(db, tableName, isSqlite, cancellationToken).ConfigureAwait(false);
            if (actualColumns is null) continue;
            var mapped = entry.Value
                .Select(property => property.GetColumnName(StoreObjectIdentifier.Table(tableName, entry.Key.Schema)))
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var column in actualColumns.ToList())
            {
                if (mapped.Contains(column)) continue;
                var notNull = await IsNotNullColumnAsync(db, tableName, column, isSqlite, cancellationToken).ConfigureAwait(false);
                if (!notNull) continue; // nullable legacy columns are inert; keep them
                await DropLegacyColumnAsync(db, tableName, column, isSqlite, cancellationToken).ConfigureAwait(false);
                actualColumns.Remove(column);
            }
        }
    }

    private static async Task<bool> IsNotNullColumnAsync(DbContext db, string tableName, string columnName, bool isSqlite, CancellationToken cancellationToken)
    {
        if (isSqlite)
        {
            var notNull = await db.Database
                .SqlQueryRaw<int>("SELECT \"notnull\" AS \"Value\" FROM pragma_table_info({0}) WHERE \"name\" = {1}", tableName, columnName)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return notNull.Count > 0 && notNull[0] == 1;
        }
        var pgNullable = await db.Database
            .SqlQueryRaw<string>("SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0} AND column_name = {1}", tableName, columnName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return pgNullable.Count > 0 && string.Equals(pgNullable[0], "NO", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DropLegacyColumnAsync(DbContext db, string tableName, string columnName, bool isSqlite, CancellationToken cancellationToken)
    {
        if (isSqlite)
        {
            // SQLite refuses DROP COLUMN when any index spans the column. That
            // case needs a full table rebuild, which this idempotent bootstrap
            // deliberately does not attempt; the error message names the table
            // so the database can be repaired manually.
            var indexes = await db.Database
                .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_index_list({0})", tableName)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var indexName in indexes)
            {
                var indexInfo = await db.Database
                    .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_index_info({0})", indexName)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (indexInfo.Any(c => string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase))) return;
            }
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{tableName}\" DROP COLUMN \"{columnName}\"", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {tableName} DROP COLUMN IF EXISTS {columnName}", cancellationToken).ConfigureAwait(false);
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
