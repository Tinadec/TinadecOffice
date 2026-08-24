using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Governance;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class UserToolActionDurabilityTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-user-action-durability", Guid.NewGuid().ToString("N"));
    private Factory? _factory;

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
    public async Task Resume_FailsClosed_WhenProviderManifestChanges()
    {
        var client = _factory!.CreateClient();
        var (actionId, permissionId) = await CreateActionAsync(client, "manifest-change");
        _factory.Provider.ChangeDescription("Changed after admission");

        var response = await client.PostAsJsonAsync($"/api/v1/governance/permission-requests/{permissionId}/decision",
            new { approve = true, reason = "approve" }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var action = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("blocked", action.GetProperty("status").GetString());
        Assert.Equal("tool_manifest_changed", action.GetProperty("error_category").GetString());
        Assert.Equal(0, _factory.Provider.CallCount);
    }

    [Fact]
    public async Task Recovery_ResumesDecidedActionExactlyOnce()
    {
        var client = _factory!.CreateClient();
        _factory.Provider.RequiresApproval = true;
        var (actionId, permissionId) = await CreateActionAsync(client, "recover-decided");
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        await authorization.DecidePermissionAsync(new PermissionDecisionCommand(permissionId, true, null, null, "approve"));

        var recovery = _factory.Services.GetRequiredService<IUserToolActionRecovery>();
        await recovery.RecoverAsync();
        var waiting = await _factory.Services.GetRequiredService<IUserToolActionService>().GetAsync(actionId);
        Assert.Equal(UserToolActionStatuses.AwaitingApproval, waiting!.Status);
        await _factory.Services.GetRequiredService<IToolApprovalCoordinator>()
            .DecideAsync(waiting.ActionApprovalId!.Value, "approved", "approve");
        await recovery.RecoverAsync();
        await recovery.RecoverAsync();

        var action = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("completed", action.GetProperty("status").GetString());
        Assert.Equal(1, _factory.Provider.CallCount);
    }

    [Fact]
    public async Task Resume_FailsClosed_WhenApprovalBindingChanges()
    {
        var client = _factory!.CreateClient();
        _factory.Provider.RequiresApproval = true;
        var (actionId, permissionId) = await CreateActionAsync(client, "approval-change");
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        await authorization.DecidePermissionAsync(new PermissionDecisionCommand(permissionId, true, null, null, "approve"));
        var recovery = _factory.Services.GetRequiredService<IUserToolActionRecovery>();
        await recovery.RecoverAsync();
        var waiting = await _factory.Services.GetRequiredService<IUserToolActionService>().GetAsync(actionId);
        await _factory.Services.GetRequiredService<IToolApprovalCoordinator>()
            .DecideAsync(waiting!.ActionApprovalId!.Value, "approved", "approve");
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            await db.ApprovalRequests.Where(x => x.Id == waiting.ActionApprovalId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Risk, "critical"));
        }

        await recovery.RecoverAsync();

        var action = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("blocked", action.GetProperty("status").GetString());
        Assert.Equal("approval_nonce_invalid", action.GetProperty("error_category").GetString());
        Assert.Equal(0, _factory.Provider.CallCount);
    }

    [Fact]
    public async Task ConcurrentResume_CallsProviderExactlyOnce()
    {
        var client = _factory!.CreateClient();
        _factory.Provider.RequiresApproval = true;
        _factory.Provider.CallDelay = TimeSpan.FromMilliseconds(200);
        var (actionId, permissionId) = await CreateActionAsync(client, "concurrent-resume");
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        await authorization.DecidePermissionAsync(new PermissionDecisionCommand(permissionId, true, null, null, "approve"));
        var recovery = _factory.Services.GetRequiredService<IUserToolActionRecovery>();
        await recovery.RecoverAsync();
        var actions = _factory.Services.GetRequiredService<IUserToolActionService>();
        var waiting = await actions.GetAsync(actionId);
        await _factory.Services.GetRequiredService<IToolApprovalCoordinator>()
            .DecideAsync(waiting!.ActionApprovalId!.Value, "approved", "approve");

        await Task.WhenAll(actions.ResumeAsync(actionId), actions.ResumeAsync(actionId));

        var completed = await actions.GetAsync(actionId);
        Assert.Equal(UserToolActionStatuses.Completed, completed!.Status);
        Assert.Equal(1, _factory.Provider.CallCount);
    }

    [Fact]
    public async Task Create_RejectsParametersOutsideFrozenSchema()
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, "invalid-schema");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "invalid", path = projectPath }, Json);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.PostAsJsonAsync("/api/v1/user/tool-actions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            tool_id = "governed_read",
            @params = new { query = 42 },
            idempotency_key = "invalid-schema"
        }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _factory.Provider.CallCount);
    }

    [Fact]
    public async Task Create_RejectsMissingManifestConfirmationBeforeGovernance()
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, "missing-confirmation");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "missing-confirmation", path = projectPath }, Json);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.PostAsJsonAsync("/api/v1/user/tool-actions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            tool_id = "confirmed_mutation",
            @params = new { query = "value" },
            idempotency_key = "missing-confirmation"
        }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _factory.Provider.CallCount);
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.UserToolActions.Where(x => x.ToolId == "confirmed_mutation").ToArrayAsync());
    }

    [Fact]
    public async Task GitPush_IsDurablyMarkedNonReversibleWithCompensationGuidance()
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, "non-reversible-push");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "push", path = projectPath }, Json);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.PostAsJsonAsync("/api/v1/user/tool-actions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            tool_id = "git_push",
            @params = new { query = "value", confirm_push = "PUSH" },
            idempotency_key = "non-reversible-push"
        }, Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(action.GetProperty("non_reversible").GetBoolean());
        Assert.Contains("remote push", action.GetProperty("compensation_guidance").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"nonce\"", action.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_reference", action.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _factory.Provider.CallCount);
    }

    [Fact]
    public async Task Resume_RevokesLeaseWhenProtectedNonceMaterialIsUnavailable()
    {
        var client = _factory!.CreateClient();
        var (actionId, permissionId) = await CreateActionAsync(client, "missing-lease-material");
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        var resolution = await authorization.DecidePermissionAsync(new PermissionDecisionCommand(permissionId, true, null, null, "approve"));
        var lease = Assert.IsType<CapabilityLeaseSnapshot>(resolution.Lease);
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<GovernanceDbContext>>().CreateDbContextAsync())
        {
            var secretReference = await db.CapabilityLeases.Where(x => x.Id == lease.Id).Select(x => x.NonceSecretReference).SingleAsync();
            await _factory.Services.GetRequiredService<INonceMaterialStore>().DeleteAsync(secretReference);
        }

        var action = await _factory.Services.GetRequiredService<IUserToolActionService>().ResumeAsync(actionId);

        Assert.Equal(UserToolActionStatuses.Blocked, action.Status);
        Assert.Equal("lease_nonce_unavailable", action.ErrorCategory);
        Assert.Equal(0, _factory.Provider.CallCount);
        await using var verifyDb = await _factory.Services.GetRequiredService<IDbContextFactory<GovernanceDbContext>>().CreateDbContextAsync();
        var revoked = await verifyDb.CapabilityLeases.SingleAsync(x => x.Id == lease.Id);
        Assert.Equal("revoked", revoked.Status);
        Assert.Equal("nonce_material_unavailable", revoked.RevokeReason);
    }

    [Fact]
    public async Task ProtectedApprovalAndLeaseMaterial_SurviveHostRestart()
    {
        _factory!.Provider.RequiresApproval = true;
        var firstClient = _factory.CreateClient();
        var (actionId, permissionId) = await CreateActionAsync(firstClient, "restart-material");
        await _factory.Services.GetRequiredService<IAuthorizationService>()
            .DecidePermissionAsync(new PermissionDecisionCommand(permissionId, true, null, null, "approve"));
        firstClient.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        _factory = new Factory(_root);
        _factory.Provider.RequiresApproval = true;
        _ = _factory.CreateClient();
        var recovery = _factory.Services.GetRequiredService<IUserToolActionRecovery>();
        await recovery.RecoverAsync();
        var waiting = await _factory.Services.GetRequiredService<IUserToolActionService>().GetAsync(actionId);
        Assert.Equal(UserToolActionStatuses.AwaitingApproval, waiting!.Status);
        await _factory.Services.GetRequiredService<IToolApprovalCoordinator>()
            .DecideAsync(waiting.ActionApprovalId!.Value, "approved", "approve");
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        _factory = new Factory(_root);
        _factory.Provider.RequiresApproval = true;
        var finalClient = _factory.CreateClient();
        await _factory.Services.GetRequiredService<IUserToolActionRecovery>().RecoverAsync();

        var action = await finalClient.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("completed", action.GetProperty("status").GetString());
        Assert.Equal(1, _factory.Provider.CallCount);
    }

    [Fact]
    public async Task Recovery_NeverReplaysRunningOrOutcomeUnknownAction()
    {
        var client = _factory!.CreateClient();
        var (actionId, _) = await CreateActionAsync(client, "recover-running");
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            await db.UserToolActions.Where(x => x.Id == actionId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, UserToolActionStatuses.Running));
        }

        var recovery = _factory.Services.GetRequiredService<IUserToolActionRecovery>();
        await recovery.RecoverAsync();
        await recovery.RecoverAsync();

        var action = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("outcome_unknown", action.GetProperty("status").GetString());
        Assert.Equal(0, _factory.Provider.CallCount);
    }

    [Theory]
    [InlineData("mark_completed", "completed", null)]
    [InlineData("mark_failed", "failed", "recovery_marked_failed")]
    public async Task RecoveryDecision_ClosesUnknownOutcomeWithoutProviderReplay(
        string decision,
        string expectedStatus,
        string? expectedError)
    {
        var client = _factory!.CreateClient();
        var (actionId, _) = await CreateActionAsync(client, $"recover-{decision}");
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            await db.UserToolActions.Where(x => x.Id == actionId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, UserToolActionStatuses.OutcomeUnknown)
                    .SetProperty(x => x.ErrorCategory, "outcome_unknown"));
        }

        var response = await client.PostAsJsonAsync($"/api/v1/user/tool-actions/{actionId}/recovery-decision",
            new { decision, reason = "User inspected the workspace and external system." }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedStatus, action.GetProperty("status").GetString());
        Assert.Equal(decision, action.GetProperty("recovery_decision").GetString());
        Assert.Equal(expectedError, action.TryGetProperty("error_category", out var error) && error.ValueKind != JsonValueKind.Null
            ? error.GetString()
            : null);
        Assert.NotEqual(JsonValueKind.Null, action.GetProperty("recovered_at").ValueKind);
        Assert.Equal(0, _factory.Provider.CallCount);

        var replay = await client.PostAsJsonAsync($"/api/v1/user/tool-actions/{actionId}/recovery-decision",
            new { decision, reason = "Idempotent duplicate." }, Json);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(0, _factory.Provider.CallCount);
    }

    private async Task<(Guid ActionId, Guid PermissionId)> CreateActionAsync(HttpClient client, string key)
    {
        var projectPath = Path.Combine(_root, key);
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = key, path = projectPath }, Json);
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var response = await client.PostAsJsonAsync("/api/v1/user/tool-actions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            tool_id = "governed_read",
            @params = new { query = "value" },
            idempotency_key = key
        }, Json);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("awaiting_user", action.GetProperty("status").GetString());
        return (action.GetProperty("id").GetGuid(), action.GetProperty("permission_request_id").GetGuid());
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public MutableProvider Provider { get; } = new();

        public Factory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(Provider);
            });
        }
    }

    private sealed class MutableProvider : IToolProvider
    {
        private string _description = "Governed read";
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool RequiresApproval { get; set; }
        public TimeSpan CallDelay { get; set; }

        public void ChangeDescription(string value) => _description = value;

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public async Task<ToolWireResponseDto> CallAsync(string workspaceRoot, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (CallDelay > TimeSpan.Zero) await Task.Delay(CallDelay, cancellationToken);
            using var result = JsonDocument.Parse("{\"value\":\"ok\"}");
            return new ToolWireResponseDto { IsSuccess = true, Result = result.RootElement.Clone() };
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private ToolManifestDto CreateManifest()
        {
            var tools = new ToolManifestEntryDto[]
            {
                new()
                {
                    Id = "governed_read",
                    Description = _description,
                    Risk = "low",
                    MutatesWorkspace = false,
                    RequiresApproval = RequiresApproval,
                    RetrySafety = "safe",
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":false}").RootElement.Clone()
                },
                new()
                {
                    Id = "confirmed_mutation",
                    Description = "Confirmed mutation",
                    Risk = "high",
                    MutatesWorkspace = true,
                    RequiresApproval = true,
                    RetrySafety = "unsafe",
                    ConfirmationFields = ["confirm_mutation"],
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"confirm_mutation\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":false}").RootElement.Clone()
                },
                new()
                {
                    Id = "git_push",
                    Description = "Remote push",
                    Risk = "high",
                    MutatesWorkspace = true,
                    RequiresApproval = true,
                    RetrySafety = "unsafe",
                    ConfirmationFields = ["confirm_push"],
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"confirm_push\":{\"type\":\"string\"}},\"required\":[\"query\",\"confirm_push\"],\"additionalProperties\":false}").RootElement.Clone()
                }
            };
            return new ToolManifestDto { ProtocolVersion = 2, ManifestHash = ToolManifestHasher.Compute(tools), Tools = tools };
        }
    }
}
