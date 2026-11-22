using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

public sealed class AgentModeLifecycleAndFoundationsTests : IAsyncLifetime
{
    private VibePackFactory? _factory;

    public Task InitializeAsync()
    {
        _factory = new VibePackFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PackModeReadback_PersistsAndBackfillsBindingsRelationshipsTierAndDefaultOptions()
    {
        using var client = _factory!.CreateClient();
        await InstallBundledGraphSeedPackAsync(client);

        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes");
        Assert.NotNull(modes);
        var vibeSummary = modes!.First(m => m.GetProperty("slug").GetString() == "vibe_graph" && m.GetProperty("pack_managed").GetBoolean());
        var vibeId = vibeSummary.GetProperty("id").GetGuid();
        Assert.Equal("self_dispatch", vibeSummary.GetProperty("orchestration_tier").GetString());
        Assert.True(vibeSummary.GetProperty("default_options").GetProperty("multi_agent").GetBoolean());

        var soloSummary = modes!.First(m => m.GetProperty("slug").GetString() == "solo" && m.GetProperty("pack_managed").GetBoolean());
        Assert.Equal("solo_dispatch", soloSummary.GetProperty("orchestration_tier").GetString());

        // Direct readback has persisted bindings, orchestration_tier, default_options, and node relationships.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{vibeId}");
        Assert.Equal("self_dispatch", detail.GetProperty("orchestration_tier").GetString());
        Assert.True(detail.GetProperty("default_options").GetProperty("multi_agent").GetBoolean());
        Assert.True(detail.GetProperty("bindings").GetArrayLength() > 0);
        var meetingNode = detail.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("node_key").GetString() == "meeting");
        Assert.Equal(JsonValueKind.Object, meetingNode.GetProperty("relationship").ValueKind);

        // Simulate a pre-upgrade dev DB where BindingsJson, OrchestrationTier, DefaultOptionsJson,
        // and ModeNodeRecord.RelationshipJson were null, plus multiple CanvasLayouts with DateTimeOffset timestamps.
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync())
        {
            var modeRow = await db.AgentModes.SingleAsync(x => x.Id == vibeId);
            modeRow.BindingsJson = null;
            modeRow.OrchestrationTier = null;
            modeRow.DefaultOptionsJson = null;
            var nodeRows = await db.ModeNodes.Where(x => x.ModeId == vibeId).ToListAsync();
            foreach (var node in nodeRows)
                node.RelationshipJson = null;
            db.CanvasLayouts.Add(new CanvasLayoutRecord
            {
                Id = Guid.NewGuid(),
                TenantId = modeRow.TenantId,
                WorkspaceId = modeRow.WorkspaceId,
                ModeId = vibeId,
                LayoutJson = "{\"viewport\":{\"zoom\":1}}",
                Status = "archived",
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        // SnapshotBackfillAsync restores everything from ModeVersionRecord.SnapshotJson without SQLite DateTimeOffset errors.
        var backfilled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{vibeId}");
        Assert.Equal("self_dispatch", backfilled.GetProperty("orchestration_tier").GetString());
        Assert.True(backfilled.GetProperty("default_options").GetProperty("multi_agent").GetBoolean());
        Assert.True(backfilled.GetProperty("bindings").GetArrayLength() > 0);
        var backfilledMeeting = backfilled.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("node_key").GetString() == "meeting");
        Assert.Equal(JsonValueKind.Object, backfilledMeeting.GetProperty("relationship").ValueKind);

        // Slug & GUID lookup via IAgentTopologyService.
        var topology = _factory.Services.GetRequiredService<IAgentTopologyService>();
        Assert.Equal(vibeId, await topology.ResolveIdBySlugOrGuidAsync("vibe_graph"));
        Assert.Equal(vibeId, await topology.ResolveIdBySlugOrGuidAsync(vibeId.ToString()));

        // CloneDraftAsync also backfills and copies all fields cleanly while skipping archived layout rows.
        using var cloneRes = await client.PostAsJsonAsync($"/api/v1/agent-modes/{vibeId}/clone", new { display_name = "Cloned Vibe", slug = "cloned-vibe" });
        Assert.Equal(HttpStatusCode.Created, cloneRes.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(cloneRes.Headers.ETag?.Tag));
        var cloned = await cloneRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft", cloned.GetProperty("status").GetString());
        Assert.Equal("self_dispatch", cloned.GetProperty("orchestration_tier").GetString());
        Assert.True(cloned.GetProperty("default_options").GetProperty("multi_agent").GetBoolean());
        Assert.True(cloned.GetProperty("bindings").GetArrayLength() > 0);
        var clonedMeeting = cloned.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("node_key").GetString() == "meeting");
        Assert.Equal(JsonValueKind.Object, clonedMeeting.GetProperty("relationship").ValueKind);
    }

    [Fact]
    public async Task ModeLifecycle_CloneEmptyBody_ReopenPublishedDraft_DeleteCustom_AndGuardPackModes()
    {
        using var client = _factory!.CreateClient();
        await InstallBundledGraphSeedPackAsync(client);

        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes");
        var packModeId = modes!.First(m => m.GetProperty("slug").GetString() == "solo" && m.GetProperty("pack_managed").GetBoolean()).GetProperty("id").GetGuid();

        // Pack-managed modes return 409 built_in_read_only on PUT /draft and DELETE.
        using var packEdit = await client.PutAsJsonAsync($"/api/v1/agent-modes/{packModeId}/draft", new { display_name = "Mutate Pack" });
        Assert.Equal(HttpStatusCode.Conflict, packEdit.StatusCode);
        Assert.Equal("built_in_read_only", (await packEdit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var packDelete = await client.DeleteAsync($"/api/v1/agent-modes/{packModeId}");
        Assert.Equal(HttpStatusCode.Conflict, packDelete.StatusCode);
        Assert.Equal("built_in_read_only", (await packDelete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Invalid orchestration_tier or default_options fail with 400.
        using var badTier = await client.PostAsJsonAsync("/api/v1/agent-modes", new { display_name = "Bad Tier", orchestration_tier = "quantum_magic" });
        Assert.Equal(HttpStatusCode.BadRequest, badTier.StatusCode);
        using var badOptions = await client.PostAsJsonAsync("/api/v1/agent-modes", new { display_name = "Bad Opts", default_options = new { unknown_flag = true } });
        Assert.Equal(HttpStatusCode.BadRequest, badOptions.StatusCode);

        // Clone with empty body succeeds and creates an editable custom draft.
        using var emptyClone = await client.PostAsync($"/api/v1/agent-modes/{packModeId}/clone", content: null);
        Assert.Equal(HttpStatusCode.Created, emptyClone.StatusCode);
        var cloned = await emptyClone.Content.ReadFromJsonAsync<JsonElement>();
        var customId = cloned.GetProperty("id").GetGuid();

        // Normalize 'solo' alias -> 'solo_dispatch' and persist default_options.
        using var updateV1 = await client.PutAsJsonAsync($"/api/v1/agent-modes/{customId}/draft", new
        {
            display_name = "Custom Solo V1",
            orchestration_tier = "solo",
            default_options = new { plan_first = true, spec_enabled = false, multi_agent = false, bulletin_board = false, worktree = false },
            instructions = "Work step by step."
        });
        Assert.Equal(HttpStatusCode.OK, updateV1.StatusCode);
        var updatedDraft = await updateV1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("solo_dispatch", updatedDraft.GetProperty("orchestration_tier").GetString());
        Assert.True(updatedDraft.GetProperty("default_options").GetProperty("plan_first").GetBoolean());

        // Publish v1.
        using var pubV1 = await client.PostAsync($"/api/v1/agent-modes/{customId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubV1.StatusCode);
        Assert.Equal(1, (await pubV1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());

        var publishedV1 = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{customId}");

        // Reopen published custom mode back to draft via PUT /draft WITH full topology (nodes, edges, canvas_layout)
        // to ensure tracked reopened entities are reused in-place without SQLite unique-index collisions.
        using var reopen = await client.PutAsJsonAsync($"/api/v1/agent-modes/{customId}/draft", new
        {
            display_name = "Custom Solo V2",
            orchestration_tier = "free_form",
            instructions = "Updated team instructions.",
            nodes = publishedV1.GetProperty("nodes"),
            edges = publishedV1.GetProperty("edges"),
            canvas_layout = new { viewport = new { x = 0, y = 0, zoom = 1.25 } }
        });
        Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
        var reopenedBody = await reopen.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft", reopenedBody.GetProperty("status").GetString());
        Assert.Equal("free_form", reopenedBody.GetProperty("orchestration_tier").GetString());
        Assert.True(reopenedBody.GetProperty("nodes").GetArrayLength() > 0);
        Assert.Equal(1.25, reopenedBody.GetProperty("canvas_layout").GetProperty("viewport").GetProperty("zoom").GetDouble());

        // Publish v2 and verify both ModeVersionRecord snapshots exist.
        using var pubV2 = await client.PostAsync($"/api/v1/agent-modes/{customId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubV2.StatusCode);
        var versions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{customId}/versions");
        Assert.Equal(2, versions!.Length);
        Assert.Equal("solo_dispatch", versions[0].GetProperty("snapshot").GetProperty("orchestration_tier").GetString());
        Assert.Equal("free_form", versions[1].GetProperty("snapshot").GetProperty("orchestration_tier").GetString());

        // Point workspace_defaults to customId, then DELETE customId -> clears workspace_defaults and deletes custom mode cleanly.
        using var setDef = await client.PutAsJsonAsync("/api/v1/workspace-defaults/draft", new
        {
            default_agent_mode_id = customId,
            default_mode_version_id = versions[1].GetProperty("id").GetGuid()
        });
        Assert.Equal(HttpStatusCode.OK, setDef.StatusCode);

        using var deleteCustom = await client.DeleteAsync($"/api/v1/agent-modes/{customId}");
        Assert.Equal(HttpStatusCode.OK, deleteCustom.StatusCode);
        Assert.True((await deleteCustom.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetBoolean());

        using var afterDelete = await client.GetAsync($"/api/v1/agent-modes/{customId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

        var defaultsAfter = await client.GetFromJsonAsync<JsonElement>("/api/v1/workspace-defaults");
        Assert.True(!defaultsAfter.TryGetProperty("default_agent_mode_id", out var dam) || dam.ValueKind == JsonValueKind.Null);
        Assert.True(!defaultsAfter.TryGetProperty("default_mode_version_id", out var dmv) || dmv.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task PublishGate_UnifiesWildcardSwitchOff_BlocksMutatingWithoutWriteGrant_AndValidatesPromptGraph()
    {
        using var client = _factory!.CreateClient();

        // Prompt pipeline accepts fragment_ref, workspace_rules, and skills_index node types.
        using var createPipe = await client.PostAsJsonAsync("/api/v1/prompt-pipelines", new
        {
            slug = "modern-prompt-graph",
            display_name = "Modern Prompt Graph",
            graph = new
            {
                nodes = new object[]
                {
                    new { id = "n1", type = "fragment_ref", fragment_key = "global-tone" },
                    new { id = "n2", type = "workspace_rules" },
                    new { id = "n3", type = "skills_index" },
                    new { id = "n4", type = "assemble" }
                },
                edges = new object[]
                {
                    new { source = "n1", target = "n4" },
                    new { source = "n2", target = "n4" },
                    new { source = "n3", target = "n4" }
                }
            }
        });
        Assert.Equal(HttpStatusCode.Created, createPipe.StatusCode);
        var pipelineId = (await createPipe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Create and publish a custom leader holding a mutating tool (write_file) + agent_generated provenance + base_prompt_pipeline_id.
        using var createLeader = await client.PostAsJsonAsync("/api/v1/agents", new
        {
            slug = "mutating-leader",
            display_name = "Mutating Leader",
            layer = "operation",
            role = "session_coordinator",
            source_kind = "agent_generated",
            provenance = new { created_by_agent_slug = "meeting", rationale = "auto-generated coordinator" },
            base_prompt_pipeline_id = pipelineId,
            tool_scope = new[] { "read_file", "write_file" },
            capabilities = new[] { "user.respond" }
        });
        Assert.Equal(HttpStatusCode.Created, createLeader.StatusCode);
        var leaderBody = await createLeader.Content.ReadFromJsonAsync<JsonElement>();
        var leaderId = leaderBody.GetProperty("id").GetGuid();
        Assert.Equal("agent_generated", leaderBody.GetProperty("source_kind").GetString());
        Assert.Equal(pipelineId, leaderBody.GetProperty("base_prompt_pipeline_id").GetGuid());
        Assert.Equal("meeting", leaderBody.GetProperty("provenance").GetProperty("created_by_agent_slug").GetString());

        // Rejects 'pack' source_kind on user/agent draft endpoints.
        using var badSourceKind = await client.PostAsJsonAsync("/api/v1/agents", new
        {
            display_name = "Spoof Pack",
            layer = "execution",
            source_kind = "pack"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badSourceKind.StatusCode);

        // Directory listing & published snapshot preserve provenance and base_prompt_pipeline_id.
        var directory = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        var dirItem = directory!.Single(a => a.GetProperty("id").GetGuid() == leaderId);
        Assert.Equal("agent_generated", dirItem.GetProperty("source_kind").GetString());
        Assert.Equal(pipelineId, dirItem.GetProperty("base_prompt_pipeline_id").GetGuid());
        Assert.Equal("meeting", dirItem.GetProperty("provenance").GetProperty("created_by_agent_slug").GetString());

        using var pubLeader = await client.PostAsync($"/api/v1/agents/{leaderId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, pubLeader.StatusCode);
        var agentVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agents/{leaderId}/versions");
        Assert.Equal(pipelineId, agentVersions![0].GetProperty("snapshot").GetProperty("base_prompt_pipeline_id").GetGuid());
        Assert.Equal("agent_generated", agentVersions[0].GetProperty("snapshot").GetProperty("source_kind").GetString());

        // Create custom mode where binding keeps write_file WITHOUT write resource grant -> blocked by ModePublishGate!
        using var createUnsafeMode = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "unsafe-gate-mode",
            display_name = "Unsafe Gate Mode",
            nodes = new object[]
            {
                new
                {
                    node_key = "leader",
                    agent_definition_id = leaderId,
                    layer = "operation",
                    label = "Leader",
                    config = new { conversation = true },
                    relationship = new { duty = "Coordinate only" }
                }
            },
            edges = Array.Empty<object>(),
            bindings = new object[]
            {
                new
                {
                    node_key = "leader",
                    envelope = new { resources = new { read = new[] { "" } } }
                }
            }
        });
        Assert.Equal(HttpStatusCode.Created, createUnsafeMode.StatusCode);
        var unsafeModeId = (await createUnsafeMode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var blockedPub = await client.PostAsync($"/api/v1/agent-modes/{unsafeModeId}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, blockedPub.StatusCode);
        Assert.Contains("mutating_tool_without_write_grant", await blockedPub.Content.ReadAsStringAsync());

        // Switching tools off via "*": false satisfies ModePublishGate consistently across services.
        using var fixWithWildcardOff = await client.PutAsJsonAsync($"/api/v1/agent-modes/{unsafeModeId}/draft", new
        {
            bindings = new object[]
            {
                new
                {
                    node_key = "leader",
                    tool_switches = new Dictionary<string, bool> { ["*"] = false },
                    envelope = new { resources = new { read = new[] { "" } } }
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, fixWithWildcardOff.StatusCode);

        using var okPub = await client.PostAsync($"/api/v1/agent-modes/{unsafeModeId}/publish", null);
        Assert.True(okPub.IsSuccessStatusCode, await okPub.Content.ReadAsStringAsync());

        // OperationalRoleKey matches git_steward by slug even when Role differs.
        var stewardBySlug = new RuntimeAgentDefinition("git_steward", "operation", "custom_git_helper", "task", [], false, "read");
        Assert.Equal("git_steward", FullDuplexRunEngine.OperationalRoleKey(stewardBySlug));
    }

    [Fact]
    public void GraphTierAndNestedDispatch_EnforcesTwoLayerEdges_StageOrdering_AndPresetNarrowing()
    {
        var coordinatorWithSelfConfigTools = new RuntimeAgentDefinition(
            "meeting", "operation", "session_coordinator", "session", ["user.respond"], true, "manage")
        {
            AllowedTools = [CoreVirtualToolPolicy.TinadecInspectToolId, CoreVirtualToolPolicy.TinadecModeApplyToolId]
        };

        // tinadec_* virtual tools do NOT turn a coordinator into solo_dispatch.
        Assert.Equal(FrozenGraphTiers.FreeForm,
            FrozenRunConfigurationResolverProbe.DeriveTier([coordinatorWithSelfConfigTools], hasDeclaredEdges: false, "meeting"));
        Assert.Equal(FrozenGraphTiers.Deterministic,
            FrozenRunConfigurationResolverProbe.DeriveTier([coordinatorWithSelfConfigTools], hasDeclaredEdges: true, "meeting"));

        // Explicit orchestration_tier overrides fallback inference (including 'solo' alias).
        Assert.Equal(FrozenGraphTiers.SoloDispatch,
            FrozenRunConfigurationResolverProbe.DeriveTier([coordinatorWithSelfConfigTools], hasDeclaredEdges: true, "meeting", "solo"));
        var soloWithReadTool = coordinatorWithSelfConfigTools with { AllowedTools = ["read_file"] };
        Assert.Equal(FrozenGraphTiers.Deterministic,
            FrozenRunConfigurationResolverProbe.DeriveTier([soloWithReadTool], hasDeclaredEdges: true, "meeting", "deterministic"));

        // SpaceCompositionPolicy.Narrow preserves non-solo preset tier when MultiAgent == true, and narrows to SoloDispatch when !MultiAgent.
        var pipelineGraph = new FrozenGraph(
            FrozenGraphTiers.Deterministic,
            "meeting",
            "leader",
            [
                new FrozenGraphNode("leader", "meeting", "operation", IsConversation: true) { AllowedDispatchTargets = ["m1"] },
                new FrozenGraphNode("stage1", "m1", "execution", IsConversation: false) { AllowedDispatchTargets = ["m2"] },
                new FrozenGraphNode("stage2", "m2", "execution", IsConversation: false),
                new FrozenGraphNode("orphan", "orphan_worker", "execution", IsConversation: false)
            ],
            [
                new DeclaredGraphEdge("e-leader-s1", "leader", "stage1"),
                new DeclaredGraphEdge("e-s1-s2", "stage1", "stage2")
            ]);

        var narrowedMulti = SpaceCompositionPolicy.Narrow(pipelineGraph, [coordinatorWithSelfConfigTools], new SpaceRunOptions(PlanFirst: true, MultiAgent: true));
        Assert.Equal(FrozenGraphTiers.Deterministic, narrowedMulti.Tier);
        var narrowedSolo = SpaceCompositionPolicy.Narrow(pipelineGraph, [coordinatorWithSelfConfigTools], new SpaceRunOptions(PlanFirst: true, MultiAgent: false));
        Assert.Equal(FrozenGraphTiers.SoloDispatch, narrowedSolo.Tier);

        // Directed BFS reachability allows 2-hop pipeline stages (m1 and m2), denies orphan and operation backflow.
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "m1"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "m2"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "orphan_worker"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "meeting"));

        // Per-dispatcher 2-layer mixed nesting: m1 -> m2 allowed; m2 -> m1 denied; m1 -> meeting (backflow) denied.
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "m2", dispatcherSlug: "m1"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "m1", dispatcherSlug: "m2"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "orphan_worker", dispatcherSlug: "m1"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(pipelineGraph, "meeting", dispatcherSlug: "m1"));

        // Per-dispatcher roster & max_depth enforcement + stage ordering instructions.
        var m1Def = WorkerDef("m1", "Stage 1 implementer", ["read_file", CoreVirtualToolPolicy.TaskDispatchToolId], ["m2"], 0);
        var m2Def = WorkerDef("m2", "Stage 2 verifier", ["read_file"], null, 1);
        var orphanDef = WorkerDef("orphan_worker", "Unconnected", ["read_file"], null, 2);
        var config = new FrozenRunConfigurationV1(
            FrozenRunConfigurationV1.CurrentSchemaVersion, "hash", 1, Guid.NewGuid(), "mode:pipeline:1", "ask",
            new SpawnPolicy(MaxDepth: 2, MaxAgentsPerRun: 8, MaxParallelWorkers: 4),
            new SchedulingPolicy(2, 2, true),
            new SupervisionPolicy(true, 2),
            new ContextPolicy(8192, 24, true),
            new MemoryPolicy(true, 8, [], []),
            new ToolRuntimePolicy("tools", true, true, 60, 1),
            [coordinatorWithSelfConfigTools with { AllowedDispatchTargets = ["m1"] }],
            [m1Def, m2Def, orphanDef],
            [])
        {
            Graph = pipelineGraph
        };

        // Run-level frozen DispatchRoster covers both coordinator target (m1) and m1's sub-target (m2), excluding orphan.
        var runRoster = FullDuplexRunEngine.ComputeDispatchRoster(config);
        Assert.Equal(["m1", "m2"], runRoster.Select(r => r.Id).ToArray());

        // Coordinator only sees m1; m1 at depth 1 sees m2; at depth 2 (MaxDepth) sees empty!
        Assert.Equal(["m1"], FullDuplexRunEngine.DispatchRosterForWorker(config, "meeting", currentDepth: 0).Select(r => r.Id).ToArray());
        Assert.Equal(["m2"], FullDuplexRunEngine.DispatchRosterForWorker(config, "m1", currentDepth: 1).Select(r => r.Id).ToArray());
        Assert.Empty(FullDuplexRunEngine.DispatchRosterForWorker(config, "m1", currentDepth: 2));

        var subTask = new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "sub-verify", RequestedAgent = "m2" };
        var (sel, _) = FullDuplexRunEngine.ResolveRequestedWorker(config, subTask, dispatcherSlug: "m1", dispatcherDepth: 1);
        Assert.Equal("m2", sel.Agent.Id);

        Assert.Throws<FullDuplexRunEngine.WorkerAssignmentException>(() =>
            FullDuplexRunEngine.ResolveRequestedWorker(config,
                new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "bad-sub", RequestedAgent = "orphan_worker" },
                dispatcherSlug: "m1",
                dispatcherDepth: 1));

        var stageInstructions = PlanningAgent.BuildStageOrderingInstructions(config);
        Assert.Contains("m1 -> m2", stageInstructions);
    }

    [Fact]
    public async Task TaskScopedWorktrees_IsolateParallelWorkers_InheritViaLineage_AndFreezeAllWorkspacePromptFragments()
    {
        using var client = _factory!.CreateClient();
        await InstallBundledGraphSeedPackAsync(client);

        var repoRoot = Path.Combine(_factory.WorkspacePath, "git-project");
        var wt1 = Path.Combine(repoRoot, ".tinadec", "worktrees", "worker-1");
        var wt2 = Path.Combine(repoRoot, ".tinadec", "worktrees", "worker-2");
        Directory.CreateDirectory(wt1);
        Directory.CreateDirectory(wt2);

        var projectRes = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Worktree Lineage", path = repoRoot });
        Assert.Equal(HttpStatusCode.Created, projectRes.StatusCode);
        var projectId = (await projectRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var sessionRes = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Lineage Session" });
        Assert.Equal(HttpStatusCode.Created, sessionRes.StatusCode);
        var sessionId = (await sessionRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Create both a meeting-targeted fragment and a search-worker-targeted fragment, then freeze run configuration.
        using var fragMeeting = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "meeting-only-rule",
            title = "Meeting Rule",
            scope = "agent",
            target_agent_slug = "meeting",
            category = "policy",
            priority = 10,
            enabled = true,
            content = "MEETING_SECRET_FRAGMENT_MARKER"
        });
        Assert.True(fragMeeting.IsSuccessStatusCode, await fragMeeting.Content.ReadAsStringAsync());

        using var fragWorker = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "search-worker-rule",
            title = "Search Worker Rule",
            scope = "agent",
            target_agent_slug = "search",
            category = "domain",
            priority = 20,
            enabled = true,
            content = "SEARCH_WORKER_FROZEN_FRAGMENT_MARKER"
        });
        Assert.True(fragWorker.IsSuccessStatusCode, await fragWorker.Content.ReadAsStringAsync());

        var cfgResolver = _factory.Services.GetRequiredService<IAgentRuntimeConfigurationResolver>();
        var frozen = await cfgResolver.ResolveAsync(sessionId, "ask");
        Assert.NotNull(frozen.FrozenPromptFragments);
        Assert.Contains(frozen.FrozenPromptFragments!, f => f.Key == "meeting-only-rule");
        Assert.Contains(frozen.FrozenPromptFragments!, f => f.Key == "search-worker-rule");

        // Assembling worker prompt from FrozenPromptFragments (IncludeLiveFragments: false) includes worker fragment and excludes meeting fragment.
        var assembler = _factory.Services.GetRequiredService<IPromptAssembler>();
        var workerPrompt = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            "search",
            ContextPack: null,
            SystemPrompt: "Base search worker prompt.",
            IncludeLiveFragments: false)
        {
            AgentSlug = "search",
            AgentLayer = "execution",
            FrozenFragments = frozen.FrozenPromptFragments
        });
        Assert.Contains("SEARCH_WORKER_FROZEN_FRAGMENT_MARKER", workerPrompt.Instructions);
        Assert.DoesNotContain("MEETING_SECRET_FRAGMENT_MARKER", workerPrompt.Instructions);

        // Task-scoped worktree isolation + DispatchedByTaskId inheritance.
        var msgRes = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Parallel worktrees" });
        var msgId = (await msgRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, msgId);

        var parentTask1 = Guid.NewGuid();
        var siblingTask2 = Guid.NewGuid();
        var childSubTask = Guid.NewGuid();
        var unrelatedTask3 = Guid.NewGuid();

        var checkpoint = new FullDuplexCheckpointV1
        {
            SessionId = sessionId,
            RunId = run.Id,
            Phase = "executing",
            Tasks =
            [
                new DurableTaskNode { TaskId = parentTask1, TaskKey = "worker-1", Title = "W1", Status = "waiting", Worktree = true },
                new DurableTaskNode { TaskId = siblingTask2, TaskKey = "worker-2", Title = "W2", Status = "running", Worktree = true },
                new DurableTaskNode { TaskId = childSubTask, TaskKey = "sub-1", Title = "Sub1", Status = "running", DispatchedByTaskId = parentTask1 },
                new DurableTaskNode { TaskId = unrelatedTask3, TaskKey = "plain-3", Title = "Plain", Status = "running" }
            ]
        };
        var lifecycleMgr = _factory.Services.GetRequiredService<ILifecycleManager>();
        await lifecycleMgr.SaveRunCheckpointAsync(
            run.Id.ToString(),
            new RunCheckpointWrite(
                0,
                "executing",
                JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                IdempotencyKey: "test-lineage"));

        var leases = _factory.Services.GetRequiredService<IResourceLeaseService>();
        Assert.True((await leases.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Worktree, wt1, Exclusive: true),
            sessionId, run.Id, TaskId: parentTask1, Purpose: ResourceLeasePurposes.Assignment))).Granted);
        Assert.True((await leases.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Worktree, wt2, Exclusive: true),
            sessionId, run.Id, TaskId: siblingTask2, Purpose: ResourceLeasePurposes.Assignment))).Granted);

        var targetResolver = _factory.Services.GetRequiredService<IToolExecutionTargetResolver>();
        var resolvedW1 = await targetResolver.ResolveAsync(sessionId, run.Id, parentTask1, "read_file", repoRoot);
        Assert.False(resolvedW1.IsRejected, resolvedW1.Error);
        Assert.Equal(Path.GetFullPath(wt1), resolvedW1.Target!.RootPath);

        var resolvedW2 = await targetResolver.ResolveAsync(sessionId, run.Id, siblingTask2, "read_file", repoRoot);
        Assert.False(resolvedW2.IsRejected, resolvedW2.Error);
        Assert.Equal(Path.GetFullPath(wt2), resolvedW2.Target!.RootPath);

        // Depth-2 sub-task inherits parentTask1's worktree via ITaskLineageLookup!
        var resolvedChild = await targetResolver.ResolveAsync(sessionId, run.Id, childSubTask, "read_file", repoRoot);
        Assert.False(resolvedChild.IsRejected, resolvedChild.Error);
        Assert.Equal(Path.GetFullPath(wt1), resolvedChild.Target!.RootPath);

        // Sibling task without worktree (and no unscoped run worktree) uses normal workspace (Target == null).
        var resolvedUnrelated = await targetResolver.ResolveAsync(sessionId, run.Id, unrelatedTask3, "read_file", repoRoot);
        Assert.False(resolvedUnrelated.IsRejected, resolvedUnrelated.Error);
        Assert.Null(resolvedUnrelated.Target);

        // CoreTaskDispatchTool schema exposes optional 'worktree' boolean, and PlannedTask.Worktree materializes cleanly.
        var dispatchSchema = CoreTaskDispatchTool.ManifestEntry().InputSchema;
        Assert.True(dispatchSchema.GetProperty("properties").TryGetProperty("worktree", out var wtProp));
        Assert.Equal("boolean", wtProp.GetProperty("type").GetString());
        var materialized = FullDuplexRunEngine.ValidateAndMaterializeGraph(
            [new PlannedTask { TaskKey = "iso", Title = "Isolated work", Worktree = true }], 4);
        var isoNode = Assert.Single(materialized);
        Assert.True(isoNode.Worktree);

        isoNode.TaskWorktreePath = wt1;
        isoNode.TaskWorktreeBranch = "tinadec/task-iso";
        isoNode.DispatchedByTaskId = parentTask1;
        isoNode.WorkerHandle = "global_engineering#1";
        isoNode.Status = "completed";
        isoNode.ResultStatus = "completed";
        isoNode.ResultSummary = "Committed isolated work.";

        using var waitDoc = JsonDocument.Parse(FullDuplexRunEngine.BuildTaskWaitResult([isoNode]));
        var waitEntry = Assert.Single(waitDoc.RootElement.GetProperty("tasks").EnumerateArray());
        Assert.Equal(wt1, waitEntry.GetProperty("worktree_path").GetString());
        Assert.Equal("tinadec/task-iso", waitEntry.GetProperty("branch").GetString());
        Assert.Equal(parentTask1, waitEntry.GetProperty("dispatched_by_task_id").GetGuid());

        var meetingEvidence = FullDuplexRunEngine.FormatTaskEvidenceForMeeting(isoNode);
        Assert.Contains(wt1, meetingEvidence);
        Assert.Contains("tinadec/task-iso", meetingEvidence);
        Assert.Contains(parentTask1.ToString(), meetingEvidence);
    }

    private static RuntimeAgentDefinition WorkerDef(
        string id,
        string description,
        IReadOnlyList<string> tools,
        IReadOnlyList<string>? allowedDispatchTargets,
        int order) =>
        new(id, "execution", "task_executor", "task", [], DirectUserOutput: false, "read")
        {
            AgentDefinitionId = Guid.NewGuid(),
            AgentVersionId = Guid.NewGuid(),
            VersionContentHash = $"hash-{id}",
            AllowedTools = tools,
            AllowedDispatchTargets = allowedDispatchTargets,
            Description = description,
            RosterOrder = order
        };

    private static class FrozenRunConfigurationResolverProbe
    {
        public static string DeriveTier(
            IReadOnlyList<RuntimeAgentDefinition> operation,
            bool hasDeclaredEdges,
            string? conversationSlug,
            string? explicitTier = null) =>
            AgentRuntimeConfigurationResolver.DeriveGraphTier(operation, hasDeclaredEdges, conversationSlug, explicitTier);
    }

    private static async Task InstallBundledGraphSeedPackAsync(HttpClient client)
    {
        var manifestPath = FindGraphSeedManifestPath();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, Encoding.UTF8));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var dto = JsonSerializer.Deserialize<AgentPackManifestDto>(document.RootElement.GetRawText(), options)!;
        var canonical = JsonCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(dto, options));
        var digest = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        var envelope = new
        {
            manifest = document.RootElement,
            integrity = new { algorithm = "sha256", digest }
        };
        using var preview = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(preview.IsSuccessStatusCode, $"GraphSeedPack preview failed: {preview.StatusCode} {await preview.Content.ReadAsStringAsync()}");
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        var previewId = previewBody.GetProperty("preview_id").GetGuid();

        using var installReq = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.graph.seed-pack")
        {
            Content = JsonContent.Create(new { preview_id = previewId, envelope })
        };
        installReq.Headers.TryAddWithoutValidation("Idempotency-Key", $"install-seed-{Guid.NewGuid():N}");
        using var install = await client.SendAsync(installReq);
        Assert.True(install.IsSuccessStatusCode, $"GraphSeedPack install failed: {install.StatusCode} {await install.Content.ReadAsStringAsync()}");
    }

    private static string FindGraphSeedManifestPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "desktop", "src", "agentPacks", "GraphSeedPack", "manifest.json");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException("Could not locate apps/desktop/src/agentPacks/GraphSeedPack/manifest.json.");
    }
}
