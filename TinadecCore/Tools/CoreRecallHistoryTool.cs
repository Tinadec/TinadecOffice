using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Core-owned virtual tool for conscious on-demand recall from the immutable conversation
/// message ledger and session summaries. Returns a two-layer result pack: synthesized
/// <c>summary</c> + exact <c>verbatim_snippets</c> honoring <c>recall_fidelity</c>
/// (<c>summary_only</c>, <c>balanced</c>, <c>verbatim_first</c>) and <c>allow_cross_session_recall</c>.
/// </summary>
public static class CoreRecallHistoryTool
{
    public const string ToolId = CoreVirtualToolPolicy.RecallHistoryToolId;

    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"query\":{\"type\":\"string\",\"description\":\"Keywords, error string, file path, or natural-language question to recall from conversation history.\"}," +
        "\"session_id\":{\"type\":\"string\",\"description\":\"Optional session GUID to search; defaults to the current session.\"}," +
        "\"cross_session\":{\"type\":\"boolean\",\"description\":\"Optional flag to search sibling sessions in the same project when allow_cross_session_recall is enabled.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":30,\"description\":\"Maximum verbatim snippets to return (1..30, default 8).\"}," +
        "\"fidelity\":{\"type\":\"string\",\"enum\":[\"summary_only\",\"balanced\",\"verbatim_first\"],\"description\":\"Output fidelity override: summary_only omits verbatim excerpts; balanced returns summary + compact snippets; verbatim_first prioritizes exact excerpts.\"}}" +
        ",\"required\":[\"query\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description =
            "Consciously recall earlier turns or sibling project sessions from the immutable conversation ledger. "
            + "Returns a two-layer pack: a concise summary plus verbatim_snippets with session_id and message_seq coordinates, "
            + "honoring recall_fidelity (summary_only | balanced | verbatim_first) and allow_cross_session_recall.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = []
    };

    public static bool IsCoreTool(string toolId) => CoreVirtualToolPolicy.IsRecallHistory(toolId);

    public static async Task<ToolWireResponseDto> ExecuteAsync(
        IContextMemoryService contextMemory,
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextMemory);

        string? query = null;
        var targetSessionId = scope.SessionId;
        bool? crossSession = null;
        int? limit = null;
        string? fidelity = null;

        if (wire.Params is { ValueKind: JsonValueKind.Object } parameters)
        {
            if (parameters.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                query = q.GetString()?.Trim();

            if (parameters.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                var rawSid = sid.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(rawSid))
                {
                    if (!Guid.TryParse(rawSid, out targetSessionId) || targetSessionId == Guid.Empty)
                    {
                        return Failure(wire, $"Invalid session_id '{rawSid}'; expected a session GUID.");
                    }
                }
            }

            if (parameters.TryGetProperty("cross_session", out var cs) && cs.ValueKind is JsonValueKind.True or JsonValueKind.False)
                crossSession = cs.GetBoolean();

            if (parameters.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number && lim.TryGetInt32(out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 30);

            if (parameters.TryGetProperty("fidelity", out var fid) && fid.ValueKind == JsonValueKind.String)
                fidelity = fid.GetString()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return Failure(wire, "recall_conversation_history requires a non-empty 'query'.");
        }

        try
        {
            var result = await contextMemory.RecallConversationHistoryAsync(
                targetSessionId,
                new ConversationRecallRequest
                {
                    Query = query,
                    CrossSession = crossSession,
                    Limit = limit,
                    Fidelity = fidelity
                },
                callerProjectId: CoreVirtualToolPolicy.FromWireSentinel(scope.ProjectId),
                cancellationToken).ConfigureAwait(false);

            return new ToolWireResponseDto
            {
                CallId = wire.ToolCallId,
                IsSuccess = true,
                Result = JsonSerializer.SerializeToElement(result, SnakeJson)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return Failure(wire, ex.Message);
        }
    }

    private static ToolWireResponseDto Failure(ToolWireRequestDto wire, string error) =>
        new() { CallId = wire.ToolCallId, IsSuccess = false, Error = error };
}
