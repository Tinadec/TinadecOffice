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
        IPromptAssembler? promptAssembler = null)
    {
        _factory?.Dispose();
        _factory = new FullDuplexFactory(_root, client ?? new ScriptedChatClient(), available, promptAssembler);
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
    public async Task InvokeStream_InjectsFrozenPromptIntoEveryRunnableRoleWithoutStartingDormantRoles()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"task_key\":\"task-1\",\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("已完成任务")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("全部完成。");
        var prompts = new RecordingPromptAssembler();
        var factory = CreateFactory(script, promptAssembler: prompts);
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
        // planner + worker + supervisor + meeting
        Assert.Equal(4, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(8, usage.GetProperty("output_tokens").GetInt64());
        Assert.Equal(12, usage.GetProperty("total_tokens").GetInt64());
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
        var appliedPatch = Assert.Single(contextVersions!, version => version.GetProperty("kind").GetString() == "patch"
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
        var appliedPatch = Assert.Single(contextVersions!, version => version.GetProperty("kind").GetString() == "patch"
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
    public async Task InvokeStream_ActiveRunLimit_Returns409ForThirdRun()
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

        var runA = StartStreamingInvoke(client, sessionId, new { content = "任务A", client_message_id = "a" });
        var runB = StartStreamingInvoke(client, sessionId, new { content = "任务B", client_message_id = "b" });
        var ackA = await runA.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var ackB = await runB.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(RunIdOf(ackA), RunIdOf(ackB));

        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/invoke-stream", new { content = "任务C", client_message_id = "c" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE_RUN_LIMIT", body.GetProperty("code").GetString());

        gate.SetResult();
        await runA.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await runB.Completion.WaitAsync(TimeSpan.FromSeconds(30));
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
    public async Task AgentEvolution_PromoteRoute_IsFailClosedUntilPipelineExists()
    {
        var factory = CreateFactory();
        var client = factory.CreateClient();
        var candidateId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync($"/api/v1/agent-evolution/proposals/{candidateId}/promote", new { reason = "reviewed" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("candidate_pipeline_required", body.GetProperty("code").GetString());
        Assert.Equal(candidateId, body.GetProperty("candidate_id").GetGuid());

        var legacy = await client.PostAsJsonAsync($"/api/v1/agent-candidates/{candidateId}/promote", new { reason = "reviewed" });
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        var legacyBody = await legacy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("candidate_pipeline_required", legacyBody.GetProperty("code").GetString());
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

        public FullDuplexFactory(string root, ScriptedChatClient client, bool available, IPromptAssembler? promptAssembler)
        {
            _root = root;
            _client = client;
            _available = available;
            _promptAssembler = promptAssembler;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
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
    /// scripts. Extra supervision verdicts play in order after the first.
    /// </summary>
    public sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _supervisorVerdicts = new();
        private string? _planner;
        private string? _worker;
        private string? _meeting;
        private UsageDetails? _usage;
        public int PlannerCalls;
        public int WorkerCalls;
        public Task? BeforeMeeting;
        public Task? BeforeWorker;
        public TaskCompletionSource? WorkerStarted;
        private readonly object _instructionsGate = new();
        private readonly List<string> _instructions = [];

        public IReadOnlyList<string> Instructions
        {
            get { lock (_instructionsGate) return _instructions.ToArray(); }
        }

        public ScriptedChatClient WhenPlanner(string script) { _planner = script; return this; }
        public ScriptedChatClient WhenWorker(string script) { _worker = script; return this; }
        public ScriptedChatClient WhenSupervisor(string verdict) { _supervisorVerdicts.Enqueue(verdict); return this; }
        public ScriptedChatClient ThenSupervisor(string verdict) { _supervisorVerdicts.Enqueue(verdict); return this; }
        public ScriptedChatClient WhenMeeting(string script) { _meeting = script; return this; }
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
            if (!isPlanner && !isSupervisor && !isMeeting)
            {
                WorkerStarted?.TrySetResult();
                if (BeforeWorker is not null) await BeforeWorker.WaitAsync(cancellationToken);
            }
            var text = RouteByPrompt(prompt, instructions);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = CloneUsage() };
        }

        /// <summary>Routes by prompt shape: the runtime's fixed Chinese scaffolding identifies the caller.</summary>
        private string RouteByPrompt(string prompt, string? instructions)
        {
            if (instructions?.Contains("任务规划智能体", StringComparison.Ordinal) == true
                || instructions?.Contains("规划层", StringComparison.Ordinal) == true
                || prompt.Contains("规划", StringComparison.Ordinal) && !prompt.Contains("执行证据", StringComparison.Ordinal))
                return RecordPlanner(_planner ?? "[]");
            if (instructions?.Contains("监督智能体", StringComparison.Ordinal) == true || prompt.Contains("执行证据", StringComparison.Ordinal))
                return _supervisorVerdicts.Count > 0 ? _supervisorVerdicts.Dequeue() : "{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}";
            if (instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) == true || prompt.Contains("Execution evidence", StringComparison.Ordinal))
                return _meeting ?? "完成";
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
