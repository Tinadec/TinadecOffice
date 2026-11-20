using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The self-configuration "last mile": the two tools that were declared but not
/// dispatched, the four inspect targets that did not exist, and the agent-publish step
/// without which an AI-created agent could never enter a runnable mode.
///
/// Every test here goes through <see cref="ITinadecConfigToolGateway"/> with the real
/// registered services — no doubles for the stores under test — because the failure
/// this suite exists to prevent is "the tool is in the manifest and the handler does
/// not exist", which a mock would happily hide.
/// </summary>
public sealed class TinadecSelfConfigAndMcpTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-selfconfig-tests", Guid.NewGuid().ToString("N"));
    private Factory _factory = null!;

    public Task InitializeAsync() { Directory.CreateDirectory(_root); _factory = new(_root); return Task.CompletedTask; }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    // ── dispatch: the two dead-on-arrival tools ──

    [Fact]
    public void EveryDeclaredTinadecToolHasAManifestEntryAndIsDispatched()
    {
        // The original bug: CoreTinadecConfigTools declared prompt_apply and
        // model_profile_apply, but the gateway's if-chain had no branch for them, so a
        // model calling either got "unknown_tool". Asserting the ids exist is not enough;
        // assert the manifest covers the whole policy list both ways.
        foreach (var id in CoreVirtualToolPolicy.TinadecConfigToolIds)
        {
            var entry = CoreTinadecConfigTools.ManifestEntry(id);
            Assert.Equal(id, entry.Id);
            Assert.False(entry.RequiresApproval, $"{id} must not require approval under the self-config policy");
            Assert.False(entry.MutatesWorkspace, $"{id} must never mutate the workspace");
        }
        Assert.Contains(CoreVirtualToolPolicy.TinadecPromptApplyToolId, CoreVirtualToolPolicy.TinadecConfigToolIds);
        Assert.Contains(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, CoreVirtualToolPolicy.TinadecConfigToolIds);
        Assert.Equal(8, CoreVirtualToolPolicy.TinadecConfigToolIds.Count);
    }

    [Fact]
    public async Task PromptApply_And_ModelProfileApply_NoLongerAnswerUnknownTool()
    {
        // A real call must reach a real handler. Either a success or a *domain* refusal is
        // acceptable here; "unknown_tool" is the one outcome that proves the branch is gone.
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        var prompt = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new { target = "prompt_pipeline", action = "publish", pipeline_id = Guid.NewGuid().ToString() }),
            CancellationToken.None);
        Assert.False(prompt.IsSuccess);
        Assert.DoesNotContain("unknown_tool", prompt.Error);

        var profile = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, new { action = "inspect" }), CancellationToken.None);
        Assert.DoesNotContain("unknown_tool", profile.Error ?? string.Empty);
    }

    [Fact]
    public async Task PromptApply_RejectsAMissingOrNonsenseTargetWithActionableText()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        var badTarget = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new { target = "mode", action = "create" }), CancellationToken.None);
        Assert.False(badTarget.IsSuccess);
        Assert.Contains("target must be fragment or pipeline", badTarget.Error);

        var badAction = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new { target = "fragment", action = "delete" }), CancellationToken.None);
        Assert.False(badAction.IsSuccess);
        Assert.Contains("action must be create, update, clone, publish or rollback", badAction.Error);
    }

    // ── inspect targets ──

    [Fact]
    public async Task Inspect_RejectsAnUnknownTargetAndNamesTheWholeSet()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "everything" }), CancellationToken.None);

        Assert.False(result.IsSuccess);
        // The error text is the discoverability contract: a model that guesses a target
        // must be told the real list, or it retries with another guess.
        foreach (var target in new[] { "agents", "agent", "modes", "mode", "catalog", "models", "prompts", "prompt_pipeline", "skills", "mcp", "market" })
            Assert.Contains(target, result.Error);
    }

    [Fact]
    public async Task Inspect_AgentBySlugReturnsTheFullDefinitionNotTheListProjection()
    {
        var client = _factory.CreateClient();
        var agents = await client.GetFromJsonAsync<JsonElement>("/api/v1/agents");
        var first = agents.EnumerateArray().First();
        var slug = first.GetProperty("slug").GetString();

        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "agent", agent_id = slug }), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var agent = result.Result!.Value.GetProperty("agent");
        // The list projection truncates exactly these; the detail read must carry them.
        foreach (var field in new[]
        {
            "system_prompt", "model_strategy", "tool_scope", "capabilities",
            "base_prompt_pipeline_id", "source_kind", "provenance", "runtime_binding",
            "revision", "built_in", "writable",
        })
            Assert.True(agent.TryGetProperty(field, out _), $"target=agent must return '{field}'");
    }

    [Fact]
    public async Task Inspect_AgentAndModeAndPipelineSayNotFoundForAFabricatedReference()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        foreach (var (target, parameter) in new[] { ("agent", "agent_id"), ("mode", "mode_id"), ("prompt_pipeline", "pipeline_id") })
        {
            var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, Scope(),
                Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new Dictionary<string, object>
                {
                    ["target"] = target,
                    [parameter] = "no-such-thing-anywhere",
                }), CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Contains("not_found", result.Error);
            // The message has to point the caller at the fix, not just say no.
            Assert.Contains("tinadec_inspect", result.Error);
        }
    }

    [Fact]
    public async Task Inspect_ModelsPromptsAndModesAnswerWithTheirOwnTargetShape()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        foreach (var (target, expectedKey) in new[]
        {
            ("models", "providers"),
            ("prompts", "fragments"),
            ("modes", "items"),
        })
        {
            var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, Scope(),
                Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target }), CancellationToken.None);
            Assert.True(result.IsSuccess, $"{target}: {result.Error}");
            Assert.Equal(target, result.Result!.Value.GetProperty("target").GetString());
            Assert.True(result.Result!.Value.TryGetProperty(expectedKey, out _), $"target={target} must return '{expectedKey}'");
        }
    }

    // ── agent publish: the step that makes an AI-created agent usable ──

    [Fact]
    public async Task AgentApply_CreateThenPublishProducesARunnableAgent()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        var created = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new
                {
                    display_name = "Audit Specialist",
                    slug = "audit-specialist",
                    layer = "execution",
                    role = "audit",
                    system_prompt = "You audit. Cite file and line.",
                    capabilities = new[] { "code.audit" },
                    tool_scope = new[] { "read_file", "git_diff" },
                },
            }), CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error);
        var agent = created.Result!.Value.GetProperty("agent");
        var agentId = agent.GetProperty("id").GetGuid();
        Assert.Equal("draft", agent.GetProperty("status").GetString());

        // A draft agent is refused by mode publish; publish is the missing step this test pins.
        var published = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new { action = "publish", agent_id = agentId.ToString() }),
            CancellationToken.None);
        Assert.True(published.IsSuccess, published.Error);
        var publishedAgent = published.Result!.Value.GetProperty("agent");
        Assert.Equal("published", publishedAgent.GetProperty("status").GetString());
        Assert.True(published.Result!.Value.GetProperty("version").GetInt32() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(published.Result!.Value.GetProperty("content_hash").GetString()));

        // Publishing twice is a refusal, not a second version: the draft is gone.
        var again = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new { action = "publish", agent_id = agentId.ToString() }),
            CancellationToken.None);
        Assert.False(again.IsSuccess);
        Assert.Contains("not_draft", again.Error);
    }

    [Fact]
    public async Task AgentApply_RecordsProvenanceAndSourceKindForARunOriginatedAgent()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        // A non-empty RunId is what distinguishes "an agent made this" from "a person did".
        var scope = Scope() with { RunId = Guid.NewGuid(), AgentInstanceId = Guid.NewGuid() };

        var created = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Generated By A Run", layer = "execution", system_prompt = "x" },
            }), CancellationToken.None);

        Assert.True(created.IsSuccess, created.Error);
        Assert.Equal("agent_generated", created.Result!.Value.GetProperty("source_kind").GetString());

        var agentId = created.Result!.Value.GetProperty("agent").GetProperty("id").GetGuid();
        var detail = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "agent", agent_id = agentId.ToString() }), CancellationToken.None);
        Assert.True(detail.IsSuccess, detail.Error);
        var provenance = detail.Result!.Value.GetProperty("agent").GetProperty("provenance");
        Assert.Equal(JsonValueKind.Object, provenance.ValueKind);
        Assert.Equal(scope.RunId.ToString(), provenance.GetProperty("created_by_run_id").GetString());
        Assert.Equal(scope.AgentInstanceId.ToString(), provenance.GetProperty("created_by_agent_id").GetString());
    }

    [Fact]
    public async Task AgentApply_RefusesABasePipelineThatDoesNotExist()
    {
        // A fabricated pipeline GUID must fail at write time. Accepting it would publish an
        // agent whose prompt layer is silently empty.
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new
                {
                    display_name = "Bad Pipeline Agent",
                    layer = "execution",
                    base_prompt_pipeline_id = Guid.NewGuid().ToString(),
                },
            }), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("not_found", result.Error);
        Assert.Contains("tinadec_inspect", result.Error);
    }

    [Fact]
    public async Task AgentApply_RefusesModelBindingCombinationsTheRuntimeBindingEndpointRefuses()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();

        var fixedWithoutProvider = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Fixed No Provider", layer = "execution", model_binding = new { mode = "fixed" } },
            }), CancellationToken.None);
        Assert.False(fixedWithoutProvider.IsSuccess);
        Assert.Contains("provider_instance_id", fixedWithoutProvider.Error);

        var routeWithoutPurpose = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Route No Purpose", layer = "execution", model_binding = new { mode = "route" } },
            }), CancellationToken.None);
        Assert.False(routeWithoutPurpose.IsSuccess);
        Assert.Contains("route_purpose", routeWithoutPurpose.Error);

        var badMode = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Bad Binding Mode", layer = "execution", model_binding = new { mode = "sometimes" } },
            }), CancellationToken.None);
        Assert.False(badMode.IsSuccess);
        Assert.Contains("inherit, route or fixed", badMode.Error);
    }

    [Fact]
    public async Task AgentApply_RefusesToolSettingsThatNameNoBinding()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Empty Tool Settings", layer = "execution", tool_settings = new { } },
            }), CancellationToken.None);

        // A silent no-op here would let a model believe it bound an MCP server.
        Assert.False(result.IsSuccess);
        Assert.Contains("mcp_server_ids", result.Error);
    }

    // ── mode apply: slug resolution and the tier ──

    [Fact]
    public async Task ModeApply_RefusesAnUnknownOrchestrationTierBeforeWritingAnything()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModeApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecModeApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Bad Tier", orchestration_tier = "chaotic" },
            }), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("invalid_orchestration_tier", result.Error);
        foreach (var tier in new[] { "solo", "solo_dispatch", "free_form", "self_dispatch", "deterministic" })
            Assert.Contains(tier, result.Error);
    }

    [Fact]
    public async Task ModeApply_AcceptsAnOrchestrationTierAndReportsItBack()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModeApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecModeApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Tier Declared", orchestration_tier = "solo" },
            }), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        // 'solo' is the documented alias; the canonical value is what gets stored.
        Assert.Equal("solo_dispatch", result.Result!.Value.GetProperty("orchestration_tier").GetString());
        Assert.Equal("solo_dispatch", result.Result!.Value.GetProperty("mode").GetProperty("orchestration_tier").GetString());
    }

    [Fact]
    public async Task ModeApply_ResolvesModeIdBySlugForUpsertAndPublish()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var created = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModeApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecModeApplyToolId, new
            {
                action = "create",
                payload = new { display_name = "Slug Resolvable", slug = "slug-resolvable" },
            }), CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error);
        var createdId = created.Result!.Value.GetProperty("mode_id").GetGuid();

        // The whole point: the slug the model read from tinadec_inspect must resolve as an id.
        // An empty draft cannot pass graph validation (a mode needs a conversation node), so
        // the assertion is that the slug RESOLVED — the refusal must be about the graph, never
        // `not_found`, which is what a GUID-only lookup would have produced.
        var upserted = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModeApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecModeApplyToolId, new
            {
                action = "upsert",
                mode_id = "slug-resolvable",
                payload = new { display_name = "Renamed Via Slug" },
            }), CancellationToken.None);
        Assert.DoesNotContain("not_found", upserted.Error ?? string.Empty);

        // Same for publish: by slug it must reach the mode and refuse for a graph reason.
        var published = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModePublishToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecModePublishToolId, new { mode_id = "slug-resolvable" }), CancellationToken.None);
        Assert.False(published.IsSuccess);
        Assert.DoesNotContain("not_found", published.Error);

        // And an inspect by slug proves the resolver end-to-end on a read.
        var inspected = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "mode", mode_id = "slug-resolvable" }),
            CancellationToken.None);
        Assert.True(inspected.IsSuccess, inspected.Error);
        Assert.Equal(createdId, inspected.Result!.Value.GetProperty("mode").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ModeApply_UnknownSlugIsNotFoundNotInvalidRequest()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModeApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecModeApplyToolId, new { action = "upsert", mode_id = "no-such-mode" }), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("not_found", result.Error);
    }

    // ── prompt apply: the pipeline lifecycle ──

    [Fact]
    public async Task PromptApply_PipelineCreateUpdatePublishRoundTripCarriesTheGraph()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();
        var graph = new
        {
            nodes = new object[]
            {
                new { node_key = "role", kind = "template", config = new { content = "You are a reviewer." } },
                new { node_key = "out", kind = "assemble", config = new { } },
            },
            edges = new object[] { new { source_node_key = "role", target_node_key = "out" } },
        };

        var created = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "create",
                payload = new { display_name = "Reviewer Base", slug = "reviewer-base", graph },
            }), CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error);
        var pipeline = created.Result!.Value.GetProperty("pipeline");
        var pipelineId = pipeline.GetProperty("id").GetGuid();
        Assert.Equal("draft", pipeline.GetProperty("status").GetString());
        Assert.Equal(2, pipeline.GetProperty("graph").GetProperty("nodes").GetArrayLength());

        var published = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "publish",
                pipeline_id = pipelineId.ToString(),
                expected_revision = pipeline.GetProperty("revision").GetInt32(),
            }), CancellationToken.None);
        Assert.True(published.IsSuccess, published.Error);
        Assert.True(published.Result!.Value.GetProperty("version").GetInt32() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(published.Result!.Value.GetProperty("content_hash").GetString()));

        // And the inspect target returns the full graph, which is what a model edits from.
        var detail = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "prompt_pipeline", pipeline_id = "reviewer-base" }),
            CancellationToken.None);
        Assert.True(detail.IsSuccess, detail.Error);
        var detailPipeline = detail.Result!.Value.GetProperty("pipeline");
        Assert.Equal(2, detailPipeline.GetProperty("graph").GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, detailPipeline.GetProperty("graph").GetProperty("edges").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, detailPipeline.GetProperty("latest_published_version").ValueKind);
    }

    [Fact]
    public async Task PromptApply_RefusesAPipelineThatCannotRun()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        var noGraph = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "create",
                payload = new { display_name = "No Graph" },
            }), CancellationToken.None);
        Assert.False(noGraph.IsSuccess);
        Assert.Contains("graph", noGraph.Error);

        var unknownKind = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "create",
                payload = new
                {
                    display_name = "Unknown Kind",
                    graph = new
                    {
                        nodes = new object[] { new { node_key = "a", kind = "interpretive_dance" } },
                        edges = Array.Empty<object>(),
                    },
                },
            }), CancellationToken.None);
        Assert.False(unknownKind.IsSuccess);
        Assert.Contains("unknown kind", unknownKind.Error);

        var danglingEdge = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "create",
                payload = new
                {
                    display_name = "Dangling Edge",
                    graph = new
                    {
                        nodes = new object[] { new { node_key = "a", kind = "template" } },
                        edges = new object[] { new { source_node_key = "a", target_node_key = "missing" } },
                    },
                },
            }), CancellationToken.None);
        Assert.False(danglingEdge.IsSuccess);
        Assert.Contains("not declared", danglingEdge.Error);
    }

    [Fact]
    public async Task PromptApply_RefusesToPublishAPipelineWithoutTemplateAndAssemble()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var created = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "create",
                payload = new
                {
                    display_name = "Templates Only",
                    slug = "templates-only",
                    graph = new
                    {
                        nodes = new object[] { new { node_key = "a", kind = "template", config = new { content = "x" } } },
                        edges = Array.Empty<object>(),
                    },
                },
            }), CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error);

        var published = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "publish",
                pipeline_id = "templates-only",
            }), CancellationToken.None);
        Assert.False(published.IsSuccess);
        Assert.Contains("assemble", published.Error);
    }

    [Fact]
    public async Task PromptApply_FragmentCreateThenCloneProducesAnIndependentEditableCopy()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        var created = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "create",
                payload = new { key = "arch-baseline", title = "Architecture", category = "persona", content = "Always cite evidence." },
            }), CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error);
        var fragment = created.Result!.Value.GetProperty("fragment");
        var fragmentId = fragment.GetProperty("id").GetGuid();
        Assert.Equal("arch-baseline", fragment.GetProperty("key").GetString());

        // Resolving by key (not GUID) is what a model can actually do from tinadec_inspect.
        var cloned = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "clone",
                fragment_id = "arch-baseline",
                payload = new { new_key = "arch-baseline-v2" },
            }), CancellationToken.None);
        Assert.True(cloned.IsSuccess, cloned.Error);
        var copy = cloned.Result!.Value.GetProperty("fragment");
        Assert.NotEqual(fragmentId, copy.GetProperty("id").GetGuid());
        Assert.Equal("arch-baseline-v2", copy.GetProperty("key").GetString());

        var updated = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "update",
                fragment_id = "arch-baseline-v2",
                expected_revision = copy.GetProperty("revision").GetInt64(),
                payload = new { content = "Always cite evidence and the command that produced it." },
            }), CancellationToken.None);
        Assert.True(updated.IsSuccess, updated.Error);
        Assert.Contains("the command that produced it", updated.Result!.Value.GetProperty("fragment").GetProperty("content").GetString());
    }

    [Fact]
    public async Task PromptApply_FragmentPublishThenUpdateUsingTheReturnedRevisionSucceeds()
    {
        // The regression this pins: the publish path used to return a FABRICATED revision
        // (`existing.Revision + 1`) instead of re-reading the row. Because a caller feeds
        // that number straight back as `expected_revision`, a wrong guess is a guaranteed
        // 412 on the very next write. Asserting the negative case alone (a stale revision is
        // refused) would pass with the bug present — only the round-trip catches it.
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        var created = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "create",
                payload = new { key = "publish-roundtrip", content = "first" },
            }), CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error);

        var published = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "publish",
                fragment_id = "publish-roundtrip",
                payload = new { content = "second", change_summary = "publish via tool" },
            }), CancellationToken.None);
        Assert.True(published.IsSuccess, published.Error);

        var returnedRevision = published.Result!.Value.GetProperty("fragment").GetProperty("revision").GetInt64();
        Assert.True(returnedRevision > 0);

        // The exact round-trip a model performs: reuse the revision the publish returned.
        var updated = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "update",
                fragment_id = "publish-roundtrip",
                expected_revision = returnedRevision,
                payload = new { content = "third" },
            }), CancellationToken.None);
        Assert.True(updated.IsSuccess,
            $"a revision returned by publish must be accepted by the next update, got: {updated.Error}");
        Assert.Equal("third", updated.Result!.Value.GetProperty("fragment").GetProperty("content").GetString());
    }

    [Fact]
    public async Task PromptApply_StaleRevisionOnAFragmentUpdateIsRefusedWithConflict()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();
        await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "create",
                payload = new { key = "revisioned", content = "one" },
            }), CancellationToken.None);

        var stale = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecPromptApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "update",
                fragment_id = "revisioned",
                expected_revision = 99,
                payload = new { content = "two" },
            }), CancellationToken.None);
        Assert.False(stale.IsSuccess);
        Assert.Contains("revision_conflict", stale.Error);
    }

    // ── model profile apply ──

    [Fact]
    public async Task ModelProfileApply_RefusesAnUnknownModelAndNeverWritesASecret()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        // A model id that cannot exist must be refused, and the refusal must name the
        // provider/model reality rather than accepting the write. Which code it is depends on
        // whether this host has an enabled provider at all — an unseeded host answers
        // `not_found` for the provider, a seeded one answers `unknown_model` for the id — so
        // the assertion is on the *refusal plus its actionable text*, not on one code.
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, new
            {
                action = "update",
                model_id = "some-model-that-cannot-exist",
                payload = new { tier = "deep" },
            }), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.True(result.Error.Contains("not_found") || result.Error.Contains("unknown_model"),
            $"expected a not_found/unknown_model refusal, got: {result.Error}");
        // Whatever it says, it must tell the caller where the real ids are.
        Assert.Contains("tinadec_inspect", result.Error);

        // An inspect must never leak a secret-shaped field name into the payload.
        var inspect = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, new { action = "inspect" }), CancellationToken.None);
        var raw = inspect.Result?.GetRawText() ?? inspect.Error ?? string.Empty;
        Assert.DoesNotContain("api_key", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_reference", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelProfileApply_UpdateWithoutAnyProfileFieldIsRefused()
    {
        // The "no writable field" guard must fire BEFORE provider resolution, otherwise a host
        // with no providers answers `not_found` for the provider and the caller never learns
        // that its payload was empty — which is the actual mistake it made. Order matters here,
        // so this test seeds nothing and still expects the payload complaint.
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, new
            {
                action = "update",
                model_id = "gpt-x",
                payload = new { unrelated = true },
            }), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("specialties", result.Error);
        Assert.Contains("invalid_request", result.Error);
    }

    [Fact]
    public void ModelProfileValidatorThrowsArgumentExceptionThatTheToolMustReportAsInvalidModelProfile()
    {
        // The gateway wraps UpdateModelProfileAsync in its own try/catch so that the
        // ArgumentException ModelProfile.Normalize raises is reported as
        // `invalid_model_profile` (the documented code, per core-prompts' contract) rather
        // than the generic `invalid_request`. This pins the validator half: it throws, and
        // it names the bad leaf so the model can fix it without guessing.
        var badTier = Assert.Throws<ArgumentException>(() => ModelProfile.Normalize(new ModelProfileDto
        {
            Specialties = ["code"],
            Tier = "enormous",
        }));
        Assert.Contains("tier", badTier.Message);
        Assert.Contains("fast", badTier.Message);

        var badContextWindow = Assert.Throws<ArgumentException>(() => ModelProfile.Normalize(new ModelProfileDto
        {
            ContextWindow = 0,
        }));
        Assert.Contains("context_window", badContextWindow.Message);

        var tooManySpecialties = Assert.Throws<ArgumentException>(() => ModelProfile.Normalize(new ModelProfileDto
        {
            Specialties = Enumerable.Range(0, 40).Select(index => $"specialty-{index}").ToArray(),
        }));
        Assert.Contains("specialties", tooManySpecialties.Message);

        // And the happy path must not throw, so the assertion above is not just "always throws".
        var normalized = ModelProfile.Normalize(new ModelProfileDto
        {
            Specialties = [" code ", "code", "reasoning"],
            Tier = "DEEP",
            CostTier = "HIGH",
        });
        Assert.Equal("deep", normalized.Tier);
        Assert.Equal("high", normalized.CostTier);
        // Duplicates collapse and whitespace is trimmed, so a caller can be sloppy once.
        Assert.Equal(["code", "reasoning"], normalized.Specialties);
    }

    // ── the MCP server surface this file also guards ──

    [Fact]
    public async Task McpServer_RejectsAnyForwardedRequestSoItCannotBeReachedThroughAProxy()
    {
        var client = _factory.CreateClient();
        // Loopback-only is the whole security model of this endpoint; a forwarded request is
        // the one shape that means "someone is relaying this from off-host".
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/tinadec")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── plumbing ──

    private ToolInvocationScope Scope()
    {
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        return new(tenant.TenantId, tenant.WorkspaceId, tenant.PrincipalId,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty,
            _root, CoreVirtualToolPolicy.TinadecConfigToolIds.ToArray(), [], "selfconfig-test", 600, 0, false);
    }

    private static ToolWireRequestDto Wire(string toolId, object parameters) => new()
    {
        ToolId = toolId,
        SessionId = "selfconfig-test",
        Params = JsonSerializer.SerializeToElement(parameters),
    };

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecTools:DefaultWorkspaceRoot"] = root,
                ["Logging:LogLevel:Default"] = "Warning",
            }));
            builder.ConfigureTestServices(services =>
            {
                // The tools child process is irrelevant to these handlers; a model catalog and
                // skill catalog must still be the real registrations, so only the provider is replaced.
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(new Provider());
            });
        }
    }

    private sealed class Provider : IToolProvider
    {
        public Task<ToolManifestDto> EnsureStartedAsync(string root, CancellationToken ct = default) => GetManifestAsync(root, ct);
        public Task<ToolManifestDto> GetManifestAsync(string root, CancellationToken ct = default) =>
            Task.FromResult(new ToolManifestDto { ProtocolVersion = 2, Tools = [] });
        public Task<ToolWireResponseDto> CallAsync(string root, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolWireResponseDto { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) });
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
