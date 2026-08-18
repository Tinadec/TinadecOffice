using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Events;
using TinadecCore.Persistence;

namespace TinadecCore.Lifecycle;

public sealed class StorageLifecycleService : IStorageMigrationParticipant
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RunLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int StreamAppendMaxAttempts = 8;
    private readonly IDbContextFactory<LifecycleDbContext> _dbFactory;
    private readonly ISessionLocator _sessions;
    private readonly StoragePaths _paths;
    private readonly StorageDiagnostics _diagnostics;

    public StorageLifecycleService(
        IDbContextFactory<LifecycleDbContext> dbFactory,
        ISessionLocator sessions,
        StoragePaths paths,
        StorageDiagnostics diagnostics,
        IContentStore contentStore)
    {
        _dbFactory = dbFactory;
        _sessions = sessions;
        _paths = paths;
        _diagnostics = diagnostics;
        _contentStore = contentStore;
    }

    private readonly IContentStore _contentStore;

    public async Task<RunRecord> StartRunAsync(Guid sessionId, Guid triggerMessageId, CancellationToken cancellationToken = default) =>
        (await StartOrGetRunAsync(sessionId, triggerMessageId, null, cancellationToken).ConfigureAwait(false)).Run;

    public async Task<RunRecord> StartRunAsync(Guid sessionId, Guid triggerMessageId, RunStartOptions? options, CancellationToken cancellationToken = default) =>
        (await StartOrGetRunAsync(sessionId, triggerMessageId, options, cancellationToken).ConfigureAwait(false)).Run;

    /// <summary>
    /// Creates exactly one run for a durable trigger message. The database unique index
    /// makes this hold across processes; callers can safely retry an admission after a
    /// crash between creating the run and attaching its conversation turn.
    /// </summary>
    public async Task<RunStartResultRecord> StartOrGetRunAsync(Guid sessionId, Guid triggerMessageId, RunStartOptions? options, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == session.TenantId
            && x.WorkspaceId == session.WorkspaceId
            && x.SessionId == sessionId
            && x.TriggerMessageId == triggerMessageId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new RunStartResultRecord(existing, Existing: true);
        }

        var now = DateTimeOffset.UtcNow;
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), TenantId = session.TenantId, WorkspaceId = session.WorkspaceId, SessionId = sessionId, TriggerMessageId = triggerMessageId,
            TurnId = options?.TurnId, ContextRevision = options?.ContextRevision ?? 0, ConfigurationVersion = options?.ConfigurationVersion ?? 0,
            ConfigurationHash = options?.ConfigurationHash ?? string.Empty, ApplicationMode = options?.ApplicationMode ?? "conversation",
            AgentMode = options?.AgentMode ?? "auto", PermissionMode = options?.PermissionMode ?? "default",
            RuntimeProfileId = options?.RuntimeProfileId ?? "conversation.auto", Status = options is null ? "running" : "understanding", CreatedAt = now, UpdatedAt = now
        };
        db.Runs.Add(run);
        db.RunStreamCursors.Add(new RunStreamCursorRecord { RunId = run.Id, NextSequence = 0, UpdatedAt = now });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A competing host admitted the same trigger. The unique database index is
            // the authority; discard this local attempt and return the durable winner.
            await using var retry = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var winner = await retry.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == session.TenantId
                && x.WorkspaceId == session.WorkspaceId
                && x.SessionId == sessionId
                && x.TriggerMessageId == triggerMessageId, cancellationToken).ConfigureAwait(false);
            if (winner is not null)
            {
                return new RunStartResultRecord(winner, Existing: true);
            }
            throw;
        }
        await WriteTaskSnapshotAsync(run.Id, new TaskSnapshotFile { RunId = run.Id }, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_paths.Artifacts(run.Id));
        return new RunStartResultRecord(run, Existing: false);
    }

    public async Task<IReadOnlyList<RunRecord>> ListRunsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return [];
        var runs = await db.Runs.AsNoTracking().Where(x => x.SessionId == sessionId && x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return runs.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task<RunRecord?> FindRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Runs.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task AdvanceRunContextRevisionAsync(Guid runId, long contextRevision, CancellationToken cancellationToken = default)
    {
        if (contextRevision < 0) throw new ArgumentOutOfRangeException(nameof(contextRevision));
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Runs.Where(item => item.Id == runId && item.ContextRevision < contextRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ContextRevision, contextRevision)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunRecord?> FindRunByTriggerMessageAsync(Guid sessionId, Guid triggerMessageId, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == session.TenantId
            && x.WorkspaceId == session.WorkspaceId
            && x.SessionId == sessionId
            && x.TriggerMessageId == triggerMessageId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null) return;
        run.Status = "completed";
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = run.CompletedAt.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountActiveRunsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return 0;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Runs.CountAsync(x => x.SessionId == sessionId && x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId && x.Status != "completed" && x.Status != "failed" && x.Status != "cancelled", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRunStatusAsync(Guid runId, string status, string? summary = null, CancellationToken cancellationToken = default)
    {
        if (!RunStatusMachine.IsKnown(status)) throw new ArgumentException($"Unknown run status '{status}'.", nameof(status));
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null) throw new KeyNotFoundException("Run was not found.");
        if (!RunStatusMachine.CanTransition(run.Status, status)) throw new InvalidOperationException($"Run cannot transition from '{run.Status}' to '{status}'.");
        run.Status = status;
        run.Summary = summary ?? run.Summary;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        if (RunStatusMachine.IsTerminal(status)) run.CompletedAt = run.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventIndexRecord> AppendEventAsync(
        Guid runId,
        string eventType,
        object? payload,
        string summary,
        string severity = "info",
        Guid? taskId = null,
        Guid? approvalId = null,
        string? toolId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Event type and summary are required.");
        }

        var gate = RunLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var run = await db.Runs.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Run was not found.");
            var session = await _sessions.FindAsync(run.SessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Run references a missing session.");
            var sequence = run.LastEventSequence + 1;
            var timestamp = DateTimeOffset.UtcNow;
            var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);
            var record = new EventFileRecord
            {
                Version = EventEnvelope.SchemaVersion,
                EventId = Guid.NewGuid(),
                RunId = runId,
                SessionId = session.SessionId,
                ProjectId = session.ProjectId,
                EventType = eventType,
                Severity = severity,
                Sequence = sequence,
                TaskId = taskId,
                ApprovalId = approvalId,
                ToolId = toolId,
                Summary = summary,
                Timestamp = timestamp,
                Payload = payloadElement
            };
            var serialized = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            var location = await AppendLineAsync(runId, serialized, cancellationToken).ConfigureAwait(false);
            var index = new EventIndexRecord
            {
                EventId = record.EventId, TenantId = session.TenantId, WorkspaceId = session.WorkspaceId, RunId = runId, SessionId = record.SessionId, ProjectId = record.ProjectId,
                EventType = eventType, Severity = severity, Sequence = sequence, TaskId = taskId, ApprovalId = approvalId,
                ToolId = toolId, Summary = summary, SchemaVersion = record.Version,
                PayloadHash = Convert.ToHexString(SHA256.HashData(serialized)).ToLowerInvariant(),
                RelativeFilePath = Path.Combine("events", runId + ".events.jsonl"), ByteOffset = location.Offset,
                ByteLength = location.Length, Timestamp = timestamp
            };
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            db.EventIndex.Add(index);
            run.LastEventSequence = sequence;
            run.LastEventAt = timestamp;
            run.UpdatedAt = timestamp;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return index;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<EventEnvelope>> ReplayEventsAsync(Guid? sessionId, long afterSequence, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.EventIndex.AsNoTracking().Where(x => x.Sequence > afterSequence);
        if (sessionId is { } id)
        {
            var session = await _sessions.FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (session is null) return [];
            query = query.Where(x => x.SessionId == id && x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId);
        }
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        rows = rows.OrderBy(x => x.Timestamp).ThenBy(x => x.Sequence).ToList();
        var events = new List<EventEnvelope>(rows.Count);
        foreach (var row in rows)
        {
            var filePath = _paths.EventLog(row.RunId);
            if (!File.Exists(filePath)) continue;
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(row.ByteOffset, SeekOrigin.Begin);
            var bytes = new byte[row.ByteLength];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }
            if (read != bytes.Length) continue;
            var record = JsonSerializer.Deserialize<EventFileRecord>(bytes, JsonOptions);
            if (record is null) continue;
            events.Add(new EventEnvelope { Version = record.Version, EventId = record.EventId.ToString(), EventType = record.EventType, Timestamp = record.Timestamp, SessionId = record.SessionId.ToString(), RunId = record.RunId.ToString(), Payload = new Dictionary<string, object?> { ["sequence"] = record.Sequence, ["summary"] = record.Summary, ["severity"] = record.Severity, ["payload"] = record.Payload } });
        }
        return events;
    }

    public async Task<IReadOnlyList<RunRecord>> ListNonTerminalRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var runs = await db.Runs.AsNoTracking()
            .Where(x => x.Status != "completed" && x.Status != "failed" && x.Status != "cancelled")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return runs.OrderByDescending(x => x.CreatedAt).ToList();
    }

    /// <summary>
    /// Lists active runs whose lease can be recovered. Runs younger than the
    /// admission grace period are skipped: a submit is still freezing the run
    /// configuration while the manifest handshake runs, and the recovery scan
    /// must not execute a run that has not been fully admitted yet.
    /// </summary>
    public async Task<IReadOnlyList<RunRecord>> ListLeaseEligibleRunsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var nowUnixMilliseconds = now.ToUnixTimeMilliseconds();
        var admissionGrace = now.AddSeconds(-AdmissionGracePeriodSeconds);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var runs = await db.Runs.AsNoTracking()
            .Where(x => x.Status != "completed" && x.Status != "failed" && x.Status != "cancelled"
                && x.CreatedAt <= admissionGrace
                && (x.LeaseOwner == null || x.LeaseExpiresUnixMilliseconds == null || x.LeaseExpiresUnixMilliseconds <= nowUnixMilliseconds))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return runs.OrderBy(x => x.UpdatedAt).ToList();
    }

    private const int AdmissionGracePeriodSeconds = 30;

    /// <summary>
    /// Persists one resolved run configuration as immutable content. Repeating the
    /// same freeze is safe; a different body is rejected so a restarted run cannot
    /// silently adopt a newly hot-reloaded profile.
    /// </summary>
    public async Task<FrozenRunConfiguration> FreezeRunConfigurationAsync(Guid runId, FrozenRunConfigurationWrite configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.SchemaVersion)) throw new ArgumentException("Configuration schema version is required.", nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.Content)) throw new ArgumentException("Frozen configuration content is required.", nameof(configuration));
        var bindings = configuration.Bindings ?? [];
        var schemaVersion = configuration.SchemaVersion.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Run was not found.");
        var bytes = Encoding.UTF8.GetBytes(configuration.Content);
        var requestedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(run.FrozenConfigurationReference))
        {
            if (!string.Equals(run.FrozenConfigurationHash, requestedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The run already has a different frozen configuration.");
            }
            return await ReadFrozenConfigurationAsync(run, cancellationToken).ConfigureAwait(false);
        }

        await using var body = new MemoryStream(bytes, writable: false);
        var stored = await _contentStore.PutAsync(new ContentWriteRequest(
            run.TenantId, run.WorkspaceId, "run_configuration", "application/json", body), cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var frozen = await db.Runs.Where(x => x.Id == runId && x.FrozenConfigurationReference == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FrozenConfigurationReference, stored.Value)
                .SetProperty(x => x.FrozenConfigurationHash, stored.Sha256)
                .SetProperty(x => x.FrozenConfigurationLength, stored.Length)
                .SetProperty(x => x.FrozenConfigurationSchemaVersion, schemaVersion)
                .SetProperty(x => x.FrozenConfigurationAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (frozen == 1)
        {
            foreach (var binding in bindings
                .Where(item => !string.IsNullOrWhiteSpace(item.ConfigurationKind))
                .GroupBy(item => new { Kind = item.ConfigurationKind.Trim().ToLowerInvariant(), item.ConfigurationVersionId }))
            {
                var item = binding.First();
                var exists = await db.RunConfigurationBindings.AnyAsync(x => x.RunId == runId
                    && x.ConfigurationKind == binding.Key.Kind
                    && x.ConfigurationVersionId == item.ConfigurationVersionId, cancellationToken).ConfigureAwait(false);
                if (exists) continue;
                db.RunConfigurationBindings.Add(new RunConfigurationBindingRecord
                {
                    RunId = runId,
                    TenantId = run.TenantId,
                    ConfigurationKind = binding.Key.Kind,
                    ConfigurationId = item.ConfigurationId,
                    ConfigurationVersionId = item.ConfigurationVersionId,
                    ManifestHash = item.ManifestHash ?? string.Empty,
                    BoundAt = now
                });
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FrozenRunConfiguration(run.Id.ToString(), schemaVersion, configuration.Content,
                stored.Value, stored.Sha256, stored.Length, now, bindings.ToArray());
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await using var winnerDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var winner = await winnerDb.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Run was not found.");
        if (!string.Equals(winner.FrozenConfigurationHash, requestedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The run already has a different frozen configuration.");
        }
        return await ReadFrozenConfigurationAsync(winner, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FrozenRunConfiguration?> GetFrozenRunConfigurationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || string.IsNullOrWhiteSpace(run.FrozenConfigurationReference)) return null;
        return await ReadFrozenConfigurationAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunCheckpoint> SaveRunCheckpointAsync(Guid runId, RunCheckpointWrite checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.ExpectedRevision < 0 || checkpoint.ExpectedRevision == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(checkpoint));
        if (string.IsNullOrWhiteSpace(checkpoint.Phase)) throw new ArgumentException("Checkpoint phase is required.", nameof(checkpoint));
        if (string.IsNullOrWhiteSpace(checkpoint.Content)) throw new ArgumentException("Checkpoint content is required.", nameof(checkpoint));
        var key = string.IsNullOrWhiteSpace(checkpoint.IdempotencyKey)
            ? $"checkpoint:{checkpoint.ExpectedRevision + 1}:{checkpoint.Phase.Trim().ToLowerInvariant()}"
            : checkpoint.IdempotencyKey.Trim();
        if (key.Length > 256) throw new ArgumentException("Checkpoint idempotency key is too long.", nameof(checkpoint));
        var phase = checkpoint.Phase.Trim();
        if (phase.Length > 64) throw new ArgumentException("Checkpoint phase is too long.", nameof(checkpoint));
        var appliedThroughEventSequence = Math.Max(0, checkpoint.AppliedThroughEventSequence);
        var bytes = Encoding.UTF8.GetBytes(checkpoint.Content);
        var requestedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var metadataDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await metadataDb.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Run was not found.");
        var existing = await metadataDb.RunCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RunId == runId && x.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await ReadMatchingCheckpointAsync(existing, runId, checkpoint, phase, requestedHash, appliedThroughEventSequence, cancellationToken).ConfigureAwait(false);
        }

        await using var body = new MemoryStream(bytes, writable: false);
        var stored = await _contentStore.PutAsync(new ContentWriteRequest(
            run.TenantId, run.WorkspaceId, "run_checkpoint", "application/json", body), cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new RunCheckpointRecord
        {
            Id = Guid.NewGuid(), TenantId = run.TenantId, WorkspaceId = run.WorkspaceId, RunId = runId,
            Revision = checkpoint.ExpectedRevision + 1, Phase = phase, IdempotencyKey = key,
            ContentReference = stored.Value, ContentHash = stored.Sha256, ContentLength = stored.Length,
            AppliedThroughEventSequence = appliedThroughEventSequence, CreatedAt = now
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.Runs.Where(x => x.Id == runId && x.CheckpointRevision == checkpoint.ExpectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CurrentCheckpointId, record.Id)
                .SetProperty(x => x.CheckpointRevision, record.Revision)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (updated == 1)
        {
            db.RunCheckpoints.Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RunCheckpoint(record.Id, runId.ToString(), record.Revision, record.Phase, checkpoint.Content,
                record.ContentReference, record.ContentHash, record.ContentLength, record.AppliedThroughEventSequence, record.IdempotencyKey, record.CreatedAt);
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await using var conflictDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var winner = await conflictDb.RunCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RunId == runId && x.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
        if (winner is not null)
        {
            return await ReadMatchingCheckpointAsync(winner, runId, checkpoint, phase, requestedHash, appliedThroughEventSequence, cancellationToken).ConfigureAwait(false);
        }
        var actualRevision = await conflictDb.Runs.AsNoTracking().Where(x => x.Id == runId)
            .Select(x => (long?)x.CheckpointRevision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (actualRevision is null) throw new KeyNotFoundException("Run was not found.");
        throw new RunCheckpointConflictException(runId.ToString(), checkpoint.ExpectedRevision, actualRevision.Value);
    }

    public async Task<RunCheckpoint?> GetCurrentRunCheckpointAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run?.CurrentCheckpointId is not { } checkpointId) return null;
        var checkpoint = await db.RunCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == checkpointId && x.RunId == runId, cancellationToken).ConfigureAwait(false);
        return checkpoint is null ? null : await ReadCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunLease> TryAcquireRunLeaseAsync(Guid runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeLeaseOwner(ownerId);
        var leaseDuration = NormalizeLeaseDuration(duration);
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(leaseDuration);
        var nowUnixMilliseconds = now.ToUnixTimeMilliseconds();
        var expiresUnixMilliseconds = expires.ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.Runs.Where(x => x.Id == runId
                && x.Status != "completed" && x.Status != "failed" && x.Status != "cancelled"
                && (x.LeaseOwner == null || x.LeaseExpiresUnixMilliseconds == null || x.LeaseExpiresUnixMilliseconds <= nowUnixMilliseconds || x.LeaseOwner == owner))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseOwner, owner)
                .SetProperty(x => x.LeaseExpiresAt, expires)
                .SetProperty(x => x.LeaseHeartbeatAt, now)
                .SetProperty(x => x.LeaseExpiresUnixMilliseconds, expiresUnixMilliseconds)
                .SetProperty(x => x.LeaseHeartbeatUnixMilliseconds, nowUnixMilliseconds)
                .SetProperty(x => x.RecoveryCount, x => x.RecoveryCount + 1)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        var current = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Run was not found.");
        return new RunLease(runId.ToString(), owner, updated == 1, current.LeaseExpiresAt, current.LeaseHeartbeatAt, current.RecoveryCount);
    }

    public async Task<bool> HeartbeatRunLeaseAsync(Guid runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeLeaseOwner(ownerId);
        var leaseDuration = NormalizeLeaseDuration(duration);
        var now = DateTimeOffset.UtcNow;
        var nowUnixMilliseconds = now.ToUnixTimeMilliseconds();
        var expiresUnixMilliseconds = now.Add(leaseDuration).ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.Runs.Where(x => x.Id == runId && x.LeaseOwner == owner && x.LeaseExpiresUnixMilliseconds != null && x.LeaseExpiresUnixMilliseconds > nowUnixMilliseconds)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseExpiresAt, now.Add(leaseDuration))
                .SetProperty(x => x.LeaseHeartbeatAt, now)
                .SetProperty(x => x.LeaseExpiresUnixMilliseconds, expiresUnixMilliseconds)
                .SetProperty(x => x.LeaseHeartbeatUnixMilliseconds, nowUnixMilliseconds)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    public async Task ReleaseRunLeaseAsync(Guid runId, string ownerId, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeLeaseOwner(ownerId);
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Runs.Where(x => x.Id == runId && x.LeaseOwner == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.LeaseHeartbeatAt, (DateTimeOffset?)null)
                .SetProperty(x => x.LeaseExpiresUnixMilliseconds, (long?)null)
                .SetProperty(x => x.LeaseHeartbeatUnixMilliseconds, (long?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a durable stream chunk. The run-scoped cursor is independent from the
    /// event journal so a reconnect can replay exactly the user-facing turn output.
    /// </summary>
    public async Task<DurableRunStreamChunk> AppendRunStreamAsync(Guid runId, DurableRunStreamAppend item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.TurnId == Guid.Empty) throw new ArgumentException("Stream turn id is required.", nameof(item));
        if (string.IsNullOrWhiteSpace(item.Kind)) throw new ArgumentException("Stream kind is required.", nameof(item));
        if (item.SafeErrorMessage?.Length > 4096) throw new ArgumentException("Safe error message is too long.", nameof(item));
        var key = string.IsNullOrWhiteSpace(item.IdempotencyKey) ? null : item.IdempotencyKey.Trim();
        if (key?.Length > 256) throw new ArgumentException("Stream idempotency key is too long.", nameof(item));
        var kind = item.Kind.Trim().ToLowerInvariant();
        var deltaText = string.IsNullOrEmpty(item.Delta) ? null : item.Delta;
        var usageJson = string.IsNullOrWhiteSpace(item.UsageJson) ? null : item.UsageJson;

        await using var metadataDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await metadataDb.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Run was not found.");
        if (key is not null)
        {
            var existing = await metadataDb.RunStream.AsNoTracking().SingleOrDefaultAsync(x => x.RunId == runId && x.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return await ReadRunStreamChunkAsync(existing, cancellationToken).ConfigureAwait(false);
            }
        }

        ContentReference? delta = null;
        if (deltaText is not null)
        {
            await using var deltaBody = new MemoryStream(Encoding.UTF8.GetBytes(deltaText), writable: false);
            delta = await _contentStore.PutAsync(new ContentWriteRequest(
                run.TenantId, run.WorkspaceId, "run_stream_delta", "text/plain; charset=utf-8", deltaBody), cancellationToken).ConfigureAwait(false);
        }
        ContentReference? usage = null;
        if (usageJson is not null)
        {
            await using var usageBody = new MemoryStream(Encoding.UTF8.GetBytes(usageJson), writable: false);
            usage = await _contentStore.PutAsync(new ContentWriteRequest(
                run.TenantId, run.WorkspaceId, "run_stream_usage", "application/json", usageBody), cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                if (key is not null)
                {
                    var existing = await db.RunStream.AsNoTracking().SingleOrDefaultAsync(x => x.RunId == runId && x.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        return await ReadRunStreamChunkAsync(existing, cancellationToken).ConfigureAwait(false);
                    }
                }
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var cursor = await db.RunStreamCursors.SingleOrDefaultAsync(x => x.RunId == runId, cancellationToken).ConfigureAwait(false);
                if (cursor is null)
                {
                    cursor = new RunStreamCursorRecord { RunId = runId, NextSequence = 0, UpdatedAt = DateTimeOffset.UtcNow };
                    db.RunStreamCursors.Add(cursor);
                }
                var now = DateTimeOffset.UtcNow;
                var record = new RunStreamRecord
                {
                    Id = Guid.NewGuid(), TenantId = run.TenantId, WorkspaceId = run.WorkspaceId, RunId = runId,
                    TurnId = item.TurnId, MessageId = item.MessageId, Sequence = cursor.NextSequence + 1,
                    Kind = kind, IdempotencyKey = key,
                    DeltaReference = delta?.Value, DeltaHash = delta?.Sha256, DeltaLength = delta?.Length,
                    UsageReference = usage?.Value, UsageHash = usage?.Sha256, UsageLength = usage?.Length,
                    FinishReason = item.FinishReason, ErrorCategory = item.ErrorCategory, SafeErrorMessage = item.SafeErrorMessage,
                    CreatedAt = now
                };
                cursor.NextSequence = record.Sequence;
                cursor.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                db.RunStream.Add(record);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DurableRunStreamChunk(runId.ToString(), record.TurnId, record.MessageId, record.Sequence, record.Kind,
                    deltaText, usageJson, record.FinishReason, record.ErrorCategory, record.SafeErrorMessage, record.CreatedAt);
            }
            catch (DbUpdateConcurrencyException) when (attempt < StreamAppendMaxAttempts)
            {
                await DelayBeforeStreamRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException) when (attempt < StreamAppendMaxAttempts)
            {
                await DelayBeforeStreamRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < StreamAppendMaxAttempts && IsTransientStreamContention(exception))
            {
                await DelayBeforeStreamRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<DurableRunStreamChunk>> ReplayRunStreamAsync(Guid runId, Guid? turnId, long afterSequence, CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null) throw new KeyNotFoundException("Run was not found.");
        var query = db.RunStream.AsNoTracking().Where(x => x.RunId == runId && x.Sequence > afterSequence);
        if (turnId is { } selectedTurn) query = query.Where(x => x.TurnId == selectedTurn);
        var records = await query.OrderBy(x => x.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var chunks = new List<DurableRunStreamChunk>(records.Count);
        foreach (var record in records)
        {
            chunks.Add(await ReadRunStreamChunkAsync(record, cancellationToken).ConfigureAwait(false));
        }
        return chunks;
    }

    public async Task<ToolExecutionRecord> StartToolExecutionAsync(ToolExecutionStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var run = await FindRunAsync(start.RunId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Run was not found.");
        var session = await _sessions.FindAsync(start.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        var parametersBytes = System.Text.Encoding.UTF8.GetBytes(start.ParametersJson);
        var reference = await _contentStore.PutAsync(
            new ContentWriteRequest(run.TenantId, run.WorkspaceId, "tool_parameters", "application/json", new MemoryStream(parametersBytes)),
            cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new ToolExecutionRecord
        {
            Id = Guid.NewGuid(), TenantId = run.TenantId, WorkspaceId = run.WorkspaceId, ProjectId = session.ProjectId,
            SessionId = start.SessionId, RunId = start.RunId, TaskId = start.TaskId, AgentInstanceId = start.AgentInstanceId,
            ApprovalId = start.ApprovalId, ToolId = start.ToolId, Risk = start.Risk, RequiresApproval = start.RequiresApproval,
            Status = "running",
            ParametersHash = Convert.ToHexString(SHA256.HashData(parametersBytes)).ToLowerInvariant(),
            ParametersReference = reference.Value, ParametersLength = reference.Length,
            Attempt = 1, CreatedAt = now, UpdatedAt = now
        };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ToolExecutions.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task CompleteToolExecutionAsync(Guid executionId, ToolExecutionCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var run = await FindToolExecutionRunAsync(executionId, cancellationToken).ConfigureAwait(false);
        var resultBytes = System.Text.Encoding.UTF8.GetBytes(completion.ResultJson);
        var reference = await _contentStore.PutAsync(
            new ContentWriteRequest(run.TenantId, run.WorkspaceId, "tool_results", completion.ResultMediaType, new MemoryStream(resultBytes)),
            cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var record = await db.ToolExecutions.SingleOrDefaultAsync(x => x.Id == executionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Tool execution was not found.");
        record.Status = "completed";
        record.ResultReference = reference.Value;
        record.ResultHash = reference.Sha256;
        record.ResultLength = reference.Length;
        record.CompletedAt = DateTimeOffset.UtcNow;
        record.UpdatedAt = record.CompletedAt.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailToolExecutionAsync(Guid executionId, string errorCategory, string safeMessage, CancellationToken cancellationToken = default)
    {
        var run = await FindToolExecutionRunAsync(executionId, cancellationToken).ConfigureAwait(false);
        var errorJson = JsonSerializer.Serialize(new { error_category = errorCategory, message = safeMessage }, JsonOptions);
        var resultBytes = System.Text.Encoding.UTF8.GetBytes(errorJson);
        var reference = await _contentStore.PutAsync(
            new ContentWriteRequest(run.TenantId, run.WorkspaceId, "tool_results", "application/json", new MemoryStream(resultBytes)),
            cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var record = await db.ToolExecutions.SingleOrDefaultAsync(x => x.Id == executionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Tool execution was not found.");
        record.Status = "failed";
        record.ResultReference = reference.Value;
        record.ResultHash = reference.Sha256;
        record.ResultLength = reference.Length;
        record.CompletedAt = DateTimeOffset.UtcNow;
        record.UpdatedAt = record.CompletedAt.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RunRecord> FindToolExecutionRunAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var runId = await db.ToolExecutions.AsNoTracking().Where(x => x.Id == executionId).Select(x => x.RunId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return await FindRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Run was not found for the tool execution.");
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var runs = await db.Runs.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var run in runs)
        {
            var path = _paths.EventLog(run.Id);
            if (!File.Exists(path)) continue;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', offset);
                if (newline < 0)
                {
                    _diagnostics.Add($"Ignoring incomplete JSONL tail for run {run.Id}.");
                    break;
                }
                var length = newline - offset + 1;
                try
                {
                    var record = JsonSerializer.Deserialize<EventFileRecord>(bytes.AsSpan(offset, newline - offset), JsonOptions)
                        ?? throw new JsonException("Event record was empty.");
                    if (!await db.EventIndex.AnyAsync(x => x.EventId == record.EventId, cancellationToken).ConfigureAwait(false))
                    {
                        db.EventIndex.Add(new EventIndexRecord { EventId = record.EventId, TenantId = run.TenantId, WorkspaceId = run.WorkspaceId, RunId = record.RunId, SessionId = record.SessionId, ProjectId = record.ProjectId, EventType = record.EventType, Severity = record.Severity, Sequence = record.Sequence, TaskId = record.TaskId, ApprovalId = record.ApprovalId, ToolId = record.ToolId, Summary = record.Summary, SchemaVersion = record.Version, PayloadHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, newline - offset))).ToLowerInvariant(), RelativeFilePath = Path.Combine("events", run.Id + ".events.jsonl"), ByteOffset = offset, ByteLength = length, Timestamp = record.Timestamp });
                    }
                }
                catch (JsonException)
                {
                    _diagnostics.Add($"Ignoring malformed JSONL record for run {run.Id} at byte {offset}.");
                }
                offset += length;
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(long Offset, int Length)> AppendLineAsync(Guid runId, byte[] record, CancellationToken cancellationToken)
    {
        var path = _paths.EventLog(runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Seek(0, SeekOrigin.End);
        var offset = stream.Position;
        await stream.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        return (offset, checked(record.Length + 1));
    }

    public async Task WriteTaskSnapshotAsync(Guid runId, TaskSnapshotFile snapshot, CancellationToken cancellationToken = default)
    {
        var path = _paths.TaskSnapshot(runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task UpdateTaskAsync(Guid runId, object taskNode, CancellationToken cancellationToken = default)
    {
        var path = _paths.TaskSnapshot(runId);
        TaskSnapshotFile snapshot;
        if (File.Exists(path))
        {
            await using var s = File.OpenRead(path);
            snapshot = JsonSerializer.DeserializeAsync<TaskSnapshotFile>(s, JsonOptions, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult() ?? new TaskSnapshotFile { RunId = runId };
        }
        else
        {
            snapshot = new TaskSnapshotFile { RunId = runId };
        }
        snapshot.Tasks.Add(taskNode);
        await WriteTaskSnapshotAsync(runId, snapshot, cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run != null)
        {
            run.TaskRevision++;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FrozenRunConfiguration> ReadFrozenConfigurationAsync(RunRecord run, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(run.FrozenConfigurationReference)
            || string.IsNullOrWhiteSpace(run.FrozenConfigurationHash)
            || run.FrozenConfigurationLength is not { } length
            || string.IsNullOrWhiteSpace(run.FrozenConfigurationSchemaVersion)
            || run.FrozenConfigurationAt is not { } frozenAt)
        {
            throw new InvalidDataException("Run frozen configuration metadata is incomplete.");
        }
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var bindings = await db.RunConfigurationBindings.AsNoTracking().Where(x => x.RunId == run.Id)
            .OrderBy(x => x.ConfigurationKind).ThenBy(x => x.ConfigurationVersionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var content = await ReadContentAsync(run.FrozenConfigurationReference, run.FrozenConfigurationHash, length, "application/json", cancellationToken).ConfigureAwait(false);
        return new FrozenRunConfiguration(run.Id.ToString(), run.FrozenConfigurationSchemaVersion, content,
            run.FrozenConfigurationReference, run.FrozenConfigurationHash, length, frozenAt,
            bindings.Select(x => new RunConfigurationBinding(x.ConfigurationKind, x.ConfigurationId, x.ConfigurationVersionId, x.ManifestHash)).ToArray());
    }

    private async Task<RunCheckpoint> ReadCheckpointAsync(RunCheckpointRecord checkpoint, CancellationToken cancellationToken)
    {
        var content = await ReadContentAsync(checkpoint.ContentReference, checkpoint.ContentHash, checkpoint.ContentLength, "application/json", cancellationToken).ConfigureAwait(false);
        return new RunCheckpoint(checkpoint.Id, checkpoint.RunId.ToString(), checkpoint.Revision, checkpoint.Phase, content,
            checkpoint.ContentReference, checkpoint.ContentHash, checkpoint.ContentLength, checkpoint.AppliedThroughEventSequence,
            checkpoint.IdempotencyKey, checkpoint.CreatedAt);
    }

    private async Task<RunCheckpoint> ReadMatchingCheckpointAsync(
        RunCheckpointRecord checkpoint,
        Guid runId,
        RunCheckpointWrite request,
        string phase,
        string requestedHash,
        long appliedThroughEventSequence,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Revision != request.ExpectedRevision + 1
            || !string.Equals(checkpoint.ContentHash, requestedHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(checkpoint.Phase, phase, StringComparison.OrdinalIgnoreCase)
            || checkpoint.AppliedThroughEventSequence != appliedThroughEventSequence)
        {
            throw new InvalidOperationException($"Checkpoint idempotency key '{checkpoint.IdempotencyKey}' was reused with different checkpoint data for run '{runId}'.");
        }
        return await ReadCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DurableRunStreamChunk> ReadRunStreamChunkAsync(RunStreamRecord record, CancellationToken cancellationToken)
    {
        var delta = string.IsNullOrWhiteSpace(record.DeltaReference)
            ? null
            : await ReadContentAsync(record.DeltaReference, record.DeltaHash ?? string.Empty, record.DeltaLength ?? 0,
                "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
        var usage = string.IsNullOrWhiteSpace(record.UsageReference)
            ? null
            : await ReadContentAsync(record.UsageReference, record.UsageHash ?? string.Empty, record.UsageLength ?? 0,
                "application/json", cancellationToken).ConfigureAwait(false);
        return new DurableRunStreamChunk(record.RunId.ToString(), record.TurnId, record.MessageId, record.Sequence,
            record.Kind, delta, usage, record.FinishReason, record.ErrorCategory, record.SafeErrorMessage, record.CreatedAt);
    }

    private async Task<string> ReadContentAsync(string reference, string hash, long length, string mediaType, CancellationToken cancellationToken)
    {
        await using var stream = await _contentStore.OpenReadAsync(new ContentReference(reference, hash, length, mediaType), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeLeaseOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Lease owner id is required.", nameof(ownerId));
        var owner = ownerId.Trim();
        if (owner.Length > 256) throw new ArgumentException("Lease owner id is too long.", nameof(ownerId));
        return owner;
    }

    private static TimeSpan NormalizeLeaseDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be between one second and thirty minutes.");
        }
        return duration;
    }

    private static Task DelayBeforeStreamRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, attempt * 10)), cancellationToken);

    private static bool IsTransientStreamContention(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            if (type.FullName == "Microsoft.Data.Sqlite.SqliteException"
                && type.GetProperty("SqliteErrorCode")?.GetValue(current) is int sqliteCode
                && (sqliteCode == 5 || sqliteCode == 6))
            {
                return true;
            }
            if (type.FullName == "Npgsql.PostgresException"
                && type.GetProperty("SqlState")?.GetValue(current) is string sqlState
                && (sqlState == "40001" || sqlState == "40P01"))
            {
                return true;
            }
        }
        return false;
    }
}

public sealed record RunStartResultRecord(RunRecord Run, bool Existing);

public sealed class EventFileRecord
{
    public string Version { get; set; } = EventEnvelope.SchemaVersion;
    public Guid EventId { get; set; }
    public Guid RunId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ProjectId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public long Sequence { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? ApprovalId { get; set; }
    public string? ToolId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public JsonElement Payload { get; set; }
}

public sealed class TaskSnapshotFile
{
    public int Version { get; set; } = 1;
    public Guid RunId { get; set; }
    public long AppliedThroughSeq { get; set; }
    public List<object> Tasks { get; set; } = [];
}

public sealed record RunStartOptions(
    Guid? TurnId,
    long ContextRevision,
    long ConfigurationVersion,
    string ConfigurationHash,
    string ApplicationMode,
    string AgentMode,
    string PermissionMode,
    string RuntimeProfileId);

public static class RunStatusMachine
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending", "running", "understanding", "executing", "replanning", "awaiting_approval", "paused", "reviewing", "finalizing", "completed", "failed", "cancelled"
    };

    public static bool IsKnown(string status) => Known.Contains(status);
    public static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";
    public static bool CanTransition(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsTerminal(from)) return false;
        return to switch
        {
            "cancelled" => true,
            "failed" => true,
            "paused" => from is "understanding" or "executing" or "replanning" or "awaiting_approval" or "reviewing" or "finalizing",
            "understanding" => from is "pending" or "running",
            "executing" => from is "understanding" or "replanning" or "paused" or "awaiting_approval" or "reviewing" or "running",
            "replanning" => from is "understanding" or "executing" or "awaiting_approval" or "reviewing",
            "awaiting_approval" => from is "understanding" or "executing" or "replanning",
            "reviewing" => from is "executing",
            "finalizing" => from is "executing" or "reviewing" or "replanning" or "running",
            "completed" => from is "executing" or "reviewing" or "finalizing" or "running",
            _ => false
        };
    }
}

public sealed class StorageDiagnostics
{
    private readonly ConcurrentQueue<string> _messages = new();
    public IReadOnlyList<string> Messages => _messages.ToArray();
    public void Add(string message) => _messages.Enqueue(message);
}
