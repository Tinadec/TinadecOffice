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
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Governance;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// End-to-end tool-chain tests: the full-duplex engine talks to a real
/// TinadecTools child process through the production manifest resolver and
/// dispatcher, pauses the run on an approval decision, resumes it after the
/// decision, and the worker's write_file call lands in the project workspace.
/// </summary>
public sealed partial class ToolChainEndpointTests : IAsyncLifetime
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
        var packDetail = await InstallToolChainPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Tool project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        // Bind the session to this pack's default-mode explicitly: an install no
        // longer re-points an already-configured workspace default, so the mode
        // must be chosen by the caller.
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "Tool session",
            mode_version_id = ToolChainPackModeVersionId(packDetail, "default-mode")
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = "tool-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));

        // Walkthrough defect 2: the row a human is asked to decide carried no target,
        // no command and no parameters — just "Tool permission request requires
        // authorization.". This park is a permission envelope, and ToolDispatcher has
        // already bound its execution row, so the frozen evidence digest must be on the
        // wire: the file it names, and not the bytes being written into it.
        var parkDetail = await (await client.GetAsync($"/api/v1/approvals/{approvalId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("permission", parkDetail.GetProperty("kind").GetString());
        Assert.Equal("write_file", parkDetail.GetProperty("tool_id").GetString());
        var parkSummary = parkDetail.GetProperty("summary").GetString();
        Assert.Contains("write_file", parkSummary, StringComparison.Ordinal);
        Assert.Contains("probe.txt", parkSummary, StringComparison.Ordinal);
        Assert.EndsWith("probe.txt", parkDetail.GetProperty("resource_path").GetString(), StringComparison.Ordinal);
        var parkArguments = parkDetail.GetProperty("arguments").GetString() ?? string.Empty;
        Assert.Contains("filepath", parkArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", parkArguments, StringComparison.Ordinal);

        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var decisionEvents = await lifecycle.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        var permissionDecision = Assert.Single(decisionEvents, item =>
            item.RunId == runId.ToString() && item.EventType == "governance.permission_decided");
        var permissionPayload = Assert.IsType<JsonElement>(permissionDecision.Payload["payload"]);
        Assert.Equal(approvalId, permissionPayload.GetProperty("permission_request_id").GetGuid());
        Assert.Equal(GovernanceOutcomes.Allowed, permissionPayload.GetProperty("outcome").GetString());
        var executionId = permissionPayload.GetProperty("execution_id").GetGuid();
        Assert.NotEqual(Guid.Empty, executionId);

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

        var timeline = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/tool-executions");
        Assert.NotNull(timeline);
        var writeExecution = Assert.Single(timeline, item =>
            item.GetProperty("tool_id").GetString() == "write_file");
        Assert.Equal(executionId.ToString(), writeExecution.GetProperty("id").GetString());
        Assert.Equal("completed", writeExecution.GetProperty("status").GetString());
        Assert.True(writeExecution.GetProperty("requires_approval").GetBoolean());
        Assert.Equal(approvalId.ToString(), writeExecution.GetProperty("approval_id").GetString());
        var timelineEventTypes = writeExecution.GetProperty("event_types").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Assert.Contains("approval.requested", timelineEventTypes);
        Assert.Contains("governance.permission_decided", timelineEventTypes);
        Assert.Contains("tool.execution.completed", timelineEventTypes);

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
            instance.GetProperty("layer").GetString() == "execution" && instance.GetProperty("task_id").ValueKind == System.Text.Json.JsonValueKind.String
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
        await InstallToolChainPackAsync(client);

        var packDetail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-packs/{ToolChainPackId}");
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Fake provider project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "Fake provider session",
            mode_version_id = ToolChainPackModeVersionId(packDetail, "default-mode")
        })).Content.ReadFromJsonAsync<JsonElement>();
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
        var packDetail = await InstallToolChainPackAsync(client);
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

        // Mode switching is the interactions endpoint's job: it validates the
        // published mode_version_id and persists it onto the session. invoke-stream
        // alone only labels the run; the frozen roster always comes from the
        // session's persisted mode.
        using var modeSwitch = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions",
            new { content = "X是什么？", client_message_id = "ask-mode-1", mode_version_id = askModeVersion, dispatch_mode = "queued" });
        Assert.True(modeSwitch.IsSuccessStatusCode, $"ask mode switch failed: {modeSwitch.StatusCode}");

        // The mode-switch message already started a run; this one runs beside it (a queued one would
        // wait behind it and have no turn to stream yet).
        var active = StartStreamingInvoke(client, sessionId, new { content = "X是什么？", client_message_id = "ask-e2e-1", dispatch_mode = "parallel" });
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
        // The gate ran with no supervisor, so the durable record must say so: the
        // engine drives the pipeline on a synthetic pass but never writes "pass" as
        // if a reviewer had approved it.
        var decisions = orchestration.GetProperty("supervision_findings").EnumerateArray()
            .Select(f => f.GetProperty("decision").GetString()).Where(d => d is not null).ToArray();
        Assert.Equal(new[] { "skipped" }, decisions);

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        Assert.Single(events, e => e.EventType == "supervision.skipped"
            && string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase));

        var sessions = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/sessions").ConfigureAwait(false);
        var sessionAfter = sessions!.Single(session => session.GetProperty("id").GetGuid() == sessionId);
        Assert.Equal(askModeVersion, sessionAfter.GetProperty("mode_version_id").GetGuid());

        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var worker = Assert.Single(lineage!, instance => instance.GetProperty("layer").GetString() == "execution" && instance.GetProperty("task_id").ValueKind == System.Text.Json.JsonValueKind.String);
        Assert.True(HasAgentVersion(worker, expectedVersions["worker.browser"]));
        Assert.DoesNotContain(lineage!, instance => instance.GetProperty("role").GetString() == "quality_controller");
        Assert.Contains(lineage!, instance => instance.GetProperty("role").GetString() == "session_coordinator"
            && HasAgentVersion(instance, expectedVersions["meeting"]));
    }

    /// <summary>
    /// The operation-layer deny floor is REMOVED BY DESIGN (2026-09-17). A mode may arm
    /// its conversation identity with tools so the agent that talks to the user also edits
    /// the workspace (the solo/master-slave shape), which means layer membership can no
    /// longer be the thing that denies a governance-layer tool call.
    ///
    /// This replaces the former
    /// <c>OperationLayerToolInvoke_IsDeniedByLayerPolicy_WhileWorkerIsAllowed</c>, which
    /// pinned the opposite. What must still hold is that the operation instance is not
    /// let through for free: it resolves through the same boundary path as the worker,
    /// and the boundaries that actually authorize the call are present.
    /// </summary>
    [Fact]
    public async Task OperationLayerToolInvoke_ResolvesLikeWorker_NoLayerDenyFloor()
    {
        var (provider, client, _, runId, approvalId, _) = await StartFakeProviderRunAsync("layer-policy");
        Assert.Equal(0, provider.CallCount);
        Assert.NotEqual(Guid.Empty, approvalId);

        var resolver = _factory!.Services.GetRequiredService<IAuthorizationContextResolver>();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var meeting = Assert.Single(lineage, item => item.GetProperty("role").GetString() == "session_coordinator");
        var worker = Assert.Single(lineage, item => item.GetProperty("layer").GetString() == "execution" && item.GetProperty("task_id").ValueKind == System.Text.Json.JsonValueKind.String);
        var taskId = worker.GetProperty("task_id").GetGuid();
        var claim = new CapabilityClaim("tool.file", "tool.invoke", "tool://write_file");

        var governanceBoundaries = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId,
            meeting.GetProperty("id").GetGuid(), claim, runId, taskId)).ConfigureAwait(false);

        // The floor is gone, so the operation instance must NOT be denied by layer. It
        // walks the ordinary path and gets the same authorizing boundaries the worker gets.
        Assert.DoesNotContain(governanceBoundaries, boundary => boundary.Name == "operation_layer_cannot_invoke_tools");
        Assert.NotEmpty(governanceBoundaries.Where(boundary => boundary.Name != "run")
            .Where(boundary => boundary.Rules.Any(rule => rule.Effect == "allow")));

        var workerBoundaries = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId,
            worker.GetProperty("id").GetGuid(), claim, runId, taskId)).ConfigureAwait(false);
        Assert.DoesNotContain(workerBoundaries, boundary => boundary.Name == "operation_layer_cannot_invoke_tools");
        Assert.NotEmpty(workerBoundaries.Where(boundary => boundary.Name != "run")
            .Where(boundary => boundary.Rules.Any(rule => rule.Effect == "allow")));
    }

    [Fact]
    public async Task AgentVersionBoundary_UsesDeclaredToolScope_AsFinalAnswer()
    {
        var (_, client, _, runId, _, _) = await StartFakeProviderRunAsync("agent-version-scope");
        var resolver = _factory!.Services.GetRequiredService<IAuthorizationContextResolver>();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var worker = Assert.Single(lineage, item => item.GetProperty("layer").GetString() == "execution" && item.GetProperty("task_id").ValueKind == System.Text.Json.JsonValueKind.String);
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
    /// Three-tier resource decision, pinned at the boundary that implements it.
    /// A MUTATING claim on an instance whose envelope holds only read-level grants
    /// must clear resource_access as an UPGRADE — carrying the operator-facing
    /// reason — and not as a deny. Denying happened before the approval gate, so
    /// the write was structurally unreachable rather than asked for.
    ///
    /// The reachable production shape is a SPAWNABLE template: the freeze derives
    /// the whole-workspace read level for any template that declared no grants while
    /// its tool ceiling still names a mutating tool. Mode bindings are guarded
    /// against that inconsistency by ModePublishGate rule ③; spawnable templates
    /// are not, which is exactly the envelope a real v1 incident denied.
    /// </summary>
    [Fact]
    public async Task ReadOnlyEnvelope_MutatingClaim_ClearsResourceBoundaryAsAnUpgrade()
    {
        var (_, client, _, runId, _, _) = await StartFakeProviderRunAsync("resource-upgrade");
        var services = _factory!.Services;
        var resolver = services.GetRequiredService<IAuthorizationContextResolver>();
        var scope = services.GetRequiredService<ITenantContextAccessor>().Current;
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var worker = Assert.Single(lineage, item => item.GetProperty("layer").GetString() == "execution"
            && item.GetProperty("task_id").ValueKind == JsonValueKind.String);
        var workerId = worker.GetProperty("id").GetGuid();
        var taskId = worker.GetProperty("task_id").GetGuid();
        // The claim shape the dispatcher sends for a mutating tool: the ACTION is the
        // level ("mutate"/"read"), which is what the resource envelope keys off.
        var mutate = new CapabilityClaim("tool.invoke", "mutate", "tool://write_file");

        // Baseline: the fixture's binding declares read+write, so the envelope
        // authorizes the mutation outright and carries no upgrade text.
        var granted = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId, workerId, mutate, runId, taskId)).ConfigureAwait(false);
        var grantedResource = Assert.Single(granted, boundary => boundary.Name == "resource_access");
        Assert.Equal("allow", Assert.Single(grantedResource.Rules).Effect);
        Assert.Null(grantedResource.UpgradeReason);

        // Drop the instance envelope to read-only: the shape a spawnable template
        // gets when it declared no grants of its own.
        var readBack = await RewriteInstanceResourcesAsync(workerId, ["read:"]).ConfigureAwait(false);
        Assert.Equal(["read:"], readBack);

        var upgraded = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId, workerId, mutate, runId, taskId)).ConfigureAwait(false);
        var resource = Assert.Single(upgraded, boundary => boundary.Name == "resource_access");
        Assert.Equal("allow", Assert.Single(resource.Rules).Effect);
        Assert.True(resource.UpgradeReason is not null,
            $"no upgrade reason; readBack=[{string.Join(",", readBack)}] rules=[{string.Join(",", resource.Rules.Select(rule => rule.Effect + ":" + rule.Action))}]");
        Assert.Contains("needs approval", resource.UpgradeReason!, StringComparison.Ordinal);
        Assert.Contains("write-level grant", resource.UpgradeReason!, StringComparison.Ordinal);

        // A READ claim on the same read-only envelope keeps its plain allow: the
        // upgrade is only ever for the level the envelope is missing.
        var readBoundaries = await resolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId, workerId,
            new CapabilityClaim("tool.invoke", "read", "tool://read_file"), runId, taskId)).ConfigureAwait(false);
        var readResource = Assert.Single(readBoundaries, boundary => boundary.Name == "resource_access");
        Assert.Equal("allow", Assert.Single(readResource.Rules).Effect);
        Assert.Null(readResource.UpgradeReason);

        // Application level: the same claim now reaches the HUMAN GATE instead of
        // being refused by policy. Before the three-tier decision this request came
        // back denied (explicit_deny) with the tool never executing — the shape the
        // v1 incident recorded for a mutating call on a read-only envelope.
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var resolution = await authorization.RequestPermissionAsync(new PermissionRequestCommand(
            scope.PrincipalId,
            workerId,
            null,
            mutate,
            runId,
            taskId,
            TimeSpan.FromMinutes(30),
            1,
            "high",
            0m,
            "tool-chain three-tier probe",
            $"tier-probe:{runId:N}",
            PermissionMode: "ask")).ConfigureAwait(false);

        Assert.Equal("awaiting_user", resolution.Request.Status);
        Assert.Equal("user_approval_required", resolution.Decision.ReasonCode);

        // The upgrade reason is persisted on the request so the approval prompt can
        // say WHAT the envelope is missing instead of a bare "approval required".
        await using (var governance = await services
            .GetRequiredService<IDbContextFactory<GovernanceDbContext>>().CreateDbContextAsync())
        {
            var persisted = await governance.PermissionRequests.AsNoTracking()
                .SingleAsync(item => item.Id == resolution.Request.Id);
            Assert.Contains("needs approval", persisted.Rationale ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("write-level grant", persisted.Rationale ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Rewrites an instance's frozen resource grants in place: the definition body
    /// is re-stored with the same shape the instance service wrote and the row's
    /// reference/hash/length are updated, so the PDP reads the new envelope. Returns
    /// what the store actually holds afterwards, so a silent no-op cannot pass for a
    /// successful rewrite.
    /// </summary>
    private async Task<IReadOnlyList<string>> RewriteInstanceResourcesAsync(Guid instanceId, IReadOnlyList<string> grants)
    {
        var services = _factory!.Services;
        var content = services.GetRequiredService<IContentStore>();
        var instances = services.GetRequiredService<IDbContextFactory<AgentControlDbContext>>();
        var scope = services.GetRequiredService<ITenantContextAccessor>().Current;

        await using var db = await instances.CreateDbContextAsync();
        var row = await db.Instances.SingleAsync(item => item.Id == instanceId);
        await using var source = await content.OpenReadAsync(
            new ContentReference(row.DefinitionReference, row.DefinitionHash, row.DefinitionLength, "application/json"));
        using var document = await JsonDocument.ParseAsync(source);

        using var rewritten = new MemoryStream();
        using (var writer = new Utf8JsonWriter(rewritten))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var name = property.Name;
                if (name.Equals("allowedResources", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("allowed_resources", StringComparison.OrdinalIgnoreCase)) continue;
                property.WriteTo(writer);
            }
            writer.WritePropertyName("allowedResources");
            writer.WriteStartArray();
            foreach (var grant in grants) writer.WriteStringValue(grant);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        rewritten.Position = 0;
        var stored = await content.PutAsync(new ContentWriteRequest(
            scope.TenantId, scope.WorkspaceId, "agent-instance", "application/json", rewritten));
        row.DefinitionReference = stored.Value;
        row.DefinitionHash = stored.Sha256;
        row.DefinitionLength = stored.Length;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await using var verifyDb = await instances.CreateDbContextAsync();
        var persistedRow = await verifyDb.Instances.AsNoTracking().SingleAsync(item => item.Id == instanceId);
        Assert.Equal(stored.Value, persistedRow.DefinitionReference);
        Assert.Equal(stored.Sha256, persistedRow.DefinitionHash);

        await using var persisted = await content.OpenReadAsync(
            new ContentReference(persistedRow.DefinitionReference, persistedRow.DefinitionHash, persistedRow.DefinitionLength, "application/json"));
        using var document2 = await JsonDocument.ParseAsync(persisted);
        if (!document2.RootElement.TryGetProperty("allowedResources", out var stored0)
            || stored0.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return stored0.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).ToArray();
    }

    /// <summary>
    /// "Always allow for this session", end to end at the decision endpoint. One
    /// human decision must stop the SAME tool from asking again for the rest of the
    /// run, which needs two durable envelopes because two separate gates ask: the
    /// PDP wants a capability grant before it will lease, and the tool-approval layer
    /// wants a pre-authorization before it will mint. And the decision must release
    /// what is already parked, not only what comes next — a scope that covered only
    /// the future would leave an already-waiting sibling blocked on the very click
    /// the user just declined to make.
    /// </summary>
    [Fact]
    public async Task RunScopedApproval_MintsBothEnvelopes_AndReleasesParkedSiblings()
    {
        var (_, client, _, runId, approvalId, _) = await StartFakeProviderRunAsync("run-scope");
        var services = _factory!.Services;
        var scope = services.GetRequiredService<ITenantContextAccessor>().Current;

        // A second, already-parked request for the SAME tool in the SAME run.
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.NotNull(lineage);
        var worker = Assert.Single(lineage, item => item.GetProperty("layer").GetString() == "execution"
            && item.GetProperty("task_id").ValueKind == JsonValueKind.String);
        var workerId = worker.GetProperty("id").GetGuid();
        var taskId = worker.GetProperty("task_id").GetGuid();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var sibling = await authorization.RequestPermissionAsync(new PermissionRequestCommand(
            scope.PrincipalId, workerId, null,
            new CapabilityClaim("tool.invoke", "mutate", "tool://write_file"),
            runId, taskId, TimeSpan.FromMinutes(30), 1, "high", 0m,
            "second call of the same tool", $"run-scope-sibling:{runId:N}",
            PermissionMode: "ask")).ConfigureAwait(false);
        Assert.True(sibling.Request.Status == "awaiting_user",
            $"sibling status={sibling.Request.Status} reason={sibling.Decision.ReasonCode}: {sibling.Decision.Reason}");

        var decide = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision",
            new { decision = "approved", scope = "run", reason = "总是允许" });
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);
        var decided = await decide.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("run", decided.GetProperty("scope").GetString());
        Assert.Equal(1, decided.GetProperty("run_scope_released").GetInt32());
        Assert.NotEqual(Guid.Empty, decided.GetProperty("pre_authorization_id").GetGuid());
        Assert.NotEqual(Guid.Empty, decided.GetProperty("capability_grant_id").GetGuid());

        // Both envelopes exist. The pre-authorization is capped at the risk class the
        // human actually approved — a session approval never widens the ceiling it was
        // given — and its scope is exactly the one tool, never a wildcard.
        var decidedRequest = await authorization.GetPermissionRequestAsync(approvalId).ConfigureAwait(false);
        Assert.NotNull(decidedRequest);
        await using (var db = await services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            var preAuth = Assert.Single(await db.PreAuthorizations.AsNoTracking().Where(x => x.RunId == runId).ToListAsync());
            var scoped = JsonSerializer.Deserialize<string[]>(preAuth.ToolScopeJson);
            Assert.NotNull(scoped);
            Assert.Equal(["write_file"], scoped);
            Assert.Equal(decidedRequest.Request.Risk, preAuth.RiskMax);
            Assert.False(preAuth.Revoked);
            Assert.True(preAuth.MaxUses > 1);
        }

        // The parked sibling is granted: the user's intent covers what is already
        // waiting, not only what comes next.
        var released = await authorization.GetPermissionRequestAsync(sibling.Request.Id).ConfigureAwait(false);
        Assert.Equal("granted", released!.Request.Status);
    }

    /// <summary>
    /// The default decision stays a one-shot: without an explicit run scope nothing
    /// session-wide is minted, so "approve" never silently becomes "always approve".
    /// </summary>
    [Fact]
    public async Task ApprovalDecision_WithoutScope_StaysOnce()
    {
        var (_, client, _, runId, approvalId, _) = await StartFakeProviderRunAsync("run-scope-once");
        var services = _factory!.Services;

        var decide = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision",
            new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);
        var decided = await decide.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("once", decided.GetProperty("scope").GetString());
        Assert.Equal(0, decided.GetProperty("run_scope_released").GetInt32());

        await using var db = await services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.PreAuthorizations.AsNoTracking().Where(x => x.RunId == runId).ToListAsync());
    }

    // ── Full-duplex engine hardening regressions (C1–C5) ─────────────────────

    /// <summary>
    /// C1: a task with empty required_tools must still receive the worker's
    /// authorized declaration surface (grant ∩ frozen manifest). Before the fix the
    /// text path advertised zero tools, so a worker improvising a catalog call failed
    /// with "not advertised"; now the call flows through the durable tool loop.
    /// </summary>
    [Fact]
    public async Task EmptyRequiredToolsTask_WorkerReceivesAuthorizedCatalog_AndToolCallDispatches()
    {
        var workspace = Path.Combine(_root, "workspace-c1");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"probe\",\"title\":\"写探针但不声明工具\",\"description\":\"\",\"success_criteria\":[\"文件写入\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。");

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, active) = await StartRunAsync(client, workspace, "c1", "写一个文件");

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        // The improvised catalog call really dispatched (approval-gated) instead of
        // failing the task as "not advertised".
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("write_file", Assert.Single(provider.ReceivedToolIds));

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration").ConfigureAwait(false);
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
        var node = Assert.Single(orchestration.GetProperty("nodes").EnumerateArray());
        Assert.Equal("completed", node.GetProperty("status").GetString());

        // Graph tiers create the worker through the engine-authoritative root
        // path: its persisted grant is the frozen roster scope (the improvised
        // catalog call above already proved it covers write_file).
        var instances = await _factory.Services.GetRequiredService<IAgentInstanceService>().ListByRunAsync(runId).ConfigureAwait(false);
        var worker = Assert.Single(instances, instance => instance.Layer == "execution");
        // The persisted grant may be the wildcard declaration; the improvised
        // catalog call already proved the expansion covers write_file.
        Assert.True(worker.AllowedTools.Contains("*") || worker.AllowedTools.Contains("write_file"),
            $"worker grant should cover write_file: {string.Join(",", worker.AllowedTools)}");
    }

    /// <summary>
    /// C5: a task requiring a tool the frozen manifest does not carry fails only
    /// itself (worker_assignment_invalid); the sibling task and the run continue.
    /// </summary>
    [Fact]
    public async Task TaskRequiringToolOutsideFrozenManifest_FailsOnlyThatTask_RunCompletes()
    {
        var workspace = Path.Combine(_root, "workspace-c5");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"ghost\",\"title\":\"需要不存在的工具\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"ghost_tool\"],\"priority\":1,\"risk\":\"low\"},"
                + "{\"task_key\":\"probe\",\"title\":\"写探针\",\"description\":\"\",\"success_criteria\":[\"文件写入\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":2,\"risk\":\"low\"}]")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。");

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, active) = await StartRunAsync(client, workspace, "c5", "执行两个任务");

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        List<JsonElement> chunks;
        try
        {
            chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            var orch = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration").ConfigureAwait(false);
            var execs = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/tool-executions").ConfigureAwait(false);
            var mgr = _factory.Services.GetRequiredService<ILifecycleManager>();
            var evts = (await mgr.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false))
                .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            throw new TimeoutException(
                $"Run {runId} did not complete after approval. status="
                + orch.GetProperty("run").GetProperty("status").GetString()
                + " summary=" + orch.GetProperty("run").GetProperty("summary").GetString()
                + " nodes=[" + string.Join(",", orch.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("status").GetString())) + "]"
                + " execs=[" + string.Join(",", (execs ?? []).Select(e => e.GetProperty("tool_id").GetString() + ":" + e.GetProperty("status").GetString())) + "]"
                + " events=[" + string.Join(",", evts.Select(e => e.EventType)) + "]", ex);
        }
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration").ConfigureAwait(false);
        Assert.Equal("completed", orchestration.GetProperty("run").GetProperty("status").GetString());
        // The replay journal shows the ghost task failed on its own while the
        // sibling task completed.
        var replay = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/replay").ConfigureAwait(false);
        var tasks = replay.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Single(tasks, item => item.GetProperty("task_key").GetString() == "probe"
            && item.GetProperty("status").GetString() == "completed");
        var ghost = Assert.Single(tasks, item => item.GetProperty("task_key").GetString() == "ghost");
        Assert.Equal("failed", ghost.GetProperty("status").GetString());
        Assert.Contains("ghost_tool", ghost.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Contains("error_category:worker_assignment_invalid",
            ghost.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()));

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        Assert.Single(events, e => e.EventType == "worker.failed");
        Assert.Equal(1, provider.CallCount);
    }

    /// <summary>
    /// C2: lane main escalates in the same tick in which lane l2's tool task parks on
    /// an approval. The run is already awaiting_user, so the approval park must skip
    /// <summary>
    /// C3: a tool dispatch that fails mid-run is handed back to the worker as a tool
    /// RESULT, not turned into a terminal task failure — this is what lets the model
    /// read the error and adapt. The turn is not re-dispatched on a later engine pass
    /// either: the fed-back failure is durable, so the dead execution is never
    /// resumed and the failure is never re-emitted.
    /// </summary>
    [Fact]
    public async Task FailedToolDispatch_IsFedBackToWorker_AndNotReDispatchedOnLaterTicks()
    {
        var workspace = Path.Combine(_root, "workspace-c3");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider { FailWhen = request =>
            request.Params is { } args && args.TryGetProperty("filepath", out var path) && path.GetString() == "t2.txt" };
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"t1\",\"title\":\"写一\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"},"
                + "{\"task_key\":\"t2\",\"title\":\"写二\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":2,\"risk\":\"low\"}]")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。")
            .WhenTaskWorkerTurns("写一",
                [new FunctionCallContent("call-t1", "write_file", new Dictionary<string, object?> { ["filepath"] = "t1.txt", ["content"] = "1" })],
                [new TextContent("t1 完成")])
            .WhenTaskWorkerTurns("写二",
                [new FunctionCallContent("call-t2", "write_file", new Dictionary<string, object?> { ["filepath"] = "t2.txt", ["content"] = "2" })],
                // Consumed after t2's dispatch failure is fed back: the model reads
                // the error and hands off instead of retrying blindly.
                [new TextContent("t2 写入失败，我据实汇报")]);

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, active) = await StartRunAsync(client, workspace, "c3", "写两个文件");

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();

        // Independent tasks can park together. Each task has its own model script;
        // t2's failure is keyed to its file, not to whichever call wins scheduling.
        var firstApproval = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/approvals/{firstApproval}/decision", new { decision = "approved" })).StatusCode);
        var secondApproval = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        Assert.NotEqual(firstApproval, secondApproval);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/approvals/{secondApproval}/decision", new { decision = "approved" })).StatusCode);
        await WaitForEventCountAsync(manager, sessionId, "worker.tool_failed", 1);

        // Force an extra engine pass over the same checkpoint: the fed-back turn
        // already carries its result, so this pass must not resume the dead
        // execution, re-dispatch it, or emit the failure a second time.
        var callsBeforeExtraPass = provider.CallCount;
        await _factory.Services.GetRequiredService<IFullDuplexRunEngine>().EnqueueAsync(runId);

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);

        // The dispatch failure is reported once, names the tool, and is flagged as
        // having been returned to the worker rather than ending the task.
        var returned = Assert.Single(events, item => item.EventType == "worker.tool_failed");
        var payload = Assert.IsType<JsonElement>(returned.Payload["payload"]);
        Assert.Equal("t2", payload.GetProperty("task_key").GetString());
        Assert.Equal("write_file", payload.GetProperty("tool_id").GetString());
        Assert.True(payload.GetProperty("returned_to_worker").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("error_category").GetString()));

        // No task was failed by the dispatch error, and the tool was not re-called.
        Assert.DoesNotContain(events, item => item.EventType == "worker.failed");
        Assert.Equal(callsBeforeExtraPass, provider.CallCount);
        Assert.Equal(2, provider.CallCount);
    }

    /// <summary>
    /// C4: an approval whose decision window lapses escalates once (on the implicit
    /// main lane under graph tiers — the lane machinery itself is unreachable). The
    /// dead execution is detached from the task, the run parks on awaiting_user
    /// (never fails), and a repeated wake neither duplicates the escalation event
    /// nor resumes the dead execution. The approval-level park is built
    /// deterministically: a one-use pre-authorization carries the first call past
    /// the approval layer, so the second call's approval row stays pending.
    /// </summary>
    [Fact]
    public async Task ParkExpiredApproval_EscalatesLaneOnce_RunStaysAwaitingUser()
    {
        var workspace = Path.Combine(_root, "workspace-c4");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"w1\",\"title\":\"写一\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"},"
                + "{\"task_key\":\"w2\",\"title\":\"写二\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[\"w1\"],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":2,\"risk\":\"low\"}]")
            .WhenMeeting("不应到达。")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-w1", "write_file", new Dictionary<string, object?> { ["filepath"] = "w1.txt", ["content"] = "1" })],
                [new TextContent("w1 完成")],
                [new FunctionCallContent("call-w2", "write_file", new Dictionary<string, object?> { ["filepath"] = "w2.txt", ["content"] = "2" })]);
        script.WorkerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        script.BeforeWorker = workerGate.Task;

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, _) = await StartRunAsync(client, workspace, "c4", "无人值守写两个文件");

        // Hold the first worker turn until the one-use pre-authorization is durable.
        await script.WorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var preAuth = await client.PostAsJsonAsync("/api/v1/approvals/pre-authorizations", new
        {
            run_id = runId,
            tool_scope = new[] { "write_file" },
            risk_max = "high",
            max_uses = 1,
            summary = "只覆盖第一个写调用"
        });
        Assert.Equal(HttpStatusCode.Created, preAuth.StatusCode);
        workerGate.SetResult();

        // w1 dispatches unattended (the pre-auth mints its approval); w2's approval
        // then stays pending because the pre-authorization budget is spent.
        await WaitForReplayTaskStatusAsync(client, runId, "w1", "completed");
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));

        // Let the decision window lapse (the ApprovalWindowTests mechanism), then
        // wake the run the way the lifecycle sweeper does.
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            var approval = await db.ApprovalRequests.SingleAsync(row => row.Id == approvalId);
            approval.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var engine = _factory.Services.GetRequiredService<IFullDuplexRunEngine>();
        await engine.EnqueueAsync(runId);

        var parked = await WaitForRunStatusAsync(client, runId, "awaiting_user", "failed");
        Assert.Equal("awaiting_user", parked);
        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var escalations = (await manager.ReplayEventsAsync(sessionId, 0))
            .Where(e => e.EventType == "supervision.user_review.requested")
            .ToArray();
        var escalation = Assert.Single(escalations);
        Assert.Equal("main", ((JsonElement)escalation.Payload["payload"]!).GetProperty("lane_key").GetString());
        Assert.Equal(1, provider.CallCount);

        // A second wake (sweeper tick) must be silent: the dead execution is not
        // resumed and the escalation is not re-emitted.
        await engine.EnqueueAsync(runId);
        await Task.Delay(TimeSpan.FromSeconds(3));

        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        Assert.Single(events.Where(e => e.EventType == "supervision.user_review.requested"));
        Assert.DoesNotContain(events, e => e.EventType == "run.failed");
        Assert.Equal(1, provider.CallCount);
        var status = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration"))
            .GetProperty("run").GetProperty("status").GetString();
        Assert.True(status == "awaiting_user", $"status={status} reviewEvents={string.Join(",", events.Select(e => e.EventType))} replay={await client.GetStringAsync($"/api/v1/runs/{runId}/replay")}");
    }

    /// <summary>
    /// Wake-path pinning (graph orchestration acceptance): a parked awaiting_user
    /// run whose approval row is decided REJECTED must wake and reach a terminal
    /// state — never hang. The approval-row branch of ControlPlaneService.DecideApproval
    /// enqueues the run for approved AND rejected decisions; this pins the rejected
    /// side. The rejection is handed to the worker as a tool result
    /// (<c>not_approved</c>), so the task adapts instead of being killed: the tool
    /// never executes, the model hands off, and the run completes rather than
    /// failing or stalling.
    /// </summary>
    [Fact]
    public async Task RejectedApproval_WakesParkedRun_FailureFedBackToWorker_RunReachesTerminal()
    {
        var workspace = Path.Combine(_root, "workspace-wake-approval");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"r1\",\"title\":\"写一个文件\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("完成。")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-r1", "write_file", new Dictionary<string, object?> { ["filepath"] = "r1.txt", ["content"] = "x" })],
                // Consumed after the rejection is fed back.
                [new TextContent("r1 未获授权，我没有写入任何文件")]);

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, _) = await StartRunAsync(client, workspace, "wake-approval", "写文件");

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var parked = await WaitForRunStatusAsync(client, runId, "awaiting_user", "failed");
        Assert.Equal("awaiting_user", parked);
        Assert.Equal(0, provider.CallCount);

        // REJECTED: the run must wake, hand the refusal to the worker, and reach a
        // terminal state under its own power.
        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "rejected" });
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        await WaitForReplayTaskStatusAsync(client, runId, "r1", "completed");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
            var status = orchestration.GetProperty("run").GetProperty("status").GetString()!;
            if (status is "completed" or "failed" or "cancelled") break;
            await Task.Delay(150);
        }
        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);

        // The refusal reached the worker as a tool result, so the task was not
        // failed by it — and the tool itself never ran.
        var returned = Assert.Single(events, e => e.EventType == "worker.tool_failed");
        var payload = Assert.IsType<JsonElement>(returned.Payload["payload"]);
        Assert.Equal("r1", payload.GetProperty("task_key").GetString());
        Assert.Equal("write_file", payload.GetProperty("tool_id").GetString());
        Assert.Equal(RunErrorTaxonomy.ApproverRejected, payload.GetProperty("error_category").GetString());
        Assert.True(payload.GetProperty("returned_to_worker").GetBoolean());

        Assert.Equal(0, provider.CallCount);
        Assert.DoesNotContain(events, e => e.EventType == "worker.failed");
        Assert.DoesNotContain(events, e => e.EventType == "run.failed");
    }

    /// <summary>
    /// Wake-path pinning, permission-request branch: a run parked on a governance
    /// permission request whose decision is DENIED must be enqueued (the denied
    /// branch of ControlPlaneService.DecideApproval mirrors GovernanceEndpoints'
    /// wake-up) and reach a terminal state instead of hanging forever. The denial
    /// is arranged through the user tool-action chain, whose permission request
    /// carries no run — the run-shaped denial contract is pinned at the decision
    /// cascade level: the rejected user action's permission row must reach a
    /// terminal decision with a recorded outcome.
    /// </summary>
    [Fact]
    public async Task RejectedPermissionRequest_TerminalDecisionRecorded_DecisionCascadeRuns()
    {
        // The user tool-action chain runs without the engine; a minimal scripted
        // factory provides the host and stores.
        _factory = new ToolChainFactory(_root, new ToolScriptedClient().WhenMeeting("未用。"), new FakeToolProvider());
        var client = _factory.CreateClient();
        var projectPath = Path.Combine(_root, "wake-permission-workspace");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Wake permission project", path = projectPath });
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();

        var create = await client.PostAsJsonAsync("/api/v1/user/tool-actions", new
        {
            project_id = projectId,
            tool_id = "write_file",
            @params = new { filepath = "denied.txt", content = "x" },
            idempotency_key = "wake-permission-1"
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var requested = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("awaiting_user", requested.GetProperty("status").GetString());
        var permissionRequestId = requested.GetProperty("permission_request_id").GetGuid();

        // Denied: the decision must commit a terminal outcome on the request row
        // (the wake-up cascade for run-scoped denials mirrors GovernanceEndpoints).
        var denied = await client.PostAsJsonAsync($"/api/v1/governance/permission-requests/{permissionRequestId}/decision",
            new { approve = false, reason = "User denied the write." });
        Assert.True((int)denied.StatusCode is >= 200 and < 300, $"denial failed: {denied.StatusCode}");
        var deniedBody = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("denied", deniedBody.GetProperty("request").GetProperty("status").GetString());
    }

    private async Task<(Guid SessionId, Guid RunId, ActiveInvoke Active)> StartRunAsync(HttpClient client, string workspace, string label, string goal)
    {
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = label + " project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        // The ToolChain pack's default-mode carries the write-capable roster;
        // sessions bind it explicitly because installs never re-point an
        // already-configured workspace default. The pack detail is the source of
        // truth — the bootstrap fixture also publishes a 'default-mode' slug.
        var packDetail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-packs/{ToolChainPackId}");
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = label + " session",
            mode_version_id = ToolChainPackModeVersionId(packDetail, "default-mode")
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var active = StartStreamingInvoke(client, sessionId, new { content = goal, client_message_id = label + "-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return (sessionId, ack.GetProperty("run_id").GetGuid(), active);
    }


    private async Task<Guid> LatestPublishedModeVersionIdAsync(string modeSlug)
    {
        await using var cfg = await _factory!.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        var modes = await cfg.AgentModes.AsNoTracking()
            .Where(mode => mode.Slug == modeSlug && mode.Status == "published")
            .ToListAsync();
        var modeIds = modes.Select(mode => mode.Id).ToHashSet();
        var versions = await cfg.ModeVersions.AsNoTracking()
            .Where(version => version.Status == "published" && modeIds.Contains(version.AgentModeId))
            .ToListAsync();
        return versions.OrderByDescending(version => version.Version).First().Id;
    }

    private static async Task<string> WaitForRunStatusAsync(HttpClient client, Guid runId, params string[] accepted)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
            var status = orchestration.GetProperty("run").GetProperty("status").GetString()!;
            if (accepted.Contains(status, StringComparer.Ordinal)) return status;
            await Task.Delay(150);
        }
        var final = await client.GetStringAsync($"/api/v1/runs/{runId}/orchestration");
        throw new TimeoutException($"Run {runId} never reached [{string.Join(", ", accepted)}]. Orchestration: {final}");
    }

    private static async Task WaitForReplayTaskStatusAsync(HttpClient client, Guid runId, string taskKey, string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var replay = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/replay");
            var task = replay.GetProperty("tasks").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("task_key").GetString() == taskKey);
            if (task.ValueKind != JsonValueKind.Undefined && task.GetProperty("status").GetString() == status) return;
            await Task.Delay(150);
        }
        var final = await client.GetStringAsync($"/api/v1/runs/{runId}/replay");
        throw new TimeoutException($"Task '{taskKey}' of run {runId} never reached '{status}'. Replay: {final}");
    }

    private static async Task WaitForEventCountAsync(ILifecycleManager manager, Guid sessionId, string eventType, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var count = (await manager.ReplayEventsAsync(sessionId, 0)).Count(e => e.EventType == eventType);
            if (count >= expected) return;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Event '{eventType}' never reached count {expected} in session {sessionId}.");
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
        await InstallToolChainPackAsync(client);

        var packDetail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-packs/{ToolChainPackId}");
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = label + " project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = label + " session",
            mode_version_id = ToolChainPackModeVersionId(packDetail, "default-mode")
        })).Content.ReadFromJsonAsync<JsonElement>();
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
        await InstallToolChainPackAsync(client);

        var packDetail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-packs/{ToolChainPackId}");
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Git steward project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "Git steward session",
            mode_version_id = ToolChainPackModeVersionId(packDetail, "default-mode")
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "查看 git 状态", client_message_id = "git-steward-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        // git_status is a READ-level tool: in the ask family it is released without a
        // human decision, so the run reaches its terminal state on its own. The
        // steward gate keys off the run having TOUCHED a git tool, not off an
        // approval — gating the read was pure latency on the user's side.
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        Assert.DoesNotContain(events, item => item.EventType == "approval.requested");

        // The steward bypass runs after the terminal done chunk is durable.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (script.StewardCalls == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(200);
        Assert.True(script.StewardCalls >= 1, "The git steward should review runs that touched git tools.");
    }

    /// <summary>
    /// Vibe pack with-run E2E on the FakeToolProvider harness (no TinadecTools
    /// binary required): a declared-graph mode freezes a self_dispatch tier (the
    /// conversation identity holds agent.create_temporary), the conversation
    /// identity authors the task graph (no task_planner instance is created), the
    /// worker is created authoritatively from the frozen roster as a root instance
    /// bound to its declared edge target, write_file parks the run on the approval
    /// gate, the approval wakes it, and the orchestration projection carries the
    /// declared graph plus observed flows. Fixture constraints: no supervisor node
    /// (the review gate skips), lanes disabled (the freeze-gate passing side).
    /// </summary>
    [Fact]
    public async Task VibePack_Run_WalksDeclaredEdges_ParksOnApproval_AndCompletes()
    {
        var workspace = Path.Combine(_root, "workspace-vibe-run");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"v1\",\"title\":\"写vibe文件\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenMeeting("vibe 完成。")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-v1", "write_file", new Dictionary<string, object?> { ["filepath"] = "vibe.txt", ["content"] = "x" })],
                [new TextContent("已写入 vibe.txt")]);

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();

        var envelope = VibeRunPackEnvelope();
        using var preview = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(preview.StatusCode == HttpStatusCode.Created || preview.StatusCode == HttpStatusCode.OK,
            $"vibe preview: {preview.StatusCode} {await preview.Content.ReadAsStringAsync()}");
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.tests.vibe-run-pack")
        {
            Content = JsonContent.Create(new { preview_id = previewBody.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", "vibe-run-install");
        using var applyResponse = await client.SendAsync(apply);
        Assert.True(applyResponse.StatusCode == HttpStatusCode.Created,
            $"vibe install: {applyResponse.StatusCode} {await applyResponse.Content.ReadAsStringAsync()}");
        var packDetail = await client.GetFromJsonAsync<JsonElement>("/api/v1/agent-packs/tinadec.tests.vibe-run-pack");
        var modeVersionId = packDetail.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("kind").GetString() == "mode")
            .Select(resource => resource.GetProperty("version_id").GetGuid())
            .Single();

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "vibe run project", path = workspace }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "vibe run",
            mode_version_id = modeVersionId
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        Assert.Contains("meeting", session.GetProperty("conversation_template_slug").GetString());

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个 vibe 文件", client_message_id = "vibe-run-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        var runId = ack.GetProperty("run_id").GetGuid();

        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var parked = await WaitForRunStatusAsync(client, runId, "awaiting_user", "failed");
        Assert.Equal("awaiting_user", parked);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" })).StatusCode);

        await WaitForReplayTaskStatusAsync(client, runId, "v1", "completed");
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);

        // Tier observability: self_dispatch, announced exactly once.
        var tierEvent = Assert.Single(events, e => e.EventType == "orchestration.mode_tier_decided");
        var tierPayload = (JsonElement)tierEvent.Payload["payload"]!;
        Assert.Equal("self_dispatch", tierPayload.GetProperty("tier").GetString());
        Assert.Contains("meeting", tierPayload.GetProperty("conversation_slug").GetString());

        // Declared graph + observed flows through the conversation identity.
        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        var graph = orchestration.GetProperty("graph");
        Assert.Equal(3, graph.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, graph.GetProperty("edges").GetArrayLength());
        var flow = Assert.Single(orchestration.GetProperty("flows").EnumerateArray()
            .Where(item => item.GetProperty("task_key").GetString() == "v1"));
        Assert.Contains("meeting", flow.GetProperty("from").GetString());
        Assert.Contains("worker", flow.GetProperty("to").GetString());

        // Root-instance lineage: the conversation identity authors; NO task_planner
        // instance exists in a graph tier.
        var createdAgents = events
            .Where(e => e.EventType == "agent.created")
            .Select(e => (JsonElement)e.Payload["payload"]!)
            .ToArray();
        Assert.Single(createdAgents, payload => payload.GetProperty("agent_slug").GetString()!.Contains("meeting"));
        Assert.DoesNotContain(createdAgents, payload => payload.GetProperty("agent_slug").GetString() == "task_planner");
        Assert.Contains(events, e => e.EventType == "supervision.skipped");
    }

    /// <summary>
    /// The 2026-09-17 plan_parse_failed cluster (feb146e4/05a89fc3): the planner's
    /// first answer writes an array-typed field as a scalar string, the declared-edge
    /// guard refuses the fallback, and attempt 2 used to resend the byte-identical
    /// prompt so the run died. Now attempt 1's scalar shape is repaired in the parser;
    /// this test pins that a run through a DECLARED-EDGE mode completes when attempt 1
    /// is unrecoverable and the retry hint steers attempt 2 into a valid array.
    /// </summary>
    [Fact]
    public async Task DeclaredEdge_PlannerRetryHint_SteersSecondAttemptIntoValidGraph()
    {
        var workspace = Path.Combine(_root, "workspace-vibe-retry");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            // Attempt 1: success_criteria is a scalar — the live failure shape, made
            // unrecoverable on purpose (a number, not a string) so only the retry path
            // can save the run.
            .WhenPlanner("[{\"task_key\":\"v1\",\"title\":\"写vibe文件\",\"description\":\"\",\"success_criteria\":42,\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .ThenPlanner("[{\"task_key\":\"v1\",\"title\":\"写vibe文件\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenMeeting("vibe 完成。")
            .WhenWorkerText("已写入 vibe.txt");

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();

        var envelope = VibeRunPackEnvelope();
        using var preview = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(preview.StatusCode == HttpStatusCode.Created || preview.StatusCode == HttpStatusCode.OK,
            $"vibe preview: {preview.StatusCode} {await preview.Content.ReadAsStringAsync()}");
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.tests.vibe-run-pack")
        {
            Content = JsonContent.Create(new { preview_id = previewBody.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", "vibe-retry-install");
        using var applyResponse = await client.SendAsync(apply);
        Assert.Equal(HttpStatusCode.Created, applyResponse.StatusCode);
        var packDetail = await client.GetFromJsonAsync<JsonElement>("/api/v1/agent-packs/tinadec.tests.vibe-run-pack");
        var modeVersionId = packDetail.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("kind").GetString() == "mode")
            .Select(resource => resource.GetProperty("version_id").GetGuid())
            .Single();

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "vibe retry project", path = workspace }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "vibe retry",
            mode_version_id = modeVersionId
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个 vibe 文件", client_message_id = "vibe-retry-c-1" });
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        var done = chunks.Last(c => KindOf(c) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());
        Assert.Equal(2, script.PlannerCalls);

        // The retry hint travels to the model on attempt 2 only, and carries the
        // parse detail + the shape contract.
        Assert.Equal(2, script.PlannerInstructions.Count);
        Assert.DoesNotContain("【重试纠正】", script.PlannerInstructions[0], StringComparison.Ordinal);
        Assert.Contains("【重试纠正】", script.PlannerInstructions[1], StringComparison.Ordinal);
        Assert.Contains("plan_parse_failed", script.PlannerInstructions[1], StringComparison.Ordinal);
        Assert.Contains("success_criteria", script.PlannerInstructions[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Generic vibe run-pack fixture (never Office content). The digest covers the
    /// Core DTO re-serialization — Core digests its own round-tripped shape, so the
    /// fixture must hash exactly what Core will hash.
    /// </summary>
    private static JsonElement VibeRunPackEnvelope()
    {
        static object Agent(string key, string layer, string role, string[] capabilities, string[] tools) => new
        {
            resource_key = key,
            slug = key,
            display_name = key,
            layer,
            role,
            capabilities,
            model_strategy = new { kind = "inherit" },
            tool_scope = tools,
            system_prompt = $"{key} system prompt",
            enabled = true,
            base_prompt_pipeline_ref = "prompt:vibe-run-base"
        };
        static object Node(string key, string agent, string layer) => new
        {
            node_key = key,
            agent_ref = $"agent:{agent}",
            layer,
            label = key,
            config = new { },
            position = (object?)null
        };
        var manifest = JsonSerializer.SerializeToElement(new
        {
            api_version = "tinadec.io/agent-pack/v1alpha1",
            kind = "AgentPack",
            metadata = new
            {
                pack_id = "tinadec.tests.vibe-run-pack",
                owner = "tinadec.tests",
                product_id = "tinadec.tests",
                name = "Vibe Run Test Pack",
                version = "1.0.0"
            },
            compatibility = new { required_core_capabilities = new[] { "graph_mode_packs" } },
            resources = new
            {
                agents = new[]
                {
                    Agent("meeting", "operation", "session_coordinator", new[] { "user.respond", "task.dispatch", "agent.create_temporary" }, Array.Empty<string>()),
                    Agent("worker.search", "execution", "task_executor", new[] { "task.dispatch" }, new[] { "mcp_search", "mcp_invoke", "read_file" }),
                    Agent("worker.global_engineering", "execution", "task_executor", new[] { "task.dispatch" }, new[] { "read_file", "write_file", "shell" })
                },
                prompt_pipelines = new[]
                {
                    new
                    {
                        resource_key = "vibe-run-base",
                        slug = "vibe-run-base",
                        display_name = "Vibe run base",
                        graph = new
                        {
                            nodes = new object[]
                            {
                                new { id = "template", type = "template", config = new { content = "vibe-run-template" } },
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
                        resource_key = "vibe-run-mode",
                        slug = "vibe-run-mode",
                        display_name = "Vibe run mode",
                        nodes = new[]
                        {
                            Node("meeting-1", "meeting", "operation"),
                            Node("search-1", "worker.search", "execution"),
                            Node("eng-1", "worker.global_engineering", "execution")
                        },
                        edges = new[]
                        {
                            new { edge_key = "e1", source_node_key = "meeting-1", target_node_key = "search-1", condition = new { request = new[] { "query" }, response = new[] { "evidence" } } },
                            new { edge_key = "e2", source_node_key = "meeting-1", target_node_key = "eng-1", condition = new { request = new[] { "task" }, response = new[] { "artifact" } } }
                        },
                        bindings = new object[]
                        {
                            // WS-4 resource envelopes: search is read-only, engineering
                            // holds the workspace write grant (the approval gate still
                            // applies to mutating tool calls).
                            new
                            {
                                node_key = "search-1",
                                agent_ref = "agent:worker.search",
                                tool_switches = new { },
                                envelope = new { resources = new { read = new[] { "" } } },
                                includes_core_reserved = false
                            },
                            new
                            {
                                node_key = "eng-1",
                                agent_ref = "agent:worker.global_engineering",
                                tool_switches = new { },
                                envelope = new { resources = new { read = new[] { "" }, write = new[] { "" } } },
                                includes_core_reserved = false
                            }
                        },
                        canvas_layout = new { }
                    }
                }
            },
            activation = new
            {
                workspace_defaults = new
                {
                    agent_ref = "agent:meeting",
                    mode_ref = "mode:vibe-run-mode",
                    prompt_pipeline_ref = "prompt:vibe-run-base"
                }
            }
        });
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
        return JsonSerializer.SerializeToElement(new
        {
            manifest,
            integrity = new { algorithm = "sha256", digest }
        });
    }

    private static Guid ToolChainPackModeVersionId(JsonElement packDetail, string modeKey) =>
        packDetail.GetProperty("resources").EnumerateArray()
            .Single(resource => resource.GetProperty("kind").GetString() == "mode"
                && resource.GetProperty("resource_key").GetString() == modeKey)
            .GetProperty("version_id").GetGuid();

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

    // ── GraphSeedPack three-tier E2E (phase 2) ──────────────────────────────

    private static async Task InstallGraphSeedPackAsync(HttpClient client, Action<System.Text.Json.Nodes.JsonNode>? mutate = null)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(FindGraphSeedManifestPath(), Encoding.UTF8))!;
        mutate?.Invoke(node);
        var manifest = JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
        // Core validates the digest over its DTO round-trip of the submitted
        // manifest — digest the same round-tripped shape, not the raw bytes.
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
        Assert.True(previewResponse.IsSuccessStatusCode,
            $"seed-pack install-preview failed ({previewResponse.StatusCode}): {await previewResponse.Content.ReadAsStringAsync()}");
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.graph.seed-pack")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", $"graph-seed-pack-install-{Guid.NewGuid():N}");
        using var applyResponse = await client.SendAsync(apply);
        Assert.True(applyResponse.StatusCode == HttpStatusCode.Created,
            $"seed-pack apply failed ({applyResponse.StatusCode}): {await applyResponse.Content.ReadAsStringAsync()}");
    }

    private static string FindGraphSeedManifestPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "apps",
                "desktop",
                "src",
                "agentPacks",
                "GraphSeedPack",
                "manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("GraphSeedPack manifest.json was not found from the test output directory.");
    }

    /// <summary>
    /// free_form tier E2E on the seed pack: the single-director mode has an empty
    /// execution roster, so the task is a spawn demand — the frozen spawnable
    /// whitelist (relationship agent_types) covers it, the director spawns the
    /// worker through the engine-authoritative root path (resource grants from
    /// the spawnable binding envelope), and the run completes.
    /// </summary>
    [Fact]
    public async Task FreeFormTier_DirectorSpawnsWhitelistedWorker_RunCompletes()
    {
        var workspace = Path.Combine(_root, "workspace-free-director");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"probe\",\"title\":\"写探针\",\"description\":\"\",\"success_criteria\":[\"文件存在\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-probe", "write_file", new Dictionary<string, object?> { ["filepath"] = "probe.txt", ["content"] = "x" })],
                [new TextContent("已写入")])
            .WhenMeeting("完成。");
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "free-director project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "free-director session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        // Switch the session onto the free_form director mode by mode_version_id
        // (PATCH persists the mode without running an interaction, keeping the
        // scripted planner/worker queue single-run).
        var directorModeVersionId = await LatestPublishedModeVersionIdAsync("free_director");
        using var modeSwitch = await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}",
            new { mode_version_id = directorModeVersionId });
        Assert.True(modeSwitch.IsSuccessStatusCode, $"free_director switch failed: {modeSwitch.StatusCode} {await modeSwitch.Content.ReadAsStringAsync()}");

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = "free-director-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        // The spawned worker's write_file parks on the approval gate (writes are
        // approval-gated by default); approve it and let the run finish.
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        var decide = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error");
        Assert.Equal("done", KindOf(done));
        Assert.Equal("completed", done.GetProperty("finish_reason").GetString());
        Assert.True(provider.CallCount >= 1, "the spawned worker's write_file should execute through the fake provider");

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration").ConfigureAwait(false);
        Assert.Equal("free_form", orchestration.GetProperty("graph").GetProperty("tier").GetString());

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var runEvents = (await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var tierEvent = Assert.Single(runEvents, e => e.EventType == "orchestration.mode_tier_decided");
        Assert.Equal("free_form", ((JsonElement)tierEvent.Payload["payload"]!).GetProperty("tier").GetString());

        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        var workers = lineage!.Where(instance => instance.GetProperty("layer").GetString() == "execution").ToArray();
        Assert.Single(workers);
        Assert.Equal("task_executor", workers[0].GetProperty("role").GetString());
        var created = Assert.Single(runEvents, e => e.EventType == "agent.created"
            && string.Equals(((JsonElement)e.Payload["payload"]!).GetProperty("agent_slug").GetString(), "global_engineering", StringComparison.Ordinal));
        var createdPayload = (JsonElement)created.Payload["payload"]!;
        Assert.Equal(workers[0].GetProperty("id").GetGuid(), createdPayload.GetProperty("agent_instance_id").GetGuid());
        Assert.True(createdPayload.TryGetProperty("author_instance_id", out var author)
            && author.ValueKind == JsonValueKind.String
            && author.GetString() != workers[0].GetProperty("id").GetString(),
            "the spawned worker's lineage audit must attribute the conversation identity as author");
    }

    /// <summary>
    /// solo_dispatch E2E on the seed pack: the master writes a plan (plan_update), dispatches a sub-task, calls task_wait, is
    /// parked while the sub-agent runs, and resumes with the sub-agent's result in the task_wait
    /// tool result — then answers from it. This is the in-run wait the dispatch contract promises.
    /// </summary>
    [Fact]
    public async Task SoloTier_MasterDispatchesWaitsAndReadsTheResult()
    {
        var workspace = Path.Combine(_root, "workspace-solo-wait");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenWorkerTurns(
                [new FunctionCallContent("call-plan", "plan_update", new Dictionary<string, object?>
                {
                    ["steps"] = new object[]
                    {
                        new Dictionary<string, object?> { ["step"] = "派 search 统计引用", ["status"] = "in_progress" },
                        new Dictionary<string, object?> { ["step"] = "据结果作答", ["status"] = "pending" }
                    }
                })],
                [new FunctionCallContent("call-dispatch", "task_dispatch", new Dictionary<string, object?>
                {
                    ["agent"] = "search",
                    ["title"] = "统计引用",
                    ["description"] = "统计工作区里 HomePage 的引用处数。"
                })],
                [new FunctionCallContent("call-wait", "task_wait", new Dictionary<string, object?>())],
                [new TextContent("HomePage 共被引用 3 处。")],
                [new TextContent("子任务报告：HomePage 共被引用 3 处。")])
            .WhenMeeting("HomePage 共被引用 3 处。");
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "solo project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        // Since E6 every mode converses through the one identity (`meeting`), so a solo session
        // starts in solo mode under exactly the conversation agent every other mode uses.
        var soloModeVersionId = await LatestPublishedModeVersionIdAsync("solo");
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "solo session", mode_version_id = soloModeVersionId })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("meeting", session.GetProperty("conversation_template_slug").GetString());
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "HomePage 被引用了几处？", client_message_id = "solo-wait-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error");
        Assert.Equal("done", KindOf(done));

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var runEvents = (await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var types = runEvents.Select(e => e.EventType).ToList();
        // The master parked, the sub-task ran, and only then did the wait resolve.
        var parked = types.IndexOf("task.wait_started");
        var resolved = types.IndexOf("task.wait_resolved");
        Assert.True(parked >= 0, "the master must park on task_wait: " + string.Join(", ", types));
        Assert.True(resolved > parked, "the wait must resolve after it parked: " + string.Join(", ", types));
        var resolvedPayload = (JsonElement)runEvents[resolved].Payload["payload"]!;
        Assert.True(resolvedPayload.GetProperty("resumed").GetBoolean());
        var awaited = Assert.Single(resolvedPayload.GetProperty("awaited").EnumerateArray());
        Assert.Equal("completed", awaited.GetProperty("status").GetString());

        // The sub-agent's answer came back to the master inside the task_wait result.
        Assert.Contains(script.WorkerToolResults, result => result.Contains("HomePage 共被引用 3 处。", StringComparison.Ordinal)
            && result.Contains("search#", StringComparison.Ordinal));
        Assert.Equal(5, script.WorkerCalls);

        // plan_update was answered by the engine in place and published for the UI.
        var plan = Assert.Single(runEvents, e => e.EventType == "plan.updated");
        var planSteps = ((JsonElement)plan.Payload["payload"]!).GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(["in_progress", "pending"], planSteps.Select(step => step.GetProperty("status").GetString()));
        Assert.Contains(script.WorkerToolResults, result => result.Contains("\"ok\"", StringComparison.Ordinal)
            && result.Contains("\"steps\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Resource governance through a real run. A sub-task whose declared write scope overlaps a held
    /// directory is refused at task_dispatch time with the holder named, so the master re-dispatches
    /// it with a free scope. The sub-agent's shell command then writes outside that scope, into the
    /// held directory: the command claimed nothing up front, so the after-the-fact check reports the
    /// change, the scope violation and the overlap — without failing the call that already happened.
    /// </summary>
    [Fact]
    public async Task WriteScope_RefusesAnOverlapAtDispatch_AndAfterTheFactCatchesAWriteOutsideIt()
    {
        var workspace = Path.Combine(_root, "workspace-write-scope");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        File.WriteAllText(Path.Combine(workspace, "src", "a.ts"), "a");
        var provider = new FakeToolProvider
        {
            OnCall = (root, request) =>
            {
                if (request.ToolId == "shell") File.WriteAllText(Path.Combine(root, "docs", "stray.md"), "written by a command");
            }
        };
        var script = new ToolScriptedClient()
            .WhenWorkerTurns(
                [new FunctionCallContent("call-d1", "task_dispatch", new Dictionary<string, object?>
                {
                    ["agent"] = "global_engineering", ["title"] = "改文档", ["description"] = "改 docs 下的文档。",
                    ["write_scope"] = new[] { "docs" }
                })],
                [new FunctionCallContent("call-d2", "task_dispatch", new Dictionary<string, object?>
                {
                    ["agent"] = "global_engineering", ["title"] = "改源码", ["description"] = "改 src 下的源码并跑一次命令。",
                    ["write_scope"] = new[] { "src" }
                })],
                [new FunctionCallContent("call-w", "task_wait", new Dictionary<string, object?>())],
                [new FunctionCallContent("call-sh", "shell", new Dictionary<string, object?> { ["command"] = "npm run gen" })],
                [new TextContent("命令已执行。")],
                [new TextContent("子任务完成。")])
            .WhenMeeting("子任务完成。");
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        // The host holds docs/ (a lease with no run: never reclaimed, and it blocks every run).
        var ledger = _factory.Services.GetRequiredService<IResourceLeaseService>();
        Assert.True((await ledger.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Path, Path.Combine(workspace, "docs"), Exclusive: true),
            SessionId: null, RunId: null, Reason: "held by the test host", Purpose: ResourceLeasePurposes.Assignment))).Granted);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "scope project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "scope session", mode_version_id = await LatestPublishedModeVersionIdAsync("solo") })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "改源码", client_message_id = "scope-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" })).StatusCode);
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));

        // Refused at dispatch, with the holder named, while the master could still react.
        Assert.Contains(script.WorkerToolResults, result => result.Contains("write_scope", StringComparison.Ordinal)
            && result.Contains("assignment", StringComparison.Ordinal));
        var runEvents = (await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        JsonElement Payload(TinadecCore.Contracts.Events.EventEnvelope e) => (JsonElement)e.Payload["payload"]!;
        var conflicts = runEvents.Where(e => e.EventType == "lease.conflict").Select(Payload).ToList();
        Assert.Contains(conflicts, payload => payload.GetProperty("detected_at").GetString() == "write_scope");

        // The command wrote docs/stray.md: outside the declared src scope, and inside the held docs/.
        var observed = Payload(Assert.Single(runEvents, e => e.EventType == "workspace.changes_observed"));
        Assert.Contains(observed.GetProperty("paths").EnumerateArray(), path => path.GetString()!.EndsWith("docs/stray.md", StringComparison.OrdinalIgnoreCase));
        var violation = Payload(Assert.Single(runEvents, e => e.EventType == "scope.violation"));
        Assert.Contains(violation.GetProperty("paths").EnumerateArray(), path => path.GetString()!.EndsWith("docs/stray.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(conflicts, payload => payload.GetProperty("detected_at").GetString() == "after_the_fact");

        // The sub-task finished, so its scope was released; the host's hold stays.
        var active2 = await ledger.ListActiveAsync();
        Assert.DoesNotContain(active2, lease => lease.RunId == runId);
        Assert.Contains(active2, lease => lease.RunId is null && lease.Purpose == ResourceLeasePurposes.Assignment);
    }

    /// <summary>
    /// The organization end to end in Team mode: a run enrols the conversation identity, the standing
    /// governance reviewer and the worker it spawns; the worker's write is refused because the host
    /// holds the file; that fact reaches the reviewer through its organization inbox (not through the
    /// run, which never waits for it); the reviewer takes its own turn with its own frozen prompt and
    /// tool scope, reads the graph and files a report about the lease; and the owner sees members,
    /// the graph and the report — with the lease's live state — through the session endpoints.
    /// </summary>
    /// <summary>Core's wire format: typed reads of API DTOs must use the snake_case the host writes.</summary>
    private static readonly JsonSerializerOptions SnakeWire = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task TeamRun_EnrolsItsOrganization_AndALeaseConflictWakesTheGovernanceReviewerWhoReports()
    {
        var workspace = Path.Combine(_root, "workspace-team-org");
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"doc\",\"title\":\"写文档\",\"description\":\"在 docs 下写说明。\",\"success_criteria\":[\"文档存在\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-doc", "write_file", new Dictionary<string, object?> { ["filepath"] = "docs/a.md", ["content"] = "x" })],
                [new TextContent("docs/a.md 被别的工作占用，没能写入。")])
            .WhenMeeting("docs 被占用，暂未写入。")
            .WhenMemberTurns(
                [new FunctionCallContent("m-graph", "graph_view", new Dictionary<string, object?>())],
                [new FunctionCallContent("m-recall", "recall_evidence", new Dictionary<string, object?> { ["query"] = "docs/a.md" })],
                [new FunctionCallContent("m-report", "org_report", new Dictionary<string, object?>
                {
                    ["kind"] = "conflict", ["severity"] = "warning", ["finding"] = "global_engineering#1 needs docs/a.md, which the host holds.",
                    ["subject_kind"] = "path", ["subject_id"] = Path.Combine(workspace, "docs"), ["evidence"] = new[] { "lease.conflict (tool_call)" },
                    ["proposed_verb"] = "ask_user", ["proposed_args"] = "release docs/ or choose another path"
                })],
                [new TextContent("Reported.")]);
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var ledger = _factory.Services.GetRequiredService<IResourceLeaseService>();
        Assert.True((await ledger.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Path, Path.Combine(workspace, "docs"), Exclusive: true),
            SessionId: null, RunId: null, Reason: "held by the test host", Purpose: ResourceLeasePurposes.Assignment))).Granted);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "team org project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "team org session", mode_version_id = await LatestPublishedModeVersionIdAsync("free_director") })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一份文档", client_message_id = "team-org-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Contains(script.WorkerToolResults, result => result.Contains("assignment", StringComparison.Ordinal));

        // The run never waited for the reviewer; its turn is owed by a durable wake. Drive it.
        var drain = _factory.Services.GetRequiredService<TinadecCore.Runtime.TinaChatWakeService>();
        OrganizationReportPage? reports = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await drain.RunPassAsync(5);
            reports = await client.GetFromJsonAsync<OrganizationReportPage>($"/api/v1/sessions/{sessionId}/organization/reports", SnakeWire);
            if (reports!.Items.Length > 0) break;
            await Task.Delay(500);
        }
        var report = Assert.Single(reports!.Items);
        Assert.Equal("governance_reviewer", report.AuthorDisplayName);
        Assert.Equal("warning", report.Severity);
        // The report shows what it is about as it is now: the host still holds docs/.
        Assert.Equal("held", report.SubjectState?.Status);

        // The reviewer's turn: its own frozen role prompt, the fact that woke it, and exactly its declared tools.
        var turn = Assert.Single(script.MemberTurns);
        Assert.Contains(TinaChatMemberTurnRunner.TurnMarker, turn.Instructions);
        Assert.Contains("治理审查智能体", turn.Instructions);
        Assert.Contains("lease_conflict", turn.Briefing);
        Assert.Equal(["graph_view", "org_directory", "org_execute_report", "org_read", "org_report", "org_send", "recall_evidence"], turn.Tools.Order().ToArray());
        // The reviewer recalled what the finished task actually said, verbatim, from the evidence archive —
        // keyword mode here (the test host has no embedding model), which is a result, not "nothing".
        var recalled = Assert.Single(script.MemberToolResults, result => result.Contains("\"mode\"", StringComparison.Ordinal));
        Assert.Contains("\"keyword\"", recalled, StringComparison.Ordinal);
        Assert.Contains("task_result", recalled, StringComparison.Ordinal);
        Assert.Contains("global_engineering#1", recalled, StringComparison.Ordinal);
        // The report itself is archived too, so a later turn can find it after the room has moved on.
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var archivedReport = await _factory.Services.GetRequiredService<IEvidenceArchive>().RecallAsync(
            new EvidenceRecallQuery(scope.TenantId, scope.WorkspaceId, sessionId, "needs docs/a.md", [EvidenceKinds.Report]));
        Assert.Equal("governance_reviewer", Assert.Single(archivedReport.Hits).Author);

        // Members: the user, the conversation identity, the standing reviewer, and the worker — offline now its run is over.
        var organization = await client.GetFromJsonAsync<OrganizationDto>($"/api/v1/sessions/{sessionId}/organization", SnakeWire);
        var byName = organization!.Members.ToDictionary(member => member.DisplayName);
        Assert.Equal(OrganizationRoles.Conversation, byName["meeting"].Role);
        Assert.Equal(OrganizationRoles.Governance, byName["governance_reviewer"].Role);
        Assert.Equal(OrganizationRoles.Executor, byName["global_engineering#1"].Role);
        Assert.Equal("offline", byName["global_engineering#1"].Presence);
        Assert.Contains(organization.Rooms, room => room.Kind == "plan");

        // The graph: the run, its task with the worker that held it, and the host's lease.
        var topology = await client.GetFromJsonAsync<SessionTopologyDto>($"/api/v1/sessions/{sessionId}/topology", SnakeWire);
        var run = Assert.Single(topology!.Runs);
        Assert.Equal(runId, run.RunId);
        Assert.Equal("free_form", run.Tier);
        Assert.Contains(run.Tasks, task => task.TaskKey == "doc" && task.Handle == "global_engineering#1");
        var member = Assert.Single(topology.Members, item => item.DisplayName == "global_engineering#1");
        Assert.NotNull(member.AgentInstanceId);
        Assert.Contains(run.Instances, instance => instance.InstanceId == member.AgentInstanceId);

        // The turn is visible where the work is: on the run whose fact woke the reviewer.
        var events = await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0);
        Assert.Contains(events, e => e.EventType == "governance.member_turn" && string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase));

        // The Team coordinator holds no tools (a tool surface would make it a solo tier), so the open
        // report reaches the next plan as context — that is how the reviewer's judgement is heard.
        var plannedBefore = script.PlannerInstructions.Count;
        var next = StartStreamingInvoke(client, sessionId, new { content = "再试一次", client_message_id = "team-org-2" });
        await next.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        await next.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(script.PlannerInstructions.Skip(plannedBefore), instructions =>
            instructions.Contains("[organization_reports]", StringComparison.Ordinal)
            && instructions.Contains("needs docs/a.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// Todo E6 (one conversation identity): every mode converses through the same agent, so a mode
    /// switch is strategy, never identity — a session moves Team → Solo and back without any
    /// adoption step and without a refusal.
    /// </summary>
    [Fact]
    public async Task ModesShareOneConversationIdentity_SwitchingNeverTouchesIt()
    {
        var workspace = Path.Combine(_root, "workspace-identity-shared");
        Directory.CreateDirectory(workspace);
        var script = new ToolScriptedClient()
            .WhenWorkerTurns([new TextContent("你好。")])
            .WhenMeeting("你好。");
        _factory = new ToolChainFactory(_root, script, new FakeToolProvider());
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "shared project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "shared session" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("meeting", session.GetProperty("conversation_template_slug").GetString());
        var sessionId = session.GetProperty("id").GetGuid();

        using var first = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions",
            new { content = "你好", client_message_id = "shared-1", mode_version_id = await LatestPublishedModeVersionIdAsync("solo"), dispatch_mode = "queued" });
        Assert.True(first.IsSuccessStatusCode, $"first message in solo mode was refused: {first.StatusCode} {await first.Content.ReadAsStringAsync()}");
        var during = await (await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("meeting", during.GetProperty("conversation_template_slug").GetString());

        using var second = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions",
            new { content = "换个模式", client_message_id = "shared-2", mode_version_id = await LatestPublishedModeVersionIdAsync("free_director"), dispatch_mode = "queued" });
        Assert.True(second.IsSuccessStatusCode, $"switching Team after Solo must not fail: {second.StatusCode} {await second.Content.ReadAsStringAsync()}");
        var after = await (await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("meeting", after.GetProperty("conversation_template_slug").GetString());
    }

    /// <summary>
    /// A session frozen on the legacy solo_master identity (pack &lt; 3.0) migrates to the unified
    /// identity at admission while nothing is running; with an active run in flight the migration
    /// must NOT happen underfoot — the freeze gate's refusal stands, because that run's roster was
    /// frozen against the identity it started with.
    /// </summary>
    [Fact]
    public async Task LegacyIdentity_MigratesWhileIdle_AndRefusesOnlyWhileARunIsActive()
    {
        var workspace = Path.Combine(_root, "workspace-identity-legacy");
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        // Solo master answers in text for the first two runs; the third run calls write_file so
        // the approval park makes the session busy.
        var script = new ToolScriptedClient()
            .WhenWorkerTurns(
                [new TextContent("你好。")],
                [new TextContent("方案：先读再动，不动文件。")],
                [new FunctionCallContent("call-doc", "write_file", new Dictionary<string, object?> { ["filepath"] = "docs/a.md", ["content"] = "x" })],
                [new TextContent("docs/a.md 已写入。")])
            .WhenMeeting("你好。");
        _factory = new ToolChainFactory(_root, script, new FakeToolProvider());
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "legacy project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "legacy session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var memoryFactory = _factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<TinadecCore.Memory.MemoryDbContext>>();
        async Task FreezeSlugAsync(string slug)
        {
            await using var db = await memoryFactory.CreateDbContextAsync();
            var row = await db.Sessions.SingleAsync(x => x.Id == sessionId);
            row.ConversationNodeKey = "meeting";
            row.ConversationTemplateSlug = slug;
            await db.SaveChangesAsync();
        }

        // Say something first so the session has history (revision > 0); then pretend it was frozen
        // on the legacy identity by an older pack.
        var first = StartStreamingInvoke(client, sessionId, new { content = "你好", client_message_id = "legacy-0", mode_version_id = await LatestPublishedModeVersionIdAsync("solo"), dispatch_mode = "parallel" });
        await first.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        await FreezeSlugAsync("solo_master");

        // Idle: the next message migrates it once (MigrateConversationIdentityAsync) and runs.
        using (var idle = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions",
            new { content = "看看方案", client_message_id = "legacy-1", mode_version_id = await LatestPublishedModeVersionIdAsync("plan"), dispatch_mode = "parallel" }))
            Assert.True(idle.IsSuccessStatusCode, $"idle migration was refused: {idle.StatusCode} {await idle.Content.ReadAsStringAsync()}");
        {
            await using var db = await memoryFactory.CreateDbContextAsync();
            Assert.Equal("meeting", (await db.Sessions.SingleAsync(x => x.Id == sessionId)).ConversationTemplateSlug);
        }

        // Busy: a run started normally (meeting identity) parks on its approval; only then pretend
        // the session was frozen on the legacy identity — every later interaction must NOT migrate
        // underfoot while that run is alive.
        for (var poll = 0; poll < 40; poll++)
        {
            var runs = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/runs");
            if (runs!.All(run => run.GetProperty("status").GetString() is "completed" or "failed" or "cancelled")) break;
            await Task.Delay(500);
        }
        var active = StartStreamingInvoke(client, sessionId, new { content = "写一份文档", client_message_id = "legacy-2", mode_version_id = await LatestPublishedModeVersionIdAsync("solo"), dispatch_mode = "parallel" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        await FreezeSlugAsync("solo_master");
        using (var busy = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions",
            new { content = "看看方案", client_message_id = "legacy-3", mode_version_id = await LatestPublishedModeVersionIdAsync("plan"), dispatch_mode = "parallel" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            var refusal = await busy.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("conversation_identity_locked_mismatch", refusal.GetProperty("code").GetString());
            Assert.Contains("新建会话", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" })).StatusCode);
        await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Plan runs on the unified conversation identity with its mutating tools switched off: the
    /// master is offered reading, planning and dispatch tools and nothing that changes the
    /// workspace, the plan pipeline reaches the model, and because every mode shares the identity
    /// since E6, the session can hand the plan over to Solo without starting a new session.
    /// </summary>
    [Fact]
    public async Task PlanMode_OffersNoWriteTool_AndHandsOverToSoloInTheSameSession()
    {
        var workspace = Path.Combine(_root, "workspace-plan");
        Directory.CreateDirectory(workspace);
        var script = new ToolScriptedClient()
            .WhenWorkerTurns(
                [new FunctionCallContent("call-plan", "plan_update", new Dictionary<string, object?>
                {
                    ["steps"] = new object[]
                    {
                        new Dictionary<string, object?> { ["step"] = "在首页加入口", ["status"] = "pending" },
                        new Dictionary<string, object?> { ["step"] = "补测试", ["status"] = "pending" }
                    }
                })],
                [new TextContent("方案：先加入口，再补测试。确认后切到 Solo 执行。")])
            .WhenMeeting("方案：先加入口，再补测试。确认后切到 Solo 执行。");
        _factory = new ToolChainFactory(_root, script, new FakeToolProvider());
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "plan project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "plan session", mode_version_id = await LatestPublishedModeVersionIdAsync("plan") })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("meeting", session.GetProperty("conversation_template_slug").GetString());
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "给首页加个入口，先出方案", client_message_id = "plan-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal("solo_dispatch", orchestration.GetProperty("graph").GetProperty("tier").GetString());
        var offered = script.WorkerToolNames[0];
        foreach (var tool in new[] { "read_file", "git_diff", "plan_update", "task_dispatch", "task_wait", "graph_view", "org_read" })
            Assert.Contains(tool, offered);
        // Reading the organization is reading; posting into it and closing reports are not.
        foreach (var tool in new[] { "write_file", "replace_lines", "insert_line", "delete_line", "shell", "git_commit", "tina_chat_send", "tina_chat_execute_intent", "org_send", "org_decide_report" })
            Assert.DoesNotContain(tool, offered);
        Assert.Contains("本模式是规划（Plan）", script.WorkerInstructions[0], StringComparison.Ordinal);

        using var handOver = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions",
            new { content = "确认，开始做", client_message_id = "plan-2", mode_version_id = await LatestPublishedModeVersionIdAsync("solo"), dispatch_mode = "queued" });
        Assert.True(handOver.IsSuccessStatusCode, $"Plan -> Solo hand-over was refused: {handOver.StatusCode} {await handOver.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Review sends each lens to its own reviewer instance: two tasks assigned to `reviewer` spawn
    /// two separate reviewers (independent contexts), each offered reading tools only, and the
    /// coordinator answers from both.
    /// </summary>
    [Fact]
    public async Task ReviewMode_SpawnsOneReadOnlyReviewerPerLens()
    {
        var workspace = Path.Combine(_root, "workspace-review");
        Directory.CreateDirectory(workspace);
        const string lens = "{{\"task_key\":\"{0}\",\"title\":\"{1}\",\"description\":\"审查工作区未提交的改动：{1}。\",\"success_criteria\":[\"每条问题有位置与证据\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[],\"assignee\":\"reviewer\",\"priority\":1,\"risk\":\"low\"}}";
        var script = new ToolScriptedClient()
            .WhenPlanner("[" + string.Format(lens, "correctness", "正确性") + "," + string.Format(lens, "security", "安全") + "]")
            .WhenWorkerTurns([new TextContent("正确性：未发现问题。")], [new TextContent("安全：未发现问题。")])
            .WhenMeeting("两位评审都没有发现问题。");
        _factory = new ToolChainFactory(_root, script, new FakeToolProvider());
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "review project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "review session", mode_version_id = await LatestPublishedModeVersionIdAsync("review") })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "帮我审一下改动", client_message_id = "review-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal("free_form", orchestration.GetProperty("graph").GetProperty("tier").GetString());
        var runEvents = (await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var reviewers = runEvents.Where(e => e.EventType == "agent.created"
            && string.Equals(((JsonElement)e.Payload["payload"]!).GetProperty("agent_slug").GetString(), "reviewer", StringComparison.Ordinal))
            .Select(e => ((JsonElement)e.Payload["payload"]!).GetProperty("agent_instance_id").GetGuid())
            .ToList();
        Assert.Equal(2, reviewers.Distinct().Count());
        Assert.Equal(2, script.WorkerCalls);
        foreach (var offered in script.WorkerToolNames)
        {
            Assert.Contains("git_diff", offered);
            foreach (var tool in new[] { "write_file", "shell", "git_commit", "web_fetch", "mcp_invoke" })
                Assert.DoesNotContain(tool, offered);
        }
    }

    /// <summary>
    /// Spec is a free-form director whose pipeline carries the spec-driven procedure. Its first
    /// turn may legitimately plan nothing and just ask the user about the requirements: an empty
    /// plan completes the run with the coordinator's answer.
    /// </summary>
    [Fact]
    public async Task SpecMode_ReachesThePlannerWithTheSpecProcedure_AndMayOpenWithQuestions()
    {
        var workspace = Path.Combine(_root, "workspace-spec");
        Directory.CreateDirectory(workspace);
        var script = new ToolScriptedClient()
            .WhenPlanner("[]")
            .WhenMeeting("开始写需求之前，想先确认两件事：……");
        _factory = new ToolChainFactory(_root, script, new FakeToolProvider());
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "spec project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "spec session", mode_version_id = await LatestPublishedModeVersionIdAsync("spec") })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "我想做一个导出功能", client_message_id = "spec-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.Equal("free_form", orchestration.GetProperty("graph").GetProperty("tier").GetString());
        var planner = Assert.Single(script.PlannerInstructions);
        Assert.Contains("本模式是规格驱动开发（Spec）", planner, StringComparison.Ordinal);
        Assert.Contains("requirements.md", planner, StringComparison.Ordinal);
        Assert.Equal(0, script.WorkerCalls);
    }

    /// <summary>
    /// deterministic tier E2E on the seed pack: the whitelist still covers a task
    /// whose roster coverage was narrowed away (tool_switches removed write_file),
    /// but the tier denies spawn — the task fails closed with
    /// graph_tier_spawn_denied and NO worker is spawned.
    /// </summary>
    [Fact]
    public async Task DeterministicTier_CoveredSpawnDemand_IsDenied()
    {
        var workspace = Path.Combine(_root, "workspace-fixed-pipeline");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"probe\",\"title\":\"写探针\",\"description\":\"\",\"success_criteria\":[\"文件存在\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenMeeting("任务无法完成。");
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        // The shipped pack no longer narrows the strict pipeline, so no roster gap exists for a
        // template to fill. Recreate one here: switch write_file off on the engineering node so
        // only the spawnable template covers the task — the demand the deterministic tier must deny.
        await InstallGraphSeedPackAsync(client, manifest =>
        {
            var fixedPipeline = manifest["resources"]!["modes"]!.AsArray()
                .First(mode => (string?)mode!["resource_key"] == "fixed_pipeline")!;
            var engineering = fixedPipeline["bindings"]!.AsArray()
                .First(binding => (string?)binding!["agent_ref"] == "agent:global_engineering")!;
            engineering["tool_switches"] = new System.Text.Json.Nodes.JsonObject { ["write_file"] = false };
        });

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "fixed-pipeline project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "fixed-pipeline session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var pipelineModeVersionId = await LatestPublishedModeVersionIdAsync("fixed_pipeline");
        using var modeSwitch = await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}",
            new { mode_version_id = pipelineModeVersionId });
        Assert.True(modeSwitch.IsSuccessStatusCode, $"fixed_pipeline switch failed: {modeSwitch.StatusCode} {await modeSwitch.Content.ReadAsStringAsync()}");

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = "fixed-pipeline-c-1" });
        var ack = await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runId = ack.GetProperty("run_id").GetGuid();

        // The denial is scoped to the task (WorkerAssignmentException), but a
        // failed task is now a code-owned fact: supervision may not wash it into a
        // successful run. After the finite revision budget the run parks for user
        // review, preserving the explicit failure and spawning no worker.
        Assert.Equal("awaiting_user", await WaitForRunStatusAsync(client, runId, "awaiting_user"));
        Assert.Equal(0, provider.CallCount);

        var replay = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/replay").ConfigureAwait(false);
        var probe = Assert.Single(replay.GetProperty("tasks").EnumerateArray(), task => task.GetProperty("task_key").GetString() == "probe");
        Assert.Equal("failed", probe.GetProperty("status").GetString());
        Assert.Contains("graph_tier_spawn_denied", probe.GetProperty("summary").GetString(), StringComparison.Ordinal);

        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration").ConfigureAwait(false);
        Assert.Equal("deterministic", orchestration.GetProperty("graph").GetProperty("tier").GetString());
        var lineage = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/runs/{runId}/agent-lineage").ConfigureAwait(false);
        Assert.DoesNotContain(lineage!, instance => instance.GetProperty("layer").GetString() == "execution");
    }

    /// <summary>
    /// Runtime baseline for the budget close-out tests. The per-task context budget
    /// is deliberately tiny so one worker turn already exceeds it, while the round
    /// limit keeps the retired global default (4) so the tests also prove the loop
    /// guard now runs inside what used to be its blind zone.
    /// </summary>
    private static string TaskBudgetToml(int taskTokenBudget, int maxToolRounds = 4, int maxToolCalls = 100) =>
        "schema_version = 1\n\n"
        + "[spawn]\nmax_depth = 2\nmax_agents_per_run = 16\nmax_parallel_workers = 4\n\n"
        + "[scheduling]\nmax_active_runs_per_session = 2\nworker_retry_limit = 2\npreserve_partial_results = true\n\n"
        + "[supervision]\nrequired_before_final = true\nmax_revision_rounds = 2\n\n"
        + $"[context]\ndefault_token_budget = {taskTokenBudget}\nrecent_message_limit = 24\noptimistic_revision = true\n\n"
        + "[memory]\ncandidate_only = true\nretrieval_limit = 8\n"
        + "allowed_scopes = [\"principal\", \"workspace\", \"project\", \"agent\"]\n"
        + "allowed_kinds = [\"fact\", \"preference\", \"decision\", \"success_pattern\", \"failure_pattern\", \"task_template\", \"supervision_rule\"]\n\n"
        + $"[tools]\nprovider = \"tinadec-tools-process\"\nmutation_requires_approval = true\nserialize_workspace_writes = true\ndefault_timeout_seconds = 120\nmax_tool_rounds = {maxToolRounds}\nmax_tool_calls = {maxToolCalls}\n\n"
        + "[triggers]\nenabled = false\ncontext_token_threshold = 0\ncompress_on_task_closed = false\nrecommend_on_task_created = false\ncurate_on_run_closed = false\ngit_steward_on_run_closed = false\n";

    private const string ReadProbePlan =
        "[{\"task_key\":\"read-probe\",\"title\":\"读取探针\",\"description\":\"读取 probe.txt\",\"success_criteria\":[\"完成\"],"
        + "\"dependencies\":[],\"required_capabilities\":[\"tool.file\"],\"required_tools\":[\"read_file\"],\"priority\":1,\"risk\":\"low\"}]";

    /// <summary>
    /// WS-3/WS-5: a task that exhausts its own context budget is closed out, not
    /// failed. The trigger fires on the first round, so the call the model already
    /// produced is dropped (the provider is never called), the next turn is
    /// text-only, and the hand-off text lands as the task evidence together with
    /// the machine-readable stop reason.
    /// </summary>
    [Fact]
    public async Task TaskTokenBudgetExhausted_DropsTheCall_AndCompletesWithCloseoutEvidence()
    {
        var workspace = Path.Combine(_root, "workspace-closeout");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        const string handOff = "收尾：已读取 probe.txt；剩余：无；停止原因：本任务 token 预算耗尽。";
        var script = new ToolScriptedClient { UsageTokensPerTurn = 200 }
            .WhenPlanner(ReadProbePlan)
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已收尾。")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["filepath"] = "probe.txt" })],
                [new TextContent(handOff)]);

        _factory = new ToolChainFactory(_root, script, provider, runtimeToml: TaskBudgetToml(taskTokenBudget: 40));
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, active) = await StartRunAsync(client, workspace, "closeout", "读取探针文件");

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Assert.Equal("done", KindOf(chunks.Last(chunk => KindOf(chunk) is "done" or "error")));

        // The trigger round's call is dropped: tools are withdrawn before any side
        // effect, which is the entire reason to close out instead of failing.
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(new[] { 1, 0 }, script.WorkerToolCounts.ToArray());

        var replay = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/replay").ConfigureAwait(false);
        var task = Assert.Single(replay.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("task_key").GetString() == "read-probe");
        Assert.Equal("completed", task.GetProperty("status").GetString());
        Assert.Equal(handOff, task.GetProperty("summary").GetString());
        var evidence = task.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(handOff, evidence);
        Assert.Contains("closeout:token_budget_exhausted", evidence);
        Assert.Contains(evidence, item => item!.StartsWith("closeout_reason:", StringComparison.Ordinal));

        var milestones = replay.GetProperty("milestones").EnumerateArray()
            .Select(item => item.GetProperty("event_type").GetString()).ToArray();
        Assert.Contains("worker.tool_round", milestones);
        Assert.Contains("worker.budget_exhausted", milestones);

        // "Completed but the goal was not reached" must be visible where the
        // supervisor and the final answer look, not only in the raw event log.
        var reasons = Assert.Single(replay.GetProperty("supervision_rounds").EnumerateArray())
            .GetProperty("reasons").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(reasons, reason => reason!.StartsWith("closeout:read-probe:token_budget_exhausted", StringComparison.Ordinal));

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        var stop = Assert.Single(events, item => item.EventType == "worker.budget_exhausted");
        var payload = Assert.IsType<JsonElement>(stop.Payload["payload"]);
        Assert.Equal("read-probe", payload.GetProperty("task_key").GetString());
        Assert.Equal("token_budget_exhausted", payload.GetProperty("category").GetString());
        Assert.False(payload.GetProperty("hard_ceiling").GetBoolean());
        Assert.True(payload.GetProperty("task_tokens_used").GetInt32() >= 40);
        Assert.Equal(40, payload.GetProperty("task_token_budget").GetInt32());
        Assert.Equal(1, payload.GetProperty("dropped_calls").GetInt32());
    }

    /// <summary>
    /// WS-2/WS-4/WS-6: the loop guard now runs on every round — including the
    /// rounds that used to sit inside its blind zone (<c>max_tool_rounds</c>) — and
    /// its verdict stops the task gracefully instead of failing it. A fuse veto is
    /// reported with <c>hard_ceiling = true</c> in the event, in the evidence, and
    /// in the supervision reasons, and the guard finally receives real budget
    /// inputs instead of the defaults it silently used before.
    /// </summary>
    [Fact]
    public async Task LoopGuardVetoOnFirstRound_StopsGracefully_AndCarriesHardCeiling()
    {
        var workspace = Path.Combine(_root, "workspace-fuse");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var guard = new CapturingLoopGuard
        {
            Decision = new LoopGuardDecision
            {
                ShouldContinue = false,
                Reason = "Tool call fuse tripped: 100/100",
                Category = RunErrorTaxonomy.ToolCallCeiling,
                HardCeiling = true
            }
        };
        var script = new ToolScriptedClient { UsageTokensPerTurn = 7 }
            .WhenPlanner(ReadProbePlan)
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("已收尾。")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["filepath"] = "probe.txt" })],
                [new TextContent("收尾：触发绝对保险丝。")]);

        _factory = new ToolChainFactory(_root, script, provider, runtimeToml: TaskBudgetToml(taskTokenBudget: 4096), loopGuardOverride: guard);
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, active) = await StartRunAsync(client, workspace, "fuse", "读取探针文件");

        // hard_ceiling is an abnormal stop. Even if the close-out model claims
        // completion, the code-owned outcome remains blocked and the finite
        // supervision budget ultimately parks the run for user review.
        Assert.Equal("awaiting_user", await WaitForRunStatusAsync(client, runId, "awaiting_user"));
        Assert.Equal(0, provider.CallCount);

        // The guard was consulted on the FIRST round: ToolRounds was still inside the
        // `> MaxToolRounds` blind zone the old precondition created, and every budget
        // field now arrives populated (tokens/calls/iteration used to default to 0,
        // which made the token and error checks dead code).
        var context = Assert.IsType<LoopGuardContext>(guard.Contexts.First());
        Assert.Equal(0, context.Iteration);
        Assert.Equal(4, context.MaxIterations);
        Assert.Equal(7, context.TokensUsed);
        Assert.Equal(4096, context.TokenBudget);
        Assert.Equal(100, context.MaxToolCalls);
        Assert.Equal(0, context.ToolCallCount);
        Assert.Empty(context.RecentToolCallFingerprints);

        var replay = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/replay").ConfigureAwait(false);
        var task = Assert.Single(replay.GetProperty("tasks").EnumerateArray(), item => item.GetProperty("task_key").GetString() == "read-probe");
        Assert.Equal("blocked", task.GetProperty("status").GetString());
        var evidence = task.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("closeout:tool_call_ceiling", evidence);
        Assert.Contains("hard_ceiling:true", evidence);

        var supervisionRounds = replay.GetProperty("supervision_rounds").EnumerateArray().ToArray();
        Assert.NotEmpty(supervisionRounds);
        Assert.Contains(supervisionRounds, round => round.GetProperty("reasons").EnumerateArray()
            .Select(item => item.GetString())
            .Any(reason => reason!.Contains("closeout:read-probe:tool_call_ceiling (hard_ceiling)", StringComparison.Ordinal)));

        var manager = _factory.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0).ConfigureAwait(false);
        var stop = events.First(item => item.EventType == "worker.budget_exhausted");
        var payload = Assert.IsType<JsonElement>(stop.Payload["payload"]);
        Assert.Equal("tool_call_ceiling", payload.GetProperty("category").GetString());
        Assert.True(payload.GetProperty("hard_ceiling").GetBoolean());

        var round = events.First(item => item.EventType == "worker.tool_round"
            && string.Equals(item.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase));
        var roundPayload = Assert.IsType<JsonElement>(round.Payload["payload"]);
        Assert.Equal(1, roundPayload.GetProperty("round").GetInt32());
        Assert.Equal(1, roundPayload.GetProperty("calls_this_round").GetInt32());
        Assert.Equal(1, roundPayload.GetProperty("calls_total").GetInt32());
        Assert.Equal(4, roundPayload.GetProperty("effective_round_limit").GetInt32());
        Assert.Equal("global_default", roundPayload.GetProperty("round_limit_source").GetString());
    }

    private sealed class FakeToolProvider : IToolProvider
    {
        private readonly object _lock = new();

        public int CallCount { get; private set; }
        public List<string> ReceivedToolIds { get; } = [];
        public bool ReceivedApproved { get; private set; }

        /// <summary>1-based dispatch number that must fail on the wire; -1 disables.</summary>
        public int FailOnCallNumber { get; set; } = -1;
        public Func<ToolWireRequestDto, bool>? FailWhen { get; init; }
        public string ShellRisk { get; init; } = "high";

        /// <summary>Optional side effect per call (workspace root, request): how a test makes a fake command change files.</summary>
        public Action<string, ToolWireRequestDto>? OnCall { get; set; }

        /// <summary>Optional result per call (workspace root, request); null keeps the default <c>{ ok, tool }</c>.</summary>
        public Func<string, ToolWireRequestDto, object?>? ResultFor { get; set; }

        /// <summary>Optional wait inside a call, before it answers: how a test keeps a tool call in flight.</summary>
        public Func<ToolWireRequestDto, Task>? Hold { get; set; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public async Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (Hold is { } hold) await hold(request);
            return Answer(workspaceRoot, request);
        }

        private ToolWireResponseDto Answer(string workspaceRoot, ToolWireRequestDto request)
        {
            int callNumber;
            lock (_lock)
            {
                callNumber = ++CallCount;
                ReceivedToolIds.Add(request.ToolId);
                ReceivedApproved |= request.Approved;
            }
            OnCall?.Invoke(workspaceRoot, request);
            if (callNumber == FailOnCallNumber || FailWhen?.Invoke(request) == true)
            {
                return new ToolWireResponseDto
                {
                    CallId = request.ToolCallId,
                    IsSuccess = false,
                    Error = "tool_error: scripted provider failure"
                };
            }
            return new ToolWireResponseDto
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Result = ResultFor?.Invoke(workspaceRoot, request) is { } custom
                    ? JsonSerializer.SerializeToElement(custom)
                    : JsonSerializer.SerializeToElement(new { ok = true, tool = request.ToolId })
            };
        }

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private ToolManifestDto CreateManifest()
        {
            // Covers the GraphSeedPack templates' declared tool scopes so the
            // spawnable-ceiling manifest intersection passes at admission; the
            // fake provider answers every call, so extra entries are inert.
            var tools = new List<ToolManifestEntryDto>
            {
                new()
                {
                    Id = "write_file",
                    Description = "In-process fake write probe",
                    RequiresApproval = true,
                    Risk = "medium",
                    MutatesWorkspace = true
                },
                new() { Id = "read_file", Description = "In-process fake read probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "ls", Description = "In-process fake directory listing probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "stat", Description = "In-process fake stat probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "file_search", Description = "In-process fake file search probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "shell", Description = "In-process fake shell probe", RequiresApproval = true, Risk = ShellRisk, MutatesWorkspace = true },
                new() { Id = "mcp_search", Description = "In-process fake search probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                // Mirrors the real descriptor: an approved MCP call is an external
                // surface, not a workspace mutation (declared explicitly on the tool).
                new() { Id = "mcp_invoke", Description = "In-process fake mcp probe", RequiresApproval = true, Risk = "medium", MutatesWorkspace = false },
                // Same shape for web_fetch: egress, approval-gated, not a mutation. The
                // GraphSeedPack's search template now declares it, and the spawnable
                // ceiling intersects declared scopes against the frozen manifest — an
                // unadvertised id there denies the spawn rather than widening the surface.
                new() { Id = "web_fetch", Description = "In-process fake web probe", RequiresApproval = true, Risk = "high", MutatesWorkspace = false },
                // The git tools are part of the GraphSeedPack templates' declared scope
                // (read tooling plus the engineering write set), so the
                // spawnable-ceiling intersection needs them in the frozen manifest even
                // though no scenario here calls them.
                new() { Id = "git_status", Description = "In-process fake git status probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "git_diff", Description = "In-process fake git diff probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "git_log", Description = "In-process fake git log probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "git_branch_list", Description = "In-process fake git branch probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "git_commit", Description = "In-process fake git commit probe", RequiresApproval = true, Risk = "high", MutatesWorkspace = true },
                new() { Id = "git_push", Description = "In-process fake git push probe", RequiresApproval = true, Risk = "high", MutatesWorkspace = true },
                // GraphSeedPack 2.15.0: the solo master opens and removes worktrees for parallel sub-tasks.
                new() { Id = "git_worktree_create", Description = "In-process fake worktree create probe", RequiresApproval = true, Risk = "high", MutatesWorkspace = true },
                new() { Id = "git_worktree_remove", Description = "In-process fake worktree remove probe", RequiresApproval = true, Risk = "high", MutatesWorkspace = true },
                // GraphSeedPack 2.9.0 read tooling (search) and line-level edits (engineering, solo master).
                new() { Id = "git_blame", Description = "In-process fake git blame probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "git_file_at_revision", Description = "In-process fake git show probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "mcp_list", Description = "In-process fake mcp list probe", RequiresApproval = false, Risk = "low", MutatesWorkspace = false },
                new() { Id = "replace_lines", Description = "In-process fake line replace probe", RequiresApproval = true, Risk = "medium", MutatesWorkspace = true },
                new() { Id = "insert_line", Description = "In-process fake line insert probe", RequiresApproval = true, Risk = "medium", MutatesWorkspace = true },
                new() { Id = "delete_line", Description = "In-process fake line delete probe", RequiresApproval = true, Risk = "medium", MutatesWorkspace = true }
            };
            return new ToolManifestDto
            {
                ProtocolVersion = 2,
                ManifestHash = ToolManifestHasher.Compute(tools),
                Tools = tools
            };
        }
    }

    // ── Embedded tool-chain pack fixture ────────────────────────────────────

    private const string ToolChainPackId = "tinadec.tests.tool-chain-pack";

    /// <summary>
    /// Embedded full-roster fixture: the tool-chain tests exercise supervisor
    /// review, the git steward bypass, the planner/worker split, and the
    /// narrowed <c>conversation.ask</c> roster — coverage that came from the
    /// retired OfficeAgentPack file. Keeping the roster in code makes the
    /// fixture self-contained (no cross-project file dependency) and generic
    /// (Core deliverables never embed Office content).
    ///
    /// Seven modes mirror the retired pack's shapes: <c>default-mode</c> plus
    /// the composer modes, with <c>conversation.ask</c> carrying a reviewer-less
    /// two-node roster (the supervision gate skip is asserted).
    /// </summary>
    private static async Task<JsonElement> InstallToolChainPackAsync(HttpClient client)
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            manifest = ToolChainPackManifest(),
            integrity = new { algorithm = "sha256", digest = ToolChainPackDigest() }
        });
        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(previewResponse.IsSuccessStatusCode,
            $"install-preview failed ({previewResponse.StatusCode}): {await previewResponse.Content.ReadAsStringAsync()}");
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-packs/{ToolChainPackId}")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", "tool-chain-pack-install");
        using var applyResponse = await client.SendAsync(apply);
        Assert.True(applyResponse.StatusCode == HttpStatusCode.Created,
            $"tool-chain pack apply failed ({applyResponse.StatusCode}): {await applyResponse.Content.ReadAsStringAsync()}");
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-packs/{ToolChainPackId}");
    }

    private static string ToolChainPackDigest()
    {
        var manifest = ToolChainPackManifest();
        var serverOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        // Core digests its own DTO round-trip of the submitted manifest, so the
        // fixture must hash exactly what Core will hash (raw bytes would 422).
        var roundTripped = JsonSerializer.Deserialize<TinadecCore.Contracts.Dtos.AgentPackManifestDto>(
            manifest.GetRawText(), serverOptions);
        var serverElement = JsonSerializer.SerializeToElement(roundTripped, serverOptions);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(JsonCanonicalizer.Canonicalize(serverElement)))
            .ToLowerInvariant();
    }

    private static JsonElement ToolChainPackManifest()
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

        // The git steward's review prompt is injected by the engine, not the pack,
        // and the batch-provider script routes on that instruction; the roster just
        // needs the role to exist with supervision/review capabilities.
        var meeting = Agent("meeting", "operation", "session_coordinator",
            ["user.respond", "task.dispatch", "agent.create_temporary", "agent.create_persistent", "agent.create_profile"], [], "meeting-system");
        var contextCompressor = Agent("context_compressor", "operation", "context_maintenance",
            ["context.read", "context.patch"], [], "context-system");
        var skillRecommender = Agent("skill_recommender", "operation", "capability_advisor",
            ["tool.search", "agent.propose"], [], "skill-system");
        var supervisor = Agent("supervisor", "operation", "quality_controller",
            ["supervision.review"], [], "supervisor-system");
        var evolution = Agent("evolution", "operation", "experience_curator",
            ["memory.candidate", "agent.candidate", "agent.create_persistent"], [], "evolution-system");
        var gitSteward = Agent("git_steward", "operation", "git_steward",
            ["git.review", "git.commit_plan", "approval.request"], [], "git-steward-system");
        var taskPlanner = Agent("task_planner", "execution", "execution_coordinator",
            ["task.plan", "task.replan", "agent.create_temporary"], ["*"], "planner-system");
        // Worker declarations are ceilings: a task's required_tools must be covered
        // by some execution member, so each worker names the tools its scenarios use.
        var workerCode = Agent("worker.code", "execution", "task_executor",
            ["tool.code", "tool.file"], ["write_file", "read_file", "shell", "mcp_invoke", "create_workspace"], "worker-system");
        var workerDocument = Agent("worker.document", "execution", "task_executor",
            ["tool.document"], ["write_file", "read_file"], "worker-system");
        var workerData = Agent("worker.data", "execution", "task_executor",
            ["tool.data"], ["read_file", "shell"], "worker-system");
        var workerBrowser = Agent("worker.browser", "execution", "task_executor",
            ["tool.search", "tool.browser"], ["browser.search", "browser.fetch", "mcp_search", "mcp_invoke"], "worker-system");
        var workerFile = Agent("worker.file", "execution", "task_executor",
            ["tool.file"], ["write_file", "read_file"], "worker-system");
        var workerGeneral = Agent("worker.general", "execution", "task_executor",
            ["task.execute"], ["*"], "worker-system");
        var workerGit = Agent("worker.git", "execution", "git_specialist",
            ["tool.git"], ["git_status", "git_diff", "git_stage", "git_unstage", "git_commit", "git_push", "git_commit_plan"], "worker-system");

        static object Pipeline(string key, string template) => new
        {
            resource_key = key,
            slug = key,
            display_name = key,
            description = key,
            graph = new
            {
                nodes = new object[]
                {
                    new { id = "template", type = "template", config = new { content = template } },
                    new { id = "assemble", type = "assemble" }
                },
                edges = new[] { new { source = "template", target = "assemble" } }
            }
        };

        // Execution nodes carry the workspace envelope; without it every provider
        // tool call fails closed at scope resolution ("no workspace resource
        // grant"). Read is implied by write at the allow-list level, but the
        // declarations stay explicit so the fixture reads like a real pack.
        static object Mode(string key, string displayName, params (string NodeKey, string Slug, string Layer)[] nodes) => new
        {
            resource_key = key,
            slug = key,
            display_name = displayName,
            description = displayName,
            nodes = nodes.Select(node => new
            {
                node_key = node.NodeKey,
                agent_ref = $"agent:{node.Slug}",
                layer = node.Layer,
                label = node.Slug,
                config = new { },
                position = (object?)null
            }).ToArray(),
            edges = Array.Empty<object>(),
            bindings = nodes
                .Where(node => node.Layer == "execution")
                .Select(node => (object)new
                {
                    node_key = node.NodeKey,
                    agent_ref = $"agent:{node.Slug}",
                    tool_switches = new { },
                    envelope = new { resources = new { read = new[] { "" }, write = new[] { "" } } },
                    includes_core_reserved = false
                })
                .ToArray(),
            canvas_layout = new { }
        };

        return JsonSerializer.SerializeToElement(new
        {
            api_version = "tinadec.io/agent-pack/v1alpha1",
            kind = "AgentPack",
            metadata = new
            {
                pack_id = ToolChainPackId,
                owner = "tinadec.tests",
                product_id = "tinadec.tests",
                name = "Tool Chain Test Agent Pack",
                version = "1.0.0"
            },
            compatibility = new
            {
                minimum_core_version = "0.1.0",
                // Mode bindings are a graph-orchestration surface: the pack must
                // opt into those semantics, and an older Core rejects the unknown
                // capability fail-closed.
                required_core_capabilities = new[] { "graph_mode_packs" }
            },
            resources = new
            {
                agents = new[]
                {
                    meeting, contextCompressor, skillRecommender, supervisor, evolution, gitSteward,
                    taskPlanner, workerCode, workerDocument, workerData, workerBrowser, workerFile, workerGeneral, workerGit
                },
                prompt_pipelines = new[]
                {
                    Pipeline("baseline-prompt", "tool-chain-template"),
                    Pipeline("meeting-prompt", "meeting-role-template"),
                    Pipeline("planner-prompt", "planner-role-template"),
                    Pipeline("supervisor-prompt", "supervisor-role-template"),
                    Pipeline("worker-prompt", "worker-role-template")
                },
                modes = new object[]
                {
                    Mode("conversation.ask", "Ask",
                        ("ask-1", "task_planner", "execution"), ("ask-2", "worker.browser", "execution"), ("ask-3", "meeting", "operation")),
                    Mode("conversation.vibe", "Vibe",
                        ("vibe-1", "task_planner", "execution"), ("vibe-2", "worker.code", "execution"), ("vibe-3", "worker.browser", "execution"), ("vibe-4", "meeting", "operation")),
                    Mode("conversation.plan", "Plan",
                        ("plan-1", "task_planner", "execution"), ("plan-2", "worker.code", "execution"), ("plan-3", "worker.document", "execution"), ("plan-4", "worker.file", "execution"), ("plan-5", "supervisor", "operation"), ("plan-6", "meeting", "operation")),
                    Mode("conversation.spec", "Spec",
                        ("spec-1", "task_planner", "execution"), ("spec-2", "worker.document", "execution"), ("spec-3", "worker.code", "execution"), ("spec-4", "worker.git", "execution"), ("spec-5", "supervisor", "operation"), ("spec-6", "meeting", "operation")),
                    Mode("conversation.auto", "Auto",
                        ("auto-1", "task_planner", "execution"), ("auto-2", "worker.code", "execution"), ("auto-3", "worker.document", "execution"), ("auto-4", "worker.data", "execution"),
                        ("auto-5", "worker.browser", "execution"), ("auto-6", "worker.file", "execution"), ("auto-7", "worker.git", "execution"), ("auto-8", "supervisor", "operation"),
                        ("auto-9", "meeting", "operation")),
                    Mode("conversation.agent", "Agent",
                        ("agent-1", "task_planner", "execution"), ("agent-2", "worker.code", "execution"), ("agent-3", "worker.document", "execution"),
                        ("agent-4", "worker.data", "execution"), ("agent-5", "worker.browser", "execution"), ("agent-6", "worker.file", "execution"),
                        ("agent-7", "worker.git", "execution"), ("agent-8", "worker.general", "execution"), ("agent-9", "context_compressor", "operation"),
                        ("agent-10", "skill_recommender", "operation"), ("agent-11", "supervisor", "operation"), ("agent-12", "evolution", "operation"),
                        ("agent-13", "meeting", "operation")),
                    Mode("default-mode", "Default",
                        ("executor-1", "task_planner", "execution"), ("executor-2", "worker.code", "execution"), ("executor-3", "worker.document", "execution"),
                        ("executor-4", "worker.data", "execution"), ("executor-5", "worker.browser", "execution"), ("executor-6", "worker.file", "execution"),
                        ("executor-7", "worker.git", "execution"), ("executor-8", "worker.general", "execution"), ("meeting-1", "meeting", "operation"),
                        ("meeting-2", "context_compressor", "operation"), ("meeting-3", "skill_recommender", "operation"), ("meeting-4", "supervisor", "operation"),
                        ("meeting-5", "evolution", "operation"), ("meeting-6", "git_steward", "operation"))
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

    private async Task<Guid> WaitForPendingApprovalAsync(HttpClient client, Guid sessionId, Guid runId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/api/v1/approvals?status=pending");
            // The handler itself cannot throw (filters plus a pure mapping), so a 500 here is the
            // database refusing a read while the engine commits. The response body used to be the only
            // evidence — which for this family of reds meant a status code and nothing else, because the
            // handler maps an unmapped exception to a fixed "An unexpected error occurred."; the journal
            // is the cause it could not print.
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Approvals poll failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()} | server cause: {_factory!.LastFailure()}");
            var approvals = await response.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
            var match = approvals.FirstOrDefault(item =>
                item.GetProperty("run_id").ValueKind == JsonValueKind.String
                && Guid.TryParse(item.GetProperty("run_id").GetString(), out var candidate) && candidate == runId);
            if (match.ValueKind != JsonValueKind.Undefined) return match.GetProperty("id").GetGuid();
            await Task.Delay(300);
        }
        var orchestration = await client.GetStringAsync($"/api/v1/runs/{runId}/orchestration");
        var executions = await client.GetStringAsync($"/api/v1/sessions/{sessionId}/tool-executions");
        var approvalsAll = await client.GetStringAsync("/api/v1/approvals");
        throw new TimeoutException($"No pending approval appeared for run {runId} within {timeout}.\nOrchestration: {orchestration}\nToolExecutions: {executions}\nApprovals: {approvalsAll}");
    }

    private static string KindOf(JsonElement chunk) => chunk.GetProperty("kind").GetString()!;
    private static Guid RunIdOf(JsonElement chunk) => chunk.GetProperty("run_id").GetGuid();

    private sealed record ActiveInvoke(Task<JsonElement> Acknowledgement, Task<List<JsonElement>> Completion);

    private ActiveInvoke StartStreamingInvoke(HttpClient client, Guid sessionId, object body)
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
                await _factory!.AssertStatusAsync(admissionResponse, HttpStatusCode.Created, "Interaction admission");
                var receipt = await admissionResponse.Content.ReadFromJsonAsync<JsonElement>();
                var runId = receipt.GetProperty("run_id").GetString();
                var cursor = receipt.TryGetProperty("stream_cursor", out var sc) ? sc.GetInt64() : 0;
                var turnId = receipt.TryGetProperty("turn_id", out var tid) ? tid.GetString() : null;
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/stream?after_seq=0&turn_id={turnId}");
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
        private readonly Queue<string> _plannerFollowUps = new();
        private string? _meeting;
        private (string ToolId, Dictionary<string, object?> Arguments)? _firstWorkerTool;
        private string? _workerFollowUp;
        private string? _workerText;
        private readonly Queue<AIContent[]> _workerTurns = new();
        private readonly Dictionary<string, Queue<AIContent[]>> _taskWorkerTurns = new();
        public int WorkerCalls;
        public int StewardCalls;
        public int PlannerCalls;
        private readonly object _instructionsGate = new();
        private readonly List<string> _plannerInstructions = [];
        /// <summary>Every planner turn's instructions, in order — the retry-hint
        /// contract (attempt 2 carries the first attempt's failure back to the
        /// model) is observable only here.</summary>
        public IReadOnlyList<string> PlannerInstructions
        {
            get { lock (_instructionsGate) return _plannerInstructions.ToArray(); }
        }
        /// <summary>Tokens reported on every worker turn; 0 leaves usage unreported.
        /// Budget tests need deterministic usage, which a provider-less script
        /// otherwise never produces.</summary>
        public int UsageTokensPerTurn { get; set; }
        /// <summary>Tool declaration count the engine sent on each worker turn, in
        /// order — the close-out contract is "the tools are withdrawn", and this is
        /// the only place that is observable.</summary>
        public List<int> WorkerToolCounts { get; } = [];
        /// <summary>The tool names each worker turn was offered, in order: what a mode really lets
        /// that agent do, as the model sees it.</summary>
        public List<string[]> WorkerToolNames { get; } = [];
        /// <summary>Each worker turn's system instructions, in order: where a mode's pipeline shows up.</summary>
        public List<string> WorkerInstructions { get; } = [];
        /// <summary>Each worker turn's message texts joined, in order: the transcript the model read.</summary>
        public List<string> WorkerPrompts { get; } = [];
        /// <summary>Every tool result the engine replayed to a worker turn, flattened, in the form
        /// the model receives it — how a test sees what an engine-executed tool like task_wait returned.</summary>
        public List<string> WorkerToolResults { get; } = [];
        /// <summary>Optional gate awaited at the start of every worker model turn
        /// (before any tool prepare), so tests can install grants/pre-authorizations
        /// while the run is durably mid-flight.</summary>
        public Task? BeforeWorker { get; set; }
        /// <summary>Optional hook awaited before a given worker turn (1-based), after the previous turn's tool ran.</summary>
        public Func<int, Task>? BeforeWorkerTurn { get; set; }
        public TaskCompletionSource? WorkerStarted { get; set; }

        public ToolScriptedClient WhenPlanner(string script) { _planner = script; return this; }
        /// <summary>Planner script consumed by the NEXT planner call after the
        /// first, in order — for retry-hint tests where attempt 1 fails and
        /// attempt 2 must recover.</summary>
        public ToolScriptedClient ThenPlanner(string script) { _plannerFollowUps.Enqueue(script); return this; }
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
        /// <summary>Per-turn worker script: each queued content set is returned for
        /// exactly one worker turn, in order; once the queue is empty the default
        /// first-call-then-text behavior resumes.</summary>
        public ToolScriptedClient WhenWorkerTurns(params AIContent[][] turns)
        {
            foreach (var turn in turns) _workerTurns.Enqueue(turn);
            return this;
        }

        public ToolScriptedClient WhenTaskWorkerTurns(string title, params AIContent[][] turns)
        {
            _taskWorkerTurns[title] = new Queue<AIContent[]>(turns);
            return this;
        }

        private readonly Queue<string> _reviewerGateVerdicts = new();
        private readonly Queue<string> _conversationGateVerdicts = new();
        /// <summary>Each delegated approval gate's model call as the gate saw it: which gate, its instructions, the facts message.</summary>
        public List<(string Gate, string Instructions, string Facts)> GateCalls { get; } = [];
        /// <summary>Verdicts the reviewer gate returns, in order (unscripted calls escalate).</summary>
        public ToolScriptedClient WhenReviewerGate(params string[] verdicts)
        {
            foreach (var verdict in verdicts) _reviewerGateVerdicts.Enqueue(verdict);
            return this;
        }
        /// <summary>Verdicts the conversation gate returns, in order (unscripted calls escalate).</summary>
        public ToolScriptedClient WhenConversationGate(params string[] verdicts)
        {
            foreach (var verdict in verdicts) _conversationGateVerdicts.Enqueue(verdict);
            return this;
        }

        private readonly Queue<AIContent[]> _memberTurns = new();
        /// <summary>Each standing organization member's turn as the model saw it: instructions, the briefing, the tools offered.</summary>
        public List<(string Instructions, string Briefing, string[] Tools)> MemberTurns { get; } = [];
        /// <summary>Every tool result a standing member's turn was handed back, in order (as the model receives it).</summary>
        public List<string> MemberToolResults { get; } = [];
        /// <summary>Per-round script for standing-member turns (governance roles woken through their inbox), in order.</summary>
        public ToolScriptedClient WhenMemberTurns(params AIContent[][] turns)
        {
            foreach (var turn in turns) _memberTurns.Enqueue(turn);
            return this;
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join('\n', messages.Select(m => m.Text));
            var instructions = options?.Instructions;
            // A delegated approval gate is one judgement call, never a task turn.
            if (instructions?.Contains(ApprovalGateJudge.GateMarker, StringComparison.Ordinal) == true)
            {
                lock (_instructionsGate)
                {
                    var gate = instructions.Contains("(conversation gate)", StringComparison.Ordinal) ? "conversation" : "reviewer";
                    GateCalls.Add((gate, instructions, prompt));
                    var queue = gate == "conversation" ? _conversationGateVerdicts : _reviewerGateVerdicts;
                    var verdict = queue.Count > 0 ? queue.Dequeue() : "{\"decision\":\"escalate\",\"rationale\":\"unscripted gate\"}";
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, verdict));
                }
            }
            // A standing member's turn never falls through to the worker script: it is not a task.
            if (instructions?.Contains(TinaChatMemberTurnRunner.TurnMarker, StringComparison.Ordinal) == true)
            {
                lock (_instructionsGate)
                {
                    var conversation = messages.ToList();
                    if (conversation.Count == 1)
                        MemberTurns.Add((instructions, conversation[0].Text ?? "", options?.Tools?.Select(tool => tool.Name).ToArray() ?? []));
                    else if (conversation[^1].Contents.OfType<FunctionResultContent>().ToArray() is { Length: > 0 } results)
                        MemberToolResults.AddRange(results.Select(result => result.Result?.ToString() ?? string.Empty));
                    var next = _memberTurns.Count > 0 ? _memberTurns.Dequeue() : [new TextContent("Nothing to report.")];
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, next));
                }
            }
            // Operational bypass roles must never fall through into the worker
            // branch: that branch consumes the scripted first-turn tool call.
            if (instructions?.Contains("You are the capability advisor", StringComparison.Ordinal) == true)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"recommendations\":[]}"));
            if (instructions?.Contains("You are the git steward", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref StewardCalls);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Change scope: scripted review. Commit boundary: single review commit."));
            }
            if (instructions?.Contains("You are the context compression agent", StringComparison.Ordinal) == true
                || instructions?.Contains("You are the experience curator", StringComparison.Ordinal) == true)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));
            if (instructions?.Contains("任务规划智能体", StringComparison.Ordinal) == true
                || instructions?.Contains("规划层", StringComparison.Ordinal) == true
                || prompt.Contains("规划", StringComparison.Ordinal) && !prompt.Contains("执行证据", StringComparison.Ordinal))
            {
                lock (_instructionsGate) _plannerInstructions.Add(instructions ?? string.Empty);
                Interlocked.Increment(ref PlannerCalls);
                // Serve queued follow-ups only AFTER the first planner call (mirror
                // ScriptedChatClient's PlannerCalls > 0 gate), so WhenPlanner is the
                // attempt-1 script and ThenPlanner the attempt-2 recovery script.
                var followUp = PlannerCalls > 1 && _plannerFollowUps.Count > 0 ? _plannerFollowUps.Dequeue() : null;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, followUp ?? _planner ?? "[]"));
            }
            if (instructions?.Contains("监督智能体", StringComparison.Ordinal) == true || prompt.Contains("执行证据", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    CompleteSupervisorVerdict(
                        _supervisorVerdicts.Count > 0
                            ? _supervisorVerdicts.Dequeue()
                            : "{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}",
                        prompt)));
            if (instructions?.Contains("You are the meeting agent", StringComparison.Ordinal) == true || prompt.Contains("Execution evidence", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, _meeting ?? "完成"));
            WorkerStarted?.TrySetResult();
            if (BeforeWorker is not null) await BeforeWorker.WaitAsync(cancellationToken);
            var workerTurn = Interlocked.Increment(ref WorkerCalls);
            var isFirstWorkerTurn = workerTurn == 1;
            if (BeforeWorkerTurn is not null) await BeforeWorkerTurn(workerTurn).WaitAsync(cancellationToken);
            WorkerToolCounts.Add(options?.Tools?.Count ?? 0);
            lock (_instructionsGate)
            {
                WorkerToolNames.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
                WorkerInstructions.Add(instructions ?? string.Empty);
                WorkerPrompts.Add(prompt);
            }
            lock (_instructionsGate)
                WorkerToolResults.AddRange(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    // Serialized the way the provider adapter sends it to the model, not JsonElement.ToString()
                    // (raw text, where non-ASCII stays escaped).
                    .Select(result => result.Result?.ToString() ?? string.Empty));
            lock (_instructionsGate)
            {
                var taskScript = _taskWorkerTurns.FirstOrDefault(pair =>
                    instructions?.Contains($"标题：{pair.Key}\n", StringComparison.Ordinal) == true).Value;
                if (taskScript is not null)
                    return WorkerResponse(new ChatMessage(ChatRole.Assistant, taskScript.Dequeue()), instructions);
                if (_workerTurns.Count > 0)
                    return WorkerResponse(new ChatMessage(ChatRole.Assistant, _workerTurns.Dequeue()), instructions);
            }
            if (_workerText is not null)
                return WorkerResponse(new ChatMessage(ChatRole.Assistant, _workerText), instructions);
            var contents = isFirstWorkerTurn
                ? new AIContent[] { new FunctionCallContent(
                    "call-1",
                    _firstWorkerTool?.ToolId ?? "write_file",
                    _firstWorkerTool?.Arguments ?? new Dictionary<string, object?> { ["filepath"] = "probe.txt", ["content"] = "hello" }) }
                : new AIContent[] { new TextContent(_workerFollowUp ?? "已写入 probe.txt") };
            return WorkerResponse(new ChatMessage(ChatRole.Assistant, contents), instructions);
        }

        private ChatResponse WorkerResponse(ChatMessage message, string? instructions)
        {
            if (!message.Contents.OfType<FunctionCallContent>().Any()
                && !string.IsNullOrWhiteSpace(message.Text))
            {
                var lines = message.Text.Replace("\r\n", "\n").TrimEnd().Split('\n').ToList();
                var markerIndex = lines.FindLastIndex(line =>
                    line.Trim().StartsWith(WorkerOutcomeProtocol.Marker, StringComparison.OrdinalIgnoreCase));
                var insertAt = markerIndex >= 0 ? markerIndex : lines.Count;
                foreach (var criterion in SuccessCriteriaOf(instructions))
                {
                    if (lines.Any(line => line.Contains(
                        WorkerOutcomeProtocol.CriterionMarker + " " + criterion,
                        StringComparison.Ordinal))) continue;
                    lines.Insert(insertAt++, $"{WorkerOutcomeProtocol.CriterionMarker} {criterion} || scripted evidence");
                }
                if (markerIndex < 0) lines.Add(WorkerOutcomeProtocol.Marker + " completed");
                message = new ChatMessage(ChatRole.Assistant, string.Join("\n", lines));
            }
            var response = new ChatResponse(message);
            if (UsageTokensPerTurn > 0)
            {
                response.Usage = new UsageDetails { InputTokenCount = UsageTokensPerTurn, OutputTokenCount = 0 };
            }
            return response;
        }

        private static IReadOnlyList<string> SuccessCriteriaOf(string? instructions)
        {
            if (string.IsNullOrWhiteSpace(instructions)) return [];
            const string marker = "成功标准：";
            var start = instructions.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return [];
            start += marker.Length;
            var end = instructions.IndexOf('\n', start);
            var value = end < 0 ? instructions[start..] : instructions[start..end];
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static string CompleteSupervisorVerdict(string verdict, string prompt)
        {
            try
            {
                using var document = JsonDocument.Parse(verdict);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return verdict;
                if (root.TryGetProperty("criteria_verdicts", out var existing)
                    && existing.ValueKind == JsonValueKind.Array
                    && existing.GetArrayLength() > 0)
                {
                    return verdict;
                }

                var decision = root.TryGetProperty("decision", out var decisionNode)
                    ? decisionNode.GetString() ?? "pass"
                    : "pass";
                var reasons = root.TryGetProperty("reasons", out var reasonsNode)
                    ? JsonSerializer.Deserialize<string[]>(reasonsNode.GetRawText()) ?? []
                    : [];
                var reviseIndexes = root.TryGetProperty("revise_task_indexes", out var indexesNode)
                    ? JsonSerializer.Deserialize<int[]>(indexesNode.GetRawText()) ?? []
                    : [];
                var criteria = SupervisorCriteriaOf(prompt)
                    .Select(item => new
                    {
                        task_key = item.TaskKey,
                        criterion = item.Criterion,
                        satisfied = true,
                        evidence = "scripted supervisor evidence"
                    })
                    .ToArray();
                return JsonSerializer.Serialize(new
                {
                    decision,
                    reasons,
                    revise_task_indexes = reviseIndexes,
                    criteria_verdicts = criteria
                });
            }
            catch (JsonException)
            {
                return verdict;
            }
        }

        private static IReadOnlyList<(string TaskKey, string Criterion)> SupervisorCriteriaOf(string prompt)
        {
            const string tasksMarker = "任务列表:\n";
            const string evidenceMarker = "\n\n执行证据";
            var start = prompt.IndexOf(tasksMarker, StringComparison.Ordinal);
            if (start < 0) return [];
            start += tasksMarker.Length;
            var end = prompt.IndexOf(evidenceMarker, start, StringComparison.Ordinal);
            var section = end < 0 ? prompt[start..] : prompt[start..end];
            var result = new List<(string TaskKey, string Criterion)>();
            foreach (var line in section.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                const string keyMarker = "key=";
                const string titleMarker = " | title=";
                const string criteriaMarker = " | criteria: ";
                var keyStart = line.IndexOf(keyMarker, StringComparison.Ordinal);
                var titleStart = line.IndexOf(titleMarker, StringComparison.Ordinal);
                var criteriaStart = line.IndexOf(criteriaMarker, StringComparison.Ordinal);
                if (keyStart < 0 || titleStart <= keyStart || criteriaStart <= titleStart) continue;
                var taskKey = line[(keyStart + keyMarker.Length)..titleStart].Trim();
                var criteriaText = line[(criteriaStart + criteriaMarker.Length)..].Trim();
                foreach (var criterion in criteriaText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    result.Add((taskKey, criterion));
                }
            }
            return result;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            // Preserve function calls and usage as well as text. The durable runtime
            // now observes the provider stream, so a text-only fake silently loses tools.
            foreach (var chunk in response.ToChatResponseUpdates())
            {
                await Task.Delay(1, cancellationToken);
                yield return chunk;
            }
        }
    }

    private sealed class ToolChainFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly ToolScriptedClient _client;
        private readonly IToolProvider? _providerOverride;
        private readonly string? _runtimeToml;
        private readonly ILoopGuard? _loopGuardOverride;
        private readonly IReadOnlyDictionary<string, string?>? _settings;

        public ToolChainFactory(
            string root,
            ToolScriptedClient client,
            IToolProvider? providerOverride = null,
            string? runtimeToml = null,
            ILoopGuard? loopGuardOverride = null,
            IReadOnlyDictionary<string, string?>? settings = null)
        {
            _root = root;
            _client = client;
            _providerOverride = providerOverride;
            _runtimeToml = runtimeToml;
            _loopGuardOverride = loopGuardOverride;
            _settings = settings;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
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
                foreach (var (key, value) in _settings ?? new Dictionary<string, string?>()) settings[key] = value;
                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAgentChatClientFactory>(new ToolScriptedFactory(_client));
                services.AddSingleton<ISecretStore>(new TestModelSecretStore());
                if (_providerOverride is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<IToolProvider>(_providerOverride));
                }
                if (_loopGuardOverride is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<ILoopGuard>(_loopGuardOverride));
                }
            });
        }
    }

    /// <summary>
    /// Records every loop-guard evaluation and answers with a scripted decision, so
    /// the engine's budget wiring can be pinned independently of the real detector
    /// thresholds. The recorded contexts are what catch a regression where the
    /// engine stops supplying tokens, call counts, or the error streak — fields
    /// that were silently left at their defaults until this round.
    /// </summary>
    private sealed class CapturingLoopGuard : ILoopGuard
    {
        public List<LoopGuardContext> Contexts { get; } = [];

        /// <summary>Decision handed back for every call; null means "continue".</summary>
        public LoopGuardDecision? Decision { get; set; }

        public Task<LoopGuardDecision> EvaluateAsync(
            string sessionId,
            string runId,
            LoopGuardContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult(Decision ?? new LoopGuardDecision());
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
