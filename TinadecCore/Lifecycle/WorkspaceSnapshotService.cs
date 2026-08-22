using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Lifecycle;

/// <summary>
/// Persists an inspectable workspace file manifest and small file contents. It is
/// intentionally separate from run checkpoints and conversation context: a
/// workspace snapshot describes external state and can be restored only after a
/// deterministic conflict check.
/// </summary>
internal sealed class WorkspaceSnapshotService : IWorkspaceSnapshotService
{
    private const string SessionProjectionKind = "session_runtime";
    private const string SessionProjectionSource = "lifecycle_projection";
    private const string SessionProjectionSchemaVersion = "1.0";
    private const long DefaultMaxBytes = 64 * 1024 * 1024;
    private const long MaxSingleFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> ProjectionLocks = new();

    private readonly IDbContextFactory<LifecycleDbContext> _dbFactory;
    private readonly ISessionLocator _sessions;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly StoragePaths _paths;

    public WorkspaceSnapshotService(
        IDbContextFactory<LifecycleDbContext> dbFactory,
        ISessionLocator sessions,
        IContentStore content,
        ITenantContextAccessor tenant,
        StoragePaths paths)
    {
        _dbFactory = dbFactory;
        _sessions = sessions;
        _content = content;
        _tenant = tenant;
        _paths = paths;
    }

    public async Task<WorkspaceSnapshot> CreateAsync(
        WorkspaceSnapshotCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ProjectId == Guid.Empty) throw new ArgumentException("Project id is required.", nameof(request));
        var scope = _tenant.Current;
        var project = await _sessions.FindProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId)
            throw new UnauthorizedAccessException("Project is outside the current tenant/workspace.");

        var key = NormalizeKey(request.IdempotencyKey);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (key is not null)
        {
            var prior = await db.WorkspaceSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.ProjectId == request.ProjectId && x.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
            if (prior is not null) return ToSnapshot(prior);
        }

        var manifest = await BuildManifestAsync(project.RootPath, request.IncludeHidden,
            Math.Clamp(request.MaxFiles, 1, 100_000), Math.Clamp(request.MaxBytes, 1, 1024L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ExpectedWorkspaceHash)
            && !FixedEquals(request.ExpectedWorkspaceHash, manifest.WorkspaceHash))
        {
            throw new WorkspaceSnapshotConflictException(
                "Workspace changed before the snapshot was admitted.",
                ["workspace_hash"]);
        }

        // SQLite cannot translate DateTimeOffset ordering. Keep the scope filter
        // server-side and select the latest row deterministically in memory.
        var priorSnapshots = await db.WorkspaceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == request.ProjectId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var priorSnapshot = priorSnapshots.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefault();
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await using var payloadStream = new MemoryStream(payload, writable: false);
        var stored = await _content.PutAsync(new ContentWriteRequest(
            scope.TenantId, scope.WorkspaceId, "workspace-snapshot", "application/json", payloadStream), cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var row = new WorkspaceSnapshotRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            ProjectId = request.ProjectId, Kind = manifest.IsGit ? "git" : "filesystem", Status = "created",
            IsGit = manifest.IsGit, WorkspaceHash = manifest.WorkspaceHash, ContentReference = stored.Value,
            ContentHash = stored.Sha256, ContentLength = stored.Length, FileCount = manifest.Files.Count,
            BaseSnapshotId = priorSnapshot?.Id, IdempotencyKey = key, CreatedAt = now
        };
        db.WorkspaceSnapshots.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (key is not null)
        {
            var winner = await db.WorkspaceSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.ProjectId == request.ProjectId && x.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
            if (winner is not null) return ToSnapshot(winner);
            throw;
        }
        return ToSnapshot(row);
    }

    public async Task<IReadOnlyList<WorkspaceSnapshot>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.WorkspaceSnapshots.AsNoTracking().Where(x =>
            x.ProjectId == projectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderByDescending(x => x.CreatedAt).Select(ToSnapshot).ToArray();
    }

    public async Task<WorkspaceSnapshot?> GetAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.WorkspaceSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == snapshotId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToSnapshot(row);
    }

    /// <inheritdoc />
    public async Task<WorkspaceSessionSnapshot?> GetSessionProjectionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session id is required.", nameof(sessionId));

        // ISessionLocator is the tenant/workspace boundary. Do not query by the
        // caller-supplied session id before this lookup succeeds.
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        var scope = _tenant.Current;
        if (session.TenantId != scope.TenantId || session.WorkspaceId != scope.WorkspaceId)
            return null;

        var gate = ProjectionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var runs = await db.Runs.AsNoTracking()
                .Where(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId && x.SessionId == sessionId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            // SQLite stores DateTimeOffset as text and cannot translate ordering
            // over that CLR type. Sort after the tenant-scoped query instead.
            runs = runs.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
            var runIds = runs.Select(x => x.Id).ToArray();
            var events = runIds.Length == 0
                ? []
                : await db.EventIndex.AsNoTracking()
                    .Where(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId && x.SessionId == sessionId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            events = events.OrderBy(x => x.Timestamp).ThenBy(x => x.RunId).ThenBy(x => x.Sequence).ToList();

            var tasks = new List<WorkspaceTaskMetadata>();
            foreach (var run in runs)
            {
                var path = _paths.TaskSnapshot(run.Id);
                if (!File.Exists(path)) continue;
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var file = await JsonSerializer.DeserializeAsync<TaskSnapshotFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (file?.Tasks is null) continue;
                    for (var index = 0; index < file.Tasks.Count; index++)
                    {
                        var value = file.Tasks[index] is JsonElement element
                            ? element.Clone()
                            : JsonSerializer.SerializeToElement(file.Tasks[index], JsonOptions);
                        tasks.Add(new WorkspaceTaskMetadata(run.Id, index, value));
                    }
                }
                catch (JsonException)
                {
                    // A malformed task projection must not make the whole debug
                    // endpoint fail. The durable run/event metadata remains valid.
                }
                catch (IOException)
                {
                    // Task files are Core-owned projections and may be replaced
                    // atomically while a run is active. Retry on the next capture.
                }
            }

            var runMetadata = runs.Select(x => new WorkspaceRunMetadata(
                x.Id, x.SessionId, x.TriggerMessageId,
                x.InitiatedByPrincipalId == Guid.Empty ? null : x.InitiatedByPrincipalId,
                x.Status, x.Summary, x.TaskRevision, x.LastEventSequence,
                x.CreatedAt, x.UpdatedAt, x.CompletedAt)).ToArray();
            var eventMetadata = events.Select(x => new WorkspaceEventMetadata(
                x.EventId, x.RunId, x.SessionId, x.Sequence, x.EventType, x.Severity,
                x.TaskId, x.ApprovalId, x.ToolId, x.Summary, x.SchemaVersion,
                x.PayloadHash, x.Timestamp)).ToArray();
            var document = new SessionProjectionDocument(
                SessionProjectionSchemaVersion, SessionProjectionKind,
                SessionProjectionSource, sessionId, session.ProjectId,
                runMetadata, tasks.ToArray(), eventMetadata);
            var payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            var contentHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            var latest = await db.SessionMetadataSnapshots.AsNoTracking()
                .Where(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId
                    && x.SessionId == sessionId && x.Kind == SessionProjectionKind && x.Source == SessionProjectionSource)
                .OrderByDescending(x => x.Revision)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (latest is not null && string.Equals(latest.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                return await ReadSessionProjectionAsync(latest, cancellationToken).ConfigureAwait(false);

            await using var body = new MemoryStream(payload, writable: false);
            var stored = await _content.PutAsync(new ContentWriteRequest(
                session.TenantId, session.WorkspaceId, "session-metadata-snapshot", "application/json", body), cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var row = new SessionMetadataSnapshotRecord
            {
                Id = Guid.NewGuid(), TenantId = session.TenantId, WorkspaceId = session.WorkspaceId,
                SessionId = sessionId, ProjectId = session.ProjectId, Kind = SessionProjectionKind,
                Source = SessionProjectionSource, SchemaVersion = SessionProjectionSchemaVersion,
                Revision = (latest?.Revision ?? 0) + 1, ContentReference = stored.Value,
                ContentHash = stored.Sha256, ContentLength = stored.Length, CapturedAt = now
            };
            db.SessionMetadataSnapshots.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Another host may have captured this exact state concurrently.
                // The content-hash unique index makes the durable winner the
                // authority; do not return an in-memory, non-durable projection.
                await using var winnerDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var winner = await winnerDb.SessionMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId
                    && x.SessionId == sessionId && x.Kind == SessionProjectionKind
                    && x.Source == SessionProjectionSource && x.ContentHash == stored.Sha256,
                    cancellationToken).ConfigureAwait(false);
                if (winner is not null) return await ReadSessionProjectionAsync(winner, cancellationToken).ConfigureAwait(false);
                throw;
            }
            return ToSessionProjection(row, document);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkspaceRestoreResult> RestoreAsync(
        Guid snapshotId,
        WorkspaceRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot id is required.", nameof(snapshotId));
        var scope = _tenant.Current;
        var key = NormalizeKey(request.IdempotencyKey);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.WorkspaceSnapshots.SingleOrDefaultAsync(x =>
            x.Id == snapshotId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Workspace snapshot was not found.");
        if (key is not null && string.Equals(row.LastRestoreIdempotencyKey, key, StringComparison.Ordinal))
            return RestoreResult(row);

        var project = await _sessions.FindProjectAsync(row.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId)
            throw new UnauthorizedAccessException("Project is outside the current tenant/workspace.");
        var manifest = await ReadManifestAsync(row, cancellationToken).ConfigureAwait(false);
        var current = await BuildManifestAsync(project.RootPath, manifest.IncludeHidden, 100_000, DefaultMaxBytes, cancellationToken).ConfigureAwait(false);
        var expected = string.IsNullOrWhiteSpace(request.ExpectedWorkspaceHash) ? row.WorkspaceHash : request.ExpectedWorkspaceHash.Trim();
        var conflicts = FixedEquals(expected, current.WorkspaceHash) ? [] : new[] { "workspace_hash" };
        if (conflicts.Length != 0 && !request.AllowConflicts)
        {
            row.Status = "conflict";
            row.ConflictJson = JsonSerializer.Serialize(conflicts, JsonOptions);
            row.LastRestoreIdempotencyKey = key;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkspaceSnapshotConflictException("Workspace changed after the snapshot was created.", conflicts);
        }

        var applied = 0;
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.ContentBase64)) continue;
            var path = SafePath(project.RootPath, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Convert.FromBase64String(file.ContentBase64);
            var temporary = path + ".tinadec-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            applied++;
        }

        // Restore replaces the captured visibility scope. Files created after
        // the snapshot are removed only after the conflict guard above has
        // passed (or the caller explicitly opted into conflicts). Git/Core
        // metadata and excluded hidden files remain outside that scope.
        var snapshotPaths = manifest.Files
            .Select(x => x.Path)
            .ToHashSet(StringComparer.Ordinal);
        var root = Path.GetFullPath(project.RootPath);
        foreach (var currentPath in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, currentPath).Replace(Path.DirectorySeparatorChar, '/');
            if (IsExcluded(relative, manifest.IncludeHidden) || snapshotPaths.Contains(relative)) continue;
            File.Delete(currentPath);
            applied++;
        }

        var now = DateTimeOffset.UtcNow;
        row.Status = conflicts.Length == 0 ? "restored" : "restored_with_conflicts";
        row.ConflictJson = conflicts.Length == 0 ? null : JsonSerializer.Serialize(conflicts, JsonOptions);
        row.AppliedFileCount = applied;
        row.LastRestoreIdempotencyKey = key;
        row.RestoredAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceRestoreResult(row.Status, row.Id, row.WorkspaceHash, conflicts, applied, now);
    }

    private async Task<WorkspaceManifest> ReadManifestAsync(WorkspaceSnapshotRecord row, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(
            row.ContentReference, row.ContentHash, row.ContentLength, "application/json"), cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<WorkspaceManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace snapshot content is invalid.");
    }

    private async Task<WorkspaceSessionSnapshot> ReadSessionProjectionAsync(
        SessionMetadataSnapshotRecord row,
        CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(
            row.ContentReference, row.ContentHash, row.ContentLength, "application/json"), cancellationToken).ConfigureAwait(false);
        var document = await JsonSerializer.DeserializeAsync<SessionProjectionDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Session metadata snapshot content is invalid.");
        return ToSessionProjection(row, document);
    }

    private static WorkspaceSessionSnapshot ToSessionProjection(
        SessionMetadataSnapshotRecord row,
        SessionProjectionDocument document) => new(
            row.Id, row.TenantId, row.WorkspaceId, row.SessionId, row.ProjectId,
            row.Kind, row.Source, row.SchemaVersion, row.Revision, row.CapturedAt,
            document.Runs, document.Tasks, document.Events);

    private static async Task<WorkspaceManifest> BuildManifestAsync(
        string root,
        bool includeHidden,
        int maxFiles,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException("Workspace root was not found.");
        var files = new List<WorkspaceFile>();
        long capturedBytes = 0;
        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(fullRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            if (IsExcluded(relative, includeHidden)) continue;
            if (files.Count >= maxFiles) throw new InvalidOperationException($"Workspace snapshot exceeds the {maxFiles} file limit.");
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            string? content = null;
            if (info.Length <= MaxSingleFileBytes && capturedBytes + info.Length <= maxBytes)
            {
                content = Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                capturedBytes += info.Length;
            }
            files.Add(new WorkspaceFile(relative, info.Length, hash, content));
        }
        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        var workspaceHash = ComputeWorkspaceHash(files);
        var isGit = Directory.Exists(Path.Combine(fullRoot, ".git")) || File.Exists(Path.Combine(fullRoot, ".git"));
        return new WorkspaceManifest(1, isGit, includeHidden, workspaceHash, files);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string ComputeWorkspaceHash(IEnumerable<WorkspaceFile> files)
    {
        var canonical = string.Join('\n', files.OrderBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => $"{x.Path}\t{x.Length}\t{x.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsExcluded(string relative, bool includeHidden)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => string.Equals(x, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(x, ".tinadec", StringComparison.OrdinalIgnoreCase))) return true;
        return !includeHidden && segments.Any(x => x.Length > 0 && x[0] == '.');
    }

    private static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Snapshot contains an invalid path.");
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Snapshot path escaped the workspace root.");
        return path;
    }

    private static string? NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim();
        return key.Length <= 256 ? key : throw new ArgumentException("Snapshot idempotency key is too long.", nameof(value));
    }

    private static bool FixedEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }

    private static WorkspaceSnapshot ToSnapshot(WorkspaceSnapshotRecord row) => new(
        row.Id, row.TenantId, row.WorkspaceId, row.ProjectId, row.Kind, row.Status, row.IsGit,
        row.WorkspaceHash, row.ContentReference, row.ContentHash, row.ContentLength, row.FileCount,
        row.BaseSnapshotId, row.CreatedAt);

    private static WorkspaceRestoreResult RestoreResult(WorkspaceSnapshotRecord row)
    {
        var conflicts = string.IsNullOrWhiteSpace(row.ConflictJson)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(row.ConflictJson, JsonOptions) ?? [];
        return new WorkspaceRestoreResult(row.Status, row.Id, row.WorkspaceHash, conflicts, row.AppliedFileCount, row.RestoredAt);
    }

    private sealed record WorkspaceManifest(int SchemaVersion, bool IsGit, bool IncludeHidden, string WorkspaceHash, List<WorkspaceFile> Files);
    private sealed record WorkspaceFile(string Path, long Length, string Sha256, string? ContentBase64);

    private sealed record SessionProjectionDocument(
        string SchemaVersion,
        string Kind,
        string Source,
        Guid SessionId,
        Guid ProjectId,
        IReadOnlyList<WorkspaceRunMetadata> Runs,
        IReadOnlyList<WorkspaceTaskMetadata> Tasks,
        IReadOnlyList<WorkspaceEventMetadata> Events);
}
