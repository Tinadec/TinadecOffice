using System.Text.Json;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Validation and normalization for per-model ability profiles stored at the top level of
/// provider configuration (<c>model_profiles</c>, outside <c>model_parameters</c>).
/// </summary>
public static class ModelProfile
{
    public const int MaxNotesLength = 500;
    public const int MaxSpecialties = 32;
    public const int MaxSpecialtyLength = 64;
    public const int MaxCurrencyLength = 16;

    public static IReadOnlyDictionary<string, ModelProfileDto> ReadMap(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new Dictionary<string, ModelProfileDto>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("model_profiles must be an object keyed by model id.");

        var result = new Dictionary<string, ModelProfileDto>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name != entry.Name.Trim() || entry.Name.Length > 256)
                throw new ArgumentException("model_profiles requires a non-empty model id of at most 256 characters.");
            var parsed = ReadEntry(entry.Name, entry.Value);
            if (!result.TryAdd(entry.Name, parsed))
                throw new ArgumentException("model_profiles cannot contain duplicate model ids.");
        }
        return result;
    }

    public static ModelProfileDto? ForModel(JsonElement providerConfig, string? model) =>
        model is not null
        && providerConfig.TryGetProperty("model_profiles", out var map)
        && ReadMap(map).TryGetValue(model, out var profile)
            ? profile
            : null;

    public static ModelProfileDto Normalize(ModelProfileDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var specialties = NormalizeSpecialties(input.Specialties);
        var tier = NormalizeTier(input.Tier);
        var notes = NormalizeNotes(input.Notes);
        if (input.ContextWindow is { } cw && cw <= 0)
            throw new ArgumentException("model_profiles.context_window must be a positive integer.");
        var costTier = NormalizeCostTier(input.CostTier);
        var inputPrice = NormalizePrice(input.InputPricePer1M, "input_price_per_1m");
        var outputPrice = NormalizePrice(input.OutputPricePer1M, "output_price_per_1m");
        var currency = NormalizeCurrency(input.Currency);
        return new ModelProfileDto
        {
            Specialties = specialties,
            Tier = tier,
            Notes = notes,
            ContextWindow = input.ContextWindow,
            CostTier = costTier,
            InputPricePer1M = inputPrice,
            OutputPricePer1M = outputPrice,
            Currency = currency
        };
    }

    /// <summary>
    /// Derives multimodal image input support from explicit <c>image_input</c> configuration,
    /// falling back to <c>model_profiles.specialties</c> (<c>"vision"</c>) when no explicit
    /// provider or model-level <c>image_input.enabled</c> override is present.
    /// </summary>
    public static ImageInputPolicy ResolveImageInput(JsonElement providerConfig, string? protocol, string? model)
    {
        var policy = ImageInputCapability.Resolve(providerConfig, protocol, model);
        if (policy.Enabled) return policy;

        var normalized = ChatProtocols.Normalize(protocol);
        if (normalized is not (ChatProtocols.OpenAiChat or ChatProtocols.OpenAiResponses or ChatProtocols.AnthropicMessages))
            return policy;

        if (HasExplicitImageInputEnabled(providerConfig, model))
            return policy;

        ModelProfileDto? profile;
        try
        {
            profile = ForModel(providerConfig, model);
        }
        catch (ArgumentException)
        {
            return policy;
        }

        if (profile is not null && profile.Specialties.Any(s => string.Equals(s, "vision", StringComparison.OrdinalIgnoreCase)))
            return policy with { Enabled = true };

        return policy;
    }

    private static bool HasExplicitImageInputEnabled(JsonElement providerConfig, string? model)
    {
        if (providerConfig.ValueKind != JsonValueKind.Object
            || !providerConfig.TryGetProperty("image_input", out var imageInput)
            || imageInput.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (model is not null
            && imageInput.TryGetProperty("model_overrides", out var overrides)
            && overrides.ValueKind == JsonValueKind.Object
            && overrides.TryGetProperty(model, out var modelOverride)
            && modelOverride.ValueKind == JsonValueKind.Object
            && modelOverride.TryGetProperty("enabled", out var modelEnabled)
            && modelEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return true;
        }

        return imageInput.TryGetProperty("enabled", out var providerEnabled)
            && providerEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static ModelProfileDto ReadEntry(string modelId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"model_profiles.{modelId} must be an object.");

        IReadOnlyList<string> specialties = [];
        var tier = "balanced";
        string? notes = null;
        int? contextWindow = null;
        string? costTier = null;
        decimal? inputPrice = null;
        decimal? outputPrice = null;
        string? currency = null;

        foreach (var prop in element.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "specialties":
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        specialties = [];
                        break;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException($"model_profiles.{modelId}.specialties must be an array of strings.");
                    var list = new List<string>();
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                            throw new ArgumentException($"model_profiles.{modelId}.specialties must contain only strings.");
                        list.Add(item.GetString() ?? string.Empty);
                    }
                    specialties = NormalizeSpecialties(list);
                    break;

                case "tier":
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        tier = "balanced";
                        break;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"model_profiles.{modelId}.tier must be fast|balanced|deep.");
                    tier = NormalizeTier(prop.Value.GetString());
                    break;

                case "notes":
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        notes = null;
                        break;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"model_profiles.{modelId}.notes must be a string.");
                    notes = NormalizeNotes(prop.Value.GetString());
                    break;

                case "context_window":
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        contextWindow = null;
                        break;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var cw) || cw <= 0)
                        throw new ArgumentException($"model_profiles.{modelId}.context_window must be a positive integer.");
                    contextWindow = cw;
                    break;

                case "cost_tier":
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        costTier = null;
                        break;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"model_profiles.{modelId}.cost_tier must be low|medium|high.");
                    costTier = NormalizeCostTier(prop.Value.GetString());
                    break;

                case "input_price_per_1m":
                    inputPrice = ReadOptionalPrice(prop.Value, modelId, "input_price_per_1m");
                    break;

                case "output_price_per_1m":
                    outputPrice = ReadOptionalPrice(prop.Value, modelId, "output_price_per_1m");
                    break;

                case "currency":
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        currency = null;
                        break;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"model_profiles.{modelId}.currency must be a string.");
                    currency = NormalizeCurrency(prop.Value.GetString());
                    break;

                default:
                    throw new ArgumentException($"Unknown model_profiles field '{prop.Name}'.");
            }
        }

        return new ModelProfileDto
        {
            Specialties = specialties,
            Tier = tier,
            Notes = notes,
            ContextWindow = contextWindow,
            CostTier = costTier,
            InputPricePer1M = inputPrice,
            OutputPricePer1M = outputPrice,
            Currency = currency
        };
    }

    private static IReadOnlyList<string> NormalizeSpecialties(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return [];
        if (values.Count > MaxSpecialties)
            throw new ArgumentException($"model_profiles.specialties cannot contain more than {MaxSpecialties} entries.");
        var result = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            var trimmed = (raw ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxSpecialtyLength)
                throw new ArgumentException($"Each model_profiles specialty must be 1..{MaxSpecialtyLength} characters.");
            if (seen.Add(trimmed)) result.Add(trimmed);
        }
        return result;
    }

    private static string NormalizeTier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "balanced";
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "fast" or "balanced" or "deep"
            ? normalized
            : throw new ArgumentException("model_profiles.tier must be fast|balanced|deep.");
    }

    private static string? NormalizeNotes(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > MaxNotesLength)
            throw new ArgumentException($"model_profiles.notes must not exceed {MaxNotesLength} characters.");
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeCostTier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high"
            ? normalized
            : throw new ArgumentException("model_profiles.cost_tier must be low|medium|high.");
    }

    private static decimal? ReadOptionalPrice(JsonElement value, string modelId, string field)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var parsed))
            throw new ArgumentException($"model_profiles.{modelId}.{field} must be a non-negative number.");
        return NormalizePrice(parsed, field);
    }

    private static decimal? NormalizePrice(decimal? value, string field)
    {
        if (value is null) return null;
        if (value.Value < 0m)
            throw new ArgumentException($"model_profiles.{field} must be a non-negative number.");
        return value.Value;
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().ToUpperInvariant();
        if (trimmed.Length > MaxCurrencyLength)
            throw new ArgumentException($"model_profiles.currency must not exceed {MaxCurrencyLength} characters.");
        return trimmed;
    }
}

public sealed class ModelProfileException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public interface IModelProfileCatalogService
{
    Task<SanitizedModelCatalogDto> GetSanitizedCatalogAsync(
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    Task<ModelProfileUpdateResultDto> UpdateModelProfileAsync(
        Guid providerId,
        string modelId,
        ModelProfileDto? profile,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);
}
