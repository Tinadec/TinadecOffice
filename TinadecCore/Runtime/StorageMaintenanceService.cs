using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Abstractions;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

public sealed record StorageCategoryStatistics(string Category, string Path, long SizeBytes, long FileCount, bool Clearable);
public sealed record StorageStatistics(string StorageId, IReadOnlyList<StorageCategoryStatistics> Categories, IReadOnlyList<ConfigurationDiagnostic> Diagnostics);
public sealed record StorageCleanupPreview(string PreviewId, string StorageId, string Category, string Path, long FileCount, long SizeBytes, DateTimeOffset ExpiresAt);

/// <summary>Only owned, explicitly selected categories can be cleared; source files and content are never cache.</summary>
public sealed class StorageMaintenanceService(IStorageScopeRegistry registry)
{
    private readonly ConcurrentDictionary<string, Cleanup> _previews = new(StringComparer.Ordinal);

    /// <summary>
    /// Last computed usage per scope, kept for a short window.
    /// </summary>
    /// <remarks>
    /// Enumerating the owned tree is the expensive part of this service, and the number it produces
    /// is a summary: it does not have to be exact to the second. Without this, moving between scopes
    /// and back paid the full walk every time — seconds of an empty panel, repeatedly. Cleanup and
    /// collection invalidate the entry below, so a change the reader just made is never masked.
    /// </remarks>
    private readonly ConcurrentDictionary<string, (StorageStatistics Statistics, DateTimeOffset At)> _statistics = new(StringComparer.Ordinal);
    private static readonly TimeSpan StatisticsTtl = TimeSpan.FromSeconds(30);

    /// <summary>Forget a scope's cached usage, because something the reader just did changed it.</summary>
    public void InvalidateStatistics(string storageId)
    {
        // Every root that id has been measured at, not just the current one: the caller knows the id,
        // and a stale entry for an abandoned root would otherwise never be reclaimed.
        foreach (var key in _statistics.Keys.Where(key => key.StartsWith(storageId + "|", StringComparison.Ordinal)).ToList())
            _statistics.TryRemove(key, out _);
    }

    /// <summary>
    /// The cache is keyed by storage id **and by the root it measured**.
    /// </summary>
    /// <remarks>
    /// Reconfiguring a scope keeps its storage id while changing where it reads and writes. Keyed by
    /// id alone, the first lookup after such a change returned the previous root's category paths and
    /// sizes for up to the TTL — a summary of a directory that is no longer this scope. The root is
    /// part of the answer, so it is part of the key.
    /// </remarks>
    private static string StatisticsKey(StorageScopeDescriptor scope) => scope.StorageId + "|" + scope.Root;

    public Task<StorageStatistics> StatisticsAsync(StorageScopeDescriptor scope, CancellationToken ct)
    {
        var key = StatisticsKey(scope);
        if (_statistics.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < StatisticsTtl)
            return Task.FromResult(cached.Statistics);
        var diagnostics = new List<ConfigurationDiagnostic>();
        var categories = new List<StorageCategoryStatistics>();
        foreach (var (category, path) in scope.Categories)
        {
            try
            {
                var files = OwnedFiles(scope.Root, path, ct);
                categories.Add(new(category, path, files.Sum(x => x.Length), files.Count, category is "cache" or "temp" or "logs"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { diagnostics.Add(new("storage_unreadable", path + ": " + ex.Message)); }
        }
        var statistics = new StorageStatistics(scope.StorageId, categories, diagnostics);
        _statistics[key] = (statistics, DateTimeOffset.UtcNow);
        return Task.FromResult(statistics);
    }

    public async Task<StorageCleanupPreview> PreviewAsync(string storageId, string category, CancellationToken ct)
    {
        if (category is not ("cache" or "temp" or "logs")) throw new ArgumentException("Only cache, temp and rotated logs are clearable categories.");
        await using var lease = await registry.AcquireExclusiveAsync(storageId, ct).ConfigureAwait(false);
        using var contentLease = lease.Services.GetRequiredService<IContentLeaseRegistry>().AcquireMaintenance();
        await RequireIdleAsync(lease.Services, ct).ConfigureAwait(false);
        var root = lease.Descriptor.Root;
        var path = StorageScopePaths.Contained(root, category);
        var files = OwnedFiles(root, path, ct).Where(x => category != "logs" || ScopeDiagnosticLoggerProvider.IsRotatedFile(x.Name)).ToList();
        var preview = new StorageCleanupPreview(Guid.NewGuid().ToString("N"), storageId, category, path, files.Count,
            files.Sum(x => x.Length), DateTimeOffset.UtcNow.AddMinutes(5));
        _previews[preview.PreviewId] = new(preview, Fingerprint(files), files.Select(x => x.FullName).ToArray());
        foreach (var expired in _previews.Where(x => x.Value.Preview.ExpiresAt < DateTimeOffset.UtcNow)) _previews.TryRemove(expired.Key, out _);
        return preview;
    }

    public async Task<StorageCleanupPreview> ApplyAsync(string storageId, string previewId, CancellationToken ct)
    {
        if (!_previews.TryRemove(previewId, out var cleanup) || cleanup.Preview.StorageId != storageId || cleanup.Preview.ExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Cleanup preview is missing, expired or belongs to another scope.");
        await using var lease = await registry.AcquireExclusiveAsync(storageId, ct).ConfigureAwait(false);
        using var contentLease = lease.Services.GetRequiredService<IContentLeaseRegistry>().AcquireMaintenance();
        await RequireIdleAsync(lease.Services, ct).ConfigureAwait(false);
        var files = OwnedFiles(lease.Descriptor.Root, cleanup.Preview.Path, ct)
            .Where(x => cleanup.Preview.Category != "logs" || ScopeDiagnosticLoggerProvider.IsRotatedFile(x.Name)).ToList();
        if (Fingerprint(files) != cleanup.Fingerprint) throw new InvalidOperationException("Storage changed after the preview. Refresh before clearing it.");
        foreach (var file in cleanup.Files)
        {
            ct.ThrowIfCancellationRequested();
            StorageScopePaths.RejectLinks(lease.Descriptor.Root, file);
            File.Delete(file);
        }
        // The cached usage describes a tree that no longer exists.
        InvalidateStatistics(storageId);
        return cleanup.Preview;
    }

    public async Task<string> ExportAsync(string storageId, CancellationToken ct)
    {
        await using var lease = await registry.AcquireExclusiveAsync(storageId, ct).ConfigureAwait(false);
        using var contentLease = lease.Services.GetRequiredService<IContentLeaseRegistry>().AcquireMaintenance();
        await RequireIdleAsync(lease.Services, ct).ConfigureAwait(false);
        var scope = lease.Descriptor;
        Directory.CreateDirectory(scope.Temp);
        var destination = Path.Combine(scope.Temp, "export-" + Guid.NewGuid().ToString("N") + ".zip");
        var databaseSnapshot = Path.Combine(scope.Temp, "database-" + Guid.NewGuid().ToString("N") + (scope.Backend == "sqlite" ? ".db" : ".dump"));
        try
        {
            var connection = lease.Services.GetRequiredService<IDatabaseConnectionInfo>();
            if (scope.Backend == "sqlite")
            {
                await using var source = new Microsoft.Data.Sqlite.SqliteConnection(connection.ConnectionString);
                await using var target = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databaseSnapshot, Pooling = false }.ToString());
                await source.OpenAsync(ct).ConfigureAwait(false); await target.OpenAsync(ct).ConfigureAwait(false);
                source.BackupDatabase(target);
            }
            else
            {
                var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connection.ConnectionString);
                var schema = lease.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions>>().Value.PostgreSql.Schema;
                if (schema is null || !Regex.IsMatch(schema, "^tinadec_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("PostgreSQL export requires this storage scope's host-minted schema.");
                var start = new ProcessStartInfo("pg_dump") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
                foreach (var argument in new[] { "--format=custom", "--no-owner", "--schema=" + schema, "--file=" + databaseSnapshot }) start.ArgumentList.Add(argument);
                start.Environment["PGHOST"] = parsed.Host; start.Environment["PGPORT"] = parsed.Port.ToString(); start.Environment["PGDATABASE"] = parsed.Database;
                start.Environment["PGUSER"] = parsed.Username; start.Environment["PGPASSWORD"] = parsed.Password;
                start.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");
                Process launched;
                try { launched = Process.Start(start) ?? throw new InvalidOperationException("pg_dump could not start."); }
                catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("PostgreSQL export requires the external pg_dump program on this host. Install it explicitly before exporting.", ex); }
                using var process = launched;
                var error = process.StandardError.ReadToEndAsync(ct); var output = process.StandardOutput.ReadToEndAsync(ct);
                try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                await output.ConfigureAwait(false); var diagnostic = await error.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("PostgreSQL export failed: " + (string.IsNullOrEmpty(parsed.Password) ? diagnostic : diagnostic.Replace(parsed.Password, "[redacted]", StringComparison.Ordinal)));
            }
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            foreach (var category in new[] { "config", "skills", "data", "packages" })
            {
                foreach (var file in OwnedFiles(scope.Root, Path.Combine(scope.Root, category), ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (file.FullName.EndsWith("tinadec.db", StringComparison.OrdinalIgnoreCase) || file.FullName.EndsWith("tinadec.db-wal", StringComparison.OrdinalIgnoreCase)
                        || file.FullName.EndsWith("tinadec.db-shm", StringComparison.OrdinalIgnoreCase)) continue;
                    if (category == "data" && file.FullName.StartsWith(Path.Combine(scope.Data, "vectors") + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        if (file.Name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Extension == ".db")
                        {
                            var snapshot = Path.Combine(scope.Temp, "vector-backup-" + Guid.NewGuid().ToString("N") + ".db");
                            try
                            {
                                await using (var sourceVector = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                                    { DataSource = file.FullName, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                                await using (var targetVector = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                                    { DataSource = snapshot, Pooling = false }.ToString()))
                                {
                                    await sourceVector.OpenAsync(ct).ConfigureAwait(false); await targetVector.OpenAsync(ct).ConfigureAwait(false);
                                    sourceVector.BackupDatabase(targetVector);
                                }
                                archive.CreateEntryFromFile(snapshot, Path.GetRelativePath(scope.Root, file.FullName).Replace('\\', '/'));
                            }
                            finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
                            continue;
                        }
                    }
                    archive.CreateEntryFromFile(file.FullName, Path.GetRelativePath(scope.Root, file.FullName).Replace('\\', '/'));
                }
            }
            if (scope.ProjectRoot is not null) archive.CreateEntryFromFile(Path.Combine(scope.Root, "project.toml"), "project.toml");
            archive.CreateEntryFromFile(databaseSnapshot, scope.Backend == "sqlite" ? "data/tinadec.db" : "data/postgresql.dump");
            return destination;
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
        finally { if (File.Exists(databaseSnapshot)) File.Delete(databaseSnapshot); }
    }

    public static async Task RequireIdleAsync(IServiceProvider services, CancellationToken ct)
    {
        if (services.GetService<IContentLeaseRegistry>() is { Count: > 0 })
            throw new InvalidOperationException("Content streams still hold storage leases. Close them before maintenance.");
        await using var db = await services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync(ct).ConfigureAwait(false);
        var statuses = await db.Runs.AsNoTracking().Select(x => x.Status).ToListAsync(ct).ConfigureAwait(false);
        if (statuses.Any(x => !RunStatusMachine.IsTerminal(x))) throw new InvalidOperationException("Active runs hold storage leases. Finish or stop them before this action.");
        if (await db.UserToolActions.AnyAsync(x => x.Status == "executing", ct).ConfigureAwait(false))
            throw new InvalidOperationException("A resource installation or user tool action is still executing.");
    }

    public async Task<StorageCleanupPreview> PreviewDeleteAsync(string storageId, CancellationToken ct)
    {
        await using var lease = await registry.AcquireExclusiveAsync(storageId, ct).ConfigureAwait(false);
        if (lease.Descriptor.ScopeKind != "project") throw new ArgumentException("Only project storage can be deleted here.");
        await RequireIdleAsync(lease.Services, ct).ConfigureAwait(false);
        await RequireNoDatabaseExtensionsAsync(lease, ct).ConfigureAwait(false);
        var files = OwnedFiles(lease.Descriptor.Root, lease.Descriptor.Root, ct);
        var preview = new StorageCleanupPreview(Guid.NewGuid().ToString("N"), storageId, "project_storage", lease.Descriptor.Root,
            files.Count, files.Sum(x => x.Length), DateTimeOffset.UtcNow.AddMinutes(5));
        _previews[preview.PreviewId] = new(preview, await DeletionFingerprintAsync(lease, files, ct).ConfigureAwait(false), files.Select(x => x.FullName).ToArray());
        return preview;
    }

    public async Task<StorageCleanupPreview> DeleteStorageAsync(string storageId, string previewId, CancellationToken ct)
    {
        if (!_previews.TryRemove(previewId, out var cleanup) || cleanup.Preview.StorageId != storageId
            || cleanup.Preview.Category != "project_storage" || cleanup.Preview.ExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Project deletion requires its own current preview.");
        await using var exclusive = await registry.AcquireExclusiveAsync(storageId, ct).ConfigureAwait(false);
        using var contentLease = exclusive.Services.GetRequiredService<IContentLeaseRegistry>().AcquireMaintenance();
        await RequireIdleAsync(exclusive.Services, ct).ConfigureAwait(false);
        await RequireNoDatabaseExtensionsAsync(exclusive, ct).ConfigureAwait(false);
        var descriptor = exclusive.Descriptor;
        var connection = exclusive.Services.GetRequiredService<IDatabaseConnectionInfo>();
        var files = OwnedFiles(descriptor.Root, descriptor.Root, ct);
        if (await DeletionFingerprintAsync(exclusive, files, ct).ConfigureAwait(false) != cleanup.Fingerprint) throw new InvalidOperationException("Project storage changed after the preview; refresh it before deleting.");
        if (registry is not StorageScopeRegistry ownedRegistry) throw new InvalidOperationException("The host cannot close storage for deletion.");
        await ownedRegistry.CloseForMaintenanceAsync(exclusive, ct).ConfigureAwait(false);
        StorageScopePaths.RejectLinks(descriptor.Root);
        var staging = descriptor.Root + ".deleting-" + Guid.NewGuid().ToString("N");
        Directory.Move(descriptor.Root, staging);
        try
        {
            _ = OwnedFiles(staging, staging, ct);
            if (descriptor.Backend == "postgresql")
            {
                // Only the host-created project schema is owned by this operation.
                // The server, database and any other schemas remain external resources.
                var schema = "tinadec_" + descriptor.StorageId;
                if (!Guid.TryParseExact(descriptor.StorageId, "N", out _)) throw new InvalidOperationException("Invalid project storage schema identity.");
                await using var database = new Npgsql.NpgsqlConnection(connection.ConnectionString);
                await database.OpenAsync(ct).ConfigureAwait(false);
                await using var command = database.CreateCommand(); command.CommandText = "DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            Directory.Delete(staging, recursive: true);
            await registry.UnregisterAsync(storageId, ct).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(staging) && !Directory.Exists(descriptor.Root)) Directory.Move(staging, descriptor.Root);
            throw;
        }
        return cleanup.Preview;
    }

    private static async Task RequireNoDatabaseExtensionsAsync(StorageRuntimeLease lease, CancellationToken ct)
    {
        var connection = lease.Services.GetRequiredService<IDatabaseConnectionInfo>();
        if (connection.Provider != DatabaseProvider.PostgreSql) return;
        var schema = lease.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions>>().Value.PostgreSql.Schema;
        await using var database = new Npgsql.NpgsqlConnection(connection.ConnectionString);
        await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new Npgsql.NpgsqlCommand("SELECT e.extname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE n.nspname = @schema LIMIT 1", database);
        command.Parameters.AddWithValue("schema", schema ?? "");
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string extension)
            throw new InvalidOperationException("Project storage cannot be deleted while its schema hosts the database-wide extension '" + extension + "'. Ask the database administrator to relocate the extension first.");
    }

    internal static List<FileInfo> OwnedFiles(string root, string directory, CancellationToken ct)
    {
        var result = new List<FileInfo>();
        if (!Directory.Exists(directory)) return result;
        StorageScopePaths.RejectLinks(root, directory);
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // One metadata read per entry. This walk is the whole cost of the usage report,
                    // and measured against a real profile it is ~99% metadata syscalls and ~0.2%
                    // enumeration: 9635 cache entries enumerate in 22 ms but reading their metadata
                    // takes 2777 ms. `File.GetAttributes(entry)` followed by `new FileInfo(entry)`
                    // is two stats of the same inode for every file, which is the share worth
                    // removing. A single `FileInfo` reads the stat once and caches it, so the
                    // attribute check below and the `Length` the caller later reads come from the
                    // same call. The link check is unchanged in kind — same attributes, same
                    // decision.
                    var info = new FileInfo(entry);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Owned storage contains a link: " + entry);
                    if ((info.Attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                    result.Add(info);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        return result;
    }

    private static IEnumerable<FileInfo> DeletionFacts(string root, IEnumerable<FileInfo> files) => files.Where(file =>
    {
        var category = Path.GetRelativePath(root, file.FullName).Split(Path.DirectorySeparatorChar)[0];
        return category is not ("logs" or "cache" or "temp") && !file.Name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            && !file.Name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);
    });

    private static async Task<string> DeletionFingerprintAsync(StorageRuntimeLease lease, IEnumerable<FileInfo> files, CancellationToken ct)
    {
        var scope = lease.Descriptor;
        var connection = lease.Services.GetRequiredService<IDatabaseConnectionInfo>();
        var mainDatabase = connection.Provider == DatabaseProvider.Sqlite
            ? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection.ConnectionString).DataSource : null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (connection.Provider == DatabaseProvider.PostgreSql)
            hash.AppendData(await PostgreSqlFactsFingerprintAsync(lease.Services, connection, ct).ConfigureAwait(false));
        foreach (var file in DeletionFacts(scope.Root, files).OrderBy(file => file.FullName, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(scope.Data, file.FullName).Replace('\\', '/');
            var database = mainDatabase is not null && StorageScopeInitializer.SamePath(file.FullName, mainDatabase)
                || relative.StartsWith("vectors/", StringComparison.Ordinal) && file.Extension == ".db";
            if (!database)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(file.FullName + "\0" + file.Length + "\0" + file.LastWriteTimeUtc.Ticks));
                continue;
            }
            // Hash a consistent database snapshot. WAL/SHM appearance and checkpoint
            // timestamps are operational details, while changes to facts invalidate consent.
            var snapshot = Path.Combine(scope.Temp, "deletion-preview-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                await using (var source = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = file.FullName, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                await using (var target = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = snapshot, Pooling = false }.ToString()))
                {
                    await source.OpenAsync(ct).ConfigureAwait(false); await target.OpenAsync(ct).ConfigureAwait(false);
                    source.BackupDatabase(target);
                }
                await using var stream = File.OpenRead(snapshot);
                hash.AppendData(Encoding.UTF8.GetBytes(file.FullName + "\0"));
                hash.AppendData(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            }
            finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<byte[]> PostgreSqlFactsFingerprintAsync(IServiceProvider services, IDatabaseConnectionInfo connection, CancellationToken ct)
    {
        var schema = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions>>().Value.PostgreSql.Schema;
        if (string.IsNullOrEmpty(schema) || schema.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidOperationException("PostgreSQL deletion preview requires its host-owned schema.");
        await using var database = new Npgsql.NpgsqlConnection(connection.ConnectionString);
        await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var tables = new List<string>();
        await using (var query = database.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema AND table_type = 'BASE TABLE' ORDER BY table_name";
            query.Parameters.AddWithValue("schema", schema);
            await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(schema + "\0"));
        foreach (var table in tables)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\0"));
            await using var query = database.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT to_jsonb(t)::text FROM \"" + schema + "\".\"" + table.Replace("\"", "\"\"") + "\" t ORDER BY to_jsonb(t)::text";
            await using var reader = await query.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + "\0"));
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return hash.GetHashAndReset();
    }

    private static string Fingerprint(IEnumerable<FileInfo> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(x => x.FullName, StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(file.FullName + "\0" + file.Length + "\0" + file.LastWriteTimeUtc.Ticks));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed record Cleanup(StorageCleanupPreview Preview, string Fingerprint, IReadOnlyList<string> Files);
}
