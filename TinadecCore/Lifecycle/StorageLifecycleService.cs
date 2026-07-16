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
    private readonly IDbContextFactory<LifecycleDbContext> _dbFactory;
    private readonly ISessionLocator _sessions;
    private readonly StoragePaths _paths;
    private readonly StorageDiagnostics _diagnostics;

    public StorageLifecycleService(
        IDbContextFactory<LifecycleDbContext> dbFactory,
        ISessionLocator sessions,
        StoragePaths paths,
        StorageDiagnostics diagnostics)
    {
        _dbFactory = dbFactory;
        _sessions = sessions;
        _paths = paths;
        _diagnostics = diagnostics;
    }

    public async Task<RunRecord> StartRunAsync(Guid sessionId, Guid triggerMessageId, CancellationToken cancellationToken = default)
    {
        if (await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Session was not found.");
        }

        var now = DateTimeOffset.UtcNow;
        var run = new RunRecord { Id = Guid.NewGuid(), SessionId = sessionId, TriggerMessageId = triggerMessageId, Status = "running", CreatedAt = now, UpdatedAt = now };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteTaskSnapshotAsync(run.Id, new TaskSnapshotFile { RunId = run.Id }, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_paths.Artifacts(run.Id));
        return run;
    }

    public async Task<IReadOnlyList<RunRecord>> ListRunsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var runs = await db.Runs.AsNoTracking().Where(x => x.SessionId == sessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return runs.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task<RunRecord?> FindRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Runs.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
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
                EventId = record.EventId, RunId = runId, SessionId = record.SessionId, ProjectId = record.ProjectId,
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
        if (sessionId is { } id) query = query.Where(x => x.SessionId == id);
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
                        db.EventIndex.Add(new EventIndexRecord { EventId = record.EventId, RunId = record.RunId, SessionId = record.SessionId, ProjectId = record.ProjectId, EventType = record.EventType, Severity = record.Severity, Sequence = record.Sequence, TaskId = record.TaskId, ApprovalId = record.ApprovalId, ToolId = record.ToolId, Summary = record.Summary, SchemaVersion = record.Version, PayloadHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, newline - offset))).ToLowerInvariant(), RelativeFilePath = Path.Combine("events", run.Id + ".events.jsonl"), ByteOffset = offset, ByteLength = length, Timestamp = record.Timestamp });
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

    private async Task WriteTaskSnapshotAsync(Guid runId, TaskSnapshotFile snapshot, CancellationToken cancellationToken)
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
}

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

public sealed class StorageDiagnostics
{
    private readonly ConcurrentQueue<string> _messages = new();
    public IReadOnlyList<string> Messages => _messages.ToArray();
    public void Add(string message) => _messages.Enqueue(message);
}
