using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Declaration for the Core-owned correction trace tool. The run engine executes
/// the call because the trace is checkpoint state, not a child-process tool.
/// </summary>
internal static class CoreCorrectionTraceTool
{
    public const string ToolId = CoreVirtualToolPolicy.CorrectionTraceToolId;

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"phase\":{\"type\":\"string\",\"enum\":[\"OBSERVE\",\"INTERVENE\",\"CONVERGE\",\"BACKTRACK\"]}," +
        "\"summary\":{\"type\":\"string\"},\"reason\":{\"type\":\"string\"}," +
        "\"goal\":{\"type\":\"string\"},\"symptom\":{\"type\":\"string\"},\"done_when\":{\"type\":\"string\"}," +
        "\"root_cause\":{\"type\":\"string\"},\"recheck\":{\"type\":\"string\"},\"confidence\":{\"type\":\"string\",\"enum\":[\"low\",\"medium\",\"high\"]}," +
        "\"intervention\":{\"type\":\"string\"},\"prediction\":{\"type\":\"string\"},\"verdict\":{\"type\":\"string\"}," +
        "\"fix\":{\"type\":\"string\"},\"verify\":{\"type\":\"string\"}," +
        "\"hypotheses\":{\"type\":\"array\",\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"id\":{\"type\":\"string\"},\"statement\":{\"type\":\"string\"},\"status\":{\"type\":\"string\",\"enum\":[\"testing\",\"confirmed\",\"rejected\"]}," +
        "\"kill_criterion\":{\"type\":\"string\"},\"evidence_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
        "\"required\":[\"id\",\"statement\"]}}," +
        "\"open_questions\":{\"type\":\"array\",\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"id\":{\"type\":\"string\"},\"question\":{\"type\":\"string\"},\"blocking\":{\"type\":\"boolean\"},\"options\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
        "\"required\":[\"id\",\"question\"]}}," +
        "\"required\":[\"phase\",\"reason\"]}}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description = "Record the host-owned correction trace: phase, evidence-backed hypotheses, root-cause candidate, intervention, prediction, fix, verification intent and open questions. Never claim verified or resolved; Core derives those from real tool facts and user feedback.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = [],
    };
}
