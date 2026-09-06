using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// End-to-end tool-chain tests: the full-duplex engine talks to a real
/// TinadecTools child process through the production manifest resolver and
/// dispatcher, pauses the run on an approval decision, resumes it after the
/// decision, and the worker's write_file call lands in the project workspace.
/// </summary>
public sealed class ToolChainEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-toolchain-api-tests", Guid.NewGuid().ToString("N"));
    private ToolChainFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
        }
    }

    [RequiresTinadecToolsFact]
    public async Task WorkerWriteFile_RequestsApproval_ApprovedThenExecuted_RunCompletes()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"write-probe\",\"title\":\"写探针文件\",\"description\":\"创建 probe.txt\",\"success_criteria\":[\"文件存在\"],\"dependencies\":[],\"required_capabilities\":[\"tool.file\"],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("文件已写入。");

        _factory = new ToolChainFactory(_root, script);
        var client = _factory.CreateClient();
        var packDetail = await InstallOfficeAgentPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Tool project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Tool session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = "tool-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        List<JsonElement> chunks;
        try
        {
            chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException ex)
        {
            var orchestrationState = await client.GetStringAsync($"/api/v1/runs/{runId}/orchestration");
            var executionState = await client.GetStringAsync($"/api/v1/sessions/{sessionId}/tool-executions");
            throw new TimeoutException(
                $"Run {runId} did not complete after approval.\nOrchestration: {orchestrationState}\nToolExecutions: {executionState}",
                ex);
        }
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal(runId, done.GetProperty("run_id").GetGuid());

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(workspace, "probe.txt")));

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
        var expectedVersions = packDetail.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("kind").GetString() == "agent")
            .ToDictionary(
                resource => resource.GetProperty("resource_key").GetString()!,
                resource => resource.GetProperty("version_id").GetGuid(),
                StringComparer.Ordinal);
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage");
        Assert.Contains(lineage!, instance =>
            instance.GetProperty("generated").GetBoolean()
            && HasAgentVersion(instance, expectedVersions["worker.file"]));
        Assert.Contains(lineage!, instance =>
            instance.GetProperty("role").GetString() == "quality_controller"
            && HasAgentVersion(instance, expectedVersions["supervisor"]));
        Assert.Contains(lineage!, instance =>
            instance.GetProperty("role").GetString() == "session_coordinator"
            && HasAgentVersion(instance, expectedVersions["meeting"]));
    }

    private static bool HasAgentVersion(JsonElement instance, Guid expectedVersionId) =>
        instance.TryGetProperty("agent_version_id", out var versionId)
        && versionId.ValueKind == JsonValueKind.String
        && versionId.TryGetGuid(out var actualVersionId)
        && actualVersionId == expectedVersionId;

    /// <summary>
    /// The generic <see cref="IToolProvider"/> contract must be decoupled from the
    /// TinadecTools child process: with the provider replaced in DI, the full
    /// approval -> resume -> dispatch loop completes and Core never touches the
    /// real process (the fake writes nothing to the workspace).
    /// </summary>
    [Fact]
    public async Task WorkerWriteFile_ThroughInProcessFakeProvider_DispatchLoopCompletesWithoutTinadecTools()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"write-probe\",\"title\":\"写探针文件\",\"description\":\"创建 probe.txt\",\"success_criteria\":[\"文件存在\"],\"dependencies\":[],\"required_capabilities\":[\"tool.file\"],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("文件已写入。");

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallOfficeAgentPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Fake provider project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Fake provider session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = "fake-provider-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal(runId, done.GetProperty("run_id").GetGuid());

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("write_file", Assert.Single(provider.ReceivedToolIds));
        Assert.True(provider.ReceivedApproved);
        Assert.False(File.Exists(Path.Combine(workspace, "probe.txt")));

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration").ConfigureAwait(false);
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
    }

    /// <summary>
    /// conversation.ask runs the full pipeline on its own narrowed roster: the run
    /// is admitted under the ask ModeVersion, the planner's research task lands on
    /// worker.browser (the only ask-roster worker covering it), and the run completes
    /// even though the roster carries no supervisor — the review gate is skipped with
    /// an audit event instead of failing the run (previously InvalidDataException).
    /// </summary>
    [Fact]
    public async Task AskMode_RunsOnNarrowedRoster_BrowserWorkerCompletesWithoutSupervisor()
    {
        var workspace = Path.Combine(_root, "workspace-ask");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"ask-research\",\"title\":\"查一下X的定义\",\"description\":\"回答用户的问题\",\"success_criteria\":[\"给出带来源的定义\"],\"dependencies\":[],\"required_capabilities\":[\"tool.search\"],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorkerText("检索结论：X 是一种示例概念（来源：example.com）。")
            .WhenMeeting("结论：X 是一种示例概念。");

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        var packDetail = await InstallOfficeAgentPackAsync(client);
        var askModeVersion = packDetail.GetProperty("resources").EnumerateArray()
            .Single(resource => resource.GetProperty("kind").GetString() == "mode"
                && resource.GetProperty("resource_key").GetString() == "conversation.ask")
            .GetProperty("version_id").GetGuid();
        var expectedVersions = packDetail.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("kind").GetString() == "agent")
            .ToDictionary(
                resource => resource.GetProperty("resource_key").GetString()!,
                resource => resource.GetProperty("version_id").GetGuid(),
                StringComparer.Ordinal);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Ask project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Ask session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        // Mode switching is the interactions endpoint's job: it validates
        // agent_mode and persists the ask ModeVersion onto the session.
        // invoke-stream alone only labels the run; the frozen roster always
        // comes from the session's persisted mode.
        using var modeSwitch = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions",
            new { content = "X是什么？", client_message_id = "ask-mode-1", agent_mode = "ask", dispatch_mode = "queued" });
        Assert.True(modeSwitch.IsSuccessStatusCode, $"ask mode switch failed: {modeSwitch.StatusCode}");

        var active = StartStreamingInvoke(client, sessionId, new { content = "X是什么？", client_message_id = "ask-e2e-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal(runId, done.GetProperty("run_id").GetGuid());

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration").ConfigureAwait(false);
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
        // Note: run.agent_mode records the invocation label (auto by default);
        // the executed roster always comes from the session's persisted mode,
        // which the interactions call above switched to conversation.ask.
        var decisions = orchestration.GetProperty("supervision_findings").EnumerateArray()
            .Select(f => f.GetProperty("decision").GetString()).Where(d => d is not null).ToArray();
        Assert.Equal(new[] { "pass" }, decisions);

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        Assert.Single(events, e => e.EventType == "supervision.skipped"
            && string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase));

        var sessions = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/sessions").ConfigureAwait(false);
        var sessionAfter = sessions!.Single(session => session.GetProperty("id").GetGuid() == sessionId);
        Assert.Equal(askModeVersion, sessionAfter.GetProperty("mode_version_id").GetGuid());

        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var worker = Assert.Single(lineage!, instance => instance.GetProperty("generated").GetBoolean());
        Assert.True(HasAgentVersion(worker, expectedVersions["worker.browser"]));
        Assert.DoesNotContain(lineage!, instance => instance.GetProperty("role").GetString() == "quality_controller");
        Assert.Contains(lineage!, instance => instance.GetProperty("role").GetString() == "session_coordinator"
            && HasAgentVersion(instance, expectedVersions["meeting"]));
    }

    /// <summary>
    /// The dual-layer split must be enforced as permission policy, not as an engine
    /// convention.  The shipped Office pack still publishes "*" for the meeting agent, so
    /// the layer rule is the only authority that can deny a governance-layer tool call;
    /// the execution worker on the same task must still be allowed.
    /// </summary>
    [Fact]
    public async Task OperationLayerToolInvoke_IsDeniedByLayerPolicy_WhileWorkerIsAllowed()
    {
        var (provider, client, _, runId, approvalId, _) = await StartFakeProviderRunAsync("layer-policy");
        Assert.Equal(0, provider.CallCount);
        Assert.NotEqual(Guid.Empty, approvalId);

        var resolver = _factory!.Services.GetRequiredService<IAuthorizationContextResolver>();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var meeting = Assert.Single(lineage, item => item.GetProperty("role").GetString() == "session_coordinator");
        var worker = Assert.Single(lineage, item => item.GetProperty("generated").GetBoolean());
        var taskId = worker.GetProperty("task_id").GetGuid();
        var claim = new CapabilityClaim("tool.file", "tool.invoke", "tool://write_file");

        var governanceBoundaries = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId,
            meeting.GetProperty("id").GetGuid(), claim, runId, taskId)).ConfigureAwait(false);
        var denial = Assert.Single(governanceBoundaries, boundary => boundary.Name == "operation_layer_cannot_invoke_tools");
        Assert.Equal("deny", Assert.Single(denial.Rules).Effect);

        var workerBoundaries = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId,
            worker.GetProperty("id").GetGuid(), claim, runId, taskId)).ConfigureAwait(false);
        Assert.DoesNotContain(workerBoundaries, boundary => boundary.Name == "operation_layer_cannot_invoke_tools");
        Assert.NotEmpty(workerBoundaries.Where(boundary => boundary.Name != "run")
            .Where(boundary => boundary.Rules.Any(rule => rule.Effect == "allow")));
    }

    /// <summary>
    /// A wildcard is legal only in a published declaration, where it names a delegable
    /// envelope.  A derived instance must carry a concrete grant, otherwise a planner that
    /// wrote "*" would mint a worker holding every tool in the manifest.
    /// </summary>
    [Fact]
    public async Task DerivedAgentCannotCarryWildcardToolGrant()
    {
        var (_, client, _, runId, _, _) = await StartFakeProviderRunAsync("wildcard-spawn");
        var instances = _factory!.Services.GetRequiredService<IAgentInstanceService>();
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var planner = Assert.Single(lineage, item => item.GetProperty("role").GetString() == "execution_coordinator");

        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => instances.SpawnAsync(new AgentSpawnRequest(
            planner.GetProperty("id").GetGuid(), "越权探测", ["观察"], ["task_context"], null,
            ["*"], ["workspace"], 4096))).ConfigureAwait(false);
        Assert.Contains("wildcard", refused.Message, StringComparison.OrdinalIgnoreCase);

        var narrowed = await instances.SpawnAsync(new AgentSpawnRequest(
            planner.GetProperty("id").GetGuid(), "收窄派生", ["观察"], ["task_context"], null,
            ["write_file"], ["workspace"], 4096)).ConfigureAwait(false);
        Assert.Equal(["write_file"], narrowed.AllowedTools);
    }

    /// <summary>
    /// The <c>agent_version</c> boundary must read the tool scope the immutable published
    /// version actually declares (<c>tool_scope</c>) and treat it as final: a tool outside
    /// the specialist's own scope is denied even though the run-frozen roster copy is derived
    /// from the same data and could otherwise answer more broadly.
    /// </summary>
    [Fact]
    public async Task AgentVersionBoundary_UsesDeclaredToolScope_AsFinalAnswer()
    {
        var (_, client, _, runId, _, _) = await StartFakeProviderRunAsync("agent-version-scope");
        var resolver = _factory!.Services.GetRequiredService<IAuthorizationContextResolver>();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var worker = Assert.Single(lineage, item => item.GetProperty("generated").GetBoolean());
        var workerId = worker.GetProperty("id").GetGuid();
        var taskId = worker.GetProperty("task_id").GetGuid();

        var boundaries = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId, workerId,
            new CapabilityClaim("tool.file", "tool.invoke", "tool://write_file"), runId, taskId)).ConfigureAwait(false);
        var granted = Assert.Single(boundaries, boundary => boundary.Name == "agent_version");
        Assert.Equal("allow", Assert.Single(granted.Rules).Effect);

        var outside = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId, workerId,
            new CapabilityClaim("tool.git", "tool.invoke", "tool://git_commit"), runId, taskId)).ConfigureAwait(false);
        var refused = Assert.Single(outside, boundary => boundary.Name == "agent_version");
        Assert.Equal("deny", Assert.Single(refused.Rules).Effect);
    }

    /// <summary>
    /// Drives one full-duplex run up to the moment its worker write is parked on an
    /// approval, so permission assertions see real frozen bindings while the root and
    /// worker instances are still live and unreleased.
    /// </summary>
    private async Task<(FakeToolProvider Provider, HttpClient Client, Guid SessionId, Guid RunId, Guid ApprovalId, ActiveInvoke Active)> StartFakeProviderRunAsync(string label)
    {
        var workspace = Path.Combine(_root, "workspace-" + label);
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"write-probe\",\"title\":\"写探针文件\",\"description\":\"创建 probe.txt\",\"success_criteria\":[\"文件存在\"],\"dependencies\":[],\"required_capabilities\":[\"tool.file\"],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("文件已写入。");

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallOfficeAgentPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = label + " project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = label + " session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = label + "-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        return (provider, client, sessionId, runId, approvalId, active);
    }

    /// <summary>
    /// The git steward operational role must fire only for runs that touched
    /// git tools: this test drives a real git_status call through the approval
    /// gate and waits for the post-run steward bypass call.
    /// </summary>
    [RequiresTinadecToolsFact]
    public async Task GitSteward_ReviewsRunsThatTouchGitTools()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        RunGit(workspace, "init");
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"git-status-check\",\"title\":\"查看 Git 状态\",\"description\":\"\",\"success_criteria\":[\"输出状态\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"git_status\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorkerTool("git_status")
            .WhenWorkerFollowUp("git 状态已检查。")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("Git 状态检查完成。");

        _factory = new ToolChainFactory(_root, script);
        var client = _factory.CreateClient();
        await InstallOfficeAgentPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Git steward project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Git steward session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "查看 git 状态", client_message_id = "git-steward-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        // The steward bypass runs after the terminal done chunk is durable.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (script.StewardCalls == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(200);
        Assert.True(script.StewardCalls >= 1, "The git steward should review runs that touched git tools.");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
    }

    private sealed class FakeToolProvider : IToolProvider
    {
        private readonly object _lock = new();

        public int CallCount { get; private set; }
        public List<string> ReceivedToolIds { get; } = [];
        public bool ReceivedApproved { get; private set; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                CallCount++;
                ReceivedToolIds.Add(request.ToolId);
                ReceivedApproved |= request.Approved;
            }
            return Task.FromResult(new ToolWireResponseDto
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Result = JsonSerializer.SerializeToElement(new { ok = true, tool = request.ToolId })
            });
        }

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static ToolManifestDto CreateManifest()
        {
            var tools = new List<ToolManifestEntryDto>
            {
                new()
                {
                    Id = "write_file",
                    Description = "In-process fake write probe",
                    RequiresApproval = true,
                    Risk = "medium",
                    MutatesWorkspace = true
                }
            };
            return new ToolManifestDto
            {
                ProtocolVersion = 2,
                ManifestHash = ToolManifestHasher.Compute(tools),
                Tools = tools
            };
        }
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
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", "tool-chain-office-pack-install");
        using var applyResponse = await client.SendAsync(apply);
        Assert.Equal(HttpStatusCode.Created, applyResponse.StatusCode);
        return await client.GetFromJsonAsync<JsonElement>("/api/v1/agent-packs/tinadec.office.agent-pack");
    }

    private static string FindOfficeManifestPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "apps",
                "desktop",
                "src",
                "agentPacks",
                "OfficeAgentPack",
                "manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("OfficeAgentPack manifest.json was not found from the test output directory.");
    }

    private static async Task<Guid> WaitForPendingApprovalAsync(HttpClient client, Guid sessionId, Guid runId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var approvals = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/approvals?status=pending") ?? [];
            var match = approvals.FirstOrDefault(item =>
                item.GetProperty("run_id").ValueKind == JsonValueKind.String
                && Guid.TryParse(item.GetProperty("run_id").GetString(), out var candidate) && candidate == runId);
            if (match.ValueKind != JsonValueKind.Undefined) return match.GetProperty("id").GetGuid();
            await Task.Delay(300);
        }
        var orchestration = await client.GetStringAsync($"/api/v1/runs/{runId}/orchestration");
        var executions = await client.GetStringAsync($"/api/v1/sessions/{sessionId}/tool-executions");
        throw new TimeoutException($"No pending approval appeared for run {runId} within {timeout}.\nOrchestration: {orchestration}\nToolExecutions: {executions}");
    }

    private static string KindOf(JsonElement chunk) => chunk.GetProperty("kind").GetString()!;
    private static Guid RunIdOf(JsonElement chunk) => chunk.GetProperty("run_id").GetGuid();

    private sealed record ActiveInvoke(Task<JsonElement> Acknowledgement, Task<List<JsonElement>> Completion);

    private static ActiveInvoke StartStreamingInvoke(HttpClient client, Guid sessionId, object body)
    {
        var acknowledgement = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
            try
            {
                var chunks = new List<JsonElement>();
                using var admissionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/interactions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                using var admissionResponse = await client.SendAsync(admissionRequest, HttpCompletionOption.ResponseHeadersRead);
                if (admissionResponse.StatusCode != HttpStatusCode.Created)
                {
                    var admissionBody = await admissionResponse.Content.ReadAsStringAsync();
                    throw new InvalidDataException($"Interaction admission rejected with {(int)admissionResponse.StatusCode}: {admissionBody}");
                }
                var receipt = await admissionResponse.Content.ReadFromJsonAsync<JsonElement>();
                var runId = receipt.GetProperty("run_id").GetString();
                var cursor = receipt.TryGetProperty("stream_cursor", out var sc) ? sc.GetInt64() : 0;
                var turnId = receipt.TryGetProperty("turn_id", out var tid) ? tid.GetString() : null;
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/stream?after_seq=0&turn_id={turnId}");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    throw new InvalidDataException($"Run stream rejected with {(int)response.StatusCode}: {body}");
                }
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
                    var received = string.Join(" | ", chunks.Select(chunk => chunk.ToString()));
                    string runStatus;
                    try
                    {
                        if (chunks.Count == 0)
                        {
                            runStatus = "(no chunks)";
                        }
                        else
                        {
                            using var probe = await client.GetAsync($"/api/v1/runs/{RunIdOf(chunks[0])}/orchestration");
                            runStatus = await probe.Content.ReadAsStringAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        runStatus = $"diagnostic failed: {ex.Message}";
                    }
                    acknowledgement.TrySetException(new InvalidDataException(
                        $"Invoke stream closed before its acknowledgement. Chunks: {received}\nOrchestration: {runStatus}"));
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

    /// <summary>
    /// Routes calls like the production scripts: the fixed Chinese scaffolding
    /// identifies planner/supervisor/meeting prompts, everything else is a worker
    /// turn. The first worker turn returns a write_file function call; later
    /// worker turns report completion text.
    /// </summary>
    private sealed class ToolScriptedClient : IChatClient
    {
        private readonly Queue<string> _supervisorVerdicts = new();
        private string? _planner;
        private string? _meeting;
        private (string ToolId, Dictionary<string, object?> Arguments)? _firstWorkerTool;
        private string? _workerFollowUp;
        private string? _workerText;
        public int WorkerCalls;
        public int StewardCalls;

        public ToolScriptedClient WhenPlanner(string script) { _planner = script; return this; }
        public ToolScriptedClient WhenSupervisor(string verdict) { _supervisorVerdicts.Enqueue(verdict); return this; }
        public ToolScriptedClient WhenMeeting(string script) { _meeting = script; return this; }
        public ToolScriptedClient WhenWorkerTool(string toolId, Dictionary<string, object?>? arguments = null)
        {
            _firstWorkerTool = (toolId, arguments ?? new Dictionary<string, object?>());
            return this;
        }
        public ToolScriptedClient WhenWorkerFollowUp(string text) { _workerFollowUp = text; return this; }
        /// <summary>Every worker turn returns plain text (no tool call), for
        /// tool-free modes such as conversation.ask.</summary>
        public ToolScriptedClient WhenWorkerText(string text) { _workerText = text; return this; }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join('\n', messages.Select(m => m.Text));
            var instructions = options?.Instructions;
            // Operational bypass roles must never fall through into the worker
            // branch: that branch consumes the scripted first-turn tool call.
            if (instructions?.Contains("You are the capability advisor", StringComparison.Ordinal) == true)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"recommendations\":[]}")));
            if (instructions?.Contains("You are the git steward", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref StewardCalls);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Change scope: scripted review. Commit boundary: single review commit.")));
            }
            if (instructions?.Contains("You are the context compression agent", StringComparison.Ordinal) == true
                || instructions?.Contains("You are the experience curator", StringComparison.Ordinal) == true)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
            if (instructions?.Contains("任务规划智能体", StringComparison.Ordinal) == true
                || instructions?.Contains("规划层", StringComparison.Ordinal) == true
                || prompt.Contains("规划", StringComparison.Ordinal) && !prompt.Contains("执行证据", StringComparison.Ordinal))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _planner ?? "[]")));
            if (instructions?.Contains("监督智能体", StringComparison.Ordinal) == true || prompt.Contains("执行证据", StringComparison.Ordinal))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    _supervisorVerdicts.Count > 0 ? _supervisorVerdicts.Dequeue() : "{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")));
            if (instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) == true || prompt.Contains("Execution evidence", StringComparison.Ordinal))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _meeting ?? "完成")));
            var isFirstWorkerTurn = Interlocked.Increment(ref WorkerCalls) == 1;
            if (_workerText is not null)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _workerText)));
            var contents = isFirstWorkerTurn
                ? new AIContent[] { new FunctionCallContent(
                    "call-1",
                    _firstWorkerTool?.ToolId ?? "write_file",
                    _firstWorkerTool?.Arguments ?? new Dictionary<string, object?> { ["filepath"] = "probe.txt", ["content"] = "hello" }) }
                : new AIContent[] { new TextContent(_workerFollowUp ?? "已写入 probe.txt") };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            var text = response.Text ?? "完成";
            foreach (var chunk in text.Chunk(2))
            {
                await Task.Delay(1, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, new string(chunk));
            }
        }
    }

    private sealed class ToolChainFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly ToolScriptedClient _client;
        private readonly IToolProvider? _providerOverride;

        public ToolChainFactory(string root, ToolScriptedClient client, IToolProvider? providerOverride = null)
        {
            _root = root;
            _client = client;
            _providerOverride = providerOverride;
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
                services.AddSingleton<IAgentChatClientFactory>(new ToolScriptedFactory(_client));
                services.AddSingleton<ISecretStore>(new TestModelSecretStore());
                if (_providerOverride is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<IToolProvider>(_providerOverride));
                }
            });
        }
    }

    private sealed class ToolScriptedFactory(ToolScriptedClient client) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "fake", ApiKey = "x", ModelId = "openai/fake" });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default)
            => Task.FromResult<IChatClient>(client);
    }
}
