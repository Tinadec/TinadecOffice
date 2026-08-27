using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Api.Tests;

public sealed class AgentPackEndpointTests
{
    private const string PackId = "tinadec.office.agent-pack";

    [Theory]
    [InlineData("-0", "0")]
    [InlineData("1E+30", "1e+30")]
    [InlineData("4.50", "4.5")]
    [InlineData("2e-3", "0.002")]
    [InlineData("1E-6", "0.000001")]
    [InlineData("1E-7", "1e-7")]
    [InlineData("1E20", "100000000000000000000")]
    [InlineData("1E21", "1e+21")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    [InlineData("9007199254740993", "9007199254740992")]
    [InlineData("0.000000000000000000000000001", "1e-27")]
    public void JcsCanonicalizer_UsesEcmaScriptNumberSerialization(string input, string expected)
    {
        using var document = JsonDocument.Parse($"{{\"number\":{input}}}");
        var canonical = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(document.RootElement));
        Assert.Equal($"{{\"number\":{expected}}}", canonical);
    }

    [Fact]
    public async Task OfficePack_AfterInstall_CenterListEndpointsReturnPackRoster()
    {
        // Regression: SQLite cannot translate DateTimeOffset ORDER BY to SQL, so the
        // Agent Center list endpoints (agents/modes/prompt-pipelines) used to 500
        // right after a pack install while the pack detail endpoint looked healthy.
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var envelope = OfficeEnvelope();

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var installResponse = await ApplyAsync(client, envelope, preview.GetProperty("preview_id").GetGuid(), "office-install-list");
        Assert.Equal(HttpStatusCode.Created, installResponse.StatusCode);

        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(agents);
        // Bootstrap seeded 14 formal agents; the pack installs its own 14 alongside them
        // (same slugs coexist — pack/bootstrap/custom are distinguished by source_kind).
        Assert.Equal(28, agents!.Length);
        Assert.All(agents, agent => Assert.Equal("published", agent.GetProperty("status").GetString()));
        Assert.Equal(14, agents.Count(agent => agent.GetProperty("source_kind").GetString() == "bootstrap"));
        Assert.Equal(14, agents.Count(agent => agent.GetProperty("source_kind").GetString() == "pack"));
        Assert.Equal(12, agents.Count(agent => agent.GetProperty("layer").GetString() == "operation"));
        Assert.Equal(16, agents.Count(agent => agent.GetProperty("layer").GetString() == "execution"));

        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes");
        Assert.NotNull(modes);
        Assert.Equal(14, modes!.Length);
        Assert.Equal(2, modes!.Count(mode => mode.GetProperty("slug").GetString() == "default-mode"));
        foreach (var composer in new[] { "ask", "vibe", "plan", "spec", "auto", "agent" })
        {
            Assert.Equal(2, modes.Count(mode => mode.GetProperty("slug").GetString() == $"conversation.{composer}"));
        }

        // Pack-managed/published modes must expose their topology on a plain read so the
        // Desktop mode-topology canvas renders the agents (regression: draft-only guard 409'd).
        var defaultModeIds = modes.Where(mode => mode.GetProperty("slug").GetString() == "default-mode").Select(mode => mode.GetProperty("id").GetGuid()).ToArray();
        var packDefaultTopologies = new List<JsonElement>();
        foreach (var modeId in defaultModeIds)
        {
            var topology = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
            if (topology.GetProperty("managed").GetBoolean()) packDefaultTopologies.Add(topology);
        }
        var packTopology = Assert.Single(packDefaultTopologies);
        Assert.Equal(14, packTopology.GetProperty("nodes").GetArrayLength());
        Assert.Equal("published", packTopology.GetProperty("status").GetString());

        var planModeIds = modes.Where(mode => mode.GetProperty("slug").GetString() == "conversation.plan").Select(mode => mode.GetProperty("id").GetGuid()).ToArray();
        var packPlanTopologies = new List<JsonElement>();
        foreach (var modeId in planModeIds)
        {
            var topology = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
            if (topology.GetProperty("managed").GetBoolean()) packPlanTopologies.Add(topology);
        }
        var planTopology = Assert.Single(packPlanTopologies);
        Assert.Equal(6, planTopology.GetProperty("nodes").GetArrayLength());
        var planNodeAgentRefs = planTopology.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("label").GetString()).ToArray();
        Assert.Contains(planNodeAgentRefs, label => label == "会议智能体");
        Assert.Contains(planNodeAgentRefs, label => label == "任务规划智能体");
        Assert.Contains(planNodeAgentRefs, label => label == "文档生成智能体");

        var pipelines = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/prompt-pipelines");
        // Bootstrap and the installed pack each publish their own baseline prompt.
        var baselines = pipelines!.Where(pipeline => pipeline.GetProperty("slug").GetString() == "baseline-prompt").ToArray();
        Assert.Equal(2, baselines.Length);
        Assert.All(baselines, baseline => Assert.Equal("published", baseline.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task OfficePack_AgentModeSelectsPublishedConversationModeVersion()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var envelope = OfficeEnvelope();

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var installResponse = await ApplyAsync(client, envelope, preview.GetProperty("preview_id").GetGuid(), "office-install-agent-mode");
        Assert.Equal(HttpStatusCode.Created, installResponse.StatusCode);

        var detail = await (await client.GetAsync($"/api/v1/agent-packs/{PackId}")).Content.ReadFromJsonAsync<JsonElement>();
        var modeVersions = detail.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("kind").GetString() == "mode")
            .ToDictionary(
                resource => resource.GetProperty("resource_key").GetString()!,
                resource => resource.GetProperty("version_id").GetGuid(),
                StringComparer.Ordinal);

        var session = await CreateSessionAsync(client, factory.WorkspacePath, "Agent mode session");
        var sessionId = session.GetProperty("id").GetGuid();
        // Session creation applied the workspace default (default-mode).
        var initial = await GetSessionAsync(client, sessionId);
        Assert.Equal(modeVersions["default-mode"], initial.GetProperty("mode_version_id").GetGuid());

        // Unknown agent_mode fails fast with a structured error.
        using var invalid = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "hi",
            client_message_id = $"cm-{Guid.NewGuid():N}",
            agent_mode = "nonsense",
            dispatch_mode = "queued"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());

        // agent_mode=plan resolves the published conversation.plan version onto the session.
        using var planned = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "规划一下",
            client_message_id = $"cm-{Guid.NewGuid():N}",
            agent_mode = "plan",
            dispatch_mode = "queued"
        });
        Assert.True(planned.IsSuccessStatusCode || planned.StatusCode == HttpStatusCode.Conflict,
            $"interaction admission should not fail on validation: {planned.StatusCode}");
        var afterPlan = await GetSessionAsync(client, sessionId);
        Assert.Equal(modeVersions["conversation.plan"], afterPlan.GetProperty("mode_version_id").GetGuid());

        // An explicit mode_version_id takes precedence over agent_mode.
        using var explicitMode = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "回到默认",
            client_message_id = $"cm-{Guid.NewGuid():N}",
            mode_version_id = modeVersions["default-mode"],
            agent_mode = "ask",
            dispatch_mode = "queued"
        });
        Assert.True(explicitMode.IsSuccessStatusCode || explicitMode.StatusCode == HttpStatusCode.Conflict,
            $"interaction admission should not fail on validation: {explicitMode.StatusCode}");
        var afterExplicit = await GetSessionAsync(client, sessionId);
        Assert.Equal(modeVersions["default-mode"], afterExplicit.GetProperty("mode_version_id").GetGuid());
    }

    [Fact]
    public async Task OfficePack_InstallsIdempotently_FreezesDefaultsAndProtectsManagedResources()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var envelope = OfficeEnvelope();

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("install", preview.GetProperty("action").GetString());
        Assert.Equal(14, preview.GetProperty("counts").GetProperty("agents").GetInt32());
        Assert.Equal(22, preview.GetProperty("resources").GetArrayLength());
        Assert.True(preview.GetProperty("defaults_will_adopt").GetBoolean());
        Assert.Equal($"\"{preview.GetProperty("revision").GetInt64()}\"", previewResponse.Headers.ETag?.Tag);

        var previewId = preview.GetProperty("preview_id").GetGuid();
        using var installResponse = await ApplyAsync(client, envelope, previewId, "office-install-1");
        Assert.Equal(HttpStatusCode.Created, installResponse.StatusCode);
        var installed = await installResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("installed", installed.GetProperty("status").GetString());
        Assert.True(installed.GetProperty("defaults_adopted").GetBoolean());
        Assert.Equal(22, installed.GetProperty("resources").GetArrayLength());

        using var replayResponse = await ApplyAsync(client, envelope, previewId, "office-install-1");
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(installed.GetProperty("integrity_digest").GetString(), replay.GetProperty("integrity_digest").GetString());
        Assert.Equal(installed.GetProperty("revision").GetInt64(), replay.GetProperty("revision").GetInt64());

        var packs = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-packs");
        Assert.Single(packs!);
        using var detailResponse = await client.GetAsync($"/api/v1/agent-packs/{PackId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("0.2.0", detail.GetProperty("active_version").GetString());
        Assert.Equal(22, detail.GetProperty("resources").GetArrayLength());

        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/v1/workspace-defaults");
        var exactModeVersionId = defaults.GetProperty("default_mode_version_id").GetGuid();
        Assert.NotEqual(Guid.Empty, defaults.GetProperty("default_agent_version_id").GetGuid());
        Assert.NotEqual(Guid.Empty, exactModeVersionId);
        Assert.NotEqual(Guid.Empty, defaults.GetProperty("default_prompt_version_id").GetGuid());

        var session = await CreateSessionAsync(client, factory.WorkspacePath, "Office session");
        Assert.Equal(exactModeVersionId, session.GetProperty("mode_version_id").GetGuid());

        var meeting = detail.GetProperty("resources").EnumerateArray().Single(resource =>
            resource.GetProperty("kind").GetString() == "agent"
            && resource.GetProperty("resource_key").GetString() == "meeting");
        var meetingId = meeting.GetProperty("logical_entity_id").GetGuid();
        using var managedResponse = await client.PutAsJsonAsync($"/api/v1/agents/{meetingId}/draft", new { display_name = "Changed" });
        Assert.Equal(HttpStatusCode.Conflict, managedResponse.StatusCode);
        Assert.Equal("application/problem+json", managedResponse.Content.Headers.ContentType?.MediaType);
        var managedProblem = await managedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("managed_resource_read_only", managedProblem.GetProperty("code").GetString());

        using var noOpPreviewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        var noOpPreview = await noOpPreviewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("up_to_date", noOpPreview.GetProperty("action").GetString());
    }

    [Fact]
    public async Task OfficePack_PreservesUserDefaults_AndReportsPreviewImpact()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var accessor = factory.Services.GetRequiredService<ITenantContextAccessor>();
        var scope = accessor.Current;
        var customAgent = Guid.NewGuid();
        var customAgentVersion = Guid.NewGuid();
        var customMode = Guid.NewGuid();
        var customModeVersion = Guid.NewGuid();
        var customPrompt = Guid.NewGuid();
        var customPromptVersion = Guid.NewGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync())
        {
            // Bootstrap already seeded an active defaults row for this workspace;
            // user customization rewrites that row instead of inserting a second one
            // (UNIQUE(tenant_id, workspace_id) holds exactly one active defaults).
            var existing = await db.WorkspaceDefaults.SingleAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.Status == "active");
            existing.DefaultAgentDefinitionId = customAgent;
            existing.DefaultAgentVersionId = customAgentVersion;
            existing.DefaultAgentModeId = customMode;
            existing.DefaultModeVersionId = customModeVersion;
            existing.DefaultPromptPipelineId = customPrompt;
            existing.DefaultPromptVersionId = customPromptVersion;
            existing.Revision = 7;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var envelope = OfficeEnvelope();
        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("defaults_will_adopt").GetBoolean());

        using var installResponse = await ApplyAsync(client, envelope, preview.GetProperty("preview_id").GetGuid(), "custom-defaults-install");
        Assert.Equal(HttpStatusCode.Created, installResponse.StatusCode);
        var installed = await installResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(installed.GetProperty("defaults_adopted").GetBoolean());

        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/v1/workspace-defaults");
        Assert.Equal(customAgent, defaults.GetProperty("default_agent_definition_id").GetGuid());
        Assert.Equal(customAgentVersion, defaults.GetProperty("default_agent_version_id").GetGuid());
        Assert.Equal(customMode, defaults.GetProperty("default_agent_mode_id").GetGuid());
        Assert.Equal(customModeVersion, defaults.GetProperty("default_mode_version_id").GetGuid());
        Assert.Equal(customPrompt, defaults.GetProperty("default_prompt_pipeline_id").GetGuid());
        Assert.Equal(customPromptVersion, defaults.GetProperty("default_prompt_version_id").GetGuid());
    }

    [Fact]
    public async Task OfficePack_UpgradeKeepsExistingSessionOnItsExactModeVersion()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var v1 = OfficeEnvelope();
        var v1Preview = await PreviewAsync(client, v1);
        using var v1Response = await ApplyAsync(client, v1, v1Preview.GetProperty("preview_id").GetGuid(), "upgrade-v1");
        Assert.Equal(HttpStatusCode.Created, v1Response.StatusCode);

        var oldSession = await CreateSessionAsync(client, factory.WorkspacePath, "Old session");
        var oldSessionId = oldSession.GetProperty("id").GetGuid();
        var oldModeVersionId = oldSession.GetProperty("mode_version_id").GetGuid();

        var v2 = OfficeEnvelope("0.3.0", manifest =>
        {
            var agents = manifest["resources"]!["agents"]!.AsArray();
            var meeting = agents.Single(node => node!["resource_key"]!.GetValue<string>() == "meeting")!.AsObject();
            meeting["system_prompt"] = meeting["system_prompt"]!.GetValue<string>() + " v2";
        });
        var v2Preview = await PreviewAsync(client, v2);
        Assert.Equal("upgrade", v2Preview.GetProperty("action").GetString());
        using var v2Response = await ApplyAsync(
            client,
            v2,
            v2Preview.GetProperty("preview_id").GetGuid(),
            "upgrade-v2",
            $"\"{v2Preview.GetProperty("revision").GetInt64()}\"");
        Assert.Equal(HttpStatusCode.OK, v2Response.StatusCode);
        var updated = await v2Response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("updated", updated.GetProperty("status").GetString());
        Assert.True(updated.GetProperty("defaults_adopted").GetBoolean());

        var oldSessionAfterUpgrade = await GetSessionAsync(client, oldSessionId);
        Assert.Equal(oldModeVersionId, oldSessionAfterUpgrade.GetProperty("mode_version_id").GetGuid());
        var newSession = await CreateSessionAsync(client, factory.WorkspacePath, "New session");
        Assert.NotEqual(oldModeVersionId, newSession.GetProperty("mode_version_id").GetGuid());

        var alteredV2 = OfficeEnvelope("0.3.0", manifest => manifest["metadata"]!["name"] = "Altered Office Pack");
        using var conflictResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", alteredV2);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_pack_version_hash_conflict", conflict.GetProperty("code").GetString());

        var older = OfficeEnvelope("0.0.9");
        var olderPreview = await PreviewAsync(client, older);
        Assert.Equal("newer_installed", olderPreview.GetProperty("action").GetString());
        Assert.Equal("0.3.0", olderPreview.GetProperty("installed_version").GetString());
    }

    [Fact]
    public async Task OfficePack_CustomSlugCoexists_AndMemberCannotManage()
    {
        Guid customAgentId;
        using (var factory = new AgentPackFactory())
        using (var client = factory.CreateClient())
        {
            var scope = factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
            await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
            customAgentId = Guid.NewGuid();
            db.AgentDefinitions.Add(new AgentDefinitionRecord
            {
                Id = customAgentId,
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                Slug = "meeting",
                DisplayName = "User Meeting",
                Layer = "operation",
                Role = "custom",
                CapabilitiesJson = "[]",
                ToolScopeJson = "[]",
                ModelStrategyJson = "{\"kind\":\"inherit\"}",
                SourceKind = "custom",
                SourceKey = "meeting",
                Managed = false,
                Status = "published",
                Revision = 1,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CreatedByPrincipalId = scope.PrincipalId,
                UpdatedByPrincipalId = scope.PrincipalId
            });
            await db.SaveChangesAsync();

            // Same-slug custom agents no longer conflict: the pack creates its own
            // managed agent and the user-owned definition stays untouched.
            var preview = await PreviewAsync(client, OfficeEnvelope());
            Assert.Equal("install", preview.GetProperty("action").GetString());
            Assert.Contains(preview.GetProperty("resources").EnumerateArray(), resource =>
                resource.GetProperty("resource_key").GetString() == "meeting"
                && resource.GetProperty("disposition").GetString() == "created");
            // `differences` carries the conflict strings; coexisting custom slugs
            // must not produce any.
            Assert.Empty(preview.GetProperty("differences").EnumerateArray());

            using var install = await ApplyAsync(client, OfficeEnvelope(), preview.GetProperty("preview_id").GetGuid(), "custom-coexist-install");
            Assert.Equal(HttpStatusCode.Created, install.StatusCode);

            var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
            Assert.NotNull(agents);
            Assert.Equal(3, agents!.Count(agent => agent.GetProperty("slug").GetString() == "meeting"));
            var custom = agents.Single(agent => agent.GetProperty("id").GetGuid() == customAgentId);
            Assert.Equal("custom", custom.GetProperty("source_kind").GetString());
            Assert.False(custom.GetProperty("managed").GetBoolean());
            Assert.Equal("User Meeting", custom.GetProperty("display_name").GetString());
            var packMeeting = agents.Single(agent => agent.GetProperty("source_kind").GetString() == "pack" && agent.GetProperty("slug").GetString() == "meeting");
            Assert.True(packMeeting.GetProperty("managed").GetBoolean());
        }

        using (var memberFactory = new AgentPackFactory("member"))
        using (var memberClient = memberFactory.CreateClient())
        using (var forbidden = await memberClient.PostAsJsonAsync("/api/v1/agent-packs/install-preview", OfficeEnvelope()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal("application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);
            var problem = await forbidden.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("agent_pack_management_forbidden", problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task OfficePack_ConcurrentFreshInstalls_ConvergeOnOneVersion()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var envelope = OfficeEnvelope();
        var firstPreview = await PreviewAsync(client, envelope);
        var secondPreview = await PreviewAsync(client, envelope);

        var responses = await Task.WhenAll(
            ApplyAsync(client, envelope, firstPreview.GetProperty("preview_id").GetGuid(), "concurrent-install-1"),
            ApplyAsync(client, envelope, secondPreview.GetProperty("preview_id").GetGuid(), "concurrent-install-2"));
        try
        {
            Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode));
            var results = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Contains(results, result => result.GetProperty("status").GetString() == "installed");
            Assert.Contains(results, result => result.GetProperty("status").GetString() == "up_to_date");
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        var scope = factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.AgentPackInstallations.CountAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId));
        Assert.Equal(1, await db.AgentPackVersions.CountAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId));
        Assert.Equal(22, await db.AgentPackManagedResources.CountAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId));
        Assert.Equal(22, await db.AgentPackResourceBindings.CountAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId));
        Assert.Equal(2, await db.AgentPackOperations.CountAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId));
        Assert.Equal(2, await db.AgentPackPreviews.CountAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.Status == "consumed"));
    }

    [Fact]
    public async Task OfficePack_UsesFullSemVerPrereleasePrecedence()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var installedEnvelope = OfficeEnvelope("1.0.0-beta.11");
        var installPreview = await PreviewAsync(client, installedEnvelope);
        using var install = await ApplyAsync(client, installedEnvelope, installPreview.GetProperty("preview_id").GetGuid(), "semver-install");
        Assert.Equal(HttpStatusCode.Created, install.StatusCode);

        foreach (var lowerVersion in new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2"
        })
        {
            var preview = await PreviewAsync(client, OfficeEnvelope(lowerVersion));
            Assert.Equal("newer_installed", preview.GetProperty("action").GetString());
        }

        foreach (var higherVersion in new[]
        {
            "1.0.0-beta.11.1",
            "1.0.0-rc.1",
            "1.0.0",
            "2147483648.0.0"
        })
        {
            var preview = await PreviewAsync(client, OfficeEnvelope(higherVersion));
            Assert.Equal("upgrade", preview.GetProperty("action").GetString());
        }

        using var equalPrecedence = await client.PostAsJsonAsync(
            "/api/v1/agent-packs/install-preview",
            OfficeEnvelope("1.0.0-beta.11+build.7"));
        Assert.Equal(HttpStatusCode.Conflict, equalPrecedence.StatusCode);
        var problem = await equalPrecedence.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_pack_version_hash_conflict", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OfficePack_RejectsInvalidSemVer2Values()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        foreach (var invalidVersion in new[]
        {
            "1.0",
            "01.0.0",
            "1.01.0",
            "1.0.01",
            "1.0.0-01",
            "1.0.0-",
            "1.0.0+",
            "1.0.0_alpha",
            " 1.0.0"
        })
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/agent-packs/install-preview",
                OfficeEnvelope(invalidVersion));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_agent_pack_manifest", problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task OfficePack_RejectsUntypedInternalReferences()
    {
        using var factory = new AgentPackFactory();
        using var client = factory.CreateClient();
        var envelopes = new[]
        {
            OfficeEnvelope(mutate: manifest =>
                manifest["resources"]!["agents"]![0]!["base_prompt_pipeline_ref"] = "baseline-prompt"),
            OfficeEnvelope(mutate: manifest =>
                manifest["resources"]!["modes"]![0]!["nodes"]![0]!["agent_ref"] = "meeting"),
            OfficeEnvelope(mutate: manifest =>
                manifest["activation"]!["workspace_defaults"]!["mode_ref"] = "default-mode")
        };

        foreach (var envelope in envelopes)
        {
            using var response = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_agent_pack_manifest", problem.GetProperty("code").GetString());
        }
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, JsonElement envelope)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> ApplyAsync(
        HttpClient client,
        JsonElement envelope,
        Guid previewId,
        string idempotencyKey,
        string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-packs/{PackId}")
        {
            Content = JsonContent.Create(new { preview_id = previewId, envelope })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> CreateSessionAsync(HttpClient client, string workspacePath, string title)
    {
        Directory.CreateDirectory(workspacePath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new
        {
            name = title,
            path = Path.Combine(workspacePath, Guid.NewGuid().ToString("N"))
        });
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title
        });
        sessionResponse.EnsureSuccessStatusCode();
        return await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GetSessionAsync(HttpClient client, Guid sessionId)
    {
        var sessions = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/sessions");
        return sessions!.Single(session => session.GetProperty("id").GetGuid() == sessionId);
    }

    private static JsonElement OfficeEnvelope(string? version = null, Action<JsonObject>? mutate = null)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(FindOfficeManifestPath(), Encoding.UTF8))!.AsObject();
        if (version is not null) manifest["metadata"]!["version"] = version;
        mutate?.Invoke(manifest);
        var manifestElement = JsonSerializer.SerializeToElement(manifest);
        var digest = Convert.ToHexString(SHA256.HashData(Canonicalize(manifestElement))).ToLowerInvariant();
        return JsonSerializer.SerializeToElement(new
        {
            manifest = manifestElement,
            integrity = new { algorithm = "sha256", digest }
        });
    }

    private static string FindOfficeManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "desktop", "src", "agentPacks", "OfficeAgentPack", "manifest.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("OfficeAgentPack manifest.json was not found from the test output directory.");
    }

    private static byte[] Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteCanonical(writer, value);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("Unsupported JSON value in OfficeAgentPack test manifest.");
        }
    }

    private sealed class AgentPackFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-agent-pack-tests", Guid.NewGuid().ToString("N"));
        private readonly string? _role;

        public AgentPackFactory(string? role = null)
        {
            _role = role;
            Directory.CreateDirectory(_root);
        }

        public string WorkspacePath => Path.Combine(_root, "workspace");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            if (_role is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ITenantContextAccessor>();
                    services.AddSingleton<ITenantContextAccessor>(new FixedTenantAccessor(_role));
                });
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedTenantAccessor(string role) : ITenantContextAccessor
    {
        public TenantContext Current { get; } = new(
            Guid.Parse("a4b6f710-3699-4a37-bd1f-4d431bbd8101"),
            Guid.Parse("a4b6f710-3699-4a37-bd1f-4d431bbd8102"),
            Guid.Parse("a4b6f710-3699-4a37-bd1f-4d431bbd8103"),
            role,
            true);
    }
}
