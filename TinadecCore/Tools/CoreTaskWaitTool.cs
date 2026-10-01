using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Core-owned virtual tool that lets a dispatcher of sub-tasks wait for them and read their outcomes
/// inside the same tool loop. Only its declaration lives here: the call is executed by the run engine
/// (it parks the caller's task in the task graph until the awaited sub-tasks are terminal), because
/// the engine is the only owner of that graph. The dispatcher answers a stray call with an error.
/// </summary>
internal static class CoreTaskWaitTool
{
    public const string ToolId = CoreVirtualToolPolicy.TaskWaitToolId;

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"dispatch_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
        "\"description\":\"dispatch_id values returned by task_dispatch (or executor handles such as search#1). Omit to wait for every sub-task you dispatched that has not been reported to you yet.\"}}," +
        "\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description =
            "Wait for sub-tasks you dispatched with task_dispatch and get their results: per task the executor "
            + "handle, status (completed / failed / blocked), summary and per-criterion evidence. You are paused "
            + "until every awaited sub-task finishes; dispatch everything that can run in parallel BEFORE waiting.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = []
    };
}
