using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using TinadecCore.Prompts;

namespace TinadecCore.Api.Tests;

public sealed class PromptEngineTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("prompt-engine-tests");
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root, managed: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PromptFragments_AvoidDoubleJsonEncoding_UnwrapLegacy_AndFilterByAgentGuidOrSlug()
    {
        using var client = _factory!.CreateClient();
        const string markdown = "# Rule\nAlways verify \"quotes\", paths\\like\\this, and 中文输出.";

        var createResp = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "rule.encoding",
            title = "Encoding Rule",
            scope = "workspace",
            target_agent_id = "meeting",
            category = "rules",
            priority = 10,
            enabled = true,
            content = markdown
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        Assert.NotNull(createResp.Headers.ETag);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var fragmentId = created.GetProperty("id").GetGuid();
        Assert.Equal(markdown, created.GetProperty("content").GetString());
        Assert.Equal("meeting", created.GetProperty("target_agent_slug").GetString());

        // Stored raw content in IContentStore must be valid single-encoded JSON (compatible with prompts.toml
        // JsonDocument.Parse) rather than double-encoded JSON ("\"# Rule...\"").
        var promptDbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PromptControlDbContext>>();
        var contentStore = _factory.Services.GetRequiredService<IContentStore>();
        await using (var db = await promptDbFactory.CreateDbContextAsync())
        {
            var fragRow = await db.Fragments.SingleAsync(x => x.Id == fragmentId);
            var verRow = await db.Versions.SingleAsync(x => x.Id == fragRow.CurrentVersionId);
            await using var stream = await contentStore.OpenReadAsync(new ContentReference(verRow.ContentReference, verRow.ContentHash, verRow.ContentLength, "application/json"));
            using var reader = new StreamReader(stream);
            var storedRaw = await reader.ReadToEndAsync();
            using var parsed = JsonDocument.Parse(storedRaw);
            Assert.Equal(JsonValueKind.String, parsed.RootElement.ValueKind);
            Assert.Equal(markdown, parsed.RootElement.GetString());
        }

        // Seed legacy double-encoded fragment and legacy { text } object fragment directly.
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var doubleEncodedPayload = JsonSerializer.Serialize(JsonSerializer.Serialize("# Legacy\nDouble-wrapped 中文"));
        var objectTextPayload = JsonSerializer.Serialize(new { text = "Legacy object wrapper text" });
        await AddRawFragmentAsync(promptDbFactory, contentStore, tenant, "legacy.double", "Legacy Double", doubleEncodedPayload, targetAgentSlug: null);
        await AddRawFragmentAsync(promptDbFactory, contentStore, tenant, "legacy.object", "Legacy Object", objectTextPayload, targetAgentSlug: null);

        var filtered = await client.GetFromJsonAsync<JsonElement>("/api/v1/prompt-fragments?target_agent_id=meeting&category=rules&enabled=true");
        var filteredItems = filtered.EnumerateArray().ToArray();
        Assert.Single(filteredItems);
        Assert.Equal(fragmentId, filteredItems[0].GetProperty("id").GetGuid());
        Assert.Equal(markdown, filteredItems[0].GetProperty("content").GetString());

        var allListed = await client.GetFromJsonAsync<JsonElement>("/api/v1/prompt-fragments");
        var allByKey = allListed.EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("content").GetString()!);
        Assert.Equal("# Legacy\nDouble-wrapped 中文", allByKey["legacy.double"]);
        Assert.Equal("Legacy object wrapper text", allByKey["legacy.object"]);
    }

    [Fact]
    public async Task PromptFragments_VersionsRollbackCloneAndBuiltInGuards_WorkEndToEnd()
    {
        using var client = _factory!.CreateClient();

        var createResp = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "versioned.rule",
            title = "Versioned Rule",
            scope = "workspace",
            category = "policy",
            content = "v1 baseline",
            change_summary = "initial"
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var rev1 = created.GetProperty("revision").GetInt64();

        // POST /versions appends v2 and advances revision
        var v2Req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/prompt-fragments/{id}/versions")
        {
            Content = JsonContent.Create(new
            {
                content = "v2 improved",
                changed_fields = new[] { "content" },
                change_summary = "clarified rule"
            })
        };
        v2Req.Headers.TryAddWithoutValidation("If-Match", $"\"{rev1}\"");
        var v2Resp = await client.SendAsync(v2Req);
        Assert.Equal(HttpStatusCode.OK, v2Resp.StatusCode);
        var v2Json = await v2Resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, v2Json.GetProperty("version").GetInt32());
        Assert.True(v2Json.GetProperty("is_active").GetBoolean());

        // Refresh current revision
        var listAfterV2 = await client.GetFromJsonAsync<JsonElement>("/api/v1/prompt-fragments");
        var rowAfterV2 = listAfterV2.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        var rev2 = rowAfterV2.GetProperty("revision").GetInt64();
        Assert.Equal("v2 improved", rowAfterV2.GetProperty("content").GetString());

        // Stale If-Match returns 412
        var stalePut = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/prompt-fragments/{id}")
        {
            Content = JsonContent.Create(new { content = "should fail" })
        };
        stalePut.Headers.TryAddWithoutValidation("If-Match", $"\"{rev1}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stalePut)).StatusCode);

        // PUT updates to v3
        var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/prompt-fragments/{id}")
        {
            Content = JsonContent.Create(new { content = "v3 regression", change_summary = "experiment" })
        };
        putReq.Headers.TryAddWithoutValidation("If-Match", $"\"{rev2}\"");
        var putResp = await client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        Assert.NotNull(putResp.Headers.ETag);
        var rev3 = (await putResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64();

        // GET /versions returns descending versions [3, 2, 1]
        var versions = await client.GetFromJsonAsync<JsonElement>($"/api/v1/prompt-fragments/{id}/versions");
        var versionItems = versions.EnumerateArray().ToArray();
        Assert.Equal(3, versionItems.Length);
        Assert.Equal(3, versionItems[0].GetProperty("version").GetInt32());
        Assert.True(versionItems[0].GetProperty("is_active").GetBoolean());
        var v1VersionId = versionItems[2].GetProperty("id").GetGuid();

        // POST /rollback by target_version restores v1 as immutable v4
        var rbReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/prompt-fragments/{id}/rollback")
        {
            Content = JsonContent.Create(new { target_version = 1, change_summary = "revert to v1" })
        };
        rbReq.Headers.TryAddWithoutValidation("If-Match", $"\"{rev3}\"");
        var rbResp = await client.SendAsync(rbReq);
        Assert.Equal(HttpStatusCode.OK, rbResp.StatusCode);
        Assert.NotNull(rbResp.Headers.ETag);
        var rbJson = await rbResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("v1 baseline", rbJson.GetProperty("content").GetString());
        var rev4 = rbJson.GetProperty("revision").GetInt64();

        // POST /rollback by version_id (to v2) also works
        var v2Id = versionItems[1].GetProperty("id").GetGuid();
        var rbByIdReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/prompt-fragments/{id}/rollback")
        {
            Content = JsonContent.Create(new { version_id = v2Id })
        };
        rbByIdReq.Headers.TryAddWithoutValidation("If-Match", $"\"{rev4}\"");
        var rbByIdResp = await client.SendAsync(rbByIdReq);
        Assert.Equal(HttpStatusCode.OK, rbByIdResp.StatusCode);
        Assert.Equal("v2 improved", (await rbByIdResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").GetString());

        // POST /clone with empty body produces unique editable copy
        var cloneEmptyReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/prompt-fragments/{id}/clone");
        var cloneEmptyResp = await client.SendAsync(cloneEmptyReq);
        Assert.Equal(HttpStatusCode.OK, cloneEmptyResp.StatusCode);
        Assert.NotNull(cloneEmptyResp.Headers.ETag);
        var cloneEmptyJson = await cloneEmptyResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(id, cloneEmptyJson.GetProperty("id").GetGuid());
        Assert.StartsWith("versioned.rule-copy", cloneEmptyJson.GetProperty("key").GetString()!, StringComparison.Ordinal);
        Assert.Equal("v2 improved", cloneEmptyJson.GetProperty("content").GetString());

        // POST /clone with custom body
        var cloneCustomResp = await client.PostAsJsonAsync($"/api/v1/prompt-fragments/{id}/clone", new
        {
            key = "versioned.rule.fork",
            title = "Forked Rule"
        });
        Assert.Equal(HttpStatusCode.OK, cloneCustomResp.StatusCode);
        var cloneCustomJson = await cloneCustomResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("versioned.rule.fork", cloneCustomJson.GetProperty("key").GetString());
        Assert.Equal("Forked Rule", cloneCustomJson.GetProperty("title").GetString());

        // Mark original built-in and verify 409 built_in_read_only on mutations, while clone still works
        var promptDbFactory = _factory.Services.GetRequiredService<IDbContextFactory<PromptControlDbContext>>();
        await using (var db = await promptDbFactory.CreateDbContextAsync())
        {
            var row = await db.Fragments.SingleAsync(x => x.Id == id);
            row.IsBuiltIn = true;
            await db.SaveChangesAsync();
        }

        var builtinPut = await client.PutAsJsonAsync($"/api/v1/prompt-fragments/{id}", new { content = "blocked" });
        Assert.Equal(HttpStatusCode.Conflict, builtinPut.StatusCode);
        var builtinRollback = await client.PostAsJsonAsync($"/api/v1/prompt-fragments/{id}/rollback", new { version_id = v1VersionId });
        Assert.Equal(HttpStatusCode.Conflict, builtinRollback.StatusCode);
        var builtinDelete = await client.DeleteAsync($"/api/v1/prompt-fragments/{id}");
        Assert.Equal(HttpStatusCode.Conflict, builtinDelete.StatusCode);
        var builtinClone = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/prompt-fragments/{id}/clone"));
        Assert.Equal(HttpStatusCode.OK, builtinClone.StatusCode);
        Assert.False((await builtinClone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("is_builtin").GetBoolean());
    }

    [Fact]
    public async Task FrozenPromptFragmentSnapshot_IsolatesMeetingAndWorkerTargetsWithoutLeakingOrDropping()
    {
        using var client = _factory!.CreateClient();
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var workerGuid = Guid.NewGuid();
        var meetingGuid = Guid.NewGuid();

        var fragService = _factory.Services.GetRequiredService<IPromptFragmentService>();
        await fragService.SaveAsync(null, new PromptFragmentSaveRequestDto
        {
            Key = "shared.global",
            Title = "Global Baseline",
            Scope = "workspace",
            Category = "general",
            Content = "GLOBAL_RULE_TEXT",
            Enabled = true
        });
        await fragService.SaveAsync(null, new PromptFragmentSaveRequestDto
        {
            Key = "agent.meeting",
            Title = "Meeting Guidance",
            Scope = "agent",
            TargetAgentId = meetingGuid.ToString(),
            TargetAgentSlug = "meeting",
            Category = "role",
            Content = "MEETING_ONLY_GUIDANCE",
            Enabled = true
        });
        await fragService.SaveAsync(null, new PromptFragmentSaveRequestDto
        {
            Key = "agent.worker",
            Title = "Worker Execution Guidance",
            Scope = "agent",
            TargetAgentId = workerGuid.ToString(),
            TargetAgentSlug = "worker",
            Category = "role",
            Content = "WORKER_ONLY_GUIDANCE",
            Enabled = true
        });
        await fragService.SaveAsync(null, new PromptFragmentSaveRequestDto
        {
            Key = "disabled.fragment",
            Title = "Disabled Guidance",
            Scope = "workspace",
            Category = "role",
            Content = "DISABLED_SHOULD_NOT_SNAPSHOT",
            Enabled = false
        });

        var source = _factory.Services.GetRequiredService<IFrozenPromptFragmentSource>();
        var snapshot = await source.SnapshotActiveFragmentsAsync(tenant.TenantId, tenant.WorkspaceId);

        // Snapshot MUST contain global + meeting + worker active fragments (never pre-filtered to meeting-only!)
        Assert.Contains(snapshot, f => f.Key == "shared.global");
        Assert.Contains(snapshot, f => f.Key == "agent.meeting");
        Assert.Contains(snapshot, f => f.Key == "agent.worker");
        Assert.DoesNotContain(snapshot, f => f.Key == "disabled.fragment");

        // Mutate live DB after freeze — shared snapshot must remain immutable.
        await fragService.SaveAsync(null, new PromptFragmentSaveRequestDto
        {
            Key = "post.freeze.live",
            Title = "Post Freeze Live",
            Scope = "workspace",
            Category = "general",
            Content = "POST_FREEZE_LIVE_LEAK",
            Enabled = true
        });

        var assembler = _factory.Services.GetRequiredService<IPromptAssembler>();

        // 1. Coordinator/meeting receives global + meeting, NEVER worker or post-freeze live fragments
        var meetingPrompt = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            AgentId: "meeting",
            ContextPack: null,
            SystemPrompt: "You coordinate.",
            FrozenFragments: snapshot,
            IncludeLiveFragments: false));
        Assert.Contains("GLOBAL_RULE_TEXT", meetingPrompt.Instructions, StringComparison.Ordinal);
        Assert.Contains("MEETING_ONLY_GUIDANCE", meetingPrompt.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_ONLY_GUIDANCE", meetingPrompt.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("POST_FREEZE_LIVE_LEAK", meetingPrompt.Instructions, StringComparison.Ordinal);

        // 2. Worker invoked by slug ("worker") gets global + worker without leaking meeting guidance
        var workerBySlug = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            AgentId: "worker",
            ContextPack: null,
            SystemPrompt: "You execute.",
            FrozenFragments: snapshot,
            IncludeLiveFragments: false));
        Assert.Contains("GLOBAL_RULE_TEXT", workerBySlug.Instructions, StringComparison.Ordinal);
        Assert.Contains("WORKER_ONLY_GUIDANCE", workerBySlug.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("MEETING_ONLY_GUIDANCE", workerBySlug.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("POST_FREEZE_LIVE_LEAK", workerBySlug.Instructions, StringComparison.Ordinal);

        // 3. Worker invoked by GUID ALSO matches via TargetAgentId GUID
        var workerByGuid = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            AgentId: workerGuid.ToString(),
            ContextPack: null,
            SystemPrompt: "You execute by GUID.",
            FrozenFragments: snapshot,
            IncludeLiveFragments: false));
        Assert.Contains("WORKER_ONLY_GUIDANCE", workerByGuid.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("MEETING_ONLY_GUIDANCE", workerByGuid.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptPipeline_ExecutesTopologicalOrderConditionsTokensPlacementsCycles_AndCjkEstimation()
    {
        _ = _factory!.CreateClient();
        var assembler = _factory.Services.GetRequiredService<IPromptAssembler>();

        var fragId = Guid.NewGuid();
        var frozen = new[]
        {
            new FrozenPromptFragment(
                fragId,
                "security.check",
                "Security Check",
                "workspace",
                null,
                null,
                "rules",
                10,
                "FRAGMENT_SECURITY_CONTENT",
                1,
                "hash-1")
        };

        var workspace = new FrozenWorkspaceBinding(
            Guid.NewGuid(),
            @"C:\repo\tinadec-sample",
            [],
            IsGitRepository: true,
            GitBranch: "feat/prompt-engine",
            TopLevelEntries: ["src/", "README.md"],
            TopLevelTruncated: false,
            WorkspacePathContracts.AbsoluteInRoot);

        // Nodes in array intentionally REVERSED from edge order to prove topological sort drives execution.
        var graphJson = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "n_assemble", type = "assemble", title = "Final Wrap", header = "## Output Contract", footer = "Stay deterministic." },
                new { id = "n_frag", type = "fragment_ref", title = "Ref Security", fragment_id = fragId.ToString() },
                new { id = "n_skills", type = "skills_index", title = "Skills" },
                new { id = "n_ws", type = "workspace_rules", title = "WS Rules", template = "Root is {{workspace_root}}" },
                new { id = "n_blocked_child", type = "template", title = "Blocked Child", template = "SHOULD_BE_FILTERED_OUT" },
                new { id = "n_cond_fail", type = "condition", title = "NonMatching Mode", field = "mode_slug", value = "audit-only" },
                new { id = "n_pass_child", type = "template", title = "Passed Child", template = "CONDITIONAL_EXEC_CAP_MATCHED" },
                new { id = "n_cond_pass", type = "condition", title = "Execution Check", layer = "execution", has_workspace = true, has_tools = true, capability = "repo_write" },
                new { id = "n_pre_sys", type = "template", title = "Pre-System Banner", placement = "before_system_prompt", template = "Agent={{agent_name}} ({{agent_slug}}) Mode={{mode_name}} [{{mode_slug}}]" }
            },
            edges = new object[]
            {
                new { source = "n_pre_sys", target = "n_ws" },
                new { source = "n_ws", target = "n_skills" },
                new { source = "n_skills", target = "n_frag" },
                new { source = "n_frag", target = "n_assemble" },
                new { source = "n_cond_fail", target = "n_blocked_child" },
                new { source = "n_cond_pass", target = "n_pass_child" }
            }
        });

        var result = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            AgentId: "implementer",
            ContextPack: new ContextPack { TokenBudget = 8192 },
            SystemPrompt: "SYSTEM_PROMPT_BODY",
            PromptPipelineId: Guid.NewGuid(),
            PromptVersionId: Guid.NewGuid(),
            PromptVersionContentHash: "pv-hash",
            PromptGraphJson: graphJson)
        {
            Workspace = workspace,
            AgentName = "Implementer Agent",
            AgentSlug = "implementer",
            AgentLayer = "execution",
            ModeName = "Build Mode",
            ModeSlug = "build-mode",
            Capabilities = ["repo_read", "repo_write"],
            HasTools = true,
            FrozenFragments = frozen,
            WorkspaceRules = ["Never commit secrets."],
            SkillsIndexText = "Available skills:\n- dotnet-build: Verify build"
        });

        // Placement before_system_prompt renders BEFORE SYSTEM_PROMPT_BODY
        var bannerIdx = result.Instructions.IndexOf("Agent=Implementer Agent (implementer) Mode=Build Mode [build-mode]", StringComparison.Ordinal);
        var sysIdx = result.Instructions.IndexOf("SYSTEM_PROMPT_BODY", StringComparison.Ordinal);
        Assert.True(bannerIdx >= 0 && sysIdx > bannerIdx, "before_system_prompt node must appear before system prompt");

        // Topological dependency order: ws -> skills -> frag -> assemble
        var wsIdx = result.Instructions.IndexOf("Root is C:\\repo\\tinadec-sample", StringComparison.Ordinal);
        var skillsIdx = result.Instructions.IndexOf("dotnet-build: Verify build", StringComparison.Ordinal);
        var fragIdx = result.Instructions.IndexOf("FRAGMENT_SECURITY_CONTENT", StringComparison.Ordinal);
        var assembleIdx = result.Instructions.IndexOf("## Output Contract", StringComparison.Ordinal);
        Assert.True(wsIdx > sysIdx && skillsIdx > wsIdx && fragIdx > skillsIdx && assembleIdx > fragIdx,
            "Nodes must execute along topological edge order rather than raw array order.");

        // Conditions filter failing branch and permit matching branch
        Assert.DoesNotContain("SHOULD_BE_FILTERED_OUT", result.Instructions, StringComparison.Ordinal);
        Assert.Contains("CONDITIONAL_EXEC_CAP_MATCHED", result.Instructions, StringComparison.Ordinal);

        // fragment_ref consumes fragment once without duplicate trailing emission
        Assert.Equal(1, CountOccurrences(result.Instructions, "FRAGMENT_SECURITY_CONTENT"));

        // Cyclic graph falls back to declaration order and records warning
        var cyclicGraph = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "a", type = "template", template = "CYCLE_FIRST" },
                new { id = "b", type = "template", template = "CYCLE_SECOND" }
            },
            edges = new object[]
            {
                new { source = "a", target = "b" },
                new { source = "b", target = "a" }
            }
        });
        var cyclicResult = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            AgentId: "meeting",
            ContextPack: null,
            PromptGraphJson: cyclicGraph));
        var idxFirst = cyclicResult.Instructions.IndexOf("CYCLE_FIRST", StringComparison.Ordinal);
        var idxSecond = cyclicResult.Instructions.IndexOf("CYCLE_SECOND", StringComparison.Ordinal);
        Assert.True(idxFirst >= 0 && idxSecond > idxFirst);
        Assert.Contains(cyclicResult.Warnings, w => w.Contains("cycle", StringComparison.OrdinalIgnoreCase));

        // CJK token estimation: 30 CJK chars should estimate 20 tokens (Ceiling(30/1.5)), much higher than len/4 (7)
        const string cjkOnly = "中文提示词长度密度评估验证用例一共正好三十个纯中文汉字字符串";
        const string asciiOnly = "ascii prompt token length eval";
        Assert.Equal(30, cjkOnly.Length);
        Assert.Equal(30, asciiOnly.Length);
        var cjkRes = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest("a", null, PromptGraphJson: JsonSerializer.Serialize(new { nodes = new[] { new { id = "t", type = "template", template = cjkOnly } } })));
        var asciiRes = await assembler.AssembleAsync(new FrozenPromptAssemblyRequest("a", null, PromptGraphJson: JsonSerializer.Serialize(new { nodes = new[] { new { id = "t", type = "template", template = asciiOnly } } })));
        var cjkLayer = cjkRes.Layers.Single(l => l.Kind == "pipeline_template");
        var asciiLayer = asciiRes.Layers.Single(l => l.Kind == "pipeline_template");
        Assert.Equal(20, cjkLayer.EstimatedTokens);
        Assert.Equal(8, asciiLayer.EstimatedTokens);
        Assert.True(cjkLayer.EstimatedTokens > asciiLayer.EstimatedTokens);
    }

    [Fact]
    public async Task PromptContextPreviewEndpoint_ReturnsLayeredBreakdown_AndSupportsDraftAndPersistedPipeline()
    {
        using var client = _factory!.CreateClient();

        // Create two prompt fragments: one enabled matching, one disabled to explicitly preview via include_fragment_ids
        var activeFragResp = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "preview.active",
            title = "Active Preview Fragment",
            scope = "workspace",
            target_agent_id = "meeting",
            category = "rules",
            content = "ACTIVE_PREVIEW_FRAGMENT_TEXT"
        });
        Assert.Equal(HttpStatusCode.OK, activeFragResp.StatusCode);

        var draftFragResp = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
        {
            key = "preview.draft",
            title = "Draft Disabled Fragment",
            scope = "workspace",
            category = "rules",
            enabled = false,
            content = "EXPLICIT_INCLUDED_FRAGMENT_TEXT"
        });
        Assert.Equal(HttpStatusCode.OK, draftFragResp.StatusCode);
        var draftFragId = (await draftFragResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Create a persisted PromptPipeline too so pipeline_id lookup path is verified
        var pipelineResp = await client.PostAsJsonAsync("/api/v1/prompt-pipelines", new
        {
            slug = "preview-persisted-pipeline",
            display_name = "Persisted Preview Pipeline",
            graph = new
            {
                nodes = new object[]
                {
                    new { id = "pt1", type = "template", title = "Persisted Banner", template = "PERSISTED_PIPELINE_FOR_{{agent_slug}}" },
                    new { id = "pa1", type = "assemble", title = "Persisted Wrap" }
                },
                edges = new object[]
                {
                    new { source = "pt1", target = "pa1" }
                }
            }
        });
        Assert.Equal(HttpStatusCode.Created, pipelineResp.StatusCode);
        var persistedPipelineId = (await pipelineResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var persistedPreviewResp = await client.PostAsJsonAsync("/api/v1/prompt-context/preview", new
        {
            agent_id = "meeting",
            pipeline_id = persistedPipelineId
        });
        Assert.Equal(HttpStatusCode.OK, persistedPreviewResp.StatusCode);
        var persistedPreview = await persistedPreviewResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("PERSISTED_PIPELINE_FOR_meeting", persistedPreview.GetProperty("assembled_prompt").GetString()!, StringComparison.Ordinal);
        Assert.Contains("ACTIVE_PREVIEW_FRAGMENT_TEXT", persistedPreview.GetProperty("assembled_prompt").GetString()!, StringComparison.Ordinal);

        var agents = await client.GetFromJsonAsync<JsonElement>("/api/v1/agents");
        var meetingAgent = agents.EnumerateArray().FirstOrDefault(a => string.Equals(a.GetProperty("slug").GetString(), "meeting", StringComparison.OrdinalIgnoreCase));
        var agentIdArg = meetingAgent.ValueKind == JsonValueKind.Object ? meetingAgent.GetProperty("id").GetGuid().ToString() : "meeting";

        var previewResp = await client.PostAsJsonAsync("/api/v1/prompt-context/preview", new
        {
            agent_id = agentIdArg,
            mode = "solo",
            pipeline_id = persistedPipelineId,
            draft_system_prompt = "DRAFT_SYSTEM_PROMPT_OVERRIDE",
            draft_mode_instructions = "DRAFT_MODE_INSTRUCTIONS_OVERRIDE",
            draft_pipeline_graph = new
            {
                nodes = new object[]
                {
                    new { id = "t1", type = "template", title = "Draft Banner", template = "Preview for {{agent_slug}} in {{mode_slug}}" },
                    new { id = "a1", type = "assemble", title = "Draft Footer", header = "CHECKLIST" }
                },
                edges = new object[]
                {
                    new { source = "t1", target = "a1" }
                }
            },
            include_fragment_ids = new[] { draftFragId },
            user_content = "Preview user turn input with 中文内容"
        });
        Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);
        var preview = await previewResp.Content.ReadFromJsonAsync<JsonElement>();

        var assembled = preview.GetProperty("assembled_prompt").GetString()!;
        Assert.Contains("DRAFT_SYSTEM_PROMPT_OVERRIDE", assembled, StringComparison.Ordinal);
        Assert.Contains("DRAFT_MODE_INSTRUCTIONS_OVERRIDE", assembled, StringComparison.Ordinal);
        Assert.Contains("Preview for meeting in solo", assembled, StringComparison.Ordinal);
        Assert.DoesNotContain("PERSISTED_PIPELINE_FOR_meeting", assembled, StringComparison.Ordinal);
        Assert.Contains("EXPLICIT_INCLUDED_FRAGMENT_TEXT", assembled, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIVE_PREVIEW_FRAGMENT_TEXT", assembled, StringComparison.Ordinal);
        Assert.Contains("Preview user turn input with 中文内容", assembled, StringComparison.Ordinal);

        var totalTokens = preview.GetProperty("total_estimated_tokens").GetInt32();
        Assert.True(totalTokens > 0);
        Assert.Equal(totalTokens, preview.GetProperty("estimated_tokens").GetInt32());

        var layers = preview.GetProperty("layers").EnumerateArray().ToArray();
        Assert.NotEmpty(layers);
        Assert.Contains(layers, l => l.GetProperty("kind").GetString() == "system_prompt" && l.GetProperty("source").GetString() == "agent");
        Assert.Contains(layers, l => l.GetProperty("kind").GetString() == "mode_instructions");
        Assert.Contains(layers, l => l.GetProperty("kind").GetString() == "pipeline_template");
        Assert.Contains(layers, l => l.GetProperty("kind").GetString() == "prompt_fragment" && l.GetProperty("estimated_tokens").GetInt32() > 0);
    }

    [Fact]
    public async Task ManagedStorage_ProjectsPromptFragmentsToPromptsToml_AndReadsCleanly()
    {
        var managedRoot = ApiTestStorage.CreateRoot("prompt-engine-managed");
        Directory.CreateDirectory(managedRoot);
        try
        {
            using var managedFactory = new Factory(managedRoot, managed: true);
            using var client = managedFactory.CreateClient();
            const string body = "# TOML Roundtrip\nRule line with \"quotes\" and 中文。";

            var postResp = await client.PostAsJsonAsync("/api/v1/prompt-fragments", new
            {
                key = "toml.roundtrip",
                title = "TOML Roundtrip Fragment",
                scope = "workspace",
                category = "rules",
                content = body
            });
            Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);
            var created = await postResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(body, created.GetProperty("content").GetString());

            var tomlFiles = Directory.GetFiles(managedRoot, "prompts.toml", SearchOption.AllDirectories);
            Assert.NotEmpty(tomlFiles);
            var toml = await File.ReadAllTextAsync(tomlFiles[0]);
            Assert.Contains("toml.roundtrip", toml, StringComparison.Ordinal);

            var fragService = managedFactory.Services.GetRequiredService<IPromptFragmentService>();
            var items = await fragService.ListAsync();
            var roundtripped = items.Single(x => x.Key == "toml.roundtrip");
            Assert.Equal(body, roundtripped.Content);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(managedRoot)) Directory.Delete(managedRoot, true);
        }
    }

    private static async Task AddRawFragmentAsync(
        IDbContextFactory<PromptControlDbContext> factory,
        IContentStore store,
        TenantContext tenant,
        string key,
        string title,
        string rawStoredContent,
        string? targetAgentSlug)
    {
        var now = DateTimeOffset.UtcNow;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawStoredContent));
        var storedRef = await store.PutAsync(new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "prompt-fragment", "application/json", stream));
        await using var db = await factory.CreateDbContextAsync();
        var fragId = Guid.NewGuid();
        var verId = Guid.NewGuid();
        db.Fragments.Add(new PromptFragmentRecord
        {
            Id = fragId,
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            Scope = "workspace",
            Key = key,
            Title = title,
            TargetAgentSlug = targetAgentSlug,
            Category = "rules",
            Priority = 0,
            Enabled = true,
            Revision = 1,
            CurrentVersionId = verId,
            CreatedByPrincipalId = tenant.PrincipalId,
            UpdatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Versions.Add(new PromptFragmentVersionRecord
        {
            Id = verId,
            FragmentId = fragId,
            Version = 1,
            ContentReference = storedRef.Value,
            ContentHash = storedRef.Sha256,
            ContentLength = storedRef.Length,
            ChangeSummary = "legacy seed",
            CreatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;
        private readonly bool _managed;

        public Factory(string root, bool managed)
        {
            _root = root;
            _managed = managed;
        }

        protected override bool UsesManagedStorage => _managed;
        protected override string? ManagedUserRoot => _managed ? _root : null;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            var settings = new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Warning" };
            if (_managed)
            {
                settings["TinadecStorage:Enabled"] = "true";
                settings["TinadecStorage:UserRoot"] = _root;
                settings["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(_root, "workspace");
            }
            else
            {
                settings["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db");
                settings["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data");
            }
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
        }
    }
}
