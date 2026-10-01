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
/// Unattended release paths end to end (phase 2 shape): the user leaves with
/// "test and commit once the feature is done" — a single main-run task graph
/// (build, run-tests, commit) whose mutating tools run without any human
/// decision. (The phase 1 lane vehicle is gone: graph tiers freeze with lanes
/// rejected, so the deferred follow-up became part of the main plan.) — through each of the three release paths in turn. The
/// tools are the real TinadecTools child process and the workspace is a real
/// temporary git repository, so a passing test proves a real commit exists.
/// </summary>
public sealed class UnattendedEndToEndTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string MainPlan =
        "[{\"task_key\":\"build\",\"title\":\"\u5f00\u53d1\u529f\u80fd\",\"description\":\"\",\"success_criteria\":[\"\u5b8c\u6210\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"},"
        + "{\"task_key\":\"run-tests\",\"title\":\"\u8fd0\u884c\u6d4b\u8bd5\",\"description\":\"\u8fd0\u884c\u6d4b\u8bd5\u5e76\u628a\u7ed3\u679c\u5199\u5165 feature.txt\",\"success_criteria\":[\"\u6d4b\u8bd5\u8f93\u51fa\u6587\u4ef6\u5b58\u5728\"],\"dependencies\":[\"build\"],\"required_capabilities\":[],\"required_tools\":[\"shell\"],\"priority\":2,\"risk\":\"medium\"},"
        + "{\"task_key\":\"commit\",\"title\":\"\u63d0\u4ea4\u53d8\u66f4\",\"description\":\"\u63d0\u4ea4\u5168\u90e8\u53d8\u66f4\",\"success_criteria\":[\"\u4ea7\u751f\u63d0\u4ea4\"],\"dependencies\":[\"run-tests\"],\"required_capabilities\":[],\"required_tools\":[\"git_commit\"],\"priority\":3,\"risk\":\"high\"}]";

    /// <summary>
    /// The same plan with <c>write_file</c> in place of <c>shell</c>: worker
    /// selection is driven by <c>required_tools</c>, so a leg whose script emits
    /// write_file must declare write_file or it would be routed to a shell-only
    /// worker whose face cannot authorize the call.
    /// </summary>
    private const string MainPlanWithWriteFile =
        "[{\"task_key\":\"build\",\"title\":\"\u5f00\u53d1\u529f\u80fd\",\"description\":\"\",\"success_criteria\":[\"\u5b8c\u6210\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"},"
        + "{\"task_key\":\"run-tests\",\"title\":\"\u8fd0\u884c\u6d4b\u8bd5\",\"description\":\"\u8fd0\u884c\u6d4b\u8bd5\u5e76\u628a\u7ed3\u679c\u5199\u5165 feature.txt\",\"success_criteria\":[\"\u6d4b\u8bd5\u8f93\u51fa\u6587\u4ef6\u5b58\u5728\"],\"dependencies\":[\"build\"],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":2,\"risk\":\"medium\"},"
        + "{\"task_key\":\"commit\",\"title\":\"\u63d0\u4ea4\u53d8\u66f4\",\"description\":\"\u63d0\u4ea4\u5168\u90e8\u53d8\u66f4\",\"success_criteria\":[\"\u4ea7\u751f\u63d0\u4ea4\"],\"dependencies\":[\"run-tests\"],\"required_capabilities\":[],\"required_tools\":[\"git_commit\"],\"priority\":3,\"risk\":\"high\"}]";

    // B6: shell is a human-only tool and can never be auto-approved, so the
    // auto-policy leg drives the same unattended scenario with write_file.
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
        + "[triggers]\nenabled = false\ncontext_token_threshold = 0\ncompress_on_task_closed = false\nrecommend_on_task_created = false\ncurate_on_run_closed = false\ngit_steward_on_run_closed = false\n";

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
        // safe default never auto-releases a mutating tool. B6: shell is a
        // human-only tool and can never be auto-approved, so this leg drives
        // the same unattended scenario with write_file + git_commit.
        var config = new Dictionary<string, string?>
        {
            ["TinadecApproval:AutoApproveEnabled"] = "true",
            ["TinadecApproval:AutoApproveRiskMax"] = "high"
        };
        var (client, workerGate, runId, workspace) =
            await StartUnattendedRunAsync("autopol", extraConfig: config, permissionMode: "auto-approve", useWriteFileForTests: true);

        workerGate.SetResult();
        await AwaitCompletionAsync(client, runId);

        await AssertUnattendedCommitAsync(client, runId, workspace, expectSource: "auto_policy");
    }

    // ── shared scenario scaffolding ───────────────────────────────────────────

    private async Task<(HttpClient Client, TaskCompletionSource WorkerGate, Guid RunId, string Workspace)>
        StartUnattendedRunAsync(string label, IReadOnlyDictionary<string, string?>? extraConfig, string? permissionMode, bool useWriteFileForTests = false)
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
            WorkerStarted = workerStarted,
            UseWriteFileForTests = useWriteFileForTests,
            // The scripted tool call must match the plan's required_tools: worker
            // selection routes by that list, so a write_file call under a shell
            // requirement lands on a worker whose face cannot authorize it.
            MainPlan = useWriteFileForTests ? MainPlanWithWriteFile : MainPlan
        };

        _factory = new UnattendedFactory(_root, script, extraConfig);
        var client = _factory.CreateClient();
        var packDetail = await InstallLifecycleFixturePackAsync(client);
        var modeVersionId = ModeVersionId(packDetail, "default-mode");

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = $"{label} project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = $"{label} session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "开发功能 X",
            client_message_id = $"{label}-c-1",
            permission_mode = permissionMode,
            mode_version_id = modeVersionId
        });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        // The main worker holds the run loop open so the deferred instruction
        // lands as a directive while the run is still mid-flight.
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(45));

        return (client, workerGate, runId, workspace);
    }

    private async Task AwaitCompletionAsync(HttpClient client, Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(150);
        string? status = null;
        JsonElement? lastOrchestration = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
            lastOrchestration = orchestration;
            status = orchestration.GetProperty("run").GetProperty("status").GetString();
            if (status is "completed" or "failed" or "awaiting_user") break;
            await Task.Delay(250);
        }
        if (status != "completed")
        {
            var steps = lastOrchestration is { } body && body.TryGetProperty("step_results", out var results)
                ? string.Join(" | ", results.EnumerateArray().Select(step =>
                    $"{step.GetProperty("status").GetString()}: {step.GetProperty("summary").GetString()}"))
                : "(no step results)";
            Assert.Fail(
                $"The unattended run should complete without a human; final status was '{status}'. "
                + $"Steps: {steps}. Events: {string.Join(" | ", await ReplayEventsAsync(client, runId))}");
        }
    }

    private async Task AssertUnattendedCommitAsync(HttpClient client, Guid runId, string workspace, string expectSource)
    {
        // The unattended tool chain really executed through the real child process.
        var events = await ReplayEventsAsync(client, runId);
        var steps = await DescribeStepsAsync(client, runId);
        Assert.True(events.Contains($"approval.pre_authorized_minted:{expectSource}"),
            $"Missing mint {expectSource}. Steps: {steps}. Events: {string.Join(" | ", events)}");
        if (expectSource == "auto_policy") Assert.Contains("approval.auto_decided", events);

        Assert.True(File.Exists(Path.Combine(workspace, "feature.txt")),
            $"The unattended tool chain should have written feature.txt through the real child process. Steps: {steps}. Events: {string.Join(" | ", events)}");
        var subject = ReadGitOutput(workspace, "log", "-1", "--format=%s");
        Assert.Equal("M8 unattended commit", subject.Trim());
        Assert.Equal("1", ReadGitOutput(workspace, "rev-list", "--count", "HEAD").Trim());
    }

    /// <summary>Task outcomes with their failure summaries, for failure messages.</summary>
    private static async Task<string> DescribeStepsAsync(HttpClient client, Guid runId)
    {
        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        if (!orchestration.TryGetProperty("step_results", out var results)) return "(no step results)";
        return string.Join(" | ", results.EnumerateArray().Select(step =>
            $"{step.GetProperty("status").GetString()}: {step.GetProperty("summary").GetString()}"));
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

    private static async Task<JsonElement> InstallLifecycleFixturePackAsync(HttpClient client)
    {
        var manifest = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(FindFixtureManifestPath(), Encoding.UTF8));
        // Core validates the digest over its DTO round-trip of the submitted
        // manifest (unknown members dropped, absent optionals omitted), so the
        // fixture must digest the same round-tripped shape — not the raw bytes.
        var serverOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var roundTripped = JsonSerializer.Deserialize<TinadecCore.Contracts.Dtos.AgentPackManifestDto>(
            manifest.GetRawText(), serverOptions);
        var serverElement = JsonSerializer.SerializeToElement(roundTripped, serverOptions);
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(JsonCanonicalizer.Canonicalize(serverElement)))
            .ToLowerInvariant();
        var envelope = JsonSerializer.SerializeToElement(new
        {
            manifest,
            integrity = new { algorithm = "sha256", digest }
        });
        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-packs/{UnattendedPackId}")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", $"unattended-e2e-pack-{Guid.NewGuid():N}");
        using var applyResponse = await client.SendAsync(apply);
        Assert.Equal(HttpStatusCode.Created, applyResponse.StatusCode);
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-packs/{UnattendedPackId}");
    }

    private const string UnattendedPackId = "tinadec.tests.unattended-agent-pack";

    /// <summary>
    /// The published mode version the unattended runs bind to. The fixture declares
    /// no edges (free_form tier) and every execution node carries the workspace
    /// write envelope the mutating tool chain needs; binding the version explicitly
    /// keeps the scenario independent of whatever the workspace default holds.
    /// </summary>
    private static Guid ModeVersionId(JsonElement packDetail, string resourceKey) =>
        packDetail.GetProperty("resources").EnumerateArray()
            .Single(resource => resource.GetProperty("kind").GetString() == "mode"
                && resource.GetProperty("resource_key").GetString() == resourceKey)
            .GetProperty("version_id").GetGuid();

    private static string FindFixtureManifestPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "TinadecCore", "tests", "TinadecCore.Api.Tests", "Fixtures", "unattended-agent-pack.manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("The unattended Agent Pack fixture manifest was not found from the test output directory.");
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

    private ActiveInvoke StartStreamingInvoke(HttpClient client, Guid sessionId, object body)
    {
        var acknowledgement = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
        var chunks = new List<JsonElement>();
        using var admissionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/interactions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var admissionResponse = await client.SendAsync(admissionRequest, HttpCompletionOption.ResponseHeadersRead);
        await _factory!.AssertStatusAsync(admissionResponse, HttpStatusCode.Created, "Interaction admission");
        var receipt = await admissionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = receipt.GetProperty("run_id").GetString();
        var cursor = receipt.TryGetProperty("stream_cursor", out var sc) ? sc.GetInt64() : 0;
        var turnId = receipt.TryGetProperty("turn_id", out var tid) ? tid.GetString() : null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/stream?after_seq=0&turn_id={turnId}")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, "Run stream");
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
    /// the caller, and worker turns are keyed by task title so each task emits
    /// exactly one tool call before reporting text.
    /// </summary>
    private sealed class UnattendedScriptedClient : IChatClient
    {
        private readonly Dictionary<string, int> _workerTurnsByTitle = new(StringComparer.Ordinal);

        public string MainPlan { private get; set; } = UnattendedEndToEndTests.MainPlan;
        public Task? BeforeWorker { private get; set; }
        public TaskCompletionSource? WorkerStarted { private get; set; }
        public bool UseWriteFileForTests { private get; set; }
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
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, MainPlan));
            }

            if (instructions?.Contains("监督智能体", StringComparison.Ordinal) == true || prompt.Contains("执行证据", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    "{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[],\"criteria_verdicts\":["
                    + "{\"task_key\":\"build\",\"criterion\":\"完成\",\"satisfied\":true,\"evidence\":\"worker completion evidence recorded\"},"
                    + "{\"task_key\":\"run-tests\",\"criterion\":\"测试输出文件存在\",\"satisfied\":true,\"evidence\":\"feature.txt tool execution completed\"},"
                    + "{\"task_key\":\"commit\",\"criterion\":\"产生提交\",\"satisfied\":true,\"evidence\":\"git_commit tool execution completed\"}]}"));

            if (instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) == true || prompt.Contains("Execution evidence", StringComparison.Ordinal))
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "全部完成。"));
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
            foreach (var chunk in response.ToChatResponseUpdates())
            {
                await Task.Delay(1, cancellationToken);
                yield return chunk;
            }
        }

        /// <summary>
        /// First turn of a task emits its tool call; every later turn of the same
        /// task reports completion text. The task title travels in the worker's
        /// instructions (not the conversation messages), so routing matches on
        /// both — and keying by title keeps it stable.
        /// </summary>
        private AIContent[] WorkerTurn(string prompt, string? instructions)
        {
            var routingText = $"{prompt}\n{instructions}";
            if (routingText.Contains("运行测试", StringComparison.Ordinal) && FirstTurn("运行测试"))
            {
                // B6: the auto-policy leg cannot use shell (human-only), so it
                // writes the evidence file through write_file instead.
                if (UseWriteFileForTests)
                {
                    return [new FunctionCallContent("call-write", "write_file", new Dictionary<string, object?>
                    {
                        ["filepath"] = "feature.txt",
                        ["content"] = "m8-e2e"
                    })];
                }
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
            return [new TextContent(CompletedOutcome(routingText))];
        }

        private static string CompletedOutcome(string routingText)
        {
            if (routingText.Contains("运行测试", StringComparison.Ordinal))
            {
                return "测试输出文件已生成。\n"
                    + "CRITERION_EVIDENCE: 测试输出文件存在 || feature.txt 的工具执行已完成。\n"
                    + "TASK_OUTCOME: completed";
            }
            if (routingText.Contains("提交变更", StringComparison.Ordinal))
            {
                return "提交已创建。\n"
                    + "CRITERION_EVIDENCE: 产生提交 || git_commit 工具执行已完成。\n"
                    + "TASK_OUTCOME: completed";
            }
            return "任务已完成。\n"
                + "CRITERION_EVIDENCE: 完成 || 脚本任务完成。\n"
                + "TASK_OUTCOME: completed";
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

    }
}
