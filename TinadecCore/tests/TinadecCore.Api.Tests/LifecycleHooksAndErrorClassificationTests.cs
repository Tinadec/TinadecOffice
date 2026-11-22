using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AspNetCore;
using TinadecCore.Contracts.Dtos;
using Xunit;

namespace TinadecCore.Api.Tests;

public sealed class LifecycleHooksAndErrorClassificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tinadec-hooks-{Guid.NewGuid():N}");
    private readonly Factory _factory;

    public LifecycleHooksAndErrorClassificationTests()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task LifecycleHooksHttpEndpoints_RoundTripWorkspaceAndGlobalScopesWithOptimisticRevision()
    {
        var client = _factory.CreateClient();
        var wsRoot = Path.Combine(_root, "ws-repo");
        Directory.CreateDirectory(wsRoot);

        // Initial GET returns empty hooks and revision 0
        var initial = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/lifecycle-hooks?scope=workspace&workspace_root={Uri.EscapeDataString(wsRoot)}");
        Assert.Equal("workspace", initial.GetProperty("scope").GetString());
        Assert.Equal(0, initial.GetProperty("revision").GetInt64());
        Assert.Equal(0, initial.GetProperty("hooks").GetArrayLength());

        // Validation failure: empty command -> 400
        var invalidResp = await client.PutAsJsonAsync("/api/v1/lifecycle-hooks", new
        {
            scope = "workspace",
            workspace_root = wsRoot,
            hooks = new[]
            {
                new { id = "bad", event_name = "PreToolUse", @event = "PreToolUse", command = "   ", timeout_ms = 2000, blocking = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidResp.StatusCode);

        // Valid PUT updates revision to 1 and sets ETag
        var saveResp = await client.PutAsJsonAsync("/api/v1/lifecycle-hooks", new
        {
            scope = "workspace",
            workspace_root = wsRoot,
            expected_revision = 0,
            hooks = new[]
            {
                new { id = "block-write", name = "Block CS Writes", @event = "PreToolUse", matcher = "write_file|*.cs", command = "echo blocked && exit 1", timeout_ms = 3000, blocking = true, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saveResp.StatusCode);
        Assert.Equal("\"1\"", saveResp.Headers.ETag?.Tag);
        var saved = await saveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, saved.GetProperty("revision").GetInt64());
        Assert.Equal(1, saved.GetProperty("hooks").GetArrayLength());

        // Stale If-Match triggers 412 conflict
        using var staleReq = new HttpRequestMessage(HttpMethod.Put, "/api/v1/lifecycle-hooks")
        {
            Content = JsonContent.Create(new { scope = "workspace", workspace_root = wsRoot, hooks = Array.Empty<object>() }),
        };
        staleReq.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var staleResp = await client.SendAsync(staleReq);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResp.StatusCode);

        // Global scope works independently
        var globalSave = await client.PutAsJsonAsync("/api/v1/lifecycle-hooks", new
        {
            scope = "global",
            hooks = new[]
            {
                new { id = "idle-notify", @event = "OnSessionIdle", command = "echo idle", timeout_ms = 2000, blocking = false, enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.OK, globalSave.StatusCode);
        var globalDoc = await client.GetFromJsonAsync<JsonElement>("/api/v1/lifecycle-hooks?scope=global");
        Assert.Equal("global", globalDoc.GetProperty("scope").GetString());
        Assert.Equal(1, globalDoc.GetProperty("hooks").GetArrayLength());
    }

    [Fact]
    public async Task LifecycleHookService_EvaluatesPrePostTaskCompleteAndSessionIdleHooks()
    {
        _ = _factory.CreateClient();
        var hooks = _factory.Services.GetRequiredService<ILifecycleHookService>();
        var wsRoot = Path.Combine(_root, "ws-eval");
        Directory.CreateDirectory(wsRoot);
        var markerFile = Path.Combine(wsRoot, "hook-events.log");

        var failExitCmd = OperatingSystem.IsWindows() ? "echo pre-blocked & exit /b 2" : "echo pre-blocked && exit 2";
        var postFeedbackCmd = "echo post-lint-ok";
        var taskDoneCmd = OperatingSystem.IsWindows()
            ? $"echo %TINADEC_HOOK_EVENT%>>\"{markerFile}\""
            : $"echo \"$TINADEC_HOOK_EVENT\">>\"{markerFile}\"";

        await hooks.SaveHooksAsync(new LifecycleHooksWriteRequestDto
        {
            Scope = "workspace",
            WorkspaceRoot = wsRoot,
            Hooks =
            [
                new LifecycleHookEntryDto { Id = "pre-block", Event = LifecycleHookEvents.PreToolUse, Matcher = "write_file|*.cs", Command = failExitCmd, TimeoutMs = 4000, Blocking = true, Enabled = true },
                new LifecycleHookEntryDto { Id = "post-msg", Event = LifecycleHookEvents.PostToolUse, Matcher = "write_file", Command = postFeedbackCmd, TimeoutMs = 4000, Blocking = false, Enabled = true },
                new LifecycleHookEntryDto { Id = "on-task", Event = LifecycleHookEvents.OnTaskComplete, Command = taskDoneCmd, TimeoutMs = 4000, Blocking = false, Enabled = true },
                new LifecycleHookEntryDto { Id = "on-idle", Event = LifecycleHookEvents.OnSessionIdle, Command = taskDoneCmd, TimeoutMs = 4000, Blocking = false, Enabled = true },
            ],
        });

        // PreToolUse matching matcher blocks
        var blocked = await hooks.EvaluatePreToolUseAsync(new LifecycleHookToolContext(
            wsRoot, "write_file", 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TargetPath: "src/App.cs"));
        Assert.False(blocked.Allowed);
        Assert.Equal("pre-block", blocked.HookId);
        Assert.Contains("pre-blocked", blocked.Reason);

        // Non-matching tool is allowed
        var allowed = await hooks.EvaluatePreToolUseAsync(new LifecycleHookToolContext(
            wsRoot, "read_file", 2, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TargetPath: "README.md"));
        Assert.True(allowed.Allowed);

        // PostToolUse collects stdout feedback
        var post = await hooks.EvaluatePostToolUseAsync(new LifecycleHookToolContext(
            wsRoot, "write_file", 3, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), IsSuccess: true));
        Assert.False(post.HasBlockingFailure);
        Assert.Contains(post.Feedback, line => line.Contains("post-lint-ok", StringComparison.Ordinal));

        // OnTaskComplete + OnSessionIdle fire cleanly
        var runCtx = new LifecycleHookRunContext(wsRoot, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "worker", "completed", "done");
        await hooks.NotifyTaskCompleteAsync(runCtx);
        await hooks.NotifySessionIdleAsync(runCtx);

        Assert.True(File.Exists(markerFile));
        var logText = await File.ReadAllTextAsync(markerFile);
        Assert.Contains("OnTaskComplete", logText);
        Assert.Contains("OnSessionIdle", logText);
    }

    [Fact]
    public async Task ExceptionHandler_DoesNotHijackModelOrProviderInvalidOperationExceptionAsModelNotConfigured()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddTinadecCoreHttp();
        var app = builder.Build();
        app.UseTinadecCoreExceptionHandler();
        app.MapGet("/throw-ef-provider", () =>
        {
            throw new InvalidOperationException("The LINQ expression in model query could not be translated by SQLite Provider.");
        });
        app.MapGet("/throw-domain-model", () =>
        {
            throw new ModelProfileException(400, "model_not_configured", "No chat provider is configured.");
        });
        await app.StartAsync();

        var client = app.GetTestClient();

        // #18: InvalidOperationException mentioning "model" and "Provider" must NOT become 400 model_not_configured
        var efResp = await client.GetAsync("/throw-ef-provider");
        Assert.Equal(HttpStatusCode.Conflict, efResp.StatusCode);
        var efProblem = await efResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conflict", efProblem.GetProperty("code").GetString());
        Assert.Contains("SQLite Provider", efProblem.GetProperty("detail").GetString());

        // Explicit domain exception still maps to 400 model_not_configured
        var domainResp = await client.GetAsync("/throw-domain-model");
        Assert.Equal(HttpStatusCode.BadRequest, domainResp.StatusCode);
        var domainProblem = await domainResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("model_not_configured", domainProblem.GetProperty("code").GetString());

        await app.StopAsync();
        await app.DisposeAsync();
    }

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecTools:DefaultWorkspaceRoot"] = root,
                ["Logging:LogLevel:Default"] = "Warning",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(new StubProvider());
            });
        }
    }

    private sealed class StubProvider : IToolProvider
    {
        public Task<ToolManifestDto> EnsureStartedAsync(string root, CancellationToken ct = default) => GetManifestAsync(root, ct);
        public Task<ToolManifestDto> GetManifestAsync(string root, CancellationToken ct = default) =>
            Task.FromResult(new ToolManifestDto { ProtocolVersion = 2, Tools = [] });
        public Task<ToolWireResponseDto> CallAsync(string root, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolWireResponseDto { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) });
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
