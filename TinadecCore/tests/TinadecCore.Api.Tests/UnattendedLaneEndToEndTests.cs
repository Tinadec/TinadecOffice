using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Scenario 1 end to end: the user leaves with "test and commit once the
/// feature is done". A mid-run goal-only directive opens a lane, the lane's own
/// planning instance derives its task graph, and the mutating tools run without
/// any human decision — through each of the three release paths in turn. The
/// tools are the real TinadecTools child process and the workspace is a real
/// temporary git repository, so a passing test proves a real commit exists.
/// </summary>
public sealed class UnattendedLaneEndToEndTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string MainPlan =
        "[{\"task_key\":\"build\",\"title\":\"开发功能\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"}]";

    private const string LanePlan =
        "[{\"task_key\":\"run-tests\",\"title\":\"运行测试\",\"description\":\"运行测试并把结果写入 feature.txt\",\"success_criteria\":[\"测试输出文件存在\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"shell\"],\"priority\":1,\"risk\":\"medium\"},"
        + "{\"task_key\":\"commit\",\"title\":\"提交变更\",\"description\":\"提交全部变更\",\"success_criteria\":[\"产生提交\"],\"dependencies\":[\"run-tests\"],\"required_capabilities\":[],\"required_tools\":[\"git_commit\"],\"priority\":1,\"risk\":\"high\"}]";

    private const string LaneOpenLine =
        "收到。已登记延后执行。\nLANE_OPEN: {\"lane_key\":\"l2\",\"goal\":\"功能开发完成后测试并提交\",\"tool_scope\":[\"shell\",\"git_commit\"],\"pre_authorization\":\"用户离场前授权\"}";

    /// <summary>
    /// A complete runtime baseline with lanes enabled: the shipped default keeps
    /// <c>lanes_enabled = false</c>, so a lane-opening directive would be rejected
    /// with <c>lanes_disabled</c> without this profile.
    /// </summary>
    private const string UnattendedRuntimeToml =
        "schema_version = 1\n\n"
        + "[spawn]\nmax_depth = 2\nmax_agents_per_run = 16\nmax_parallel_workers = 4\n\n"
        + "[scheduling]\nmax_active_runs_per_session = 2\nworker_retry_limit = 2\npreserve_partial_results = true\n\n"
        + "[supervision]\nrequired_before_final = true\nmax_revision_rounds = 2\n\n"
        + "[context]\ndefault_token_budget = 8192\nrecent_message_limit = 24\noptimistic_revision = true\n\n"
        + "[memory]\ncandidate_only = true\nretrieval_limit = 8\n"
        + "allowed_scopes = [\"principal\", \"workspace\", \"project\", \"agent\"]\n"
        + "allowed_kinds = [\"fact\", \"preference\", \"decision\", \"success_pattern\", \"failure_pattern\", \"task_template\", \"supervision_rule\"]\n\n"
        + "[tools]\nprovider = \"tinadec-tools-process\"\nmutation_requires_approval = true\nserialize_workspace_writes = true\ndefault_timeout_seconds = 120\nmax_tool_rounds = 4\n\n"
        + "[triggers]\nenabled = false\ncontext_token_threshold = 0\ncompress_on_task_closed = false\nrecommend_on_task_created = false\ncurate_on_run_closed = false\ngit_steward_on_run_closed = false\n\n"
        + "[orchestration]\nlanes_enabled = true\nmax_lanes_per_run = 4\nmax_tasks_per_lane = 6\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-unattended-e2e", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DeleteDirectory(_root);
        return Task.CompletedTask;
    }

    [RequiresTinadecToolsFact]
    public async Task FullAccess_EndToEnd_GitCommitWithoutHuman()
    {
        var (client, workerGate, runId, workspace) =
            await StartUnattendedRunAsync("full-access", extraConfig: null, permissionMode: "full-access");

        workerGate.SetResult();
        await AwaitCompletionAsync(client, runId);

        await AssertUnattendedCommitAsync(client, runId, workspace, expectSource: "full_access_auto_mint");
        Assert.DoesNotContain(await ReplayEventsAsync(client, runId), e => e == "approval.auto_decided");
    }

    [RequiresTinadecToolsFact]
    public async Task PreAuthorization_EndToEnd_GitCommitWithoutHuman()
    {
        var (client, workerGate, runId, workspace) =
            await StartUnattendedRunAsync("preauth", extraConfig: null, permissionMode: null);

        // The user grants before leaving, knowing only the run id: a run-level
        // grant must reach the deferred lane's mutating tools.
        var grant = await client.PostAsJsonAsync("/api/v1/approvals/pre-authorizations", new
        {
            run_id = runId,
            tool_scope = new[] { "shell", "git_commit" },
            risk_max = "high",
            max_uses = 2,
            summary = "离场前授权：测试并提交"
        }, Json);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);

        workerGate.SetResult();
        await AwaitCompletionAsync(client, runId);

        await AssertUnattendedCommitAsync(client, runId, workspace, expectSource: "pre_authorized");
    }

    [RequiresTinadecToolsFact]
    public async Task AutoPolicy_EndToEnd_GitCommitWithoutHuman()
    {
        // The run is frozen under the auto-approve mode and the write tools
        // register as high risk, so the ceiling must be raised explicitly; the
        // safe default never auto-releases a mutating tool.
        var config = new Dictionary<string, string?>
        {
            ["TinadecApproval:AutoApproveEnabled"] = "true",
            ["TinadecApproval:AutoApproveRiskMax"] = "high"
        };
        var (client, workerGate, runId, workspace) =
            await StartUnattendedRunAsync("autopol", extraConfig: config, permissionMode: "auto-approve");

        workerGate.SetResult();
        await AwaitCompletionAsync(client, runId);

        await AssertUnattendedCommitAsync(client, runId, workspace, expectSource: "auto_policy");
    }

    // ── shared scenario scaffolding ───────────────────────────────────────────

    private async Task<(HttpClient Client, TaskCompletionSource WorkerGate, Guid RunId, string Workspace)>
        StartUnattendedRunAsync(string label, IReadOnlyDictionary<string, string?>? extraConfig, string? permissionMode)
    {
        var workspace = Path.Combine(_root, $"{label}-workspace");
        Directory.CreateDirectory(workspace);
        RunGit(workspace, "init");
        RunGit(workspace, "config", "user.email", "unattended@test.local");
        RunGit(workspace, "config", "user.name", "Unattended Lane E2E");

        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new UnattendedScriptedClient
        {
            BeforeWorker = workerGate.Task,
            WorkerStarted = workerStarted
        };

        _factory = new UnattendedFactory(_root, script, extraConfig);
        var client = _factory.CreateClient();
        await InstallOfficeAgentPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = $"{label} project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = $"{label} session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "开发功能 X",
            client_message_id = $"{label}-c-1",
            permission_mode = permissionMode
        });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        // The main worker holds the run loop open so the deferred instruction
        // lands as a directive while the run is still mid-flight.
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(45));

        var clarification = await StreamInvokeAsync(client, sessionId, new
        {
            content = "功能开发完成后测试并提交",
            client_message_id = $"{label}-c-2",
            target_run_id = runId
        });
        var meetingDelta = string.Concat(clarification
            .Where(chunk => KindOf(chunk) == "delta")
            .Select(chunk => chunk.GetProperty("delta").GetString()));
        Assert.True(meetingDelta.Contains("Orchestration registered", StringComparison.Ordinal), meetingDelta);
        return (client, workerGate, runId, workspace);
    }

    private async Task AwaitCompletionAsync(HttpClient client, Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(150);
        string? status = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
            status = orchestration.GetProperty("run").GetProperty("status").GetString();
            if (status is "completed" or "failed" or "awaiting_user") break;
            await Task.Delay(250);
        }
        Assert.True(status == "completed",
            $"The unattended run should complete without a human; final status was '{status}'. Events: {string.Join(" | ", await ReplayEventsAsync(client, runId))}");
    }

    private async Task AssertUnattendedCommitAsync(HttpClient client, Guid runId, string workspace, string expectSource)
    {
        // The lane really executed: its planner instance planned two tasks and
        // the run dispatches them through the real child process.
        var events = await ReplayEventsAsync(client, runId);
        Assert.Contains("orchestration.lane_planning", events);
        Assert.Contains("orchestration.lane_planned", events);
        Assert.Contains("orchestration.lane_opened", events);
        Assert.True(events.Contains($"approval.pre_authorized_minted:{expectSource}"),
            $"Missing mint {expectSource}. Events: {string.Join(" | ", events)}");
        if (expectSource == "auto_policy") Assert.Contains("approval.auto_decided", events);

        Assert.True(File.Exists(Path.Combine(workspace, "feature.txt")),
            $"The shell tool should have written feature.txt through the real child process. Events: {string.Join(" | ", events)}");
        var subject = ReadGitOutput(workspace, "log", "-1", "--format=%s");
        Assert.Equal("M8 unattended commit", subject.Trim());
        Assert.Equal("1", ReadGitOutput(workspace, "rev-list", "--count", "HEAD").Trim());
    }

    private async Task<List<string>> ReplayEventsAsync(HttpClient client, Guid runId)
    {
        var manager = _factory!.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(null, 0);
        var lines = events
            .Where(e => Guid.TryParse(e.RunId, out var parsed) && parsed == runId)
            .Select(e =>
            {
                if (e.EventType != "approval.pre_authorized_minted") return e.EventType;
                var payload = (JsonElement)e.Payload["payload"]!;
                return payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                    ? $"{e.EventType}:{source.GetString()}"
                    : e.EventType;
            })
            .ToList();
        return lines;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var output = RunGitRaw(workingDirectory, arguments);
        if (output.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output.Stderr}");
    }

    private static string ReadGitOutput(string workingDirectory, params string[] arguments) => RunGitRaw(workingDirectory, arguments).Stdout;

    private static (int ExitCode, string Stdout, string Stderr) RunGitRaw(string workingDirectory, string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("Could not start git.");
        if (!process.WaitForExit(30000)) throw new InvalidOperationException($"git {string.Join(' ', arguments)} timed out.");
        return (process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
    }

    private static async Task<JsonElement> InstallOfficeAgentPackAsync(HttpClient client)
    {
        var manifest = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(FindOfficeManifestPath(), Encoding.UTF8));
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(JsonCanonicalizer.Canonicalize(manifest)))
            .ToLowerInvariant();
        var envelope = JsonSerializer.SerializeToElement(new
        {
            manifest,
            integrity = new { algorithm = "sha256", digest }
        });
        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.office.agent-pack")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", $"unattended-e2e-pack-{Guid.NewGuid():N}");
        using var applyResponse = await client.SendAsync(apply);
        Assert.Equal(HttpStatusCode.Created, applyResponse.StatusCode);
        return await client.GetFromJsonAsync<JsonElement>("/api/v1/agent-packs/tinadec.office.agent-pack");
    }

    private static string FindOfficeManifestPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "desktop", "src", "agentPacks", "OfficeAgentPack", "manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("OfficeAgentPack manifest.json was not found from the test output directory.");
    }

    private static string KindOf(JsonElement chunk) => chunk.GetProperty("kind").GetString()!;

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (File.Exists(entry)) File.SetAttributes(entry, FileAttributes.Normal);
                else if (Directory.Exists(entry)) new DirectoryInfo(entry).Attributes = FileAttributes.Normal;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        // The TinadecTools child process hosts the workspace as its working
        // directory and may outlive the factory disposal by a moment, so every
        // delete attempt swallows the transient lock instead of failing the test.
        for (var attempt = 0; attempt < 20 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, true); break; }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }

    private sealed record ActiveInvoke(Task<JsonElement> Acknowledgement, Task<List<JsonElement>> Completion);

    private static ActiveInvoke StartStreamingInvoke(HttpClient client, Guid sessionId, object body)
    {
        var acknowledgement = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
            var chunks = new List<JsonElement>();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/invoke-stream")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
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
                acknowledgement.TrySetException(new InvalidDataException(
                    $"Invoke stream closed before its acknowledgement. Chunks: {string.Join(" | ", chunks.Select(chunk => chunk.ToString()))}"));
            }
            return chunks;
        });
        return new ActiveInvoke(acknowledgement.Task, completion);
    }

    private static async Task<List<JsonElement>> StreamInvokeAsync(HttpClient client, Guid sessionId, object body)
    {
        var active = StartStreamingInvoke(client, sessionId, body);
        return await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    // ── host ──────────────────────────────────────────────────────────────────

    private sealed class UnattendedFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly UnattendedScriptedClient _client;
        private readonly IReadOnlyDictionary<string, string?>? _extra;

        public UnattendedFactory(string root, UnattendedScriptedClient client, IReadOnlyDictionary<string, string?>? extra)
        {
            _root = root;
            _client = client;
            _extra = extra;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var tomlPath = Path.Combine(_root, "unattended-agent-runtime.toml");
                File.WriteAllText(tomlPath, UnattendedRuntimeToml, Encoding.UTF8);
                var values = new Dictionary<string, string?>
                {
                    ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                    ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                    ["TinadecAgent:ProfileConfigPath"] = tomlPath,
                    ["Logging:LogLevel:Default"] = "Warning"
                };
                if (_extra is not null)
                {
                    foreach (var pair in _extra) values[pair.Key] = pair.Value;
                }
                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAgentChatClientFactory>(new UnattendedScriptedFactory(_client));
                services.AddSingleton<ISecretStore>(new TestModelSecretStore());
            });
        }
    }

    private sealed class UnattendedScriptedFactory(UnattendedScriptedClient client) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "fake", ApiKey = "x", ModelId = "openai/fake" });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default)
            => Task.FromResult<IChatClient>(client);
    }

    // ── scripted model ────────────────────────────────────────────────────────

    /// <summary>
    /// Routes like the production scripts: fixed Chinese scaffolding identifies
    /// the caller, and worker turns are keyed by task title so each lane task
    /// emits exactly one tool call before reporting text. The lane planner is
    /// routed by the "Lane: l2" marker its instructions carry.
    /// </summary>
    private sealed class UnattendedScriptedClient : IChatClient
    {
        private readonly Dictionary<string, int> _workerTurnsByTitle = new(StringComparer.Ordinal);
        private int _meetingOnceUsed;

        public string MainPlan { private get; set; } = UnattendedLaneEndToEndTests.MainPlan;
        public string LanePlan { private get; set; } = UnattendedLaneEndToEndTests.LanePlan;
        public string LaneOpenLine { private get; set; } = UnattendedLaneEndToEndTests.LaneOpenLine;
        public Task? BeforeWorker { private get; set; }
        public TaskCompletionSource? WorkerStarted { private get; set; }
        public int WorkerCalls;

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join('\n', messages.Select(m => m.Text));
            var instructions = options?.Instructions;

            if (instructions?.Contains("You are the capability advisor", StringComparison.Ordinal) == true)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"recommendations\":[]}"));
            if (instructions?.Contains("You are the git steward", StringComparison.Ordinal) == true)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Change scope: scripted review."));
            if (instructions?.Contains("You are the context compression agent", StringComparison.Ordinal) == true
                || instructions?.Contains("You are the experience curator", StringComparison.Ordinal) == true)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));

            var isPlanner = instructions?.Contains("任务规划智能体", StringComparison.Ordinal) == true
                || instructions?.Contains("规划层", StringComparison.Ordinal) == true
                || prompt.Contains("规划", StringComparison.Ordinal) && !prompt.Contains("执行证据", StringComparison.Ordinal);
            if (isPlanner)
            {
                var lane = LaneKeyFrom(prompt) ?? LaneKeyFrom(instructions);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    string.Equals(lane, "l2", StringComparison.Ordinal) ? LanePlan : MainPlan));
            }

            if (instructions?.Contains("监督智能体", StringComparison.Ordinal) == true || prompt.Contains("执行证据", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    "{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}"));

            if (instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) == true || prompt.Contains("Execution evidence", StringComparison.Ordinal))
            {
                // First meeting turn is the user's deferred instruction — it must
                // carry the LANE_OPEN line; every later one (run finalization)
                // is plain completion text.
                var firstMeetingTurn = Interlocked.Exchange(ref _meetingOnceUsed, 1) == 0;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, firstMeetingTurn ? LaneOpenLine : "全部完成。"));
            }

            WorkerStarted?.TrySetResult();
            if (BeforeWorker is not null) await BeforeWorker.WaitAsync(cancellationToken);
            Interlocked.Increment(ref WorkerCalls);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, WorkerTurn(prompt, instructions)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var chunk in (response.Text ?? "完成").Chunk(2))
            {
                await Task.Delay(1, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, new string(chunk));
            }
        }

        /// <summary>
        /// First turn of a lane task emits its tool call; every later turn of the
        /// same task reports completion text. The task title travels in the
        /// worker's instructions (not the conversation messages), so routing
        /// matches on both — and keying by title keeps it stable no matter
        /// which order the lanes dispatch in.
        /// </summary>
        private AIContent[] WorkerTurn(string prompt, string? instructions)
        {
            var routingText = $"{prompt}\n{instructions}";
            if (routingText.Contains("运行测试", StringComparison.Ordinal) && FirstTurn("运行测试"))
            {
                return [new FunctionCallContent("call-shell", "shell", new Dictionary<string, object?>
                {
                    ["command"] = "echo m8-e2e> feature.txt"
                })];
            }
            if (routingText.Contains("提交变更", StringComparison.Ordinal) && FirstTurn("提交变更"))
            {
                return [new FunctionCallContent("call-commit", "git_commit", new Dictionary<string, object?>
                {
                    ["message"] = "M8 unattended commit",
                    ["include_all"] = true,
                    ["confirm_commit"] = "yes"
                })];
            }
            return [new TextContent("完成")];
        }

        private bool FirstTurn(string title)
        {
            lock (_workerTurnsByTitle)
            {
                var turn = _workerTurnsByTitle.GetValueOrDefault(title) + 1;
                _workerTurnsByTitle[title] = turn;
                return turn == 1;
            }
        }

        private static string? LaneKeyFrom(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            const string marker = "Lane: ";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            var end = text.IndexOf('\n', start);
            return (end < 0 ? text[start..] : text[start..end]).Trim();
        }
    }
}
