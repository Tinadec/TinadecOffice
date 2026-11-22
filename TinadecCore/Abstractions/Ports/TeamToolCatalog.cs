using System.Text.Json;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Model-facing JSON schema catalog for the 5 orthogonal <c>team_*</c> coordination tools (#33).
/// Kept in <c>TinadecCore.Abstractions.Ports</c> alongside <see cref="OrganizationToolCatalog"/> so
/// run freezing, engine virtual dispatch, and the loopback scoped MCP server share one schema source.
/// </summary>
public static class TeamToolCatalog
{
    public sealed record Entry(string Id, string Description, string SchemaJson, bool Writes);

    private const string RuntimeOverrideSchema =
        "{\"type\":\"object\",\"description\":\"Optional heterogeneous runtime override for this teammate (e.g. CLI/ACP/TUI harness or specific provider instance).\",\"properties\":{"
        + "\"provider_instance_id\":{\"type\":\"string\",\"description\":\"Optional provider instance GUID or configured provider slug.\"},"
        + "\"channel\":{\"type\":\"string\",\"enum\":[\"api\",\"acp\",\"headless-cli\",\"tui\",\"opencode-serve\"],\"description\":\"Optional transport/channel override.\"},"
        + "\"model_id\":{\"type\":\"string\",\"description\":\"Optional explicit model identifier when using an API provider.\"}"
        + "},\"additionalProperties\":false}";

    private const string PlanStepsSchema =
        "{\"type\":\"array\",\"description\":\"Ordered plan steps with explicit ownership and checkable evidence.\",\"items\":{"
        + "\"type\":\"object\",\"properties\":{"
        + "\"id\":{\"type\":\"string\",\"description\":\"Stable short step identifier (e.g. 'step-1', 'auth-port').\"},"
        + "\"title\":{\"type\":\"string\",\"description\":\"Concise action statement for this step.\"},"
        + "\"owner_handle\":{\"type\":\"string\",\"description\":\"Handle of the responsible teammate or 'coordinator'.\"},"
        + "\"status\":{\"type\":\"string\",\"enum\":[\"pending\",\"in_progress\",\"completed\",\"blocked\",\"skipped\"],\"description\":\"Current execution state of the step.\"},"
        + "\"evidence\":{\"type\":\"string\",\"description\":\"Checkable completion or blocker evidence (file paths, commit SHA, test output).\"}"
        + "},\"required\":[\"id\"],\"additionalProperties\":false}}";

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new(
            CoreVirtualToolPolicy.TeamSpawnToolId,
            "Spawn a specialist teammate (`mode: 'teammate'`, persistent and re-wakable on the shared run board) or a lightweight one-shot worker "
            + "(`mode: 'ephemeral'`). Set `worktree: true` with a disjoint `write_scope` when multiple teammates edit files concurrently so each "
            + "works in an isolated Git worktree and merges cleanly via `team_integrate`.",
            Obj(
                required: ["agent_slug", "prompt"],
                ("handle", Str("Short unique teammate handle within this run (e.g. 'core-api', 'ui-audit'). Optional in ephemeral mode.")),
                ("agent_slug", Str("Target specialist agent slug from the mode's dispatch roster (e.g. 'search', 'global_engineering', 'reviewer').")),
                ("prompt", Str("Focused assignment brief: owned files/scope, boundaries not to cross, verification commands, and expected deliverable.")),
                ("mode", Enum("Lifecycle mode: 'teammate' stays addressable on the shared board for follow-ups/rework; 'ephemeral' runs once and exits. Default 'teammate'.", "teammate", "ephemeral")),
                ("worktree", Bool("When true, creates an isolated Git worktree for this teammate so concurrent edits never collide.")),
                ("write_scope", ArrStr("Workspace-relative path prefixes or globs this teammate is authorized to modify (enforced at tool/ACP & integration gates).")),
                ("runtime_override", RuntimeOverrideSchema)),
            Writes: true),

        new(
            CoreVirtualToolPolicy.TeamMessageToolId,
            "Post a structured update, review verdict, handoff, or question on the run's low-latency shared board. Address `'coordinator'`, a specific "
            + "teammate `<handle>`, or `'all'`. When `wake` is true (default), a settled target teammate or waiting coordinator is woken with sub-second debounce.",
            Obj(
                required: ["target", "content"],
                ("target", Str("Recipient address: 'coordinator', 'all', or a spawned teammate handle.")),
                ("content", Str("Concise, factual message citing concrete paths, findings, conflict files, or questions.")),
                ("kind", Enum("Semantic message category (default 'update').", "update", "review", "handoff", "question")),
                ("wake", Bool("Whether to immediately wake the target if it is currently settled/waiting (default true)."))),
            Writes: true),

        new(
            CoreVirtualToolPolicy.TeamWaitToolId,
            "Yield the current turn loop until specified teammates (or any active teammate when `handles` is omitted) settle, post a board message, "
            + "or reach `timeout_seconds`, returning a structured snapshot of teammate states, latest board messages, and outcomes.",
            Obj(
                required: [],
                ("handles", ArrStr("Optional list of teammate handles to wait on; omit or pass [] to wait on any active teammate.")),
                ("timeout_seconds", "{\"type\":\"integer\",\"minimum\":1,\"maximum\":600,\"description\":\"Maximum seconds to wait before returning current status snapshot (default 120).\"}"),
                ("until", Enum("Wake condition: 'settled' waits until target teammates finish their current turn, 'message' wakes on new board post, 'any' wakes on whichever happens first (default 'any').", "settled", "message", "any"))),
            Writes: false),

        new(
            CoreVirtualToolPolicy.TeamPlanToolId,
            "Create or update the run's shared `SessionPlan` visible to the user, coordinator, and all teammates. Use `operation: 'replace'` after "
            + "read-only exploration to establish the plan, and `operation: 'update_step'` to advance step statuses and record verification evidence.",
            Obj(
                required: ["steps"],
                ("goal", Str("High-level objective of the run (required on initial 'replace', optional on 'update_step').")),
                ("steps", PlanStepsSchema),
                ("operation", Enum("Whether to 'replace' the full step list or 'update_step' matching steps by id (default 'replace').", "replace", "update_step"))),
            Writes: true),

        new(
            CoreVirtualToolPolicy.TeamIntegrateToolId,
            "Inspect, cleanly integrate, or abort isolated teammate Git worktrees into the target workspace branch via `IWorktreeIntegratorService`. "
            + "`inspect` checks dirty files, `write_scope` violations, and cross-teammate file overlaps without mutating the target branch; `integrate` "
            + "refuses overlaps/scope violations fail-closed and automatically rolls back (`git merge --abort` / `git cherry-pick --abort`) on any conflict.",
            Obj(
                required: ["action"],
                ("action", Enum("Operation: 'inspect' preflight report, 'integrate' merge into target branch, or 'abort' clean up any in-progress merge/cherry-pick.", "inspect", "integrate", "abort")),
                ("handles", ArrStr("Teammate handles to inspect or integrate in order; omit to include all isolated teammate worktrees in the run.")),
                ("strategy", Enum("Git integration strategy when action='integrate' (default 'no_ff').", "no_ff", "squash", "cherry_pick")),
                ("target_branch", Str("Optional target branch in the main workspace repository; defaults to the run's checked-out branch."))),
            Writes: true),
    ];

    public static Entry? Find(string? toolId) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, toolId, StringComparison.OrdinalIgnoreCase));

    public static ToolManifestEntryDto? ManifestEntry(string? toolId)
    {
        var entry = Find(toolId);
        if (entry is null) return null;
        return ToManifestEntry(entry);
    }

    public static FrozenToolManifestEntry? FrozenEntry(string? toolId)
    {
        var dto = ManifestEntry(toolId);
        if (dto is null) return null;
        return new FrozenToolManifestEntry(
            dto.Id,
            dto.Description,
            dto.InputSchema.Clone(),
            dto.Risk,
            dto.MutatesWorkspace,
            dto.RequiresApproval,
            dto.RetrySafety,
            dto.ConfirmationFields);
    }

    public static IReadOnlyList<ToolManifestEntryDto> AllManifestEntries() =>
        Entries.Select(ToManifestEntry).ToArray();

    private static ToolManifestEntryDto ToManifestEntry(Entry entry) => new()
    {
        Id = entry.Id,
        Description = entry.Description,
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(entry.SchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = [],
    };

    private static string Obj(string[] required, params (string Name, string Schema)[] fields)
    {
        var req = required.Length == 0
            ? string.Empty
            : ",\"required\":[" + string.Join(",", required.Select(r => $"\"{r}\"")) + "]";
        return "{\"type\":\"object\",\"properties\":{"
            + string.Join(",", fields.Select(x => $"\"{x.Name}\":{x.Schema}"))
            + "}" + req + ",\"additionalProperties\":false}";
    }

    private static string Str(string description) =>
        "{\"type\":\"string\",\"description\":\"" + Escape(description) + "\"}";

    private static string Bool(string description) =>
        "{\"type\":\"boolean\",\"description\":\"" + Escape(description) + "\"}";

    private static string ArrStr(string description) =>
        "{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + Escape(description) + "\"}";

    private static string Enum(string description, params string[] values) =>
        "{\"type\":\"string\",\"enum\":[" + string.Join(",", values.Select(v => $"\"{v}\"")) + "],\"description\":\"" + Escape(description) + "\"}";

    private static string Escape(string value) => value.Replace("\\", "").Replace("\"", "'");
}
