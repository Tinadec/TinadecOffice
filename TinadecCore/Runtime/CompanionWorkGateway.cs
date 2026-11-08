using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.TinaChat;

namespace TinadecCore.Runtime;

/// <summary>
/// Implements the companion's management verbs in the composition root, because the Companion
/// module must not reference Lifecycle, Memory or DmaEA. Two boundaries are enforced here and
/// nowhere else:
///
/// 1. <b>Tenancy.</b> Every session, run and project is resolved through the tenant-scoped
///    stores; a foreign id is reported exactly like a missing one, so the companion cannot use a
///    refusal's wording to probe another tenant.
/// 2. <b>Admission, not a second engine.</b> Dispatching reuses the ordinary interaction path
///    (<see cref="IFullDuplexRunCoordinator"/>): it freezes the session's configuration, creates
///    the turn, writes the run row and wakes the engine. Calling <c>StartRunAsync</c> alone would
///    create a run with no turn and no frozen configuration, which the engine can only fail.
/// </summary>
public sealed class CompanionWorkGateway(
    ISessionLocator sessions,
    ProjectSessionStore sessionStore,
    IConversationStore conversations,
    ILifecycleManager lifecycle,
    IFullDuplexRunCoordinator coordinator,
    IDbContextFactory<LifecycleDbContext> runDb,
    ITenantContextAccessor tenant) : ICompanionWorkGateway
{
    private const int MaxSessions = 50;
    private const int DefaultSessionMessages = 20;
    private const int MaxSessionMessages = 50;

    public async Task<CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        // The store already orders by UpdatedAt descending and scopes by tenant/workspace; a
        // message append touches UpdatedAt, so this is "most recently active first".
        var rows = await sessionStore.ListSessionsAsync(null, LifecycleStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        var selected = rows.Take(MaxSessions).ToList();
        if (selected.Count == 0)
            return CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>.Ok([]);

        var ids = selected.Select(row => row.Id).ToList();
        await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // One tenant-scoped query for the whole page instead of one per session. Child runs are
        // excluded: the session's latest run is the user-facing one, not an execution inside it.
        var runs = await db.Runs.AsNoTracking()
            .Where(run => ids.Contains(run.SessionId)
                && run.TenantId == scope.TenantId
                && run.WorkspaceId == scope.WorkspaceId
                && run.ParentRunId == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var latest = runs
            .GroupBy(run => run.SessionId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(run => run.CreatedAt).First());

        var result = selected.Select(row => new CompanionWorkSession(
            row.Id,
            row.Title,
            row.ProjectId,
            latest.TryGetValue(row.Id, out var run)
                ? new CompanionWorkRunSummary(run.Id, run.Status, run.Summary)
                : null)).ToList();
        return CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>.Ok(result);
    }

    public async Task<CompanionWorkResult<CompanionWorkRun>> ReadRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await runDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // The lifecycle port answers a missing run with an empty planning state, so existence and
        // tenancy come from the row itself; the port then supplies the canonical run state.
        var row = await db.Runs.AsNoTracking()
            .SingleOrDefaultAsync(run => run.Id == runId
                && run.TenantId == scope.TenantId
                && run.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return CompanionWorkResult<CompanionWorkRun>.Fail(
                "run_not_found", "The run does not exist in this tenant/workspace.");

        var state = await lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        return CompanionWorkResult<CompanionWorkRun>.Ok(new CompanionWorkRun(
            runId,
            row.SessionId,
            RunStatusWire(state.Status),
            state.Summary ?? row.Summary,
            state.TerminalErrorCategory ?? row.TerminalErrorCategory));
    }

    public async Task<CompanionWorkResult<CompanionWorkDispatch>> DispatchAsync(
        Guid sessionId,
        string task,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        if (string.IsNullOrWhiteSpace(task))
            return CompanionWorkResult<CompanionWorkDispatch>.Fail("invalid_arguments", "task is required.");

        // The locator is the boundary authority; the store supplies the title the caller needs.
        // Both are tenant-scoped, and the explicit comparison keeps the boundary even if a future
        // ISessionLocator implementation stops scoping on its own.
        var reference = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (reference is null || reference.TenantId != scope.TenantId || reference.WorkspaceId != scope.WorkspaceId)
            return CompanionWorkResult<CompanionWorkDispatch>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");
        var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return CompanionWorkResult<CompanionWorkDispatch>.Fail(
                "session_not_found", "The session does not exist in this tenant/workspace.");

        var content = task.Trim();
        // The coordinator appends the user message itself, and only after its admission checks
        // (active-run limit, mode resolution, TinaChat lock). Writing it here first would leave an
        // orphan task message in the user's session whenever admission refuses — the text would sit
        // in the transcript with no run behind it.
        //
        // The key comes from the caller and is derived from facts that survive a retry (the
        // companion run id plus the dispatch itself). A fresh GUID here would let an at-least-once
        // effect retry append a second task message to the user's session.
        var clientMessageId = string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"companion-dispatch-{Guid.NewGuid():N}"
            : idempotencyKey.Trim();
        try
        {
            // The dispatched run inherits the target session's own permission policy. Forcing "ask"
            // would stall a full-access session behind a human click the user already decided not
            // to require; "deny" must stay deny.
            var submission = await coordinator.SubmitAsync(new FullDuplexInvocation(
                sessionId,
                content,
                clientMessageId,
                PermissionMode: session.PermissionMode,
                TargetRunId: null,
                ExpectedContextRevision: null), cancellationToken).ConfigureAwait(false);
            return CompanionWorkResult<CompanionWorkDispatch>.Ok(
                new CompanionWorkDispatch(sessionId, submission.RunId, session.Title));
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

    public async Task<CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
    {
        var projects = await sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        var sessions = await sessionStore.ListSessionsAsync(null, LifecycleStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        var counts = sessions
            .Where(session => session.ProjectId is not null)
            .GroupBy(session => session.ProjectId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var result = projects
            .Select(project => new CompanionWorkWorkspaceSummary(
                project.Id,
                project.Name,
                project.RootPath,
                counts.GetValueOrDefault(project.Id)))
            .ToList();
        return CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>.Ok(result);
    }

    public async Task<CompanionWorkResult<CompanionWorkSessionDetail>> ReadSessionAsync(
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

        // Capped by the caller, and capped again here: the model decides how much of the transcript
        // it needs, but this port never hands back a whole long conversation.
        var take = Math.Clamp(limit <= 0 ? DefaultSessionMessages : limit, 1, MaxSessionMessages);
        var messages = await conversations.ListMessagesAsync(sessionId, take, cancellationToken)
            .ConfigureAwait(false);
        var activeRuns = await lifecycle.CountActiveRunsAsync(sessionId.ToString(), cancellationToken)
            .ConfigureAwait(false);
        return CompanionWorkResult<CompanionWorkSessionDetail>.Ok(new CompanionWorkSessionDetail(
            sessionId,
            session.Title,
            session.ProjectId,
            session.PermissionMode ?? "default",
            activeRuns > 0,
            messages.Select(message => new CompanionWorkMessage(
                message.Role, message.Content, message.CreatedAt)).ToList()));
    }

    public async Task<CompanionWorkResult<CompanionWorkWorkspace>> CreateWorkspaceAsync(
        string name,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("invalid_arguments", "name is required.");
        if (string.IsNullOrWhiteSpace(path))
            return CompanionWorkResult<CompanionWorkWorkspace>.Fail("invalid_arguments", "path is required.");

        // Rootedness is a property of the caller's input: GetFullPath normalizes a relative path
        // against the process CWD, so checking it afterwards can never fail.
        //
        // IsPathFullyQualified, not IsPathRooted: on Windows the latter accepts drive-relative forms
        // like "C:tmp", which GetFullPath then resolves against the *process* current directory on
        // that drive — a directory the model never named.
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
            // Exactly the two calls BindSessionToWorkspaceAsync makes (directory, find by root,
            // create only when absent) minus the session rebind this verb does not have a session
            // for. A second registration path would drift from the approval-gated one.
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
            // A concurrent caller registered the same root between the lookup and the insert; the
            // row it wrote is the answer, not a second project.
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
