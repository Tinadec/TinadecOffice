using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The two mode-lifecycle routes that the Gateway and the Desktop already called but Core
/// never mapped: <c>POST /agent-modes/{id}/clone</c> and <c>DELETE /agent-modes/{id}</c>.
///
/// What these tests pin down, beyond "the route exists":
/// ① a clone is an EDITABLE COPY — instructions, the team binding list, every node's
///   relationship file and the explicit tier all travel with it (a clone that lost them
///   produced an empty team in the editor, which is the defect this replaces);
/// ② a clone of a pack-managed mode is unmanaged and writable;
/// ③ delete removes a user-authored mode whether draft or published, refuses a
///   pack-managed one with 409 <c>built_in_read_only</c>, keeps the version history, and
///   clears a workspace default that pointed at the deleted mode.
/// </summary>
public sealed class AgentModeCloneDeleteTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-agent-mode-clone-delete", Guid.NewGuid().ToString("N"));
    private CloneDeleteFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new CloneDeleteFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A custom team mode carrying all three documents, cloned: the copy must come back with
    /// the same instructions, the same two bindings (one node-keyed, one agent-ref'd) and the
    /// same node relationship — as a DRAFT, with a distinct id and slug.
    /// </summary>
    [Fact]
    public async Task Clone_CopiesInstructionsBindingsRelationshipAndTierIntoTypedDraft()
    {
        var client = _factory!.CreateClient();
        var (leaderId, memberSlug) = await CreatePublishedLeaderAndFindMemberAsync(client);
        var sourceId = await CreateTeamModeAsync(client, "clone-source", leaderId, memberSlug, tier: "deterministic");

        using var clone = await client.PostAsJsonAsync($"/api/v1/agent-modes/{sourceId}/clone", new { display_name = "Clone Source (copy)" });
        var cloneBody = await clone.Content.ReadAsStringAsync();
        Assert.True(clone.StatusCode == HttpStatusCode.Created,
            $"clone failed: {clone.StatusCode} {cloneBody} | {_factory.LastFailure()}");
        var copy = JsonDocument.Parse(cloneBody).RootElement;

        // A copy, not the same row: new identity, new slug, and it starts as an editable draft.
        var copyId = copy.GetProperty("id").GetGuid();
        // The Location header is the string passed to Results.Created; tolerate an absolute
        // form so a host that absolutizes it does not fail an otherwise correct clone.
        Assert.EndsWith($"/api/v1/agent-modes/{copyId}", clone.Headers.Location?.OriginalString ?? string.Empty);
        Assert.NotEqual(sourceId, copyId);
        Assert.Equal("draft", copy.GetProperty("status").GetString());
        Assert.Equal("Clone Source (copy)", copy.GetProperty("display_name").GetString());
        Assert.NotEqual("clone-source", copy.GetProperty("slug").GetString());

        // ① instructions travel.
        Assert.Equal("Route every step through the leader.", copy.GetProperty("instructions").GetString());

        // ② the explicit tier travels — the whole feature is inert without this.
        Assert.Equal("deterministic", copy.GetProperty("orchestration_tier").GetString());

        // ③ the team bindings travel, both shapes intact.
        var bindings = copy.GetProperty("bindings");
        Assert.Equal(JsonValueKind.Array, bindings.ValueKind);
        Assert.Equal(2, bindings.GetArrayLength());
        Assert.Contains(bindings.EnumerateArray(), binding =>
            binding.TryGetProperty("node_key", out var nodeKey) && nodeKey.GetString() == "leader");
        // The custom mode declared a bare slug; the clone must copy it verbatim (a custom mode
        // is not re-shaped through the pack's `agent:` reference translation).
        Assert.Contains(bindings.EnumerateArray(), binding =>
            binding.TryGetProperty("agent_ref", out var agentRef) && agentRef.GetString() == memberSlug);

        // ④ every node's relationship file travels.
        var nodes = copy.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        var leaderNode = nodes.EnumerateArray().Single(node => node.GetProperty("node_key").GetString() == "leader");
        var relationship = leaderNode.GetProperty("relationship");
        Assert.Equal("Own the goal and dispatch members", relationship.GetProperty("duty").GetString());
        Assert.Contains(relationship.GetProperty("agent_types").EnumerateArray(), item => item.GetString() == memberSlug);
        var memberNode = nodes.EnumerateArray().Single(node => node.GetProperty("node_key").GetString() == "member");
        Assert.Equal("Do the browsing", memberNode.GetProperty("relationship").GetProperty("duty").GetString());

        // The clone is independently editable: a draft save on the copy leaves the source alone.
        // The submission keeps the conversation node on purpose — a draft whose node list
        // removes every conversation-capable node is refused by GraphValidation, which is
        // correct behaviour and not what this test is about.
        using var save = await client.PutAsJsonAsync($"/api/v1/agent-modes/{copyId}/draft", new
        {
            instructions = "Edited copy only.",
            nodes = new object[]
            {
                new
                {
                    node_key = "leader",
                    agent_definition_id = leaderId,
                    layer = "operation",
                    label = "Leader",
                    config = new { conversation = true }
                }
            },
            edges = Array.Empty<object>(),
        });
        Assert.True(save.IsSuccessStatusCode, $"clone draft save failed: {save.StatusCode} {await save.Content.ReadAsStringAsync()}");
        var sourceReread = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{sourceId}");
        Assert.Equal("Route every step through the leader.", sourceReread.GetProperty("instructions").GetString());
        Assert.Equal(2, sourceReread.GetProperty("nodes").GetArrayLength());
    }

    /// <summary>
    /// A pack-managed mode clones (that is the whole point of "clone before customizing") and
    /// the copy is NOT managed, so the editor may write to it.
    /// </summary>
    [Fact]
    public async Task Clone_PackManagedMode_ProducesAnUnmanagedWritableDraft()
    {
        var client = _factory!.CreateClient();
        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?status=published");
        Assert.NotNull(modes);
        var packMode = modes!.First(mode => mode.GetProperty("slug").GetString() == "conversation.agent");

        using var clone = await client.PostAsJsonAsync($"/api/v1/agent-modes/{packMode.GetProperty("id").GetGuid()}/clone", new { });
        var body = await clone.Content.ReadAsStringAsync();
        Assert.True(clone.StatusCode == HttpStatusCode.Created, $"clone failed: {clone.StatusCode} {body} | {_factory.LastFailure()}");
        var copy = JsonDocument.Parse(body).RootElement;

        Assert.False(copy.GetProperty("managed").GetBoolean());
        Assert.Equal("draft", copy.GetProperty("status").GetString());
        // The pack's topology came along: a clone of the 13-node agent mode is not an empty shell.
        Assert.Equal("Agent (copy)", copy.GetProperty("display_name").GetString());
        Assert.Equal(13, copy.GetProperty("nodes").GetArrayLength());

        // And it is writable: the copy is not owned by any pack.
        var copyId = copy.GetProperty("id").GetGuid();
        using var save = await client.PutAsJsonAsync($"/api/v1/agent-modes/{copyId}/draft", new { display_name = "My Agent Mode" });
        Assert.True(save.IsSuccessStatusCode, $"draft save on a clone failed: {save.StatusCode} {await save.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Delete must refuse a pack-managed mode: the pack still owns the row and its next
    /// reconcile would resurrect it, so deleting would be a lie to the user.
    /// </summary>
    [Fact]
    public async Task Delete_PackManagedMode_IsRefusedWithBuiltInReadOnly()
    {
        var client = _factory!.CreateClient();
        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?status=published");
        Assert.NotNull(modes);
        var packModeId = modes!.First(mode => mode.GetProperty("slug").GetString() == "conversation.ask").GetProperty("id").GetGuid();

        using var delete = await client.DeleteAsync($"/api/v1/agent-modes/{packModeId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("built_in_read_only", (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Still readable: a refused delete changes nothing.
        using var reread = await client.GetAsync($"/api/v1/agent-modes/{packModeId}");
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
    }

    /// <summary>
    /// Delete a PUBLISHED user mode: the projection and the mode row go, the historical
    /// ModeVersionRecord rows stay (they are the audit trail every run binding names), and a
    /// workspace default pointing at the mode is cleared so nothing resolves a dead mode.
    /// </summary>
    [Fact]
    public async Task Delete_PublishedCustomMode_RemovesItKeepsVersionHistoryAndClearsDefaults()
    {
        var client = _factory!.CreateClient();
        var (leaderId, memberSlug) = await CreatePublishedLeaderAndFindMemberAsync(client);
        var modeId = await CreateTeamModeAsync(client, "delete-published", leaderId, memberSlug, tier: null);
        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        Assert.True(publish.IsSuccessStatusCode,
            $"publish failed: {publish.StatusCode} {await publish.Content.ReadAsStringAsync()} | {_factory.LastFailure()}");
        var versionId = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{modeId}/versions"))![0].GetProperty("id").GetGuid();

        // Point the workspace defaults at the mode so the clear-on-delete path is exercised.
        using var defaults = await client.PutAsJsonAsync("/api/v1/workspace-defaults/draft", new
        {
            default_agent_mode_id = modeId,
            default_mode_version_id = versionId,
        });
        Assert.True(defaults.IsSuccessStatusCode, $"defaults write failed: {defaults.StatusCode} {await defaults.Content.ReadAsStringAsync()}");

        using var delete = await client.DeleteAsync($"/api/v1/agent-modes/{modeId}");
        var deleteBody = await delete.Content.ReadAsStringAsync();
        Assert.True(delete.StatusCode == HttpStatusCode.OK, $"delete failed: {delete.StatusCode} {deleteBody}");
        var result = JsonDocument.Parse(deleteBody).RootElement;
        Assert.Equal(modeId, result.GetProperty("id").GetGuid());
        Assert.True(result.GetProperty("deleted").GetBoolean());

        // Gone from the read path.
        using var reread = await client.GetAsync($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(HttpStatusCode.NotFound, reread.StatusCode);

        // Version history survives: a run binding that names it must still resolve to a row.
        var versions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{modeId}/versions");
        Assert.NotNull(versions);
        Assert.Single(versions!);

        // Workspace defaults no longer point at the deleted mode.
        var cleared = await client.GetFromJsonAsync<JsonElement>("/api/v1/workspace-defaults");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("default_agent_mode_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("default_mode_version_id").ValueKind);
    }

    /// <summary>Deleting a draft is the historical behaviour and must keep working.</summary>
    [Fact]
    public async Task Delete_DraftCustomMode_RemovesIt()
    {
        var client = _factory!.CreateClient();
        var (leaderId, memberSlug) = await CreatePublishedLeaderAndFindMemberAsync(client);
        var modeId = await CreateTeamModeAsync(client, "delete-draft", leaderId, memberSlug, tier: null);

        using var delete = await client.DeleteAsync($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var reread = await client.GetAsync($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(HttpStatusCode.NotFound, reread.StatusCode);
    }

    /// <summary>Deleting an id that does not exist is a 404, not a silent success.</summary>
    [Fact]
    public async Task Delete_UnknownMode_IsNotFound()
    {
        var client = _factory!.CreateClient();
        using var delete = await client.DeleteAsync($"/api/v1/agent-modes/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("not_found", (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>
    /// The explicit tier is part of the read projection and round-trips through a draft save,
    /// including the <c>solo</c> alias folding onto <c>solo_dispatch</c>. Without the
    /// projection the editor's selector could never show what it had saved.
    /// </summary>
    [Fact]
    public async Task OrchestrationTier_RoundTripsThroughDraftSave_AndAcceptsTheSoloAlias()
    {
        var client = _factory!.CreateClient();
        var (leaderId, memberSlug) = await CreatePublishedLeaderAndFindMemberAsync(client);
        var modeId = await CreateTeamModeAsync(client, "tier-round-trip", leaderId, memberSlug, tier: "free_form");
        Assert.Equal("free_form", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}")).GetProperty("orchestration_tier").GetString());

        using var save = await client.PutAsJsonAsync($"/api/v1/agent-modes/{modeId}/draft", new { orchestration_tier = "solo" });
        Assert.True(save.IsSuccessStatusCode, $"tier save failed: {save.StatusCode} {await save.Content.ReadAsStringAsync()}");
        Assert.Equal("solo_dispatch", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}")).GetProperty("orchestration_tier").GetString());

        // An unsupported tier is refused with a message naming the vocabulary, not silently dropped.
        using var rejected = await client.PutAsJsonAsync($"/api/v1/agent-modes/{modeId}/draft", new { orchestration_tier = "dictatorship" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("invalid_request", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The frozen snapshot carries the tier, which is what the runtime prefers over inference.
        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        Assert.True(publish.IsSuccessStatusCode, $"publish failed: {publish.StatusCode} {await publish.Content.ReadAsStringAsync()} | {_factory.LastFailure()}");
        var snapshot = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{modeId}/versions"))![0].GetProperty("snapshot");
        Assert.Equal("solo_dispatch", snapshot.GetProperty("orchestration_tier").GetString());
    }

    // ── helpers ──

    private static async Task<(Guid leaderId, string memberSlug)> CreatePublishedLeaderAndFindMemberAsync(HttpClient client)
    {
        using var leaderCreate = await client.PostAsJsonAsync("/api/v1/agents", new
        {
            slug = "clone-leader",
            display_name = "Clone Leader",
            layer = "operation",
            role = "session_coordinator",
            tool_scope = new[] { "read_file" },
            capabilities = Array.Empty<string>(),
            enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, leaderCreate.StatusCode);
        var leader = await leaderCreate.Content.ReadFromJsonAsync<JsonElement>();
        var leaderId = leader.GetProperty("id").GetGuid();
        using var leaderPublish = await client.PostAsync($"/api/v1/agents/{leaderId}/publish", null);
        Assert.True(leaderPublish.IsSuccessStatusCode, $"leader publish failed: {leaderPublish.StatusCode} {await leaderPublish.Content.ReadAsStringAsync()}");

        var member = await FindAgentAsync(client, "worker.browser");
        return (leaderId, member.GetProperty("slug").GetString()!);
    }

    private static async Task<JsonElement> FindAgentAsync(HttpClient client, string slug)
    {
        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(agents);
        return agents!.First(agent => agent.GetProperty("slug").GetString() == slug);
    }

    private static async Task<Guid> CreateTeamModeAsync(HttpClient client, string slug, Guid leaderId, string memberSlug, string? tier)
    {
        var memberId = (await FindAgentAsync(client, memberSlug)).GetProperty("id").GetGuid();
        var payload = new Dictionary<string, object?>
        {
            ["slug"] = slug,
            ["display_name"] = "Clone Source",
            ["instructions"] = "Route every step through the leader.",
            ["nodes"] = new object[]
            {
                new
                {
                    node_key = "leader",
                    agent_definition_id = leaderId,
                    layer = "operation",
                    label = "Leader",
                    config = new { conversation = true },
                    relationship = new
                    {
                        duty = "Own the goal and dispatch members",
                        agent_types = new[] { memberSlug },
                        allowed_dispatch_targets = new[] { memberSlug },
                        inputs_outputs = new[] { "goal in", "answer out" },
                        success_criteria = new[] { "The goal is answered with evidence" }
                    }
                },
                new
                {
                    node_key = "member",
                    agent_definition_id = memberId,
                    layer = "execution",
                    label = "Member",
                    relationship = new { duty = "Do the browsing", inputs_outputs = new[] { "query in", "findings out" } }
                }
            },
            ["edges"] = Array.Empty<object>(),
            ["bindings"] = new object[]
            {
                new
                {
                    node_key = "leader",
                    tool_switches = new Dictionary<string, bool> { ["*"] = false },
                    envelope = new { spawn = new { max_depth = 2, max_agents_per_run = 4, max_parallel_workers = 2 } },
                    includes_core_reserved = false
                },
                new
                {
                    agent_ref = memberSlug,
                    envelope = new { resources = new { read = new[] { "" } } },
                    includes_core_reserved = false
                }
            }
        };
        if (tier is not null) payload["orchestration_tier"] = tier;

        using var create = await client.PostAsJsonAsync("/api/v1/agent-modes", payload);
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"mode create failed: {create.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private sealed class CloneDeleteFactory : IsolatedApiFactory
    {
        private readonly string _root;
        public CloneDeleteFactory(string root) => _root = root;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
