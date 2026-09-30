using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Core-owned virtual tool that lets an agent working through a multi-step task inside one tool
/// loop write its plan down and keep it current. Only the declaration lives here: the run engine
/// executes the call (it stores the plan on the caller's durable task node and emits
/// <c>plan.updated</c>), because the plan is part of the task state the engine owns. The
/// dispatcher answers a stray call with an error.
/// </summary>
internal static class CoreTaskPlanTool
{
    public const string ToolId = CoreVirtualToolPolicy.PlanUpdateToolId;

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"steps\":{\"type\":\"array\",\"maxItems\":20,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"step\":{\"type\":\"string\",\"description\":\"One short, concrete action.\"}," +
        "\"status\":{\"type\":\"string\",\"enum\":[\"pending\",\"in_progress\",\"completed\"]}}," +
        "\"required\":[\"step\",\"status\"],\"additionalProperties\":false}," +
        "\"description\":\"The whole plan, in order. Each call replaces the previous plan.\"}," +
        "\"explanation\":{\"type\":\"string\",\"description\":\"Optional. Why the plan changed, when it did.\"}}," +
        "\"required\":[\"steps\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description =
            "Write down or update your step-by-step plan for this task; the user sees it live. Use it for work "
            + "with three or more steps, skip it for simple tasks. Send the whole plan each time, keep at most one "
            + "step in_progress, and mark a step completed as soon as it is done.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        // Replacing the plan with the same plan changes nothing.
        RetrySafety = "safe",
        ConfirmationFields = []
    };
}
