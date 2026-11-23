using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Memory;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

public sealed class ContextStrategyOverlayPrecedenceTests
{
    private static FrozenRunConfigurationV1 BuildConfig(
        ContextPolicy? context = null,
        MemoryPolicy? memory = null,
        ModeRuntimePolicyDto? runtimePolicy = null,
        SpaceRunOptions? defaultOptions = null,
        SpaceRunOptions? spaceOptions = null) =>
        new(
            FrozenRunConfigurationV1.CurrentSchemaVersion,
            "baseline-hash",
            1,
            Guid.NewGuid(),
            "mode:test:1",
            "default",
            new SpawnPolicy(2, 8, 4),
            new SchedulingPolicy(2, 1, true),
            new SupervisionPolicy(false, 1),
            context ?? new ContextPolicy(65536, 24, true)
            {
                Strategy = "managed",
                CompactionTriggerRatio = 0.75,
                RecallFidelity = "balanced",
                AllowCrossSessionRecall = true
            },
            memory ?? new MemoryPolicy(false, 8, ["workspace"], ["fact"])
            {
                EvolutionMode = "auto_evolve",
                CoreMarkdownMaxTokens = 1500
            },
            new ToolRuntimePolicy("none", true, true, 60, 0),
            [],
            [],
            [])
        {
            RuntimePolicy = runtimePolicy,
            DefaultOptions = defaultOptions,
            SpaceOptions = spaceOptions
        };

    [Fact]
    public void ThreeTierPrecedence_ResolvesWorkspaceThenModeThenSessionOverlay_PerParameter()
    {
        // Priority 3: Workspace baseline applies when neither Mode nor Session overlay sets values
        var baselineOnly = BuildConfig();
        Assert.Equal("managed", FullDuplexRunEngine.ResolveEffectiveContextStrategy(baselineOnly));
        Assert.Equal(0.75, FullDuplexRunEngine.ResolveEffectiveCompactionRatio(baselineOnly));
        Assert.Equal("balanced", FullDuplexRunEngine.ResolveEffectiveRecallPolicy(baselineOnly));
        Assert.Equal(24, FullDuplexRunEngine.ResolveEffectiveRecentMessageLimit(baselineOnly));
        Assert.True(FullDuplexRunEngine.ResolveEffectiveAllowCrossSessionRecall(baselineOnly));
        Assert.Equal("auto_evolve", FullDuplexRunEngine.ResolveEffectiveEvolutionMode(baselineOnly));
        Assert.Equal(1500, FullDuplexRunEngine.ResolveEffectiveCoreMarkdownMaxTokens(baselineOnly));

        // Priority 2b: Mode runtime_policy.settings overrides workspace baseline
        var policySettings = JsonDocument.Parse("""
            {
              "context_strategy": "deep_curator",
              "compaction_trigger_ratio": 0.82,
              "recall_policy": "summary_only",
              "recent_message_limit": 48,
              "allow_cross_session_recall": false,
              "memory_evolution_mode": "candidate_only",
              "core_markdown_max_tokens": 2400
            }
            """).RootElement.Clone();
        var withRuntimePolicy = BuildConfig(runtimePolicy: new ModeRuntimePolicyDto
        {
            Kind = ModeRuntimePolicyRegistry.StructuredInvestigationKind,
            Version = 1,
            Features = [CorrectionRuntimePolicy.CausalTraceFeature],
            Settings = policySettings
        });
        Assert.Equal("deep_curator", FullDuplexRunEngine.ResolveEffectiveContextStrategy(withRuntimePolicy));
        Assert.Equal(0.82, FullDuplexRunEngine.ResolveEffectiveCompactionRatio(withRuntimePolicy));
        Assert.Equal("summary_only", FullDuplexRunEngine.ResolveEffectiveRecallPolicy(withRuntimePolicy));
        Assert.Equal(48, FullDuplexRunEngine.ResolveEffectiveRecentMessageLimit(withRuntimePolicy));
        Assert.False(FullDuplexRunEngine.ResolveEffectiveAllowCrossSessionRecall(withRuntimePolicy));
        Assert.Equal("candidate_only", FullDuplexRunEngine.ResolveEffectiveEvolutionMode(withRuntimePolicy));
        Assert.Equal(2400, FullDuplexRunEngine.ResolveEffectiveCoreMarkdownMaxTokens(withRuntimePolicy));

        // Priority 2a: Mode default_options overrides runtime_policy.settings and workspace baseline
        var modeDefaults = new SpaceRunOptions(
            ContextStrategy: "managed",
            ContextAdvanced: new ContextPolicyParamsOverride(
                RecallFidelity: "verbatim_first",
                CompactionTriggerRatio: 0.65,
                RecentMessageLimit: 80,
                AllowCrossSessionRecall: true,
                EvolutionMode: "auto_evolve",
                CoreMarkdownMaxTokens: 4096));
        var withModeDefaults = BuildConfig(
            runtimePolicy: withRuntimePolicy.RuntimePolicy,
            defaultOptions: modeDefaults);
        Assert.Equal("managed", FullDuplexRunEngine.ResolveEffectiveContextStrategy(withModeDefaults));
        Assert.Equal(0.65, FullDuplexRunEngine.ResolveEffectiveCompactionRatio(withModeDefaults));
        Assert.Equal("verbatim_first", FullDuplexRunEngine.ResolveEffectiveRecallPolicy(withModeDefaults));
        Assert.Equal(80, FullDuplexRunEngine.ResolveEffectiveRecentMessageLimit(withModeDefaults));
        Assert.True(FullDuplexRunEngine.ResolveEffectiveAllowCrossSessionRecall(withModeDefaults));
        Assert.Equal("auto_evolve", FullDuplexRunEngine.ResolveEffectiveEvolutionMode(withModeDefaults));
        Assert.Equal(4096, FullDuplexRunEngine.ResolveEffectiveCoreMarkdownMaxTokens(withModeDefaults));

        // Priority 1: Session overlay overrides specified fields while null fields inherit from Mode defaults
        var sessionOverlay = new SpaceRunOptions(
            PlanFirst: true,
            ContextStrategy: "direct",
            ContextAdvanced: new ContextPolicyParamsOverride(
                CompactionTriggerRatio: 0.50,
                RecentMessageLimit: 12,
                EvolutionMode: "candidate_only"));
        var withOverlay = BuildConfig(
            runtimePolicy: withRuntimePolicy.RuntimePolicy,
            defaultOptions: modeDefaults,
            spaceOptions: sessionOverlay);
        Assert.Equal("direct", FullDuplexRunEngine.ResolveEffectiveContextStrategy(withOverlay));
        Assert.Equal(0.50, FullDuplexRunEngine.ResolveEffectiveCompactionRatio(withOverlay));
        Assert.Equal("verbatim_first", FullDuplexRunEngine.ResolveEffectiveRecallPolicy(withOverlay));
        Assert.Equal(12, FullDuplexRunEngine.ResolveEffectiveRecentMessageLimit(withOverlay));
        Assert.True(FullDuplexRunEngine.ResolveEffectiveAllowCrossSessionRecall(withOverlay));
        Assert.Equal("candidate_only", FullDuplexRunEngine.ResolveEffectiveEvolutionMode(withOverlay));
        Assert.Equal(4096, FullDuplexRunEngine.ResolveEffectiveCoreMarkdownMaxTokens(withOverlay));
    }

    [Fact]
    public void NormalizeDefaultOptionsJson_CanonicalizesValidFields_OmitsEmptyAdvanced_AndRejectsInvalidBounds()
    {
        using var plainDoc = JsonDocument.Parse("""{"plan_first":true,"multi_agent":false}""");
        var plainNormalized = GraphValidation.NormalizeDefaultOptionsJson(plainDoc.RootElement);
        Assert.Equal("""{"bulletin_board":false,"multi_agent":false,"plan_first":true,"spec_enabled":false,"worktree":false}""", plainNormalized);

        using var emptyAdvDoc = JsonDocument.Parse("""{"plan_first":false,"context_strategy":null,"context_advanced":{"recall_fidelity":null}}""");
        var emptyAdvNormalized = GraphValidation.NormalizeDefaultOptionsJson(emptyAdvDoc.RootElement);
        Assert.Equal("""{"bulletin_board":false,"multi_agent":false,"plan_first":false,"spec_enabled":false,"worktree":false}""", emptyAdvNormalized);

        using var richDoc = JsonDocument.Parse("""
            {
              "planFirst": true,
              "contextStrategy": "DEEP_CURATOR",
              "contextAdvanced": {
                "recallFidelity": "Verbatim_First",
                "compactionTriggerRatio": 0.85,
                "recentMessageLimit": 64,
                "allowCrossSessionRecall": false,
                "evolutionMode": "candidate_only",
                "coreMarkdownMaxTokens": 3200
              }
            }
            """);
        var richNormalized = GraphValidation.NormalizeDefaultOptionsJson(richDoc.RootElement);
        Assert.NotNull(richNormalized);
        using var parsedRich = JsonDocument.Parse(richNormalized!);
        Assert.Equal("deep_curator", parsedRich.RootElement.GetProperty("context_strategy").GetString());
        var adv = parsedRich.RootElement.GetProperty("context_advanced");
        Assert.Equal("verbatim_first", adv.GetProperty("recall_fidelity").GetString());
        Assert.Equal(0.85, adv.GetProperty("compaction_trigger_ratio").GetDouble());
        Assert.Equal(64, adv.GetProperty("recent_message_limit").GetInt32());
        Assert.False(adv.GetProperty("allow_cross_session_recall").GetBoolean());
        Assert.Equal("candidate_only", adv.GetProperty("evolution_mode").GetString());
        Assert.Equal(3200, adv.GetProperty("core_markdown_max_tokens").GetInt32());

        string[] invalidPayloads =
        [
            """{"context_strategy":"turbo"}""",
            """{"context_advanced":{"compaction_trigger_ratio":0.35}}""",
            """{"context_advanced":{"compaction_trigger_ratio":0.99}}""",
            """{"context_advanced":{"recent_message_limit":2}}""",
            """{"context_advanced":{"recent_message_limit":250}}""",
            """{"context_advanced":{"core_markdown_max_tokens":128}}""",
            """{"context_advanced":{"core_markdown_max_tokens":9000}}""",
            """{"context_advanced":{"recall_fidelity":"everything"}}""",
            """{"context_advanced":{"evolution_mode":"yolo"}}""",
            """{"context_advanced":{"unknown_knob":1}}"""
        ];
        foreach (var bad in invalidPayloads)
        {
            using var badDoc = JsonDocument.Parse(bad);
            Assert.Throws<InvalidDataException>(() => GraphValidation.NormalizeDefaultOptionsJson(badDoc.RootElement));
        }
    }

    [Fact]
    public void FormalModeResolver_LegacyCanvasLayoutFallback_AppliesOnlyWhenDefaultOptionsAndRuntimePolicyOmit()
    {
        var legacyOnlyVersion = new ModeVersionRecord
        {
            Id = Guid.NewGuid(),
            AgentModeId = Guid.NewGuid(),
            SnapshotJson = """
                {
                  "canvas_layout": {
                    "zoom": 1,
                    "context_strategy": "deep_curator",
                    "context_advanced": {
                      "recall_fidelity": "verbatim_first",
                      "compaction_trigger_ratio": 0.88,
                      "recent_message_limit": 50,
                      "allow_cross_session_recall": true,
                      "evolution_mode": "candidate_only",
                      "core_markdown_max_tokens": 2048
                    }
                  }
                }
                """
        };
        var (legacyParsed, isLegacyOnly) = FormalModeResolver.ParseDefaultOptionsWithLegacyFallback(legacyOnlyVersion);
        Assert.True(isLegacyOnly);
        Assert.NotNull(legacyParsed);
        Assert.Equal("deep_curator", legacyParsed!.ContextStrategy);
        Assert.Equal("verbatim_first", legacyParsed.ContextAdvanced?.RecallFidelity);
        Assert.Equal(0.88, legacyParsed.ContextAdvanced?.CompactionTriggerRatio);
        Assert.Equal(50, legacyParsed.ContextAdvanced?.RecentMessageLimit);
        Assert.Equal(true, legacyParsed.ContextAdvanced?.AllowCrossSessionRecall);
        Assert.Equal("candidate_only", legacyParsed.ContextAdvanced?.EvolutionMode);
        Assert.Equal(2048, legacyParsed.ContextAdvanced?.CoreMarkdownMaxTokens);

        // When runtime_policy.settings provides context_strategy / compaction_trigger_ratio, canvas_layout does NOT override those
        var policyWithStrategy = new ModeRuntimePolicyDto
        {
            Kind = ModeRuntimePolicyRegistry.StructuredInvestigationKind,
            Version = 1,
            Features = [CorrectionRuntimePolicy.CausalTraceFeature],
            Settings = JsonDocument.Parse("""{"context_strategy":"direct","compaction_trigger_ratio":0.55}""").RootElement.Clone()
        };
        var (withPolicyParsed, _) = FormalModeResolver.ParseDefaultOptionsWithLegacyFallback(legacyOnlyVersion, policyWithStrategy);
        Assert.Null(withPolicyParsed!.ContextStrategy);
        Assert.Null(withPolicyParsed.ContextAdvanced?.CompactionTriggerRatio);
        Assert.Equal("verbatim_first", withPolicyParsed.ContextAdvanced?.RecallFidelity);

        // When default_options provides explicit values, default_options wins over legacy canvas_layout
        var migratedVersion = new ModeVersionRecord
        {
            Id = Guid.NewGuid(),
            AgentModeId = Guid.NewGuid(),
            SnapshotJson = """
                {
                  "default_options": {
                    "plan_first": true,
                    "multi_agent": true,
                    "bulletin_board": false,
                    "spec_enabled": false,
                    "worktree": false,
                    "context_strategy": "managed",
                    "context_advanced": {
                      "recall_fidelity": "summary_only"
                    }
                  },
                  "canvas_layout": {
                    "context_strategy": "deep_curator",
                    "context_advanced": {
                      "recall_fidelity": "verbatim_first",
                      "recent_message_limit": 72
                    }
                  }
                }
                """
        };
        var (migratedParsed, migratedLegacyOnly) = FormalModeResolver.ParseDefaultOptionsWithLegacyFallback(migratedVersion);
        Assert.False(migratedLegacyOnly);
        Assert.NotNull(migratedParsed);
        Assert.True(migratedParsed!.PlanFirst);
        Assert.True(migratedParsed.MultiAgent);
        Assert.Equal("managed", migratedParsed.ContextStrategy);
        Assert.Equal("summary_only", migratedParsed.ContextAdvanced?.RecallFidelity);
        Assert.Equal(72, migratedParsed.ContextAdvanced?.RecentMessageLimit);
    }

    [Fact]
    public void SpaceRunOptionsJson_And_SessionValidation_SupportSnakeAndCamelCase_AndRejectInvalidValues()
    {
        using var camelDoc = JsonDocument.Parse("""
            {
              "planFirst": true,
              "contextStrategy": "managed",
              "contextAdvanced": {
                "recallFidelity": "balanced",
                "compactionTriggerRatio": 0.70,
                "recentMessageLimit": 32,
                "allowCrossSessionRecall": false,
                "evolutionMode": "candidate_only",
                "coreMarkdownMaxTokens": 2000
              }
            }
            """);
        var camel = SpaceRunOptionsJson.Parse(camelDoc.RootElement);
        ProjectSessionStore.ValidateSessionSettings("flat", "default", camel);
        Assert.Equal("managed", camel.ContextStrategy);
        Assert.Equal("candidate_only", camel.ContextAdvanced?.EvolutionMode);

        using var unknownFieldDoc = JsonDocument.Parse("""{"plan_first":false,"bogus":true}""");
        Assert.Throws<JsonException>(() => SpaceRunOptionsJson.Parse(unknownFieldDoc.RootElement));

        Assert.Throws<ArgumentException>(() => ProjectSessionStore.ValidateSessionSettings(
            "flat", "default", new SpaceRunOptions(ContextStrategy: "invalid")));
        Assert.Throws<ArgumentException>(() => ProjectSessionStore.ValidateSessionSettings(
            "flat", "default", new SpaceRunOptions(ContextAdvanced: new ContextPolicyParamsOverride(CompactionTriggerRatio: 0.20))));

        var strategySvc = new ContextStrategyService();
        var profile = strategySvc.ResolveProfile(
            strategyOverride: "managed",
            policy: null,
            compactionTriggerRatioOverride: 0.68,
            recallPolicyOverride: "verbatim_first",
            allowCrossSessionRecallOverride: true,
            evolutionModeOverride: "candidate_only");
        Assert.Equal("managed", profile.Strategy);
        Assert.True(profile.CrossSessionRecallEnabled);
        Assert.False(profile.ActiveMemoryEvolutionEnabled);
    }
}
