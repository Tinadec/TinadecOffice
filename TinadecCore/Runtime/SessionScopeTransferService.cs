using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.TinaChat;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Runtime;

public sealed record SessionScopeTransferReceipt(Guid TransferId, Guid SessionId, string SourceStorageId,
    string TargetStorageId, Guid ProjectId, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Error = null);

public interface ISessionScopeTransferService
{
    Task<SessionScopeTransferReceipt> RequestAsync(Guid sessionId, string targetStorageId, CancellationToken cancellationToken = default);
    SessionScopeTransferReceipt Get(Guid transferId);
    void AssertSessionWritable(Guid sessionId, string storageId);
    void EnsurePurgeAllowed(Guid sessionId, string storageId);
    Task ProcessPendingAsync(CancellationToken cancellationToken = default);
    Task PurgeCompletedAsync(Guid sessionId, string storageId, CancellationToken cancellationToken = default);
}

public static class SessionScopeTransferRegistration
{
    /// <summary>Register once in the host. Project runtimes share its admission guard.</summary>
    public static IServiceCollection AddTinadecSessionTransfers(this IServiceCollection services)
    {
        services.AddSingleton<SessionScopeTransferService>();
        services.AddSingleton<ISessionScopeTransferService>(sp => sp.GetRequiredService<SessionScopeTransferService>());
        services.AddSingleton<ISessionStorageAdmissionGuard>(sp => sp.GetRequiredService<SessionScopeTransferService>());
        services.AddSingleton<ISessionWorkspaceBinder, HostSessionWorkspaceBinder>();
        services.AddHostedService<SessionScopeTransferWorker>();
        return services;
    }
}

/// <summary>A durable host transaction: complete destination first, then remove the source graph.</summary>
public sealed class SessionScopeTransferService : ISessionScopeTransferService, ISessionStorageAdmissionGuard
{
    private readonly IStorageScopeRegistry _registry;
    private readonly ILogger<SessionScopeTransferService> _logger;
    private readonly ConcurrentDictionary<Guid, SessionScopeTransferReceipt> _transfers = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionLocks = new();
    private readonly SemaphoreSlim _journalLock = new(1, 1);
    private readonly string _journal;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public SessionScopeTransferService(IStorageScopeRegistry registry, ILogger<SessionScopeTransferService> logger)
    {
        _registry = registry; _logger = logger;
        _journal = Path.Combine(registry.User.State, "session-transfers.toml");
        LoadJournal();
    }

    public SessionScopeTransferReceipt Get(Guid transferId) => _transfers.TryGetValue(transferId, out var value)
        ? value : throw new KeyNotFoundException("Session transfer was not found.");

    public void AssertSessionWritable(Guid sessionId, string storageId)
    {
        var transfer = _transfers.Values.FirstOrDefault(t => t.SessionId == sessionId && t.Status != "failed"
            && (storageId == t.SourceStorageId || storageId == t.TargetStorageId));
        if (transfer is { Status: "completed" } && storageId != transfer.TargetStorageId)
            throw new RunAdmissionException("session_storage_changed", "The session belongs to storage scope '" + transfer.TargetStorageId + "'.");
        if (transfer is not null && transfer.Status != "completed")
            throw new RunAdmissionException("session_transfer_pending", "Session editing is paused until its project transfer completes.");
    }

    public void EnsurePurgeAllowed(Guid sessionId, string storageId)
    {
        var receipts = _transfers.Values.Where(t => t.SessionId == sessionId
            && (storageId == t.SourceStorageId || storageId == t.TargetStorageId)).ToArray();
        if (receipts.Any(t => t.Status != "completed"))
            throw new RunAdmissionException("session_transfer_pending", "Resolve the session transfer before purging its facts or history.");
        if (receipts.Any(t => t.TargetStorageId != storageId))
            throw new RunAdmissionException("session_storage_changed", "Session transfer history can only be purged by its destination storage owner.");
    }

    /// <summary>Invoked by the host's session purge after the destination facts are deleted.</summary>
    public async Task PurgeCompletedAsync(Guid sessionId, string storageId, CancellationToken cancellationToken = default)
    {
        EnsurePurgeAllowed(sessionId, storageId);
        var receipts = _transfers.Values.Where(t => t.SessionId == sessionId
            && (storageId == t.SourceStorageId || storageId == t.TargetStorageId)).ToArray();
        if (receipts.Any(t => t.Status != "completed"))
            throw new RunAdmissionException("session_transfer_pending", "Wait for the session transfer to complete before purging its history.");
        foreach (var receipt in receipts)
        {
            if (receipt.TargetStorageId != storageId)
                throw new InvalidOperationException("Session transfer history can only be purged by its destination storage owner.");
            var target = _registry.List().Single(scope => scope.StorageId == storageId);
            foreach (var (scopeRoot, archive) in new[]
                {
                    (_registry.User.Root, StorageScopePaths.Contained(_registry.User.State, Path.Combine("transfers", receipt.TransferId.ToString("N")))),
                    (target.Root, StorageScopePaths.Contained(target.Data, Path.Combine("transfers", receipt.TransferId.ToString("N"))))
                })
            {
                cancellationToken.ThrowIfCancellationRequested(); StorageScopePaths.RejectLinks(scopeRoot, archive);
                if (Directory.Exists(archive)) Directory.Delete(archive, recursive: true);
            }
            _transfers.TryRemove(receipt.TransferId, out _);
        }
        if (receipts.Length > 0) await SaveJournalAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable> AcquireAsync(Guid sessionId, string storageId, bool startingRun, CancellationToken cancellationToken = default)
    {
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transfer = _transfers.Values.FirstOrDefault(t => t.SessionId == sessionId && t.Status != "failed"
                && (storageId == t.SourceStorageId || storageId == t.TargetStorageId));
            if (transfer is { Status: "completed" } && storageId != transfer.TargetStorageId)
                throw new RunAdmissionException("session_storage_changed", "The session belongs to storage scope '" + transfer.TargetStorageId + "'.");
            if (startingRun && transfer is not null && transfer.Status != "completed")
                throw new RunAdmissionException("session_transfer_pending", "The session is moving to a project after its active run finishes. New runs are paused until the transfer completes.");
            return new GateLease(gate);
        }
        catch { gate.Release(); throw; }
    }

    public async Task<SessionScopeTransferReceipt> RequestAsync(Guid sessionId, string targetStorageId, CancellationToken cancellationToken = default)
    {
        if (targetStorageId == "user") throw new ArgumentException("The destination must be a registered project storage scope.");
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _transfers.Values.FirstOrDefault(t => t.SessionId == sessionId && t.Status != "failed");
            if (previous is not null)
            {
                if (previous.TargetStorageId != targetStorageId) throw new InvalidOperationException("The session already has a transfer to another project.");
                return previous;
            }
            await using var source = await _registry.AcquireAsync("user", cancellationToken).ConfigureAwait(false);
            await using var target = await _registry.AcquireAsync(targetStorageId, cancellationToken).ConfigureAwait(false);
            if (target.Descriptor.ProjectId is not { } projectId) throw new InvalidOperationException("The destination is not a project scope.");
            var actor = source.Services.GetRequiredService<ITenantContextAccessor>().Current;
            await using var memory = await source.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var session = await memory.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId && s.TenantId == actor.TenantId
                && s.WorkspaceId == actor.WorkspaceId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Session was not found.");
            if (session.ProjectId is not null) throw new InvalidOperationException("Only a free session can move into a project scope.");
            if (await HasQueuedInteractionsAsync(source.Services, sessionId, cancellationToken).ConfigureAwait(false))
                throw new RunAdmissionException("queued_interactions_pending", "Drain the session's accepted interaction queue before requesting a project transfer.");
            var now = DateTimeOffset.UtcNow;
            var receipt = new SessionScopeTransferReceipt(Guid.NewGuid(), sessionId, "user", targetStorageId, projectId, "pending", now, now);
            _transfers[receipt.TransferId] = receipt;
            await SaveJournalAsync(cancellationToken).ConfigureAwait(false);
            // The HTTP request itself owns a normal source lease. The worker starts only after
            // that request releases it, so its exclusive storage leases cannot deadlock this request.
            return receipt;
        }
        finally { gate.Release(); }
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        foreach (var pending in _transfers.Values.Where(t => t.Status is "pending" or "copying" or "copied").ToArray())
        {
            var gate = _sessionLocks.GetOrAdd(pending.SessionId, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) continue;
            try { await ProcessOneAsync(pending, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Keep the durable phase for a restart/retry; the source remains protected from new admission.
                var current = _transfers[pending.TransferId] with { Error = ex.Message, UpdatedAt = DateTimeOffset.UtcNow };
                _transfers[pending.TransferId] = current;
                await SaveJournalAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(ex, "Session storage transfer {TransferId} will be retried", pending.TransferId);
            }
            finally { gate.Release(); }
        }
    }

    private async Task<SessionScopeTransferReceipt> ProcessOneAsync(SessionScopeTransferReceipt receipt, CancellationToken cancellationToken)
    {
        // Probe readiness without excluding ongoing run callbacks or event streams.
        await using (var probe = await _registry.AcquireAsync(receipt.SourceStorageId, cancellationToken).ConfigureAwait(false))
        {
            await using var lifecycle = await probe.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var statuses = await lifecycle.Runs.AsNoTracking().Where(r => r.SessionId == receipt.SessionId).Select(r => r.Status).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (statuses.Any(s => s is not ("completed" or "failed" or "cancelled"))
                || await HasQueuedInteractionsAsync(probe.Services, receipt.SessionId, cancellationToken).ConfigureAwait(false)) return receipt;
        }
        await using var source = await _registry.AcquireExclusiveAsync(receipt.SourceStorageId, cancellationToken).ConfigureAwait(false);
        await using var target = await _registry.AcquireExclusiveAsync(receipt.TargetStorageId, cancellationToken).ConfigureAwait(false);
        if (receipt.SourceStorageId != "user" || target.Descriptor.ProjectId != receipt.ProjectId)
            throw new InvalidDataException("A session transfer journal disagrees with its host-authorized project destination.");
        var archiveRoot = StorageScopePaths.Contained(source.Descriptor.State, Path.Combine("transfers", receipt.TransferId.ToString("N")));
        var graphFile = Path.Combine(archiveRoot, "graph.toml");
        var targetGraphFile = Path.Combine(archiveRoot, "target-graph.toml");
        SessionDataGraph graph;
        if (receipt.Status == "copied") graph = ReadGraph(graphFile);
        else
        {
            Directory.CreateDirectory(archiveRoot);
            if (receipt.Status == "copying" && File.Exists(graphFile)) graph = ReadGraph(graphFile);
            else
            {
                graph = await ScopeSessionDataGraph.ReadAsync(source.Services, receipt.SessionId, cancellationToken).ConfigureAwait(false);
                WriteGraph(graphFile, graph);
            }
            receipt = await SetPhaseAsync(receipt, "copying", cancellationToken).ConfigureAwait(false);
            await CopyContentAndResourcesAsync(source, target, graph, cancellationToken).ConfigureAwait(false);
            await CopyConfigurationArchiveAsync(source, target, receipt, cancellationToken).ConfigureAwait(false);
            var targetArchive = StorageScopePaths.Contained(target.Descriptor.Data, Path.Combine("transfers", receipt.TransferId.ToString("N"), "graph.toml"));
            await CopyFileWithoutOverwriteAsync(source.Descriptor.Root, graphFile, target.Descriptor.Root, targetArchive, cancellationToken).ConfigureAwait(false);
            SessionDataGraph targetGraph;
            if (File.Exists(targetGraphFile)) targetGraph = ReadGraph(targetGraphFile);
            else
            {
                targetGraph = CloneGraph(graph);
                await CopyEventFilesAsync(source, target, targetGraph, receipt.TransferId, cancellationToken).ConfigureAwait(false);
                targetGraph = await AddSharedActorDependenciesAsync(source.Services, targetGraph, cancellationToken).ConfigureAwait(false);
                await RebindFutureConfigurationAsync(target.Services, targetGraph, cancellationToken).ConfigureAwait(false);
                // A module commit may succeed before the phase journal is flushed. Freeze
                // timestamps and future bindings before the first target write so retries
                // replay exactly the same rows rather than minting conflicting facts.
                WriteGraph(targetGraphFile, targetGraph);
            }
            await CopyFileWithoutOverwriteAsync(source.Descriptor.Root, targetGraphFile, target.Descriptor.Root,
                Path.Combine(Path.GetDirectoryName(targetArchive)!, "target-graph.toml"), cancellationToken).ConfigureAwait(false);
            await ScopeSessionDataGraph.CopyToAsync(target.Services, targetGraph, receipt.ProjectId, cancellationToken).ConfigureAwait(false);
            receipt = await SetPhaseAsync(receipt, "copied", cancellationToken).ConfigureAwait(false);
        }
        // Once copied is durable, cancellation cannot leave the source as an independently writable owner.
        await ScopeSessionDataGraph.DeleteAsync(source.Services, graph, cancellationToken).ConfigureAwait(false);
        await RemoveRunFilesAsync(source, graph, cancellationToken).ConfigureAwait(false);
        return await SetPhaseAsync(receipt, "completed", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasQueuedInteractionsAsync(IServiceProvider source, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await source.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.RunDirectives.AsNoTracking().AnyAsync(d => d.SessionId == sessionId && d.Kind == "queued_interaction"
            && (d.Status == "queued" || d.Status == "pending"), cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionScopeTransferReceipt> SetPhaseAsync(SessionScopeTransferReceipt receipt, string phase, CancellationToken cancellationToken)
    {
        receipt = receipt with { Status = phase, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
        _transfers[receipt.TransferId] = receipt;
        await SaveJournalAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    private static async Task RebindFutureConfigurationAsync(IServiceProvider target, SessionDataGraph graph, CancellationToken cancellationToken)
    {
        var session = graph.Entities.SelectMany(e => e.Rows).OfType<SessionRecord>().Single();
        var local = target.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var global = target.GetRequiredService<GlobalAgentConfiguration>();
        var modeId = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
            local, global, session.TenantId, session.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Publish a default Agent Mode in the destination project before moving this session.");
        var modeFactory = await AgentConfigurationLookup.SelectModeFactoryAsync(
            local, global, session.TenantId, session.WorkspaceId, modeId, cancellationToken).ConfigureAwait(false);
        await using var cfg = await modeFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var mode = await cfg.ModeVersions.AsNoTracking().SingleOrDefaultAsync(m => m.Id == modeId
            && m.TenantId == session.TenantId && m.WorkspaceId == session.WorkspaceId && m.Status == "published", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The destination default Agent Mode is unavailable.");
        var definitions = await cfg.AgentDefinitions.AsNoTracking().Where(a => a.TenantId == session.TenantId && a.WorkspaceId == session.WorkspaceId)
            .Select(a => new ConversationIdentityResolver.DefinitionInput(a.Id, a.Slug, a.Layer, a.CapabilitiesJson)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var identity = ConversationIdentityResolver.Resolve(mode.SnapshotJson, definitions)
            ?? throw new InvalidOperationException("The destination default mode has no conversation identity.");
        session.ModeVersionId = mode.Id;
        session.ConversationNodeKey = identity.NodeKey;
        session.ConversationTemplateSlug = identity.TemplateSlug;
        session.MeetingModelOverrideProviderInstanceId = null;
        session.MeetingModelOverrideModel = null;
        if (ProjectSessionStore.ReadSpaceOptions(session) is { } space)
            session.SpaceOptionsJson = JsonSerializer.Serialize(space with { WorkflowModeVersionId = null }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        session.SettingsRevision++;
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<SessionDataGraph> AddSharedActorDependenciesAsync(IServiceProvider source, SessionDataGraph graph, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(["ParticipantId", "SenderId", "CreatorId", "AuthorId", "DecidedById", "MemberId", "RequesterId", "HostParticipantId", "HumanParticipantId"], StringComparer.Ordinal);
        var ids = graph.Entities.SelectMany(e => e.Rows).SelectMany(row => row.GetType().GetProperties()
            .Where(p => names.Contains(p.Name)).Select(p => p.GetValue(row))).OfType<Guid>().Distinct().ToArray();
        if (ids.Length == 0) return graph;
        await using var db = await source.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var actors = await db.Participants.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return graph with { Entities = [.. graph.Entities, new SessionEntityRows(typeof(TinaChatDbContext), typeof(ChatParticipant), actors.Cast<object>().ToList())] };
    }

    private static async Task CopyContentAndResourcesAsync(StorageRuntimeLease source, StorageRuntimeLease target, SessionDataGraph graph, CancellationToken cancellationToken)
    {
        var sourceContent = source.Services.GetRequiredService<IContentStore>();
        var targetContent = target.Services.GetRequiredService<IContentStore>();
        var session = graph.Entities.SelectMany(e => e.Rows).OfType<SessionRecord>().Single();
        var structuredKinds = new HashSet<string>(["run_configuration", "run_checkpoint", "workspace-snapshot", "session-metadata-snapshot",
            "agent-instance", "agent-candidate", "context_snapshot", "context-snapshot"], StringComparer.Ordinal);
        var pending = new Queue<string>(graph.ContentReferences.Where(r => r.StartsWith("content/", StringComparison.Ordinal)));
        var copied = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var reference))
        {
            if (!copied.Add(reference)) continue;
            var segments = reference.Split('/');
            if (segments.Length != 6 || segments[0] != "content" || segments[1] != "tenants" || !Guid.TryParse(segments[2], out var tenant))
                throw new InvalidDataException("A session content reference has an unsupported storage shape.");
            var workspace = segments[3] == "tenant" ? (Guid?)null : Guid.Parse(segments[3]);
            if (tenant != session.TenantId || workspace is not null && workspace != session.WorkspaceId)
                throw new InvalidDataException("A session content reference belongs to another tenant or workspace.");
            await using var input = await sourceContent.OpenReadAsync(new(reference, segments[5], 0, "application/octet-stream"), cancellationToken).ConfigureAwait(false);
            var stored = await targetContent.PutAsync(new(tenant, workspace, segments[4], "application/octet-stream", input), cancellationToken).ConfigureAwait(false);
            if (stored.Value != reference || stored.Sha256 != segments[5]) throw new InvalidDataException("A session content blob failed its integrity check at the destination.");
            // Only host-authored snapshots may introduce additional immutable dependencies.
            // Arbitrary messages and attachments must never act as pointers to another blob.
            if (structuredKinds.Contains(segments[4]))
            {
                await using var structured = await targetContent.OpenReadAsync(stored, cancellationToken).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(structured, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var nested in EnumerateStrings(json.RootElement))
                    if (nested.StartsWith("content/", StringComparison.Ordinal)) pending.Enqueue(nested);
            }
        }
        // Immutable package directories are content addressed; copying a retained version never changes target editing state.
        await CopyTreeWithoutOverwriteAsync(source.Descriptor.Packages, target.Descriptor.Packages, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) { yield return value.GetString()!; yield break; }
        if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) foreach (var text in EnumerateStrings(child)) yield return text;
        if (value.ValueKind == JsonValueKind.Object) foreach (var child in value.EnumerateObject()) foreach (var text in EnumerateStrings(child.Value)) yield return text;
    }

    private static async Task CopyConfigurationArchiveAsync(StorageRuntimeLease source, StorageRuntimeLease target,
        SessionScopeTransferReceipt receipt, CancellationToken cancellationToken)
    {
        var destination = StorageScopePaths.Contained(target.Descriptor.Data, Path.Combine("transfers", receipt.TransferId.ToString("N"), "configuration"));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source.Descriptor.Config, "*.toml"))
            await CopyFileWithoutOverwriteAsync(source.Descriptor.Root, file, target.Descriptor.Root, Path.Combine(destination, Path.GetFileName(file)), cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyEventFilesAsync(StorageRuntimeLease source, StorageRuntimeLease target, SessionDataGraph graph, Guid transferId, CancellationToken cancellationToken)
    {
        var from = source.Services.GetRequiredService<StoragePaths>(); var to = target.Services.GetRequiredService<StoragePaths>();
        foreach (var run in graph.RunIds)
        {
            foreach (var (origin, destination) in new[] { (from.EventLog(run), to.EventLog(run)), (from.TaskSnapshot(run), to.TaskSnapshot(run)) })
                if (File.Exists(origin)) await CopyFileWithoutOverwriteAsync(source.Descriptor.Root, origin, target.Descriptor.Root, destination, cancellationToken).ConfigureAwait(false);
            await CopyTreeWithoutOverwriteAsync(from.Artifacts(run), to.Artifacts(run), cancellationToken).ConfigureAwait(false);
        }
        if (File.Exists(from.SessionHistory(graph.SessionId)))
            await CopyFileWithoutOverwriteAsync(source.Descriptor.Root, from.SessionHistory(graph.SessionId), target.Descriptor.Root, to.SessionHistory(graph.SessionId), cancellationToken).ConfigureAwait(false);
        // A control journal can contain several sessions. Move only the indexed bytes
        // owned by this graph, keeping the untouched payload and hash as historical facts.
        foreach (var index in graph.Entities.SelectMany(e => e.Rows).OfType<ControlEventIndexRecord>())
        {
            if (index.ByteOffset < 0 || index.ByteLength is <= 0 or > 16 * 1024 * 1024)
                throw new InvalidDataException("A control event index has an invalid byte range.");
            var origin = StorageScopePaths.Contained(from.Root, index.RelativeFilePath);
            StorageScopePaths.RejectLinks(source.Descriptor.Root, origin);
            var bytes = new byte[index.ByteLength];
            await using (var input = File.OpenRead(origin))
            { input.Seek(index.ByteOffset, SeekOrigin.Begin); await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false); }
            var fullHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var payloadLength = bytes.Length;
            while (payloadLength > 0 && bytes[payloadLength - 1] is (byte)'\r' or (byte)'\n') payloadLength--;
            var payloadHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, payloadLength))).ToLowerInvariant();
            if (index.PayloadHash != fullHash && index.PayloadHash != payloadHash)
                throw new InvalidDataException("A control event payload failed its integrity check.");
            var relative = Path.Combine("transfers", transferId.ToString("N"), "control-events", index.Id.ToString("N") + ".events.jsonl");
            var destination = StorageScopePaths.Contained(to.Root, relative);
            StorageScopePaths.RejectLinks(target.Descriptor.Root, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                var existing = await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("A transferred control event identity already contains different data.");
            }
            else WriteAtomicBytes(destination, bytes);
            index.RelativeFilePath = relative; index.ByteOffset = 0;
        }
    }

    private static Task RemoveRunFilesAsync(StorageRuntimeLease source, SessionDataGraph graph, CancellationToken cancellationToken)
    {
        var paths = source.Services.GetRequiredService<StoragePaths>();
        foreach (var run in graph.RunIds)
        {
            foreach (var path in new[] { paths.EventLog(run), paths.TaskSnapshot(run) })
            { cancellationToken.ThrowIfCancellationRequested(); StorageScopePaths.RejectLinks(source.Descriptor.Root, path); if (File.Exists(path)) File.Delete(path); }
            var artifacts = paths.Artifacts(run); StorageScopePaths.RejectLinks(source.Descriptor.Root, artifacts);
            if (Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
        }
        var history = paths.SessionHistory(graph.SessionId); StorageScopePaths.RejectLinks(source.Descriptor.Root, history); if (File.Exists(history)) File.Delete(history);
        return Task.CompletedTask;
    }

    private static async Task CopyTreeWithoutOverwriteAsync(string source, string target, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return;
        StorageScopePaths.RejectLinks(source); StorageScopePaths.RejectLinks(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("A retained package contains a link.");
            var destination = Path.Combine(target, Path.GetFileName(path));
            if (Directory.Exists(path)) await CopyTreeWithoutOverwriteAsync(path, destination, cancellationToken).ConfigureAwait(false);
            else await CopyFileWithoutOverwriteAsync(source, path, target, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileWithoutOverwriteAsync(string sourceRoot, string source, string targetRoot, string target, CancellationToken cancellationToken)
    {
        StorageScopePaths.RejectLinks(sourceRoot, source); StorageScopePaths.RejectLinks(targetRoot, target);
        if (File.Exists(target))
        {
            await using var sourceStream = File.OpenRead(source); await using var targetStream = File.OpenRead(target);
            var left = await SHA256.HashDataAsync(sourceStream, cancellationToken).ConfigureAwait(false);
            var right = await SHA256.HashDataAsync(targetStream, cancellationToken).ConfigureAwait(false);
            if (!left.AsSpan().SequenceEqual(right)) throw new InvalidOperationException("The destination already contains different data for a transferred identity.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var input = File.OpenRead(source);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false); await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true); }
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteGraph(string path, SessionDataGraph graph)
    {
        var model = new TomlTable { ["session_id"] = graph.SessionId.ToString(), ["run_ids"] = new TomlArray(), ["content_references"] = new TomlArray(), ["entities"] = new TomlTableArray() };
        foreach (var id in graph.RunIds) ((TomlArray)model["run_ids"]).Add(id.ToString());
        foreach (var reference in graph.ContentReferences) ((TomlArray)model["content_references"]).Add(reference);
        foreach (var entity in graph.Entities)
            ((TomlTableArray)model["entities"]).Add(new TomlTable { ["context_type"] = entity.ContextType.AssemblyQualifiedName!, ["entity_type"] = entity.EntityType.AssemblyQualifiedName!, ["rows"] = JsonSerializer.Serialize(entity.Rows, Json) });
        WriteAtomic(path, TomlSerializer.Serialize(model));
    }

    private static SessionDataGraph ReadGraph(string path)
    {
        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;
        var entities = new List<SessionEntityRows>();
        foreach (var row in (TomlTableArray)model["entities"])
        {
            var context = Type.GetType((string)row["context_type"], throwOnError: true)!;
            var entity = Type.GetType((string)row["entity_type"], throwOnError: true)!;
            if (!typeof(DbContext).IsAssignableFrom(context) || entity.Assembly.GetName().Name?.StartsWith("TinadecCore.", StringComparison.Ordinal) != true)
                throw new InvalidDataException("A transfer graph contains an unexpected record type.");
            var listType = typeof(List<>).MakeGenericType(entity);
            var rows = ((System.Collections.IEnumerable)JsonSerializer.Deserialize((string)row["rows"], listType, Json)!).Cast<object>().ToList();
            entities.Add(new(context, entity, rows));
        }
        return new(Guid.Parse((string)model["session_id"]), ((TomlArray)model["run_ids"]).Select(v => Guid.Parse((string)v!)).ToList(), entities,
            ((TomlArray)model["content_references"]).Cast<string>().ToList());
    }

    private static SessionDataGraph CloneGraph(SessionDataGraph graph) => new(graph.SessionId, graph.RunIds,
        graph.Entities.Select(e => new SessionEntityRows(e.ContextType, e.EntityType,
            e.Rows.Select(r => JsonSerializer.Deserialize(JsonSerializer.Serialize(r, e.EntityType, Json), e.EntityType, Json)!).ToList())).ToList(), graph.ContentReferences);

    private void LoadJournal()
    {
        StorageScopePaths.RejectLinks(_registry.User.Root, _journal);
        if (!File.Exists(_journal)) return;
        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(_journal))!;
        if (!model.TryGetValue("transfers", out var value) || value is not TomlTableArray rows) return;
        foreach (var row in rows)
        {
            var receipt = new SessionScopeTransferReceipt(Guid.Parse((string)row["transfer_id"]), Guid.Parse((string)row["session_id"]),
                (string)row["source_storage_id"], (string)row["target_storage_id"], Guid.Parse((string)row["project_id"]), (string)row["status"],
                DateTimeOffset.Parse((string)row["created_at"], System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse((string)row["updated_at"], System.Globalization.CultureInfo.InvariantCulture),
                row.TryGetValue("error", out var error) ? error as string : null);
            if (receipt.SourceStorageId != "user" || receipt.TargetStorageId == "user"
                || receipt.Status is not ("pending" or "copying" or "copied" or "completed"))
                throw new InvalidDataException("A session transfer journal has an unsupported scope or phase.");
            _transfers[receipt.TransferId] = receipt;
        }
    }

    private async Task SaveJournalAsync(CancellationToken cancellationToken)
    {
        await _journalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_registry.User.State); StorageScopePaths.RejectLinks(_registry.User.Root, _journal);
            var rows = new TomlTableArray();
            foreach (var receipt in _transfers.Values.OrderBy(t => t.CreatedAt))
            {
                var row = new TomlTable { ["transfer_id"] = receipt.TransferId.ToString("N"), ["session_id"] = receipt.SessionId.ToString("N"),
                    ["source_storage_id"] = receipt.SourceStorageId, ["target_storage_id"] = receipt.TargetStorageId,
                    ["project_id"] = receipt.ProjectId.ToString("N"), ["status"] = receipt.Status,
                    ["created_at"] = receipt.CreatedAt.ToString("O"), ["updated_at"] = receipt.UpdatedAt.ToString("O") };
                if (receipt.Error is not null) row["error"] = receipt.Error;
                rows.Add(row);
            }
            WriteAtomic(_journal, TomlSerializer.Serialize(new TomlTable { ["schema_version"] = 1L, ["transfers"] = rows }));
        }
        finally { _journalLock.Release(); }
    }

    private static void WriteAtomic(string path, string text)
        => WriteAtomicBytes(path, new UTF8Encoding(false).GetBytes(text));

    private static void WriteAtomicBytes(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class GateLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref _gate, null)?.Release(); return ValueTask.CompletedTask; }
    }
}

internal sealed class SessionScopeTransferWorker(ISessionScopeTransferService transfers, ILogger<SessionScopeTransferWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { await transfers.ProcessPendingAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Session transfer retry pass failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
