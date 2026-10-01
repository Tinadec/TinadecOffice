using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Core-owned virtual tool that lets the conversation identity hand a sub-task to the
/// ordinary dispatch path. The call never reaches a TinadecTools child process: Core
/// persists a durable task node and the existing dispatch loop picks it up (worker
/// selection, spawnable templates, approval and convergence all unchanged).
///
/// Why it exists: a solo master works inside ONE tool loop, so to "dispatch
/// aggressively, as the model judges" it needs a way to express that from inside the
/// loop. Emitting a task array is the PLANNER's mechanism and the master never runs the
/// planner — without this tool the instruction would be prose the model cannot act on.
///
/// The call persists the task and returns immediately with a dispatch_id; it does NOT
/// block. Reading the result back is <see cref="CoreTaskWaitTool"/>'s job: the engine
/// parks the caller as "waiting" until the named sub-tasks are terminal and then answers
/// the pending task_wait call with their results. follow_up_of hands an earlier
/// sub-task's result to a new executor, so a correction does not start from nothing.
///
/// Approval: NOT required. Dispatching is a scheduling decision, and every side effect it
/// can lead to is gated downstream by the sub-task's own authorization, resource envelope
/// and per-write approval. Requiring approval here would double-gate the same writes and
/// make the "dispatch when it pays off" behaviour painful enough that the model would
/// simply stop doing it.
/// </summary>
internal static class CoreTaskDispatchTool
{
    public const string ToolId = CoreVirtualToolPolicy.TaskDispatchToolId;

    // The coordinator names the executor by responsibility (agent) and hands it a self-contained
    // brief: the executor does not share the coordinator's conversation, so the brief is all it gets.
    // required_tools is an optional hint only — routing by tools picked the narrowest holder of the
    // listed tools and misrouted 4/12 tasks in the 2026-09-27 sampling; naming the role routed 11/12.
    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"agent\":{\"type\":\"string\",\"description\":\"Executor id, copied verbatim from the dispatchable-executor roster in your instructions. Choose by responsibility: the executor whose duty and boundary fit the sub-task.\"}," +
        "\"title\":{\"type\":\"string\",\"description\":\"One sentence naming the sub-task.\"}," +
        "\"description\":{\"type\":\"string\",\"description\":\"Self-contained brief: the goal, the absolute paths involved, and any context the executor needs. It cannot see your conversation.\"}," +
        "\"success_criteria\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
        "\"description\":\"Externally verifiable criteria (an observable file, command output or state).\"}," +
        "\"required_tools\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
        "\"description\":\"Optional. Restricts the executor to exactly these tool ids; omit to give it its full tool set. Not used to choose the executor.\"}," +
        "\"follow_up_of\":{\"type\":\"string\",\"description\":\"Optional. dispatch_id or handle (e.g. search#1) of an earlier sub-task this one continues; its title, result and evidence are handed to the new executor as context.\"}," +
        "\"write_scope\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
        "\"description\":\"Optional, for sub-tasks that write. Paths inside the workspace (files or directories, absolute or relative to the workspace root) the sub-task will change. They are reserved for it right away: if another run already holds one, this call is refused and names the holder, so two agents never edit the same place. Omit for read-only work.\"}}," +
        "\"required\":[\"agent\",\"title\",\"description\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description =
            "Hand a sub-task to a named executor. Use it when work can run in parallel or needs a "
            + "responsibility you do not hold. Returns at once with a dispatch_id; the executor's result is NOT "
            + "in this reply. Dispatch everything that can run in parallel, then call task_wait with the "
            + "dispatch_ids to get the results before you rely on them.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        // Repeating the identical call creates a second task, so it is not idempotent —
        // an honest self-report that keeps the loop guard's repeat detection meaningful.
        RetrySafety = "unsafe",
        ConfirmationFields = []
    };

    public static bool IsCoreTool(string toolId) => CoreVirtualToolPolicy.IsTaskDispatch(toolId);
}
