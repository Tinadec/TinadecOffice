using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.Api.Tests;

public sealed class SessionContextOverlayEndToEndTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("session-context-overlay");
    private OverlayFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new OverlayFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ModeDefaults_OverriddenBySessionOverlay_AndResetCleanlyOnModeSwitch()
    {
        using var client = _factory!.CreateClient();

        var modesResp = await client.GetAsync("/api/v1/agent-modes");
        modesResp.EnsureSuccessStatusCode();
        var modesList = await modesResp.Content.ReadFromJsonAsync<JsonElement>();
        var sourceModeId = modesList.EnumerateArray().First().GetProperty("id").GetGuid();

        var modeAVersionId = await CloneAndPublishModeWithDefaultOptionsAsync(
            client,
            sourceModeId,
            "Mode A Deep Curator",
            new
            {
                plan_first = false,
                spec_enabled = false,
                multi_agent = false,
                bulletin_board = false,
                worktree = false,
                context_strategy = "deep_curator",
                context_advanced = new
                {
                    recall_fidelity = "verbatim_first",
                    compaction_trigger_ratio = 0.85,
                    recent_message_limit = 64,
                    allow_cross_session_recall = true,
                    evolution_mode = "auto_evolve",
                    core_markdown_max_tokens = 4096
                }
            });

        var modeBVersionId = await CloneAndPublishModeWithDefaultOptionsAsync(
            client,
            sourceModeId,
            "Mode B Managed Compact",
            new
            {
                plan_first = false,
                spec_enabled = false,
                multi_agent = false,
                bulletin_board = false,
                worktree = false,
                context_strategy = "managed",
                context_advanced = new
                {
                    recall_fidelity = "summary_only",
                    compaction_trigger_ratio = 0.55,
                    recent_message_limit = 16,
                    allow_cross_session_recall = false,
                    evolution_mode = "candidate_only",
                    core_markdown_max_tokens = 1024
                }
            });

        // 1. Create session on Mode A with zero overlay -> Mode A defaults apply
        var createResp = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            title = "Context Overlay Session",
            mode_version_id = modeAVersionId
        });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var createdSession = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = createdSession.GetProperty("id").GetGuid();
        Assert.False(createdSession.TryGetProperty("space_options", out var initialSpace) && initialSpace.ValueKind != JsonValueKind.Null);

        var resolver = _factory.Services.GetRequiredService<IAgentRuntimeConfigurationResolver>();
        var frozenModeA = await resolver.ResolveAsync(sessionId, "default");
        Assert.Equal("deep_curator", FullDuplexRunEngine.ResolveEffectiveContextStrategy(frozenModeA));
        Assert.Equal(0.85, FullDuplexRunEngine.ResolveEffectiveCompactionRatio(frozenModeA));
        Assert.Equal("verbatim_first", FullDuplexRunEngine.ResolveEffectiveRecallPolicy(frozenModeA));
        Assert.Equal(64, FullDuplexRunEngine.ResolveEffectiveRecentMessageLimit(frozenModeA));
        Assert.True(FullDuplexRunEngine.ResolveEffectiveAllowCrossSessionRecall(frozenModeA));
        Assert.Equal("auto_evolve", FullDuplexRunEngine.ResolveEffectiveEvolutionMode(frozenModeA));
        Assert.Equal(4096, FullDuplexRunEngine.ResolveEffectiveCoreMarkdownMaxTokens(frozenModeA));

        // 2. Patch Session Overlay -> overrides specified fields while null advanced fields inherit Mode A
        var overlayPatchResp = await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new
        {
            space_options = new
            {
                plan_first = false,
                spec_enabled = false,
                multi_agent = false,
                workflow_mode_version_id = (string?)null,
                bulletin_board = false,
                worktree = false,
                context_strategy = "direct",
                context_advanced = new
                {
                    compaction_trigger_ratio = 0.60,
                    recent_message_limit = 12,
                    evolution_mode = "candidate_only"
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, overlayPatchResp.StatusCode);
        var patchedSession = await overlayPatchResp.Content.ReadFromJsonAsync<JsonElement>();
        var savedSpace = patchedSession.GetProperty("space_options");
        Assert.Equal("direct", savedSpace.GetProperty("context_strategy").GetString());
        Assert.Equal(0.60, savedSpace.GetProperty("context_advanced").GetProperty("compaction_trigger_ratio").GetDouble());
        Assert.Equal(12, savedSpace.GetProperty("context_advanced").GetProperty("recent_message_limit").GetInt32());

        var frozenOverlay = await resolver.ResolveAsync(sessionId, "default");
        Assert.Equal("direct", FullDuplexRunEngine.ResolveEffectiveContextStrategy(frozenOverlay));
        Assert.Equal(0.60, FullDuplexRunEngine.ResolveEffectiveCompactionRatio(frozenOverlay));
        Assert.Equal("verbatim_first", FullDuplexRunEngine.ResolveEffectiveRecallPolicy(frozenOverlay));
        Assert.Equal(12, FullDuplexRunEngine.ResolveEffectiveRecentMessageLimit(frozenOverlay));
        Assert.True(FullDuplexRunEngine.ResolveEffectiveAllowCrossSessionRecall(frozenOverlay));
        Assert.Equal("candidate_only", FullDuplexRunEngine.ResolveEffectiveEvolutionMode(frozenOverlay));
        Assert.Equal(4096, FullDuplexRunEngine.ResolveEffectiveCoreMarkdownMaxTokens(frozenOverlay));

        // Invalid overlay bounds are rejected with 400
        var invalidPatch = await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new
        {
            space_options = new
            {
                plan_first = false,
                spec_enabled = false,
                multi_agent = false,
                bulletin_board = false,
                worktree = false,
                context_strategy = "direct",
                context_advanced = new { compaction_trigger_ratio = 0.99 }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPatch.StatusCode);

        // 3. Switch mode to Mode B -> destroys old Session Overlay and resolves cleanly to Mode B defaults
        var switchResp = await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new
        {
            mode_version_id = modeBVersionId
        });
        Assert.Equal(HttpStatusCode.OK, switchResp.StatusCode);
        var switchedSession = await switchResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(modeBVersionId, switchedSession.GetProperty("mode_version_id").GetGuid());
        Assert.False(switchedSession.TryGetProperty("space_options", out var afterSwitchSpace) && afterSwitchSpace.ValueKind != JsonValueKind.Null);

        var frozenModeB = await resolver.ResolveAsync(sessionId, "default");
        Assert.Equal("managed", FullDuplexRunEngine.ResolveEffectiveContextStrategy(frozenModeB));
        Assert.Equal(0.55, FullDuplexRunEngine.ResolveEffectiveCompactionRatio(frozenModeB));
        Assert.Equal("summary_only", FullDuplexRunEngine.ResolveEffectiveRecallPolicy(frozenModeB));
        Assert.Equal(16, FullDuplexRunEngine.ResolveEffectiveRecentMessageLimit(frozenModeB));
        Assert.False(FullDuplexRunEngine.ResolveEffectiveAllowCrossSessionRecall(frozenModeB));
        Assert.Equal("candidate_only", FullDuplexRunEngine.ResolveEffectiveEvolutionMode(frozenModeB));
        Assert.Equal(1024, FullDuplexRunEngine.ResolveEffectiveCoreMarkdownMaxTokens(frozenModeB));

        // 4. Interaction endpoint validates camelCase & snake_case space_options and rejects invalid bounds
        using var camelBadInteraction = new StringContent("""
            {
              "content": "hello",
              "client_message_id": "cm-bad-ratio",
              "dispatch_mode": "queued",
              "space_options": {
                "planFirst": false,
                "contextStrategy": "direct",
                "contextAdvanced": { "recentMessageLimit": 999 }
              }
            }
            """, Encoding.UTF8, "application/json");
        var badInteractionResp = await client.PostAsync($"/api/v1/sessions/{sessionId}/interactions", camelBadInteraction);
        Assert.Equal(HttpStatusCode.BadRequest, badInteractionResp.StatusCode);
        var badBody = await badInteractionResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_session_settings", badBody.GetProperty("code").GetString());
    }

    private static async Task<Guid> CloneAndPublishModeWithDefaultOptionsAsync(
        HttpClient client,
        Guid sourceModeId,
        string displayName,
        object defaultOptions)
    {
        var cloneResp = await client.PostAsJsonAsync($"/api/v1/agent-modes/{sourceModeId}/clone", new
        {
            display_name = displayName
        });
        cloneResp.EnsureSuccessStatusCode();
        var cloned = await cloneResp.Content.ReadFromJsonAsync<JsonElement>();
        var clonedId = cloned.GetProperty("id").GetGuid();

        var detailResp = await client.GetAsync($"/api/v1/agent-modes/{clonedId}");
        detailResp.EnsureSuccessStatusCode();
        var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
        var revision = detail.GetProperty("revision").GetInt32();

        var draftResp = await client.PutAsJsonAsync($"/api/v1/agent-modes/{clonedId}/draft", new
        {
            expected_revision = revision,
            default_options = defaultOptions
        });
        draftResp.EnsureSuccessStatusCode();

        var publishResp = await client.PostAsJsonAsync($"/api/v1/agent-modes/{clonedId}/publish", new { });
        publishResp.EnsureSuccessStatusCode();

        var publishedDetailResp = await client.GetAsync($"/api/v1/agent-modes/{clonedId}");
        publishedDetailResp.EnsureSuccessStatusCode();
        var publishedDetail = await publishedDetailResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(publishedDetail.GetProperty("default_options").TryGetProperty("context_strategy", out _));
        return publishedDetail.GetProperty("latest_published_mode_version_id").GetGuid();
    }

    private sealed class OverlayFactory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
