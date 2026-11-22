namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The complete set of management verbs a companion may use against the user's work sessions:
/// list modes/workspaces/sessions/approvals, create sessions, read runs/sessions, dispatch tasks,
/// steer or cancel runs, ask read-only side questions, and create/reuse project workspaces.
///
/// The port lives here because the Companion module owns its tool surface but may not reference
/// Lifecycle, Memory, DmaEA or the Runtime composition root. There is deliberately no verb that
/// reads or writes code, runs a shell, touches Git, or reaches MCP: a companion is a manager of
/// sessions, never an executor inside them. Dispatching is a management action (no tool approval);
/// the agents inside the target session keep their own approval gates.
/// </summary>
public interface ICompanionWorkGateway
{
    /// <summary>
    /// Published Agent Modes (<c>Solo</c>, <c>Plan</c>, <c>Team</c>, <c>Spec</c>, <c>Correction</c>, etc.)
    /// available for creating or dispatching into work sessions.
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkMode>>> ListModesAsync(
        Guid? companionId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompanionWorkResult<IReadOnlyList<CompanionWorkMode>>.Ok([]));

    /// <summary>
    /// Up to 50 active sessions in the current tenant/workspace, most recently active first,
    /// each with its newest user-facing run (null when the session has never run).
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>> ListSessionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Companion-scoped active sessions in authorized workspaces (optionally filtered by <paramref name="projectId"/>).
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>> ListSessionsAsync(
        Guid? companionId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default) =>
        ListSessionsAsync(cancellationToken);

    /// <summary>
    /// Creates a new work session in an authorized project workspace (or unbound flat session if allowed),
    /// optionally pinned to a formal <paramref name="modeVersionId"/>.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkSession>> CreateSessionAsync(
        Guid? companionId,
        Guid? projectId,
        string? title,
        Guid? modeVersionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompanionWorkResult<CompanionWorkSession>.Fail(
            "not_supported", "Session creation is not supported by this gateway implementation."));

    /// <summary>
    /// Creates a new work session and records the originating Companion conversation for backflow wakeups.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkSession>> CreateSessionAsync(
        Guid? companionId,
        Guid? conversationId,
        Guid? projectId,
        string? title,
        Guid? modeVersionId,
        CancellationToken cancellationToken = default) =>
        CreateSessionAsync(companionId, projectId, title, modeVersionId, cancellationToken);

    /// <summary>
    /// One run's state. A run outside the current tenant/workspace is reported exactly like a run
    /// that does not exist.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkRun>> ReadRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Companion-scoped run state check enforcing <c>work_session</c> connector authorization.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkRun>> ReadRunAsync(
        Guid? companionId,
        Guid runId,
        CancellationToken cancellationToken = default) =>
        ReadRunAsync(runId, cancellationToken);

    /// <summary>
    /// Writes <paramref name="task"/> as a user message into the session, then starts a run for it.
    /// The message is stored before the run row. A session outside the current tenant/workspace is
    /// reported exactly like a missing session.
    ///
    /// <paramref name="idempotencyKey"/> is what makes a retried turn safe: the companion effect
    /// queue is at-least-once, so a turn replayed after a failed model call must not append a
    /// second task message. Callers derive it from facts that survive the retry (run id plus the
    /// dispatch itself), never from a fresh GUID.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkDispatch>> DispatchAsync(
        Guid sessionId,
        string task,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a task into an existing session with optional formal mode version override
    /// and isolated Git worktree execution flag.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkDispatch>> DispatchAsync(
        Guid sessionId,
        string task,
        string idempotencyKey,
        Guid? modeVersionId,
        bool? worktree,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(sessionId, task, idempotencyKey, cancellationToken);

    /// <summary>
    /// Companion-scoped dispatch enforcing <c>work_session</c> connector authorization and recording backflow tracking.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkDispatch>> DispatchAsync(
        Guid? companionId,
        Guid sessionId,
        string task,
        string idempotencyKey,
        Guid? modeVersionId = null,
        bool? worktree = null,
        Guid? conversationId = null,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(sessionId, task, idempotencyKey, modeVersionId, worktree, cancellationToken);

    /// <summary>
    /// Steers an in-flight run (or submits follow-up guidance into the session when idle) inside an authorized workspace.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkSteer>> SteerRunAsync(
        Guid? companionId,
        Guid sessionId,
        Guid? runId,
        string guidance,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompanionWorkResult<CompanionWorkSteer>.Fail(
            "not_supported", "Run steering is not supported by this gateway implementation."));

    /// <summary>
    /// Steers an in-flight run and records the originating Companion conversation for backflow wakeups.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkSteer>> SteerRunAsync(
        Guid? companionId,
        Guid? conversationId,
        Guid sessionId,
        Guid? runId,
        string guidance,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        SteerRunAsync(companionId, sessionId, runId, guidance, idempotencyKey, cancellationToken);

    /// <summary>
    /// Cancels/interrupts an active run inside an authorized workspace.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkCancel>> CancelRunAsync(
        Guid? companionId,
        Guid sessionId,
        Guid? runId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompanionWorkResult<CompanionWorkCancel>.Fail(
            "not_supported", "Run cancellation is not supported by this gateway implementation."));

    /// <summary>
    /// Answers a read-only side question about a session/run without mutating session state or interrupting an active run.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkSideAnswer>> SideQuestionAsync(
        Guid? companionId,
        Guid sessionId,
        Guid? runId,
        string question,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompanionWorkResult<CompanionWorkSideAnswer>.Fail(
            "not_supported", "Side questions are not supported by this gateway implementation."));

    /// <summary>
    /// Lists pending tool approvals across authorized workspaces (or for a single <paramref name="sessionId"/>).
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>> ListApprovalsAsync(
        Guid? companionId,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CompanionWorkResult<IReadOnlyList<CompanionWorkApproval>>.Ok([]));

    /// <summary>
    /// Active project workspaces in this tenant/workspace with their root paths and active-session counts.
    /// A companion checks this before registering a root, and to see which projects the user works in.
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Companion-scoped active project workspaces filtered by <c>work_session</c> connector grants.
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>> ListWorkspacesAsync(
        Guid? companionId,
        CancellationToken cancellationToken = default) =>
        ListWorkspacesAsync(cancellationToken);

    /// <summary>
    /// One session in depth: its policy, whether a run is active, and a capped transcript of recent
    /// turns. The transcript is what lets a companion notice the same work is already under way
    /// before it dispatches a duplicate.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkSessionDetail>> ReadSessionAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Companion-scoped session detail enforcing <c>work_session</c> connector authorization.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkSessionDetail>> ReadSessionAsync(
        Guid? companionId,
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default) =>
        ReadSessionAsync(sessionId, limit, cancellationToken);

    /// <summary>
    /// Registers a project workspace. <paramref name="path"/> must be absolute; an existing
    /// directory is reused, and an existing project row for the same root is returned as-is.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkWorkspace>> CreateWorkspaceAsync(
        string name,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Companion-scoped workspace creation (requires wildcard <c>"*"</c> <c>work_session</c> grant).
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkWorkspace>> CreateWorkspaceAsync(
        Guid? companionId,
        string name,
        string path,
        CancellationToken cancellationToken = default) =>
        CreateWorkspaceAsync(name, path, cancellationToken);

    /// <summary>
    /// Sends one message to another companion over TinaChat — the durable channel the three roles
    /// already share. This is how an autonomous partner asks the buddy to arrange work, and how a
    /// buddy answers.
    ///
    /// It is a message between two agents, never an instruction to execute: the receiver decides
    /// what to do with it on its own turn, under its own tools and authorizations. The send is made
    /// as the sender's own participant, so it is a real identity and not a borrowed one.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkRelay>> RelayToCompanionAsync(
        Guid fromCompanionId,
        Guid toCompanionId,
        string content,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One gateway answer. A refusal is data, not an exception: the tool loop must be able to hand
/// every outcome back to the model in the same structured shape.
/// </summary>
public sealed record CompanionWorkResult<T>(T? Value, string? Error, string? Message)
{
    public bool IsSuccess => Error is null;

    public static CompanionWorkResult<T> Ok(T value) => new(value, null, null);

    public static CompanionWorkResult<T> Fail(string error, string? message = null) => new(default, error, message);
}

public sealed record CompanionWorkMode(
    Guid ModeVersionId,
    string Slug,
    string DisplayName,
    string? Description = null,
    string? OrchestrationTier = null);

public sealed record CompanionWorkPlanProgress(
    int Done,
    int Total,
    string? CurrentStep = null,
    string? Summary = null);

public sealed record CompanionWorkSession(
    Guid Id,
    string Title,
    Guid? ProjectId,
    CompanionWorkRunSummary? LatestRun,
    string? ProjectName = null,
    string? ModeName = null,
    string? PlanSummary = null,
    IReadOnlyList<CompanionPendingApprovalItem>? PendingApprovals = null,
    string? ViewMode = null,
    DateTimeOffset? UpdatedAt = null,
    CompanionWorkPlanProgress? PlanProgress = null);

public sealed record CompanionWorkRunSummary(Guid Id, string Status, string? Summary);

public sealed record CompanionWorkRun(
    Guid Id,
    Guid SessionId,
    string Status,
    string? Summary,
    string? ErrorCategory,
    string? ProjectName = null,
    string? ModeName = null,
    string? PlanSummary = null,
    IReadOnlyList<CompanionPendingApprovalItem>? PendingApprovals = null);

public sealed record CompanionWorkDispatch(
    Guid SessionId,
    Guid RunId,
    string SessionTitle,
    Guid? ProjectId = null,
    string? ProjectName = null,
    string? ModeName = null,
    string? PlanSummary = null,
    IReadOnlyList<CompanionPendingApprovalItem>? PendingApprovals = null);

public sealed record CompanionWorkSteer(
    Guid SessionId,
    Guid RunId,
    string Status,
    string? ModeName = null,
    string? PlanSummary = null);

public sealed record CompanionWorkCancel(
    Guid SessionId,
    Guid RunId,
    string Status);

public sealed record CompanionWorkSideAnswer(
    Guid SessionId,
    Guid? RunId,
    string Answer,
    string? RunStatus = null,
    string? PlanSummary = null);

public sealed record CompanionWorkApproval(
    Guid ApprovalId,
    Guid SessionId,
    string SessionTitle,
    Guid RunId,
    string ToolId,
    string? Summary,
    string? RiskLevel,
    DateTimeOffset CreatedAt,
    Guid? ProjectId = null,
    string? ProjectName = null);

public sealed record CompanionWorkWorkspace(Guid ProjectId, string Name, string Path);

public sealed record CompanionWorkWorkspaceSummary(Guid ProjectId, string Name, string RootPath, int ActiveSessions);

/// <summary>
/// One session's policy plus a capped slice of its transcript. Messages are the raw conversation rows
/// the session already owns; this port never writes them.
/// </summary>
public sealed record CompanionWorkSessionDetail(
    Guid SessionId,
    string Title,
    Guid? ProjectId,
    string PermissionMode,
    bool HasActiveRun,
    IReadOnlyList<CompanionWorkMessage> RecentMessages,
    string? ProjectName = null,
    string? ModeName = null,
    string? PlanSummary = null,
    IReadOnlyList<CompanionPendingApprovalItem>? PendingApprovals = null,
    CompanionWorkRunSummary? LatestRun = null);

public sealed record CompanionWorkMessage(string Role, string Content, DateTimeOffset CreatedAt);

/// <summary>One relayed message: the conversation it landed in and the message row itself.</summary>
public sealed record CompanionWorkRelay(Guid ConversationId, Guid MessageId, long Sequence);
