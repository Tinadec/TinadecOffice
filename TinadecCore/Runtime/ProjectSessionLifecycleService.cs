using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>Raised when archive/trash/purge is attempted while a session still has a non-terminal run.</summary>
public sealed class ActiveRunConflictException : Exception
{
    public Guid RunId { get; }

    public ActiveRunConflictException(Guid runId)
        : base("The operation was rejected because a run is still active.") => RunId = runId;
}

/// <summary>
/// Orchestrates the project/session lifecycle (archive/trash/restore/purge) across the
/// Memory and Lifecycle stores plus Core-owned files. Business modules never reference
/// each other directly, so this cross-module composition lives at the host layer.
/// </summary>
public sealed class ProjectSessionLifecycleService
{
    private readonly ProjectSessionStore _store;
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycleFactory;
    private readonly StoragePaths _paths;
    private readonly ISessionOrganization? _organization;
    private readonly IServiceProvider? _services;

    public ProjectSessionLifecycleService(
        ProjectSessionStore store,
        IDbContextFactory<LifecycleDbContext> lifecycleFactory,
        StoragePaths paths,
        ISessionOrganization? organization = null,
        IServiceProvider? services = null)
    {
        _store = store;
        _lifecycleFactory = lifecycleFactory;
        _paths = paths;
        _organization = organization;
        _services = services;
    }

    public Task<ProjectRecord> ArchiveProjectAsync(Guid projectId, CancellationToken ct = default) =>
        TransitionProjectAsync(projectId, LifecycleStatuses.Archived, ct);

    public Task<ProjectRecord> TrashProjectAsync(Guid projectId, CancellationToken ct = default) =>
        TransitionProjectAsync(projectId, LifecycleStatuses.Trashed, ct);

    public Task<ProjectRecord> RestoreProjectAsync(Guid projectId, CancellationToken ct = default) =>
        TransitionProjectAsync(projectId, LifecycleStatuses.Active, ct);

    public Task<SessionRecord> ArchiveSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        TransitionSessionAsync(sessionId, LifecycleStatuses.Archived, ct);

    public Task<SessionRecord> TrashSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        TransitionSessionAsync(sessionId, LifecycleStatuses.Trashed, ct);

    public Task<SessionRecord> RestoreSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        TransitionSessionAsync(sessionId, LifecycleStatuses.Active, ct);

    /// <summary>
    /// Edit-and-resend support: cut the conversation at a message so the corrected turn
    /// can be sent again. Refused while any run of that session is still active, for the
    /// same reason a lifecycle transition is — the live run's checkpoint references the
    /// messages that would leave the history.
    /// </summary>
    public async Task<SessionHistoryRevert> RevertSessionHistoryAsync(Guid sessionId, Guid fromMessageId, CancellationToken ct = default)
    {
        await ThrowIfAnyActiveRunAsync([sessionId], ct).ConfigureAwait(false);
        return await _store.RevertHistoryAsync(sessionId, fromMessageId, ct).ConfigureAwait(false);
    }

    private async Task<ProjectRecord> TransitionProjectAsync(Guid projectId, string target, CancellationToken ct)
    {
        var sessions = await _store.ListAllSessionsForProjectAsync(projectId, ct).ConfigureAwait(false);
        if (sessions.Count != 0) await ThrowIfAnyActiveRunAsync(sessions.Select(x => x.Id), ct).ConfigureAwait(false);
        return await _store.SetProjectLifecycleAsync(projectId, target, ct).ConfigureAwait(false);
    }

    private async Task<SessionRecord> TransitionSessionAsync(Guid sessionId, string target, CancellationToken ct)
    {
        await ThrowIfAnyActiveRunAsync([sessionId], ct).ConfigureAwait(false);
        var record = await _store.SetSessionLifecycleAsync(sessionId, target, ct).ConfigureAwait(false);
        // An archived or trashed session keeps its organization readable and refuses writes to it;
        // restoring it reopens it. Nothing in the organization is ever deleted by a transition.
        if (_organization is not null)
            await _organization.SetArchivedAsync(sessionId, target != LifecycleStatuses.Active, ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>Permanently deletes a trashed session: lifecycle rows, memory rows, and Core-owned run files.</summary>
    public async Task PurgeSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Recovery uses the original closure even if a previous module transaction
        // already removed the session row or its relationships.
        var pending = PurgeJournal(sessionId);
        if (_services is not null && File.Exists(pending))
        { await CompletePurgeAsync(await ScopeSessionDataGraph.ReadSnapshotAsync(_services, pending, ct).ConfigureAwait(false), pending, ct).ConfigureAwait(false); return; }
        var session = await _store.GetSessionAnyStatusAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        if (session.LifecycleStatus != LifecycleStatuses.Trashed)
            throw new InvalidOperationException("Only trashed sessions can be permanently deleted.");
        await ThrowIfAnyActiveRunAsync([sessionId], ct).ConfigureAwait(false);
        await PurgeSessionDataAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Permanently deletes a trashed project together with every session that belongs to it.</summary>
    public async Task PurgeProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _store.GetProjectAnyStatusAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project.LifecycleStatus != LifecycleStatuses.Trashed)
            throw new InvalidOperationException("Only trashed projects can be permanently deleted.");
        var sessions = await _store.ListAllSessionsForProjectAsync(projectId, ct).ConfigureAwait(false);
        if (sessions.Count != 0) await ThrowIfAnyActiveRunAsync(sessions.Select(x => x.Id), ct).ConfigureAwait(false);
        foreach (var session in sessions) await PurgeSessionDataAsync(session.Id, ct).ConfigureAwait(false);
        await _store.DeleteProjectRowAsync(projectId, ct).ConfigureAwait(false);
        var vectorPath = _paths.ProjectVectorDatabase(project.TenantId, project.WorkspaceId, project.Id);
        SqliteConnectionPragmaInterceptor.ClearPoolForPath(vectorPath);
        TryDeleteFile(vectorPath);
    }

    private async Task PurgeSessionDataAsync(Guid sessionId, CancellationToken ct)
    {
        if (_services is not null)
        {
            await using var admission = _services.GetService(typeof(ISessionStorageAdmissionGuard)) is ISessionStorageAdmissionGuard guard
                ? await guard.AcquireAsync(sessionId, _paths.Locations.StorageId, false, ct).ConfigureAwait(false) : null;
            if (_services.GetService(typeof(ISessionScopeTransferService)) is ISessionScopeTransferService transfers)
                transfers.EnsurePurgeAllowed(sessionId, _paths.Locations.StorageId);
            await ThrowIfAnyActiveRunAsync([sessionId], ct).ConfigureAwait(false);
            var graph = await ScopeSessionDataGraph.ReadAsync(_services, sessionId, ct).ConfigureAwait(false);
            var journal = PurgeJournal(sessionId);
            StorageScopePaths.RejectLinks(_paths.Locations.Root, journal);
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            await ScopeSessionDataGraph.WriteSnapshotAsync(journal, graph, ct).ConfigureAwait(false);
            await CompletePurgeAsync(graph, journal, ct).ConfigureAwait(false);
            return;
        }
        var runIds = new List<Guid>();
        await using (var lifecycle = await _lifecycleFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            runIds = await lifecycle.Runs.AsNoTracking().Where(x => x.SessionId == sessionId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
            await lifecycle.EventIndex.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await lifecycle.Runs.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        await _store.DeleteSessionDataAsync(sessionId, ct).ConfigureAwait(false);
        foreach (var runId in runIds)
        {
            TryDeleteFile(_paths.EventLog(runId));
            TryDeleteFile(_paths.TaskSnapshot(runId));
            try { Directory.Delete(_paths.Artifacts(runId), recursive: true); } catch { /* best-effort file cleanup */ }
        }
    }

    public async Task RecoverPendingPurgesAsync(CancellationToken ct = default)
    {
        if (_services is null) return;
        var root = Path.Combine(_paths.Locations.State, "session-purges");
        StorageScopePaths.RejectLinks(_paths.Locations.Root, root);
        if (!Directory.Exists(root)) return;
        foreach (var journal in Directory.EnumerateFiles(root, "*.toml"))
        {
            StorageScopePaths.RejectLinks(root, journal);
            var graph = await ScopeSessionDataGraph.ReadSnapshotAsync(_services, journal, ct).ConfigureAwait(false);
            if (Path.GetFileNameWithoutExtension(journal) != graph.SessionId.ToString("N"))
                throw new InvalidDataException("Session purge journal identity does not match its filename: " + journal);
            await CompletePurgeAsync(graph, journal, ct).ConfigureAwait(false);
        }
    }

    private string PurgeJournal(Guid sessionId) => Path.Combine(_paths.Locations.State, "session-purges", sessionId.ToString("N") + ".toml");

    private async Task CompletePurgeAsync(SessionDataGraph graph, string journal, CancellationToken ct)
    {
        if (_services is null) throw new InvalidOperationException("Session purge participants are unavailable.");
        if (_services.GetService(typeof(ISessionScopeTransferService)) is ISessionScopeTransferService transfers)
            transfers.EnsurePurgeAllowed(graph.SessionId, _paths.Locations.StorageId);
        await ScopeSessionDataGraph.DeleteAsync(_services, graph, ct).ConfigureAwait(false);
        foreach (var runId in graph.RunIds) DeleteOwnedRunFiles(runId);
        DeleteOptionalOwnedFile(_paths.SessionHistory(graph.SessionId));
        if (_services.GetService(typeof(ISessionScopeTransferService)) is ISessionScopeTransferService completedTransfers)
            await completedTransfers.PurgeCompletedAsync(graph.SessionId, _paths.Locations.StorageId, ct).ConfigureAwait(false);
        File.Delete(journal);
    }

    private void DeleteOwnedRunFiles(Guid runId)
    {
        foreach (var file in new[] { _paths.EventLog(runId), _paths.TaskSnapshot(runId) })
        { StorageScopePaths.RejectLinks(_paths.Root, file); DeleteOptionalOwnedFile(file); }
        var artifacts = _paths.Artifacts(runId);
        StorageScopePaths.RejectLinks(_paths.Root, artifacts);
        if (Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
    }

    private static void DeleteOptionalOwnedFile(string path)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { /* This run may never have written an event or snapshot. */ }
    }

    private async Task ThrowIfAnyActiveRunAsync(IEnumerable<Guid> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds.ToList();
        if (ids.Count == 0) return;
        await using var lifecycle = await _lifecycleFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var activeRunId = await lifecycle.Runs.AsNoTracking()
            .Where(x => ids.Contains(x.SessionId) && x.Status != "completed" && x.Status != "failed" && x.Status != "cancelled")
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (activeRunId is { } runId) throw new ActiveRunConflictException(runId);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort file cleanup */ }
    }
}
