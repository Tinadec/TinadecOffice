using System.Text.Json;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// What a member is in its organization. The role decides the default edges of the graph a member
/// sits in: who it may contact without asking, which rooms it joins, what it may read and whether a
/// fact wakes it. It is not a permission to act — every verb is still checked where it executes.
/// </summary>
public static class OrganizationRoles
{
    /// <summary>The user who owns the session.</summary>
    public const string Human = "human";

    /// <summary>The system voice that posts engine facts (notices). Never an agent, never woken.</summary>
    public const string Host = "host";

    /// <summary>The conversation identity: one member per session, whichever instance currently speaks as it.</summary>
    public const string Conversation = "conversation";

    /// <summary>A standing governance role (reviewer, conflict arbiter…): session-scoped, woken by the facts it subscribes to.</summary>
    public const string Governance = "governance";

    /// <summary>A dispatched worker instance. Run-bound: online while its task runs, offline (history kept) after.</summary>
    public const string Executor = "executor";

    /// <summary>
    /// Roles that see the whole organization: every room, every report, except confidential messages
    /// they are not in the audience of. This is the "visibility flows downward" rule — governance and
    /// the conversation identity read the graph instead of waiting to be told.
    /// </summary>
    public static bool SeesWholeOrganization(string? role) =>
        role is Human or Conversation or Governance or Host;

    /// <summary>Roles that may post on the board. Everybody reads it; only these write, so it stays a notice board.</summary>
    public static bool MayPostOnBoard(string? role) =>
        role is Human or Conversation or Governance or Host;

    /// <summary>Roles everybody may contact without a request: the people whose job is to be reachable.</summary>
    public static bool IsUniversalContact(string? role) =>
        role is Human or Conversation or Governance or Host;
}

/// <summary>The session an organization belongs to, with the identity that owns it.</summary>
public sealed record OrganizationScope(Guid TenantId, Guid WorkspaceId, Guid PrincipalId, Guid SessionId);

/// <summary>
/// Makes an agent a member of its session's organization. Idempotent per instance (a resumed task
/// enrols again and gets the same member back) and per standing role.
/// </summary>
/// <param name="Role">One of <see cref="OrganizationRoles.Conversation"/>, <see cref="OrganizationRoles.Governance"/>, <see cref="OrganizationRoles.Executor"/>.</param>
/// <param name="AgentSlug">The agent template the member runs (for a standing role, its identity in the org).</param>
/// <param name="DisplayName">How others address it: the run handle for executors (<c>search#1</c>), the slug otherwise.</param>
/// <param name="AgentInstanceId">The instance that acts as this member; its tool calls resolve to the member through this.</param>
/// <param name="DispatcherInstanceId">The instance that dispatched an executor: whose plan room it joins, and its default contact.</param>
public sealed record OrganizationMemberEnrolment(
    OrganizationScope Scope,
    string Role,
    string AgentSlug,
    string DisplayName,
    Guid? RunId = null,
    Guid? AgentInstanceId = null,
    Guid? TaskId = null,
    Guid? DispatcherInstanceId = null,
    Guid? AgentDefinitionId = null,
    string? Description = null);

public sealed record OrganizationMemberRef(Guid OrganizationId, Guid ParticipantId, string Handle, string DisplayName, string Role);

/// <summary>
/// An engine fact delivered to a subscribed standing member. The host posts it into the member's
/// attention channel, which is what queues the member's turn — the same durable wake a message
/// leaves behind, coalesced and rate limited rather than dropped.
/// </summary>
public sealed record OrganizationNotice(
    OrganizationScope Scope,
    Guid RunId,
    string Topic,
    string AgentSlug,
    string Text,
    string? SubjectKind = null,
    string? SubjectId = null);

/// <summary>A compact open report, for a model context or a badge.</summary>
public sealed record OrganizationReportDigest(
    Guid ReportId,
    string AuthorDisplayName,
    string ReportKind,
    string Severity,
    string Finding,
    string? SubjectKind,
    string? SubjectId,
    string? ProposedVerb,
    DateTimeOffset CreatedAt);

/// <summary>One member as the topology and the UI see it.</summary>
public sealed record OrganizationMemberSummary(
    Guid ParticipantId,
    string Handle,
    string DisplayName,
    string Role,
    string Presence,
    string? AgentSlug,
    Guid? ParentParticipantId,
    Guid? RunId,
    Guid? AgentInstanceId);

/// <summary>
/// How far a member may look into run internals (todo E5): <c>down</c> (the default for governance
/// roles and the conversation identity — the whole session, "visibility flows downward") versus
/// <c>own</c> (only its own run's data: nothing for a standing member, its task's run for an
/// executor). Set by the user per member; it narrows <c>graph_view</c>, evidence recall and the
/// facts that wake a member — never chat rooms, which membership rules already govern.
/// </summary>
public sealed record OrganizationMemberVisibility(string Scope, Guid? CurrentRunId)
{
    public const string Down = "down";
    public const string Own = "own";

    public bool Restricted => string.Equals(Scope, Own, StringComparison.OrdinalIgnoreCase);

    public static OrganizationMemberVisibility? Of(string? scope, Guid? currentRunId) =>
        scope is null ? null : new OrganizationMemberVisibility(scope, currentRunId);
}

/// <summary>
/// The engine's seam into the session's TinaChat organization (architecture §9.1): a session is an
/// organization whose members — the user, the conversation identity, standing governance roles and
/// every dispatched worker — have contacts, rooms, direct chats and a board.
///
/// Optional by design: the run engine calls it best-effort and a failure never fails a run, because
/// communication is a view over the work, not the work (the task graph and the ledger stay the truth).
/// </summary>
public interface ISessionOrganization
{
    Task<OrganizationMemberRef> EnrolAsync(OrganizationMemberEnrolment enrolment, CancellationToken cancellationToken = default);

    /// <summary>Marks the run's executors offline once it is terminal. Their history stays readable.</summary>
    Task<int> SetRunOfflineAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Marks one executor offline when its task closes.</summary>
    Task SetInstanceOfflineAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>Delivers an engine fact to the named standing member. False when it has no member to reach (not enrolled, archived).</summary>
    Task<bool> NotifyAsync(OrganizationNotice notice, CancellationToken cancellationToken = default);

    /// <summary>An archived (or trashed) session keeps its organization readable but refuses every write.</summary>
    Task SetArchivedAsync(Guid sessionId, bool archived, CancellationToken cancellationToken = default);

    /// <summary>The open reports of the session's organization, most severe and newest first.</summary>
    Task<IReadOnlyList<OrganizationReportDigest>> ListOpenReportsAsync(Guid sessionId, int max, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganizationMemberSummary>> ListMembersAsync(Guid sessionId, int max, CancellationToken cancellationToken = default);

    /// <summary>The member an instance acts as, and its visibility (todo E5). Null when the instance is nobody in this session.</summary>
    Task<OrganizationMemberVisibility?> VisibilityForInstanceAsync(Guid sessionId, Guid agentInstanceId, CancellationToken cancellationToken = default);

    /// <summary>The participant's visibility (todo E5), for a standing member's own turn. Null when unrestricted.</summary>
    Task<OrganizationMemberVisibility?> VisibilityForParticipantAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A tool a standing member may call during its own turn. Declarations are handed to the model as
/// declaration-only functions; the runner executes each call through the toolbox, so the member acts
/// with exactly the rules an in-run tool call is held to.
/// </summary>
public sealed record TinaChatToolDeclaration(string Id, string Description, JsonElement InputSchema);

/// <summary>The member-bound tool surface a turn may use, already intersected with the member's frozen tool scope.</summary>
public interface ITinaChatMemberToolbox
{
    IReadOnlyList<TinaChatToolDeclaration> Tools { get; }

    /// <param name="callKey">Stable per call within the turn; reused as the idempotency key of any write.</param>
    Task<TinaChatToolOutcome> ExecuteAsync(string toolId, JsonElement? arguments, string callKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// One turn of a standing member, in its own context: its role prompt, what woke it, and its tools.
/// It is not a step of anybody's run — the executor that caused the fact never waits for it.
/// </summary>
public sealed record TinaChatMemberTurn(
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId,
    Guid SessionId,
    Guid OrganizationId,
    Guid ParticipantId,
    string DisplayName,
    string AgentSlug,
    string Briefing,
    Guid? RunId,
    string TurnKey);

public sealed record TinaChatMemberTurnOutcome(int Rounds, int ToolCalls, string? FinalText, IReadOnlyList<string> ToolsCalled);

/// <summary>Runs a standing member's turn with its frozen model plan and prompt. Implemented where the model plumbing lives.</summary>
public interface ITinaChatMemberTurnRunner
{
    Task<TinaChatMemberTurnOutcome> RunAsync(TinaChatMemberTurn turn, ITinaChatMemberToolbox toolbox, CancellationToken cancellationToken = default);
}

/// <summary>
/// Delivers a TinaChat message addressed to an active execution member at its next run context
/// boundary. The implementation lives in Runtime so TinaChat remains independent of DmaEA.
/// </summary>
public interface IExecutorMessageWakeSink
{
    Task<bool> DeliverAsync(
        Guid sessionId,
        Guid runId,
        Guid agentInstanceId,
        string briefing,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A governance fact raised outside the run engine (the tool dispatcher sees a lease conflict or
/// parks a call on approval). The sink resolves the run's declared subscribers from its frozen
/// configuration and notifies each through the organization — the same path engine-side facts take.
/// </summary>
public sealed record GovernanceTopicSignal(
    Guid SessionId,
    Guid RunId,
    string Topic,
    string Summary,
    string? SubjectKind = null,
    string? SubjectId = null);

public interface IGovernanceTopicSink
{
    Task RaiseAsync(GovernanceTopicSignal signal, CancellationToken cancellationToken = default);
}
