using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Memory;

public sealed class ProjectSessionStore : ISessionLocator, IWorkspaceRootResolver, IConversationStore, IStorageMigrationParticipant, ISessionWorkspaceBinder
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SessionLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly StoragePaths _paths;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenantContext;

    public ProjectSessionStore(IDbContextFactory<MemoryDbContext> dbFactory, StoragePaths paths, IContentStore content, ITenantContextAccessor tenantContext)
    {
        _dbFactory = dbFactory;
        _paths = paths;
        _content = content;
        _tenantContext = tenantContext;
    }

    public async Task<ProjectRecord> CreateProjectAsync(string name, string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Project name and path are required.");
        }

        // Rootedness is a property of the caller's input, not of GetFullPath's
        // output (which always returns a rooted path), so check before normalizing.
        var trimmedPath = path.Trim();
        if (!Path.IsPathRooted(trimmedPath))
        {
            throw new ArgumentException("Project path must be absolute.");
        }

        var rootPath = Path.GetFullPath(trimmedPath);

        var now = DateTimeOffset.UtcNow;
        var scope = _tenantContext.Current;
        var project = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            Name = name.Trim(),
            RootPath = Path.TrimEndingDirectorySeparator(rootPath),
            NormalizedRootPath = NormalizeRootPath(rootPath),
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Projects.AnyAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.NormalizedRootPath == project.NormalizedRootPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A project already exists for this root path.");
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(string lifecycleStatus = LifecycleStatuses.Active, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var projects = await db.Projects.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == lifecycleStatus).ToListAsync(cancellationToken).ConfigureAwait(false);
        return projects.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<SessionRecord> CreateSessionAsync(
        Guid? projectId,
        string? title,
        Guid modeVersionId,
        SessionModelOverride? meetingModelOverride = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        if (projectId.HasValue && !await db.Projects.AnyAsync(x => x.Id == projectId.Value && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Project was not found.");
        }

        var now = DateTimeOffset.UtcNow;
        if (modeVersionId == Guid.Empty) throw new ArgumentException("A published Agent Mode version is required.", nameof(modeVersionId));
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            ProjectId = projectId, Title = string.IsNullOrWhiteSpace(title) ? "New session" : title.Trim(),
            ModeVersionId = modeVersionId,
            MeetingModelOverrideProviderInstanceId = meetingModelOverride?.ProviderInstanceId,
            MeetingModelOverrideModel = meetingModelOverride?.Model,
            CreatedAt = now, UpdatedAt = now
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(Guid? projectId, string lifecycleStatus = LifecycleStatuses.Active, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var query = db.Sessions.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == lifecycleStatus);
        if (projectId is { } id) query = query.Where(x => x.ProjectId == id);
        var sessions = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return sessions.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<SessionRecord?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRecord?> UpdateTitleAsync(Guid sessionId, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Session title is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        session.Title = title.Trim();
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<SessionRecord?> UpdateSessionModeAsync(
        Guid sessionId,
        Guid? modeVersionId,
        SessionModelOverride? meetingModelOverride,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        if (modeVersionId.HasValue) session.ModeVersionId = modeVersionId;
        if (meetingModelOverride is not null)
        {
            session.MeetingModelOverrideProviderInstanceId = meetingModelOverride.ProviderInstanceId;
            session.MeetingModelOverrideModel = meetingModelOverride.Model?.Trim();
        }
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<SessionRecord> MigrateSessionAsync(
        Guid sessionId,
        Guid targetProjectId,
        CancellationToken cancellationToken = default)
    {
        // Rebinding a session is a read-modify-write on one row; without the gate a
        // concurrent migrate/create_workspace pair can lose the winning update.
        var gate = SessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MigrateSessionUnlockedAsync(sessionId, targetProjectId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SessionRecord> MigrateSessionUnlockedAsync(
        Guid sessionId,
        Guid targetProjectId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");

        if (!await db.Projects.AnyAsync(x => x.Id == targetProjectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Target project was not found.");
        }

        session.ProjectId = targetProjectId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<SessionWorkspaceBinding> BindSessionToWorkspaceAsync(Guid sessionId, string name, string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Workspace name is required.");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Workspace path is required.");
        // Rootedness must be checked on the caller-supplied value: GetFullPath
        // normalizes a relative path against the process CWD, so checking it after
        // the call can never fail.
        var trimmedPath = path.Trim();
        if (!Path.IsPathRooted(trimmedPath)) throw new ArgumentException("Workspace path must be absolute.");
        var fullPath = Path.GetFullPath(trimmedPath);
        // Find-or-create plus the session rebind must be one critical section per
        // session: a double-clicked approval or retried dispatch otherwise races
        // two create_workspace calls and the loser silently overwrites the winner.
        var gate = SessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(fullPath);
            var existing = await FindByRootAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var projectId = existing?.ProjectId ?? (await CreateProjectAsync(name, fullPath, cancellationToken).ConfigureAwait(false)).Id;
            var session = await MigrateSessionUnlockedAsync(sessionId, projectId, cancellationToken).ConfigureAwait(false);
            return new SessionWorkspaceBinding(session.Id, projectId, name.Trim(), Path.TrimEndingDirectorySeparator(fullPath), existing is null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProjectRecord?> RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
        if (project is null) return null;
        project.Name = name.Trim();
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<ProjectRecord?> GetProjectAnyStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRecord?> GetSessionAnyStatusAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionRecord>> ListAllSessionsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Sessions.AsNoTracking().Where(x => x.ProjectId == projectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectRecord> SetProjectLifecycleAsync(Guid projectId, string target, CancellationToken cancellationToken = default)
    {
        if (!LifecycleStatuses.IsKnown(target)) throw new ArgumentException($"Unknown lifecycle status '{target}'.", nameof(target));
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        ValidateLifecycleTransition(project.LifecycleStatus, target);
        var now = DateTimeOffset.UtcNow;
        project.LifecycleStatus = target;
        project.TrashedAt = target == LifecycleStatuses.Trashed ? now : null;
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<SessionRecord> SetSessionLifecycleAsync(Guid sessionId, string target, CancellationToken cancellationToken = default)
    {
        if (!LifecycleStatuses.IsKnown(target)) throw new ArgumentException($"Unknown lifecycle status '{target}'.", nameof(target));
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        ValidateLifecycleTransition(session.LifecycleStatus, target);
        var now = DateTimeOffset.UtcNow;
        session.LifecycleStatus = target;
        session.TrashedAt = target == LifecycleStatuses.Trashed ? now : null;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private static void ValidateLifecycleTransition(string from, string target)
    {
        var allowed = from switch
        {
            LifecycleStatuses.Active => target is LifecycleStatuses.Archived or LifecycleStatuses.Trashed,
            LifecycleStatuses.Archived => target is LifecycleStatuses.Active or LifecycleStatuses.Trashed,
            LifecycleStatuses.Trashed => target is LifecycleStatuses.Active,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Cannot transition lifecycle from '{from}' to '{target}'.");
    }

    /// <summary>Deletes all Memory-owned rows plus the legacy history file for a session. Callers must enforce lifecycle and active-run guards first.</summary>
    public async Task DeleteSessionDataAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        await db.Messages.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Turns.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.ContextSnapshots.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.ContextPatches.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Sessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        try { File.Delete(_paths.SessionHistory(sessionId)); } catch { /* best-effort file cleanup */ }
    }

    public async Task DeleteProjectRowAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        db.Projects.Remove(project);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredMessage>> ListMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Messages.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<StoredMessage>(rows.Count);
        foreach (var row in rows) result.Add(await ToStoredMessageAsync(row, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public Task<StoredMessage> AddMessageAsync(Guid sessionId, string content, string role = "user", Guid? runId = null, CancellationToken cancellationToken = default) =>
        AddMessageAsync(sessionId, content, role, runId, null, null, cancellationToken);

    public async Task<StoredMessage> AddMessageAsync(Guid sessionId, string content, string role, Guid? runId, Guid? turnId, string? clientMessageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Message content is required.");
        var sessionRef = await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Session was not found.");
        var gate = SessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(clientMessageId))
            {
                var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.ClientMessageId == clientMessageId, cancellationToken).ConfigureAwait(false);
                if (existing is not null) return await ToStoredMessageAsync(existing, cancellationToken).ConfigureAwait(false);
            }
            await using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            var stored = await _content.PutAsync(new ContentWriteRequest(sessionRef.TenantId, sessionRef.WorkspaceId, "message", "text/plain; charset=utf-8", body), cancellationToken).ConfigureAwait(false);
            var sequence = (await db.Messages.Where(x => x.SessionId == sessionId).MaxAsync(x => (long?)x.Sequence, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
            var now = DateTimeOffset.UtcNow;
            var row = new MessageRecord
            {
                Id = Guid.NewGuid(), TenantId = sessionRef.TenantId, WorkspaceId = sessionRef.WorkspaceId, SessionId = sessionId,
                RunId = runId, TurnId = turnId, ClientMessageId = string.IsNullOrWhiteSpace(clientMessageId) ? null : clientMessageId.Trim(),
                Sequence = sequence, Role = role.Trim().ToLowerInvariant(), ContentReference = stored.Value, ContentHash = stored.Sha256,
                ContentLength = stored.Length, CreatedAt = now
            };
            db.Messages.Add(row);
            var session = await db.Sessions.SingleAsync(x => x.Id == sessionId, cancellationToken).ConfigureAwait(false);
            session.HistoryRevision++;
            session.UpdatedAt = now;
            var contextSnapshot = await StoreContextDocumentAsync(
                sessionRef.TenantId,
                sessionRef.WorkspaceId,
                "message",
                new
                {
                    schema_version = 1,
                    revision = session.HistoryRevision,
                    source = "message",
                    message_id = row.Id,
                    message_sequence = row.Sequence,
                    role = row.Role,
                    content_reference = row.ContentReference,
                    content_hash = row.ContentHash,
                    content_length = row.ContentLength
                },
                cancellationToken).ConfigureAwait(false);
            db.ContextSnapshots.Add(new ContextSnapshotRecord
            {
                Id = Guid.NewGuid(),
                TenantId = sessionRef.TenantId,
                WorkspaceId = sessionRef.WorkspaceId,
                SessionId = sessionId,
                RunId = runId,
                Revision = session.HistoryRevision,
                ContentReference = contextSnapshot.Value,
                ContentHash = contextSnapshot.Sha256,
                ContentLength = contextSnapshot.Length,
                CreatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new StoredMessage { Id = row.Id, SessionId = sessionId, RunId = runId, TurnId = turnId, ClientMessageId = row.ClientMessageId, Sequence = sequence, Role = row.Role, Content = content, CreatedAt = now };
        }
        finally { gate.Release(); }
    }

    async Task<ConversationMessage> IConversationStore.AppendMessageAsync(Guid sessionId, string role, string content, Guid? runId, Guid? turnId, string? clientMessageId, CancellationToken cancellationToken)
    {
        var item = await AddMessageAsync(sessionId, content, role, runId, turnId, clientMessageId, cancellationToken).ConfigureAwait(false);
        return ToConversationMessage(item);
    }

    async Task<IReadOnlyList<ConversationMessage>> IConversationStore.ListMessagesAsync(Guid sessionId, int? limit, CancellationToken cancellationToken)
    {
        var messages = await ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return messages.TakeLast(Math.Max(0, limit ?? messages.Count)).Select(ToConversationMessage).ToArray();
    }

    async Task<ConversationMessage?> IConversationStore.FindMessageByClientMessageIdAsync(Guid sessionId, string clientMessageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientMessageId))
        {
            return null;
        }

        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Messages.AsNoTracking().SingleOrDefaultAsync(item => item.SessionId == sessionId
            && item.TenantId == scope.TenantId
            && item.WorkspaceId == scope.WorkspaceId
            && item.ClientMessageId == clientMessageId.Trim(), cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToConversationMessage(await ToStoredMessageAsync(row, cancellationToken).ConfigureAwait(false));
    }

    public async Task<long> GetContextRevisionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Sessions.Where(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active)
            .Select(x => (long?)x.HistoryRevision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
    }

    public async Task<IReadOnlyList<ConversationContextVersion>> ListContextVersionsAsync(
        Guid sessionId,
        Guid? runId = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = db.ContextSnapshots.AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId);
        if (runId is { } selectedRunId)
        {
            snapshots = snapshots.Where(item => item.RunId == selectedRunId);
        }

        // SQLite cannot translate DateTimeOffset ordering; order by the numeric revision
        // on the server, then refine with the timestamp on the client.
        var bounded = Math.Clamp(limit, 1, 200);
        var snapshotRows = await snapshots
            .OrderByDescending(item => item.Revision)
            .Take(bounded)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        snapshotRows.Sort((a, b) => b.Revision.CompareTo(a.Revision) is var order && order != 0 ? order : b.CreatedAt.CompareTo(a.CreatedAt));
        var patchRows = await db.ContextPatches.AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId
                && (runId == null || item.RunId == runId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        patchRows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        if (patchRows.Count > bounded) patchRows.RemoveRange(bounded, patchRows.Count - bounded);

        // Patch kind lives in the immutable content body; resolve it so the
        // metadata projection distinguishes supplements, goal adjustments, and
        // operational compactions without exposing the body itself.
        var patchVersions = new List<ConversationContextVersion>(patchRows.Count);
        foreach (var row in patchRows)
        {
            var patch = await ReadContextPatchAsync(row, cancellationToken).ConfigureAwait(false);
            patchVersions.Add(new ConversationContextVersion(
                row.Id, row.SessionId, row.RunId, row.AppliedRevision ?? row.BaseRevision, patch.Kind, row.Status, row.BaseRevision, row.CreatedAt));
        }

        return snapshotRows.Select(item => new ConversationContextVersion(
                item.Id, item.SessionId, item.RunId, item.Revision, "snapshot", "applied", null, item.CreatedAt))
            .Concat(patchVersions)
            .OrderByDescending(item => item.Revision)
            .ThenByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArray();
    }

    public async Task<ContextPatchApplyResult> ApplyContextPatchAsync(
        ContextPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Context patch content is required.", nameof(request));
        }
        var patchKind = request.Kind?.Trim().ToLowerInvariant();
        if (patchKind is not ("supplement" or "goal_adjustment" or "compaction"))
        {
            throw new ArgumentException("Context patch kind must be supplement, goal_adjustment, or compaction.", nameof(request));
        }

        var sessionRef = await FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        var gate = SessionLocks.GetOrAdd(request.SessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await StoreContextDocumentAsync(
                sessionRef.TenantId,
                sessionRef.WorkspaceId,
                "context-patch",
                new
                {
                    schema_version = 1,
                    kind = patchKind,
                    base_revision = request.BaseRevision,
                    summary = request.Summary,
                    content = request.Content
                },
                cancellationToken).ConfigureAwait(false);
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var scope = _tenantContext.Current;
            var session = await db.Sessions.SingleOrDefaultAsync(item => item.Id == request.SessionId
                && item.TenantId == scope.TenantId
                && item.WorkspaceId == scope.WorkspaceId
                && item.LifecycleStatus == LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Session was not found.");
            var now = DateTimeOffset.UtcNow;
            var patch = new ContextPatchRecord
            {
                Id = Guid.NewGuid(),
                TenantId = session.TenantId,
                WorkspaceId = session.WorkspaceId,
                SessionId = request.SessionId,
                RunId = request.RunId,
                AgentInstanceId = request.AgentInstanceId,
                BaseRevision = request.BaseRevision,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedAt = now
            };

            if (session.HistoryRevision != request.BaseRevision)
            {
                patch.Status = "stale";
                db.ContextPatches.Add(patch);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new ContextPatchApplyResult(patch.Id, patch.Status, session.HistoryRevision, null);
            }

            session.HistoryRevision++;
            session.UpdatedAt = now;
            patch.Status = "applied";
            patch.AppliedRevision = session.HistoryRevision;
            patch.AppliedAt = now;
            db.ContextPatches.Add(patch);
            db.ContextSnapshots.Add(new ContextSnapshotRecord
            {
                Id = Guid.NewGuid(),
                TenantId = session.TenantId,
                WorkspaceId = session.WorkspaceId,
                SessionId = request.SessionId,
                RunId = request.RunId,
                Revision = session.HistoryRevision,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ContextPatchApplyResult(patch.Id, patch.Status, session.HistoryRevision, patch.AppliedRevision);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationContextPatch>> ListAppliedContextPatchesAsync(
        Guid sessionId,
        Guid runId,
        long afterRevision,
        CancellationToken cancellationToken = default)
    {
        if (afterRevision < 0) throw new ArgumentOutOfRangeException(nameof(afterRevision));

        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ContextPatches.AsNoTracking()
            .Where(item => item.SessionId == sessionId
                && item.RunId == runId
                && item.TenantId == scope.TenantId
                && item.WorkspaceId == scope.WorkspaceId
                && item.Status == "applied"
                && item.AppliedRevision != null
                && item.AppliedRevision > afterRevision)
            .OrderBy(item => item.AppliedRevision)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var patches = new List<ConversationContextPatch>(rows.Count);
        foreach (var row in rows)
        {
            patches.Add(await ReadContextPatchAsync(row, cancellationToken).ConfigureAwait(false));
        }
        return patches;
    }

    public async Task<TurnRecord> CreateTurnAsync(Guid sessionId, Guid userMessageId, string kind, long baseContextRevision, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Session was not found.");
        var now = DateTimeOffset.UtcNow;
        var turn = new TurnRecord { Id = Guid.NewGuid(), TenantId = session.TenantId, WorkspaceId = session.WorkspaceId, SessionId = sessionId, UserMessageId = userMessageId, Kind = kind, Status = "accepted", BaseContextRevision = baseContextRevision, ResultContextRevision = baseContextRevision, CreatedAt = now, UpdatedAt = now };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Turns.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId && x.UserMessageId == userMessageId, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;
        db.Turns.Add(turn);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return turn;
    }

    public async Task CompleteTurnAsync(Guid turnId, Guid runId, Guid? assistantMessageId, long resultContextRevision, string status, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var turn = await db.Turns.SingleOrDefaultAsync(x => x.Id == turnId, cancellationToken).ConfigureAwait(false);
        if (turn is null) return;
        turn.RunId = runId;
        turn.AssistantMessageId = assistantMessageId;
        turn.ResultContextRevision = resultContextRevision;
        turn.Status = status;
        turn.UpdatedAt = DateTimeOffset.UtcNow;
        turn.CompletedAt = turn.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task<ConversationTurn> IConversationStore.CreateTurnAsync(Guid sessionId, Guid userMessageId, string kind, long baseContextRevision, CancellationToken cancellationToken) =>
        ToConversationTurn(await CreateTurnAsync(sessionId, userMessageId, kind, baseContextRevision, cancellationToken).ConfigureAwait(false));

    async Task<ConversationTurn?> IConversationStore.FindTurnByUserMessageAsync(Guid userMessageId, CancellationToken cancellationToken)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Turns.AsNoTracking().SingleOrDefaultAsync(x => x.UserMessageId == userMessageId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToConversationTurn(row);
    }

    async Task IConversationStore.AttachRunAsync(Guid turnId, Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var turn = await db.Turns.SingleOrDefaultAsync(x => x.Id == turnId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Turn was not found.");
        turn.RunId = runId;
        turn.Status = "running";
        turn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    Task IConversationStore.CompleteTurnAsync(Guid turnId, Guid runId, Guid? assistantMessageId, long resultContextRevision, string status, CancellationToken cancellationToken) =>
        CompleteTurnAsync(turnId, runId, assistantMessageId, resultContextRevision, status, cancellationToken);

    Task<IReadOnlyList<ConversationContextVersion>> IConversationStore.ListContextVersionsAsync(Guid sessionId, Guid? runId, int limit, CancellationToken cancellationToken) =>
        ListContextVersionsAsync(sessionId, runId, limit, cancellationToken);

    Task<ContextPatchApplyResult> IConversationStore.ApplyContextPatchAsync(ContextPatchRequest request, CancellationToken cancellationToken) =>
        ApplyContextPatchAsync(request, cancellationToken);

    Task<IReadOnlyList<ConversationContextPatch>> IConversationStore.ListAppliedContextPatchesAsync(Guid sessionId, Guid runId, long afterRevision, CancellationToken cancellationToken) =>
        ListAppliedContextPatchesAsync(sessionId, runId, afterRevision, cancellationToken);

    public async Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Sessions.AsNoTracking().Where(x => x.Id == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active)
            .Select(x => new SessionReference(
                x.Id, x.ProjectId, x.TenantId, x.WorkspaceId, x.ModeVersionId,
                x.MeetingModelOverrideProviderInstanceId == null
                    ? null
                    : new SessionModelOverride(x.MeetingModelOverrideProviderInstanceId.Value, x.MeetingModelOverrideModel)))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectReference?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Projects.AsNoTracking()
            .Where(x => x.Id == projectId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.LifecycleStatus == LifecycleStatuses.Active)
            .Select(x => new ProjectReference(x.Id, x.TenantId, x.WorkspaceId, x.RootPath))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectReference?> FindByRootAsync(string cwd, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        string normalized;
        try
        {
            normalized = NormalizeRootPath(cwd.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        return await db.Projects.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId
                && x.LifecycleStatus == LifecycleStatuses.Active
                && x.NormalizedRootPath == normalized)
            .Select(x => new ProjectReference(x.Id, x.TenantId, x.WorkspaceId, x.RootPath))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
        await EnsureSessionColumnsAsync(db, cancellationToken).ConfigureAwait(false);
        var scope = _tenantContext.Current;
        var legacyProjects = await db.Projects.Where(x => x.TenantId == Guid.Empty && x.WorkspaceId == Guid.Empty).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var project in legacyProjects) { project.TenantId = scope.TenantId; project.WorkspaceId = scope.WorkspaceId; }
        var legacySessions = await db.Sessions.Where(x => x.TenantId == Guid.Empty && x.WorkspaceId == Guid.Empty).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in legacySessions) { session.TenantId = scope.TenantId; session.WorkspaceId = scope.WorkspaceId; }
        if (legacyProjects.Count != 0 || legacySessions.Count != 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var sessionIds = await db.Sessions.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId).Select(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sessionId in sessionIds)
        {
            if (await db.Messages.AnyAsync(x => x.SessionId == sessionId, cancellationToken).ConfigureAwait(false)) continue;
            var history = await ReadHistoryAsync(sessionId, cancellationToken).ConfigureAwait(false);
            foreach (var legacy in history.Messages.OrderBy(x => x.CreatedAt))
                await AddMessageAsync(sessionId, legacy.Content, legacy.Role, legacy.RunId, legacy.TurnId, legacy.ClientMessageId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSessionColumnsAsync(MemoryDbContext db, CancellationToken ct)
    {
        // ponytail: idempotent for both providers; SQLite lacks IF NOT EXISTS before 3.35 so use pragma, PostgreSQL uses IF NOT EXISTS
        if (db.Database.IsSqlite())
        {
            var cols = (await db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('sessions')").ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!cols.Contains("mode_version_id"))
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"sessions\" ADD COLUMN \"mode_version_id\" TEXT NULL", ct).ConfigureAwait(false);
            if (!cols.Contains("meeting_model_override_provider_instance_id"))
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"sessions\" ADD COLUMN \"meeting_model_override_provider_instance_id\" TEXT NULL", ct).ConfigureAwait(false);
            if (!cols.Contains("meeting_model_override_model"))
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"sessions\" ADD COLUMN \"meeting_model_override_model\" TEXT NULL", ct).ConfigureAwait(false);
            return;
        }
        // PostgreSQL (and any other) – IF NOT EXISTS is idempotent; swallow provider-specific syntax errors for unknown providers
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"sessions\" ADD COLUMN IF NOT EXISTS \"mode_version_id\" uuid NULL", ct).ConfigureAwait(false); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"sessions\" ADD COLUMN IF NOT EXISTS \"meeting_model_override_provider_instance_id\" uuid NULL", ct).ConfigureAwait(false); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"sessions\" ADD COLUMN IF NOT EXISTS \"meeting_model_override_model\" TEXT NULL", ct).ConfigureAwait(false); } catch { }
    }

    private async Task EnsureSessionExistsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is null) throw new KeyNotFoundException("Session was not found.");
    }

    private async Task<SessionHistoryFile> ReadHistoryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = _paths.SessionHistory(sessionId);
        if (!File.Exists(path)) return new SessionHistoryFile { SessionId = sessionId };
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionHistoryFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Session history is invalid.");
    }

    private async Task<StoredMessage> ToStoredMessageAsync(MessageRecord row, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(row.ContentReference, row.ContentHash, row.ContentLength, "text/plain; charset=utf-8"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return new StoredMessage
        {
            Id = row.Id, SessionId = row.SessionId, RunId = row.RunId, TurnId = row.TurnId,
            ClientMessageId = row.ClientMessageId, Sequence = row.Sequence, Role = row.Role,
            Content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), CreatedAt = row.CreatedAt
        };
    }

    private static ConversationMessage ToConversationMessage(StoredMessage item) =>
        new(item.Id, item.SessionId, item.RunId, item.TurnId, item.ClientMessageId, item.Sequence, item.Role, item.Content, item.CreatedAt);

    private static ConversationTurn ToConversationTurn(TurnRecord item) =>
        new(item.Id, item.SessionId, item.UserMessageId, item.AssistantMessageId, item.RunId, item.Kind, item.Status, item.BaseContextRevision, item.ResultContextRevision, item.CreatedAt, item.UpdatedAt, item.CompletedAt);

    private async Task<ConversationContextPatch> ReadContextPatchAsync(ContextPatchRecord row, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(
            new ContentReference(row.ContentReference, row.ContentHash, row.ContentLength, "application/json"), cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var kind = root.TryGetProperty("kind", out var kindNode) && kindNode.ValueKind == JsonValueKind.String
            ? kindNode.GetString() ?? "supplement"
            : "supplement";
        var summary = root.TryGetProperty("summary", out var summaryNode) && summaryNode.ValueKind == JsonValueKind.String
            ? summaryNode.GetString() ?? string.Empty
            : string.Empty;
        var content = root.TryGetProperty("content", out var contentNode) && contentNode.ValueKind == JsonValueKind.String
            ? contentNode.GetString() ?? string.Empty
            : string.Empty;
        return new ConversationContextPatch(
            row.Id,
            row.SessionId,
            row.RunId,
            row.AgentInstanceId,
            row.BaseRevision,
            row.AppliedRevision ?? row.BaseRevision,
            kind,
            summary,
            content,
            row.CreatedAt);
    }

    private async Task<ContentReference> StoreContextDocumentAsync(
        Guid tenantId,
        Guid workspaceId,
        string kind,
        object document,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions)));
        return await _content.PutAsync(new ContentWriteRequest(tenantId, workspaceId, kind, "application/json", stream), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteHistoryAsync(Guid sessionId, SessionHistoryFile history, CancellationToken cancellationToken)
    {
        var path = _paths.SessionHistory(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, history, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string NormalizeRootPath(string rootPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)).ToUpperInvariant();
}

public sealed class SessionHistoryFile
{
    public int Version { get; set; } = 1;
    public Guid SessionId { get; set; }
    public long Revision { get; set; }
    public List<StoredMessage> Messages { get; set; } = [];
}

public sealed class StoredMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? RunId { get; set; }
    public Guid? TurnId { get; set; }
    public string? ClientMessageId { get; set; }
    public long Sequence { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
