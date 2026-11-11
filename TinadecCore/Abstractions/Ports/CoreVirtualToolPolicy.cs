namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Single source of truth for Core-owned virtual tools: tools that never reach a
/// TinadecTools child process and are executed by Core itself. The projectless
/// (free-conversation) contract needs the same identity in Lifecycle (approval
/// admission), Tools (dispatch/manifest freezing), and the HTTP layer, so the
/// literal lives here instead of being re-typed per module.
/// </summary>
public static class CoreVirtualToolPolicy
{
    public const string CreateWorkspaceToolId = "create_workspace";

    /// <summary>
    /// Lets the conversation identity hand a sub-task to the ordinary dispatch path.
    ///
    /// A solo master works in ONE tool loop, so it needs a way to say "dispatch this"
    /// from INSIDE that loop: emitting a task array is the planner's mechanism and the
    /// master never runs the planner. This tool is that mechanism, and it is the reason
    /// "dispatch aggressively, as the model judges" is something the model can actually
    /// carry out rather than a line of prompt prose.
    /// </summary>
    public const string TaskDispatchToolId = "task_dispatch";

    /// <summary>
    /// Lets the dispatcher of sub-tasks wait for them and read their results inside the same loop.
    ///
    /// Why it exists: task_dispatch only queues; without a wait the master finished its own loop
    /// before any sub-task ran, so it could never use a result - the run's final answer was the only
    /// place results met. Executed by the run engine, which owns the task graph: the call parks the
    /// caller's task until the awaited sub-tasks are terminal, then returns their outcomes.
    /// </summary>
    public const string TaskWaitToolId = "task_wait";

    /// <summary>
    /// Lets an agent working inside one tool loop write its step-by-step plan down and keep it current.
    ///
    /// Why it exists: graph tiers plan through the planner's task array, but a solo master or an
    /// executor on a long task works in a single loop with nowhere to put a plan - it either kept the
    /// plan in its head (and drifted) or narrated it in prose nobody could track. Executed by the run
    /// engine, which stores the plan on the task node and emits plan.updated for the UI.
    /// </summary>
    public const string PlanUpdateToolId = "plan_update";
    public const string SpecProposeToolId = "spec_propose";
    public static bool IsSpecPropose(string? toolId) => string.Equals(toolId, SpecProposeToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Host-owned causal trace for the submitted symptom. Only a mode with the
    /// structured investigation runtime policy receives this declaration.
    /// </summary>
    public const string CorrectionTraceToolId = "update_correction_trace";
    public static bool IsCorrectionTrace(string? toolId) => string.Equals(toolId, CorrectionTraceToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Upper bound on plan_update steps.
    ///
    /// <para>
    /// It exists to reject a step list that is really a backlog, not to cap how big a legitimate
    /// plan may be: a migration, a spec-driven build or a cross-module change routinely has more
    /// than twenty steps, and a limit that small makes a normal task fail its own plan call. The
    /// real failure modes are caught elsewhere — one in_progress step at a time, the empty-plan
    /// refusal, and the loop guard. Keep this and the spec checklist ceiling in step.
    /// </para>
    /// </summary>
    public const int PlanUpdateMaxSteps = 64;

    /// <summary>
    /// Reads a page of a file the user attached to a message in this session.
    ///
    /// Why it exists: the context builder can only quote a bounded excerpt of every attachment per
    /// turn, so the rest of a log, a CSV or a long config reaches the model as a filename and a byte
    /// count. A model that needs line 4000 of a crash log had to ask the user to paste it. This tool
    /// hands the paging to the model, which is the only party that knows how much it needs.
    /// </summary>
    public const string ReadAttachmentToolId = "read_attachment";

    /// <summary>
    /// Loads a skill body by name from the run's frozen skill catalog. The index names each skill and
    /// its description only; this tool is the progressive-disclosure half, and it also works for
    /// built-in skills that have no readable file on disk.
    /// </summary>
    public const string LoadSkillToolId = "load_skill";
    public static bool IsLoadSkill(string? toolId) => string.Equals(toolId, LoadSkillToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The workbench-configuration group: inspect the live agent/mode/skill/MCP/market
    /// configuration, then apply a governed change to it. They run inside Core (no child
    /// process), the read-only inspection needs no approval, and every write is
    /// high-risk and approval-gated. The ids are the model-facing contract; removing or
    /// renaming one is a breaking change for the seed pack's tool scopes.
    /// </summary>
    public const string TinadecInspectToolId = "tinadec_inspect";
    public const string TinadecAgentApplyToolId = "tinadec_agent_apply";
    public const string TinadecModeApplyToolId = "tinadec_mode_apply";
    public const string TinadecModePublishToolId = "tinadec_mode_publish";
    public const string TinadecSkillApplyToolId = "tinadec_skill_apply";
    public const string TinadecMcpApplyToolId = "tinadec_mcp_apply";

    public static readonly IReadOnlyList<string> TinadecConfigToolIds =
    [
        TinadecInspectToolId, TinadecAgentApplyToolId, TinadecModeApplyToolId,
        TinadecModePublishToolId, TinadecSkillApplyToolId, TinadecMcpApplyToolId,
    ];

    public static bool IsTinadecConfig(string? toolId) =>
        toolId is not null && TinadecConfigToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read-only inspection: the one tool in the group that never needs approval.</summary>
    public static bool IsTinadecInspect(string? toolId) =>
        string.Equals(toolId, TinadecInspectToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A nullable project id cannot travel on the wire, so a projectless call
    /// carries <see cref="Guid.Empty"/> as its project sentinel.
    /// </summary>
    public static readonly Guid ProjectlessProjectId = Guid.Empty;

    public static bool IsCreateWorkspace(string? toolId) =>
        string.Equals(toolId, CreateWorkspaceToolId, StringComparison.OrdinalIgnoreCase);

    public static bool IsTaskDispatch(string? toolId) =>
        string.Equals(toolId, TaskDispatchToolId, StringComparison.OrdinalIgnoreCase);

    public static bool IsTaskWait(string? toolId) =>
        string.Equals(toolId, TaskWaitToolId, StringComparison.OrdinalIgnoreCase);

    public static bool IsPlanUpdate(string? toolId) =>
        string.Equals(toolId, PlanUpdateToolId, StringComparison.OrdinalIgnoreCase);

    public static bool IsReadAttachment(string? toolId) =>
        string.Equals(toolId, ReadAttachmentToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The nine tools that let a governed run act as a named member of a TinaChat conversation. They
    /// never reach a tool child process: Core executes them against its own communication state, so a
    /// declared surface, not an approval, is what authorizes them.
    ///
    /// Why they exist: without them "talk to your colleagues in the group" is prompt prose the
    /// model cannot carry out. The wake queue can already hand an agent a turn, but the agent had
    /// no way to answer, to look somebody up, or to file a brief of its own. Read/accept/handoff
    /// are in the same list because the conversation role that may decide a brief is held by agents,
    /// not by a human clicking a button in an observation panel.
    /// </summary>
    public static readonly IReadOnlyList<string> TinaChatToolIds =
    [
        "tina_chat_bind", "tina_chat_search_people", "tina_chat_list_rooms",
        "tina_chat_read_inbox", "tina_chat_send", "tina_chat_propose_intent",
        "tina_chat_list_intents", "tina_chat_decide_intent", "tina_chat_execute_intent",
    ];

    public static bool IsTinaChat(string? toolId) =>
        toolId is not null && TinaChatToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tools a member uses inside its session's organization (architecture §9.1): see who is here,
    /// read a room by cursor, talk to a contact or a room, file a report, decide one, ask for a contact,
    /// open a room. Executed by the communication module against the member the calling instance was
    /// enrolled as, so a worker speaks as its own <c>search#1</c> and never as the session.
    ///
    /// Separate from <see cref="TinaChatToolIds"/> on purpose: those address the workspace-wide chat
    /// through an explicit bind; these address the session's own organization, where identity is the
    /// instance's membership and needs no bind.
    /// </summary>
    public static readonly IReadOnlyList<string> OrganizationToolIds =
    [
        "org_directory", "org_read", "org_send", "org_report", "org_decide_report", "org_execute_report", "org_contact", "org_room",
    ];

    public static bool IsOrganization(string? toolId) =>
        toolId is not null && OrganizationToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the session as a graph — runs, their tasks and workers, the resources they hold, the
    /// organization's members. Read-only; what a governance member reasons about is what the user's
    /// topology view shows, because both read the same projection.
    /// </summary>
    public const string GraphViewToolId = "graph_view";

    /// <summary>
    /// Searches the session's evidence archive (task results, reports, member conclusions and context
    /// summaries, verbatim): how a governance role goes back from a summary to what was actually said.
    /// Read-only and session-scoped, so like <c>graph_view</c> it needs no approval.
    /// </summary>
    public const string RecallEvidenceToolId = "recall_evidence";

    public static bool IsRecallEvidence(string? toolId) =>
        string.Equals(toolId, RecallEvidenceToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The environment steward's tools (todo E1): see the workspace's environments and who holds them,
    /// take one for the calling run, give it back. Core state only — a lease in the ledger — so like the
    /// organization tools they need no approval; what the run then does inside the environment is
    /// governed by the tools it uses there.
    /// </summary>
    public const string EnvironmentListToolId = "environment_list";
    public const string EnvironmentAcquireToolId = "environment_acquire";
    public const string EnvironmentReleaseToolId = "environment_release";

    public static readonly IReadOnlyList<string> EnvironmentToolIds = [EnvironmentListToolId, EnvironmentAcquireToolId, EnvironmentReleaseToolId];

    public static bool IsEnvironment(string? toolId) =>
        toolId is not null && EnvironmentToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The exploration graph's tools (B1): read what has been recorded, record a node, link two nodes,
    /// ask how much of the declared asset inventory is covered, and fold material into a digest whose
    /// source is archived verbatim. Core state only; each tool's feature is validated at pack admission.
    /// </summary>
    public const string ExplorationViewToolId = "exploration_view";
    public const string ExplorationRecordToolId = "exploration_record";
    public const string ExplorationLinkToolId = "exploration_link";
    public const string ExplorationCoverageToolId = "exploration_coverage";
    public const string ExplorationDigestToolId = "exploration_digest";

    public static readonly IReadOnlyList<string> ExplorationToolIds =
    [
        ExplorationViewToolId, ExplorationRecordToolId, ExplorationLinkToolId, ExplorationCoverageToolId, ExplorationDigestToolId,
    ];

    public static bool IsExploration(string? toolId) =>
        toolId is not null && ExplorationToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The run's shared plan ledger (B2): read every intent with its owner and state, then claim,
    /// complete or block the one this worker owns. Core state only; the <c>plan_ledger</c> feature
    /// has to be declared by the mode, and the ledger itself enforces claim ownership.
    /// </summary>
    public const string PlanLedgerReadToolId = "plan_ledger_read";
    public const string PlanLedgerUpdateToolId = "plan_ledger_update";

    public static readonly IReadOnlyList<string> PlanLedgerToolIds = [PlanLedgerReadToolId, PlanLedgerUpdateToolId];

    public static bool IsPlanLedger(string? toolId) =>
        toolId is not null && PlanLedgerToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Searches one run's trace across the workers that produced it (B4): durable events, tool
    /// executions with their redacted previews, and the evidence archive. Read-only and run-scoped;
    /// the <c>cross_worker_trace</c> feature is declared by the mode and checked before serving.
    /// </summary>
    public const string TraceSearchToolId = "trace_search";

    public static bool IsTraceSearch(string? toolId) =>
        string.Equals(toolId, TraceSearchToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One run's dossier (B9): what the run was told, what its plan claimed, what its workers closed,
    /// what the investigation recorded, and what is still open — assembled from records the run
    /// already left behind. Read-only and run-scoped; the <c>run_dossier</c> feature is declared by
    /// the mode and checked before serving.
    ///
    /// Why it exists: the run's own records are spread across four surfaces (lifecycle, replay journal,
    /// plan ledger, exploration graph), and a worker told to "summarize where this got to" had to
    /// reconstruct that from conversation memory. One bounded read replaces an unbounded search.
    /// </summary>
    public const string RunDossierToolId = "run_dossier";

    /// <summary>
    /// Hard ceiling on rows per dossier section. It lives here, next to the id, because two modules
    /// enforce it: the service clamps its own read with it, and the tool layer refuses to pass a larger
    /// <c>limit</c> through. A model narrows this read; it can never widen it.
    /// </summary>
    public const int RunDossierSectionLimit = 50;

    public static bool IsRunDossier(string? toolId) =>
        string.Equals(toolId, RunDossierToolId, StringComparison.OrdinalIgnoreCase);

    public static bool IsPlanLedgerRead(string? toolId) =>
        string.Equals(toolId, PlanLedgerReadToolId, StringComparison.OrdinalIgnoreCase);

    public static bool IsGraphView(string? toolId) =>
        string.Equals(toolId, GraphViewToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for ANY Core-owned virtual tool. These are executed by Core itself and are
    /// deliberately absent from the TinadecTools child manifest, so every place that
    /// validates a tool id against the live manifest has to recognise them — otherwise a
    /// legitimately declared virtual tool is dropped as "the process does not offer it".
    /// </summary>
    public static bool IsCoreVirtual(string? toolId) =>
        IsCreateWorkspace(toolId) || IsTaskDispatch(toolId) || IsTaskWait(toolId) || IsPlanUpdate(toolId) || IsSpecPropose(toolId)
        || IsCorrectionTrace(toolId) || IsReadAttachment(toolId)
        || IsTinaChat(toolId) || IsOrganization(toolId) || IsGraphView(toolId) || IsRecallEvidence(toolId) || IsEnvironment(toolId)
        || IsExploration(toolId) || IsPlanLedger(toolId) || IsTraceSearch(toolId) || IsRunDossier(toolId)
        || IsLoadSkill(toolId) || IsTinadecConfig(toolId);

    /// <summary>
    /// True when a call-time gate may demand that the tool appear in the TinadecTools child-process
    /// manifest. False for Core-owned virtual tools: they have no child-process entry by construction,
    /// so a run declares and authorizes them through its frozen manifest alone.
    ///
    /// Both call-time gates have to ask this instead of looking up the live manifest directly. A gate
    /// that insists on a live entry rejects a legitimately declared virtual tool, and it does so with a
    /// message about the child process - which is why "the pack declares task_dispatch" read as working
    /// while every project-backed call was being refused.
    /// </summary>
    public static bool RequiresLiveManifestEntry(string? toolId) => !IsCoreVirtual(toolId);

    /// <summary>True when the caller declared no project (the <see cref="Guid.Empty"/> sentinel).</summary>
    public static bool IsProjectlessScope(Guid projectId) => projectId == ProjectlessProjectId;

    /// <summary>
    /// Wire → durable translation for the projectless sentinel: the wire-level
    /// Guid.Empty becomes a durable NULL. Every call site that stores or compares a
    /// project id arriving from the wire must go through this pair — comparing a raw
    /// wire sentinel against a durable NULL never matches and silently breaks
    /// projectless admission/idempotency.
    /// </summary>
    public static Guid? FromWireSentinel(Guid projectId) =>
        IsProjectlessScope(projectId) ? null : projectId;

    /// <summary>
    /// Durable → wire translation: a stored NULL is projected back as the
    /// Guid.Empty sentinel (and a real project id passes through unchanged).
    /// </summary>
    public static Guid ToWireSentinel(Guid? projectId) => projectId ?? ProjectlessProjectId;

    /// <summary>
    /// True for the only tool that may run inside a projectless scope: the
    /// Core-owned workspace-creation virtual tool. Every provider-backed tool
    /// requires a real project root and must fail closed here.
    /// </summary>
    public static bool IsProjectlessCreateWorkspace(Guid projectId, string? toolId) =>
        IsProjectlessScope(projectId) && IsCreateWorkspace(toolId);
}
