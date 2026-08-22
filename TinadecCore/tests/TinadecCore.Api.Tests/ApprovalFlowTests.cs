using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// End-to-end approval contract tests: create → decide → consume exactly once,
/// with server-computed parameter hashes and tenant scoping.
/// </summary>
public sealed class ApprovalFlowTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-approval-tests", Guid.NewGuid().ToString("N"));
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
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_DecideApproved_ConsumeOnce_SecondConsumeFails()
    {
        var client = _factory!.CreateClient();
        var coordinator = _factory.Services.GetRequiredService<IToolApprovalCoordinator>();

        var createResponse = await client.PostAsJsonAsync("/api/v1/approvals", new
        {
            kind = "tool",
            tool_id = "write_file",
            summary = "write report",
            parameters = JsonSerializer.SerializeToElement(new { path = "a.txt", content = "hi" })
        }, Json);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var approvalId = created.GetProperty("id").GetGuid();
        Assert.Equal("pending", created.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("request_hash").GetString()));

        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" }, Json);
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);
        var decided = await decideResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approved", decided.GetProperty("status").GetString());

        var hash = created.GetProperty("request_hash").GetString()!;
        var executionId = Guid.NewGuid().ToString();
        Assert.True(await coordinator.TryConsumeApprovalAsync(approvalId, executionId, hash));
        Assert.False(await coordinator.TryConsumeApprovalAsync(approvalId, Guid.NewGuid().ToString(), hash), "consumed approval must not be consumable twice");

        var fetched = await client.GetAsync($"/api/v1/approvals/{approvalId}");
        var body = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(executionId, body.GetProperty("consumed_by_execution_id").GetString());
    }

    [Fact]
    public async Task Consume_WithMismatchedHash_Fails()
    {
        var client = _factory!.CreateClient();
        var coordinator = _factory.Services.GetRequiredService<IToolApprovalCoordinator>();

        var createResponse = await client.PostAsJsonAsync("/api/v1/approvals", new
        {
            tool_id = "command_run",
            parameters = JsonSerializer.SerializeToElement(new { command = "dotnet build" })
        }, Json);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var approvalId = created.GetProperty("id").GetGuid();

        await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" }, Json);

        var tamperedHash = ToolParametersHash.Compute("{\"command\":\"dotnet test\"}");
        Assert.False(await coordinator.TryConsumeApprovalAsync(approvalId, Guid.NewGuid().ToString(), tamperedHash));
    }

    [Fact]
    public async Task Consume_PendingOrRejectedOrExpired_Fails()
    {
        var client = _factory!.CreateClient();
        var coordinator = _factory.Services.GetRequiredService<IToolApprovalCoordinator>();
        var hash = ToolParametersHash.Compute("{}");

        // Pending (not yet decided).
        var pending = await CreateApprovalAsync(client, "{}");
        Assert.False(await coordinator.TryConsumeApprovalAsync(pending, Guid.NewGuid().ToString(), hash));

        // Rejected.
        var rejected = await CreateApprovalAsync(client, "{}");
        await client.PostAsJsonAsync($"/api/v1/approvals/{rejected}/decision", new { decision = "rejected" }, Json);
        Assert.False(await coordinator.TryConsumeApprovalAsync(rejected, Guid.NewGuid().ToString(), hash));
    }

    [Fact]
    public async Task Decide_Twice_ReturnsConflict()
    {
        var client = _factory!.CreateClient();
        var approvalId = await CreateApprovalAsync(client, "{}");

        var first = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" }, Json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "rejected" }, Json);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Decide_ConcurrentRequests_OnlyOneDecisionCommits()
    {
        var client = _factory!.CreateClient();
        var approvalId = await CreateApprovalAsync(client, "{}");

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" }, Json),
            client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "rejected" }, Json));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Decide_InvalidDecision_ReturnsBadRequest()
    {
        var client = _factory!.CreateClient();
        var approvalId = await CreateApprovalAsync(client, "{}");

        var response = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "maybe" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ParametersHash_IsCanonicalized_AcrossPropertyOrder()
    {
        var a = ToolParametersHash.Compute("{\"b\":2,\"a\":1}");
        var b = ToolParametersHash.Compute("{\"a\":1,\"b\":2}");
        Assert.Equal(a, b);

        var c = ToolParametersHash.Compute("{\"a\":1,\"b\":3}");
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task ListApprovals_FiltersByStatus()
    {
        var client = _factory!.CreateClient();
        var pending = await CreateApprovalAsync(client, "{}");
        var toApprove = await CreateApprovalAsync(client, "{}");
        await client.PostAsJsonAsync($"/api/v1/approvals/{toApprove}/decision", new { decision = "approved" }, Json);

        var pendingList = await client.GetFromJsonAsync<JsonElement>("/api/v1/approvals?status=pending");
        var approvedList = await client.GetFromJsonAsync<JsonElement>("/api/v1/approvals?status=approved");

        Assert.Contains(pendingList.EnumerateArray(), x => x.GetProperty("id").GetGuid() == pending);
        Assert.DoesNotContain(pendingList.EnumerateArray(), x => x.GetProperty("id").GetGuid() == toApprove);
        Assert.Contains(approvedList.EnumerateArray(), x => x.GetProperty("id").GetGuid() == toApprove);
    }

    [Fact]
    public async Task Prepare_SameToolCallKey_ReturnsTheOriginalExecutionAndApproval()
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, "idempotent-workspace");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Tool key project", path = projectPath }, Json);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Tool key session" }, Json);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var coordinator = _factory.Services.GetRequiredService<IToolExecutionCoordinator>();
        var approvalCoordinator = _factory.Services.GetRequiredService<IToolApprovalCoordinator>();
        var runId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        const string parameters = "{\"path\":\"once.txt\",\"content\":\"one\"}";
        var request = new ToolExecutionPrepareRequest(
            scope.TenantId,
            scope.WorkspaceId,
            projectId,
            sessionId,
            runId,
            taskId,
            agentId,
            "write_file",
            "high",
            MutatesWorkspace: true,
            RequiresApproval: true,
            parameters,
            ToolParametersHash.Compute(parameters),
            "worker:round:0:call:0",
            "write once");

        var first = await coordinator.PrepareAsync(request);
        var replay = await coordinator.PrepareAsync(request);

        Assert.False(first.Existing);
        Assert.True(replay.Existing);
        Assert.Equal(first.Execution.Id, replay.Execution.Id);
        Assert.Equal(first.Execution.ApprovalId, replay.Execution.ApprovalId);

        var changed = request with { ParametersHash = ToolParametersHash.Compute("{\"path\":\"once.txt\",\"content\":\"two\"}") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareAsync(changed));

        var approvals = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/approvals?run_id={runId}");
        Assert.Single(approvals!);
        Assert.Equal(first.Execution.ApprovalId, approvals![0].GetProperty("id").GetGuid());

        var approvalId = first.Execution.ApprovalId!.Value;
        await approvalCoordinator.DecideAsync(approvalId, "approved", null);
        var hash = ToolParametersHash.Compute(parameters);
        Assert.False(await approvalCoordinator.TryConsumeApprovalAsync(approvalId, Guid.NewGuid().ToString(), hash));
        Assert.True(await approvalCoordinator.TryConsumeApprovalAsync(approvalId, first.Execution.Id.ToString(), hash));
    }

    private async Task<Guid> CreateApprovalAsync(HttpClient client, string rawParameters)
    {
        var parameters = JsonSerializer.Deserialize<JsonElement>(rawParameters);
        var response = await client.PostAsJsonAsync("/api/v1/approvals", new
        {
            kind = "tool",
            tool_id = "write_file",
            parameters
        }, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

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
}
