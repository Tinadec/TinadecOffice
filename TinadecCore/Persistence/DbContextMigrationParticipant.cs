using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Persistence;

internal interface IDbContextSchemaMigrationParticipant
{
}

/// <summary>Small adapter allowing each business module to participate in coordinated provider migrations.</summary>
public sealed class DbContextMigrationParticipant<TContext> : IStorageMigrationParticipant, IDbContextSchemaMigrationParticipant where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    public DbContextMigrationParticipant(IDbContextFactory<TContext> factory) => _factory = factory;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var batch = DbContextSchemaBootstrapper.TryGetMatchingSqliteBatchSession(db);
        bool hasPending;
        if (batch is not null)
        {
            hasPending = await batch.HasPendingMigrationsAsync(db, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            hasPending = pending.Any();
        }
        if (hasPending)
        {
            if (batch is null || !batch.LockAlreadyCleared)
            {
                await DbContextSchemaBootstrapper.ClearStaleSqliteMigrationsLockAsync(db, cancellationToken).ConfigureAwait(false);
                if (batch is not null) batch.LockAlreadyCleared = true;
            }
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            batch?.Invalidate();
        }
        // Some module control-plane schemas predate provider migration assemblies.
        // The idempotent model bootstrap fills only missing tables while migrations
        // remain authoritative for changes to existing tables.
        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
    }
}

public static class DbContextSchemaBootstrapper
{
    private static readonly AsyncLocal<SqliteBatchSchemaSession?> CurrentSqliteBatch = new();

    internal static IDisposable? BeginSqliteBatchSession(string connectionString, bool lockAlreadyCleared)
    {
        var normalized = NormalizeSqliteDataSource(connectionString);
        if (normalized is null) return null;
        var previous = CurrentSqliteBatch.Value;
        var next = new SqliteBatchSchemaSession(normalized) { LockAlreadyCleared = lockAlreadyCleared };
        CurrentSqliteBatch.Value = next;
        return new BatchSessionScope(previous);
    }

    internal static void InvalidateCurrentSqliteBatchSession() => CurrentSqliteBatch.Value?.Invalidate();

    internal static SqliteBatchSchemaSession? TryGetMatchingSqliteBatchSession(DbContext db)
    {
        var session = CurrentSqliteBatch.Value;
        if (session is null || !db.Database.IsSqlite()) return null;
        var current = NormalizeSqliteDataSource(db.Database.GetConnectionString());
        return string.Equals(current, session.NormalizedDataSource, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? session
            : null;
    }

    private static string? NormalizeSqliteDataSource(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource)
                || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
                || builder.Mode == SqliteOpenMode.Memory)
            {
                return null;
            }
            return Path.GetFullPath(builder.DataSource);
        }
        catch
        {
            return null;
        }
    }

    private sealed class BatchSessionScope(SqliteBatchSchemaSession? previous) : IDisposable
    {
        public void Dispose() => CurrentSqliteBatch.Value = previous;
    }
    /// <summary>
    /// Clears an orphaned <c>__EFMigrationsLock</c> row left behind if a previous single-owner SQLite
    /// host process was terminated mid-migration. Scope single-writer leases (<c>host.lock</c> / scope
    /// lease) already forbid concurrent process migrators on the same SQLite database.
    /// </summary>
    public static async Task ClearStaleSqliteMigrationsLockAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!db.Database.IsSqlite()) return;
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await ClearMigrationsLockOnOpenConnectionAsync(connection, logger: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (closeAfter)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task ClearStaleSqliteMigrationsLockAsync(
        string connectionString,
        int busyTimeoutMs = SqliteConnectionPragmaInterceptor.DefaultBusyTimeoutMs,
        bool enableWal = true,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteConnectionPragmaInterceptor.ApplyPragmasAsync(connection, busyTimeoutMs, enableWal, cancellationToken).ConfigureAwait(false);
        await ClearMigrationsLockOnOpenConnectionAsync(connection, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ClearMigrationsLockOnOpenConnectionAsync(
        System.Data.Common.DbConnection connection,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = '__EFMigrationsLock';";
        var tableExists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
        if (!tableExists) return;

        await using var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM \"__EFMigrationsLock\";";
        var cleared = await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (cleared > 0)
        {
            logger?.LogWarning("Cleared {Count} orphaned SQLite __EFMigrationsLock row(s) from a previously interrupted host process.", cleared);
        }
    }
    public static async Task EnsureTablesAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        // Column reconciliation must run before the create script: the script's
        // index DDL comes from the current model and can reference columns that
        // a migrations-era table does not have yet, which aborts the whole
        // script before reconciliation ever gets a chance to add them.
        await EnsureColumnsAsync(db, cancellationToken).ConfigureAwait(false);
        var batch = TryGetMatchingSqliteBatchSession(db);
        if (db.Database.IsSqlite() && await AllSqliteTablesAndIndexesExistAsync(db, batch, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(script))
        {
            await db.Database.ExecuteSqlRawAsync(script, cancellationToken).ConfigureAwait(false);
            batch?.Invalidate();
        }
    }

    private static async Task<bool> AllSqliteTablesAndIndexesExistAsync(
        DbContext db,
        SqliteBatchSchemaSession? batch,
        CancellationToken cancellationToken)
    {
        HashSet<string> existing;
        if (batch is not null)
        {
            existing = await batch.GetTablesAndIndexesAsync(db, cancellationToken).ConfigureAwait(false);
            if (existing.Count == 0) return false;
        }
        else
        {
            var names = await db.Database
                .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type IN ('table', 'index')")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (names.Count == 0) return false;
            existing = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrEmpty(tableName)) continue;
            if (!existing.Contains(tableName)) return false;

            var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            foreach (var index in entityType.GetIndexes())
            {
                var indexName = index.GetDatabaseName(table);
                if (!string.IsNullOrEmpty(indexName) && !existing.Contains(indexName))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Reconciles only columns of tables that already exist. Callers that can run
    /// before migrations must use this instead of EnsureTablesAsync so a fresh
    /// database still gets its tables and migration history in the normal order.
    /// </summary>
    public static Task EnsureColumnsAsync(DbContext db, CancellationToken cancellationToken = default) =>
        ReconcileModelColumnsAsync(db, cancellationToken);

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
        var batch = isSqlite ? TryGetMatchingSqliteBatchSession(db) : null;
        // One catalog read per migration batch (or per context outside a batch) instead of
        // one round trip per table and per nullable column.
        var catalog = new SqliteColumnCatalog(db, isSqlite, batch);
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
            var actualColumns = await GetTableColumnsAsync(db, catalog, tableName, isSqlite, cancellationToken).ConfigureAwait(false);
            if (actualColumns is null) continue;
            var table = StoreObjectIdentifier.Table(tableName, entry.Key.Schema);
            var addedColumn = false;
            foreach (var property in entry.Value)
            {
                var columnName = property.GetColumnName(table);
                if (columnName is null || actualColumns.Contains(columnName)) continue;
                await AddColumnAsync(db, tableName, columnName, property, isSqlite, cancellationToken).ConfigureAwait(false);
                addedColumn = true;
            }
            if (addedColumn) catalog.Forget();
        }

        await DropLegacyCapabilityLeaseNonceAsync(db, catalog, isSqlite, cancellationToken).ConfigureAwait(false);
        await DropLegacyNotNullColumnsAsync(db, catalog, expectedByTable, isSqlite, cancellationToken).ConfigureAwait(false);
        await RelaxModelNullableColumnsAsync(db, catalog, expectedByTable, isSqlite, cancellationToken).ConfigureAwait(false);
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
        SqliteColumnCatalog catalog,
        Dictionary<(string TableName, string? Schema), List<IProperty>> expectedByTable,
        bool isSqlite,
        CancellationToken cancellationToken)
    {
        foreach (var entry in expectedByTable)
        {
            var tableName = entry.Key.TableName;
            var actualColumns = await GetTableColumnsAsync(db, catalog, tableName, isSqlite, cancellationToken).ConfigureAwait(false);
            if (actualColumns is null) continue;
            var table = StoreObjectIdentifier.Table(tableName, entry.Key.Schema);
            var targets = new List<string>();
            foreach (var property in entry.Value)
            {
                if (!property.IsNullable) continue;
                var columnName = property.GetColumnName(table);
                if (columnName is null || !actualColumns.Contains(columnName)) continue;
                if (await IsNotNullColumnAsync(db, catalog, tableName, columnName, isSqlite, cancellationToken).ConfigureAwait(false))
                    targets.Add(columnName);
            }
            if (targets.Count == 0) continue;
            if (isSqlite)
            {
                await RebuildSqliteTableRelaxingColumnsAsync(db, tableName, targets, cancellationToken).ConfigureAwait(false);
                catalog.Forget();
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
    private static async Task DropLegacyCapabilityLeaseNonceAsync(DbContext db, SqliteColumnCatalog catalog, bool isSqlite, CancellationToken cancellationToken)
    {
        var actualColumns = await GetTableColumnsAsync(db, catalog, "capability_leases", isSqlite, cancellationToken).ConfigureAwait(false);
        if (actualColumns is null || !actualColumns.Contains("nonce")) return;
        if (isSqlite)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"capability_leases\" DROP COLUMN \"nonce\"", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE capability_leases DROP COLUMN IF EXISTS nonce", cancellationToken).ConfigureAwait(false);
        }
        // The generic NOT NULL sweep that runs next reads through the same catalog, so it has to
        // see the table as it is now; otherwise it drops "nonce" a second time and the whole
        // bootstrap fails with "no such column: \"nonce\"".
        catalog.Forget();
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
        SqliteColumnCatalog catalog,
        Dictionary<(string TableName, string? Schema), List<IProperty>> expectedByTable,
        bool isSqlite,
        CancellationToken cancellationToken)
    {
        foreach (var entry in expectedByTable)
        {
            var tableName = entry.Key.TableName;
            var actualColumns = await GetTableColumnsAsync(db, catalog, tableName, isSqlite, cancellationToken).ConfigureAwait(false);
            if (actualColumns is null) continue;
            var mapped = entry.Value
                .Select(property => property.GetColumnName(StoreObjectIdentifier.Table(tableName, entry.Key.Schema)))
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var column in actualColumns.ToList())
            {
                if (mapped.Contains(column)) continue;
                var notNull = await IsNotNullColumnAsync(db, catalog, tableName, column, isSqlite, cancellationToken).ConfigureAwait(false);
                if (!notNull) continue; // nullable legacy columns are inert; keep them
                await DropLegacyColumnAsync(db, catalog, tableName, column, isSqlite, cancellationToken).ConfigureAwait(false);
                actualColumns.Remove(column);
            }
        }
    }

    private static async Task<bool> IsNotNullColumnAsync(DbContext db, SqliteColumnCatalog catalog, string tableName, string columnName, bool isSqlite, CancellationToken cancellationToken)
    {
        if (isSqlite)
        {
            var notNull = await catalog.IsNotNullAsync(tableName, columnName, cancellationToken).ConfigureAwait(false);
            return notNull == true;
        }
        var pgNullable = await db.Database
            .SqlQueryRaw<string>("SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0} AND column_name = {1}", tableName, columnName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return pgNullable.Count > 0 && string.Equals(pgNullable[0], "NO", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DropLegacyColumnAsync(DbContext db, SqliteColumnCatalog catalog, string tableName, string columnName, bool isSqlite, CancellationToken cancellationToken)
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
        catalog.Forget();
    }

    /// <summary>Returns the column names of an existing table, or <c>null</c> when the table does not exist.</summary>
    private static async Task<List<string>?> GetTableColumnsAsync(DbContext db, SqliteColumnCatalog catalog, string tableName, bool isSqlite, CancellationToken cancellationToken)
    {
        if (isSqlite)
        {
            return await catalog.ColumnsAsync(tableName, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// One SQLite catalog read per context. <c>pragma_table_info</c> is a table-valued
    /// function, so a single query returns every table's columns; the per-table and
    /// per-column shape it replaces cost one round trip each (698 measured while
    /// mounting one real project scope, ~5.8 s). Results are memoized because a
    /// reconciliation only reads, and the create script that follows may add
    /// tables/columns that are never queried again through this object.
    /// </summary>
    internal sealed class SqliteBatchSchemaSession(string normalizedDataSource)
    {
        private readonly HashSet<string> _objects = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _appliedMigrations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, bool>> _columns = new(StringComparer.OrdinalIgnoreCase);
        private Task? _loaded;

        public string NormalizedDataSource { get; } = normalizedDataSource;
        public bool LockAlreadyCleared { get; set; }

        public async Task<bool> HasPendingMigrationsAsync(DbContext db, CancellationToken cancellationToken)
        {
            await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);
            var defined = db.Database.GetMigrations();
            foreach (var id in defined)
            {
                if (!_appliedMigrations.Contains(id))
                    return true;
            }
            return false;
        }

        public async Task<HashSet<string>> GetTablesAndIndexesAsync(DbContext db, CancellationToken cancellationToken)
        {
            await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);
            return _objects;
        }

        public async Task<Dictionary<string, Dictionary<string, bool>>> GetColumnsAsync(DbContext db, CancellationToken cancellationToken)
        {
            await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);
            return _columns;
        }

        public void Invalidate()
        {
            _loaded = null;
            _objects.Clear();
            _appliedMigrations.Clear();
            _columns.Clear();
        }

        private async Task EnsureLoadedAsync(DbContext db, CancellationToken cancellationToken)
        {
            if (_loaded is null) _loaded = LoadAsync(db, cancellationToken);
            try
            {
                await _loaded.ConfigureAwait(false);
            }
            catch
            {
                Invalidate();
                throw;
            }
        }

        private async Task LoadAsync(DbContext db, CancellationToken cancellationToken)
        {
            _objects.Clear();
            _appliedMigrations.Clear();
            _columns.Clear();

            var objects = await db.Database
                .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type IN ('table', 'index')")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var name in objects)
            {
                _objects.Add(name);
            }

            if (_objects.Contains("__EFMigrationsHistory"))
            {
                var applied = await db.Database
                    .SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM \"__EFMigrationsHistory\"")
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var id in applied)
                {
                    _appliedMigrations.Add(id);
                }
            }

            if (_objects.Count > 0)
            {
                var rows = await db.Database
                    .SqlQueryRaw<string>("SELECT m.name || char(31) || p.name || char(31) || p.\"notnull\" AS \"Value\" FROM sqlite_master AS m JOIN pragma_table_info(m.name) AS p WHERE m.type = 'table'")
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var parts = row.Split('\u001f');
                    if (parts.Length < 3) continue;
                    if (!_columns.TryGetValue(parts[0], out var columns))
                        _columns[parts[0]] = columns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    columns[parts[1]] = parts[2] == "1";
                }
            }
        }
    }

    private sealed class SqliteColumnCatalog(DbContext db, bool isSqlite, SqliteBatchSchemaSession? batch = null)
    {
        private readonly Dictionary<string, Dictionary<string, bool>> _tables = new(StringComparer.OrdinalIgnoreCase);
        private Task? _loaded;

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            var rows = await db.Database
                .SqlQueryRaw<string>("SELECT m.name || char(31) || p.name || char(31) || p.\"notnull\" AS \"Value\" FROM sqlite_master AS m JOIN pragma_table_info(m.name) AS p WHERE m.type = 'table'")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var parts = row.Split('\u001f');
                if (parts.Length < 3) continue;
                if (!_tables.TryGetValue(parts[0], out var columns))
                    _tables[parts[0]] = columns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                columns[parts[1]] = parts[2] == "1";
            }
        }

        private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (batch is not null) return;
            if (_loaded is null) _loaded = LoadAsync(cancellationToken);
            await _loaded.ConfigureAwait(false);
        }

        /// <summary>Column names of an existing table, or null when the table does not exist.</summary>
        public async Task<List<string>?> ColumnsAsync(string tableName, CancellationToken cancellationToken)
        {
            if (!isSqlite) throw new InvalidOperationException("The SQLite column catalog is only valid for a SQLite database.");
            if (batch is not null)
            {
                var tables = await batch.GetColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                return tables.TryGetValue(tableName, out var batchCols) && batchCols.Count > 0 ? [.. batchCols.Keys] : null;
            }
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _tables.TryGetValue(tableName, out var columns) && columns.Count > 0 ? [.. columns.Keys] : null;
        }

        /// <summary>Whether the column is declared NOT NULL; null when the table or column is absent.</summary>
        public async Task<bool?> IsNotNullAsync(string tableName, string columnName, CancellationToken cancellationToken)
        {
            if (!isSqlite) throw new InvalidOperationException("The SQLite column catalog is only valid for a SQLite database.");
            if (batch is not null)
            {
                var tables = await batch.GetColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                return tables.TryGetValue(tableName, out var batchCols) && batchCols.TryGetValue(columnName, out var batchNotNull) ? batchNotNull : null;
            }
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _tables.TryGetValue(tableName, out var columns) && columns.TryGetValue(columnName, out var notNull) ? notNull : null;
        }

        /// <summary>
        /// Invalidates the memoized catalog after a statement changed a table's shape, so the next
        /// read reloads it from the database. Reconciliation is otherwise read-only, but it does
        /// drop legacy columns, and a later pass that still saw a dropped column issued a second
        /// DROP COLUMN for it and failed the whole bootstrap with SQLite's "no such column".
        /// </summary>
        public void Forget()
        {
            if (!isSqlite) return;
            batch?.Invalidate();
            _tables.Clear();
            _loaded = null;
        }
    }
}
