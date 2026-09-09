using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Lifecycle;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Dispatcher resilience: transport failures stay task-level (B2), run cancel
/// interrupts in-flight provider calls (B3), the wire timeout covers the tool's
/// own timeout_ms budget (B4), shell-class embedded failure results are recorded
/// honestly without changing the dispatch outcome (B5), and a stale "running"
/// row no longer reports already_running forever (B1).
/// </summary>
public sealed class ToolDispatcherResilienceTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Parameters = "{\"command\":\"echo hi\"}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-dispatcher-tests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private WebApplicationFactory<Program>? _factory;

    public ToolDispatcherResilienceTests()
    {
        _workspace = Path.Combine(_root, "workspace");
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, true);
                break;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(250);
            }
        }
        return Task.CompletedTask;
    }

    // ── B1: stale running rows ──────────────────────────────────────────────

    [Fact]
    public async Task ResumeAsync_StaleRunningApprovalGatedExecution_ReturnsOutcomeUnknown_NotAlreadyRunning()
    {
        var tools = new[] { Tool("write_file", risk: "medium", mutates: true, requiresApproval: true) };
        var provider = new ConfigurableToolProvider(tools, OkResult());
        var (dispatcher, _, _, run, execution) = await PrepareApprovedMutatingExecutionAsync(tools, provider, "disp:stale:0:0");
        await MarkRunningWithConsumedApprovalAsync(execution);

        // Nothing registered in this process: the running row is a stale remnant.
        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        Assert.Equal(ToolDispatchStatus.OutcomeUnknown, result.Status);
        Assert.NotEqual("already_running", result.ErrorCategory);
        Assert.Equal(0, provider.CallCount);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("outcome_unknown", row.Status);
    }

    [Fact]
    public async Task ResumeAsync_LocallyHeldRunningExecution_StillReportsAlreadyRunning()
    {
        var tools = new[] { Tool("write_file", risk: "medium", mutates: true, requiresApproval: true) };
        var provider = new ConfigurableToolProvider(tools, OkResult());
        var (dispatcher, _, _, run, execution) = await PrepareApprovedMutatingExecutionAsync(tools, provider, "disp:held:0:0");
        await MarkRunningWithConsumedApprovalAsync(execution);
        var registry = _factory!.Services.GetRequiredService<IInFlightToolCallRegistry>();
        using var held = registry.TryRegister(run.Id, execution.Id);
        Assert.NotNull(held);

        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        Assert.Equal(ToolDispatchStatus.Blocked, result.Status);
        Assert.Equal("already_running", result.ErrorCategory);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("running", row.Status);
    }

    // ── B2: transport failures stay task-level ─────────────────────────────

    [Fact]
    public async Task ResumeAsync_HandshakeFailure_FailsExecution_RunSurvives()
    {
        var tools = new[] { Tool("read_file") };
        var provider = new ConfigurableToolProvider(tools, (_, _, _) =>
            throw new InvalidDataException("TinadecTools manifest handshake failed."));
        var (dispatcher, _, _, run, execution) = await PrepareReadOnlyExecutionAsync(tools, provider, "disp:handshake:0:0");

        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        Assert.Equal(ToolDispatchStatus.Failed, result.Status);
        Assert.Equal("tool_runtime_unavailable", result.ErrorCategory);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("failed", row.Status);
        Assert.Equal("tool_runtime_unavailable", row.ErrorCategory);
        var runRow = await verify.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal("executing", runRow.Status);
    }

    [Fact]
    public async Task ResumeAsync_ProviderStartupTimeout_FailsExecution_RunSurvives()
    {
        var tools = new[] { Tool("read_file") };
        var provider = new ConfigurableToolProvider(tools, (_, _, _) =>
            throw new TaskCanceledException("The TinadecTools startup timeout elapsed."));
        var (dispatcher, _, _, run, execution) = await PrepareReadOnlyExecutionAsync(tools, provider, "disp:startup:0:0");

        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        Assert.Equal(ToolDispatchStatus.Failed, result.Status);
        Assert.Equal("tool_runtime_unavailable", result.ErrorCategory);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var runRow = await verify.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal("executing", runRow.Status);
    }

    [Fact]
    public async Task PrepareAsync_HandshakeFailure_BlockedAsToolRuntimeUnavailable()
    {
        var tools = new[] { Tool("read_file") };
        var provider = new ConfigurableToolProvider(tools, OkResult())
        {
            ManifestFailure = new InvalidDataException("TinadecTools manifest handshake failed.")
        };
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("prepare-handshake");
        var run = await InsertRunAsync(sessionId, "executing");
        var taskId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var dispatcher = CreateDispatcher(provider, ScopeFor(tools, run, projectId, sessionId, taskId, agentId));

        var result = await dispatcher.PrepareAsync(new ToolDispatchRequestDto
        {
            RunId = run.Id.ToString(),
            TaskId = taskId.ToString(),
            AgentInstanceId = agentId.ToString(),
            ToolId = "read_file",
            ToolCallKey = "disp:prepare-handshake:0:0",
            Params = JsonSerializer.SerializeToElement(new { path = "probe.txt" })
        });

        Assert.Equal(ToolDispatchStatus.Blocked, result.Status);
        Assert.Equal("tool_runtime_unavailable", result.ErrorCategory);
    }

    [Fact]
    public async Task ResumeAsync_HostShutdown_PropagatesCancellation()
    {
        var tools = new[] { Tool("read_file") };
        using var hostStop = new CancellationTokenSource();
        var provider = new ConfigurableToolProvider(tools, (_, _, _) =>
        {
            // The host token fires mid-call; the OCE it causes must propagate,
            // never be downgraded into a task-level failure.
            hostStop.Cancel();
            throw new OperationCanceledException();
        });
        var (dispatcher, _, _, _, execution) = await PrepareReadOnlyExecutionAsync(tools, provider, "disp:hoststop:0:0");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.ResumeAsync(execution.Id.ToString(), hostStop.Token));
    }

    // ── B3: run cancel interrupts the in-flight call ───────────────────────

    [Fact]
    public async Task ResumeAsync_RunCancel_MutatingCall_BecomesOutcomeUnknown()
    {
        var tools = new[] { Tool("write_file", risk: "medium", mutates: true, requiresApproval: true) };
        var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ConfigurableToolProvider(tools, async (request, _, ct) =>
        {
            callStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ToolWireResponseDto { CallId = request.ToolCallId, IsSuccess = true };
        });
        var (dispatcher, _, _, run, execution) = await PrepareApprovedMutatingExecutionAsync(tools, provider, "disp:cancel-m:0:0");

        var resumeTask = dispatcher.ResumeAsync(execution.Id.ToString());
        await callStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // The run-control cancel path reaches the tool layer through the
        // terminal session control: it must interrupt the in-flight wire call.
        var control = _factory!.Services.GetRequiredService<ITerminalSessionControl>();
        await control.KillForRunAsync(run.Id);

        var result = await resumeTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ToolDispatchStatus.OutcomeUnknown, result.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("outcome_unknown", row.Status);
    }

    [Fact]
    public async Task ResumeAsync_RunCancel_ReadOnlyCall_IsCancelled()
    {
        var tools = new[] { Tool("read_file") };
        var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ConfigurableToolProvider(tools, async (request, _, ct) =>
        {
            callStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ToolWireResponseDto { CallId = request.ToolCallId, IsSuccess = true };
        });
        var (dispatcher, _, _, run, execution) = await PrepareReadOnlyExecutionAsync(tools, provider, "disp:cancel-r:0:0");

        var resumeTask = dispatcher.ResumeAsync(execution.Id.ToString());
        await callStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var control = _factory!.Services.GetRequiredService<ITerminalSessionControl>();
        await control.KillForRunAsync(run.Id);

        var result = await resumeTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ToolDispatchStatus.Blocked, result.Status);
        Assert.Equal(RunErrorTaxonomy.Cancelled, result.ErrorCategory);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("cancelled", row.Status);
    }

    // ── B4: wire timeout covers the tool's own budget ──────────────────────

    [Theory]
    [InlineData(600000, 630)]   // worker asks for 10 minutes: 600s + margin
    [InlineData(120000, 150)]   // tool timeout equal to the default: margin still applies
    [InlineData(60000, 120)]    // tool timeout below the default: default wins
    [InlineData(10000000, 1830)] // above the tool ceiling: clamp to 1800 + margin
    public async Task ResumeAsync_ToolTimeoutMs_RaisesWireTimeoutAboveDefault(int timeoutMs, int expectedSeconds)
    {
        var tools = new[] { Tool("shell") };
        var provider = new ConfigurableToolProvider(tools, OkResult());
        var (dispatcher, _, _, _, execution) = await PrepareReadOnlyExecutionAsync(
            tools, provider, $"disp:timeout:{timeoutMs}", toolId: "shell",
            parameters: $"{{\"command\":\"echo hi\",\"timeout_ms\":{timeoutMs}}}");

        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        Assert.Equal(ToolDispatchStatus.Completed, result.Status);
        var captured = Assert.Single(provider.CapturedTimeouts);
        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), captured.Value);
    }

    [Fact]
    public void ResolveWireTimeout_UnitLevels()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), ToolDispatcher.ResolveWireTimeout(null, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(120), ToolDispatcher.ResolveWireTimeout(JsonDocument.Parse("{}").RootElement, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(630), ToolDispatcher.ResolveWireTimeout(JsonDocument.Parse("{\"timeout_ms\":600000}").RootElement, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(1830), ToolDispatcher.ResolveWireTimeout(JsonDocument.Parse("{\"timeout_ms\":1800000}").RootElement, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(1830), ToolDispatcher.ResolveWireTimeout(JsonDocument.Parse("{\"timeout_ms\":99999999}").RootElement, TimeSpan.FromSeconds(120)));
    }

    // ── B5: embedded success=false stays a completed dispatch, honestly ────

    [Fact]
    public async Task ResumeAsync_ShellEmbeddedFailure_RecordsToolSuccessFalse_DispatchStaysCompleted()
    {
        var tools = new[] { Tool("shell") };
        var provider = new ConfigurableToolProvider(tools, (request, _, _) => Task.FromResult(new ToolWireResponseDto
        {
            CallId = request.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { success = false, terminal_session_id = "", command = "exit 1", status = "failed", exit_code = 1 })
        }));
        var (dispatcher, _, sessionId, _, execution) = await PrepareReadOnlyExecutionAsync(
            tools, provider, "disp:shell-fail:0:0", toolId: "shell");

        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        // The dispatch stays completed so the result still flows back to the
        // model; the embedded failure is recorded on the row and in the event.
        Assert.Equal(ToolDispatchStatus.Completed, result.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("completed", row.Status);
        Assert.False(row.ToolSuccess);

        var events = await LifecycleManager().ReplayEventsAsync(sessionId, 0);
        var completedEvent = Assert.Single(events, e => e.EventType == "tool.execution.completed");
        var payload = Assert.IsType<JsonElement>(completedEvent.Payload["payload"]);
        Assert.False(payload.GetProperty("tool_success").GetBoolean());
        Assert.Equal("warning", Assert.IsType<string>(completedEvent.Payload["severity"]));
    }

    [Fact]
    public async Task ResumeAsync_ShellEmbeddedSuccess_RecordsToolSuccessTrue()
    {
        var tools = new[] { Tool("shell") };
        var provider = new ConfigurableToolProvider(tools, (request, _, _) => Task.FromResult(new ToolWireResponseDto
        {
            CallId = request.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { success = true, terminal_session_id = "", command = "echo hi", status = "completed", exit_code = 0 })
        }));
        var (dispatcher, _, _, _, execution) = await PrepareReadOnlyExecutionAsync(
            tools, provider, "disp:shell-ok:0:0", toolId: "shell");

        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        Assert.Equal(ToolDispatchStatus.Completed, result.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.True(row.ToolSuccess);
    }

    [Fact]
    public async Task ResumeAsync_NonShellTool_DoesNotReadEmbeddedSuccess()
    {
        var tools = new[] { Tool("read_file") };
        var provider = new ConfigurableToolProvider(tools, (request, _, _) => Task.FromResult(new ToolWireResponseDto
        {
            CallId = request.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { success = false, note = "not a shell-shaped payload we honour" })
        }));
        var (dispatcher, _, sessionId, _, execution) = await PrepareReadOnlyExecutionAsync(
            tools, provider, "disp:nonshell:0:0");

        var result = await dispatcher.ResumeAsync(execution.Id.ToString());

        Assert.Equal(ToolDispatchStatus.Completed, result.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Null(row.ToolSuccess);
        var events = await LifecycleManager().ReplayEventsAsync(sessionId, 0);
        var completedEvent = Assert.Single(events, e => e.EventType == "tool.execution.completed");
        var payload = Assert.IsType<JsonElement>(completedEvent.Payload["payload"]);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tool_success").ValueKind);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<(ToolDispatcher Dispatcher, Guid ProjectId, Guid SessionId, RunRecord Run, ToolExecutionSnapshot Execution)>
        PrepareReadOnlyExecutionAsync(
            IReadOnlyList<ToolManifestEntryDto> tools,
            ConfigurableToolProvider provider,
            string toolCallKey,
            string toolId = "read_file",
            string? parameters = null)
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("disp-" + Guid.NewGuid().ToString("N")[..6]);
        var run = await InsertRunAsync(sessionId, "executing");
        var taskId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var dispatcher = CreateDispatcher(provider, ScopeFor(tools, run, projectId, sessionId, taskId, agentId));
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, taskId, agentId,
            toolCallKey, toolId, risk: "low", mutatesWorkspace: false, requiresApproval: false,
            parameters ?? Parameters);
        return (dispatcher, projectId, sessionId, run, execution);
    }

    private async Task<(ToolDispatcher Dispatcher, Guid ProjectId, Guid SessionId, RunRecord Run, ToolExecutionSnapshot Execution)>
        PrepareApprovedMutatingExecutionAsync(
            IReadOnlyList<ToolManifestEntryDto> tools,
            ConfigurableToolProvider provider,
            string toolCallKey)
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("disp-" + Guid.NewGuid().ToString("N")[..6]);
        var run = await InsertRunAsync(sessionId, "executing");
        var taskId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var dispatcher = CreateDispatcher(provider, ScopeFor(tools, run, projectId, sessionId, taskId, agentId));
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, taskId, agentId,
            toolCallKey, "write_file", risk: "medium", mutatesWorkspace: true, requiresApproval: true, Parameters);
        Assert.NotNull(execution.ApprovalId);
        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == execution.ApprovalId);
            approval.Status = "approved";
            approval.Decision = "approved";
            approval.ExecutionWindowExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            await db.SaveChangesAsync();
        }
        return (dispatcher, projectId, sessionId, run, execution);
    }

    private async Task MarkRunningWithConsumedApprovalAsync(ToolExecutionSnapshot execution)
    {
        await using var db = await DbFactory().CreateDbContextAsync();
        var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == execution.ApprovalId);
        approval.Status = "consumed";
        approval.Decision = "approved";
        approval.ConsumedByExecutionId = execution.Id;
        approval.ConsumedAt = DateTimeOffset.UtcNow;
        var row = await db.ToolExecutions.SingleAsync(x => x.Id == execution.Id);
        row.Status = "running";
        await db.SaveChangesAsync();
    }

    private ToolDispatcher CreateDispatcher(IToolProvider provider, ToolInvocationScope scope) => new(
        provider,
        new FakeScopeResolver(scope),
        ExecutionCoordinator(),
        new FakeAuthorization(),
        _factory!.Services.GetRequiredService<IWorkspaceSnapshotService>(),
        LifecycleManager(),
        _factory.Services.GetRequiredService<ITerminalSessionRegistry>(),
        _factory.Services.GetRequiredService<IInFlightToolCallRegistry>(),
        new ToolDispatchOptions(),
        NullLogger<ToolDispatcher>.Instance);

    private ToolInvocationScope ScopeFor(
        IReadOnlyList<ToolManifestEntryDto> tools,
        RunRecord run,
        Guid projectId,
        Guid sessionId,
        Guid taskId,
        Guid agentId,
        int timeoutSeconds = 120)
    {
        var tenant = TenantAccessor().Current;
        var frozen = tools
            .Select(t => new FrozenToolManifestEntry(t.Id, t.Description, t.InputSchema, t.Risk, t.MutatesWorkspace, t.RequiresApproval, t.RetrySafety, t.ConfirmationFields))
            .ToList();
        return new ToolInvocationScope(
            tenant.TenantId,
            tenant.WorkspaceId,
            tenant.PrincipalId,
            projectId,
            sessionId,
            run.Id,
            taskId,
            agentId,
            _workspace,
            ["*"],
            [],
            "frozen-config-hash",
            timeoutSeconds,
            0,
            false,
            frozen,
            ToolManifestHasher.Compute(tools),
            null);
    }

    private static ToolManifestEntryDto Tool(string id, string risk = "low", bool mutates = false, bool requiresApproval = false, string retrySafety = "safe") => new()
    {
        Id = id,
        Description = id,
        Risk = risk,
        MutatesWorkspace = mutates,
        RequiresApproval = requiresApproval,
        RetrySafety = retrySafety
    };

    private static Func<ToolWireRequestDto, TimeSpan?, CancellationToken, Task<ToolWireResponseDto>> OkResult() =>
        (request, _, _) => Task.FromResult(new ToolWireResponseDto
        {
            CallId = request.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { ok = true })
        });

    private async Task<(Guid ProjectId, Guid SessionId)> CreateProjectAndSessionAsync(string name)
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, $"{name}-workspace");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name, path = projectPath }, Json);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = name }, Json);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (projectId, session.GetProperty("id").GetGuid());
    }

    private async Task<RunRecord> InsertRunAsync(Guid sessionId, string status)
    {
        var scope = TenantAccessor().Current;
        await using var db = await DbFactory().CreateDbContextAsync();
        var run = new RunRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SessionId = sessionId,
            InitiatedByPrincipalId = Guid.NewGuid(),
            TriggerMessageId = Guid.NewGuid(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task<ToolExecutionSnapshot> PrepareExecutionAsync(
        Guid projectId,
        Guid sessionId,
        Guid runId,
        Guid taskId,
        Guid agentId,
        string toolCallKey,
        string toolId,
        string risk,
        bool mutatesWorkspace,
        bool requiresApproval,
        string parameters)
    {
        var scope = TenantAccessor().Current;
        var request = new ToolExecutionPrepareRequest(
            scope.TenantId,
            scope.WorkspaceId,
            projectId,
            sessionId,
            runId,
            taskId,
            agentId,
            toolId,
            risk,
            mutatesWorkspace,
            requiresApproval,
            parameters,
            ToolParametersHash.Compute(parameters),
            toolCallKey,
            "dispatcher resilience test");
        var preparation = await ExecutionCoordinator().PrepareAsync(request);
        return preparation.Execution;
    }

    private IDbContextFactory<LifecycleDbContext> DbFactory() =>
        _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();

    private IToolExecutionCoordinator ExecutionCoordinator() =>
        _factory!.Services.GetRequiredService<IToolExecutionCoordinator>();

    private ILifecycleManager LifecycleManager() =>
        _factory!.Services.GetRequiredService<ILifecycleManager>();

    private ITenantContextAccessor TenantAccessor() =>
        _factory!.Services.GetRequiredService<ITenantContextAccessor>();

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }

    private sealed class FakeScopeResolver(ToolInvocationScope scope) : IToolInvocationScopeResolver
    {
        public Task<ToolInvocationScope> ResolveAsync(ToolInvocationScopeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(scope);
    }

    private sealed class ConfigurableToolProvider : IToolProvider
    {
        private readonly IReadOnlyList<ToolManifestEntryDto> _tools;
        private readonly Func<ToolWireRequestDto, TimeSpan?, CancellationToken, Task<ToolWireResponseDto>> _handler;

        public ConfigurableToolProvider(
            IReadOnlyList<ToolManifestEntryDto> tools,
            Func<ToolWireRequestDto, TimeSpan?, CancellationToken, Task<ToolWireResponseDto>> handler)
        {
            _tools = tools;
            _handler = handler;
        }

        public int CallCount { get; private set; }
        public List<TimeSpan?> CapturedTimeouts { get; } = [];
        /// <summary>When set, manifest reads throw this (handshake failure).</summary>
        public Exception? ManifestFailure { get; init; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Manifest();

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Manifest();

        private Task<ToolManifestDto> Manifest()
        {
            if (ManifestFailure is not null) throw ManifestFailure;
            return Task.FromResult(new ToolManifestDto
            {
                ProtocolVersion = 2,
                ManifestHash = ToolManifestHasher.Compute(_tools),
                Tools = _tools
            });
        }

        public async Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            CapturedTimeouts.Add(timeout);
            CallCount++;
            return await _handler(request, timeout, cancellationToken);
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAuthorization : IAuthorizationService
    {
        public Task<ToolAuthorizationResult> AuthorizeToolAsync(ToolAuthorizationCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolAuthorizationResult("allowed", Decision(command.SubjectPrincipalId, command.Claim), null, Guid.NewGuid(), "nonce"));

        public Task<ToolAuthorizationResult> ConsumeToolLeaseAsync(ToolLeaseConsumptionCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolAuthorizationResult("allowed", Decision(command.SubjectPrincipalId, command.Claim)));

        private static AuthorizationDecisionSnapshot Decision(Guid principalId, CapabilityClaim claim) => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), principalId, null, claim,
            null, null, null, null, null,
            "allowed", "allowed", "test authorization", "test", "policy-hash",
            principalId, null, DateTimeOffset.UtcNow);

        public Task<PolicyBundleSnapshot> CreatePolicyBundleAsync(CreatePolicyBundleCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PolicyVersionSnapshot> PublishPolicyVersionAsync(PublishPolicyVersionCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CapabilityGrantSnapshot> GrantCapabilityAsync(GrantCapabilityCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApprovalDelegationSnapshot> CreateApprovalDelegationAsync(CreateApprovalDelegationCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PermissionResolution> RequestPermissionAsync(PermissionRequestCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PermissionResolution> DecidePermissionAsync(PermissionDecisionCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PermissionResolution?> GetPermissionRequestAsync(Guid permissionRequestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PermissionRequestSnapshot>> ListPermissionRequestsAsync(string? status = null, Guid? runId = null, Guid? taskId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LeaseConsumptionResult> TryConsumeLeaseAsync(LeaseConsumptionCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokeGrantAsync(Guid grantId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokeDelegationAsync(Guid delegationId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokeLeaseAsync(Guid leaseId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokePolicyBundleAsync(Guid policyBundleId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
