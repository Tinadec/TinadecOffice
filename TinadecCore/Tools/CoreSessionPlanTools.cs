using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manifest entry for <c>plan_read</c>, the Core-owned virtual tool that lets coordinators
/// and workers inspect the session's persistent structured plan (phases, steps, dependencies,
/// assigned agents, verification criteria, and recorded artifacts).
/// </summary>
internal static class CoreSessionPlanTools
{
    public const string ReadToolId = CoreVirtualToolPolicy.PlanReadToolId;

    private const string ReadInputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"phase_id\":{\"type\":\"string\",\"description\":\"Optional phase id filter.\"}," +
        "\"status\":{\"type\":\"string\",\"enum\":[\"pending\",\"in_progress\",\"completed\",\"blocked\",\"failed\",\"skipped\"],\"description\":\"Optional step status filter.\"}" +
        "},\"additionalProperties\":false}";

    public static ToolManifestEntryDto ReadManifestEntry() => new()
    {
        Id = ReadToolId,
        Description =
            "Read the persistent structured plan for this session (phases, steps, dependencies, assigned agents, "
            + "verification criteria, and artifacts). Read-only and safe to call anytime.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(ReadInputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = []
    };
}
