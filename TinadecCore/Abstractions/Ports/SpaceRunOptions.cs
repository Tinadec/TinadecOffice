using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Optional per-session or per-mode overrides for context governance parameters.
/// Null members inherit from the next lower tier (Mode Defaults, then Workspace Defaults).
/// </summary>
public sealed record ContextPolicyParamsOverride(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecallFidelity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CompactionTriggerRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RecentMessageLimit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllowCrossSessionRecall = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EvolutionMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CoreMarkdownMaxTokens = null)
{
    [JsonIgnore]
    public bool IsEmpty =>
        RecallFidelity is null
        && CompactionTriggerRatio is null
        && RecentMessageLimit is null
        && AllowCrossSessionRecall is null
        && EvolutionMode is null
        && CoreMarkdownMaxTokens is null;

    public static ContextPolicyParamsOverride? Normalize(ContextPolicyParamsOverride? value)
    {
        if (value is null) return null;
        var normalized = new ContextPolicyParamsOverride(
            RecallFidelity: value.RecallFidelity?.Trim().ToLowerInvariant(),
            CompactionTriggerRatio: value.CompactionTriggerRatio,
            RecentMessageLimit: value.RecentMessageLimit,
            AllowCrossSessionRecall: value.AllowCrossSessionRecall,
            EvolutionMode: value.EvolutionMode?.Trim().ToLowerInvariant(),
            CoreMarkdownMaxTokens: value.CoreMarkdownMaxTokens);
        return normalized.IsEmpty ? null : normalized;
    }
}

/// <summary>
/// A user's composable choices for the next spatial task (and optional session/mode context
/// governance overrides). These are captured with the submitted message and frozen at admission;
/// changing the session never changes an admitted run.
/// </summary>
public sealed record SpaceRunOptions(
    bool PlanFirst = false,
    bool SpecEnabled = false,
    bool MultiAgent = false,
    Guid? WorkflowModeVersionId = null,
    bool BulletinBoard = false,
    bool Worktree = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContextStrategy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ContextPolicyParamsOverride? ContextAdvanced = null)
{
    public static SpaceRunOptions? Normalize(SpaceRunOptions? options) =>
        options is null
            ? null
            : options with
            {
                ContextStrategy = options.ContextStrategy?.Trim().ToLowerInvariant(),
                ContextAdvanced = ContextPolicyParamsOverride.Normalize(options.ContextAdvanced)
            };
}

/// <summary>
/// Dual snake_case and camelCase JSON parser for <see cref="SpaceRunOptions"/> and
/// <see cref="ContextPolicyParamsOverride"/>, rejecting unknown fields when requested.
/// </summary>
public static class SpaceRunOptionsJson
{
    public static SpaceRunOptions Parse(JsonElement element, bool rejectUnknownFields = true)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("space_options must be a JSON object.");

        bool planFirst = false, specEnabled = false, multiAgent = false, bulletinBoard = false, worktree = false;
        Guid? workflowModeVersionId = null;
        string? contextStrategy = null;
        ContextPolicyParamsOverride? contextAdvanced = null;

        foreach (var prop in element.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "plan_first" or "planFirst":
                    planFirst = ReadRequiredBoolean(prop);
                    break;
                case "spec_enabled" or "specEnabled":
                    specEnabled = ReadRequiredBoolean(prop);
                    break;
                case "multi_agent" or "multiAgent":
                    multiAgent = ReadRequiredBoolean(prop);
                    break;
                case "bulletin_board" or "bulletinBoard":
                    bulletinBoard = ReadRequiredBoolean(prop);
                    break;
                case "worktree":
                    worktree = ReadRequiredBoolean(prop);
                    break;
                case "workflow_mode_version_id" or "workflowModeVersionId":
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        workflowModeVersionId = null;
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String && Guid.TryParse(prop.Value.GetString(), out var parsedGuid))
                    {
                        workflowModeVersionId = parsedGuid;
                    }
                    else
                    {
                        throw new JsonException($"Property '{prop.Name}' must be a GUID string or null.");
                    }
                    break;
                case "context_strategy" or "contextStrategy":
                    contextStrategy = ReadNullableString(prop);
                    break;
                case "context_advanced" or "contextAdvanced":
                    contextAdvanced = ParseContextAdvanced(prop.Value, rejectUnknownFields);
                    break;
                default:
                    if (rejectUnknownFields)
                        throw new JsonException($"Unknown property '{prop.Name}' in space_options.");
                    break;
            }
        }

        return SpaceRunOptions.Normalize(new SpaceRunOptions(
            PlanFirst: planFirst,
            SpecEnabled: specEnabled,
            MultiAgent: multiAgent,
            WorkflowModeVersionId: workflowModeVersionId,
            BulletinBoard: bulletinBoard,
            Worktree: worktree,
            ContextStrategy: contextStrategy,
            ContextAdvanced: contextAdvanced))!;
    }

    public static ContextPolicyParamsOverride? ParseContextAdvanced(JsonElement element, bool rejectUnknownFields = true)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("context_advanced must be a JSON object or null.");

        string? recallFidelity = null;
        double? compactionTriggerRatio = null;
        int? recentMessageLimit = null;
        bool? allowCrossSessionRecall = null;
        string? evolutionMode = null;
        int? coreMarkdownMaxTokens = null;

        foreach (var prop in element.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "recall_fidelity" or "recallFidelity":
                    recallFidelity = ReadNullableString(prop);
                    break;
                case "compaction_trigger_ratio" or "compactionTriggerRatio":
                    compactionTriggerRatio = ReadNullableDouble(prop);
                    break;
                case "recent_message_limit" or "recentMessageLimit":
                    recentMessageLimit = ReadNullableInt32(prop);
                    break;
                case "allow_cross_session_recall" or "allowCrossSessionRecall":
                    allowCrossSessionRecall = ReadNullableBoolean(prop);
                    break;
                case "evolution_mode" or "evolutionMode":
                    evolutionMode = ReadNullableString(prop);
                    break;
                case "core_markdown_max_tokens" or "coreMarkdownMaxTokens":
                    coreMarkdownMaxTokens = ReadNullableInt32(prop);
                    break;
                default:
                    if (rejectUnknownFields)
                        throw new JsonException($"Unknown property '{prop.Name}' in context_advanced.");
                    break;
            }
        }

        return ContextPolicyParamsOverride.Normalize(new ContextPolicyParamsOverride(
            RecallFidelity: recallFidelity,
            CompactionTriggerRatio: compactionTriggerRatio,
            RecentMessageLimit: recentMessageLimit,
            AllowCrossSessionRecall: allowCrossSessionRecall,
            EvolutionMode: evolutionMode,
            CoreMarkdownMaxTokens: coreMarkdownMaxTokens));
    }

    private static bool ReadRequiredBoolean(JsonProperty prop) =>
        prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.Value.GetBoolean()
            : throw new JsonException($"Property '{prop.Name}' must be a boolean.");

    private static bool? ReadNullableBoolean(JsonProperty prop) =>
        prop.Value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True or JsonValueKind.False => prop.Value.GetBoolean(),
            _ => throw new JsonException($"Property '{prop.Name}' must be a boolean or null.")
        };

    private static string? ReadNullableString(JsonProperty prop) =>
        prop.Value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => prop.Value.GetString(),
            _ => throw new JsonException($"Property '{prop.Name}' must be a string or null.")
        };

    private static double? ReadNullableDouble(JsonProperty prop)
    {
        if (prop.Value.ValueKind == JsonValueKind.Null) return null;
        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var parsed))
            return parsed;
        throw new JsonException($"Property '{prop.Name}' must be a number or null.");
    }

    private static int? ReadNullableInt32(JsonProperty prop)
    {
        if (prop.Value.ValueKind == JsonValueKind.Null) return null;
        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var parsed))
            return parsed;
        throw new JsonException($"Property '{prop.Name}' must be an integer or null.");
    }
}
