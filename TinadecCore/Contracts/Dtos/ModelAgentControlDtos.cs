using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public static class ModelStrategyKinds
{
    public const string Inherit = "inherit";
    public const string Route = "route";
    public const string Fixed = "fixed";
}

public sealed class ModelStrategyDto
{
    public string Kind { get; init; } = ModelStrategyKinds.Inherit;
    public string? RoutePurpose { get; init; }
    public Guid? ProviderInstanceId { get; init; }
    public string? Model { get; init; }
}

public sealed class ModelRouteCandidateDto
{
    public Guid ProviderInstanceId { get; init; }
    public string? Model { get; init; }
    public int Position { get; init; }
}

public sealed class ModelRouteWriteRequestDto
{
    public IReadOnlyList<ModelRouteCandidateDto> Candidates { get; init; } = [];
}

public sealed class ModelRouteDto
{
    public Guid Id { get; init; }
    public string Purpose { get; init; } = string.Empty;
    public Guid VersionId { get; init; }
    public int Version { get; init; }
    public IReadOnlyList<ModelRouteCandidateDto> Candidates { get; init; } = [];
    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class MeetingModelOverrideDto
{
    public Guid ProviderInstanceId { get; init; }
    public string? Model { get; init; }
}

public sealed class ModelResolutionPreviewRequestDto
{
    public ModelStrategyDto? Strategy { get; init; }
    public MeetingModelOverrideDto? MeetingModelOverride { get; init; }
    public Guid? AgentDefinitionId { get; init; }
    public Guid? AgentVersionId { get; init; }
    public Guid? ModeVersionId { get; init; }
    public string? NodeKey { get; init; }
    public Guid? ParentInstanceId { get; init; }
}

public sealed class ModelResolutionStepDto
{
    public string Source { get; init; } = string.Empty;
    public ModelStrategyDto Strategy { get; init; } = new();
    public bool Selected { get; init; }
}

public sealed class ModelResolutionCandidatePreviewDto
{
    public int Position { get; init; }
    public Guid ProviderInstanceId { get; init; }
    public Guid? ProviderVersionId { get; init; }
    public Guid? RouteId { get; init; }
    public Guid? RouteVersionId { get; init; }
    public string? Model { get; init; }
    public string? Protocol { get; init; }
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
}

public sealed class ModelResolutionPreviewDto
{
    public string StrategySource { get; init; } = string.Empty;
    public IReadOnlyList<ModelResolutionStepDto> Chain { get; init; } = [];
    public IReadOnlyList<ModelResolutionCandidatePreviewDto> Candidates { get; init; } = [];
    public ModelResolutionCandidatePreviewDto? ExpectedSelection { get; init; }
}

public sealed class ModelReferenceDto
{
    public string ReferenceKind { get; init; } = string.Empty;
    public Guid? ReferenceId { get; init; }
    public string? ReferenceKey { get; init; }
    public Guid ProviderInstanceId { get; init; }
    public string? Model { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}

public sealed class ModelInvocationDto
{
    public Guid Id { get; init; }
    public Guid CallId { get; init; }
    public int Attempt { get; init; }
    public Guid SessionId { get; init; }
    public Guid RunId { get; init; }
    public Guid? TurnId { get; init; }
    public Guid? AgentInstanceId { get; init; }
    public Guid AgentDefinitionId { get; init; }
    public Guid AgentVersionId { get; init; }
    public Guid ModeVersionId { get; init; }
    public string StrategySource { get; init; } = string.Empty;
    public Guid? RouteId { get; init; }
    public Guid? RouteVersionId { get; init; }
    public Guid ProviderInstanceId { get; init; }
    public Guid ProviderVersionId { get; init; }
    public string? Model { get; init; }
    public string Protocol { get; init; } = string.Empty;
    public int FallbackPosition { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ErrorCategory { get; init; }
    public string? SafeErrorMessage { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class ModelInvocationPageDto
{
    public IReadOnlyList<ModelInvocationDto> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed class AgentModeUsageDto
{
    public Guid ModeId { get; init; }
    public Guid? ModeVersionId { get; init; }
    public string ModeSlug { get; init; } = string.Empty;
    public string NodeKey { get; init; } = string.Empty;
    public ModelStrategyDto? ModelStrategyOverride { get; init; }
}

public sealed class AgentDirectoryItemDto
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Layer { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public bool Managed { get; init; }
    public bool Writable { get; init; }
    public bool Enabled { get; init; }
    public string Status { get; init; } = string.Empty;
    public long Revision { get; init; }
    public int Version { get; init; }
    public Guid? CurrentVersionId { get; init; }
    public ModelStrategyDto ConfiguredStrategy { get; init; } = new();
    public IReadOnlyList<AgentModeUsageDto> ModeUsages { get; init; } = [];
    public IReadOnlyDictionary<string, ModelResolutionPreviewDto> EffectivePreviews { get; init; } = new Dictionary<string, ModelResolutionPreviewDto>();
    public ModelInvocationDto? RecentInvocation { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>用户级运行时绑定覆盖（null = 未覆盖，跟随 agent 定义/默认路由）。</summary>
    public AgentRuntimeBindingDto? ModelBinding { get; init; }
}

public sealed class AgentRuntimeBindingDto
{
    /// <summary>inherit | route | fixed</summary>
    public string Mode { get; init; } = "inherit";
    public Guid? ProviderInstanceId { get; init; }
    public string? Model { get; init; }
    /// <summary>mode == route 时的 model_routes.purpose；其他模式为 null。</summary>
    public string? RoutePurpose { get; init; }
    public IReadOnlyList<string>? ToolScopeOverride { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public static class ModelStrategyJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static ModelStrategyDto Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ModelStrategyDto();
        return Parse(JsonSerializer.Deserialize<JsonElement>(json));
    }

    public static ModelStrategyDto Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("model_strategy must be an object.");
        var kind = value.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String
            ? kindValue.GetString()?.Trim().ToLowerInvariant()
            : null;
        return kind switch
        {
            ModelStrategyKinds.Inherit => new ModelStrategyDto { Kind = ModelStrategyKinds.Inherit },
            ModelStrategyKinds.Route => new ModelStrategyDto
            {
                Kind = ModelStrategyKinds.Route,
                RoutePurpose = RequiredString(value, "route_purpose")
            },
            ModelStrategyKinds.Fixed => new ModelStrategyDto
            {
                Kind = ModelStrategyKinds.Fixed,
                ProviderInstanceId = RequiredGuid(value, "provider_instance_id"),
                Model = OptionalString(value, "model")
            },
            _ => throw new ArgumentException("model_strategy.kind must be inherit|route|fixed.")
        };
    }

    public static string Serialize(ModelStrategyDto value) => JsonSerializer.Serialize(value, Options);

    private static string RequiredString(JsonElement value, string property) =>
        OptionalString(value, property) is { Length: > 0 } text ? text : throw new ArgumentException($"model_strategy.{property} is required.");

    private static string? OptionalString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;

    private static Guid RequiredGuid(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id) && id != Guid.Empty
            ? id
            : throw new ArgumentException($"model_strategy.{property} must be a non-empty UUID.");
}
