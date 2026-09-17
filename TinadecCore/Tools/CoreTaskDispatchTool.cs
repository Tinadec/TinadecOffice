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
/// v1 boundary, deliberately: the call persists the task and returns immediately; it does
/// NOT block the conversation waiting for the sub-agent's result. The sub-agent's output
/// reaches the user through the close-out evidence, which the meeting reads. Genuinely
/// waiting in place and reading the result back needs the park/resume boundary and is
/// registered as a separate follow-up — claiming otherwise here would be a lie the
/// prompt and the answer would then repeat.
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

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"title\":{\"type\":\"string\",\"description\":\"One sentence describing the sub-task to hand off.\"}," +
        "\"description\":{\"type\":\"string\",\"description\":\"What the sub-agent must do, with any paths or context it needs.\"}," +
        "\"success_criteria\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
        "\"description\":\"Externally verifiable criteria (an observable file, command output or state).\"}," +
        "\"required_tools\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
        "\"description\":\"Tool ids the sub-task needs, verbatim from the frozen roster. Leave empty only when you do not know which tool is needed.\"}," +
        "\"required_capabilities\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
        "\"description\":\"Capabilities the sub-task needs, from the frozen roster.\"}}," +
        "\"required\":[\"title\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description =
            "Dispatch a sub-task to another agent and keep working yourself. Use it when work can be "
            + "split in parallel or needs a capability you do not hold. Returns as soon as the sub-task "
            + "is queued; its result appears in the run's evidence, not in this call's return value.",
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
