using System.Text.Json.Serialization;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

internal sealed class FullDuplexCheckpointV1
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public Guid SessionId { get; init; }
    public Guid TurnId { get; init; }
    public Guid TriggerMessageId { get; init; }
    public string UserGoal { get; set; } = string.Empty;
    public string Phase { get; set; } = "planning";
    public string InteractionKind { get; set; } = "new_task";
    public Guid? TargetRunId { get; set; }
    public long CheckpointRevision { get; set; }
    public long ContextRevision { get; set; }
    public int PlanRevision { get; set; }
    public Guid? MeetingAgentId { get; set; }
    public Guid? PlannerAgentId { get; set; }
    public Guid? SupervisorAgentId { get; set; }

    /// <summary>
    /// Parallel instance pool carrying the conversation identity (DmaEA graph
    /// orchestration). The conversation-identity instance IS the main instance:
    /// user-facing output is emitted only through it, pool members execute
    /// envelope-bounded sub-work while sharing context_revision and tools.
    /// </summary>
    public Guid? ConversationIdentityInstanceId { get; set; }
    public List<Guid> InstancePoolIds { get; set; } = [];
    public Guid? MainInstanceId { get; set; }
    public List<DurableTaskNode> Tasks { get; set; } = [];

    /// <summary>
    /// Persistent and ephemeral teammates spawned in this run (#33 / #36).
    /// Settled <c>teammate</c> entries park in <c>waiting</c> state on the run's shared
    /// board and retain bounded context + isolated worktree across wakes.
    /// </summary>
    public List<DurableTeammateState> Teammates { get; set; } = [];

    /// <summary>
    /// Low-latency shared run board messages exchanged via <c>team_message</c>,
    /// MCP loopback tools, review feedback, or worktree integration diagnostics.
    /// </summary>
    public List<DurableTeamBoardMessage> TeamBoard { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long NextTeamBoardSeq { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsExploreFirstCoordinator { get; set; }

    public int SupervisionRound { get; set; }
    public string? SupervisionDecision { get; set; }
    public List<string> SupervisionReasons { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserAcceptedIncompleteOutcome { get; set; }
    public string? MeetingResponse { get; set; }
    public List<RecommendedCapability> RecommendedCapabilities { get; set; } = [];
    public ModelUsage? ModelUsage { get; set; }
    public Guid? AssistantMessageId { get; set; }

    /// <summary>
    /// Number of consecutive full model calls that exhausted their own short
    /// retry/fallback chain without durable phase progress.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TransientModelRetryCount { get; set; }

    /// <summary>Durable cooling-off deadline after a transient model outage.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ModelRetryNotBefore { get; set; }

    /// <summary>The last retryable model failure category, retained for audit events.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastTransientModelErrorCategory { get; set; }

    /// <summary>
    /// Retained strictly for checkpoint deserialization and contract compatibility;
    /// lane execution branches have been removed.
    /// </summary>
    public List<DurableLane> Lanes { get; set; } = [];

    /// <summary>Retained for pre-existing checkpoint compatibility.</summary>
    public Dictionary<string, Guid> LanePlannerIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Count of run_directives rows this run has drained. Resume safety comes
    /// from the pending-status filter on the drain query, not from this cursor.
    /// </summary>
    public long DirectiveCursor { get; set; }

    /// <summary>
    /// Set in the same CAS save as the first planning tick once the
    /// orchestration.mode_tier_decided event has been appended.
    /// </summary>
    public bool GraphTierAnnounced { get; set; }

    public bool SpacePreparationComplete { get; set; }
    public string? SpaceWorktreePath { get; set; }
    public List<SpaceSpecDocument> SpaceSpecDocuments { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CorrectionTraceState? Correction { get; set; }

    /// <summary>
    /// Set when a graph-tier approval expiry parks the run on the user-review escalation.
    /// </summary>
    public bool AwaitingApprovalExpiryReview { get; set; }
}

/// <summary>
/// Durable state of a spawned teammate in <c>Team</c> (<c>teammate</c>) or <c>Solo</c> (<c>ephemeral</c>) mode (#33 / #36).
/// </summary>
internal sealed class DurableTeammateState
{
    public string Handle { get; set; } = string.Empty;
    public string AgentSlug { get; set; } = string.Empty;

    /// <summary><c>teammate</c> (persistent, re-wakable) or <c>ephemeral</c> (single-shot).</summary>
    public string Mode { get; set; } = "teammate";

    /// <summary><c>working</c>, <c>waiting</c>, or <c>terminated</c>.</summary>
    public string Status { get; set; } = "working";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorktreePath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BranchName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? WriteScope { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeBadge { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ProviderInstanceId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RelayDepth { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long LastWakeSeq { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranscriptSummary { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastOutcomeSummary { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AgentInstanceId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CurrentTaskId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelOverride { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long LastDeliveredBoardSeq { get; set; }
}

/// <summary>
/// One message posted on the run's shared low-latency board (#33).
/// </summary>
internal sealed class DurableTeamBoardMessage
{
    public long Seq { get; init; }
    public string Sender { get; init; } = string.Empty;
    public string Target { get; init; } = "all";
    public string Kind { get; init; } = "update";
    public string Content { get; init; } = string.Empty;
    public bool Wake { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A skill/capability recommendation produced by the operational capability
/// advisor and injected into subsequent planning rounds.
/// </summary>
internal sealed record RecommendedCapability(string Skill, string Reason, string Confidence);

internal sealed class DurableTaskNode
{
    public bool IsSpacePreparation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCoordinatorTask { get; set; }
    public Guid TaskId { get; init; }
    public string TaskKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; init; }
    public List<string> SuccessCriteria { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
    public List<string> RequiredCapabilities { get; init; } = [];
    public List<string> RequiredTools { get; init; } = [];
    public int Priority { get; init; }
    public string Risk { get; init; } = "medium";
    public string Status { get; set; } = "pending";
    public int Attempt { get; set; }
    public int ToolRounds { get; set; }
    public List<WorkerToolTurn> ToolTurns { get; set; } = [];

    /// <summary>Tokens this task has consumed across all of its model turns.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TokensUsed { get; set; }

    /// <summary>Trailing consecutive empty model responses (no text, no calls).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConsecutiveEmptyResponses { get; set; }

    /// <summary>Set once a budget or loop-guard trigger stopped tool use.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CloseoutRequested { get; set; }

    /// <summary>RunErrorTaxonomy category of the stop that requested the close-out.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CloseoutCategory { get; set; }

    /// <summary>Human-readable stop reason with the concrete budget numbers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CloseoutReason { get; set; }

    /// <summary>True when the stop came from the absolute call fuse.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CloseoutHardCeiling { get; set; }

    public string? PendingToolExecutionId { get; set; }
    public string? PendingToolApprovalId { get; set; }
    public Guid? WorkerAgentId { get; set; }
    public string? WorkerAgentSlug { get; set; }
    public Guid? WorkerAgentDefinitionId { get; set; }
    public Guid? WorkerAgentVersionId { get; set; }
    public string? WorkerAgentVersionHash { get; set; }
    public string? WorkerAssignmentReason { get; set; }

    /// <summary>The executor the coordinator named for this task.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedAgent { get; set; }

    /// <summary>Run-scoped instance handle assigned with the worker.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkerHandle { get; set; }

    /// <summary>Persistent or ephemeral teammate handle bound via <c>team_spawn</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TeammateHandle { get; set; }

    /// <summary>Teammate mode (<c>teammate</c> or <c>ephemeral</c>) when spawned via <c>team_spawn</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TeammateMode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ProviderInstanceOverride { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelOverride { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelIdOverride { get; set; }

    /// <summary>The task_dispatch / team_spawn id this sub-task was created from.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DispatchId { get; set; }

    /// <summary>Paths the dispatcher declared this task will write (<c>write_scope</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? WriteScope { get; set; }

    /// <summary>The task whose tool loop dispatched this sub-task.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DispatchedByTaskId { get; set; }

    /// <summary>True when this task requested its own isolated git worktree.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Worktree { get; set; }

    /// <summary>Materialized task-scoped git worktree root path once provisioned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskWorktreePath { get; set; }

    /// <summary>Branch checked out in <see cref="TaskWorktreeBranch"/> when task-scoped isolation is active.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskWorktreeBranch { get; set; }

    /// <summary>Sub-tasks this task is parked on (status <c>waiting</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<Guid> AwaitingTaskIds { get; set; } = [];

    /// <summary>Teammate handles this task is parked on via <c>team_wait</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AwaitingTeammateHandles { get; set; }

    /// <summary>Board sequence watermark recorded when <c>team_wait</c> started.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long AwaitingBoardSeq { get; set; }

    /// <summary>Wake mode for <c>team_wait</c>: <c>settled</c>, <c>message</c>, or <c>any</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TeamWaitUntil { get; set; }

    /// <summary>Deadline after which a parked <c>team_wait</c> resolves with a snapshot.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? TeamWaitDeadline { get; set; }

    /// <summary>Sub-tasks whose results task_wait/team_wait already handed to this task.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<Guid> ReportedTaskIds { get; set; } = [];

    /// <summary>The agent's own working plan, as last sent through plan_update or team_plan.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TaskPlanStep>? Plan { get; set; }

    /// <summary>Rounds a hard insert cut off (todo D2), oldest first.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WorkerInterruption>? Interruptions { get; set; }

    public long InputContextRevision { get; set; }
    public string? ResultStatus { get; set; }
    public string? ResultSummary { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<StaleTaskEvidence> StaleEvidence { get; set; } = [];
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Retained for API/UI contract compatibility (defaults to implicit "main").</summary>
    public string? LaneKey { get; set; }

    /// <summary>Retained for pre-existing checkpoint serialization compatibility.</summary>
    public List<LaneWait> Waits { get; set; } = [];

    /// <summary>Per-criterion verdicts produced by worker outcome protocol or supervision.</summary>
    public List<CriterionVerdict> CriteriaVerdicts { get; set; } = [];
}

internal sealed record SupervisionReviewBrief(
    int TotalTasks,
    int CompletedTasks,
    int BlockedTasks,
    int FailedTasks,
    int PendingTasks,
    int UnmetCriteria,
    int RevisionRound,
    int MaxRevisionRounds,
    bool SupervisorRequired,
    bool SupervisorAvailable);

/// <summary>One step of an agent's plan_update plan; status is pending, in_progress or completed.</summary>
public sealed record TaskPlanStep(
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("status")] string Status);

internal sealed record StaleTaskEvidence(
    long InputContextRevision,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RecordedAt);

internal sealed class RunAwaitingExternalDecisionException : Exception;

internal sealed class WorkerUnavailableException(string message) : Exception(message);

internal sealed class MeetingResponseUnavailableException(string category, string message) : Exception(message)
{
    public string Category { get; } = category;
}
