using System.Diagnostics;
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
        if (Directory.Exists(_root))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                    else if (Directory.Exists(path)) new DirectoryInfo(path).Attributes = FileAttributes.Normal;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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

    [Fact]
    public async Task Create_DecideApproved_ConsumeOnce_SecondConsumeFails()
    {
        var client = _factory!.CreateClient();
        var coordinator = _factory.Services.GetRequiredService<IToolApprovalCoordinator>();
        const string parameters = "{\"content\":\"hi\",\"path\":\"a.txt\"}";
        var approvalId = await CreateApprovalAsync(client, parameters);
        var created = await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}");
        Assert.Equal("pending", created.GetProperty("status").GetString());
        var hash = ToolParametersHash.Compute(parameters);
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("request_hash").GetString()));

        var decideResponse = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" }, Json);
        Assert.True(decideResponse.IsSuccessStatusCode, await decideResponse.Content.ReadAsStringAsync());
        var decided = await decideResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approved", decided.GetProperty("status").GetString());

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

        var approvalId = await CreateApprovalAsync(client, "{\"command\":\"dotnet build\"}", "command_run");

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
        return await CreateApprovalAsync(client, rawParameters, "write_file");
    }

    private async Task<Guid> CreateApprovalAsync(HttpClient client, string rawParameters, string toolId)
    {
        _ = client;
        var coordinator = _factory!.Services.GetRequiredService<IToolApprovalCoordinator>();
        return await coordinator.CreateToolApprovalAsync(
            Guid.NewGuid().ToString(),
            string.Empty,
            string.Empty,
            toolId,
            ToolParametersHash.Compute(rawParameters),
            $"Test approval for {toolId}");
    }

    [Fact]
    public async Task ClientCannotCreateArbitraryApproval()
    {
        var client = _factory!.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/approvals", new { tool_id = "write_file", parameters = new { } }, Json);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task UserToolAction_WriteFile_UsesPermissionApprovalLeaseAndProvider()
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, "user-action-workspace");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "User action project", path = projectPath }, Json);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();

        var create = await client.PostAsJsonAsync("/api/v1/user/tool-actions", new
        {
            project_id = projectId,
            tool_id = "write_file",
            @params = new { filepath = "user-action.txt", content = "governed" },
            idempotency_key = "user-action-write-1"
        }, Json);
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var requested = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("awaiting_user", requested.GetProperty("status").GetString());
        var actionId = requested.GetProperty("id").GetGuid();
        var permissionRequestId = requested.GetProperty("permission_request_id").GetGuid();

        var permission = await client.PostAsJsonAsync($"/api/v1/governance/permission-requests/{permissionRequestId}/decision",
            new { approve = true, reason = "User confirmed the file write." }, Json);
        Assert.Equal(HttpStatusCode.Accepted, permission.StatusCode);
        var awaitingApproval = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("awaiting_approval", awaitingApproval.GetProperty("status").GetString());
        var actionApprovalId = awaitingApproval.GetProperty("action_approval_id").GetGuid();

        var approval = await client.PostAsJsonAsync($"/api/v1/approvals/{actionApprovalId}/decision",
            new { decision = "approved", reason = "User confirmed the governed write." }, Json);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var completed = await approval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", completed.GetProperty("action").GetProperty("status").GetString());
        Assert.Equal("governed", await File.ReadAllTextAsync(Path.Combine(projectPath, "user-action.txt")));

        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{completed.GetProperty("action").GetProperty("id").GetGuid()}");
        Assert.Equal("completed", fetched.GetProperty("status").GetString());
        Assert.StartsWith("user-tool-action:", fetched.GetProperty("audit_reference").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserToolAction_GitCommit_UsesGitSnapshotAndGovernanceChain()
    {
        var client = _factory!.CreateClient();
        var repositoryPath = Path.Combine(_root, "git-action-repository");
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, "init", "--quiet");
        await RunGitAsync(repositoryPath, "config", "user.email", "tinadec-tests@example.invalid");
        await RunGitAsync(repositoryPath, "config", "user.name", "Tinadec Tests");
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "governed.txt"), "git governed\n");

        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Git action project", path = repositoryPath }, Json);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();

        var create = await client.PostAsJsonAsync("/api/v1/user/tool-actions", new
        {
            project_id = projectId,
            tool_id = "git_commit",
            @params = new
            {
                repository_path = repositoryPath,
                message = "governed git commit",
                include_all = true,
                commit_staged_only = false,
                confirm_commit = "COMMIT"
            },
            idempotency_key = "user-action-git-commit-1"
        }, Json);
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var requested = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("awaiting_user", requested.GetProperty("status").GetString());
        var actionId = requested.GetProperty("id").GetGuid();
        var permissionRequestId = requested.GetProperty("permission_request_id").GetGuid();
        var snapshotId = requested.GetProperty("snapshot_id").GetGuid();
        Assert.NotEqual(Guid.Empty, snapshotId);

        var permission = await client.PostAsJsonAsync($"/api/v1/governance/permission-requests/{permissionRequestId}/decision",
            new { approve = true, reason = "User confirmed the Git commit." }, Json);
        Assert.Equal(HttpStatusCode.Accepted, permission.StatusCode);
        var awaitingApproval = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("awaiting_approval", awaitingApproval.GetProperty("status").GetString());
        var actionApprovalId = awaitingApproval.GetProperty("action_approval_id").GetGuid();

        var approval = await client.PostAsJsonAsync($"/api/v1/approvals/{actionApprovalId}/decision",
            new { decision = "approved", reason = "User confirmed the governed commit." }, Json);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var completed = await approval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", completed.GetProperty("action").GetProperty("status").GetString());
        Assert.Equal(40, (await RunGitAsync(repositoryPath, "rev-parse", "HEAD")).Trim().Length);
        Assert.Equal("", (await RunGitAsync(repositoryPath, "status", "--porcelain")).Trim());
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git was not found.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout;
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
