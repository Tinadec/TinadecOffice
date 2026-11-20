using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The pack → row projection contract for the graph surface. Installing a pack used to write
/// the team bindings and the node relationship files into the frozen ModeVersionRecord
/// snapshot ONLY, so the mode row (which is what GET, the editor and the runtime roster read)
/// came back with an empty team and empty duty contracts. These tests install a pack that
/// declares all three documents and assert they survive the round trip.
///
/// A local manifest is used rather than the bundled GraphSeedPack so the assertions do not
/// drift with the shipped artifact; the digest is computed over the same round-tripped DTO
/// shape Core validates, exactly as the Agent Pack lifecycle tests do.
/// </summary>
public sealed class AgentPackModeProjectionTests : IAsyncLifetime
{
    private const string PackId = "tinadec.tests.mode-projection";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-pack-mode-projection", Guid.NewGuid().ToString("N"));
    private ProjectionFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new ProjectionFactory(_root);
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
    /// An installed pack mode reads back with its team bindings, each node's relationship file
    /// and the explicit tier — the three documents the projection used to drop.
    /// </summary>
    [Fact]
    public async Task InstalledPackMode_ReadbackCarriesBindingsRelationshipAndTier()
    {
        var client = _factory!.CreateClient();
        await InstallPackAsync(client);
        var mode = await ReadModeAsync(client, "conversation.pipeline");

        Assert.True(mode.GetProperty("managed").GetBoolean());

        // ① team bindings, both shapes, with the manifest's own key spelling preserved.
        var bindings = mode.GetProperty("bindings");
        Assert.Equal(JsonValueKind.Array, bindings.ValueKind);
        Assert.Equal(2, bindings.GetArrayLength());
        var nodeBinding = bindings.EnumerateArray().Single(binding => binding.TryGetProperty("node_key", out _));
        Assert.Equal("leader", nodeBinding.GetProperty("node_key").GetString());
        Assert.False(nodeBinding.GetProperty("tool_switches").GetProperty("*").GetBoolean());
        Assert.Equal(2, nodeBinding.GetProperty("envelope").GetProperty("spawn").GetProperty("max_depth").GetInt32());
        // The node-less binding is the one WITHOUT `node_key`, not merely the one with `agent_ref`:
        // `AgentPackModeBindingDto` declares both properties as optional on the same type, so a
        // node-keyed binding carries `agent_ref` too and `Single(b => b.TryGetProperty("agent_ref"))`
        // matches both. This predicate and the one above partition the array exactly.
        var agentBinding = bindings.EnumerateArray()
            .Single(binding => !binding.TryGetProperty("node_key", out _) && binding.TryGetProperty("agent_ref", out _));
        // The stored document keeps the manifest's own spelling (`agent:<resource_key>`),
        // which is what the desktop's teamShape.ts strips before matching a slug.
        Assert.Equal("agent:worker.stage", agentBinding.GetProperty("agent_ref").GetString());
        Assert.Equal("", agentBinding.GetProperty("envelope").GetProperty("resources").GetProperty("read")[0].GetString());
        Assert.Equal("", agentBinding.GetProperty("envelope").GetProperty("resources").GetProperty("write")[0].GetString());

        // ② every node's relationship file, byte-shaped like the manifest declared it.
        var nodes = mode.GetProperty("nodes");
        Assert.Equal(3, nodes.GetArrayLength());
        foreach (var node in nodes.EnumerateArray())
        {
            var relationship = node.GetProperty("relationship");
            Assert.Equal(JsonValueKind.Object, relationship.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(relationship.GetProperty("duty").GetString()));
        }
        var leader = nodes.EnumerateArray().Single(node => node.GetProperty("node_key").GetString() == "leader");
        Assert.Equal("Own the goal and hand each stage on", leader.GetProperty("relationship").GetProperty("duty").GetString());
        Assert.Contains(leader.GetProperty("relationship").GetProperty("agent_types").EnumerateArray(),
            item => item.GetString() == "worker.stage");

        // ③ the explicit tier, which is what the runtime prefers over inference.
        Assert.Equal("deterministic", mode.GetProperty("orchestration_tier").GetString());

        // And the frozen snapshot carries the same tier, so a run freeze reads it.
        var snapshot = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{mode.GetProperty("id").GetGuid()}/versions"))![0].GetProperty("snapshot");
        Assert.Equal("deterministic", snapshot.GetProperty("orchestration_tier").GetString());
    }

    /// <summary>
    /// A mode whose pack declares no bindings/relationship/tier must NOT grow those keys: the
    /// snapshot is hashed, so writing an empty value would change the topology hash of every
    /// mode that never opted in.
    /// </summary>
    [Fact]
    public async Task InstalledPackMode_WithoutGraphDocuments_GrowsNoEmptyKeys()
    {
        var client = _factory!.CreateClient();
        await InstallPackAsync(client, includeGraphDocuments: false);
        var mode = await ReadModeAsync(client, "conversation.pipeline");

        Assert.Equal(JsonValueKind.Null, mode.GetProperty("bindings").ValueKind);
        Assert.Equal(JsonValueKind.Null, mode.GetProperty("orchestration_tier").ValueKind);
        Assert.All(mode.GetProperty("nodes").EnumerateArray(),
            node => Assert.Equal(JsonValueKind.Null, node.GetProperty("relationship").ValueKind));

        var snapshot = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{mode.GetProperty("id").GetGuid()}/versions"))![0].GetProperty("snapshot");
        Assert.False(snapshot.TryGetProperty("orchestration_tier", out _));
    }

    // ── helpers ──

    private static async Task<JsonElement> ReadModeAsync(HttpClient client, string slug)
    {
        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?status=published");
        Assert.NotNull(modes);
        var row = modes!.First(mode => mode.GetProperty("slug").GetString() == slug
            && mode.GetProperty("pack_id").GetString() == PackId);
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{row.GetProperty("id").GetGuid()}");
    }

    private static async Task InstallPackAsync(HttpClient client, bool includeGraphDocuments = true)
    {
        // The envelope is a JsonObject, not a string: passing the SERIALIZED TEXT to
        // PostAsJsonAsync makes System.Text.Json write it as one JSON string literal, so the
        // body arrives as `"{\"manifest\":…}"` and the server rejects it at `$` with
        // "The JSON value could not be converted to AgentPackEnvelopeDto".
        var envelope = BuildEnvelope(includeGraphDocuments);
        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        Assert.True(previewResponse.StatusCode == HttpStatusCode.OK, $"install-preview failed: {previewResponse.StatusCode} {previewBody}");
        var preview = JsonDocument.Parse(previewBody).RootElement;
        Assert.Equal("install", preview.GetProperty("action").GetString());

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-packs/{PackId}")
        {
            // Deep-clone so the request content owns its own nodes instead of aliasing the
            // tree the preview call already serialized.
            Content = JsonContent.Create(new
            {
                preview_id = preview.GetProperty("preview_id").GetGuid(),
                envelope = envelope.DeepClone()
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "mode-projection-install");
        using var install = await client.SendAsync(request);
        var installBody = await install.Content.ReadAsStringAsync();
        Assert.True(install.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"install failed: {install.StatusCode} {installBody}");
    }

    /// <summary>
    /// A three-node pipeline (leader → stage1 → stage2) with all the graph documents the
    /// projection used to lose. <paramref name="includeGraphDocuments"/> turns them off to
    /// prove the absent case grows no keys.
    /// </summary>
    private static JsonObject BuildEnvelope(bool includeGraphDocuments)
    {
        var capabilities = new[] { "agent_pack_lifecycle", "versioned_agent_configuration", "frozen_agent_version_binding" };
        var leaderNode = new JsonObject
        {
            ["node_key"] = "leader",
            ["agent_ref"] = "agent:leader",
            ["layer"] = "operation",
            ["label"] = "Leader",
            ["config"] = new JsonObject { ["conversation"] = true },
            ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
        };
        var stage1Node = new JsonObject
        {
            ["node_key"] = "stage-1",
            ["agent_ref"] = "agent:worker.stage",
            ["layer"] = "execution",
            ["label"] = "Stage 1",
            ["config"] = new JsonObject(),
            ["position"] = new JsonObject { ["x"] = 240, ["y"] = 0 },
        };
        var stage2Node = new JsonObject
        {
            ["node_key"] = "stage-2",
            ["agent_ref"] = "agent:worker.stage",
            ["layer"] = "execution",
            ["label"] = "Stage 2",
            ["config"] = new JsonObject(),
            ["position"] = new JsonObject { ["x"] = 480, ["y"] = 0 },
        };
        var mode = new JsonObject
        {
            ["resource_key"] = "conversation.pipeline",
            ["slug"] = "conversation.pipeline",
            ["display_name"] = "Pipeline",
            ["description"] = "A deterministic stage pipeline.",
            ["nodes"] = new JsonArray(leaderNode, stage1Node, stage2Node),
            ["edges"] = new JsonArray(
                // edge_key is validated by RequiredToken: ASCII letters/digits and . _ - only.
                // ">" is NOT accepted (that is the manifest rule, not a mode-draft rule).
                new JsonObject { ["edge_key"] = "leader_to_stage_1", ["source_node_key"] = "leader", ["target_node_key"] = "stage-1", ["condition"] = new JsonObject() },
                new JsonObject { ["edge_key"] = "stage_1_to_stage_2", ["source_node_key"] = "stage-1", ["target_node_key"] = "stage-2", ["condition"] = new JsonObject() }),
            ["canvas_layout"] = new JsonObject(),
        };

        if (includeGraphDocuments)
        {
            capabilities = [.. capabilities, "graph_mode_packs"];
            leaderNode["relationship"] = new JsonObject
            {
                ["duty"] = "Own the goal and hand each stage on",
                ["inputs_outputs"] = new JsonArray("goal in", "final answer out"),
                ["allowed_dispatch_targets"] = new JsonArray("worker.stage"),
                ["agent_types"] = new JsonArray("worker.stage"),
                ["success_criteria"] = new JsonArray("Every stage completed with evidence"),
            };
            stage1Node["relationship"] = new JsonObject
            {
                ["duty"] = "Produce the first artefact",
                ["inputs_outputs"] = new JsonArray("goal in", "artefact out"),
            };
            stage2Node["relationship"] = new JsonObject
            {
                ["duty"] = "Turn the first artefact into the answer",
                ["inputs_outputs"] = new JsonArray("artefact in", "answer out"),
            };
            mode["bindings"] = new JsonArray(
                new JsonObject
                {
                    ["node_key"] = "leader",
                    ["tool_switches"] = new JsonObject { ["*"] = false },
                    ["envelope"] = new JsonObject { ["spawn"] = new JsonObject { ["max_depth"] = 2, ["max_agents_per_run"] = 4, ["max_parallel_workers"] = 2 } },
                    ["includes_core_reserved"] = false,
                },
                new JsonObject
                {
                    ["agent_ref"] = "agent:worker.stage",
                    ["envelope"] = new JsonObject { ["resources"] = new JsonObject { ["read"] = new JsonArray(""), ["write"] = new JsonArray("") } },
                    ["includes_core_reserved"] = false,
                });
            mode["orchestration_tier"] = "deterministic";
        }

        var manifest = new JsonObject
        {
            ["api_version"] = "tinadec.io/agent-pack/v1alpha1",
            ["kind"] = "AgentPack",
            ["metadata"] = new JsonObject
            {
                ["pack_id"] = PackId,
                ["owner"] = "tinadec.tests",
                ["product_id"] = "tinadec.tests",
                ["name"] = "ModeProjectionFixture",
                ["version"] = "1.0.0",
            },
            ["compatibility"] = new JsonObject
            {
                ["minimum_core_version"] = "0.1.0",
                ["required_core_capabilities"] = new JsonArray(capabilities.Select(capability => (JsonNode)JsonValue.Create(capability)!).ToArray()),
            },
            ["resources"] = new JsonObject
            {
                ["agents"] = new JsonArray(
                    new JsonObject
                    {
                        ["resource_key"] = "leader",
                        ["slug"] = "leader",
                        ["display_name"] = "Projection Leader",
                        ["description"] = "Owns the goal and dispatches stages.",
                        ["layer"] = "operation",
                        ["role"] = "session_coordinator",
                        ["capabilities"] = new JsonArray("user.respond"),
                        ["model_strategy"] = new JsonObject { ["kind"] = "inherit" },
                        ["tool_scope"] = new JsonArray("read_file"),
                        ["system_prompt"] = "You own the goal.",
                        ["enabled"] = true,
                        ["base_prompt_pipeline_ref"] = "prompt:projection-prompt",
                    },
                    new JsonObject
                    {
                        ["resource_key"] = "worker.stage",
                        ["slug"] = "worker.stage",
                        ["display_name"] = "Projection Stage Worker",
                        ["description"] = "Produces one stage artefact.",
                        ["layer"] = "execution",
                        ["role"] = "stage_worker",
                        ["capabilities"] = new JsonArray(),
                        ["model_strategy"] = new JsonObject { ["kind"] = "inherit" },
                        ["tool_scope"] = new JsonArray("read_file", "write_file"),
                        ["system_prompt"] = "You produce the artefact for your stage.",
                        ["enabled"] = true,
                        ["base_prompt_pipeline_ref"] = "prompt:projection-prompt",
                    }),
                ["prompt_pipelines"] = new JsonArray(
                    new JsonObject
                    {
                        ["resource_key"] = "projection-prompt",
                        ["slug"] = "projection-prompt",
                        ["display_name"] = "Projection Prompt",
                        ["graph"] = new JsonObject
                        {
                            ["nodes"] = new JsonArray(
                                new JsonObject { ["id"] = "template", ["type"] = "template", ["config"] = new JsonObject { ["content"] = "Projection baseline." } },
                                new JsonObject { ["id"] = "assemble", ["type"] = "assemble" }),
                            ["edges"] = new JsonArray(
                                new JsonObject { ["source"] = "template", ["target"] = "assemble" }),
                        },
                    }),
                ["modes"] = new JsonArray(mode),
            },
            ["activation"] = new JsonObject
            {
                ["workspace_defaults"] = new JsonObject
                {
                    ["agent_ref"] = "agent:leader",
                    ["mode_ref"] = "mode:conversation.pipeline",
                    ["prompt_pipeline_ref"] = "prompt:projection-prompt",
                },
            },
        };

        // Digest the same round-tripped DTO shape Core validates.
        var manifestElement = JsonSerializer.SerializeToElement(manifest);
        var serverOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var roundTripped = JsonSerializer.Deserialize<TinadecCore.Contracts.Dtos.AgentPackManifestDto>(manifestElement.GetRawText(), serverOptions);
        var digest = Convert.ToHexString(SHA256.HashData(Canonicalize(JsonSerializer.SerializeToElement(roundTripped, serverOptions)))).ToLowerInvariant();
        // Built as ONE JsonNode tree. A JsonObject nested inside an anonymous object
        // serializes as an escaped JSON *string*, which is what made the envelope
        // un-deserializable at `$` — and the caller must receive the NODE, not its text,
        // or PostAsJsonAsync writes a string literal instead of an object.
        return new JsonObject
        {
            ["manifest"] = manifest,
            ["integrity"] = new JsonObject { ["algorithm"] = "sha256", ["digest"] = digest },
        };
    }

    private static byte[] Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
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
                throw new InvalidDataException("Unsupported JSON value in the mode projection fixture manifest.");
        }
    }

    private sealed class ProjectionFactory : IsolatedApiFactory
    {
        private readonly string _root;
        public ProjectionFactory(string root) => _root = root;

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
