using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Prompts;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The prompt pipeline rules the canvas and the preview both depend on. These are pure functions on
/// purpose: the ordering, the condition gate, the fragment-reference resolver and the token
/// estimator decide what a model is actually told, so each is asserted directly instead of being
/// inferred from a rendered prompt string.
/// </summary>
public sealed class PromptEngineTests
{
    private static PromptEvalContext Context(
        string layer = "execution",
        string modeSlug = "solo",
        bool hasWorkspace = true,
        bool hasTools = true,
        params string[] capabilities) =>
        new(
            AgentName: "Meeting",
            AgentSlug: "meeting",
            ModeName: "Solo",
            ModeSlug: modeSlug,
            Layer: layer,
            WorkspaceRoot: "/work/root",
            HasWorkspace: hasWorkspace,
            HasTools: hasTools,
            Capabilities: new HashSet<string>(capabilities, StringComparer.OrdinalIgnoreCase));

    private static List<PromptGraphNode> Nodes(params (string Id, string Kind)[] nodes)
    {
        var empty = Json("{}");
        return nodes.Select((node, index) => new PromptGraphNode(index, node.Id, node.Kind, node.Id, empty)).ToList();
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    // ---------------------------------------------------------------- topological order

    [Fact]
    public void TopologicalSort_FollowsEdges_NotDeclarationOrder()
    {
        // Declared assemble-first, template-last: only the edges can put them in a usable order.
        var nodes = Nodes(("assemble", "assemble"), ("template", "template"), ("condition", "condition"));
        var edges = new[]
        {
            new PromptGraphEdge("condition", "template"),
            new PromptGraphEdge("template", "assemble")
        };

        var ordered = PromptPipeline.TopologicalSort(nodes, edges, new List<string>());

        Assert.Equal(new[] { "condition", "template", "assemble" }, ordered.Select(node => node.Id).ToArray());
    }

    [Fact]
    public void TopologicalSort_WithoutEdges_KeepsDeclarationOrder()
    {
        var nodes = Nodes(("c", "template"), ("a", "template"), ("b", "template"));

        var ordered = PromptPipeline.TopologicalSort(nodes, [], new List<string>());

        Assert.Equal(new[] { "c", "a", "b" }, ordered.Select(node => node.Id).ToArray());
    }

    [Fact]
    public void TopologicalSort_WithCycle_FallsBackToDeclarationOrderAndWarns()
    {
        // A half-built canvas must still preview; a cycle is a warning, never a throw.
        var nodes = Nodes(("a", "template"), ("b", "template"), ("c", "template"));
        var edges = new[]
        {
            new PromptGraphEdge("a", "b"),
            new PromptGraphEdge("b", "a")
        };
        var warnings = new List<string>();

        var ordered = PromptPipeline.TopologicalSort(nodes, edges, warnings);

        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(node => node.Id).ToArray());
        Assert.Contains(warnings, warning => warning.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseEdges_DropsUnknownNodesSelfLoopsAndDuplicates()
    {
        var nodes = Nodes(("a", "template"), ("b", "template"));
        var root = Json("""
            {"edges":[
              {"source":"a","target":"b"},
              {"source":"a","target":"b"},
              {"source":"a","target":"a"},
              {"source":"a","target":"ghost"},
              {"source_node_key":"b","target_node_key":"a"}
            ]}
            """);

        var edges = PromptPipeline.ParseEdges(root, nodes);

        Assert.Equal(2, edges.Count);
        Assert.Contains(new PromptGraphEdge("a", "b"), edges);
        Assert.Contains(new PromptGraphEdge("b", "a"), edges);
    }

    // ---------------------------------------------------------------- fragment_ref resolution

    private static PromptFragmentCandidate Fragment(string key, Guid? id = null, string content = "body") =>
        new(id ?? Guid.NewGuid(), key, key, "workspace", null, null, "general", 0, true, content);

    [Fact]
    public void ResolveFragmentReference_PrefersId_OverKey()
    {
        var byKey = Fragment("shared-key", content: "from key");
        var byId = Fragment("renamed", content: "from id");
        var pool = new[] { byKey, byId };

        var resolved = PromptPipeline.ResolveFragmentReference(pool, byId.Id.ToString(), "shared-key");

        Assert.NotNull(resolved);
        Assert.Equal("from id", resolved!.Content);
    }

    [Fact]
    public void ResolveFragmentReference_FallsBackToKey_WhenIdIsNotAGuid()
    {
        var pool = new[] { Fragment("tone-guide", content: "be terse") };

        var resolved = PromptPipeline.ResolveFragmentReference(pool, null, "TONE-GUIDE");

        Assert.NotNull(resolved);
        Assert.Equal("be terse", resolved!.Content);
    }

    [Fact]
    public void ResolveFragmentReference_ReturnsNull_ForAnUnknownReference()
    {
        var pool = new[] { Fragment("tone-guide") };

        Assert.Null(PromptPipeline.ResolveFragmentReference(pool, Guid.NewGuid().ToString(), "missing"));
    }

    // ---------------------------------------------------------------- condition gating

    [Fact]
    public void EvaluateCondition_InspectorPair_GatesOnHasWorkspace()
    {
        var node = Json("""{"type":"condition","config":{"field":"has_workspace","value":true}}""");

        Assert.True(PromptPipeline.EvaluateCondition(node, Context(hasWorkspace: true)));
        Assert.False(PromptPipeline.EvaluateCondition(node, Context(hasWorkspace: false)));
    }

    [Fact]
    public void EvaluateCondition_GatesOnModeSlugCapabilityAndLayer()
    {
        var soloOnly = Json("""{"type":"condition","config":{"mode_slug":"solo"}}""");
        var capability = Json("""{"type":"condition","config":{"capability":"code_edit"}}""");
        var layer = Json("""{"type":"condition","config":{"layer":["governance","planning"]}}""");

        Assert.True(PromptPipeline.EvaluateCondition(soloOnly, Context(modeSlug: "solo")));
        Assert.False(PromptPipeline.EvaluateCondition(soloOnly, Context(modeSlug: "review")));

        Assert.True(PromptPipeline.EvaluateCondition(capability, Context(capabilities: ["code_edit"])));
        Assert.False(PromptPipeline.EvaluateCondition(capability, Context(capabilities: ["research"])));

        Assert.True(PromptPipeline.EvaluateCondition(layer, Context(layer: "planning")));
        Assert.False(PromptPipeline.EvaluateCondition(layer, Context(layer: "execution")));
    }

    [Fact]
    public void EvaluateCondition_WithNoCondition_IsTrue()
    {
        Assert.True(PromptPipeline.EvaluateCondition(Json("""{"type":"template","content":"hi"}"""), Context()));
    }

    [Fact]
    public void EvaluateCondition_AllPresentPredicatesMustHold()
    {
        // A node carrying two conditions is an AND, not a pick-one: the canvas shows both fields.
        var node = Json("""{"type":"condition","config":{"has_tools":true,"mode_slug":"review"}}""");

        Assert.False(PromptPipeline.EvaluateCondition(node, Context(modeSlug: "solo", hasTools: true)));
        Assert.True(PromptPipeline.EvaluateCondition(node, Context(modeSlug: "review", hasTools: true)));
    }

    // ---------------------------------------------------------------- token estimation

    [Fact]
    public void EstimateTokens_CjkIsAboutOneAndAHalfCharsPerToken()
    {
        // 30 Chinese characters: len/4 would say 8, the real ratio is ~20.
        var chinese = new string('中', 30);

        var tokens = PromptPipeline.EstimateTokens(chinese);

        Assert.Equal(20, tokens);
        Assert.True(tokens > chinese.Length / 4, "a flat len/4 must not be the estimate for Chinese");
    }

    [Fact]
    public void EstimateTokens_AsciiStaysAtFourCharsPerToken()
    {
        var ascii = new string('a', 40);

        Assert.Equal(10, PromptPipeline.EstimateTokens(ascii));
    }

    [Fact]
    public void EstimateTokens_MixedTextCountsEachScriptSeparately()
    {
        // 15 CJK -> 10, 40 ASCII -> 10.
        var mixed = new string('中', 15) + new string('a', 40);

        Assert.Equal(20, PromptPipeline.EstimateTokens(mixed));
    }

    [Fact]
    public void EstimateTokens_EmptyTextIsZero_ButANonEmptyLayerIsNeverZero()
    {
        Assert.Equal(0, PromptPipeline.EstimateTokens(string.Empty));
        Assert.Equal(0, PromptPipeline.EstimateTokens(null));
        Assert.Equal(1, PromptPipeline.EstimateTokens("a"));
        Assert.Equal(1, PromptPipeline.EstimateTokens("中"));
    }

    [Fact]
    public void EstimateTokens_CountsKanaHangulAndFullwidthPunctuationAsCjk()
    {
        // Japanese, Korean and the fullwidth block are all dense scripts, not ASCII.
        Assert.Equal(2, PromptPipeline.EstimateTokens("あい"));   // hiragana
        Assert.Equal(2, PromptPipeline.EstimateTokens("アイ"));   // katakana
        Assert.Equal(2, PromptPipeline.EstimateTokens("한글"));   // hangul
        Assert.Equal(2, PromptPipeline.EstimateTokens("，。"));   // fullwidth punctuation
    }

    // ---------------------------------------------------------------- slug vs GUID matching

    [Fact]
    public void FragmentTargeting_MatchesAgentSlug_WhenFragmentIsScopedBySlug()
    {
        // The defect: a fragment scoped to slug "meeting" never applied to a GUID-only identity.
        var (guids, slugs) = PromptFragmentTargeting.BuildIdentity("meeting");

        Assert.True(PromptFragmentTargeting.Matches(null, "meeting", guids, slugs));
    }

    [Fact]
    public void FragmentTargeting_MatchesAgentGuid_WhenFragmentIsScopedByGuid()
    {
        var agentId = Guid.NewGuid();
        var (guids, slugs) = PromptFragmentTargeting.BuildIdentity("meeting", agentId);

        Assert.True(PromptFragmentTargeting.Matches(agentId, null, guids, slugs));
    }

    [Fact]
    public void FragmentTargeting_MatchesWhenOnlyOneSideIsKnown()
    {
        // A row written before the resolver ran has a GUID but no slug; the agent is addressed by
        // slug alone. Both must still meet once the agent's full identity is known.
        var agentId = Guid.NewGuid();
        var (guids, slugs) = PromptFragmentTargeting.BuildIdentity("meeting", agentId);

        Assert.True(PromptFragmentTargeting.Matches(agentId, null, guids, slugs));
        Assert.True(PromptFragmentTargeting.Matches(null, "meeting", guids, slugs));
    }

    [Fact]
    public void FragmentTargeting_UnscopedFragmentAppliesToEveryAgent()
    {
        var (guids, slugs) = PromptFragmentTargeting.BuildIdentity("meeting");

        Assert.True(PromptFragmentTargeting.Matches(null, null, guids, slugs));
    }

    [Fact]
    public void FragmentTargeting_RejectsADifferentAgent()
    {
        var (guids, slugs) = PromptFragmentTargeting.BuildIdentity("meeting", Guid.NewGuid());

        Assert.False(PromptFragmentTargeting.Matches(Guid.NewGuid(), "reviewer", guids, slugs));
    }

    [Fact]
    public void FragmentTargeting_AcceptsAGuidStoredAsSlugText()
    {
        // A resolver that could not find the definition stores the GUID text in the slug column.
        var agentId = Guid.NewGuid();
        var (guids, slugs) = PromptFragmentTargeting.BuildIdentity(agentId.ToString());

        Assert.True(PromptFragmentTargeting.Matches(null, agentId.ToString(), guids, slugs));
        Assert.True(PromptFragmentTargeting.Matches(agentId, null, guids, slugs));
    }

    // ---------------------------------------------------------------- double JSON encoding

    [Fact]
    public void ContentCodec_ReadsLegacyDoubleEncodedRows()
    {
        // What an importer that re-serialized an already-encoded body left behind.
        var doubleEncoded = JsonSerializer.Serialize(JsonSerializer.Serialize("# 标题\n正文"));

        Assert.Equal("# 标题\n正文", PromptContentCodec.UnwrapFragmentText(doubleEncoded));
    }

    [Fact]
    public void ContentCodec_ReadsSingleEncodedAndCleanText()
    {
        Assert.Equal("# 标题", PromptContentCodec.UnwrapFragmentText(JsonSerializer.Serialize("# 标题")));
        Assert.Equal("# 标题", PromptContentCodec.UnwrapFragmentText("# 标题"));
        Assert.Equal(string.Empty, PromptContentCodec.UnwrapFragmentText(null));
    }

    [Fact]
    public void ContentCodec_ReadsAnObjectEnvelope()
    {
        Assert.Equal("body", PromptContentCodec.UnwrapFragmentText("""{"content":"body"}"""));
        Assert.Equal("body", PromptContentCodec.UnwrapFragmentText("""{"text":"body"}"""));
    }

    [Fact]
    public void ContentCodec_NormalizeForWrite_StripsExactlyOneLegacyLayer()
    {
        // A caller that posts an already-encoded body must not get a second layer stacked on it.
        var legacy = JsonSerializer.Serialize("# clean markdown");

        Assert.Equal("# clean markdown", PromptContentCodec.NormalizeForWrite(legacy));
        Assert.Equal("# clean markdown", PromptContentCodec.NormalizeForWrite("# clean markdown"));
    }

    [Fact]
    public void ContentCodec_NormalizeForWrite_LeavesJsonShapedPromptsAlone()
    {
        // A fragment may legitimately document a JSON schema; that is text, not an envelope.
        const string schema = """{"type":"object","properties":{"a":{"type":"string"}}}""";

        Assert.Equal(schema, PromptContentCodec.NormalizeForWrite(schema));
    }

    [Fact]
    public void ContentCodec_WriteThenRead_RoundTripsCjkMarkdown()
    {
        // The stored blob must be valid JSON (the prompts.toml projection parses it) while reading
        // back as the exact markdown that was written.
        const string body = "# 会议纪要\n\n- 决定：保留\n- 风险：\"引号\" 与 \\ 反斜杠";

        var stored = JsonSerializer.Serialize(PromptContentCodec.NormalizeForWrite(body));
        using var parsed = JsonDocument.Parse(stored); // must not throw: the projection requires JSON
        Assert.Equal(JsonValueKind.String, parsed.RootElement.ValueKind);
        Assert.Equal(body, PromptContentCodec.UnwrapFragmentText(stored));
    }

    // ---------------------------------------------------------------- token substitution

    [Fact]
    public void SubstituteTokens_ReplacesTheFiveDocumentedVariables()
    {
        var substituted = PromptPipeline.SubstituteTokens(
            "{{agent_name}}/{{agent_slug}} in {{mode_name}}/{{mode_slug}} at {{workspace_root}}",
            Context());

        Assert.Equal("Meeting/meeting in Solo/solo at /work/root", substituted);
    }

    [Fact]
    public void SubstituteTokens_LeavesAnUnknownTokenVerbatim()
    {
        // Silently blanking an unknown token would hide a typo in the template.
        Assert.Equal("{{unknown_token}}", PromptPipeline.SubstituteTokens("{{unknown_token}}", Context()));
    }
}

/// <summary>
/// Endpoint contract for the prompt surface the desktop already calls. These assert that the routes
/// answer with a real payload instead of the 501 capability stub, because a stub is indistinguishable
/// from a working feature in the UI until a user hits it.
/// </summary>
public sealed class PromptEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-prompt-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private HttpClient Client() => _factory!.CreateClient();

    [Fact]
    public async Task Preview_IsNot501_AndReturnsRealLayersWithCjkAwareTokens()
    {
        var client = Client();

        var response = await client.PostAsJsonAsync("/api/v1/prompt-context/preview", new
        {
            agent_id = "meeting",
            draft_system_prompt = "你是一个会议智能体，负责汇总证据。",
            draft_mode_instructions = "只输出经过证据支持的决定。"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("assembled_prompt", out var assembled));
        Assert.False(string.IsNullOrWhiteSpace(assembled.GetString()));

        Assert.True(root.TryGetProperty("layers", out var layers));
        Assert.Equal(JsonValueKind.Array, layers.ValueKind);
        Assert.NotEmpty(layers.EnumerateArray());

        Assert.True(root.TryGetProperty("total_estimated_tokens", out var total));
        Assert.True(total.GetInt32() > 0);

        // The Chinese system prompt must be reported at CJK density, not len/4: a 15-character
        // Chinese layer cannot be worth 3 tokens.
        var systemLayer = layers.EnumerateArray()
            .First(layer => layer.GetProperty("kind").GetString() == "system_prompt");
        var systemTokens = systemLayer.GetProperty("estimated_tokens").GetInt32();
        var systemLength = systemLayer.GetProperty("content").GetString()!.Length;
        Assert.True(systemTokens > systemLength / 4, $"CJK layer of {systemLength} chars reported {systemTokens} tokens");
    }

    [Fact]
    public async Task Preview_ExecutesTheDraftPipelineGraph_SoTemplateAndConditionNodesRender()
    {
        var client = Client();

        var response = await client.PostAsJsonAsync("/api/v1/prompt-context/preview", new
        {
            agent_id = "meeting",
            draft_pipeline_graph = new
            {
                nodes = new object[]
                {
                    // A preview without a session has no frozen workspace, so a has_workspace:true
                    // gate must fail and take everything reachable only through it with it.
                    new { id = "gate", type = "condition", config = new { field = "has_workspace", value = true } },
                    new { id = "always", type = "template", content = "agent={{agent_slug}} mode={{mode_slug}}" },
                    new { id = "gated", type = "template", placement = "before_runtime_constraints", content = "GATED-MARKER" }
                },
                edges = new object[]
                {
                    new { id = "e1", source = "gate", target = "gated" }
                }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The template node renders with substituted tokens...
        Assert.Contains("agent=meeting", body, StringComparison.Ordinal);
        // ...and the node gated behind a false condition does not render.
        Assert.DoesNotContain("GATED-MARKER", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_CreatesANewIdentity_WithoutABody()
    {
        var client = Client();

        var created = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "tone-guide",
            title = "Tone guide",
            scope = "workspace",
            category = "general",
            content = "保持简洁。",
            priority = 0,
            enabled = true
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var sourceId = createdDoc.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("保持简洁。", createdDoc.RootElement.GetProperty("content").GetString());

        // The desktop posts no body at all: Content-Length 0 must mean "derive the identity".
        var cloneResponse = await client.PostAsync($"/api/v1/prompt-fragments/{sourceId}/clone", null);

        Assert.Equal(HttpStatusCode.OK, cloneResponse.StatusCode);
        using var cloneDoc = JsonDocument.Parse(await cloneResponse.Content.ReadAsStringAsync());
        var clone = cloneDoc.RootElement;
        Assert.NotEqual(sourceId, clone.GetProperty("id").GetGuid());
        Assert.Equal("tone-guide-copy", clone.GetProperty("key").GetString());
        Assert.Equal("保持简洁。", clone.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Versions_RollbackAndList_AreRealAndAppendOnly()
    {
        var client = Client();

        var created = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "rollback-target",
            title = "Rollback target",
            scope = "workspace",
            category = "general",
            content = "version one",
            priority = 0,
            enabled = true
        });
        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var fragmentId = createdDoc.RootElement.GetProperty("id").GetGuid();

        // A second version through the versions route (immutable row from the posted body).
        var versionResponse = await client.PostAsJsonAsync($"/api/v1/prompt-fragments/{fragmentId}/versions", new
        {
            content = "version two",
            changed_fields = new[] { "content" },
            change_summary = "second"
        });
        Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
        using var versionDoc = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, versionDoc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("version two", versionDoc.RootElement.GetProperty("content").GetString());

        var listResponse = await client.GetAsync($"/api/v1/prompt-fragments/{fragmentId}/versions");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, listDoc.RootElement.GetArrayLength());

        // Rollback restores version one's content AND records a third version, so history grows.
        var rollbackResponse = await client.PostAsJsonAsync($"/api/v1/prompt-fragments/{fragmentId}/rollback", new { target_version = 1 });
        Assert.Equal(HttpStatusCode.OK, rollbackResponse.StatusCode);
        using var rollbackDoc = JsonDocument.Parse(await rollbackResponse.Content.ReadAsStringAsync());
        Assert.Equal("version one", rollbackDoc.RootElement.GetProperty("content").GetString());

        var afterResponse = await client.GetAsync($"/api/v1/prompt-fragments/{fragmentId}/versions");
        using var afterDoc = JsonDocument.Parse(await afterResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, afterDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task SignalsAndCompare_StayAnHonest501()
    {
        // Accepted limitation: no run telemetry exists to back either route, so both must say so
        // rather than return a fabricated empty success.
        var client = Client();
        var fragmentId = Guid.NewGuid();

        var signals = await client.PostAsJsonAsync($"/api/v1/prompt-fragments/{fragmentId}/signals", new { signal = "positive" });
        Assert.Equal(HttpStatusCode.NotImplemented, signals.StatusCode);
        Assert.Contains("capability_unavailable", await signals.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var compare = await client.PostAsJsonAsync($"/api/v1/prompt-fragments/{fragmentId}/compare", new { version_a = 1, version_b = 2 });
        Assert.Equal(HttpStatusCode.NotImplemented, compare.StatusCode);
        Assert.Contains("capability_unavailable", await compare.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelSettings_IsNot501_AndRoundTripsThroughTheProvider()
    {
        var client = Client();

        var before = await client.GetAsync("/api/v1/model-settings");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var saved = await client.PutAsJsonAsync("/api/v1/model-settings", new
        {
            base_url = "https://api.example.com/v1",
            model = "example-model"
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedDoc = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.Equal("https://api.example.com/v1", savedDoc.RootElement.GetProperty("base_url").GetString());
        Assert.Equal("example-model", savedDoc.RootElement.GetProperty("model").GetString());

        var after = await client.GetAsync("/api/v1/model-settings");
        using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal("https://api.example.com/v1", afterDoc.RootElement.GetProperty("base_url").GetString());
        Assert.Equal("example-model", afterDoc.RootElement.GetProperty("model").GetString());
    }

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
