using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Lifecycle;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Terminal stdin and kill must reach the Tools child that created the session. The
/// creating dispatch hands Core a materialized context; re-deriving one on the control
/// path can hash differently (a cwd outside the frozen sources rewrites the primary
/// root's Path), which used to select a child that has never heard of the session.
/// These tests pin the dispatch shape only — the provider is a recorder, so no terminal
/// is ever really written to or killed here.
/// </summary>
public sealed class TerminalOwnershipTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-terminal-ownership-tests", Guid.NewGuid().ToString("N"));
    private Factory? _factory;
    private RecordingProvider _provider = null!;
    private ScriptedResolver _resolver = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _provider = new RecordingProvider();
        _resolver = new ScriptedResolver();
        _factory = new Factory(_root, _provider, _resolver);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Stdin_CarriesTheMaterializedContextAndAddressesTheRunsOwnProcess()
    {
        var session = await AdmitSessionAsync("stdin-context");
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/v1/terminals/{session.TerminalSessionId}/stdin", new { data = "echo hi\n" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_provider.RunCalls);
        Assert.Equal(session.RunId.ToString(), call.RunId);
        Assert.Equal(session.WorkspaceRoot, call.WorkspaceRoot);
        Assert.Equal("#terminal", call.Request.ToolId);
        Assert.Equal(session.RunId.ToString(), call.Request.SessionId);
        Assert.Equal("write", call.Request.Params!.Value.GetProperty("action").GetString());
        Assert.Equal(session.TerminalSessionId, call.Request.Params.Value.GetProperty("terminal_session_id").GetString());
        Assert.NotNull(call.Request.ExecutionContext);
        Assert.Equal("scope-a", call.Request.ExecutionContext!.StorageId);
        Assert.Equal(session.RunId.ToString(), call.Request.ExecutionContext.RunId);
        // The materializer must receive the context the creating dispatch used, not a
        // freshly resolved one, and must be asked for the reserved transport tool.
        Assert.Equal("scope-a", _resolver.LastFrozen!.StorageId);
        Assert.Equal(new[] { "#terminal" }, _resolver.LastAllowedToolIds);
        Assert.Empty(_provider.PlainCalls);
    }

    [Fact]
    public async Task Stdin_WhenTheContextCannotBeMaterialized_Returns409AndNeverDispatches()
    {
        var session = await AdmitSessionAsync("stdin-mismatch");
        _resolver.Failure = new ToolSettingsException("frozen_storage_scope_mismatch", "This historical run belongs to a different storage scope.", 409);
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/v1/terminals/{session.TerminalSessionId}/stdin", new { data = "x" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TERMINAL_CONTEXT_UNAVAILABLE", await CodeAsync(response));
        Assert.Empty(_provider.RunCalls);
        Assert.Empty(_provider.PlainCalls);
    }

    [Fact]
    public async Task Stdin_WhenTheCreatingDispatchHadNoContext_StillAddressesTheRun()
    {
        // A session created without a context was created on the root-keyed child, so
        // passing null here selects the same child. It must not become a 409.
        var session = await AdmitSessionAsync("stdin-nocontext", withContext: false);
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/v1/terminals/{session.TerminalSessionId}/stdin", new { data = "x" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_provider.RunCalls);
        Assert.Equal(session.RunId.ToString(), call.RunId);
        Assert.Null(call.Request.ExecutionContext);
        Assert.Empty(_provider.PlainCalls);
    }

    [Fact]
    public async Task Stdin_UnknownSession_Returns404()
    {
        using var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/terminals/does-not-exist/stdin", new { data = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("TERMINAL_SESSION_NOT_FOUND", await CodeAsync(response));
        Assert.Empty(_provider.RunCalls);
    }

    [Fact]
    public async Task Stdin_ClosedSession_Returns409()
    {
        var session = await AdmitSessionAsync("stdin-closed", status: "exited");
        using var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/v1/terminals/{session.TerminalSessionId}/stdin", new { data = "x" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TERMINAL_SESSION_CLOSED", await CodeAsync(response));
        Assert.Empty(_provider.RunCalls);
    }

    [Fact]
    public async Task Stdin_WhenTheProviderThrows_Returns503()
    {
        var session = await AdmitSessionAsync("stdin-provider-down");
        _provider.Throw = new IOException("child is gone");
        using var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/v1/terminals/{session.TerminalSessionId}/stdin", new { data = "x" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("TOOL_PROVIDER_UNAVAILABLE", await CodeAsync(response));
    }

    [Fact]
    public async Task Kill_DispatchesTheKillPayloadToTheRunsOwnProcess()
    {
        var session = await AdmitSessionAsync("kill-context");
        using var client = _factory!.CreateClient();

        var response = await client.PostAsync($"/api/v1/terminals/{session.TerminalSessionId}/kill", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_provider.RunCalls);
        Assert.Equal(session.RunId.ToString(), call.RunId);
        Assert.Equal("#terminal", call.Request.ToolId);
        Assert.Equal("kill", call.Request.Params!.Value.GetProperty("action").GetString());
        Assert.Equal(session.TerminalSessionId, call.Request.Params.Value.GetProperty("terminal_session_id").GetString());
        Assert.NotNull(call.Request.ExecutionContext);
        Assert.Equal("scope-a", call.Request.ExecutionContext!.StorageId);
        Assert.Empty(_provider.PlainCalls);
        Assert.False(_factory.Services.GetRequiredService<ITerminalSessionRegistry>().Find(session.TerminalSessionId)!.IsLive);
    }

    [Fact]
    public async Task Kill_WhenTheContextCannotBeMaterialized_Returns409AndLeavesTheSessionLive()
    {
        var session = await AdmitSessionAsync("kill-mismatch");
        _resolver.Failure = new ToolSettingsException("frozen_storage_scope_mismatch", "This historical run belongs to a different storage scope.", 409);
        using var client = _factory!.CreateClient();

        var response = await client.PostAsync($"/api/v1/terminals/{session.TerminalSessionId}/kill", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TERMINAL_CONTEXT_UNAVAILABLE", await CodeAsync(response));
        Assert.Empty(_provider.RunCalls);
        Assert.True(_factory.Services.GetRequiredService<ITerminalSessionRegistry>().Find(session.TerminalSessionId)!.IsLive);
    }

    [Fact]
    public async Task Kill_UnknownSession_Returns404()
    {
        using var client = _factory!.CreateClient();
        var response = await client.PostAsync("/api/v1/terminals/does-not-exist/kill", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("TERMINAL_SESSION_NOT_FOUND", await CodeAsync(response));
        Assert.Empty(_provider.RunCalls);
    }

    [Fact]
    public async Task Kill_WhenTheProviderThrows_Returns503AndLeavesTheSessionLive()
    {
        var session = await AdmitSessionAsync("kill-provider-down");
        _provider.Throw = new IOException("child is gone");
        using var client = _factory!.CreateClient();
        var response = await client.PostAsync($"/api/v1/terminals/{session.TerminalSessionId}/kill", content: null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("TOOL_PROVIDER_UNAVAILABLE", await CodeAsync(response));
        Assert.True(_factory.Services.GetRequiredService<ITerminalSessionRegistry>().Find(session.TerminalSessionId)!.IsLive);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private async Task<TerminalSessionRecord> AdmitSessionAsync(string label, string status = "long_lived", bool withContext = true)
    {
        using var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = $"Terminal {label}", path = Path.Combine(_root, $"workspace-{label}") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = $"Terminal {label}" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "terminal trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());

        var workspaceRoot = Path.Combine(_root, $"workspace-{label}");
        var record = new TerminalSessionRecord(
            $"term-{label}",
            run.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            workspaceRoot,
            "pwsh -NoLogo",
            status,
            DateTimeOffset.UtcNow,
            withContext ? new ToolExecutionContextDto
            {
                StorageId = "scope-a",
                StorageRoot = Path.Combine(_root, "storage"),
                RunId = run.Id.ToString(),
                WorkingDirectory = workspaceRoot,
                PrimaryRootId = "primary",
                WorkspaceRoots = [new ToolWorkspaceRootDto { Id = "primary", Path = workspaceRoot }]
            } : null);
        _factory.Services.GetRequiredService<ITerminalSessionRegistry>().Record(record);
        return record;
    }

    private sealed class Factory(string root, IToolProvider provider, IToolConfigurationResolver resolver) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddDebug(); });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(provider));
                services.Replace(ServiceDescriptor.Singleton(resolver));
            });
        }
    }

    private sealed record RunCall(string RunId, string WorkspaceRoot, ToolWireRequestDto Request);

    private sealed class RecordingProvider : IToolProvider
    {
        public List<RunCall> RunCalls { get; } = [];
        public List<ToolWireRequestDto> PlainCalls { get; } = [];
        public Exception? Throw { get; set; }

        public Task<ToolWireResponseDto> CallForRunAsync(string runId, string workspaceRoot, ToolWireRequestDto request, CancellationToken cancellationToken = default)
        {
            RunCalls.Add(new RunCall(runId, workspaceRoot, request));
            return RespondAsync(request);
        }

        public Task<ToolWireResponseDto> CallAsync(string workspaceRoot, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            PlainCalls.Add(request);
            return RespondAsync(request);
        }

        private Task<ToolWireResponseDto> RespondAsync(ToolWireRequestDto request) =>
            Throw is not null ? Task.FromException<ToolWireResponseDto>(Throw)
                : Task.FromResult(new ToolWireResponseDto { CallId = request.ToolCallId, IsSuccess = true });

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) => Task.FromResult(Manifest());
        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) => Task.FromResult(Manifest());
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static ToolManifestDto Manifest() => new()
        {
            ProtocolVersion = 2,
            ManifestHash = "terminal-ownership-test",
            Tools = [new ToolManifestEntryDto { Id = "#terminal", Description = "Terminal transport", InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone() }]
        };
    }

    private sealed class ScriptedResolver : IToolConfigurationResolver
    {
        public ToolSettingsException? Failure { get; set; }
        public ToolExecutionContextDto? LastFrozen { get; private set; }
        public IReadOnlyList<string>? LastAllowedToolIds { get; private set; }

        public Task<ToolExecutionContextDto> MaterializeForCallAsync(ToolExecutionContextDto frozen, IReadOnlyList<string> allowedToolIds, CancellationToken cancellationToken = default, string? runId = null, string? toolId = null, string? workingDirectory = null)
        {
            LastFrozen = frozen;
            LastAllowedToolIds = allowedToolIds;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new ToolExecutionContextDto
            {
                StorageId = frozen.StorageId,
                StorageRoot = frozen.StorageRoot,
                RunId = runId ?? frozen.RunId,
                WorkingDirectory = workingDirectory ?? frozen.WorkingDirectory,
                PrimaryRootId = frozen.PrimaryRootId,
                WorkspaceRoots = frozen.WorkspaceRoots,
                AllowedToolIds = allowedToolIds
            });
        }

        public Task<FrozenToolConfigurationDto> ResolveForRunAsync(Guid? projectId, IReadOnlyList<Guid> agentDefinitionIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolExecutionContextDto> ResolveAsync(Guid? projectId, Guid? agentDefinitionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolExecutionContextDto> ResolveForInspectionAsync(Guid? projectId, Guid? agentDefinitionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}