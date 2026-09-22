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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> ProjectionLocks = new();

    private readonly IDbContextFactory<LifecycleDbContext> _dbFactory;
    private readonly ISessionLocator _sessions;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly StoragePaths _paths;
    private readonly IReadOnlyList<IWorkspaceSnapshotProvider> _providers;

    public WorkspaceSnapshotService(
        IDbContextFactory<LifecycleDbContext> dbFactory,
        ISessionLocator sessions,
        IContentStore content,
        ITenantContextAccessor tenant,
        StoragePaths paths,
        IEnumerable<IWorkspaceSnapshotProvider> providers)
    {
        _dbFactory = dbFactory;
        _sessions = sessions;
        _content = content;
        _tenant = tenant;
        _paths = paths;
        _providers = providers.OrderByDescending(x => string.Equals(x.Kind, "git", StringComparison.OrdinalIgnoreCase)).ToArray();
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

        var provider = ResolveProvider(project.RootPath);
        var manifest = await provider.CaptureAsync(new WorkspaceSnapshotCaptureRequest(
            project.RootPath,
            request.IncludeHidden,
            Math.Clamp(request.MaxFiles, 1, 100_000),
            Math.Clamp(request.MaxBytes, 1, 1024L * 1024 * 1024)), cancellationToken).ConfigureAwait(false);
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
            ProjectId = request.ProjectId, Kind = manifest.ProviderKind, Status = "created",
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

    public async Task<WorkspaceSnapshotValidationResult> ValidateAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot id is required.", nameof(snapshotId));
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.WorkspaceSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == snapshotId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Workspace snapshot was not found.");
        var project = await _sessions.FindProjectAsync(row.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId)
            throw new UnauthorizedAccessException("Project is outside the current tenant/workspace.");
        var manifest = await ReadManifestAsync(row, cancellationToken).ConfigureAwait(false);
        var provider = ResolveProvider(project.RootPath, manifest.ProviderKind);
        var current = await provider.CaptureAsync(new WorkspaceSnapshotCaptureRequest(
            project.RootPath, manifest.IncludeHidden, 100_000, 1024L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        var conflicts = new List<string>();
        if (!FixedEquals(row.WorkspaceHash, current.WorkspaceHash)) conflicts.Add("workspace_hash");
        if (manifest.Git is { } expectedGit && current.Git is { } actualGit)
        {
            if (!string.Equals(expectedGit.Head, actualGit.Head, StringComparison.OrdinalIgnoreCase)) conflicts.Add("git.head");
            if (!string.Equals(expectedGit.Branch, actualGit.Branch, StringComparison.Ordinal)) conflicts.Add("git.branch");
            if (!string.Equals(expectedGit.IndexTreeHash, actualGit.IndexTreeHash, StringComparison.OrdinalIgnoreCase)) conflicts.Add("git.index");
            if (!string.Equals(expectedGit.WorktreeHash, actualGit.WorktreeHash, StringComparison.OrdinalIgnoreCase)) conflicts.Add("git.worktree");
            if (!expectedGit.ConflictPaths.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                actualGit.ConflictPaths.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal)) conflicts.Add("git.conflicts");
        }
        else if (manifest.Git is not null || current.Git is not null)
        {
            conflicts.Add("git_state_missing");
        }
        return new WorkspaceSnapshotValidationResult(
            conflicts.Count == 0,
            snapshotId,
            row.WorkspaceHash,
            current.WorkspaceHash,
            conflicts);
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
        var provider = ResolveProvider(project.RootPath, manifest.ProviderKind);
        WorkspaceSnapshotProviderRestoreResult providerResult;
        try
        {
            providerResult = await provider.RestoreAsync(project.RootPath, manifest,
                new WorkspaceSnapshotProviderRestoreRequest(request.ExpectedWorkspaceHash, request.AllowConflicts),
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceSnapshotConflictException ex)
        {
            row.Status = "conflict";
            row.ConflictJson = JsonSerializer.Serialize(ex.Conflicts, JsonOptions);
            row.LastRestoreIdempotencyKey = key;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        var now = DateTimeOffset.UtcNow;
        row.Status = providerResult.Status;
        row.ConflictJson = providerResult.Conflicts.Count == 0
            ? null
            : JsonSerializer.Serialize(providerResult.Conflicts, JsonOptions);
        row.AppliedFileCount = providerResult.AppliedFileCount;
        row.LastRestoreIdempotencyKey = key;
        row.RestoredAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceRestoreResult(row.Status, row.Id, row.WorkspaceHash,
            providerResult.Conflicts, providerResult.AppliedFileCount, now);
    }

    /// <summary>
    /// One file's text is served to a reviewer up to this many bytes. It is a preview ceiling, not
    /// a safety limit: the hash below the text is what a restore is compared against, and a file
    /// over the ceiling still restores whole.
    /// </summary>
    private const long MaxDiffTextBytes = 256 * 1024;

    public async Task<IReadOnlyList<WorkspaceFileChange>> ListFileChangesAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var (manifest, _, current, _) = await ReadSnapshotWithCurrentAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        return Compare(manifest, current);
    }

    public async Task<WorkspaceFileDiff?> GetFileDiffAsync(
        Guid snapshotId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var (row, project) = await ReadSnapshotRowAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        var relative = ResolveRelativeInsideRoot(project.RootPath, path)
            ?? throw new WorkspaceSnapshotPathException(path);
        var manifest = await ReadManifestAsync(row, cancellationToken).ConfigureAwait(false);
        var stored = manifest.Files.FirstOrDefault(x => string.Equals(x.Path, relative, StringComparison.Ordinal));
        var diskPath = Path.Combine(Path.GetFullPath(project.RootPath), relative.Replace('/', Path.DirectorySeparatorChar));
        var disk = File.Exists(diskPath)
            ? new WorkspaceSnapshotFile(relative, new FileInfo(diskPath).Length,
                await WorkspaceSnapshotProviderSupport.HashFileAsync(diskPath, cancellationToken).ConfigureAwait(false), null)
            : null;
        if (stored is null && disk is null) return null;
        var status = Status(stored, disk);
        return new WorkspaceFileDiff(
            relative,
            status,
            Side(stored, stored?.ContentBase64 is null ? null
                : Convert.FromBase64String(stored.ContentBase64)),
            Side(disk, disk is null ? null : await File.ReadAllBytesAsync(diskPath, cancellationToken).ConfigureAwait(false)),
            Restorable: stored?.ContentBase64 is not null);
    }

    public async Task<WorkspaceFileChange> RestoreFileAsync(
        Guid snapshotId,
        WorkspaceFileRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentException("A file restore request is required.", nameof(request));
        if (request.ExpectedSha256 is null)
            throw new ArgumentException(
                "expected_sha256 is required: name the hash that was reviewed, or an empty string to assert the file was absent.",
                nameof(request));
        var (row, project) = await ReadSnapshotRowAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        var relative = ResolveRelativeInsideRoot(project.RootPath, request.Path)
            ?? throw new WorkspaceSnapshotPathException(request.Path);
        var manifest = await ReadManifestAsync(row, cancellationToken).ConfigureAwait(false);
        var stored = manifest.Files.FirstOrDefault(x => string.Equals(x.Path, relative, StringComparison.Ordinal))
            ?? throw new WorkspaceSnapshotConflictException(
                "This workspace snapshot has no record of that file.",
                [$"file:{relative}:not_in_snapshot"]);
        if (stored.ContentBase64 is null)
            throw new WorkspaceSnapshotFileNotCapturedException(relative);

        var target = Path.Combine(Path.GetFullPath(project.RootPath), relative.Replace('/', Path.DirectorySeparatorChar));
        var bytes = Convert.FromBase64String(stored.ContentBase64);
        var live = File.Exists(target)
            ? await WorkspaceSnapshotProviderSupport.HashFileAsync(target, cancellationToken).ConfigureAwait(false)
            : null;
        // The caller was shown a hash by the listing or the diff; writing over anything else would
        // silently discard an edit made after they looked. An empty expectation is the reviewer
        // saying "there was no file here", so a file that appeared in the meantime blocks too.
        var reviewed = request.ExpectedSha256.Length == 0
            ? live is null
            : live is not null && FixedEquals(request.ExpectedSha256, live);
        if (!reviewed)
            throw new WorkspaceSnapshotConflictException(
                "The file changed after it was reviewed.",
                [$"file:{relative}:content_unavailable"]);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        return new WorkspaceFileChange(relative, "unchanged", stored.Sha256, stored.Sha256, stored.Length, bytes.LongLength, Restorable: true);
    }

    private async Task<(WorkspaceSnapshotDocument Manifest, WorkspaceSnapshotRecord Row, WorkspaceSnapshotDocument Current, ProjectReference Project)>
        ReadSnapshotWithCurrentAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        var (row, project) = await ReadSnapshotRowAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(row, cancellationToken).ConfigureAwait(false);
        var provider = ResolveProvider(project.RootPath, manifest.ProviderKind);
        // MaxBytes of one asks the provider for hashes only. A listing answers "which files moved
        // out of line with the snapshot", and copying every small file into memory to find out
        // would make the read side as expensive as taking another snapshot.
        var current = await provider.CaptureAsync(new WorkspaceSnapshotCaptureRequest(
            project.RootPath, manifest.IncludeHidden, 100_000, 1), cancellationToken).ConfigureAwait(false);
        return (manifest, row, current, project);
    }

    private async Task<(WorkspaceSnapshotRecord Row, ProjectReference Project)> ReadSnapshotRowAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot id is required.", nameof(snapshotId));
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.WorkspaceSnapshots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == snapshotId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Workspace snapshot was not found.");
        var project = await _sessions.FindProjectAsync(row.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId)
            throw new UnauthorizedAccessException("Project is outside the current tenant/workspace.");
        return (row, project);
    }

    private static IReadOnlyList<WorkspaceFileChange> Compare(
        WorkspaceSnapshotDocument stored,
        WorkspaceSnapshotDocument current)
    {
        var currentByPath = current.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var rows = new List<WorkspaceFileChange>();
        foreach (var entry in stored.Files)
        {
            currentByPath.TryGetValue(entry.Path, out var live);
            currentByPath.Remove(entry.Path);
            rows.Add(Change(entry, live));
        }
        foreach (var extra in currentByPath.Values.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            rows.Add(Change(null, extra));
        }
        return rows;
    }

    private static WorkspaceFileChange Change(WorkspaceSnapshotFile? stored, WorkspaceSnapshotFile? live) => new(
        (stored ?? live!).Path,
        Status(stored, live),
        stored?.Sha256,
        live?.Sha256,
        stored?.Length,
        live?.Length,
        Restorable: stored?.ContentBase64 is not null);

    private static string Status(WorkspaceSnapshotFile? stored, WorkspaceSnapshotFile? live)
    {
        if (stored is null) return "added";
        if (live is null) return "deleted";
        return string.Equals(stored.Sha256, live.Sha256, StringComparison.OrdinalIgnoreCase) ? "unchanged" : "modified";
    }

    private static WorkspaceFileSide Side(WorkspaceSnapshotFile? file, byte[]? bytes)
    {
        if (file is null) return new WorkspaceFileSide(false, null, null, Binary: false, Truncated: false, Text: null);
        if (bytes is null)
            // The manifest has the row but not the body: the file was over the provider's content
            // ceiling when the snapshot was taken. Say "no preview" rather than letting an empty
            // string read as "the file became empty".
            return new WorkspaceFileSide(true, file.Length, file.Sha256, Binary: false, Truncated: true, Text: null);
        var truncated = file.Length > MaxDiffTextBytes;
        var window = truncated ? bytes[..checked((int)MaxDiffTextBytes)] : bytes;
        if (IsBinary(window))
            return new WorkspaceFileSide(true, file.Length, file.Sha256, Binary: true, truncated, null);
        return new WorkspaceFileSide(true, file.Length, file.Sha256, Binary: false, truncated,
            Encoding.UTF8.GetString(window));
    }

    private static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return false;
        if (bytes.IndexOf((byte)0) >= 0) return true;
        try { return Encoding.UTF8.GetString(bytes.ToArray()).IndexOf('\uFFFD') >= 0; }
        catch (ArgumentException) { return true; }
    }

    /// <summary>
    /// Turns a caller-supplied path into the manifest's own spelling, or null when it does not
    /// name a file inside the root. The manifest stores forward-slash relative paths, so the
    /// comparison is done on the normalized form; <c>..</c>, absolute paths and drive- or
    /// UNC-qualified segments all fall outside and are refused rather than clamped.
    /// </summary>
    private static string? ResolveRelativeInsideRoot(string workspaceRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var candidate = path.Trim().Replace('\\', '/');
        if (candidate.StartsWith('/') || candidate.Contains(":") || Path.IsPathRooted(path)) return null;
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar))); }
        catch (ArgumentException) { return null; }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return full[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
    }

    private IWorkspaceSnapshotProvider ResolveProvider(string workspaceRoot, string? kind = null)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var matching = _providers.FirstOrDefault(x => string.Equals(x.Kind, kind, StringComparison.OrdinalIgnoreCase));
            if (matching is null || !matching.CanHandle(workspaceRoot))
                throw new InvalidOperationException($"Workspace snapshot provider '{kind}' is unavailable for this workspace.");
            return matching;
        }

        return _providers.FirstOrDefault(x => x.CanHandle(workspaceRoot))
            ?? throw new InvalidOperationException("No workspace snapshot provider can handle this workspace.");
    }

    private async Task<WorkspaceSnapshotDocument> ReadManifestAsync(WorkspaceSnapshotRecord row, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(
            row.ContentReference, row.ContentHash, row.ContentLength, "application/json"), cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<WorkspaceSnapshotDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
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

    private sealed record SessionProjectionDocument(
        string SchemaVersion,
        string Kind,
        string Source,
        Guid SessionId,
        Guid? ProjectId,
        IReadOnlyList<WorkspaceRunMetadata> Runs,
        IReadOnlyList<WorkspaceTaskMetadata> Tasks,
        IReadOnlyList<WorkspaceEventMetadata> Events);
}
