using System.Text.Json;

namespace TinadecCore.Companion;

/// <summary>
/// Governed tool catalog for Companion Bots and Session Partners. Covers work-session command &amp;
/// perception (<see cref="TinadecCore.Abstractions.Ports.ICompanionWorkGateway"/>), unified layered
/// memory (<see cref="TinadecCore.Abstractions.Ports.IContextMemoryService"/>), and the Bot's own
/// persistent <c>schedules</c>, <c>plans</c>, and <c>todos</c> — never direct code, shell, Git, or
/// filesystem execution inside user workspaces.
/// </summary>
public static class CompanionToolCatalog
{
    public const string ListModes = "companion_list_modes";
    public const string ListSessions = "companion_list_sessions";
    public const string CreateSession = "companion_create_session";
    public const string ReadRun = "companion_read_run";
    public const string Dispatch = "companion_dispatch";
    public const string SteerRun = "companion_steer_run";
    public const string CancelRun = "companion_cancel_run";
    public const string SideQuestion = "companion_side_question";
    public const string ListApprovals = "companion_list_approvals";
    public const string CreateWorkspace = "companion_create_workspace";
    public const string ListWorkspaces = "companion_list_workspaces";
    public const string SessionDetail = "companion_session_detail";
    public const string Relay = "companion_relay";

    public const string MemoryRemember = "companion_memory_remember";
    public const string MemorySearch = "companion_memory_search";
    public const string ProjectNote = "companion_project_note";

    public const string ListSchedules = "companion_list_schedules";
    public const string UpsertSchedule = "companion_upsert_schedule";
    public const string DeleteSchedule = "companion_delete_schedule";
    public const string ListPlans = "companion_list_plans";
    public const string UpsertPlan = "companion_upsert_plan";
    public const string ListTodos = "companion_list_todos";
    public const string UpsertTodo = "companion_upsert_todo";

    public static IReadOnlyList<CompanionToolDeclaration> Tools { get; } =
    [
        new(ListModes,
            "List published Agent Modes (Solo, Plan, Team, Spec, Correction, Review, etc.) with their mode_version_id, slug, display_name, and description.",
            Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)),

        new(ListSessions,
            "List active work sessions in authorized workspaces (up to 50), most recently active first, including project, mode, latest run status/summary, live SessionPlan progress, and pending approvals.",
            Parse("""
                {"type":"object","properties":{"project_id":{"type":"string","description":"Optional project workspace UUID filter."}},"additionalProperties":false}
                """)),

        new(CreateSession,
            "Create a new work session in an authorized project workspace, optionally pinned to a published mode_version_id.",
            Parse("""
                {"type":"object","properties":{"project_id":{"type":"string","description":"Project workspace UUID from companion_list_workspaces."},"title":{"type":"string","description":"Concise title for the new work session."},"mode_version_id":{"type":"string","description":"Optional published mode_version_id from companion_list_modes."}},"additionalProperties":false}
                """)),

        new(ReadRun,
            "Read one work run's current status, summary, plan progress, pending approvals, and terminal error category.",
            Parse("""
                {"type":"object","properties":{"run_id":{"type":"string","description":"Run id returned by companion_list_sessions or companion_dispatch."}},"required":["run_id"],"additionalProperties":false}
                """)),

        new(Dispatch,
            "Dispatch a task into an authorized work session as a user message and start a run, optionally pinning a formal mode_version_id or isolated Git worktree.",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Session id from companion_list_sessions or companion_create_session."},"task":{"type":"string","description":"Task text to deliver to that session."},"mode_version_id":{"type":"string","description":"Optional published agent mode version UUID."},"worktree":{"type":"boolean","description":"Optional flag to execute inside an isolated Git worktree."}},"required":["session_id","task"],"additionalProperties":false}
                """)),

        new(SteerRun,
            "Inject mid-flight steering guidance into an active run (interrupting in-flight model generation cleanly) or follow up on an authorized work session.",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Target work session UUID."},"run_id":{"type":"string","description":"Optional active run UUID; defaults to the session's active root run."},"guidance":{"type":"string","description":"Steering correction or supplementary direction."}},"required":["session_id","guidance"],"additionalProperties":false}
                """)),

        new(CancelRun,
            "Stop/cancel an active run inside an authorized work session immediately.",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Target work session UUID."},"run_id":{"type":"string","description":"Optional active run UUID; defaults to the session's active root run."},"reason":{"type":"string","description":"Optional short reason for stopping the run."}},"required":["session_id"],"additionalProperties":false}
                """)),

        new(SideQuestion,
            "Ask a strictly read-only side question about a work session or active run (inspecting status, plan progress, pending approvals, and recent transcript without mutating or interrupting the session).",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Target work session UUID."},"run_id":{"type":"string","description":"Optional run UUID."},"question":{"type":"string","description":"Question to inspect against the session's live state and transcript."}},"required":["session_id","question"],"additionalProperties":false}
                """)),

        new(ListApprovals,
            "List pending tool approvals waiting on user confirmation across authorized workspaces (or narrowed to one session_id).",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Optional session UUID filter."}},"additionalProperties":false}
                """)),

        new(ListWorkspaces,
            "List authorized project workspaces (id, name, root path, active session count).",
            Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)),

        new(SessionDetail,
            "Read one authorized work session in depth: title, project, mode, permission mode, active-run flag, plan progress, pending approvals, and recent transcript turns.",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Session id from companion_list_sessions."},"limit":{"type":"integer","description":"Recent message count (1-50, default 20)."}},"required":["session_id"],"additionalProperties":false}
                """)),

        new(Relay,
            "Send a durable message to another active companion in this tenant (governed by max_bot_hop_depth).",
            Parse("""
                {"type":"object","properties":{"companion_id":{"type":"string","description":"Target companion id."},"content":{"type":"string","description":"Message body in your own voice."}},"required":["companion_id","content"],"additionalProperties":false}
                """)),

        new(CreateWorkspace,
            "Create or reuse a project workspace directory using an absolute filesystem path (requires wildcard '*' work_session grant).",
            Parse("""
                {"type":"object","properties":{"name":{"type":"string","description":"Display name of the project workspace."},"path":{"type":"string","description":"Absolute filesystem path."}},"required":["name","path"],"additionalProperties":false}
                """)),

        new(MemoryRemember,
            "Add, supersede, or revoke an item in your private Companion memory ledger (scope='companion', synced to your private memory.md).",
            Parse("""
                {"type":"object","properties":{"action":{"type":"string","description":"add | supersede | revoke (default add)."},"content":{"type":"string","description":"Memory content (required for add and supersede)."},"kind":{"type":"string","description":"bond | episode | task_template | fact | preference | decision"},"section":{"type":"string","description":"Optional Markdown heading section."},"item_id":{"type":"string","description":"Existing private memory item UUID (required for supersede or revoke)."},"reason":{"type":"string","description":"Optional provenance or revocation reason."}},"additionalProperties":false}
                """)),

        new(MemorySearch,
            "Search your private Companion memory plus read-only L3 user global profile (principal) and L2 project memory.",
            Parse("""
                {"type":"object","properties":{"query":{"type":"string","description":"Search query keywords."},"scope":{"type":"string","description":"all | companion | principal | project (default all)."},"project_id":{"type":"string","description":"Optional project UUID when searching project memory."},"limit":{"type":"integer","description":"Maximum items per scope (1-50, default 10)."}},"additionalProperties":false}
                """)),

        new(ProjectNote,
            "Record a provenance-tagged note into L2 project memory when companion_can_write_project_memory=true in runtime.toml (otherwise refused cleanly in favor of companion_dispatch).",
            Parse("""
                {"type":"object","properties":{"project_id":{"type":"string","description":"Target project workspace UUID."},"workspace_root":{"type":"string","description":"Optional workspace root path if project_id is omitted."},"content":{"type":"string","description":"Concise factual project note to record."},"kind":{"type":"string","description":"fact | decision | failure_pattern (default fact)."},"section":{"type":"string","description":"Optional project memory section."},"reason":{"type":"string","description":"Why this note is being recorded."}},"required":["content"],"additionalProperties":false}
                """)),

        new(ListSchedules,
            "List your own persistent schedules (recurring 5-field UTC cron jobs and one-shot reminders).",
            Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)),

        new(UpsertSchedule,
            "Create or update one of your persistent schedules. Supply schedule_id to update an existing schedule, or omit it to create a new one.",
            Parse("""
                {"type":"object","properties":{"schedule_id":{"type":"string","description":"Optional existing schedule UUID to update."},"title":{"type":"string","description":"Short schedule title."},"prompt":{"type":"string","description":"Instruction delivered to you when the schedule fires."},"cron_expression":{"type":"string","description":"Optional 5-field UTC cron expression (e.g. '0 9 * * 1-5')."},"run_at":{"type":"string","description":"Optional ISO-8601 UTC timestamp for one-shot schedules."},"one_shot":{"type":"boolean","description":"True if the schedule should run once and complete."},"target_conversation_id":{"type":"string","description":"Optional conversation UUID to post the wake into."},"enabled":{"type":"boolean","description":"Whether the schedule is active."}},"additionalProperties":false}
                """)),

        new(DeleteSchedule,
            "Delete one of your schedules by schedule_id.",
            Parse("""
                {"type":"object","properties":{"schedule_id":{"type":"string","description":"Schedule UUID to delete."}},"required":["schedule_id"],"additionalProperties":false}
                """)),

        new(ListPlans,
            "List your own persistent multi-step plans.",
            Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)),

        new(UpsertPlan,
            "Create or update one of your persistent plans and its checklist steps. Supply plan_id to update an existing plan, or omit it to create one.",
            Parse("""
                {"type":"object","properties":{"plan_id":{"type":"string","description":"Optional existing plan UUID to update."},"title":{"type":"string","description":"Plan title."},"summary":{"type":"string","description":"Optional summary."},"status":{"type":"string","description":"active | completed | paused | archived"},"steps":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"status":{"type":"string","description":"pending | in_progress | completed | blocked"},"note":{"type":"string"}},"required":["title"],"additionalProperties":false}}},"additionalProperties":false}
                """)),

        new(ListTodos,
            "List your own persistent todo items.",
            Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)),

        new(UpsertTodo,
            "Create or update one of your persistent todo items. Supply todo_id to update an existing todo, or omit it to create one.",
            Parse("""
                {"type":"object","properties":{"todo_id":{"type":"string","description":"Optional existing todo UUID to update."},"title":{"type":"string","description":"Todo title."},"note":{"type":"string","description":"Optional detail note."},"priority":{"type":"string","description":"low | medium | high"},"status":{"type":"string","description":"pending | in_progress | done | cancelled"},"due_at":{"type":"string","description":"Optional ISO-8601 due timestamp."},"plan_id":{"type":"string","description":"Optional parent plan UUID."}},"additionalProperties":false}
                """))
    ];

    /// <summary>
    /// Tools available during an autonomous background tick: workspace/session perception,
    /// governed session creation/dispatch/steering, private/read-only memory tools, and
    /// self-owned schedule/plan/todo management.
    /// </summary>
    public static IReadOnlyList<CompanionToolDeclaration> AutonomyTools { get; } =
        Tools.Where(t => t.Id is ListModes or ListSessions or CreateSession or SessionDetail or ReadRun
            or ListWorkspaces or ListApprovals or SideQuestion
            or Dispatch or SteerRun
            or MemoryRemember or MemorySearch
            or ListSchedules or UpsertSchedule or DeleteSchedule
            or ListPlans or UpsertPlan
            or ListTodos or UpsertTodo).ToList();

    public static CompanionToolDeclaration? Find(string? toolId) =>
        string.IsNullOrWhiteSpace(toolId)
            ? null
            : Tools.FirstOrDefault(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));

    public static bool IsCompanionTool(string? toolId) => Find(toolId) is not null;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

public sealed record CompanionToolDeclaration(string Id, string Description, JsonElement InputSchema);
