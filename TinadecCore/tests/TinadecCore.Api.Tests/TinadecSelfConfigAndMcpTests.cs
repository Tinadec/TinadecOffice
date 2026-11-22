using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AspNetCore.Endpoints;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;
using Xunit;

namespace TinadecCore.Api.Tests;

public sealed class TinadecSelfConfigAndMcpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tinadec-selfconfig-{Guid.NewGuid():N}");
    private readonly Factory _factory;

    public TinadecSelfConfigAndMcpTests()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadSkill_ProjectlessSentinel_LoadsBuiltinWorkbenchAndArchitectSkills()
    {
        _ = _factory.CreateClient();
        var skills = _factory.Services.GetRequiredService<IToolSkillResourceService>();
        var projectlessScope = Scope(projectId: Guid.Empty);

        var workbenchRes = await CoreSkillLoadTool.ExecuteAsync(
            skills,
            projectlessScope,
            Wire(CoreVirtualToolPolicy.LoadSkillToolId, new { name = "tinadec-workbench" }),
            CancellationToken.None);
        Assert.True(workbenchRes.IsSuccess, workbenchRes.Error);
        var workbenchBody = workbenchRes.Result!.Value.GetProperty("body").GetString()!;
        Assert.Contains("payload", workbenchBody);
        Assert.Contains("edge_key", workbenchBody);

        var architectRes = await CoreSkillLoadTool.ExecuteAsync(
            skills,
            projectlessScope,
            Wire(CoreVirtualToolPolicy.LoadSkillToolId, new { name = "tinadec-agent-architect" }),
            CancellationToken.None);
        Assert.True(architectRes.IsSuccess, architectRes.Error);
        var architectBody = architectRes.Result!.Value.GetProperty("body").GetString()!;
        Assert.Contains("tinadec_model_profile_apply", architectBody);
        Assert.Contains("agent_generated", architectBody);
    }

    [Fact]
    public async Task AgentAndPromptPipelineLifecycle_CreatesBindsPublishesAndInspectsBySlugOrGuid()
    {
        _ = _factory.CreateClient();
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var runId = Guid.NewGuid();
        var creatorAgentInstanceId = Guid.NewGuid();
        var scope = Scope(runId: runId, agentInstanceId: creatorAgentInstanceId);

        // 1. Create and publish a prompt pipeline with fragment_ref, workspace_rules, skills_index, template, assemble
        var pipeCreate = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecPromptApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "create",
                payload = new
                {
                    display_name = "Architect Pipeline",
                    slug = "architect-pipeline",
                    description = "Pipeline for generated specialist",
                    graph = new
                    {
                        nodes = new object[]
                        {
                            new { id = "n1", type = "template", content = "You are {{agent_name}}." },
                            new { id = "n2", type = "workspace_rules" },
                            new { id = "n3", type = "skills_index" },
                            new { id = "n4", type = "assemble" },
                        },
                        edges = new object[]
                        {
                            new { source = "n1", target = "n4" },
                            new { source = "n2", target = "n4" },
                            new { source = "n3", target = "n4" },
                        },
                    },
                },
            }),
            CancellationToken.None);
        Assert.True(pipeCreate.IsSuccess, pipeCreate.Error);
        var pipelineId = pipeCreate.Result!.Value.GetProperty("pipeline").GetProperty("id").GetGuid();

        var pipePublish = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecPromptApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "pipeline",
                action = "publish",
                pipeline_id = "architect-pipeline",
            }),
            CancellationToken.None);
        Assert.True(pipePublish.IsSuccess, pipePublish.Error);
        Assert.Equal(1, pipePublish.Result!.Value.GetProperty("version").GetInt32());

        var pipeInspect = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new
            {
                target = "prompt_pipeline",
                pipeline_id = "architect-pipeline",
            }),
            CancellationToken.None);
        Assert.True(pipeInspect.IsSuccess, pipeInspect.Error);
        Assert.Equal(4, pipeInspect.Result!.Value.GetProperty("pipeline").GetProperty("graph").GetProperty("nodes").GetArrayLength());

        // 2. Create an agent referencing the prompt pipeline by slug, defaulting to agent_generated with provenance
        var agentCreate = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecAgentApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "create",
                payload = new
                {
                    display_name = "Audit Specialist",
                    slug = "audit-specialist",
                    layer = "execution",
                    role = "verifier",
                    system_prompt = "Verify diffs carefully.",
                    capabilities = new[] { "code.read" },
                    tool_scope = new[] { "read_file", "tinadec_inspect" },
                    base_prompt_pipeline_id = "architect-pipeline",
                    provenance = new { reason = "Created for verification" },
                    model_binding = new { mode = "route", route_purpose = "chat" },
                    tool_settings = new { mcp_server_ids = Array.Empty<Guid>(), skill_resource_ids = Array.Empty<Guid>() },
                },
            }),
            CancellationToken.None);
        Assert.True(agentCreate.IsSuccess, agentCreate.Error);
        var createdAgent = agentCreate.Result!.Value.GetProperty("agent");
        Assert.Equal("agent_generated", createdAgent.GetProperty("source_kind").GetString());
        Assert.Equal(pipelineId, createdAgent.GetProperty("base_prompt_pipeline_id").GetGuid());
        Assert.Equal("route", createdAgent.GetProperty("model_binding").GetProperty("mode").GetString());
        Assert.Equal("inherit", createdAgent.GetProperty("model_strategy").GetProperty("kind").GetString());

        // 2b. Inspect target="tools" lists core virtual tools cleanly
        var toolsInspect = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "tools", query = "tinadec", limit = 20 }),
            CancellationToken.None);
        Assert.True(toolsInspect.IsSuccess, toolsInspect.Error);
        var toolIds = toolsInspect.Result!.Value.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Contains(CoreVirtualToolPolicy.TinadecInspectToolId, toolIds);

        // 3. Publish the agent (verifies safe ModelStrategyJson serialization and version snapshot)
        var agentPublish = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecAgentApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "publish",
                agent_id = "audit-specialist",
            }),
            CancellationToken.None);
        Assert.True(agentPublish.IsSuccess, agentPublish.Error);
        Assert.Equal(1, agentPublish.Result!.Value.GetProperty("version").GetInt32());

        // 4. Inspect full agent detail by slug
        var agentInspect = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new
            {
                target = "agent",
                agent_id = "audit-specialist",
            }),
            CancellationToken.None);
        Assert.True(agentInspect.IsSuccess, agentInspect.Error);
        var detail = agentInspect.Result!.Value.GetProperty("agent");
        Assert.Equal("Verify diffs carefully.", detail.GetProperty("system_prompt").GetString());
        Assert.Equal("agent_generated", detail.GetProperty("source_kind").GetString());
        Assert.Equal(pipelineId, detail.GetProperty("base_prompt_pipeline_id").GetGuid());
        Assert.Equal(runId, detail.GetProperty("provenance").GetProperty("run_id").GetGuid());
        Assert.Equal("route", detail.GetProperty("model_binding").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task PromptFragmentAndModelProfileTools_EnforceValidationAndSanitizeSecrets()
    {
        var client = _factory.CreateClient();
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        // 1. Prompt fragment lifecycle via tinadec_prompt_apply
        var fragCreate = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecPromptApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "create",
                payload = new
                {
                    key = "team.guardrails",
                    title = "Team Guardrails",
                    scope = "global",
                    category = "policy",
                    content = "Version 1 guardrails",
                    priority = 10,
                },
            }),
            CancellationToken.None);
        Assert.True(fragCreate.IsSuccess, fragCreate.Error);
        var fragId = fragCreate.Result!.Value.GetProperty("fragment").GetProperty("id").GetGuid();

        var fragUpdate = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecPromptApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "update",
                fragment_id = "team.guardrails",
                payload = new { content = "Version 2 guardrails" },
            }),
            CancellationToken.None);
        Assert.True(fragUpdate.IsSuccess, fragUpdate.Error);

        var fragRollback = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecPromptApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecPromptApplyToolId, new
            {
                target = "fragment",
                action = "rollback",
                fragment_id = fragId.ToString(),
                payload = new { target_version = 1 },
            }),
            CancellationToken.None);
        Assert.True(fragRollback.IsSuccess, fragRollback.Error);
        Assert.Equal("Version 1 guardrails", fragRollback.Result!.Value.GetProperty("fragment").GetProperty("content").GetString());

        var promptsInspect = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "prompts", query = "guardrails" }),
            CancellationToken.None);
        Assert.True(promptsInspect.IsSuccess, promptsInspect.Error);
        Assert.True(promptsInspect.Result!.Value.GetProperty("fragments_total").GetInt32() >= 1);

        // 2. Empty payload on tinadec_model_profile_apply returns invalid_request BEFORE not_found
        var emptyProfileRes = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecModelProfileApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, new
            {
                action = "update",
                provider_id = Guid.NewGuid().ToString(),
                model_id = "gpt-5",
                payload = new { },
            }),
            CancellationToken.None);
        Assert.False(emptyProfileRes.IsSuccess);
        Assert.Contains("invalid_request", emptyProfileRes.Error);

        // 3. Create an enabled provider with an API key and update its model_profiles without exposing secrets
        var createProviderResp = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            protocol = "openai_compatible",
            display_name = "LocalOpenAI",
            connection_kind = "remote",
            base_url = "https://api.example.test/v1",
            api_key = "sk-super-secret-do-not-leak",
            default_model = "gpt-4.1",
            models = new[] { "gpt-4.1" },
            enabled = true,
        });
        createProviderResp.EnsureSuccessStatusCode();
        var providerId = (await createProviderResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var updateProfile = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecModelProfileApplyToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, new
            {
                action = "update",
                provider_id = "LocalOpenAI",
                model_id = "gpt-4.1",
                payload = new
                {
                    specialties = new[] { "code", "reasoning" },
                    tier = "deep",
                    notes = "Primary coding & architecture model",
                    context_window = 128000,
                    cost_tier = "medium",
                },
            }),
            CancellationToken.None);
        Assert.True(updateProfile.IsSuccess, updateProfile.Error);
        Assert.Equal("deep", updateProfile.Result!.Value.GetProperty("profile").GetProperty("tier").GetString());

        var modelsInspect = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "models" }),
            CancellationToken.None);
        Assert.True(modelsInspect.IsSuccess, modelsInspect.Error);
        var modelsRaw = modelsInspect.Result!.Value.GetRawText();
        Assert.DoesNotContain("sk-super-secret-do-not-leak", modelsRaw);
        Assert.Contains("Primary coding", modelsRaw);
        Assert.Contains(providerId.ToString(), modelsRaw);
    }

    [Fact]
    public async Task LoopbackMcpServer_SupportsMcpProtocolAndBlocksExternalNetworkRequests()
    {
        var client = _factory.CreateClient();

        // 1. initialize
        var initResp = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2024-11-05" },
        });
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        var initBody = await initResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tinadec-selfconfig-mcp", initBody.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        // 2. notifications/initialized
        var notifyResp = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        });
        Assert.Equal(HttpStatusCode.Accepted, notifyResp.StatusCode);

        // 3. tools/list
        var listResp = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
        });
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var tools = (await listResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains(CoreVirtualToolPolicy.TinadecInspectToolId, tools);
        Assert.Contains(CoreVirtualToolPolicy.TinadecPromptApplyToolId, tools);
        Assert.Contains(CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, tools);

        // 4. tools/call -> tinadec_inspect
        var callResp = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "tinadec_inspect",
                arguments = new { target = "modes", limit = 10 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, callResp.StatusCode);
        var callResult = (await callResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.False(callResult.GetProperty("isError").GetBoolean());

        // 5. resources/list & resources/read
        var resListResp = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "resources/list",
        });
        Assert.Equal(HttpStatusCode.OK, resListResp.StatusCode);
        var resources = (await resListResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("resources");
        Assert.True(resources.GetArrayLength() >= 6);

        var resReadResp = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "resources/read",
            @params = new { uri = "tinadec://agents" },
        });
        Assert.Equal(HttpStatusCode.OK, resReadResp.StatusCode);
        var contents = (await resReadResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("contents");
        Assert.Equal("tinadec://agents", contents[0].GetProperty("uri").GetString());

        // 6. External X-Forwarded-For is refused with 403 external_network_forbidden
        using var externalReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/tinadec")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 6, method = "tools/list" }),
        };
        externalReq.Headers.Add("X-Forwarded-For", "203.0.113.42");
        var blockedResp = await client.SendAsync(externalReq);
        Assert.Equal(HttpStatusCode.Forbidden, blockedResp.StatusCode);
        var blockedJson = await blockedResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("external_network_forbidden", blockedJson.GetProperty("code").GetString());

        // 7. Direct non-loopback RemoteIpAddress check
        var nonLoopbackContext = new DefaultHttpContext();
        nonLoopbackContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.25");
        Assert.False(TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(nonLoopbackContext));

        var loopbackContext = new DefaultHttpContext();
        loopbackContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(loopbackContext));
    }

    [Fact]
    public async Task ScopedTeammateBearerToken_ExposesOnlyRunBoardTools_RejectsSelfConfig_AndRevokesOnRunClose()
    {
        var client = _factory.CreateClient();
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var teamBridge = _factory.Services.GetRequiredService<ITeamBoardMcpBridge>();
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        TeamBoardToolCallContext? capturedCall = null;
        using var handlerReg = teamBridge.RegisterRunBoardHandler(runId, (ctx, _) =>
        {
            capturedCall = ctx;
            return Task.FromResult(new TeamBoardToolCallResult(true, JsonSerializer.SerializeToElement(new { ok = true, seq = 7 })));
        });

        var lease = teamBridge.IssueToken(
            tenant.TenantId,
            tenant.WorkspaceId,
            sessionId,
            runId,
            teammateHandle: "impl-worker");

        async Task<HttpResponseMessage> PostScopedAsync(string bearer, object payload)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/tinadec")
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            return await client.SendAsync(req);
        }

        // 1. Scoped initialize identifies tinadec-team-board-mcp
        var initResp = await PostScopedAsync(lease.Token, new { jsonrpc = "2.0", id = 1, method = "initialize" });
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        var initJson = await initResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tinadec-team-board-mcp", initJson.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        // 2. Scoped tools/list exposes ONLY shared-board & read-only recall tools (zero tinadec_* self-config tools)
        var listResp = await PostScopedAsync(lease.Token, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listedTools = (await listResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("team_message", listedTools);
        Assert.Contains("team_plan", listedTools);
        Assert.Contains("recall_evidence", listedTools);
        Assert.Contains("recall_conversation_history", listedTools);
        Assert.Contains("graph_view", listedTools);
        Assert.DoesNotContain(listedTools, t => t.StartsWith("tinadec_", StringComparison.OrdinalIgnoreCase));

        // 3. Scoped tools/call -> team_message succeeds and forwards teammate identity
        var msgResp = await PostScopedAsync(lease.Token, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "team_message",
                arguments = new { target = "coordinator", content = "API slice ready" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, msgResp.StatusCode);
        var msgResult = (await msgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.False(msgResult.GetProperty("isError").GetBoolean());
        Assert.NotNull(capturedCall);
        Assert.Equal(runId, capturedCall!.RunId);
        Assert.Equal("impl-worker", capturedCall.TeammateHandle);

        // 4. Scoped tools/call -> tinadec_inspect & tinadec_agent_apply are strictly rejected
        foreach (var forbiddenTool in new[] { "tinadec_inspect", "tinadec_agent_apply" })
        {
            var forbidResp = await PostScopedAsync(lease.Token, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new { name = forbiddenTool, arguments = new { target = "modes" } }
            });
            Assert.Equal(HttpStatusCode.OK, forbidResp.StatusCode);
            var forbidJson = await forbidResp.Content.ReadFromJsonAsync<JsonElement>();
            var isErr = forbidJson.TryGetProperty("error", out _)
                || (forbidJson.TryGetProperty("result", out var r) && r.TryGetProperty("isError", out var ie) && ie.GetBoolean());
            Assert.True(isErr, $"Scoped token must reject '{forbiddenTool}'.");
        }

        // 5. Revoking run tokens invalidates the token with 401 while unauthenticated loopback tinadec_* stays intact
        Assert.Equal(1, teamBridge.RevokeRunTokens(runId));
        var revokedResp = await PostScopedAsync(lease.Token, new { jsonrpc = "2.0", id = 5, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResp.StatusCode);
        Assert.Equal("invalid_team_mcp_token", (await revokedResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var unauthInspect = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 6,
            method = "tools/call",
            @params = new { name = "tinadec_inspect", arguments = new { target = "modes", limit = 5 } }
        });
        Assert.Equal(HttpStatusCode.OK, unauthInspect.StatusCode);
        Assert.False((await unauthInspect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("isError").GetBoolean());
    }

    private ToolInvocationScope Scope(Guid? projectId = null, Guid? runId = null, Guid? agentInstanceId = null)
    {
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        return new(
            tenant.TenantId,
            tenant.WorkspaceId,
            tenant.PrincipalId,
            projectId ?? Guid.Empty,
            Guid.Empty,
            runId ?? Guid.Empty,
            Guid.Empty,
            agentInstanceId ?? Guid.Empty,
            _root,
            CoreVirtualToolPolicy.TinadecConfigToolIds.ToArray(),
            [],
            "test",
            600,
            0,
            false);
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
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(new StubToolProvider());
            });
        }
    }

    private sealed class StubToolProvider : IToolProvider
    {
        public Task<ToolManifestDto> EnsureStartedAsync(string root, CancellationToken ct = default) => GetManifestAsync(root, ct);
        public Task<ToolManifestDto> GetManifestAsync(string root, CancellationToken ct = default) =>
            Task.FromResult(new ToolManifestDto { ProtocolVersion = 2, Tools = [] });
        public Task<ToolWireResponseDto> CallAsync(string root, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolWireResponseDto { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) });
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
