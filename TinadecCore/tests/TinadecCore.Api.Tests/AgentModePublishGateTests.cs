using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The tool-switch predicate and the publish gates, pinned at every layer that used to have its
/// own copy of the rule:
///
/// ① <c>"*": false</c> must be honoured identically by <see cref="GraphValidation"/>,
///   <see cref="ModePublishGate"/> and the pack admission path. It used to be honoured on two
///   of the three, which is how a pack could declare "shed the whole face" and still ship it.
/// ② A CUSTOM publish must run the same two gates a PACK publish runs — the binding envelope
///   gate and the exploration-tool/policy gate — so the two paths cannot diverge.
/// </summary>
public sealed class AgentModePublishGateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-mode-publish-gate", Guid.NewGuid().ToString("N"));
    private PublishGateFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new PublishGateFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    // ── ① the one switch predicate ─────────────────────────────────────────────

    /// <summary>
    /// The predicate itself: an exact id wins over the wildcard (so "shed everything, re-arm
    /// one tool" works), a bool <c>false</c> and the string <c>"false"</c> both count, and a
    /// non-object/absent map switches nothing off.
    /// </summary>
    [Fact]
    public void SwitchPredicate_HonoursExactIdWildcardAndStringFalse()
    {
        using var document = JsonDocument.Parse("""
            {
              "wildcard": {"*": false},
              "rearm": {"*": false, "write_file": true},
              "exact": {"write_file": false},
              "stringly": {"shell": "false"},
              "on": {"write_file": true},
              "empty": {}
            }
            """);
        var root = document.RootElement;
        JsonElement Map(string name) => root.GetProperty(name);

        // The wildcard sheds every tool it is asked about.
        Assert.True(GraphValidation.IsSwitchedOff(Map("wildcard"), "write_file"));
        Assert.True(GraphValidation.IsSwitchedOff(Map("wildcard"), "read_file"));
        // An exact id beats the wildcard in both directions.
        Assert.False(GraphValidation.IsSwitchedOff(Map("rearm"), "write_file"));
        Assert.True(GraphValidation.IsSwitchedOff(Map("rearm"), "shell"));
        // An exact false and the string spelling both switch off.
        Assert.True(GraphValidation.IsSwitchedOff(Map("exact"), "write_file"));
        Assert.True(GraphValidation.IsSwitchedOff(Map("stringly"), "shell"));
        // True, an empty map and a missing map switch nothing off.
        Assert.False(GraphValidation.IsSwitchedOff(Map("on"), "write_file"));
        Assert.False(GraphValidation.IsSwitchedOff(Map("empty"), "write_file"));
        Assert.False(GraphValidation.IsSwitchedOff(null, "write_file"));

        // ModePublishGate answers identically — one implementation, two callers.
        Assert.True(ModePublishGate.IsSwitchedOff(Map("wildcard"), "write_file"));
        Assert.False(ModePublishGate.IsSwitchedOff(Map("rearm"), "write_file"));
        Assert.True(ModePublishGate.IsSwitchedOff(Map("stringly"), "shell"));
    }

    /// <summary>
    /// A mode binding that declares <c>"*": false</c> on its leader sheds the leader's whole
    /// tool face in the FROZEN snapshot, which is what keeps the run in the team tier instead
    /// of degrading to solo_dispatch. This is the pack path's copy of the predicate, exercised
    /// through a real install.
    /// </summary>
    [Fact]
    public async Task PackPublish_WildcardFalse_ShedsTheWholeToolFaceInTheSnapshot()
    {
        var client = _factory!.CreateClient();
        await InstallPackAsync(client);
        var mode = await ReadModeAsync(client, "conversation.wildcard");

        var snapshot = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{mode.GetProperty("id").GetGuid()}/versions"))![0].GetProperty("snapshot");
        var leader = snapshot.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("node_key").GetString() == "leader");
        Assert.Equal(0, leader.GetProperty("effective_tools").GetArrayLength());

        // The worker's binding switches nothing off, so its face survives.
        var worker = snapshot.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("node_key").GetString() == "worker");
        Assert.Contains(worker.GetProperty("effective_tools").EnumerateArray(), tool => tool.GetString() == "read_file");
    }

    // ── ② custom publish runs the pack gates ──────────────────────────────────

    /// <summary>
    /// A custom publish that keeps a workspace-mutating tool with no write grant must be
    /// refused exactly as a pack publish would be. Before the unification the manual path
    /// never consulted <see cref="ModePublishGate"/> at all.
    /// </summary>
    [Fact]
    public async Task CustomPublish_MutatingToolWithoutWriteGrant_IsRefused()
    {
        var client = _factory!.CreateClient();
        var leaderId = await CreatePublishedLeaderAsync(client, "gate-leader", ["read_file", "write_file"]);
        var modeId = await CreateModeAsync(client, "gate-mutating", leaderId, bindings: new object[]
        {
            new { node_key = "leader", includes_core_reserved = false }
        });

        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        var body = await publish.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(AgentTopologyCodes.ModePublishRejected, problem.GetProperty("code").GetString());
        Assert.Contains("mutating_tool_without_write_grant", problem.GetProperty("message").GetString());
        // The message names the tools and the remedy, so the editor can tell the author what to do.
        Assert.Contains("write_file", problem.GetProperty("message").GetString());
        Assert.Contains("resources.write", problem.GetProperty("message").GetString());
    }

    /// <summary>
    /// The same mode WITH a write grant publishes: the gate refuses the contradiction, not the
    /// configuration.
    /// </summary>
    [Fact]
    public async Task CustomPublish_MutatingToolWithWriteGrant_IsAccepted()
    {
        var client = _factory!.CreateClient();
        var leaderId = await CreatePublishedLeaderAsync(client, "gate-leader-granted", ["read_file", "write_file"]);
        var modeId = await CreateModeAsync(client, "gate-granted", leaderId, bindings: new object[]
        {
            new
            {
                node_key = "leader",
                envelope = new { resources = new { write = new[] { "" } } },
                includes_core_reserved = false
            }
        });

        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        var body = await publish.Content.ReadAsStringAsync();
        Assert.True(publish.IsSuccessStatusCode, $"publish failed: {publish.StatusCode} {body} | {_factory.LastFailure()}");
    }

    /// <summary>
    /// Switching the mutating tools off with <c>"*": false</c> is the other legal remedy, and it
    /// proves the gate reads the SWITCHED surface rather than the template's raw scope.
    /// </summary>
    [Fact]
    public async Task CustomPublish_WildcardFalse_RemovesTheMutatingToolsSoTheGatePasses()
    {
        var client = _factory!.CreateClient();
        var leaderId = await CreatePublishedLeaderAsync(client, "gate-leader-switched", ["read_file", "write_file"]);
        var modeId = await CreateModeAsync(client, "gate-switched", leaderId, bindings: new object[]
        {
            new
            {
                node_key = "leader",
                tool_switches = new Dictionary<string, bool> { ["*"] = false },
                includes_core_reserved = false
            }
        });

        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        var body = await publish.Content.ReadAsStringAsync();
        Assert.True(publish.IsSuccessStatusCode, $"publish failed: {publish.StatusCode} {body} | {_factory.LastFailure()}");

        // And the frozen surface really is empty: the wildcard was honoured on the publish path.
        var snapshot = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{modeId}/versions"))![0].GetProperty("snapshot");
        Assert.Equal(0, snapshot.GetProperty("nodes")[0].GetProperty("effective_tools").GetArrayLength());
    }

    /// <summary>
    /// A mode that keeps an exploration tool without enabling its runtime-policy feature is
    /// refused by the same gate pack admission runs (<see cref="ExplorationToolPolicy"/>).
    /// </summary>
    [Fact]
    public async Task CustomPublish_ExplorationToolWithoutItsPolicyFeature_IsRefused()
    {
        var client = _factory!.CreateClient();
        var leaderId = await CreatePublishedLeaderAsync(client, "gate-leader-exploration",
            ["read_file", CoreVirtualToolPolicy.ExplorationViewToolId]);
        var modeId = await CreateModeAsync(client, "gate-exploration", leaderId, bindings: Array.Empty<object>());

        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        var body = await publish.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(AgentTopologyCodes.ModePublishRejected, problem.GetProperty("code").GetString());
        Assert.Contains(CoreVirtualToolPolicy.ExplorationViewToolId, problem.GetProperty("message").GetString());
        Assert.Contains("runtime_policy", problem.GetProperty("message").GetString());
    }

    // ── helpers ──

    private static async Task<JsonElement> ReadModeAsync(HttpClient client, string slug)
    {
        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?status=published");
        Assert.NotNull(modes);
        var row = modes!.First(mode => mode.GetProperty("slug").GetString() == slug);
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{row.GetProperty("id").GetGuid()}");
    }

    private static async Task<Guid> CreatePublishedLeaderAsync(HttpClient client, string slug, string[] toolScope)
    {
        using var create = await client.PostAsJsonAsync("/api/v1/agents", new
        {
            slug,
            display_name = slug,
            layer = "operation",
            role = "session_coordinator",
            tool_scope = toolScope,
            capabilities = Array.Empty<string>(),
            enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var leader = await create.Content.ReadFromJsonAsync<JsonElement>();
        var leaderId = leader.GetProperty("id").GetGuid();
        using var publish = await client.PostAsync($"/api/v1/agents/{leaderId}/publish", null);
        Assert.True(publish.IsSuccessStatusCode, $"agent publish failed: {publish.StatusCode} {await publish.Content.ReadAsStringAsync()}");
        return leaderId;
    }

    private static async Task<Guid> CreateModeAsync(HttpClient client, string slug, Guid leaderId, object[] bindings)
    {
        using var create = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug,
            display_name = slug,
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
            bindings
        });
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"mode create failed: {create.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Installs a minimal pack whose single mode has a leader with a real tool face and a
    /// worker, so the leader's <c>"*": false</c> binding has something to shed.
    /// </summary>
    private static async Task InstallPackAsync(HttpClient client)
    {
        var manifest = new
        {
            api_version = "tinadec.io/agent-pack/v1alpha1",
            kind = "AgentPack",
            metadata = new
            {
                pack_id = "tinadec.tests.wildcard",
                owner = "tinadec.tests",
                product_id = "tinadec.tests",
                name = "WildcardFixture",
                version = "1.0.0"
            },
            compatibility = new
            {
                minimum_core_version = "0.1.0",
                required_core_capabilities = new[] { "agent_pack_lifecycle", "versioned_agent_configuration", "frozen_agent_version_binding", "graph_mode_packs" }
            },
            resources = new
            {
                agents = new object[]
                {
                    new
                    {
                        resource_key = "leader",
                        slug = "wildcard.leader",
                        display_name = "Wildcard Leader",
                        description = "Leads the wildcard mode.",
                        layer = "operation",
                        role = "session_coordinator",
                        capabilities = new[] { "user.respond" },
                        model_strategy = new { kind = "inherit" },
                        tool_scope = new[] { "read_file", "write_file" },
                        system_prompt = "Lead.",
                        enabled = true,
                        base_prompt_pipeline_ref = "prompt:wildcard-prompt"
                    },
                    new
                    {
                        resource_key = "worker",
                        slug = "wildcard.worker",
                        display_name = "Wildcard Worker",
                        description = "Does the work.",
                        layer = "execution",
                        role = "task_executor",
                        capabilities = Array.Empty<string>(),
                        model_strategy = new { kind = "inherit" },
                        tool_scope = new[] { "read_file" },
                        system_prompt = "Work.",
                        enabled = true,
                        base_prompt_pipeline_ref = "prompt:wildcard-prompt"
                    }
                },
                prompt_pipelines = new object[]
                {
                    new
                    {
                        resource_key = "wildcard-prompt",
                        slug = "wildcard-prompt",
                        display_name = "Wildcard Prompt",
                        graph = new
                        {
                            nodes = new object[]
                            {
                                new { id = "template", type = "template", config = new { content = "Wildcard baseline." } },
                                new { id = "assemble", type = "assemble" }
                            },
                            edges = new object[] { new { source = "template", target = "assemble" } }
                        }
                    }
                },
                modes = new object[]
                {
                    new
                    {
                        resource_key = "conversation.wildcard",
                        slug = "conversation.wildcard",
                        display_name = "Wildcard",
                        description = "Sheds the leader's tool face.",
                        nodes = new object[]
                        {
                            new { node_key = "leader", agent_ref = "agent:leader", layer = "operation", label = "Leader", config = new { conversation = true }, position = new { x = 0, y = 0 } },
                            new { node_key = "worker", agent_ref = "agent:worker", layer = "execution", label = "Worker", config = new { }, position = new { x = 240, y = 0 } }
                        },
                        edges = new object[]
                        {
                            new { edge_key = "leader_to_worker", source_node_key = "leader", target_node_key = "worker", condition = new { } }
                        },
                        bindings = new object[]
                        {
                            new { node_key = "leader", tool_switches = new Dictionary<string, bool> { ["*"] = false }, includes_core_reserved = false },
                            new { node_key = "worker", includes_core_reserved = false }
                        },
                        canvas_layout = new { }
                    }
                }
            },
            activation = new
            {
                workspace_defaults = new
                {
                    agent_ref = "agent:leader",
                    mode_ref = "mode:conversation.wildcard",
                    prompt_pipeline_ref = "prompt:wildcard-prompt"
                }
            }
        };

        // Digest the round-tripped DTO shape Core validates.
        var manifestElement = JsonSerializer.SerializeToElement(manifest);
        var serverOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var roundTripped = JsonSerializer.Deserialize<TinadecCore.Contracts.Dtos.AgentPackManifestDto>(manifestElement.GetRawText(), serverOptions);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(roundTripped, serverOptions)))).ToLowerInvariant();
        var envelope = JsonSerializer.SerializeToElement(new { manifest, integrity = new { algorithm = "sha256", digest } });

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        Assert.True(previewResponse.StatusCode == HttpStatusCode.OK, $"install-preview failed: {previewResponse.StatusCode} {previewBody}");
        var preview = JsonDocument.Parse(previewBody).RootElement;

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.tests.wildcard")
        {
            Content = JsonContent.Create(new
            {
                preview_id = preview.GetProperty("preview_id").GetGuid(),
                envelope
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "wildcard-install");
        using var install = await client.SendAsync(request);
        var installBody = await install.Content.ReadAsStringAsync();
        Assert.True(install.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"install failed: {install.StatusCode} {installBody}");
    }

    private sealed class PublishGateFactory : IsolatedApiFactory
    {
        private readonly string _root;
        public PublishGateFactory(string root) => _root = root;

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
