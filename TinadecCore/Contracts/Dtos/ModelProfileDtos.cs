using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed record ModelProfileDto
{
    [JsonPropertyName("specialties")]
    public IReadOnlyList<string> Specialties { get; init; } = [];

    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "balanced";

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }

    [JsonPropertyName("context_window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextWindow { get; init; }

    [JsonPropertyName("cost_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CostTier { get; init; }

    [JsonPropertyName("input_price_per_1m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? InputPricePer1M { get; init; }

    [JsonPropertyName("output_price_per_1m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OutputPricePer1M { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

public sealed record ModelParametersSnapshotDto
{
    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }
}

public sealed record SanitizedProviderCatalogItemDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("driver")]
    public string Driver { get; init; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("channel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channel { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("connection_kind")]
    public string ConnectionKind { get; init; } = string.Empty;

    [JsonPropertyName("default_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultModel { get; init; }

    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models { get; init; } = [];

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    [JsonPropertyName("has_api_key")]
    public bool HasApiKey { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("model_profiles")]
    public IReadOnlyDictionary<string, ModelProfileDto> ModelProfiles { get; init; } = new Dictionary<string, ModelProfileDto>();

    [JsonPropertyName("model_parameters")]
    public IReadOnlyDictionary<string, ModelParametersSnapshotDto> ModelParameters { get; init; } = new Dictionary<string, ModelParametersSnapshotDto>();
}

public sealed record SanitizedModelRouteCandidateDto
{
    [JsonPropertyName("position")]
    public int Position { get; init; }

    [JsonPropertyName("provider_instance_id")]
    public Guid ProviderInstanceId { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }
}

public sealed record SanitizedModelRouteDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<SanitizedModelRouteCandidateDto> Candidates { get; init; } = [];
}

public sealed record SanitizedModelHealthDto
{
    [JsonPropertyName("provider_instance_id")]
    public Guid ProviderInstanceId { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("consecutive_failures")]
    public int ConsecutiveFailures { get; init; }

    [JsonPropertyName("is_cooling")]
    public bool IsCooling { get; init; }

    [JsonPropertyName("cooling_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CoolingUntil { get; init; }

    [JsonPropertyName("last_category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastCategory { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SanitizedModelCatalogDto
{
    [JsonPropertyName("providers")]
    public IReadOnlyList<SanitizedProviderCatalogItemDto> Providers { get; init; } = [];

    [JsonPropertyName("routes")]
    public IReadOnlyList<SanitizedModelRouteDto> Routes { get; init; } = [];

    [JsonPropertyName("health")]
    public IReadOnlyList<SanitizedModelHealthDto> Health { get; init; } = [];
}

public sealed record ModelProfileUpdateResultDto
{
    [JsonPropertyName("provider_instance_id")]
    public Guid ProviderInstanceId { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelProfileDto? Profile { get; init; }

    [JsonPropertyName("model_profiles")]
    public IReadOnlyDictionary<string, ModelProfileDto> ModelProfiles { get; init; } = new Dictionary<string, ModelProfileDto>();

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}
