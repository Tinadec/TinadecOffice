using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Full-duplex runtime endpoint tests. A scripted fake chat client drives the whole
/// pipeline through HTTP: admission, background execution, supervision loop, streaming
/// chunks, run control, mode catalogs, context versions, lineage, and candidate review.
/// </summary>
public sealed class FullDuplexEndpointTests : IAsyncLifetime
{
    private const string TestAgentPackId = "tinadec.tests.runtime-agent-pack";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-fullduplex-api-tests", Guid.NewGuid().ToString("N"));
    private FullDuplexFactory? _factory;
    private bool _agentPackInstalled;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private FullDuplexFactory CreateFactory(
        ScriptedChatClient? client = null,
        bool available = true,
        IPromptAssembler? promptAssembler = null,
        string? runtimeToml = null)
    {
        _factory?.Dispose();
        _factory = new FullDuplexFactory(_root, client ?? new ScriptedChatClient(), available, promptAssembler, runtimeToml);
        return _factory;
    }

    private async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        await EnsureRuntimeAgentPackInstalledAsync(client);
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "FD project", path = Path.Combine(_root, "workspace") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "FD session" })).Content.ReadFromJsonAsync<JsonElement>();
        return session.GetProperty("id").GetGuid();
    }

    private async Task EnsureRuntimeAgentPackInstalledAsync(HttpClient client)
    {
        if (_agentPackInstalled) return;
        var manifest = TestAgentPackManifest();
        var envelope = JsonSerializer.SerializeToElement(new
        {
            manifest,
            integrity = new { algorithm = "sha256", digest = ComputeCanonicalDigest(manifest) }
        });
        var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("install", preview.GetProperty("action").GetString());

        using var apply = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-packs/{TestAgentPackId}")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", "full-duplex-test-pack-install");
        using var applyResponse = await client.SendAsync(apply);
        Assert.Equal(HttpStatusCode.Created, applyResponse.StatusCode);
        var applied = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("installed", applied.GetProperty("status").GetString());
        _agentPackInstalled = true;
    }

    private static JsonElement TestAgentPackManifest()
    {
        static object Agent(
            string key,
            string layer,
            string role,
            string[] capabilities,
            string[] tools,
            string prompt) => new
        {
            resource_key = key,
            slug = key,
            display_name = key,
            description = key,
            layer,
            role,
            capabilities,
            model_strategy = new { kind = "inherit" },
            tool_scope = tools,
            system_prompt = prompt,
            enabled = true,
            base_prompt_pipeline_ref = "prompt:baseline-prompt"
        };
        static object Node(string key, string agent, string layer) => new
        {
            node_key = key,
            agent_ref = $"agent:{agent}",
            layer,
            label = agent,
            config = new { },
            position = (object?)null
        };

        return JsonSerializer.SerializeToElement(new
        {
            api_version = "tinadec.io/agent-pack/v1alpha1",
            kind = "AgentPack",
            metadata = new
            {
                pack_id = TestAgentPackId,
                owner = "tinadec.tests",
                product_id = "tinadec.tests",
                name = "Runtime Test Agent Pack",
                version = "0.1.0"
            },
            compatibility = new
            {
                minimum_core_version = "0.1.0",
                required_core_capabilities = Array.Empty<string>()
            },
            resources = new
            {
                agents = new[]
                {
                    Agent("meeting", "operation", "session_coordinator", ["task.dispatch", "user.respond"], ["*"], "meeting-system"),
                    Agent("context_compressor", "operation", "context_maintenance", ["context.patch"], ["*"], "context-system"),
                    Agent("skill_recommender", "operation", "capability_advisor", ["tool.search"], ["*"], "skill-system"),
                    Agent("supervisor", "operation", "quality_controller", ["supervision.review"], ["*"], "supervisor-system"),
                    Agent("evolution", "operation", "experience_curator", ["agent.candidate"], ["*"], "evolution-system"),
                    Agent("git_steward", "operation", "git_steward", ["git.review"], Array.Empty<string>(), "git-steward-system"),
                    Agent("task_planner", "execution", "execution_coordinator", ["agent.create_temporary", "task.plan"], ["*"], "planner-system"),
                    Agent("worker.general", "execution", "task_executor", ["task.execute"], ["*"], "worker-system")
                },
                prompt_pipelines = new[]
                {
                    new
                    {
                        resource_key = "baseline-prompt",
                        slug = "baseline-prompt",
                        display_name = "Baseline Prompt",
                        description = "Runtime test prompt.",
                        graph = new
                        {
                            nodes = new object[]
                            {
                                new { id = "template", type = "template", config = new { content = "runtime-test-template" } },
                                new { id = "assemble", type = "assemble" }
                            },
                            edges = new[] { new { source = "template", target = "assemble" } }
                        }
                    }
                },
                modes = new[]
                {
                    new
                    {
                        resource_key = "default-mode",
                        slug = "default-mode",
                        display_name = "Default Mode",
                        description = "Runtime test mode.",
                        nodes = new[]
                        {
                            Node("executor-1", "task_planner", "execution"),
                            Node("executor-2", "worker.general", "execution"),
                            Node("meeting-1", "meeting", "operation"),
                            Node("meeting-2", "context_compressor", "operation"),
                            Node("meeting-3", "skill_recommender", "operation"),
                            Node("meeting-4", "supervisor", "operation"),
                            Node("meeting-5", "evolution", "operation"),
                            Node("meeting-6", "git_steward", "operation")
                        },
                        edges = Array.Empty<object>(),
                        canvas_layout = new { }
                    }
                }
            },
            activation = new
            {
                workspace_defaults = new
                {
                    agent_ref = "agent:meeting",
                    mode_ref = "mode:default-mode",
                    prompt_pipeline_ref = "prompt:baseline-prompt"
                }
            }
        });
    }

    private static string ComputeCanonicalDigest(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, value);
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray())).ToLowerInvariant();
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
                throw new InvalidDataException("Test agent pack contains an unsupported JSON value.");
        }
    }

    private static async Task<List<JsonElement>> StreamInvokeAsync(HttpClient client, Guid sessionId, object body)
    {
        var chunks = new List<JsonElement>();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/invoke-stream") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var builder = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    chunks.Add(JsonSerializer.Deserialize<JsonElement>(builder.ToString()));
                    builder.Clear();
                }
                continue;
            }
            if (line.StartsWith("data: ", StringComparison.Ordinal)) builder.Append(line[6..]);
        }
        return chunks;
    }

    private sealed record ActiveInvoke(Task<JsonElement> Acknowledgement, Task<List<JsonElement>> Completion);

    /// <summary>
    /// Starts an invoke stream without waiting for its terminal chunk. This lets an
    /// interaction test send a meeting turn while the durable target run is blocked
    /// inside the scripted worker.
    /// </summary>
    private static ActiveInvoke StartStreamingInvoke(HttpClient client, Guid sessionId, object body)
    {
        var acknowledgement = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
            try
            {
                var chunks = new List<JsonElement>();
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/invoke-stream")
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Assert.Fail($"Invoke stream returned {(int)response.StatusCode} {response.StatusCode}: {errorBody}");
                }
                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                var builder = new StringBuilder();
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (builder.Length > 0)
                        {
                            var chunk = JsonSerializer.Deserialize<JsonElement>(builder.ToString());
                            chunks.Add(chunk);
                            if (KindOf(chunk) == "ack") acknowledgement.TrySetResult(chunk);
                            builder.Clear();
                        }
                        continue;
                    }
                    if (line.StartsWith("data: ", StringComparison.Ordinal)) builder.Append(line[6..]);
                }
                if (!acknowledgement.Task.IsCompleted)
                {
                    acknowledgement.TrySetException(new InvalidDataException("Invoke stream closed before its acknowledgement."));
                }
                return chunks;
            }
            catch (Exception ex)
            {
                acknowledgement.TrySetException(ex);
                throw;
            }
        });
        return new ActiveInvoke(acknowledgement.Task, completion);
    }

    private static async Task<List<JsonElement>> StreamRunAsync(HttpClient client, Guid runId, Guid turnId)
    {
        var chunks = new List<JsonElement>();
        using var response = await client.GetAsync($"/api/v1/runs/{runId}/stream?turn_id={turnId}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var builder = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    chunks.Add(JsonSerializer.Deserialize<JsonElement>(builder.ToString()));
                    builder.Clear();
                }
                continue;
            }
            if (line.StartsWith("data: ", StringComparison.Ordinal)) builder.Append(line[6..]);
        }
        return chunks;
    }

    private static async Task<long> GetCurrentContextRevisionAsync(HttpClient client, Guid sessionId)
    {
        var versions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions");
        var materialized = Assert.IsType<JsonElement[]>(versions);
        Assert.NotEmpty(materialized);
        return materialized.Max(version => version.GetProperty("revision").GetInt64());
    }

    private static Guid RunIdOf(JsonElement chunk) => chunk.GetProperty("run_id").GetGuid();
    private static string KindOf(JsonElement chunk) => chunk.GetProperty("kind").GetString()!;

    [Fact]
    public async Task InvokeStream_HappyPath_StreamsAckDeltasDoneAndPersistsSingleAssistantMessage()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("已完成任务")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("全部完成。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "做两个任务", client_message_id = "c-1" });

        var runId = RunIdOf(chunks[0]);
        Assert.Equal("ack", KindOf(chunks[0]));
        Assert.Contains(chunks, c => KindOf(c) == "delta" && c.GetProperty("delta").GetString() == "全部完成。");
        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        // Exactly one user and one assistant message.
        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        Assert.Equal(2, messages!.Length);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("全部完成。", messages[1].GetProperty("content").GetString());

        // Run terminal state and lineage.
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage");
        Assert.Contains(lineage!, a => a.GetProperty("layer").GetString() == "operation");
        Assert.Contains(lineage!, a => a.GetProperty("layer").GetString() == "execution" && a.GetProperty("generated").GetBoolean());
        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
        Assert.True(orchestration.GetProperty("supervision_findings").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task InvokeStream_FailsClosedWithWorkerUnavailableWhenFrozenRosterCannotCoverTask()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("""[{"task_key":"slides","title":"Create slides","description":"","success_criteria":["done"],"dependencies":[],"required_capabilities":["tool.presentation"],"required_tools":[],"priority":1,"risk":"low"}]""");
        var client = CreateFactory(script).CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "Create slides", client_message_id = "worker-unavailable" });

        var error = chunks.Last(chunk => KindOf(chunk) is "done" or "error");
        Assert.Equal("error", KindOf(error));
        Assert.Equal("worker_unavailable", error.GetProperty("error_category").GetString());
        Assert.Contains("No frozen execution specialist", error.GetProperty("safe_error_message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStream_InjectsFrozenPromptIntoEveryRunnableRoleWithoutStartingDormantRolesWhenTriggersDisabled()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"task-1\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("已完成任务")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("全部完成。");
        var prompts = new RecordingPromptAssembler();
        var factory = CreateFactory(script, promptAssembler: prompts, runtimeToml: RuntimeTomlWith("enabled = false\n"));
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行任务", client_message_id = "frozen-prompts" });

        Assert.Equal("done", KindOf(chunks.Last(chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(new[] { "meeting", "supervisor", "task_planner", "worker.general" },
            prompts.AgentIds.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        foreach (var agentId in prompts.AgentIds)
        {
            Assert.Contains(script.Instructions, instructions =>
                instructions.Contains($"frozen-prompt:{agentId}", StringComparison.Ordinal));
        }

        var runId = RunIdOf(chunks[0]);
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage");
        Assert.Equal(4, lineage!.Length);
        Assert.DoesNotContain(lineage, instance => instance.GetProperty("role").GetString() is
            "context_maintenance" or "capability_advisor" or "experience_curator" or "git_steward");

        var session = await factory.Services.GetRequiredService<ISessionLocator>().FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.True(session.ModeVersionId.HasValue);
        var modeVersionId = session.ModeVersionId.Value;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>()
            .CreateDbContextAsync();
        var modeVersion = await db.ModeVersions.AsNoTracking().SingleAsync(version => version.Id == modeVersionId);
        using var modeDocument = JsonDocument.Parse(modeVersion.SnapshotJson!);
        var versionIds = modeDocument.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("agent_version_id").GetGuid())
            .ToArray();
        var versions = await db.AgentVersions.AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .ToListAsync();
        var expectedBySlug = versions.ToDictionary(
            version => JsonDocument.Parse(version.SnapshotJson).RootElement.GetProperty("slug").GetString()!,
            version => version,
            StringComparer.Ordinal);
        var instances = await factory.Services.GetRequiredService<IAgentInstanceService>().ListByRunAsync(runId);
        AssertVersionBinding(Assert.Single(instances, instance => instance.Generated), expectedBySlug["worker.general"]);
        AssertVersionBinding(Assert.Single(instances, instance => instance.Role == "quality_controller"), expectedBySlug["supervisor"]);
    }

    [Fact]
    public async Task OperationalTriggers_ActivateBypassRolesWithoutCreatingLineageInstances()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"task-1\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("已完成任务")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("全部完成。")
            .WhenRecommender("{\"recommendations\":[{\"skill\":\"worker.general\",\"reason\":\"通用执行即可覆盖\",\"confidence\":\"high\"}]}");
        var prompts = new RecordingPromptAssembler();
        // Default trigger policy ships enabled; the capability advisor should fire
        // after planning while compression stays below threshold.
        var factory = CreateFactory(script, promptAssembler: prompts);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行任务", client_message_id = "bypass-roles" });
        Assert.Equal("done", KindOf(chunks.Last(chunk => KindOf(chunk) is "done" or "error")));

        Assert.True(script.RecommenderCalls >= 1, "The capability advisor should be triggered after the task graph is created.");
        Assert.Contains("skill_recommender", prompts.AgentIds);

        var runId = RunIdOf(chunks[0]);
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage");
        // Bypass operational calls never enter the task graph or lineage.
        Assert.DoesNotContain(lineage!, instance => instance.GetProperty("role").GetString() is
            "context_maintenance" or "capability_advisor" or "experience_curator" or "git_steward");
        var deltas = string.Concat(chunks.Where(chunk => KindOf(chunk) == "delta").Select(chunk => chunk.GetProperty("delta").GetString()));
        Assert.Equal("全部完成。", deltas);
    }

    private static void AssertVersionBinding(RuntimeAgentInstance instance, AgentVersionRecord expected)
    {
        Assert.Equal(expected.AgentDefinitionId, instance.AgentDefinitionId);
        Assert.Equal(expected.Id, instance.AgentVersionId);
        Assert.Equal(expected.ContentHash, instance.AgentVersionContentHash);
    }

    [Fact]
    public async Task InvokeStream_DurableDoneAggregatesUsageAcrossAllModelRoles()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("已完成任务")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("全部完成。")
            .WithUsage(inputTokens: 1, outputTokens: 2);
        var client = CreateFactory(script).CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行任务", client_message_id = "usage-1" });

        var done = chunks.Last(chunk => KindOf(chunk) == "done");
        var usage = done.GetProperty("usage");
        // planner + capability advisor + worker + supervisor + meeting. The experience
        // curator runs after the terminal done chunk (post-run bypass), so its usage
        // is attributed in model_invocations, not in this aggregate.
        Assert.Equal(5, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(10, usage.GetProperty("output_tokens").GetInt64());
        Assert.Equal(15, usage.GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public async Task InvokeStream_SameClientMessageId_IsIdempotent()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好的。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var first = await StreamInvokeAsync(client, sessionId, new { content = "目标", client_message_id = "dup-1" });
        var second = await StreamInvokeAsync(client, sessionId, new { content = "目标", client_message_id = "dup-1" });

        Assert.Equal(RunIdOf(first[0]), RunIdOf(second[0]));
        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        Assert.Equal(2, messages!.Length);
    }

    [Fact]
    public async Task InvokeStream_StaleContextRevision_Returns409()
    {
        var factory = CreateFactory();
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/invoke-stream", new { content = "目标", expected_context_revision = 999 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONTEXT_REVISION_CONFLICT", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MeetingSupplement_MatchingRevisionAppliesAndStaleRevisionReturnsConflict()
    {
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已吸收补充。" );
        script.BeforeWorker = workerGate.Task;
        script.WorkerStarted = workerStarted;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var initial = StartStreamingInvoke(client, sessionId, new { content = "初始目标", client_message_id = "meeting-supplement-target" });
        var acknowledgement = await initial.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(15));
        var runId = RunIdOf(acknowledgement);

        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var baseContextRevision = await GetCurrentContextRevisionAsync(client, sessionId);
        List<JsonElement>? initialChunks = null;
        try
        {
            var applied = await StreamInvokeAsync(client, sessionId, new
            {
                content = "补充：必须满足额外约束",
                client_message_id = "meeting-supplement-applied",
                target_run_id = runId,
                expected_context_revision = baseContextRevision
            });
            Assert.Equal(runId, RunIdOf(applied[0]));
            Assert.Equal("ack", KindOf(applied[0]));
            Assert.Equal("context_applied", applied.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

            var stale = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/invoke-stream", new
            {
                content = "补充：这条基于过期版本",
                client_message_id = "meeting-supplement-stale",
                target_run_id = runId,
                expected_context_revision = baseContextRevision
            });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var staleBody = await stale.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CONTEXT_REVISION_CONFLICT", staleBody.GetProperty("code").GetString());
        }
        finally
        {
            workerGate.TrySetResult();
            initialChunks = await initial.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.Equal("completed", initialChunks!.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
        Assert.Equal(2, script.WorkerCalls);

        var contextVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions?run_id={runId}");
        var appliedPatch = Assert.Single(contextVersions!, version => version.GetProperty("kind").GetString() == "supplement"
            && version.GetProperty("status").GetString() == "applied");
        Assert.True(appliedPatch.GetProperty("revision").GetInt64() > baseContextRevision);
        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal(appliedPatch.GetProperty("revision").GetInt64(), orchestration.GetProperty("run").GetProperty("context_revision").GetInt64());
    }

    [Fact]
    public async Task MeetingGoalAdjustment_AppliesPatchAndReplansTheActiveRun()
    {
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已按新目标完成。" );
        script.BeforeWorker = workerGate.Task;
        script.WorkerStarted = workerStarted;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var initial = StartStreamingInvoke(client, sessionId, new { content = "旧目标", client_message_id = "meeting-goal-target" });
        var acknowledgement = await initial.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(15));
        var runId = RunIdOf(acknowledgement);

        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var baseContextRevision = await GetCurrentContextRevisionAsync(client, sessionId);
        List<JsonElement>? initialChunks = null;
        try
        {
            var adjustment = await StreamInvokeAsync(client, sessionId, new
            {
                content = "调整目标：改为完成新的交付物",
                client_message_id = "meeting-goal-adjustment",
                target_run_id = runId,
                expected_context_revision = baseContextRevision
            });
            Assert.Equal(runId, RunIdOf(adjustment[0]));
            Assert.Equal("context_applied", adjustment.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
        }
        finally
        {
            workerGate.TrySetResult();
            initialChunks = await initial.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.Equal("completed", initialChunks!.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
        Assert.Equal(2, script.PlannerCalls);
        Assert.Equal(2, script.WorkerCalls);

        var contextVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions?run_id={runId}");
        var appliedPatch = Assert.Single(contextVersions!, version => version.GetProperty("kind").GetString() == "goal_adjustment"
            && version.GetProperty("status").GetString() == "applied");
        Assert.True(appliedPatch.GetProperty("revision").GetInt64() > baseContextRevision);
        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal(appliedPatch.GetProperty("revision").GetInt64(), orchestration.GetProperty("run").GetProperty("context_revision").GetInt64());
    }

    [Fact]
    public async Task MeetingStatusQuery_UsesAnIndependentRunAndDurableStream()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("初始任务完成。" );
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var targetChunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "meeting-status-target" });
        var targetRunId = RunIdOf(targetChunks[0]);
        Assert.Equal("completed", targetChunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var statusChunks = await StreamInvokeAsync(client, sessionId, new
        {
            content = "当前状态和进度？",
            client_message_id = "meeting-status-query",
            target_run_id = targetRunId
        });
        var statusRunId = RunIdOf(statusChunks[0]);
        var statusTurnId = statusChunks[0].GetProperty("turn_id").GetGuid();
        Assert.NotEqual(targetRunId, statusRunId);
        Assert.Contains(statusChunks, chunk => KindOf(chunk) == "delta"
            && chunk.GetProperty("delta").GetString()!.Contains(targetRunId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("completed", statusChunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var replay = await StreamRunAsync(client, statusRunId, statusTurnId);
        Assert.Equal(new[] { "ack", "delta", "done" }, replay.Select(KindOf).ToArray());
        Assert.All(replay, chunk => Assert.Equal(statusRunId, RunIdOf(chunk)));

        var runs = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/runs");
        Assert.Equal(2, runs!.Length);
        Assert.Contains(runs, run => run.GetProperty("id").GetGuid() == targetRunId);
        Assert.Contains(runs, run => run.GetProperty("id").GetGuid() == statusRunId);
    }

    [Fact]
    public async Task MeetingClarification_UsesMeetingModelResponseInsteadOfFixedText()
    {
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好的，我会继续跟进这个约束。");
        script.BeforeWorker = workerGate.Task;
        script.WorkerStarted = workerStarted;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var target = StartStreamingInvoke(client, sessionId, new { content = "执行目标", client_message_id = "meeting-clarify-target" });
        var acknowledgement = await target.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var targetRunId = RunIdOf(acknowledgement);
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        List<JsonElement>? targetChunks = null;
        try
        {
            var clarification = await StreamInvokeAsync(client, sessionId, new
            {
                content = "你觉得这个方案可靠吗？",
                client_message_id = "meeting-clarify-turn",
                target_run_id = targetRunId
            });
            var clarificationRunId = RunIdOf(clarification[0]);
            Assert.NotEqual(targetRunId, clarificationRunId);
            var deltas = string.Concat(clarification.Where(chunk => KindOf(chunk) == "delta").Select(chunk => chunk.GetProperty("delta").GetString()));
            Assert.Contains("我会继续跟进这个约束", deltas, StringComparison.Ordinal);
            Assert.Equal("completed", clarification.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

            var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
            Assert.Contains(messages!, message => message.GetProperty("role").GetString() == "assistant"
                && message.GetProperty("content").GetString()!.Contains("我会继续跟进这个约束", StringComparison.Ordinal));
        }
        finally
        {
            workerGate.TrySetResult();
            targetChunks = await target.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal("completed", targetChunks!.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task OperationalCompression_BelowThreshold_SkipsModelCall()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。")
            .WhenCompressor("不应被调用。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "compression-below" });
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        // The shipped baseline threshold (6000 tokens) is far above this short
        // session, so the compressor must stay dormant.
        Assert.Equal(0, script.CompressorCalls);
        var contextVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions");
        Assert.DoesNotContain(contextVersions!, version => version.GetProperty("kind").GetString() == "compaction");
    }

    /// <summary>
    /// Builds a complete runtime baseline TOML with a custom [triggers] section.
    /// All other policy sections mirror the shipped default-agent-runtime.toml.
    /// </summary>
    private static string RuntimeTomlWith(string triggersSection) =>
        "schema_version = 1\n\n"
        + "[spawn]\nmax_depth = 2\nmax_agents_per_run = 16\nmax_parallel_workers = 4\n\n"
        + "[scheduling]\nmax_active_runs_per_session = 2\nworker_retry_limit = 2\npreserve_partial_results = true\n\n"
        + "[supervision]\nrequired_before_final = true\nmax_revision_rounds = 2\n\n"
        + "[context]\ndefault_token_budget = 8192\nrecent_message_limit = 24\noptimistic_revision = true\n\n"
        + "[memory]\ncandidate_only = true\nretrieval_limit = 8\n"
        + "allowed_scopes = [\"principal\", \"workspace\", \"project\", \"agent\"]\n"
        + "allowed_kinds = [\"fact\", \"preference\", \"decision\", \"success_pattern\", \"failure_pattern\", \"task_template\", \"supervision_rule\"]\n\n"
        + "[tools]\nprovider = \"tinadec-tools-process\"\nmutation_requires_approval = true\nserialize_workspace_writes = true\ndefault_timeout_seconds = 120\nmax_tool_rounds = 4\n\n"
        + "[triggers]\n" + triggersSection;

    [Fact]
    public async Task OperationalCompression_AboveThreshold_AppliesCompactionPatch()
    {
        var runtimeToml = RuntimeTomlWith(
            "enabled = true\ncontext_token_threshold = 1\ncompress_on_task_closed = true\nrecommend_on_task_created = false\ncurate_on_run_closed = false\ngit_steward_on_run_closed = false\n");
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。")
            .WhenCompressor("当前目标：执行目标。\n关键约束：无。\n已完成事项：任务A。");
        var factory = CreateFactory(script, runtimeToml: runtimeToml);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "compression-above" });
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        Assert.True(script.CompressorCalls >= 1, "The compressor should run at task close and/or run finalization.");
        var contextVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions");
        var compactions = contextVersions!.Where(version => version.GetProperty("kind").GetString() == "compaction").ToArray();
        Assert.NotEmpty(compactions);
        Assert.All(compactions, compaction => Assert.Equal("applied", compaction.GetProperty("status").GetString()));
        Assert.All(compactions, compaction => Assert.True(compaction.GetProperty("revision").GetInt64() > 0));
    }

    [Fact]
    public async Task OperationalSkillRecommendation_FeedsRecommendationsIntoReplanning()
    {
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("结果")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已完成。")
            .WhenRecommender("{\"recommendations\":[{\"skill\":\"worker.browser\",\"reason\":\"目标涉及网页信息\",\"confidence\":\"high\"}]}");
        script.BeforeWorker = workerGate.Task;
        script.WorkerStarted = workerStarted;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var initial = StartStreamingInvoke(client, sessionId, new { content = "调研并汇总资料", client_message_id = "skill-recommendation-target" });
        var acknowledgement = await initial.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var runId = RunIdOf(acknowledgement);
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var baseContextRevision = await GetCurrentContextRevisionAsync(client, sessionId);

        List<JsonElement>? initialChunks = null;
        try
        {
            var adjustment = await StreamInvokeAsync(client, sessionId, new
            {
                content = "调整目标：改为汇总网页资料",
                client_message_id = "skill-recommendation-adjust",
                target_run_id = runId,
                expected_context_revision = baseContextRevision
            });
            Assert.Equal("context_applied", adjustment.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
        }
        finally
        {
            workerGate.TrySetResult();
            initialChunks = await initial.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal("completed", initialChunks!.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
        Assert.True(script.RecommenderCalls >= 1, "The capability advisor should run after the task graph is created.");
        Assert.Equal(2, script.PlannerCalls);
        // The goal adjustment forces a replanning round, which must carry the
        // advisory recommendations captured after the first planning round.
        Assert.Contains(script.Instructions, instructions =>
            instructions.Contains("Capability recommendations from the operation layer", StringComparison.Ordinal)
            && instructions.Contains("worker.browser", StringComparison.Ordinal));
    }

    private const string CuratorCandidatesJson = """
        {"memory_candidates":[{"scope":"workspace","kind":"success_pattern","content":"先规划再分派执行体可稳定完成写入任务","confidence":0.9,"applicability":"写入类任务"}],"agent_candidates":[{"name":"日志巡检执行体","layer":"execution","agent_type":"task_executor","confidence":0.8,"proposal":{"slug":"worker.log-review","display_name":"日志巡检执行体","layer":"execution","role":"task_executor","system_prompt":"你负责读取并归纳日志输出。","description":"从成功运行沉淀"}}]}
        """;

    private static ScriptedChatClient CuratedRunScript(string curatorJson) => new ScriptedChatClient()
        .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
        .WhenWorker("完成")
        .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
        .WhenMeeting("完成。")
        .WhenCurator(curatorJson);

    /// <summary>
    /// The curator runs after the terminal done chunk is durable, so post-run
    /// candidate assertions poll instead of racing the bypass dispatch.
    /// </summary>
    private static async Task<JsonElement[]> PollAgentCandidatesAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        JsonElement[] candidates = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            candidates = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-candidates?status=proposed");
            if (candidates!.Length > 0) return candidates;
            await Task.Delay(100);
        }
        return candidates;
    }

    [Fact]
    public async Task OperationalEvolution_CuratesMemoryAndAgentCandidatesOnRunClose()
    {
        var script = CuratedRunScript(CuratorCandidatesJson);
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "evolution-curate" });
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
        Assert.True(script.CuratorCalls >= 1, "The experience curator should run at close.");

        var agentCandidates = await PollAgentCandidatesAsync(client, TimeSpan.FromSeconds(15));
        var candidate = Assert.Single(agentCandidates);
        Assert.Equal("日志巡检执行体", candidate.GetProperty("name").GetString());
        Assert.Equal("execution", candidate.GetProperty("layer").GetString());

        var memoryCandidates = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/memory-candidates?status=proposed");
        var memory = Assert.Single(memoryCandidates!);
        Assert.Equal("success_pattern", memory.GetProperty("kind").GetString());
        Assert.Equal("workspace", memory.GetProperty("scope").GetString());
        Assert.Equal(candidate.GetProperty("source_run_id").GetString(), memory.GetProperty("source_run_id").GetString());

        var runId = RunIdOf(chunks[0]);
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage");
        Assert.Contains(lineage!, instance => instance.GetProperty("role").GetString() == "experience_curator");
    }

    [Fact]
    public async Task AgentCandidatePromotion_PublishesImmutableVersion()
    {
        var script = CuratedRunScript(CuratorCandidatesJson);
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "evolution-promote" });
        var candidates = await PollAgentCandidatesAsync(client, TimeSpan.FromSeconds(15));
        var candidateId = Assert.Single(candidates).GetProperty("id").GetGuid();

        var promoteResponse = await client.PostAsJsonAsync($"/api/v1/agent-evolution/proposals/{candidateId}/promote", new { reason = "评测说明：模式稳定，人工批准" });
        Assert.Equal(HttpStatusCode.OK, promoteResponse.StatusCode);
        var promoted = await promoteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("promoted", promoted.GetProperty("status").GetString());
        Assert.Equal("评测说明：模式稳定，人工批准", promoted.GetProperty("decision_reason").GetString());
        var published = promoted.GetProperty("published_agent");
        Assert.Equal(1, published.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(published.GetProperty("content_hash").GetString()));
        Assert.Equal(published.GetProperty("id").GetGuid(), promoted.GetProperty("promoted_agent_id").GetGuid());

        var directory = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.Contains(directory!, agent => agent.GetProperty("display_name").GetString() == "日志巡检执行体");

        var repeat = await client.PostAsJsonAsync($"/api/v1/agent-evolution/proposals/{candidateId}/promote", new { reason = "duplicate" });
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal("ALREADY_DECIDED", (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MemoryClosedLoop_PromotedMemoryIsInjectedIntoLaterRuns()
    {
        const string MemoryContent = "probe-pattern: dispatch workers after planning";
        var curatorJson = $"{{\"memory_candidates\":[{{\"scope\":\"workspace\",\"kind\":\"fact\",\"content\":\"{MemoryContent}\",\"confidence\":0.9,\"applicability\":\"probe pattern runs\"}}],\"agent_candidates\":[]}}";
        var script = CuratedRunScript(curatorJson);
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var firstRun = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "memory-loop-run-1" });
        Assert.Equal("completed", firstRun.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        // The curator runs after the done chunk; poll until its candidate lands.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        JsonElement[] memoryCandidates = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            memoryCandidates = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/memory-candidates?status=proposed");
            if (memoryCandidates!.Length > 0) break;
            await Task.Delay(100);
        }
        var memoryCandidateId = Assert.Single(memoryCandidates).GetProperty("id").GetGuid();

        var promoteResponse = await client.PostAsJsonAsync($"/api/v1/memory-candidates/{memoryCandidateId}/promote", new { reason = "verified pattern" });
        Assert.Equal(HttpStatusCode.OK, promoteResponse.StatusCode);

        // Second run in the same session: keyword retrieval must surface the
        // promoted memory inside the assembled worker/planner instructions.
        var secondRun = await StreamInvokeAsync(client, sessionId, new { content = "probe pattern notes", client_message_id = "memory-loop-run-2" });
        Assert.Equal("completed", secondRun.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());
        Assert.Contains(script.Instructions, instructions => instructions.Contains("probe-pattern", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunReplay_ReconstructsTimelineForCompletedRun()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "run-replay" });
        var runId = RunIdOf(chunks[0]);
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var replay = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/replay");
        Assert.Equal("completed", replay.GetProperty("status").GetString());
        var task = Assert.Single(replay.GetProperty("tasks").EnumerateArray());
        Assert.Equal("completed", task.GetProperty("status").GetString());
        var round = Assert.Single(replay.GetProperty("supervision_rounds").EnumerateArray());
        Assert.Equal("pass", round.GetProperty("decision").GetString());
        var milestones = replay.GetProperty("milestones").EnumerateArray()
            .Select(milestone => milestone.GetProperty("event_type").GetString()).ToArray();
        Assert.Contains("task_graph.created", milestones);
        Assert.Contains("user.response", milestones);
    }

    [Fact]
    public async Task AgentCandidateEvaluation_ReturnsProposalAndSourceRunReplay()
    {
        var script = CuratedRunScript(CuratorCandidatesJson);
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "candidate-evaluation" });
        var runId = RunIdOf(chunks[0]);
        var candidates = await PollAgentCandidatesAsync(client, TimeSpan.FromSeconds(15));
        var candidateId = Assert.Single(candidates).GetProperty("id").GetGuid();

        var evaluation = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-evolution/proposals/{candidateId}/evaluation");
        Assert.Equal("日志巡检执行体", evaluation.GetProperty("name").GetString());
        Assert.True(evaluation.GetProperty("proposal").TryGetProperty("system_prompt", out _));
        var replay = evaluation.GetProperty("source_run_replay");
        Assert.Equal(runId.ToString(), replay.GetProperty("run_id").GetString());
        Assert.NotEmpty(replay.GetProperty("tasks").EnumerateArray().ToArray());
        Assert.Equal("pass", Assert.Single(replay.GetProperty("supervision_rounds").EnumerateArray()).GetProperty("decision").GetString());
    }

    [Fact]
    public async Task WorkerContextPatch_AppliedAgainstInputRevision()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("探针完成。\nCONTEXT_PATCH: probe-pattern 已建立 || 后续任务应复用 probe-pattern 模式")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "worker-patch-applied" });
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var contextVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions");
        var patch = Assert.Single(contextVersions!, version =>
            version.GetProperty("kind").GetString() == "supplement"
            && version.GetProperty("status").GetString() == "applied");
        Assert.True(patch.GetProperty("revision").GetInt64() > patch.GetProperty("base_revision").GetInt64());
    }

    [Fact]
    public async Task WorkerContextPatch_SiblingRaceMarksSecondPatchStale()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("结果。\nCONTEXT_PATCH: 并行结论 || 两个任务基于同一上下文版本各自产出结论")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "执行目标", client_message_id = "worker-patch-race" });
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var contextVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions");
        var supplements = contextVersions!.Where(version => version.GetProperty("kind").GetString() == "supplement").ToArray();
        Assert.Equal(2, supplements.Length);
        Assert.Single(supplements, version => version.GetProperty("status").GetString() == "applied");
        Assert.Single(supplements, version => version.GetProperty("status").GetString() == "stale");
    }

    [Fact]
    public async Task InvokeStream_ActiveRunLimit_Returns409ForThirdRun()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforeWorker = gate.Task;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var runA = StartStreamingInvoke(client, sessionId, new { content = "任务A", client_message_id = "a" });
        var runB = StartStreamingInvoke(client, sessionId, new { content = "任务B", client_message_id = "b" });
        var ackA = await runA.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var ackB = await runB.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(RunIdOf(ackA), RunIdOf(ackB));

        // Wait until both runs are deterministically parked inside their worker
        // gate; their run states stay active regardless of meeting timing.
        var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (script.WorkerGateEntries < 2 && DateTimeOffset.UtcNow < waitDeadline)
        {
            await Task.Delay(50);
        }
        Assert.True(script.WorkerGateEntries >= 2, "Both runs should reach the worker gate before the limit probe.");

        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/invoke-stream", new { content = "任务C", client_message_id = "c" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE_RUN_LIMIT", body.GetProperty("code").GetString());

        gate.SetResult();
        await runA.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await runB.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Interactions_QueuedBehindActiveRunLimit_PersistsDirectiveMessageAndEvent()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforeWorker = gate.Task;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var runA = StartStreamingInvoke(client, sessionId, new { content = "任务A", client_message_id = "qa" });
        var runB = StartStreamingInvoke(client, sessionId, new { content = "任务B", client_message_id = "qb" });
        await runA.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        await runB.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (script.WorkerGateEntries < 2 && DateTimeOffset.UtcNow < waitDeadline)
        {
            await Task.Delay(50);
        }
        Assert.True(script.WorkerGateEntries >= 2, "Both runs should reach the worker gate before the queued probe.");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/interactions",
            new { content = "完成后测试并提交", client_message_id = "queued-followup" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", body.GetProperty("status").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("run_id").GetGuid());

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            var directives = await db.RunDirectives.Where(x => x.SessionId == sessionId).ToListAsync();
            var directive = Assert.Single(directives);
            Assert.Equal("queued_interaction", directive.Kind);
            Assert.Equal("pending", directive.Status);
            Assert.Equal(body.GetProperty("interaction_id").GetGuid(), directive.Id);
            Assert.NotNull(directive.RunId);
            Assert.NotNull(directive.MessageId);
            var payload = JsonSerializer.Deserialize<JsonElement>(directive.PayloadJson);
            Assert.Equal("完成后测试并提交", payload.GetProperty("content").GetString());
            Assert.Equal("queued-followup", payload.GetProperty("client_message_id").GetString());
            Assert.Equal(body.GetProperty("run_id").GetGuid(), directive.RunId!.Value);

            var queuedEvents = await db.EventIndex
                .Where(x => x.SessionId == sessionId && x.EventType == "run.queued")
                .ToListAsync();
            Assert.Single(queuedEvents, x => x.RunId == directive.RunId!.Value);
        }

        // The same client message replays the stored directive instead of queueing twice.
        var replay = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/interactions",
            new { content = "完成后测试并提交", client_message_id = "queued-followup" });
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(body.GetProperty("interaction_id").GetGuid(), replayBody.GetProperty("interaction_id").GetGuid());

        gate.SetResult();
        await runA.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await runB.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TwoLanes_WithLanesEnabled_RunCompletesWithBothTasks()
    {
        var runtimeToml = RuntimeTomlWith(
            "enabled = false\ncontext_token_threshold = 0\ncompress_on_task_closed = false\nrecommend_on_task_created = false\ncurate_on_run_closed = false\ngit_steward_on_run_closed = false\n")
            + "\n[orchestration]\nlanes_enabled = true\nmax_lanes_per_run = 4\nmax_tasks_per_lane = 6\n";
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\",\"lane_key\":\"l2\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("两个泳道都完成了。");
        var factory = CreateFactory(script, runtimeToml: runtimeToml);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "并行目标", client_message_id = "lanes-basic" });

        var done = Assert.Single(chunks.Where(chunk => KindOf(chunk) == "done"));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());
        var runId = RunIdOf(chunks[0]);

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
        var nodes = orchestration.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, node => Assert.Equal("completed", node.GetProperty("status").GetString()));
    }

    private static string LanesEnabledToml() =>
        RuntimeTomlWith(
            "enabled = false\ncontext_token_threshold = 0\ncompress_on_task_closed = false\nrecommend_on_task_created = false\ncurate_on_run_closed = false\ngit_steward_on_run_closed = false\n")
        + "\n[orchestration]\nlanes_enabled = true\nmax_lanes_per_run = 4\nmax_tasks_per_lane = 6\n";

    [Fact]
    public async Task TwoLanes_AdvanceInParallelUnderSingleRunLease()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\",\"lane_key\":\"l2\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("都完成了。");
        script.BeforeWorker = gate.Task;
        var factory = CreateFactory(script, runtimeToml: LanesEnabledToml());
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var run = StartStreamingInvoke(client, sessionId, new { content = "并行目标", client_message_id = "parallel-lease" });
        var runId = RunIdOf(await run.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));

        // Both lanes dispatch their worker under the same run before either
        // finishes: two gate entries prove per-lane ticks share one run loop.
        var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (script.WorkerGateEntries < 2 && DateTimeOffset.UtcNow < waitDeadline)
        {
            await Task.Delay(50);
        }
        Assert.Equal(2, script.WorkerGateEntries);

        gate.SetResult();
        var chunks = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var done = Assert.Single(chunks.Where(chunk => KindOf(chunk) == "done"));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
        Assert.All(orchestration.GetProperty("nodes").EnumerateArray(),
            node => Assert.Equal("completed", node.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task LaneGate_WaitingDoesNotFailRunAsInvalidTaskGraph()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[\"a\"],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\",\"lane_key\":\"l2\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。");
        script.BeforeWorker = gate.Task;
        var factory = CreateFactory(script, runtimeToml: LanesEnabledToml());
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var run = StartStreamingInvoke(client, sessionId, new { content = "跨 lane 目标", client_message_id = "lane-wait-park" });
        var runId = RunIdOf(await run.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));

        // l2 parks behind main instead of the old invalid_task_graph failure.
        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        JsonElement? waitingPayload = null;
        var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (waitingPayload is null && DateTimeOffset.UtcNow < waitDeadline)
        {
            foreach (var envelope in await manager.ReplayEventsAsync(sessionId, 0))
            {
                if (envelope.EventType != "orchestration.lane_waiting") continue;
                waitingPayload = (JsonElement)envelope.Payload["payload"]!;
                break;
            }
            if (waitingPayload is null) await Task.Delay(50);
        }
        Assert.NotNull(waitingPayload);
        Assert.Equal("l2", waitingPayload!.Value.GetProperty("lane_key").GetString());

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.NotEqual("failed", orchestration.GetProperty("run").GetProperty("status").GetString());
        Assert.NotEqual("invalid_task_graph", orchestration.GetProperty("run").GetProperty("status").GetString());

        gate.SetResult();
        var chunks = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task LaneGate_GateSequenceProceedsThroughWaitingReviewExecuting()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[\"a\"],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\",\"lane_key\":\"l2\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[],\"criteria_verdicts\":[{\"task_key\":\"a\",\"criterion\":\"完成\",\"satisfied\":true,\"evidence\":\"worker 输出含完成标记\"}]}")
            .WhenMeeting("完成。")
            .WhenGate("{\"decision\":\"proceed\",\"reasons\":[\"事实与裁决一致\"]}");
        var runtimeToml = LanesEnabledToml() + "[supervision]\nrequired_before_final = true\nmax_revision_rounds = 2\n";
        var factory = CreateFactory(script, runtimeToml: runtimeToml);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "门控目标", client_message_id = "gate-sequence" });
        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());
        var runId = RunIdOf(chunks[0]);

        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var laneEventTypes = (await manager.ReplayEventsAsync(sessionId, 0))
            .Where(e => e.EventType.StartsWith("orchestration.", StringComparison.Ordinal))
            .Select(e => (EventType: e.EventType, Payload: (JsonElement)e.Payload["payload"]!))
            .Where(pair => pair.Payload.TryGetProperty("lane_key", out var laneKey) && laneKey.GetString() == "l2")
            .Select(pair => pair.EventType)
            .ToArray();
        Assert.Equal(new[] { "orchestration.lane_waiting", "orchestration.gate_review", "orchestration.gate_review.completed" }, laneEventTypes);

        Assert.Equal(1, script.GateCalls);
        var gatePrompt = Assert.Single(script.GatePrompts);
        Assert.Contains("Lane: l2", gatePrompt, StringComparison.Ordinal);
        Assert.Contains("门控评审", gatePrompt, StringComparison.Ordinal);
        Assert.Contains("ObservedFactsHash: ", gatePrompt, StringComparison.Ordinal);
        Assert.Contains("criteria: 完成", gatePrompt, StringComparison.Ordinal);
        Assert.Contains("\"satisfied\":true", gatePrompt, StringComparison.Ordinal);
        Assert.Equal(1, script.PlannerLaneCalls["main"]);
        Assert.Equal(2, script.WorkerCalls);
    }

    [Fact]
    public async Task GateReview_ModelProceedCannotOverrideFalseFacts()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[\"a\"],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\",\"lane_key\":\"l2\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[],\"criteria_verdicts\":[{\"task_key\":\"a\",\"criterion\":\"完成\",\"satisfied\":false,\"evidence\":\"\"}]}")
            .WhenMeeting("不应到达。")
            .WhenGate("{\"decision\":\"proceed\",\"reasons\":[]}");
        var runtimeToml = LanesEnabledToml() + "[supervision]\nrequired_before_final = true\nmax_revision_rounds = 2\n";
        var factory = CreateFactory(script, runtimeToml: runtimeToml);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var run = StartStreamingInvoke(client, sessionId, new { content = "假事实目标", client_message_id = "gate-stale" });
        await run.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var runId = RunIdOf(await run.Acknowledgement);

        string? status = null;
        JsonElement orchestration = default;
        var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < waitDeadline)
        {
            orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
            status = orchestration.GetProperty("run").GetProperty("status").GetString();
            if (status == "awaiting_user" || status == "failed") break;
            await Task.Delay(50);
        }
        Assert.Equal("awaiting_user", status);
        Assert.False(run.Completion.IsCompleted);

        // The model said proceed, but the supervisor's unsatisfied verdict over
        // code facts rejects the gate and escalates only the waiting lane.
        Assert.Equal(1, script.GateCalls);
        Assert.Equal(1, script.WorkerCalls);
        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0);
        var rejection = Assert.Single(events, e => e.EventType == "gate.review.rejected_stale");
        var rejectionPayload = (JsonElement)rejection.Payload["payload"]!;
        Assert.Equal("l2", rejectionPayload.GetProperty("lane_key").GetString());
        Assert.Contains(events, e => e.EventType == "supervision.user_review.requested");
        // Task b was never dispatched, so the event-rebuilt orchestration graph
        // has no node for it.
        var nodes = orchestration.GetProperty("nodes").EnumerateArray().ToList();
        Assert.DoesNotContain(nodes, node => node.GetProperty("title").GetString() == "任务B");
    }

    [Fact]
    public async Task MeetingDirective_LaneOpenConsumedByTargetRun()
    {
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeetingOnce("收到。已登记延后执行。\nLANE_OPEN: {\"lane_key\":\"l2\",\"goal\":\"完成后测试并提交\",\"tasks\":[{\"task_key\":\"t1\",\"title\":\"测试并提交\",\"description\":\"\",\"success_criteria\":[\"通过\"],\"dependencies\":[],\"priority\":1,\"risk\":\"low\"}],\"waits\":[{\"lane\":\"main\"}]}")
            .WhenMeeting("全部完成。");
        script.BeforeWorker = workerGate.Task;
        script.WorkerStarted = workerStarted;
        var factory = CreateFactory(script, runtimeToml: LanesEnabledToml());
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var target = StartStreamingInvoke(client, sessionId, new { content = "开发功能 X", client_message_id = "lane-open-target" });
        var runId = RunIdOf(await target.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The user leaves mid-task with a deferred instruction; the meeting turn
        // must carry it as a durable directive, and the protocol line must never
        // reach the user stream — the confirmation sentence is code-generated.
        var clarification = await StreamInvokeAsync(client, sessionId, new
        {
            content = "完成后测试并提交",
            client_message_id = "lane-open-1",
            target_run_id = runId
        });
        var meetingDelta = string.Concat(clarification
            .Where(chunk => KindOf(chunk) == "delta")
            .Select(chunk => chunk.GetProperty("delta").GetString()));
        Assert.DoesNotContain("LANE_OPEN", meetingDelta, StringComparison.Ordinal);
        Assert.Contains("Orchestration registered", meetingDelta, StringComparison.Ordinal);

        workerGate.SetResult();
        var chunks = await target.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var events = (await manager.ReplayEventsAsync(sessionId, 0))
            .Where(e => e.RunId == runId.ToString())
            .ToList();
        var opened = Assert.Single(events, e => e.EventType == "orchestration.lane_opened");
        var openedPayload = (JsonElement)opened.Payload["payload"]!;
        Assert.Equal("l2", openedPayload.GetProperty("lane_key").GetString());
        Assert.Equal("t1", openedPayload.GetProperty("task_keys").EnumerateArray().Single().GetString());
        Assert.Equal("main", openedPayload.GetProperty("waits").EnumerateArray().Single().GetProperty("lane").GetString());
        Assert.DoesNotContain(events, e => e.EventType == "orchestration.directive.rejected");

        // The opened lane really executed its deferred task under the same run
        // and lease: the main worker ran once, then the l2 worker ran once.
        Assert.Equal(2, script.WorkerCalls);
    }

    [Fact]
    public async Task GoalOnlyLaneOpen_PlansThroughItsOwnPlannerInstance()
    {
        // The user's deferred instruction carries only a goal. The lane must be
        // planned by its own planning agent instance — literally a second
        // parallel task-planning agent inside the same run lease.
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"main-a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenPlanner("l2", "[{\"task_key\":\"l2-test\",\"title\":\"测试并提交\",\"description\":\"\",\"success_criteria\":[\"通过\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeetingOnce("收到。已登记延后执行。\nLANE_OPEN: {\"lane_key\":\"l2\",\"goal\":\"完成后测试并提交\",\"waits\":[{\"lane\":\"main\"}]}")
            .WhenMeeting("全部完成。");
        script.BeforeWorker = workerGate.Task;
        script.WorkerStarted = workerStarted;
        var factory = CreateFactory(script, runtimeToml: LanesEnabledToml());
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var target = StartStreamingInvoke(client, sessionId, new { content = "开发功能 X", client_message_id = "goal-lane-target" });
        var runId = RunIdOf(await target.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var clarification = await StreamInvokeAsync(client, sessionId, new
        {
            content = "完成后测试并提交",
            client_message_id = "goal-lane-1",
            target_run_id = runId
        });
        Assert.Contains("Orchestration registered", string.Concat(clarification
            .Where(chunk => KindOf(chunk) == "delta")
            .Select(chunk => chunk.GetProperty("delta").GetString())), StringComparison.Ordinal);

        workerGate.SetResult();
        var chunks = await target.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var events = (await manager.ReplayEventsAsync(sessionId, 0)).Where(e => e.RunId == runId.ToString()).ToList();

        // The lane passed through planning on its own instance and then planned.
        var planning = Assert.Single(events, e => e.EventType == "orchestration.lane_planning");
        Assert.Equal("l2", ((JsonElement)planning.Payload["payload"]!).GetProperty("lane_key").GetString());
        var planned = Assert.Single(events, e => e.EventType == "orchestration.lane_planned");
        var plannedPayload = (JsonElement)planned.Payload["payload"]!;
        Assert.Equal("l2", plannedPayload.GetProperty("lane_key").GetString());
        Assert.Equal(1, plannedPayload.GetProperty("task_count").GetInt32());
        Assert.NotEqual(Guid.Empty, plannedPayload.GetProperty("planner_instance_id").GetGuid());

        // The lane's planner instance is a distinct root instance tagged with the lane.
        var created = Assert.Single(events, e =>
        {
            if (e.EventType != "agent.created") return false;
            var payload = (JsonElement)e.Payload["payload"]!;
            return payload.TryGetProperty("lane_key", out var laneKey) && laneKey.GetString() == "l2";
        });
        var createdPayload = (JsonElement)created.Payload["payload"]!;
        Assert.Equal(plannedPayload.GetProperty("planner_instance_id").GetGuid(), createdPayload.GetProperty("agent_instance_id").GetGuid());
        Assert.Equal("l2", createdPayload.GetProperty("lane_key").GetString());

        // The opened lane really executed its deferred task: main once, l2 once.
        var opened = Assert.Single(events, e => e.EventType == "orchestration.lane_opened");
        Assert.Equal(1, ((JsonElement)opened.Payload["payload"]!).GetProperty("task_count").GetInt32());
        Assert.Equal(2, script.WorkerCalls);
        Assert.Equal(1, script.PlannerLaneCalls["l2"]);
    }

    [Fact]
    public async Task GoalOnlyLane_GateReviewRoutesToItsOwnPlannerInstance()
    {
        // Main carries two serial tasks: the run loop is blocked inside the
        // first worker round, so the directive is consumed between rounds while
        // main's second task is still pending. The goal-only lane therefore
        // parks on its unmet wait, and once main finishes it must pass a gate
        // that is reviewed by the lane's own planning agent instance.
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[\"a\"],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenPlanner("l2", "[{\"task_key\":\"c\",\"title\":\"任务C\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[],\"criteria_verdicts\":[{\"task_key\":\"a\",\"criterion\":\"完成\",\"satisfied\":true,\"evidence\":\"worker 输出含完成标记\"},{\"task_key\":\"b\",\"criterion\":\"完成\",\"satisfied\":true,\"evidence\":\"worker 输出含完成标记\"}]}")
            .WhenMeetingOnce("收到。已登记延后执行。\nLANE_OPEN: {\"lane_key\":\"l2\",\"goal\":\"收尾验证\",\"waits\":[{\"lane\":\"main\"}]}")
            .WhenMeeting("全部完成。")
            .WhenGate("{\"decision\":\"proceed\",\"reasons\":[\"事实与裁决一致\"]}");
        script.BeforeWorker = workerGate.Task;
        script.WorkerStarted = workerStarted;
        var runtimeToml = LanesEnabledToml() + "[supervision]\nrequired_before_final = true\nmax_revision_rounds = 2\n";
        var factory = CreateFactory(script, runtimeToml: runtimeToml);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var target = StartStreamingInvoke(client, sessionId, new { content = "开发功能 X", client_message_id = "goal-gate-target" });
        var runId = RunIdOf(await target.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The directive lands while the run loop is blocked inside main's first
        // worker round, so the lane's wait is unmet when it is consumed.
        await StreamInvokeAsync(client, sessionId, new
        {
            content = "收尾验证",
            client_message_id = "goal-gate-1",
            target_run_id = runId
        });
        workerGate.SetResult();
        var chunks = await target.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var events = (await manager.ReplayEventsAsync(sessionId, 0)).Where(e => e.RunId == runId.ToString()).ToList();
        Assert.Contains(events, e => e.EventType == "orchestration.lane_planning");
        Assert.Contains(events, e => e.EventType == "orchestration.lane_waiting");

        // The gate for a lane with its own planner is reviewed by that planner.
        Assert.Equal(1, script.GateCalls);
        var gatePrompt = Assert.Single(script.GatePrompts);
        Assert.Contains("Lane: l2", gatePrompt, StringComparison.Ordinal);
        Assert.Equal(1, script.PlannerLaneCalls["l2"]);
        Assert.Equal(1, script.PlannerLaneCalls["main"]);
        Assert.Equal(3, script.WorkerCalls);
    }

    [Fact]
    public async Task RunTerminal_DrainsQueuedDirective_RejectedWhenLanesDisabled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforeWorker = gate.Task;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var runA = StartStreamingInvoke(client, sessionId, new { content = "任务A", client_message_id = "drain-a" });
        var runB = StartStreamingInvoke(client, sessionId, new { content = "任务B", client_message_id = "drain-b" });
        await runA.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        await runB.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (script.WorkerGateEntries < 2 && DateTimeOffset.UtcNow < waitDeadline)
        {
            await Task.Delay(50);
        }

        var queued = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/interactions",
            new { content = "完成后测试并提交", client_message_id = "drain-followup" });
        Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
        var queuedBody = await queued.Content.ReadFromJsonAsync<JsonElement>();
        var directiveId = queuedBody.GetProperty("interaction_id").GetGuid();

        gate.SetResult();
        await runA.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await runB.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            var directive = await db.RunDirectives.SingleAsync(x => x.Id == directiveId);
            Assert.Equal("rejected", directive.Status);
            Assert.NotNull(directive.DrainedAt);
        }

        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0);
        var rejection = Assert.Single(events, e => e.EventType == "orchestration.directive.rejected");
        var payload = (JsonElement)rejection.Payload["payload"]!;
        Assert.Equal("lanes_disabled", payload.GetProperty("code").GetString());
        Assert.Equal(directiveId.ToString(), payload.GetProperty("directive_id").GetString());
    }

    [Fact]
    public async Task LaneCheckpointSave_NeverReusesPriorRevisionBody()
    {
        var factory = CreateFactory(new ScriptedChatClient());
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var runId = await manager.StartRunAsync(sessionId.ToString());

        // Same purpose and revision from two lanes must produce two checkpoint
        // rows; a shared key would hand the second lane the first lane's body.
        var mainSave = await manager.SaveRunCheckpointAsync(runId, new RunCheckpointWrite(0, "executing", "{\"v\":\"main\"}", IdempotencyKey: $"run:{runId}:main:tick:1"));
        var laneSave = await manager.SaveRunCheckpointAsync(runId, new RunCheckpointWrite(mainSave.Revision, "executing", "{\"v\":\"l2\"}", IdempotencyKey: $"run:{runId}:l2:tick:1"));

        Assert.Equal(mainSave.Revision + 1, laneSave.Revision);
        var current = await manager.GetCurrentRunCheckpointAsync(runId);
        Assert.NotNull(current);
        Assert.Contains("\"l2\"", current!.Content, StringComparison.Ordinal);

        // An exact replay — same expected revision, phase, body, and key —
        // returns the stored row instead of writing a new one.
        var replay = await manager.SaveRunCheckpointAsync(runId, new RunCheckpointWrite(0, "executing", "{\"v\":\"main\"}", IdempotencyKey: $"run:{runId}:main:tick:1"));
        Assert.Equal(mainSave.Revision, replay.Revision);
    }

    [Fact]
    public async Task RunControl_Cancel_EndsWithDoneCancelled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforeMeeting = gate.Task;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var invoke = StartStreamingInvoke(client, sessionId, new { content = "长任务" });
        var runId = RunIdOf(await invoke.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));

        var control = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/control", new { action = "cancel" });
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        gate.SetResult();

        var chunks = await invoke.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var last = chunks.Last();
        Assert.Equal("done", KindOf(last));
        Assert.Equal("cancelled", last.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task Supervision_ReviseThenPass_ReExecutesTasks()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("第一轮结果")
            .WhenSupervisor("{\"decision\":\"revise\",\"reasons\":[\"证据不足\"],\"revise_task_indexes\":[0]}")
            .ThenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已修正。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "目标" });

        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        var runId = RunIdOf(chunks[0]);
        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        var decisions = orchestration.GetProperty("supervision_findings").EnumerateArray()
            .Select(f => f.GetProperty("decision").GetString()).Where(d => d is not null).ToArray();
        Assert.Equal(new[] { "revise", "pass" }, decisions);
        Assert.Equal(2, script.WorkerCalls);
        // A revise verdict replans through the planner instead of just retrying.
        Assert.Equal(2, script.PlannerCalls);
    }

    [Fact]
    public async Task Supervision_Revise_ReplansGraphWithNewTask()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .ThenPlanner("[{\"task_key\":\"a\",\"title\":\"任务A-修正\",\"description\":\"按监督意见修正\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"任务B\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[\"a\"],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("结果")
            .WhenSupervisor("{\"decision\":\"revise\",\"reasons\":[\"缺任务B\"],\"revise_task_indexes\":[0]}")
            .ThenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已修正。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "目标" });

        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        Assert.Equal(2, script.PlannerCalls);
        // Round 1 runs A; the replan adds B behind A, so three worker turns run.
        Assert.Equal(3, script.WorkerCalls);
        var runId = RunIdOf(chunks[0]);
        // The replan is journaled as a new task_graph.created event carrying
        // the revised keys — not a silent retry of the old graph.
        var manager = factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0);
        var replans = events
            .Where(e => e.EventType == "task_graph.created" && string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(e => (JsonElement)e.Payload["payload"]!)
            .Where(p => p.TryGetProperty("replan", out var replan) && replan.GetBoolean())
            .ToArray();
        var replan = Assert.Single(replans);
        var keys = replan.GetProperty("task_keys").EnumerateArray().Select(k => k.GetString()).ToArray();
        Assert.Contains("b", keys);
    }

    [Fact]
    public async Task Supervision_Revise_PlannerGarbage_FallsBackToRetry()
    {
        // Duplicate task keys fail graph validation on both replan attempts
        // ("[]" would silently degrade to one task; oversize is unreachable
        // because PlanningAgent caps output at Take(8)).
        const string invalidGraph = "[{\"task_key\":\"dup\",\"title\":\"任务X\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"},{\"task_key\":\"dup\",\"title\":\"任务Y\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]";
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .ThenPlanner(invalidGraph)
            .ThenPlanner(invalidGraph)
            .WhenWorker("结果")
            .WhenSupervisor("{\"decision\":\"revise\",\"reasons\":[\"证据不足\"],\"revise_task_indexes\":[0]}")
            .ThenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已修正。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "目标" });

        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        // Both replan attempts fail validation, so the run falls back to
        // resetting the flagged task and re-executes it.
        Assert.Equal(3, script.PlannerCalls);
        Assert.Equal(2, script.WorkerCalls);
    }

    [Fact]
    public async Task Supervision_Escalate_AwaitsUserDecisionAndContinuesWithoutEscalationFinishReason()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("结果")
            .WhenSupervisor("{\"decision\":\"escalate\",\"reasons\":[\"高风险\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("请用户决定。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var active = StartStreamingInvoke(client, sessionId, new { content = "目标" });
        var acknowledgement = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(15));
        var runId = RunIdOf(acknowledgement);

        JsonElement orchestration = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
            if (orchestration.GetProperty("run").GetProperty("status").GetString() == "awaiting_user") break;
            await Task.Delay(50);
        }

        Assert.Equal("awaiting_user", orchestration.GetProperty("run").GetProperty("status").GetString());
        Assert.False(active.Completion.IsCompleted);

        var resume = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/control", new { action = "resume" });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());
        Assert.DoesNotContain(chunks, chunk => chunk.TryGetProperty("finish_reason", out var reason)
            && reason.GetString() == "completed_with_escalation");
    }

    [Fact]
    public async Task AgentModes_ExposeTheBootstrapDirectory()
    {
        var factory = CreateFactory();
        var client = factory.CreateClient();

        var conversation = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?application_mode=conversation");
        Assert.Contains(conversation!, m => m.GetProperty("slug").GetString() == "conversation.auto");
        Assert.Contains(conversation!, m => m.GetProperty("slug").GetString() == "conversation.plan");

        var space = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?application_mode=space");
        Assert.Contains(space!, m => m.GetProperty("slug").GetString() == "default-mode");

        // `im` remains a conversation alias.
        var im = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?application_mode=im");
        Assert.Contains(im!, m => m.GetProperty("slug").GetString() == "conversation.auto");

        var unknown = await client.GetAsync("/api/v1/agent-modes?application_mode=nope");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // The TOML-era projection is gone: the mode directory is the only surface.
        var legacy = await client.GetAsync("/api/v1/application-modes");
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    [Fact]
    public async Task AgentEvolution_PromoteRoute_RequiresAnExistingProposedCandidate()
    {
        var factory = CreateFactory();
        var client = factory.CreateClient();
        var candidateId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync($"/api/v1/agent-evolution/proposals/{candidateId}/promote", new { reason = "reviewed" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("code").GetString());
        Assert.Equal(candidateId, body.GetProperty("candidate_id").GetGuid());

        var legacy = await client.PostAsJsonAsync($"/api/v1/agent-candidates/{candidateId}/promote", new { reason = "reviewed" });
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
        var legacyBody = await legacy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", legacyBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ContextVersions_ListedAfterInvoke()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好。");
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var chunks = await StreamInvokeAsync(client, sessionId, new { content = "目标" });
        var runId = RunIdOf(chunks[0]);

        var versions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/context-versions");
        Assert.NotEmpty(versions!);
        Assert.Contains(versions!, v => v.GetProperty("kind").GetString() == "snapshot");
    }

    [Fact]
    public async Task MemoryCandidate_PromoteMakesItRetrievable_RejectKeepsItHidden()
    {
        var factory = CreateFactory();
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        // Promote one candidate created through the service.
        var scope = factory.Services.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
        var promoted = await memory.CreateCandidateAsync(new MemoryCandidateProposal(Guid.NewGuid(), Guid.NewGuid(), "workspace", "fact", "用户偏好深色主题", 0.9));
        var rejected = await memory.CreateCandidateAsync(new MemoryCandidateProposal(Guid.NewGuid(), Guid.NewGuid(), "workspace", "fact", "不该出现的记忆", 0.4));

        var promote = await client.PostAsJsonAsync($"/api/v1/memory-candidates/{promoted.Id}/promote", new { reason = "正确" });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        var reject = await client.PostAsJsonAsync($"/api/v1/memory-candidates/{rejected.Id}/reject", new { reason = "不相关" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var again = await client.PostAsJsonAsync($"/api/v1/memory-candidates/{promoted.Id}/promote", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var listed = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/memory-candidates?status=promoted");
        Assert.Contains(listed!, c => c.GetProperty("id").GetGuid() == promoted.Id);
        var items = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/memory-items");
        Assert.Contains(items!, i => i.GetProperty("status").GetString() == "active");

        // Retrieval sees only the promoted item.
        var memoryStore = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var hits = await memoryStore.RetrieveAsync(sessionId.ToString(), "深色主题", 10);
        Assert.Contains(hits, h => h.Content.Contains("深色主题"));
        Assert.DoesNotContain(hits, h => h.Content.Contains("不该出现的记忆"));
    }

    [Fact]
    public async Task RunControl_InvalidAction_Returns400()
    {
        var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/v1/runs/{Guid.NewGuid()}/control", new { action = "explode" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_RUN_CONTROL", body.GetProperty("code").GetString());
    }

    private sealed class FullDuplexFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly ScriptedChatClient _client;
        private readonly bool _available;
        private readonly IPromptAssembler? _promptAssembler;
        private readonly string? _runtimeToml;

        public FullDuplexFactory(string root, ScriptedChatClient client, bool available, IPromptAssembler? promptAssembler, string? runtimeToml = null)
        {
            _root = root;
            _client = client;
            _available = available;
            _promptAssembler = promptAssembler;
            _runtimeToml = runtimeToml;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            var settings = new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            };
            if (_runtimeToml is not null)
            {
                var tomlPath = Path.Combine(_root, "test-agent-runtime.toml");
                File.WriteAllText(tomlPath, _runtimeToml, Encoding.UTF8);
                settings["TinadecAgent:ProfileConfigPath"] = tomlPath;
            }
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAgentChatClientFactory>(new ScriptedFactory(_client, _available));
                services.AddSingleton<ISecretStore>(new TestModelSecretStore(_available));
                services.AddSingleton<IToolManifestSnapshotResolver, EmptyToolManifestSnapshotResolver>();
                if (_promptAssembler is not null) services.AddSingleton(_promptAssembler);
            });
        }
    }

    private sealed class RecordingPromptAssembler : IPromptAssembler
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _agentIds = new(StringComparer.Ordinal);

        public IReadOnlyList<string> AgentIds
        {
            get { lock (_gate) return _agentIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
        }

        public Task<PromptAssemblyResult> AssembleAsync(
            string agentId,
            ContextPack? contextPack,
            CancellationToken cancellationToken = default) =>
            AssembleAsync(new FrozenPromptAssemblyRequest(agentId, contextPack), cancellationToken);

        public Task<PromptAssemblyResult> AssembleAsync(
            FrozenPromptAssemblyRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _agentIds.Add(request.AgentId);
            return Task.FromResult(new PromptAssemblyResult
            {
                Instructions = $"frozen-prompt:{request.AgentId}",
                FragmentIds = [$"test:{request.AgentId}"]
            });
        }
    }

    private sealed class ScriptedFactory(ScriptedChatClient client, bool available) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default)
            => Task.FromResult(available
                ? new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "fake", ApiKey = "x", ModelId = "openai/fake" }
                : new ChatResolution { IsAvailable = false, Error = "Provider API key is not stored." });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default)
            => Task.FromResult<IChatClient>(client);
    }

    /// <summary>
    /// Meeting interaction tests do not exercise tool execution. Freezing a valid
    /// empty v2 manifest keeps them isolated from a local TinadecTools child
    /// process while still using the production admission path.
    /// </summary>
    private sealed class EmptyToolManifestSnapshotResolver : IToolManifestSnapshotResolver
    {
        private static readonly ToolManifestSnapshot Snapshot = new(
            2,
            ToolManifestHasher.Compute(Array.Empty<FrozenToolManifestEntry>()),
            []);

        public Task<ToolManifestSnapshot> ResolveAsync(
            ToolManifestSnapshotRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }

    /// <summary>
    /// Routes calls by agent name: planner/executor/supervisor/meeting get independent
    /// scripts. Extra supervision verdicts play in order after the first. Lane-aware:
    /// planner prompts carrying a "Lane: &lt;key&gt;" line (in prompt or instructions) route
    /// to their per-lane script, and gate-review prompts (marked "门控评审") route to the
    /// gate script instead of the planner script.
    /// </summary>
    public sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _supervisorVerdicts = new();
        private string? _planner;
        private readonly Queue<string> _plannerFollowUps = new();
        private readonly Dictionary<string, string> _plannerLanes = new(StringComparer.Ordinal);
        private string? _gate;
        private string? _worker;
        private string? _meeting;
        private string? _meetingOnce;
        private string? _compressor;
        private string? _recommender;
        private string? _curator;
        private string? _steward;
        private UsageDetails? _usage;
        public int PlannerCalls;
        public int WorkerCalls;
        public int CompressorCalls;
        public int RecommenderCalls;
        public int CuratorCalls;
        public int StewardCalls;
        public int GateCalls;
        public Dictionary<string, int> PlannerLaneCalls { get; } = new(StringComparer.Ordinal);
        private readonly object _laneGate = new();
        private readonly List<string> _gatePrompts = [];
        public IReadOnlyList<string> GatePrompts
        {
            get { lock (_laneGate) return _gatePrompts.ToArray(); }
        }
        public Task? BeforeMeeting;
        public Task? BeforeWorker;
        public TaskCompletionSource? WorkerStarted;
        public int WorkerGateEntries;
        private readonly object _instructionsGate = new();
        private readonly List<string> _instructions = [];

        public IReadOnlyList<string> Instructions
        {
            get { lock (_instructionsGate) return _instructions.ToArray(); }
        }

        public ScriptedChatClient WhenPlanner(string script) { _planner = script; return this; }
        public ScriptedChatClient WhenPlanner(string lane, string script) { _plannerLanes[lane] = script; return this; }
        /// <summary>Planner script consumed by the next replan call (supervision revise),
        /// then falls back to WhenPlanner. Queued in order for multiple revisions.</summary>
        public ScriptedChatClient ThenPlanner(string script) { _plannerFollowUps.Enqueue(script); return this; }
        public ScriptedChatClient WhenGate(string script) { _gate = script; return this; }
        public ScriptedChatClient WhenWorker(string script) { _worker = script; return this; }
        public ScriptedChatClient WhenSupervisor(string verdict) { _supervisorVerdicts.Enqueue(verdict); return this; }
        public ScriptedChatClient ThenSupervisor(string verdict) { _supervisorVerdicts.Enqueue(verdict); return this; }
        public ScriptedChatClient WhenMeeting(string script) { _meeting = script; return this; }

        /// <summary>Meeting text consumed by the very next meeting call, then falls
        /// back to WhenMeeting/defaults — for clarification turns whose protocol
        /// lines must not leak into the target run's own finalization meeting.</summary>
        public ScriptedChatClient WhenMeetingOnce(string script) { _meetingOnce = script; return this; }
        public ScriptedChatClient WhenCompressor(string script) { _compressor = script; return this; }
        public ScriptedChatClient WhenRecommender(string script) { _recommender = script; return this; }
        public ScriptedChatClient WhenCurator(string script) { _curator = script; return this; }
        public ScriptedChatClient WhenSteward(string script) { _steward = script; return this; }
        public ScriptedChatClient WithUsage(long inputTokens, long outputTokens)
        {
            _usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens
            };
            return this;
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join('\n', messages.Select(m => m.Text));
            var instructions = options?.Instructions;
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                lock (_instructionsGate) _instructions.Add(instructions);
            }
            var isPlanner = instructions?.Contains("任务规划智能体", StringComparison.Ordinal) == true
                || instructions?.Contains("规划层", StringComparison.Ordinal) == true
                || prompt.Contains("规划", StringComparison.Ordinal) && !prompt.Contains("执行证据", StringComparison.Ordinal);
            var isSupervisor = instructions?.Contains("监督智能体", StringComparison.Ordinal) == true
                || prompt.Contains("执行证据", StringComparison.Ordinal);
            var isMeeting = instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) == true
                || prompt.Contains("Execution evidence", StringComparison.Ordinal);
            if (!isPlanner && !isSupervisor && !isMeeting && !IsOperational(instructions))
            {
                WorkerStarted?.TrySetResult();
                if (BeforeWorker is not null)
                {
                    Interlocked.Increment(ref WorkerGateEntries);
                    await BeforeWorker.WaitAsync(cancellationToken);
                }
            }
            var text = RouteByPrompt(prompt, instructions);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = CloneUsage() };
        }

        private static bool IsOperational(string? instructions) =>
            instructions?.Contains("You are the context compression agent", StringComparison.Ordinal) == true
            || instructions?.Contains("You are the capability advisor", StringComparison.Ordinal) == true
            || instructions?.Contains("You are the experience curator", StringComparison.Ordinal) == true
            || instructions?.Contains("You are the git steward", StringComparison.Ordinal) == true;

        /// <summary>Routes by prompt shape: the runtime's fixed Chinese scaffolding identifies the caller.</summary>
        private string RouteByPrompt(string prompt, string? instructions)
        {
            if (instructions?.Contains("You are the context compression agent", StringComparison.Ordinal) == true)
                return RecordOperational(ref CompressorCalls, _compressor ?? "当前目标：完成用户任务。\n关键约束：无。\n已完成事项：无。");
            if (instructions?.Contains("You are the capability advisor", StringComparison.Ordinal) == true)
                return RecordOperational(ref RecommenderCalls, _recommender ?? "{\"recommendations\":[]}");
            if (instructions?.Contains("You are the experience curator", StringComparison.Ordinal) == true)
                return RecordOperational(ref CuratorCalls, _curator ?? "{\"memory_candidates\":[],\"agent_candidates\":[]}");
            if (instructions?.Contains("You are the git steward", StringComparison.Ordinal) == true)
                return RecordOperational(ref StewardCalls, _steward ?? "No git changes to review.");
            if (prompt.Contains("门控评审", StringComparison.Ordinal))
                return RecordGate(prompt, _gate ?? "{\"decision\":\"proceed\",\"reasons\":[]}");
            var plannerLane = LaneKeyFromPrompt(prompt) ?? LaneKeyFromPrompt(instructions);
            if (instructions?.Contains("任务规划智能体", StringComparison.Ordinal) == true
                || instructions?.Contains("规划层", StringComparison.Ordinal) == true
                || prompt.Contains("规划", StringComparison.Ordinal) && !prompt.Contains("执行证据", StringComparison.Ordinal))
            {
                if (plannerLane is not null) RecordPlannerLane(plannerLane);
                if (plannerLane is not null && _plannerLanes.TryGetValue(plannerLane, out var laneScript))
                    return RecordPlanner(laneScript);
                // A supervision replan reuses the main planner call: serve queued
                // follow-up graphs first so tests can assert true replanning.
                if (PlannerCalls > 0 && _plannerFollowUps.Count > 0)
                    return RecordPlanner(_plannerFollowUps.Dequeue());
                return RecordPlanner(_planner ?? "[]");
            }
            if (instructions?.Contains("监督智能体", StringComparison.Ordinal) == true || prompt.Contains("执行证据", StringComparison.Ordinal))
                return _supervisorVerdicts.Count > 0 ? _supervisorVerdicts.Dequeue() : "{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}";
            if (instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) == true || prompt.Contains("Execution evidence", StringComparison.Ordinal))
            {
                var once = Interlocked.Exchange(ref _meetingOnce, null);
                return once ?? _meeting ?? "完成";
            }
            return _worker is { } workerText ? RecordWorker(workerText) : "完成";
        }

        private string RecordWorker(string text)
        {
            Interlocked.Increment(ref WorkerCalls);
            return text;
        }

        private string RecordPlanner(string text)
        {
            Interlocked.Increment(ref PlannerCalls);
            return text;
        }

        private string RecordGate(string prompt, string text)
        {
            Interlocked.Increment(ref GateCalls);
            lock (_laneGate) _gatePrompts.Add(prompt);
            return text;
        }

        private void RecordPlannerLane(string lane)
        {
            lock (_laneGate)
            {
                PlannerLaneCalls[lane] = PlannerLaneCalls.GetValueOrDefault(lane) + 1;
            }
        }

        /// <summary>Extracts the first "Lane: &lt;key&gt;" token from prompt or instructions text.</summary>
        private static string? LaneKeyFromPrompt(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            const string marker = "Lane: ";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            return end > start ? text[start..end] : null;
        }

        private static string RecordOperational(ref int counter, string text)
        {
            Interlocked.Increment(ref counter);
            return text;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(options?.Instructions))
            {
                lock (_instructionsGate) _instructions.Add(options.Instructions);
            }
            var isMeeting = options?.Instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) ?? false;
            if (isMeeting)
            {
                if (BeforeMeeting is not null) await BeforeMeeting.WaitAsync(cancellationToken);
                var chunks = (_meeting ?? "回复").Chunk(2).ToArray();
                foreach (var chunk in chunks)
                {
                    await Task.Delay(1, cancellationToken);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, new string(chunk));
                }
                if (CloneUsage() is { } usage)
                {
                    yield return new ChatResponseUpdate(null, [new UsageContent(usage)]);
                }
            }
            else
            {
                await Task.Delay(1, cancellationToken);
                var response = await GetResponseAsync(messages, options, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
            }
        }

        private UsageDetails? CloneUsage() => _usage is null
            ? null
            : new UsageDetails
            {
                InputTokenCount = _usage.InputTokenCount,
                OutputTokenCount = _usage.OutputTokenCount,
                TotalTokenCount = _usage.TotalTokenCount
            };
    }
}
