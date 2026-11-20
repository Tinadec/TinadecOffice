using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The boundary between a model's capability profile (<c>model_profiles</c>) and its inference
/// settings (<c>model_parameters</c>). They live at the same level of the provider configuration and
/// are easy to conflate, but <see cref="ModelParameters"/> is
/// <c>[JsonUnmappedMemberHandling(Disallow)]</c>: a profile written into it fails validation for
/// every provider save, so the two must stay strictly separate.
/// </summary>
public sealed class ModelProfileTests
{
    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ReadMap_ReadsAProfileKeyedByModelId()
    {
        var profiles = ModelProfile.ReadMap(Json("""
            {
              "gpt-4o-mini": {
                "specialties": ["代码审查", "长文档"],
                "tier": "fast",
                "notes": "cheap and quick",
                "context_window": 128000,
                "cost_tier": "low"
              }
            }
            """));

        var profile = Assert.Single(profiles).Value;
        Assert.Equal(new[] { "代码审查", "长文档" }, profile.Specialties.ToArray());
        Assert.Equal("fast", profile.Tier);
        Assert.Equal("cheap and quick", profile.Notes);
        Assert.Equal(128000, profile.ContextWindow!.Value);
        Assert.Equal("low", profile.CostTier);
    }

    [Fact]
    public void ReadMap_AbsentOrNullIsAnEmptyMap_NotAnError()
    {
        Assert.Empty(ModelProfile.ReadMap(Json("null")));
        Assert.Empty(ModelProfile.ReadMap(default));
    }

    [Fact]
    public void ReadMap_RejectsANonObjectMap()
    {
        var error = Assert.Throws<ArgumentException>(() => ModelProfile.ReadMap(Json("[]")));
        Assert.Contains("model_profiles", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadMap_RejectsAnUnknownField_SoATypoCannotSilentlyDropData()
    {
        // A profile field spelled wrong would otherwise be discarded and the operator would see a
        // saved profile that lost the value they typed.
        var error = Assert.Throws<ArgumentException>(() =>
            ModelProfile.ReadMap(Json("""{"m":{"specialities":["x"]}}""")));
        Assert.Contains("specialities", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"m":{"tier":"turbo"}}""")]
    [InlineData("""{"m":{"cost_tier":"free"}}""")]
    [InlineData("""{"m":{"context_window":0}}""")]
    [InlineData("""{"m":{"context_window":-5}}""")]
    public void ReadMap_RejectsAnInvalidTierCostTierOrContextWindow(string raw)
    {
        Assert.Throws<ArgumentException>(() => ModelProfile.ReadMap(Json(raw)));
    }

    [Fact]
    public void ReadMap_TreatsEmptyNotesAsNull()
    {
        var profile = Assert.Single(ModelProfile.ReadMap(Json("""{"m":{"notes":"   "}}"""))).Value;
        Assert.Null(profile.Notes);
    }

    [Fact]
    public void ReadMap_RejectsMoreSpecialtiesThanTheCap()
    {
        var specialties = string.Join(',', Enumerable.Range(0, ModelProfile.MaxSpecialties + 1).Select(index => $"\"s{index}\""));
        // Plain concatenation, not an interpolated raw string: the JSON ends in three consecutive
        // '}' and a `$$"""..."""` literal cannot carry a brace run longer than its '$' count
        // (CS9007). A fixed raw string is a readability win; an interpolation inside nested JSON is
        // a brace-counting footgun.
        var raw = "{\"m\":{\"specialties\":[" + specialties + "]}}";

        Assert.Throws<ArgumentException>(() => ModelProfile.ReadMap(Json(raw)));
    }

    [Fact]
    public void Normalize_DeduplicatesSpecialtiesCaseInsensitively_AndDefaultsTier()
    {
        var normalized = ModelProfile.Normalize(new ModelProfileDto
        {
            Specialties = ["review", "Review", " review ", "plan"],
            Tier = "",
            Notes = "   ",
            CostTier = "  "
        });

        Assert.Equal(new[] { "review", "plan" }, normalized.Specialties.ToArray());
        Assert.Equal("balanced", normalized.Tier);
        Assert.Null(normalized.Notes);
        Assert.Null(normalized.CostTier);
    }

    [Fact]
    public void ForModel_ReadsOnlyTheNamedModel()
    {
        var config = Json("""{"model_profiles":{"a":{"tier":"deep"},"b":{"tier":"fast"}}}""");

        Assert.Equal("deep", ModelProfile.ForModel(config, "a")!.Tier);
        Assert.Equal("fast", ModelProfile.ForModel(config, "b")!.Tier);
        Assert.Null(ModelProfile.ForModel(config, "c"));
        Assert.Null(ModelProfile.ForModel(config, null));
    }

    [Fact]
    public void ForModel_OnAConfigWithoutProfiles_IsNull_NotAnError()
    {
        Assert.Null(ModelProfile.ForModel(Json("""{"base_url":"https://api.example.com/v1"}"""), "a"));
    }

    [Fact]
    public void ModelParameters_RejectsAProfileNestedInsideIt()
    {
        // This is the guard the product depends on: `model_profiles` must never be written inside
        // `model_parameters`, because Disallow turns it into a hard provider-save failure.
        var error = Assert.ThrowsAny<Exception>(() => ModelParameters.ReadMap(Json("""
            {"gpt-4o-mini":{"model_profiles":{"gpt-4o-mini":{"tier":"fast"}}}}
            """)));

        Assert.Contains("model_profiles", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelParameters_StillAcceptsTheRealInferenceSettings()
    {
        var settings = ModelParameters.ReadMap(Json("""
            {"gpt-4o-mini":{"temperature":0.2,"top_p":0.9,"max_output_tokens":2048}}
            """));

        var single = Assert.Single(settings).Value;
        Assert.Equal(0.2f, single.Temperature!.Value);
        Assert.Equal(0.9f, single.TopP!.Value);
        Assert.Equal(2048, single.MaxOutputTokens!.Value);
    }

    [Fact]
    public void ProfileAndParameters_CoexistAtTheSameConfigLevel_WithoutInterfering()
    {
        // Both maps are top-level members of one provider config. Reading either must not disturb
        // the other, and the profile must not be visible to ModelParameters.
        var config = Json("""
            {
              "base_url": "https://api.example.com/v1",
              "model_parameters": { "gpt-4o-mini": { "temperature": 0.2 } },
              "model_profiles": { "gpt-4o-mini": { "tier": "fast", "specialties": ["chat"] } }
            }
            """);

        Assert.Equal(0.2f, ModelParameters.ForModel(config, "gpt-4o-mini")!.Temperature!.Value);
        Assert.Equal("fast", ModelProfile.ForModel(config, "gpt-4o-mini")!.Tier);
    }
}
