using Microsoft.EntityFrameworkCore;
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

    public ProjectSessionLifecycleService(
        ProjectSessionStore store,
        IDbContextFactory<LifecycleDbContext> lifecycleFactory,
        StoragePaths paths)
    {
        _store = store;
        _lifecycleFactory = lifecycleFactory;
        _paths = paths;
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

    private async Task<ProjectRecord> TransitionProjectAsync(Guid projectId, string target, CancellationToken ct)
    {
        var sessions = await _store.ListAllSessionsForProjectAsync(projectId, ct).ConfigureAwait(false);
        if (sessions.Count != 0) await ThrowIfAnyActiveRunAsync(sessions.Select(x => x.Id), ct).ConfigureAwait(false);
        return await _store.SetProjectLifecycleAsync(projectId, target, ct).ConfigureAwait(false);
    }

    private async Task<SessionRecord> TransitionSessionAsync(Guid sessionId, string target, CancellationToken ct)
    {
        await ThrowIfAnyActiveRunAsync([sessionId], ct).ConfigureAwait(false);
        return await _store.SetSessionLifecycleAsync(sessionId, target, ct).ConfigureAwait(false);
    }

    /// <summary>Permanently deletes a trashed session: lifecycle rows, memory rows, and Core-owned run files.</summary>
    public async Task PurgeSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
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
        TryDeleteFile(_paths.ProjectVectorDatabase(project.TenantId, project.WorkspaceId, project.Id));
    }

    private async Task PurgeSessionDataAsync(Guid sessionId, CancellationToken ct)
    {
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
