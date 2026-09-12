using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// DmaEA graph orchestration pack round (generic fixture — Core deliverables never
/// embed Office content): a three-template graph pack with mode bindings and node
/// relationship files installs under the always-v1 contract, the capability gate
/// rejects new-field packs that skip the graph_mode_packs declaration, the session
/// creation freeze resolves the conversation identity, and the orchestration
/// projection returns the declared graph (graph is no longer null).
/// </summary>
public sealed class VibeGraphPackTests
{
    private const string PackId = "vibe-graph-pack";

    [Fact]
    public async Task VibePack_Installs_IdentityResolves_AndOrchestrationReturnsDeclaredGraph()
    {
        using var factory = new VibePackFactory();
        using var client = factory.CreateClient();
        var envelope = VibeEnvelope();

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(previewResponse.StatusCode == HttpStatusCode.OK, $"preview: {previewResponse.StatusCode} {await previewResponse.Content.ReadAsStringAsync()}");
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();

        var installRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-packs/{PackId}")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        installRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "vibe-install-1");
        using var installResponse = await client.SendAsync(installRequest);
        Assert.True(installResponse.StatusCode == HttpStatusCode.Created, $"install: {installResponse.StatusCode} {await installResponse.Content.ReadAsStringAsync()}");

        // Projectless (free-conversation) session bound to the pack's published
        // mode version: identity frozen at creation — the literal meeting node
        // (legacy tier ③, since the fixture declares no explicit marker).
        var detail = await (await client.GetAsync($"/api/v1/agent-packs/{PackId}")).Content.ReadFromJsonAsync<JsonElement>();
        var modeVersionId = detail.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("kind").GetString() == "mode")
            .Select(resource => resource.GetProperty("version_id").GetGuid())
            .Single();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "vibe free conversation", mode_version_id = modeVersionId });
        Assert.True(sessionResponse.StatusCode == HttpStatusCode.Created, $"session: {sessionResponse.StatusCode} {await sessionResponse.Content.ReadAsStringAsync()}");
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        // Pack publication derives node keys and agent slugs (node-NN-<key>); the
        // identity freeze stores the published conversation node of the mode.
        Assert.Contains("meeting", session.GetProperty("conversation_node_key").GetString());
        Assert.Contains("meeting", session.GetProperty("conversation_template_slug").GetString());

        // The declared graph is the orchestration base layer even without any run.
        var sessionId = session.GetProperty("id").GetGuid();
        var orchestration = await (await client.GetAsync($"/api/v1/sessions/{sessionId}/orchestration"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var graph = orchestration.GetProperty("graph");
        Assert.Equal(JsonValueKind.Object, graph.ValueKind);
        Assert.Equal(3, graph.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, graph.GetProperty("edges").GetArrayLength());
        var meetingNode = graph.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("node_key").GetString() == "meeting");
        Assert.True(meetingNode.GetProperty("is_conversation").GetBoolean());
    }

    [Fact]
    public async Task ToolsSection_WithoutCapabilityDeclaration_IsRejected_FailClosed()
    {
        using var factory = new VibePackFactory();
        using var client = factory.CreateClient();
        var envelope = VibeEnvelope(mutate: manifest =>
        {
            // New-field surface without the gate declaration: an older Core must
            // reject this fail-closed, so this Core does too (option (c) discipline).
            manifest["compatibility"]!["required_core_capabilities"] = new JsonArray();
            manifest["resources"]!["tools"] = new JsonArray
            {
                new JsonObject { ["tool_id"] = "read_file", ["kind"] = "reference", ["entry_hash"] = new string('a', 64) }
            };
        });

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(previewResponse.StatusCode == HttpStatusCode.BadRequest, $"preview: {previewResponse.StatusCode} {await previewResponse.Content.ReadAsStringAsync()}");
        var problem = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("graph_mode_packs", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ToolsReference_WithMalformedPin_IsRejected()
    {
        using var factory = new VibePackFactory();
        using var client = factory.CreateClient();
        var envelope = VibeEnvelope(mutate: manifest =>
        {
            manifest["resources"]!["tools"] = new JsonArray
            {
                new JsonObject { ["tool_id"] = "read_file", ["kind"] = "reference", ["entry_hash"] = "tooshort" }
            };
        });

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(previewResponse.StatusCode == HttpStatusCode.BadRequest, $"preview: {previewResponse.StatusCode} {await previewResponse.Content.ReadAsStringAsync()}");
        var problem = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("entry_hash", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RelationshipFile_MissingField_IsRejected()
    {
        using var factory = new VibePackFactory();
        using var client = factory.CreateClient();
        var envelope = VibeEnvelope(mutate: manifest =>
        {
            var mode = manifest["resources"]!["modes"]!.AsArray()[0]!.AsObject();
            var meetingNode = mode["nodes"]!.AsArray().First(node => node!["node_key"]!.GetValue<string>() == "meeting")!.AsObject();
            meetingNode["relationship"] = new JsonObject
            {
                ["duty"] = "orchestrate",
                ["inputs_outputs"] = new JsonObject(),
                ["allowed_dispatch_targets"] = new JsonArray(),
                // success_criteria missing → incomplete file is rejected
                ["agent_types"] = new JsonArray()
            };
        });

        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(previewResponse.StatusCode == HttpStatusCode.BadRequest, $"preview: {previewResponse.StatusCode} {await previewResponse.Content.ReadAsStringAsync()}");
        var problem = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("success_criteria", problem.GetProperty("detail").GetString());
    }

    private static JsonElement VibeEnvelope(string? version = null, Action<JsonObject>? mutate = null)
    {
        var relationship = new JsonObject
        {
            ["duty"] = "Understand intent, split tasks, dispatch inside the envelope.",
            ["inputs_outputs"] = new JsonObject { ["receives"] = "user goal", ["returns"] = "final answer" },
            ["allowed_dispatch_targets"] = new JsonArray("worker.search", "worker.global_engineering"),
            ["success_criteria"] = new JsonArray("every dispatched task declares its edge data contract"),
            ["agent_types"] = new JsonArray("orchestrator")
        };
        var manifest = new JsonObject
        {
            ["api_version"] = "tinadec.io/agent-pack/v1alpha1",
            ["kind"] = "AgentPack",
            ["metadata"] = new JsonObject
            {
                ["pack_id"] = PackId,
                ["owner"] = "tinadec",
                ["product_id"] = "tinadec-core",
                ["name"] = "Vibe Graph Pack",
                ["version"] = version ?? "1.0.0"
            },
            ["compatibility"] = new JsonObject
            {
                ["required_core_capabilities"] = new JsonArray("graph_mode_packs")
            },
            ["resources"] = new JsonObject
            {
                ["agents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["resource_key"] = "meeting",
                        ["slug"] = "meeting",
                        ["display_name"] = "Meeting",
                        ["layer"] = "operation",
                        ["role"] = "session_coordinator",
                        ["capabilities"] = new JsonArray("user.respond", "task.dispatch", "agent.create_temporary"),
                        ["model_strategy"] = new JsonObject { ["kind"] = "inherit" },
                        ["tool_scope"] = new JsonArray(),
                        ["system_prompt"] = "You are the conversation identity.",
                        ["enabled"] = true,
                        ["base_prompt_pipeline_ref"] = "prompt:vibe-base"
                    },
                    new JsonObject
                    {
                        ["resource_key"] = "worker.search",
                        ["slug"] = "worker.search",
                        ["display_name"] = "Search",
                        ["layer"] = "execution",
                        ["role"] = "task_executor",
                        ["capabilities"] = new JsonArray("task.dispatch"),
                        ["model_strategy"] = new JsonObject { ["kind"] = "inherit" },
                        ["tool_scope"] = new JsonArray("mcp_search", "mcp_invoke", "read_file"),
                        ["system_prompt"] = "You gather evidence.",
                        ["enabled"] = true,
                        ["base_prompt_pipeline_ref"] = "prompt:vibe-base"
                    },
                    new JsonObject
                    {
                        ["resource_key"] = "worker.global_engineering",
                        ["slug"] = "worker.global_engineering",
                        ["display_name"] = "Global Engineering",
                        ["layer"] = "execution",
                        ["role"] = "task_executor",
                        ["capabilities"] = new JsonArray("task.dispatch"),
                        ["model_strategy"] = new JsonObject { ["kind"] = "inherit" },
                        ["tool_scope"] = new JsonArray("read_file", "write_file", "shell"),
                        ["system_prompt"] = "You engineer end to end.",
                        ["enabled"] = true,
                        ["base_prompt_pipeline_ref"] = "prompt:vibe-base"
                    }
                },
                ["prompt_pipelines"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["resource_key"] = "vibe-base",
                        ["slug"] = "vibe-base",
                        ["display_name"] = "Vibe base pipeline",
                        ["graph"] = new JsonObject
                        {
                            ["nodes"] = new JsonArray
                            {
                                new JsonObject { ["id"] = "template", ["type"] = "template", ["config"] = new JsonObject { ["content"] = "Vibe graph collaboration baseline." } },
                                new JsonObject { ["id"] = "assemble", ["type"] = "assemble" }
                            },
                            ["edges"] = new JsonArray { new JsonObject { ["source"] = "template", ["target"] = "assemble" } }
                        }
                    }
                },
                ["modes"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["resource_key"] = "vibe-mode",
                        ["slug"] = "vibe-mode",
                        ["display_name"] = "Vibe mode",
                        ["description"] = "Three-node conversation graph",
                        ["nodes"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["node_key"] = "meeting",
                                ["agent_ref"] = "agent:meeting",
                                ["layer"] = "operation",
                                ["label"] = "Meeting",
                                ["config"] = new JsonObject { ["conversation"] = true },
                                ["relationship"] = relationship,
                                ["position"] = null
                            },
                            new JsonObject
                            {
                                ["node_key"] = "worker.search",
                                ["agent_ref"] = "agent:worker.search",
                                ["layer"] = "execution",
                                ["label"] = "Search",
                                ["config"] = new JsonObject(),
                                ["position"] = null
                            },
                            new JsonObject
                            {
                                ["node_key"] = "worker.global_engineering",
                                ["agent_ref"] = "agent:worker.global_engineering",
                                ["layer"] = "execution",
                                ["label"] = "Engineering",
                                ["config"] = new JsonObject(),
                                ["position"] = null
                            }
                        },
                        ["edges"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["edge_key"] = "meeting-to-search",
                                ["source_node_key"] = "meeting",
                                ["target_node_key"] = "worker.search",
                                ["condition"] = new JsonObject { ["request"] = new JsonArray("query", "constraints"), ["response"] = new JsonArray("evidence", "sources") }
                            },
                            new JsonObject
                            {
                                ["edge_key"] = "meeting-to-engineering",
                                ["source_node_key"] = "meeting",
                                ["target_node_key"] = "worker.global_engineering",
                                ["condition"] = new JsonObject { ["request"] = new JsonArray("task", "success_criteria", "scope"), ["response"] = new JsonArray("artifact", "verification", "risks") }
                            }
                        },
                        ["bindings"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["node_key"] = "meeting",
                                ["agent_ref"] = "agent:meeting",
                                ["duty_description_ref"] = "vibe://relationship/meeting",
                                ["tool_switches"] = new JsonObject(),
                                ["envelope"] = new JsonObject { ["spawn"] = new JsonObject { ["max_depth"] = 1 } },
                                ["includes_core_reserved"] = false
                            }
                        },
                        ["canvas_layout"] = new JsonObject()
                    }
                }
            },
            ["activation"] = new JsonObject
            {
                ["workspace_defaults"] = new JsonObject
                {
                    ["agent_ref"] = "agent:meeting",
                    ["mode_ref"] = "mode:vibe-mode",
                    ["prompt_pipeline_ref"] = "prompt:vibe-base"
                }
            }
        };
        mutate?.Invoke(manifest);
        var manifestElement = JsonSerializer.SerializeToElement(manifest);
        // Core computes the digest over its DTO re-serialization (unknown members
        // dropped, optional members omitted when absent), so the fixture must digest
        // the same round-tripped shape — not the raw submitted bytes.
        var serverOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var roundTripped = JsonSerializer.Deserialize<TinadecCore.Contracts.Dtos.AgentPackManifestDto>(
            manifestElement.GetRawText(), serverOptions);
        var serverElement = JsonSerializer.SerializeToElement(roundTripped, serverOptions);
        var digest = Convert.ToHexString(SHA256.HashData(Canonicalize(serverElement))).ToLowerInvariant();
        return JsonSerializer.SerializeToElement(new
        {
            manifest = manifestElement,
            integrity = new { algorithm = "sha256", digest }
        });
    }

    private static byte[] Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
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
                writer.WriteNullValue();
                break;
        }
    }
}

/// <summary>Isolated host for the vibe pack round (own database and workspace).</summary>
public sealed class VibePackFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-vibe-pack-tests", Guid.NewGuid().ToString("N"));

    public string WorkspacePath => Path.Combine(_root, "workspace");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(WorkspacePath);
        builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
