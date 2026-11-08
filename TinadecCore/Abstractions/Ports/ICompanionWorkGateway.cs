namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The complete set of management verbs a companion may use against the user's work sessions:
/// list them, read one run, dispatch a task into one, and create/reuse a project workspace.
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
    /// Up to 50 active sessions in the current tenant/workspace, most recently active first,
    /// each with its newest user-facing run (null when the session has never run).
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkSession>>> ListSessionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One run's state. A run outside the current tenant/workspace is reported exactly like a run
    /// that does not exist.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkRun>> ReadRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

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
    /// Active project workspaces in this tenant/workspace with their root paths and active-session counts.
    /// A companion checks this before registering a root, and to see which projects the user works in.
    /// </summary>
    Task<CompanionWorkResult<IReadOnlyList<CompanionWorkWorkspaceSummary>>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default);

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
    /// Registers a project workspace. <paramref name="path"/> must be absolute; an existing
    /// directory is reused, and an existing project row for the same root is returned as-is.
    /// </summary>
    Task<CompanionWorkResult<CompanionWorkWorkspace>> CreateWorkspaceAsync(
        string name,
        string path,
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

public sealed record CompanionWorkSession(
    Guid Id,
    string Title,
    Guid? ProjectId,
    CompanionWorkRunSummary? LatestRun);

public sealed record CompanionWorkRunSummary(Guid Id, string Status, string? Summary);

public sealed record CompanionWorkRun(
    Guid Id,
    Guid SessionId,
    string Status,
    string? Summary,
    string? ErrorCategory);

public sealed record CompanionWorkDispatch(Guid SessionId, Guid RunId, string SessionTitle);

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
    IReadOnlyList<CompanionWorkMessage> RecentMessages);

public sealed record CompanionWorkMessage(string Role, string Content, DateTimeOffset CreatedAt);
