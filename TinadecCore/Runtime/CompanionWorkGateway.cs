using System.Text;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Companion;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.TinaChat;

namespace TinadecCore.Runtime;

/// <summary>
/// Implements the companion's governed command &amp; perception verbs in the composition root,
/// because the Companion module must not reference Lifecycle, Memory, AgentConfiguration, or DmaEA.
///
/// Enforces two hard boundaries:
/// 1. <b>Tenancy &amp; <c>work_session</c> Connector Grants.</b> Every session, run, and project is
///    resolved through tenant-scoped stores, and whenever <c>companionId</c> is supplied, access is
///    gated by the Companion's active <c>work_session</c> bindings (<c>"*"</c> wildcard or matching
///    project id / workspace root path; auto-seeded with <c>"*"</c> on the tenant's default/main
///    companion if missing). Once granted a workspace, the Companion holds top-level command
///    authority over ALL sessions in that workspace (both Bot-created and user-created Flat/Space
///    sessions, including mid-flight steering and immediate stop/cancel).
/// 2. <b>Admission, not a second engine.</b> Dispatching, steering, and cancellation reuse the
///    ordinary full-duplex coordinator, durable context-patch interrupt pipeline, and run control
///    plane, while recording <see cref="ICompanionSessionBackflow.TrackCommandedSession"/> for
///    event-driven backflow wakeups.
/// </summary>
public sealed class CompanionWorkGateway(
    ISessionLocator sessions,
    ProjectSessionStore sessionStore,
    IConversationStore conversations,
    ILifecycleManager lifecycle,
    IFullDuplexRunCoordinator coordinator,
    IDbContextFactory<LifecycleDbContext> runDb,
    CompanionChatBridge chatBridge,
    ITenantContextAccessor tenant,
    IDbContextFactory<CompanionDbContext>? companionDb = null,
    IDbContextFactory<AgentConfigurationDbContext>? agentConfigDb = null,
    GlobalAgentConfiguration? globalAgentConfig = null,
    SessionPlanService? sessionPlans = null,
    IFullDuplexRunEngine? runEngine = null,
    IRunInterrupts? runInterrupts = null,
    StorageLifecycleService? storageLifecycle = null,
    ICompanionSessionBackflow? sessionBackflow = null) : ICompanionWorkGateway
{
    private const int MaxSessions = 50;
    private const int DefaultSessionMessages = 20;
    private const int MaxSessionMessages = 50;

    private sealed record WorkSessionGrantSet(
        bool Unrestricted,
        bool HasWildcard,
        HashSet<Guid> AllowedProjectIds,
        HashSet<string> AllowedNormalizedRoots)
    {
        public static WorkSessionGrantSet AllowAll { get; } = new(true, true, [], []);

        public bool AllowsProject(Guid? projectId, string? projectRootPath = null)
        {
            if (Unrestricted || HasWildcard) return true;
            if (projectId is not { } pid) return false;
            if (AllowedProjectIds.Contains(pid)) return true;
            if (!string.IsNullOrWhiteSpace(projectRootPath)
                && AllowedNormalizedRoots.Contains(NormalizePathKey(projectRootPath)))
            {
                return true;
            }
            return false;
        }
    }

    // ── modes ──

    public async Task<CompanionWorkResult<IReadOnlyList<CompanionWorkMode>>> ListModesAsync(
        Guid? companionId = null,
        CancellationToken cancellationToken = default)
    {
        if (agentConfigDb is null)
            return CompanionWorkResult<IReadOnlyList<CompanionWorkMode>>.Ok([]);

        var scope = tenant.Current;
        var factory = globalAgentConfig is not null
            ? await AgentConfigurationLookup.SelectCatalogFactoryAsync(
                agentConfigDb, globalAgentConfig, scope.TenantId, scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            : agentConfigDb;

        var modes = await ReadPublishedModesFromFactoryAsync(factory, scope.TenantId, scope.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (!ReferenceEquals(factory, agentConfigDb))
        {
            var localModes = await ReadPublishedModesFromFactoryAsync(agentConfigDb, scope.TenantId, scope.WorkspaceId, cancellationToken)
                .ConfigureAwait(false);
            var seenSlugs = modes.Select(m => m.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var local in localModes)
            {
                if (seenSlugs.Add(local.Slug))
                    modes.Add(local);
            }
        }

        return CompanionWorkResult<IReadOnlyList<CompanionWorkMode>>.Ok(modes);
    }

    private static async Task<List<CompanionWorkMode>> ReadPublishedModesFromFactoryAsync(
        IDbContextFactory<AgentConfigurationDbContext> factory,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var modeRows = await db.AgentModes.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.WorkspaceId == workspaceId && m.ArchivedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (modeRows.Count == 0) return [];

        var modeIds = modeRows.Select(m => m.Id).ToList();
        var versionRows = await db.ModeVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.WorkspaceId == workspaceId
                && modeIds.Contains(v.AgentModeId) && v.Status == "published")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (versionRows.Count == 0) return [];

        var latestVersionByMode = versionRows
            .GroupBy(v => v.AgentModeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Version).First());

        return modeRows
            .Where(m => latestVersionByMode.ContainsKey(m.Id))
            .OrderBy(m => ModeOrderRank(m.Slug))
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var ver = latestVersionByMode[m.Id];
                return new CompanionWorkMode(
                    ver.Id,
                    m.Slug,
                    m.DisplayName,
                    m.Description,
                    m.OrchestrationTier);
            })
            .ToList();
    }

    private static int ModeOrderRank(string slug) => slug.ToLowerInvariant() switch
    {
        "solo" => 0,
        "plan" => 1,
        "team" or "free_director" => 2,
        "spec" => 3,
        "review" => 4,
        "correction" => 5,
        _ => 10
    };

    // ── sessions ──

    public Task<CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>> ListSessionsAsync(
        CancellationToken cancellationToken = default) =>
        ListSessionsAsync(companionId: null, projectId: null, cancellationToken);

    public async Task<CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>> ListSessionsAsync(
        Guid? companionId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        var grants = await LoadGrantsAsync(companionId, cancellationToken).ConfigureAwait(false);
        if (grants is null)
            return CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>.Fail(
                "companion_not_found", "Companion not found in this tenant.");

        var projects = await sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
        var projectById = projects.ToDictionary(p => p.Id);

        if (projectId is { } explicitPid && !grants.AllowsProject(explicitPid, projectById.GetValueOrDefault(explicitPid)?.RootPath))
        {
            return CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>.Fail(
                "work_session_unauthorized",
                "This companion does not hold an active 'work_session' connector grant for the requested project workspace.");
        }

        var rows = await sessionStore.ListSessionsAsync(projectId, LifecycleStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        var authorizedRows = rows
            .Where(row => grants.AllowsProject(
                row.ProjectId,
                row.ProjectId is { } pid && projectById.TryGetValue(pid, out var proj) ? proj.RootPath : null))
            .Take(MaxSessions)
            .ToList();
        if (authorizedRows.Count == 0)
            return CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>.Ok([]);

        var ids = authorizedRows.Select(row => row.Id).ToList();
        var now = DateTimeOffset.UtcNow;

        await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var runs = await db.Runs.AsNoTracking()
            .Where(run => ids.Contains(run.SessionId)
                && run.TenantId == scope.TenantId
                && run.WorkspaceId == scope.WorkspaceId
                && run.ParentRunId == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var latestRunBySession = runs
            .GroupBy(run => run.SessionId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(run => run.CreatedAt).First());

        var sessionNullableIds = ids.Select(id => (Guid?)id).ToList();
        var pendingApprovalRows = (await db.ApprovalRequests.AsNoTracking()
                .Where(a => a.TenantId == scope.TenantId
                    && a.WorkspaceId == scope.WorkspaceId
                    && a.Status == "pending"
                    && sessionNullableIds.Contains(a.SessionId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(a => a.SessionId.HasValue && a.ExpiresAt > now)
            .GroupBy(a => a.SessionId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CompanionPendingApprovalItem>)g
                    .OrderByDescending(a => a.CreatedAt)
                    .Select(MapPendingApproval)
                    .ToList());

        var modeNames = await LoadModeNamesAsync(
            authorizedRows.Where(r => r.ModeVersionId.HasValue).Select(r => r.ModeVersionId!.Value).Distinct().ToList(),
            scope.TenantId,
            scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);

        var result = new List<CompanionWorkSession>(authorizedRows.Count);
        foreach (var row in authorizedRows)
        {
            var (planSummary, planProgress) = await LoadPlanSummaryAsync(row.Id, cancellationToken).ConfigureAwait(false);
            var projName = row.ProjectId is { } pid && projectById.TryGetValue(pid, out var proj) ? proj.Name : null;
            var modeName = row.ModeVersionId is { } mvid ? modeNames.GetValueOrDefault(mvid) : null;
            var approvals = pendingApprovalRows.GetValueOrDefault(row.Id) ?? [];

            result.Add(new CompanionWorkSession(
                row.Id,
                row.Title,
                row.ProjectId,
                latestRunBySession.TryGetValue(row.Id, out var run)
                    ? new CompanionWorkRunSummary(run.Id, run.Status, run.Summary)
                    : null,
                ProjectName: projName,
                ModeName: modeName,
                PlanSummary: planSummary,
                PendingApprovals: approvals,
                ViewMode: row.ViewMode ?? "flat",
                UpdatedAt: row.UpdatedAt,
                PlanProgress: planProgress));
        }

        return CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>.Ok(result);
    }

    public Task<CompanionWorkResult<CompanionWorkSession>> CreateSessionAsync(
        Guid? companionId,
        Guid? projectId,
        string? title,
        Guid? modeVersionId,
        CancellationToken cancellationToken = default) =>
        CreateSessionAsync(companionId, conversationId: null, projectId, title, modeVersionId, cancellationToken);

    public async Task<CompanionWorkResult<CompanionWorkSession>> CreateSessionAsync(
        Guid? companionId,
        Guid? conversationId,
        Guid? projectId,
        string? title,
        Guid? modeVersionId,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        var grants = await LoadGrantsAsync(companionId, cancellationToken).ConfigureAwait(false);
        if (grants is null)
            return CompanionWorkResult<CompanionWorkSession>.Fail("companion_not_found", "Companion not found in this tenant.");

        ProjectRecord? project = null;
        if (projectId is { } pid)
        {
            var activeProjects = await sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
            project = activeProjects.FirstOrDefault(p => p.Id == pid);
            if (project is null)
                return CompanionWorkResult<CompanionWorkSession>.Fail("project_not_found", "The target project workspace does not exist.");
        }

        if (!grants.AllowsProject(projectId, project?.RootPath))
        {
            return CompanionWorkResult<CompanionWorkSession>.Fail(
                "work_session_unauthorized",
                "This companion does not hold an active 'work_session' connector grant for the target workspace.");
        }

        var effectiveModeVersionId = modeVersionId;
        if (effectiveModeVersionId is { } requestedMode)
        {
            if (agentConfigDb is not null && globalAgentConfig is not null)
            {
                var valid = await AgentConfigurationLookup.HasPublishedModeAsync(
                    agentConfigDb, globalAgentConfig, scope.TenantId, scope.WorkspaceId, requestedMode, cancellationToken)
                    .ConfigureAwait(false);
                if (!valid)
                    return CompanionWorkResult<CompanionWorkSession>.Fail(
                        "mode_not_found", "mode_version_id does not reference a published Agent Mode in this workspace.");
            }
        }
        else if (agentConfigDb is not null && globalAgentConfig is not null)
        {
            effectiveModeVersionId = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
                agentConfigDb, globalAgentConfig, scope.TenantId, scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }

        if (effectiveModeVersionId is null || effectiveModeVersionId == Guid.Empty)
        {
            var availableModes = await ListModesAsync(companionId, cancellationToken).ConfigureAwait(false);
            effectiveModeVersionId = availableModes.Value?.FirstOrDefault()?.ModeVersionId;
        }

        if (effectiveModeVersionId is null || effectiveModeVersionId == Guid.Empty)
        {
            return CompanionWorkResult<CompanionWorkSession>.Fail(
                "agent_mode_not_configured", "No published Agent Mode is configured in this workspace.");
        }

        ConversationIdentityResolver.ConversationIdentity? identity = null;
        string? modeName = null;
        if (agentConfigDb is not null)
        {
            var modeFactory = globalAgentConfig is not null
                ? await AgentConfigurationLookup.SelectModeFactoryAsync(
                    agentConfigDb, globalAgentConfig, scope.TenantId, scope.WorkspaceId, effectiveModeVersionId.Value, cancellationToken)
                    .ConfigureAwait(false)
                : agentConfigDb;
            await using var cfg = await modeFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var versionRow = await cfg.ModeVersions.AsNoTracking()
                .Where(x => x.Id == effectiveModeVersionId.Value && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
                .Select(x => new { x.AgentModeId, x.SnapshotJson })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (versionRow is not null)
            {
                modeName = await cfg.AgentModes.AsNoTracking()
                    .Where(m => m.Id == versionRow.AgentModeId)
                    .Select(m => m.DisplayName)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                var defs = await cfg.AgentDefinitions.AsNoTracking()
                    .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
                    .Select(x => new ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                identity = ConversationIdentityResolver.Resolve(versionRow.SnapshotJson, defs);
            }
        }

        try
        {
            var created = await sessionStore.CreateSessionAsync(
                projectId: projectId,
                title: string.IsNullOrWhiteSpace(title) ? "Companion Session" : title.Trim(),
                modeVersionId: effectiveModeVersionId.Value,
                conversationNodeKey: identity?.NodeKey,
                conversationTemplateSlug: identity?.TemplateSlug,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (companionId is { } cid && cid != Guid.Empty)
            {
                sessionBackflow?.TrackCommandedSession(scope.TenantId, cid, conversationId, created.Id, runId: null);
            }

            return CompanionWorkResult<CompanionWorkSession>.Ok(new CompanionWorkSession(
                created.Id,
                created.Title,
                created.ProjectId,
                LatestRun: null,
                ProjectName: project?.Name,
                ModeName: modeName,
                PlanSummary: null,
                PendingApprovals: [],
                ViewMode: created.ViewMode ?? "flat",
                UpdatedAt: created.UpdatedAt));
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return CompanionWorkResult<CompanionWorkSession>.Fail("session_create_failed", ex.Message);
        }
    }

    // ── runs ──

    public Task<CompanionWorkResult<CompanionWorkRun>> ReadRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        ReadRunAsync(companionId: null, runId, cancellationToken);

    public async Task<CompanionWorkResult<CompanionWorkRun>> ReadRunAsync(
        Guid? companionId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Runs.AsNoTracking()
            .SingleOrDefaultAsync(run => run.Id == runId
                && run.TenantId == scope.TenantId
                && run.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return CompanionWorkResult<CompanionWorkRun>.Fail(
                "run_not_found", "The run does not exist in this tenant/workspace.");

        var session = await sessionStore.GetSessionAsync(row.SessionId, cancellationToken).ConfigureAwait(false);
        var (authorized, authErr, project) = await VerifySessionAuthorizationAsync(companionId, session, cancellationToken).ConfigureAwait(false);
        if (!authorized)
            return CompanionWorkResult<CompanionWorkRun>.Fail(authErr!, "This companion is not authorized for the session's workspace.");

        var state = await lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        var (planSummary, _) = await LoadPlanSummaryAsync(row.SessionId, cancellationToken).ConfigureAwait(false);
        var approvals = await LoadRunPendingApprovalsAsync(db, scope.TenantId, scope.WorkspaceId, runId, cancellationToken).ConfigureAwait(false);
        var modeName = session?.ModeVersionId is { } mvid
            ? (await LoadModeNamesAsync([mvid], scope.TenantId, scope.WorkspaceId, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(mvid)
            : null;

        return CompanionWorkResult<CompanionWorkRun>.Ok(new CompanionWorkRun(
            runId,
            row.SessionId,
            RunStatusWire(state.Status),
            state.Summary ?? row.Summary,
            state.TerminalErrorCategory ?? row.TerminalErrorCategory,
            ProjectName: project?.Name,
            ModeName: modeName,
            PlanSummary: planSummary,
            PendingApprovals: approvals));
    }

    // ── dispatch / steer / cancel / side-question / approvals ──

    public Task<CompanionWorkResult<CompanionWorkDispatch>> DispatchAsync(
        Guid sessionId,
        string task,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(companionId: null, sessionId, task, idempotencyKey, modeVersionId: null, worktree: null, conversationId: null, cancellationToken);

    public Task<CompanionWorkResult<CompanionWorkDispatch>> DispatchAsync(
        Guid sessionId,
        string task,
        string idempotencyKey,
        Guid? modeVersionId,
        bool? worktree,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(companionId: null, sessionId, task, idempotencyKey, modeVersionId, worktree, conversationId: null, cancellationToken);

    public async Task<CompanionWorkResult<CompanionWorkDispatch>> DispatchAsync(
        Guid? companionId,
        Guid sessionId,
        string task,
        string idempotencyKey,
        Guid? modeVersionId = null,
        bool? worktree = null,
        Guid? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        if (string.IsNullOrWhiteSpace(task))
            return CompanionWorkResult<CompanionWorkDispatch>.Fail("invalid_arguments", "task is required.");

        var reference = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (reference is null || reference.TenantId != scope.TenantId || reference.WorkspaceId != scope.WorkspaceId)
            return CompanionWorkResult<CompanionWorkDispatch>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");
        var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return CompanionWorkResult<CompanionWorkDispatch>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");

        var (authorized, authErr, project) = await VerifySessionAuthorizationAsync(companionId, session, cancellationToken).ConfigureAwait(false);
        if (!authorized)
            return CompanionWorkResult<CompanionWorkDispatch>.Fail(
                authErr!, "This companion does not hold an active 'work_session' connector grant for the target session's workspace.");

        var content = task.Trim();
        var clientMessageId = string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"companion-dispatch-{Guid.NewGuid():N}"
            : idempotencyKey.Trim();
        var spaceOptions = worktree is { } wt
            ? new SpaceRunOptions(Worktree: wt, WorkflowModeVersionId: modeVersionId)
            : null;
        try
        {
            var submission = await coordinator.SubmitAsync(new FullDuplexInvocation(
                sessionId,
                content,
                clientMessageId,
                PermissionMode: session.PermissionMode,
                TargetRunId: null,
                ExpectedContextRevision: null,
                ModeVersionId: modeVersionId,
                SpaceOptions: spaceOptions), cancellationToken).ConfigureAwait(false);

            if (companionId is { } cid && cid != Guid.Empty)
            {
                sessionBackflow?.TrackCommandedSession(scope.TenantId, cid, conversationId, sessionId, submission.RunId);
            }

            var effectiveModeId = modeVersionId ?? session.ModeVersionId;
            var modeName = effectiveModeId is { } mvid
                ? (await LoadModeNamesAsync([mvid], scope.TenantId, scope.WorkspaceId, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(mvid)
                : null;
            var (planSummary, _) = await LoadPlanSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false);

            return CompanionWorkResult<CompanionWorkDispatch>.Ok(new CompanionWorkDispatch(
                sessionId,
                submission.RunId,
                session.Title,
                ProjectId: session.ProjectId,
                ProjectName: project?.Name,
                ModeName: modeName,
                PlanSummary: planSummary,
                PendingApprovals: []));
        }
        catch (RunAdmissionException ex)
        {
            return CompanionWorkResult<CompanionWorkDispatch>.Fail(ex.Code.ToLowerInvariant(), ex.Message);
        }
        catch (TinaChatException ex)
        {
            return CompanionWorkResult<CompanionWorkDispatch>.Fail(ex.Code, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return CompanionWorkResult<CompanionWorkDispatch>.Fail("session_not_found", ex.Message);
        }
    }

    public Task<CompanionWorkResult<CompanionWorkSteer>> SteerRunAsync(
        Guid? companionId,
        Guid sessionId,
        Guid? runId,
        string guidance,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        SteerRunAsync(companionId, conversationId: null, sessionId, runId, guidance, idempotencyKey, cancellationToken);

    public async Task<CompanionWorkResult<CompanionWorkSteer>> SteerRunAsync(
        Guid? companionId,
        Guid? conversationId,
        Guid sessionId,
        Guid? runId,
        string guidance,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        if (string.IsNullOrWhiteSpace(guidance))
            return CompanionWorkResult<CompanionWorkSteer>.Fail("invalid_arguments", "guidance is required.");

        var reference = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (reference is null || reference.TenantId != scope.TenantId || reference.WorkspaceId != scope.WorkspaceId)
            return CompanionWorkResult<CompanionWorkSteer>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");
        var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return CompanionWorkResult<CompanionWorkSteer>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");

        var (authorized, authErr, _) = await VerifySessionAuthorizationAsync(companionId, session, cancellationToken).ConfigureAwait(false);
        if (!authorized)
            return CompanionWorkResult<CompanionWorkSteer>.Fail(
                authErr!, "This companion does not hold an active 'work_session' connector grant for the target session's workspace.");

        var targetRun = await FindTargetRunAsync(scope.TenantId, scope.WorkspaceId, sessionId, runId, requireActive: true, cancellationToken)
            .ConfigureAwait(false);
        if (targetRun is null)
        {
            if (runId is not null)
            {
                return CompanionWorkResult<CompanionWorkSteer>.Fail(
                    "run_not_active", "The specified run is not active in this session.");
            }

            // Session is currently idle: start a clean follow-up run with the guidance.
            var followup = await DispatchAsync(
                companionId, sessionId, guidance, idempotencyKey, modeVersionId: null, worktree: null, conversationId: conversationId, cancellationToken)
                .ConfigureAwait(false);
            return followup.IsSuccess && followup.Value is { } dispatched
                ? CompanionWorkResult<CompanionWorkSteer>.Ok(new CompanionWorkSteer(
                    sessionId, dispatched.RunId, "dispatched_followup", dispatched.ModeName, dispatched.PlanSummary))
                : CompanionWorkResult<CompanionWorkSteer>.Fail(followup.Error ?? "steer_failed", followup.Message);
        }

        var trimmedGuidance = guidance.Trim();
        var baseRev = await conversations.GetContextRevisionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var patch = await conversations.ApplyContextPatchAsync(
            new ContextPatchRequest(sessionId, baseRev, trimmedGuidance, $"Companion steering for {targetRun.Id}", targetRun.Id, Kind: "supplement"),
            cancellationToken).ConfigureAwait(false);
        if (patch.Status == "stale")
        {
            patch = await conversations.ApplyContextPatchAsync(
                new ContextPatchRequest(sessionId, patch.CurrentRevision, trimmedGuidance, $"Companion steering for {targetRun.Id}", targetRun.Id, Kind: "supplement"),
                cancellationToken).ConfigureAwait(false);
            if (patch.Status == "stale")
            {
                return CompanionWorkResult<CompanionWorkSteer>.Fail(
                    "context_conflict", "Context revision changed concurrently; retry steering.");
            }
        }

        if (storageLifecycle is not null)
        {
            await storageLifecycle.AppendEventAsync(
                targetRun.Id,
                "interaction.steering",
                new { interaction_id = Guid.NewGuid(), target_run_id = targetRun.Id, content = trimmedGuidance, dispatch_mode = "insert", interrupt = true, source = "companion" },
                $"Companion steering injected into run {targetRun.Id}",
                "info",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await storageLifecycle.AppendRunStreamAsync(
                targetRun.Id,
                new DurableRunStreamAppend(Guid.NewGuid(), "steering", null, IdempotencyKey: $"run:{targetRun.Id}:companion-steer:{idempotencyKey}"),
                cancellationToken).ConfigureAwait(false);
        }

        runInterrupts?.Request(targetRun.Id, new RunInterruptRequest(patch.PatchId, trimmedGuidance, DateTimeOffset.UtcNow));

        if (targetRun.Status == "awaiting_user" && storageLifecycle is not null)
        {
            await storageLifecycle.SetRunStatusAsync(
                targetRun.Id,
                "executing",
                "Companion steering accepted; resuming after review.",
                cancellationToken).ConfigureAwait(false);
        }

        if (runEngine is not null)
            await runEngine.EnqueueAsync(targetRun.Id, cancellationToken).ConfigureAwait(false);

        if (companionId is { } cid && cid != Guid.Empty)
        {
            sessionBackflow?.TrackCommandedSession(scope.TenantId, cid, conversationId, sessionId, targetRun.Id);
        }

        var (planSummary, _) = await LoadPlanSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var modeName = session.ModeVersionId is { } mvid
            ? (await LoadModeNamesAsync([mvid], scope.TenantId, scope.WorkspaceId, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(mvid)
            : null;

        return CompanionWorkResult<CompanionWorkSteer>.Ok(
            new CompanionWorkSteer(sessionId, targetRun.Id, "steering_injected", modeName, planSummary));
    }

    public async Task<CompanionWorkResult<CompanionWorkCancel>> CancelRunAsync(
        Guid? companionId,
        Guid sessionId,
        Guid? runId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        var reference = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (reference is null || reference.TenantId != scope.TenantId || reference.WorkspaceId != scope.WorkspaceId)
            return CompanionWorkResult<CompanionWorkCancel>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");
        var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return CompanionWorkResult<CompanionWorkCancel>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");

        var (authorized, authErr, _) = await VerifySessionAuthorizationAsync(companionId, session, cancellationToken).ConfigureAwait(false);
        if (!authorized)
            return CompanionWorkResult<CompanionWorkCancel>.Fail(
                authErr!, "This companion does not hold an active 'work_session' connector grant for the target session's workspace.");

        var targetRun = await FindTargetRunAsync(scope.TenantId, scope.WorkspaceId, sessionId, runId, requireActive: true, cancellationToken)
            .ConfigureAwait(false);
        if (targetRun is null)
            return CompanionWorkResult<CompanionWorkCancel>.Fail(
                "no_active_run", "There is no active run in this session to cancel.");

        var control = await coordinator.ControlAsync(
            targetRun.Id,
            new RunControlCommand("cancel", $"companion-cancel-{targetRun.Id:N}"),
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(reason) && storageLifecycle is not null)
        {
            try
            {
                await storageLifecycle.AppendEventAsync(
                    targetRun.Id,
                    "run.cancelled_by_companion",
                    new { companion_id = companionId, reason = reason.Trim() },
                    $"Companion stopped run: {reason.Trim()}",
                    "info",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort diagnostic event.
            }
        }

        return CompanionWorkResult<CompanionWorkCancel>.Ok(
            new CompanionWorkCancel(sessionId, targetRun.Id, control.Status));
    }

    public async Task<CompanionWorkResult<CompanionWorkSideAnswer>> SideQuestionAsync(
        Guid? companionId,
        Guid sessionId,
        Guid? runId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        if (string.IsNullOrWhiteSpace(question))
            return CompanionWorkResult<CompanionWorkSideAnswer>.Fail("invalid_arguments", "question is required.");

        var reference = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (reference is null || reference.TenantId != scope.TenantId || reference.WorkspaceId != scope.WorkspaceId)
            return CompanionWorkResult<CompanionWorkSideAnswer>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");
        var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return CompanionWorkResult<CompanionWorkSideAnswer>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");

        var (authorized, authErr, project) = await VerifySessionAuthorizationAsync(companionId, session, cancellationToken).ConfigureAwait(false);
        if (!authorized)
            return CompanionWorkResult<CompanionWorkSideAnswer>.Fail(
                authErr!, "This companion does not hold an active 'work_session' connector grant for the target session's workspace.");

        var targetRun = await FindTargetRunAsync(scope.TenantId, scope.WorkspaceId, sessionId, runId, requireActive: false, cancellationToken)
            .ConfigureAwait(false);
        var (planSummary, planProgress) = await LoadPlanSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CompanionPendingApprovalItem> approvals = [];
        if (targetRun is not null)
        {
            await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            approvals = await LoadRunPendingApprovalsAsync(db, scope.TenantId, scope.WorkspaceId, targetRun.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        var recentMessages = await conversations.ListMessagesAsync(sessionId, 12, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {session.Title} (view={session.ViewMode ?? "flat"}{(project is not null ? $", workspace={project.Name}" : string.Empty)})");
        if (targetRun is not null)
        {
            sb.AppendLine($"Latest Run ({targetRun.Id}): status={targetRun.Status}" +
                (!string.IsNullOrWhiteSpace(targetRun.Summary) ? $", summary={targetRun.Summary}" : string.Empty));
        }
        else
        {
            sb.AppendLine("Latest Run: idle (no runs yet)");
        }

        if (!string.IsNullOrWhiteSpace(planSummary))
            sb.AppendLine($"Plan Progress: {planSummary}");
        if (approvals.Count > 0)
        {
            sb.AppendLine($"Pending Approvals ({approvals.Count}): " +
                string.Join("; ", approvals.Select(a => $"{a.ToolId} [{a.RiskLevel ?? "medium"}] ({a.ApprovalId}): {a.Summary}")));
        }

        if (recentMessages.Count > 0)
        {
            sb.AppendLine("Recent Transcript Context:");
            foreach (var msg in recentMessages.TakeLast(5))
            {
                var snippet = msg.Content.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (snippet.Length > 220) snippet = snippet[..220] + "…";
                sb.AppendLine($"- [{msg.Role}] {snippet}");
            }
        }

        return CompanionWorkResult<CompanionWorkSideAnswer>.Ok(new CompanionWorkSideAnswer(
            sessionId,
            targetRun?.Id,
            sb.ToString().TrimEnd(),
            targetRun?.Status,
            planSummary));
    }

    public async Task<CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>> ListApprovalsAsync(
        Guid? companionId,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        var grants = await LoadGrantsAsync(companionId, cancellationToken).ConfigureAwait(false);
        if (grants is null)
            return CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>.Fail(
                "companion_not_found", "Companion not found in this tenant.");

        var activeProjects = await sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
        var projectById = activeProjects.ToDictionary(p => p.Id);

        var sessionRows = await sessionStore.ListSessionsAsync(null, LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
        var authorizedSessions = sessionRows
            .Where(s => (!sessionId.HasValue || s.Id == sessionId.Value)
                && grants.AllowsProject(s.ProjectId, s.ProjectId is { } pid && projectById.TryGetValue(pid, out var p) ? p.RootPath : null))
            .ToDictionary(s => s.Id);

        if (sessionId.HasValue && !authorizedSessions.ContainsKey(sessionId.Value))
        {
            var rawSession = sessionRows.FirstOrDefault(s => s.Id == sessionId.Value);
            return rawSession is null
                ? CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>.Fail("session_not_found", "The session does not exist in this tenant/workspace.")
                : CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>.Fail("work_session_unauthorized", "This companion is not authorized for the session's workspace.");
        }

        if (authorizedSessions.Count == 0)
            return CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>.Ok([]);

        var now = DateTimeOffset.UtcNow;
        var allowedNullableIds = authorizedSessions.Keys.Select(id => (Guid?)id).ToList();
        await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await db.ApprovalRequests.AsNoTracking()
                .Where(a => a.TenantId == scope.TenantId
                    && a.WorkspaceId == scope.WorkspaceId
                    && a.Status == "pending"
                    && allowedNullableIds.Contains(a.SessionId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(a => a.SessionId.HasValue && a.RunId.HasValue && a.ExpiresAt > now)
            .OrderByDescending(a => a.CreatedAt)
            .ToList();

        var approvals = rows.Select(a =>
        {
            var sess = authorizedSessions[a.SessionId!.Value];
            var projName = sess.ProjectId is { } pid && projectById.TryGetValue(pid, out var proj) ? proj.Name : null;
            return new CompanionWorkApproval(
                a.Id,
                sess.Id,
                sess.Title,
                a.RunId!.Value,
                a.ToolId,
                string.IsNullOrWhiteSpace(a.Summary) ? a.ArgumentsDigest : a.Summary,
                a.Risk,
                a.CreatedAt,
                sess.ProjectId,
                projName);
        }).ToList();

        return CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>.Ok(approvals);
    }

    // ── workspaces & session detail & relay ──

    public Task<CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default) =>
        ListWorkspacesAsync(companionId: null, cancellationToken);

    public async Task<CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>> ListWorkspacesAsync(
        Guid? companionId,
        CancellationToken cancellationToken = default)
    {
        var grants = await LoadGrantsAsync(companionId, cancellationToken).ConfigureAwait(false);
        if (grants is null)
            return CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>.Fail(
                "companion_not_found", "Companion not found in this tenant.");

        var projects = await sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        var activeSessions = await sessionStore.ListSessionsAsync(null, LifecycleStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        var counts = activeSessions
            .Where(session => session.ProjectId is not null)
            .GroupBy(session => session.ProjectId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var result = projects
            .Where(project => grants.AllowsProject(project.Id, project.RootPath))
            .Select(project => new CompanionWorkWorkspaceSummary(
                project.Id,
                project.Name,
                project.RootPath,
                counts.GetValueOrDefault(project.Id)))
            .ToList();
        return CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>.Ok(result);
    }

    public Task<CompanionWorkResult<CompanionWorkSessionDetail>> ReadSessionAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default) =>
        ReadSessionAsync(companionId: null, sessionId, limit, cancellationToken);

    public async Task<CompanionWorkResult<CompanionWorkSessionDetail>> ReadSessionAsync(
        Guid? companionId,
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        var reference = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (reference is null || reference.TenantId != scope.TenantId || reference.WorkspaceId != scope.WorkspaceId)
            return CompanionWorkResult<CompanionWorkSessionDetail>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");
        var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return CompanionWorkResult<CompanionWorkSessionDetail>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");

        var (authorized, authErr, project) = await VerifySessionAuthorizationAsync(companionId, session, cancellationToken).ConfigureAwait(false);
        if (!authorized)
            return CompanionWorkResult<CompanionWorkSessionDetail>.Fail(
                authErr!, "This companion does not hold an active 'work_session' connector grant for the target session's workspace.");

        var take = Math.Clamp(limit <= 0 ? DefaultSessionMessages : limit, 1, MaxSessionMessages);
        var messages = await conversations.ListMessagesAsync(sessionId, take, cancellationToken)
            .ConfigureAwait(false);
        var activeRuns = await lifecycle.CountActiveRunsAsync(sessionId.ToString(), cancellationToken)
            .ConfigureAwait(false);

        var latestRun = await FindTargetRunAsync(scope.TenantId, scope.WorkspaceId, sessionId, runId: null, requireActive: false, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<CompanionPendingApprovalItem> approvals = [];
        if (latestRun is not null)
        {
            await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            approvals = await LoadRunPendingApprovalsAsync(db, scope.TenantId, scope.WorkspaceId, latestRun.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        var (planSummary, _) = await LoadPlanSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var modeName = session.ModeVersionId is { } mvid
            ? (await LoadModeNamesAsync([mvid], scope.TenantId, scope.WorkspaceId, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(mvid)
            : null;

        return CompanionWorkResult<CompanionWorkSessionDetail>.Ok(new CompanionWorkSessionDetail(
            sessionId,
            session.Title,
            session.ProjectId,
            session.PermissionMode ?? "default",
            activeRuns > 0,
            messages.Select(message => new CompanionWorkMessage(
                message.Role, message.Content, message.CreatedAt)).ToList(),
            ProjectName: project?.Name,
            ModeName: modeName,
            PlanSummary: planSummary,
            PendingApprovals: approvals,
            LatestRun: latestRun is null ? null : new CompanionWorkRunSummary(latestRun.Id, latestRun.Status, latestRun.Summary)));
    }

    public async Task<CompanionWorkResult<CompanionWorkRelay>> RelayToCompanionAsync(
        Guid fromCompanionId,
        Guid toCompanionId,
        string content,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (fromCompanionId == Guid.Empty || toCompanionId == Guid.Empty)
            return CompanionWorkResult<CompanionWorkRelay>.Fail("invalid_arguments", "companion ids must be non-empty GUIDs.");
        try
        {
            var message = await chatBridge.RelayAsync(
                fromCompanionId, toCompanionId, content, idempotencyKey, cancellationToken).ConfigureAwait(false);
            return CompanionWorkResult<CompanionWorkRelay>.Ok(
                new CompanionWorkRelay(message.ConversationId, message.Id, message.Sequence));
        }
        catch (TinaChatException ex)
        {
            return CompanionWorkResult<CompanionWorkRelay>.Fail(ex.Code, ex.Message);
        }
    }

    public Task<CompanionWorkResult<CompanionWorkWorkspace>> CreateWorkspaceAsync(
        string name,
        string path,
        CancellationToken cancellationToken = default) =>
        CreateWorkspaceAsync(companionId: null, name, path, cancellationToken);

    public async Task<CompanionWorkResult<CompanionWorkWorkspace>> CreateWorkspaceAsync(
        Guid? companionId,
        string name,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("invalid_arguments", "name is required.");
        if (string.IsNullOrWhiteSpace(path))
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("invalid_arguments", "path is required.");

        var grants = await LoadGrantsAsync(companionId, cancellationToken).ConfigureAwait(false);
        if (grants is null)
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("companion_not_found", "Companion not found in this tenant.");
        if (!grants.Unrestricted && !grants.HasWildcard)
        {
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail(
                "work_session_unauthorized",
                "Creating a new project workspace requires a wildcard '*' work_session grant on this companion.");
        }

        var trimmedPath = path.Trim();
        if (!Path.IsPathFullyQualified(trimmedPath))
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("invalid_arguments", "path must be an absolute path.");
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("invalid_arguments", "path is not a valid filesystem path.");
        }

        try
        {
            Directory.CreateDirectory(fullPath);
            var existing = await sessionStore.FindByRootAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var project = await sessionStore.GetProjectAnyStatusAsync(existing.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
                return CompanionWorkResult<CompanionWorkWorkspace>.Ok(
                    new CompanionWorkWorkspace(existing.ProjectId, project?.Name ?? name.Trim(), existing.RootPath));
            }

            var created = await sessionStore.CreateProjectAsync(name.Trim(), fullPath, cancellationToken).ConfigureAwait(false);
            return CompanionWorkResult<CompanionWorkWorkspace>.Ok(
                new CompanionWorkWorkspace(created.Id, created.Name, created.RootPath));
        }
        catch (InvalidOperationException)
        {
            var raced = await sessionStore.FindByRootAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (raced is null)
                return CompanionWorkResult<CompanionWorkWorkspace>.Fail(
                    "workspace_create_failed", "The workspace could not be registered.");
            var project = await sessionStore.GetProjectAnyStatusAsync(raced.ProjectId, cancellationToken).ConfigureAwait(false);
            return CompanionWorkResult<CompanionWorkWorkspace>.Ok(
                new CompanionWorkWorkspace(raced.ProjectId, project?.Name ?? name.Trim(), raced.RootPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("workspace_create_failed", ex.Message);
        }
    }

    // ── internal grant & metadata helpers ──

    private async Task<WorkSessionGrantSet?> LoadGrantsAsync(Guid? companionId, CancellationToken cancellationToken)
    {
        if (companionId is not { } cid || cid == Guid.Empty || companionDb is null)
            return WorkSessionGrantSet.AllowAll;

        var tenantId = tenant.Current.TenantId;
        await using var db = await companionDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var exists = await db.Companions.AsNoTracking()
            .AnyAsync(x => x.Id == cid && x.TenantId == tenantId && x.Status != "archived", cancellationToken)
            .ConfigureAwait(false);
        if (!exists) return null;

        await CompanionService.EnsureMainWorkSessionGrantCoreAsync(db, tenantId, cid, cancellationToken).ConfigureAwait(false);

        var activeBindings = await db.ConnectionBindings.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.CompanionId == cid
                && x.Kind == "work_session"
                && x.Enabled
                && x.Authorization == "authorized")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasWildcard = false;
        var projectIds = new HashSet<Guid>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in activeBindings)
        {
            var endpoint = binding.Endpoint?.Trim();
            if (string.IsNullOrEmpty(endpoint) || endpoint == "*")
            {
                hasWildcard = true;
                break;
            }
            if (Guid.TryParse(endpoint, out var pid) && pid != Guid.Empty)
            {
                projectIds.Add(pid);
            }
            else
            {
                roots.Add(NormalizePathKey(endpoint));
            }
        }

        return new WorkSessionGrantSet(false, hasWildcard, projectIds, roots);
    }

    private async Task<(bool Authorized, string? ErrorCode, ProjectRecord? Project)> VerifySessionAuthorizationAsync(
        Guid? companionId,
        SessionRecord? session,
        CancellationToken cancellationToken)
    {
        ProjectRecord? project = null;
        if (session?.ProjectId is { } pid)
        {
            project = await sessionStore.GetProjectAnyStatusAsync(pid, cancellationToken).ConfigureAwait(false);
        }

        if (companionId is not { } cid || cid == Guid.Empty || companionDb is null)
            return (true, null, project);

        var grants = await LoadGrantsAsync(cid, cancellationToken).ConfigureAwait(false);
        if (grants is null)
            return (false, "companion_not_found", project);
        if (session is null)
            return (false, "session_not_found", project);
        if (!grants.AllowsProject(session.ProjectId, project?.RootPath))
            return (false, "work_session_unauthorized", project);

        return (true, null, project);
    }

    private async Task<RunRecord?> FindTargetRunAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid sessionId,
        Guid? runId,
        bool requireActive,
        CancellationToken cancellationToken)
    {
        await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Runs.AsNoTracking()
            .Where(r => r.TenantId == tenantId
                && r.WorkspaceId == workspaceId
                && r.SessionId == sessionId
                && r.ParentRunId == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ordered = rows.OrderByDescending(r => r.CreatedAt).ToList();
        if (runId is { } explicitRunId && explicitRunId != Guid.Empty)
        {
            var exact = ordered.FirstOrDefault(r => r.Id == explicitRunId);
            if (exact is null) return null;
            return !requireActive || !IsTerminalRunStatus(exact.Status) ? exact : null;
        }

        return requireActive
            ? ordered.FirstOrDefault(r => !IsTerminalRunStatus(r.Status))
            : ordered.FirstOrDefault();
    }

    private static bool IsTerminalRunStatus(string? status) =>
        status is "completed" or "failed" or "cancelled";

    private static async Task<IReadOnlyList<CompanionPendingApprovalItem>> LoadRunPendingApprovalsAsync(
        LifecycleDbContext db,
        Guid tenantId,
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                && a.WorkspaceId == workspaceId
                && a.RunId == runId
                && a.Status == "pending")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .Where(a => a.ExpiresAt > now)
            .OrderByDescending(a => a.CreatedAt)
            .Select(MapPendingApproval)
            .ToList();
    }

    private static CompanionPendingApprovalItem MapPendingApproval(ApprovalRequestRecord row) =>
        new(
            row.Id,
            row.ToolId,
            string.IsNullOrWhiteSpace(row.Summary) ? row.ArgumentsDigest : row.Summary,
            row.Risk,
            row.CreatedAt);

    private async Task<(string? Summary, CompanionWorkPlanProgress? Progress)> LoadPlanSummaryAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionPlans is null) return (null, null);
        try
        {
            var plan = await sessionPlans.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (plan.Revision <= 0 && plan.Progress.TotalSteps == 0)
                return (null, null);

            var done = plan.Progress.CompletedSteps;
            var total = plan.Progress.TotalSteps;
            var allSteps = plan.Phases.SelectMany(p => p.Steps).ToList();
            var currentStep = allSteps.FirstOrDefault(s => s.Status == "in_progress")?.Title
                ?? allSteps.FirstOrDefault(s => s.Status is "blocked" or "failed")?.Title
                ?? allSteps.FirstOrDefault(s => s.Status == "pending")?.Title;

            string summary;
            if (total > 0)
            {
                summary = string.IsNullOrWhiteSpace(currentStep)
                    ? $"{done}/{total}"
                    : $"{done}/{total} · {currentStep}";
            }
            else
            {
                summary = string.IsNullOrWhiteSpace(plan.Title) ? plan.Status : plan.Title;
            }

            return (summary, new CompanionWorkPlanProgress(done, total, currentStep, summary));
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<Dictionary<Guid, string>> LoadModeNamesAsync(
        IReadOnlyList<Guid> modeVersionIds,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (modeVersionIds.Count == 0 || agentConfigDb is null) return result;

        await PopulateModeNamesFromFactoryAsync(agentConfigDb, modeVersionIds, tenantId, workspaceId, result, cancellationToken)
            .ConfigureAwait(false);
        if (globalAgentConfig is not null && !ReferenceEquals(globalAgentConfig.Factory, agentConfigDb))
        {
            await PopulateModeNamesFromFactoryAsync(globalAgentConfig.Factory, modeVersionIds, tenantId, workspaceId, result, cancellationToken)
                .ConfigureAwait(false);
        }
        return result;
    }

    private static async Task PopulateModeNamesFromFactoryAsync(
        IDbContextFactory<AgentConfigurationDbContext> factory,
        IReadOnlyList<Guid> modeVersionIds,
        Guid tenantId,
        Guid workspaceId,
        Dictionary<Guid, string> target,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pairs = await (
            from version in db.ModeVersions.AsNoTracking()
            join mode in db.AgentModes.AsNoTracking() on version.AgentModeId equals mode.Id
            where modeVersionIds.Contains(version.Id)
                && version.TenantId == tenantId
                && version.WorkspaceId == workspaceId
            select new { version.Id, mode.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pair in pairs)
        {
            target[pair.Id] = pair.DisplayName;
        }
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())).Replace('\\', '/');
        }
        catch
        {
            return path.Trim().TrimEnd('/', '\\').Replace('\\', '/');
        }
    }

    private static string RunStatusWire(RunStatus status) => status switch
    {
        RunStatus.Planning => "planning",
        RunStatus.Understanding => "understanding",
        RunStatus.Executing => "executing",
        RunStatus.Replanning => "replanning",
        RunStatus.AwaitingApproval => "awaiting_approval",
        RunStatus.AwaitingDelegate => "awaiting_delegate",
        RunStatus.AwaitingUser => "awaiting_user",
        RunStatus.Paused => "paused",
        RunStatus.Reviewing => "reviewing",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => "planning"
    };
}
