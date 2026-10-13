using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// <c>GET /api/v1/tool-executions/{id}/preview</c> is a new read path for tool output, so
/// its security review is pinned here: scope isolation answers 404 without revealing that
/// the row exists, the stored parameters never come back (only the evidence projection),
/// a running call has no preview yet, and <c>max_bytes</c> is validated.
/// </summary>
public sealed class ToolExecutionPreviewEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-preview-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
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
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CompletedReadFile_ReturnsLinesAndEvidence_NeverTheStoredParameters()
    {
        var client = _factory!.CreateClient();
        var execution = await CompletedExecutionAsync(
            "read_file",
            """{"filepath":"src/app.ts","api_key":"sk-preview-secret"}""",
            """{"success":true,"file_hash":"h","all_contents":[{"content":{"Content":"export const a = 1;","LineNumber":1},"line_hash":"x"}]}""");

        var response = await client.GetAsync($"/api/v1/tool-executions/{execution.Id}/preview");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = JsonDocument.Parse(body).RootElement;
        Assert.Equal("completed", preview.GetProperty("status").GetString());
        Assert.Equal("file_lines", preview.GetProperty("preview").GetProperty("kind").GetString());
        Assert.Equal("export const a = 1;", preview.GetProperty("preview").GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.Equal("src/app.ts", preview.GetProperty("preview").GetProperty("path").GetString());
        Assert.DoesNotContain("sk-preview-secret", body, StringComparison.Ordinal);
        Assert.Contains("[redacted]", preview.GetProperty("arguments").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutionOfAnotherWorkspace_AnswersTheSame404AsAMissingOne()
    {
        var client = _factory!.CreateClient();
        var execution = await CompletedExecutionAsync("read_file", """{"filepath":"a.ts"}""", """{"success":true,"all_contents":[]}""");
        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            var row = await db.ToolExecutions.SingleAsync(x => x.Id == execution.Id);
            row.WorkspaceId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        var foreign = await client.GetAsync($"/api/v1/tool-executions/{execution.Id}/preview");
        var missing = await client.GetAsync($"/api/v1/tool-executions/{Guid.NewGuid()}/preview");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", foreign.Content.Headers.ContentType?.MediaType);
        var foreignProblem = await foreign.Content.ReadFromJsonAsync<JsonElement>();
        var missingProblem = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tool_execution_not_found", foreignProblem.GetProperty("code").GetString());
        Assert.Equal(missingProblem.GetProperty("detail").GetString(), foreignProblem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RunningExecution_HasNoPreviewYet()
    {
        var client = _factory!.CreateClient();
        var execution = await PreparedExecutionAsync("shell", """{"command":"npm test"}""");
        var started = await ExecutionCoordinator().TryStartAsync(execution.Id);
        Assert.Equal("running", started.Status);

        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tool-executions/{execution.Id}/preview");

        Assert.Equal("running", preview.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("preview").ValueKind);
        Assert.Contains("npm test", preview.GetProperty("arguments").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxBytes_MustBeAnInteger_AndIsClamped()
    {
        var client = _factory!.CreateClient();
        var lines = string.Join(',', Enumerable.Range(1, 60).Select(i => $$$"""{"content":{"Content":"{{{new string('z', 380)}}}","LineNumber":{{{i}}}}}"""));
        var execution = await CompletedExecutionAsync("read_file", """{"filepath":"big.ts"}""", $$"""{"success":true,"all_contents":[{{lines}}]}""");

        var invalid = await client.GetAsync($"/api/v1/tool-executions/{execution.Id}/preview?max_bytes=lots");
        var tiny = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tool-executions/{execution.Id}/preview?max_bytes=1");

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_max_bytes", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        // 1 is clamped up to the 1 KiB floor: a couple of 380-character lines, then truncated.
        Assert.True(tiny.GetProperty("truncated").GetBoolean());
        var shown = tiny.GetProperty("preview").GetProperty("lines").EnumerateArray().Sum(line => line.GetProperty("text").GetString()!.Length);
        Assert.InRange(shown, 1, 1024);
    }

    private async Task<ToolExecutionSnapshot> CompletedExecutionAsync(string toolId, string parameters, string result)
    {
        var execution = await PreparedExecutionAsync(toolId, parameters);
        var started = await ExecutionCoordinator().TryStartAsync(execution.Id);
        Assert.Equal("running", started.Status);
        return await ExecutionCoordinator().CompleteAsync(execution.Id, result, toolSuccess: true);
    }

    private async Task<ToolExecutionSnapshot> PreparedExecutionAsync(string toolId, string parameters)
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync();
        var runId = await InsertRunAsync(sessionId);
        var scope = TenantAccessor().Current;
        var preparation = await ExecutionCoordinator().PrepareAsync(new ToolExecutionPrepareRequest(
            scope.TenantId,
            scope.WorkspaceId,
            projectId,
            sessionId,
            runId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            toolId,
            "low",
            false,
            false,
            parameters,
            ToolParametersHash.Compute(parameters),
            $"preview:{Guid.NewGuid():N}",
            "preview endpoint test"));
        return preparation.Execution;
    }

    private async Task<(Guid ProjectId, Guid SessionId)> CreateProjectAndSessionAsync()
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, $"ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectPath);
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Preview", path = projectPath }, Json))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Preview" }, Json))
            .Content.ReadFromJsonAsync<JsonElement>();
        return (projectId, session.GetProperty("id").GetGuid());
    }

    private async Task<Guid> InsertRunAsync(Guid sessionId)
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
            Status = "running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private IDbContextFactory<LifecycleDbContext> DbFactory() =>
        _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();

    private IToolExecutionCoordinator ExecutionCoordinator() =>
        _factory!.Services.GetRequiredService<IToolExecutionCoordinator>();

    private ITenantContextAccessor TenantAccessor() =>
        _factory!.Services.GetRequiredService<ITenantContextAccessor>();

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
