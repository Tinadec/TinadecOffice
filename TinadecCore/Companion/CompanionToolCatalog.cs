using System.Text.Json;

namespace TinadecCore.Companion;

/// <summary>
/// The companion's entire tool surface. Four management verbs over the user's work sessions and
/// nothing else — no file, shell, Git, MCP or browser entry exists here, and adding one would turn
/// a companion into an unapproved executor of the work it is supposed to supervise.
///
/// The descriptions are written for the model: when a verb applies, what its arguments mean, and
/// the conditions under which it is refused (a foreign or missing session, a relative path, a
/// session that cannot admit another run).
/// </summary>
public static class CompanionToolCatalog
{
    public const string ListSessions = "companion_list_sessions";
    public const string ReadRun = "companion_read_run";
    public const string Dispatch = "companion_dispatch";
    public const string CreateWorkspace = "companion_create_workspace";
    public const string ListWorkspaces = "companion_list_workspaces";
    public const string SessionDetail = "companion_session_detail";

    public static IReadOnlyList<CompanionToolDeclaration> Tools { get; } =
    [
        new(ListSessions,
            "List the work sessions of this workspace, most recently active first, at most 50. Each item has the "
            + "session id, title, project id and its latest run (id, status, summary) or null when the session has "
            + "never run. Use this first: companion_dispatch and companion_read_run address sessions and runs by id.",
            Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)),

        new(ReadRun,
            "Read one run's current status, summary and terminal error category. Use it to check what happened after "
            + "you dispatched a task, or when a session's latest run looks stuck. A run id that belongs to another "
            + "tenant or workspace is refused exactly like an id that does not exist (run_not_found), so never treat "
            + "that refusal as a transient failure to retry.",
            Parse("""
                {"type":"object","properties":{"run_id":{"type":"string","description":"Run id returned by companion_list_sessions or companion_dispatch."}},"required":["run_id"],"additionalProperties":false}
                """)),

        new(Dispatch,
            "Dispatch a task into an existing work session: the task is written as a user message and a run is started "
            + "for it. This is a management action and does not ask for tool approval; the agents working inside that "
            + "session still go through their own approval gates. Refused when session_id does not belong to the "
            + "current tenant/workspace (session_not_found), when task is blank, or when the session cannot admit the "
            + "run (for example a busy or at-limit session). The answer is the new run's identifiers, not the work's "
            + "result — check companion_read_run later.",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Session id from companion_list_sessions."},"task":{"type":"string","description":"The full task text to deliver to that session as a user message."}},"required":["session_id","task"],"additionalProperties":false}
                """)),

        new(ListWorkspaces,
            "List the project workspaces registered here: id, name, root path, and how many active sessions each "
            + "holds. Use it before companion_create_workspace so you do not register a root that already exists, and "
            + "to answer which projects the user is working in.",
            Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)),

        new(SessionDetail,
            "Read one session in depth: title, project, permission mode, whether it currently has an active run, and "
            + "a capped transcript of recent turns (oldest first, at most 20 by default). Use it to check whether that "
            + "session is already working on the same thing before dispatching, or when the user asks what a session "
            + "has been doing. Refused for a session outside this tenant/workspace.",
            Parse("""
                {"type":"object","properties":{"session_id":{"type":"string","description":"Session id from companion_list_sessions."},"limit":{"type":"integer","description":"How many recent messages to include, 1-50; defaults to 20."}},"required":["session_id"],"additionalProperties":false}
                """)),

        new(CreateWorkspace,
            "Create or reuse a project workspace directory and register it in this workspace. path must be absolute; "
            + "an existing directory is reused and an existing project with the same root is returned rather than "
            + "duplicated. Refused when name or path is blank, when path is relative, or when the directory cannot be "
            + "created. This registers a workspace; it does not create a session in it.",
            Parse("""
                {"type":"object","properties":{"name":{"type":"string","description":"Display name of the project workspace."},"path":{"type":"string","description":"Absolute filesystem path of the workspace directory."}},"required":["name","path"],"additionalProperties":false}
                """))
    ];

    public static CompanionToolDeclaration? Find(string? toolId) =>
        string.IsNullOrWhiteSpace(toolId)
            ? null
            : Tools.FirstOrDefault(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));

    public static bool IsCompanionTool(string? toolId) => Find(toolId) is not null;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>One declared verb: the id the model calls, its model-facing description, and its JSON schema.</summary>
public sealed record CompanionToolDeclaration(string Id, string Description, JsonElement InputSchema);
