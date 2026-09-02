using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Pre-authorization mint contract: an unattended grant (or a frozen
/// full-access run) upgrades the existing pending approval without ever
/// weakening the binding checks TryStartAsync enforces. "Only who approves
/// changes — there must still be exactly one binding-checked approval."
/// </summary>
public sealed class PreAuthorizationMintTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Parameters = "{\"path\":\"preauth.txt\",\"content\":\"x\"}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-preauth-tests", Guid.NewGuid().ToString("N"));
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
    public async Task PreAuthorization_GrantMatch_MintsApprovedApprovalAndStarts()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-happy");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "happy:0:0:0");

        var minted = await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id);

        Assert.NotNull(minted);
        Assert.Equal("pre_authorized", minted!.Source);
        Assert.Equal(execution.ApprovalId, minted.Snapshot.ApprovalId);

        await using var db = await DbFactory().CreateDbContextAsync();
        var grant = await db.PreAuthorizations.AsNoTracking().SingleAsync(x => x.RunId == run.Id);
        Assert.Equal(1, grant.UseCount);
        var approval = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == execution.ApprovalId);
        Assert.Equal("approved", approval.Status);
        Assert.Equal("approved", approval.Decision);
        Assert.Equal("pre_authorized", approval.DecisionReason);

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id);
        Assert.Equal("running", start.Status);
    }

    [Theory]
    [InlineData("tool_id")]
    [InlineData("task_id")]
    [InlineData("parameters_hash")]
    [InlineData("nonce")]
    public async Task PreAuthorization_AutoMint_KeepsBindingChecks(string tampered)
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync($"preauth-bind-{tampered}");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, $"bind-{tampered}:0:0:0");
        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var row = await db.ToolExecutions.SingleAsync(x => x.Id == execution.Id);
        if (tampered == "tool_id") row.ToolId = "delete_file";
        else if (tampered == "task_id") row.TaskId = Guid.NewGuid();
        else if (tampered == "parameters_hash") row.ParametersHash = ToolParametersHash.Compute("{\"path\":\"evil.txt\"}");
        else
        {
            var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == execution.ApprovalId);
            approval.NonceHash = string.Empty;
        }
        await db.SaveChangesAsync();

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id);
        Assert.Equal("not_approved", start.Status);
    }

    [Fact]
    public async Task PreAuthorization_GrantBudget_ExhaustsAndThenRefuses()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-budget");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.MaxUses = 2);
        var first = await PrepareExecutionAsync(projectId, sessionId, run.Id, "budget:0:0:0");
        var second = await PrepareExecutionAsync(projectId, sessionId, run.Id, "budget:0:0:1");
        var third = await PrepareExecutionAsync(projectId, sessionId, run.Id, "budget:0:0:2");

        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(first.Id));
        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(second.Id));
        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(third.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var grant = await db.PreAuthorizations.AsNoTracking().SingleAsync(x => x.RunId == run.Id);
        Assert.Equal(2, grant.UseCount);
        var pendingApproval = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == third.ApprovalId);
        Assert.Equal("pending", pendingApproval.Status);
    }

    [Theory]
    [InlineData("low", "high")]
    [InlineData("medium", "critical")]
    public async Task PreAuthorization_RiskAboveCeiling_NeverMints(string grantRiskMax, string executionRisk)
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync($"preauth-risk-{grantRiskMax}-{executionRisk}");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.RiskMax = grantRiskMax);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, $"risk-{executionRisk}:0:0:0", risk: executionRisk);

        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var approval = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == execution.ApprovalId);
        Assert.Equal("pending", approval.Status);
    }

    [Theory]
    [InlineData("[\"*\"]")]
    [InlineData("[]")]
    public async Task PreAuthorization_UnsafeScopes_NeverMint(string scopeJson)
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-unsafe-scope");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.ToolScopeJson = scopeJson);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "unsafe:0:0:0");

        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));
    }

    [Fact]
    public async Task PreAuthorization_ToolOutsideScope_NeverMints()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-outside-scope");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.ToolScopeJson = "[\"git_commit\"]");
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "outside:0:0:0");

        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));
    }

    [Fact]
    public async Task PreAuthorization_LaneBoundGrant_NeverMints()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-lane-bound");
        var run = await InsertRunAsync(sessionId);
        // Lane-bound grants stay fail-closed until executions carry a lane key:
        // spending one from a lane-less mint would widen it to every lane.
        await InsertGrantAsync(run.Id, grant => grant.LaneKey = "lane-a");
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "lane:0:0:0");

        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));
    }

    [Fact]
    public async Task PreAuthorization_LaneBoundGrant_MintsOnlyItsOwnLane()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-lane-scoped");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.LaneKey = "lane-a");

        var inLane = await PrepareExecutionAsync(projectId, sessionId, run.Id, "lane-match:0:0:0", laneKey: "lane-a");
        var otherLane = await PrepareExecutionAsync(projectId, sessionId, run.Id, "lane-other:0:0:0", laneKey: "lane-b");

        var minted = await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(inLane.Id);
        Assert.NotNull(minted);
        Assert.Equal("pre_authorized", minted!.Source);
        // The grant was scoped to one lane, so a sibling lane cannot spend it.
        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(otherLane.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var other = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == otherLane.ApprovalId);
        Assert.Equal("pending", other.Status);
    }

    [Fact]
    public async Task PreAuthorization_RunLevelGrant_ServesEveryLane()
    {
        // The user grants before leaving, when only the run id is known. A
        // run-level grant must therefore reach the deferred lane too, otherwise
        // the unattended "test and commit" lane could never be authorized.
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-run-level");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.MaxUses = 2);

        var mainLane = await PrepareExecutionAsync(projectId, sessionId, run.Id, "run-main:0:0:0", laneKey: "main");
        var deferredLane = await PrepareExecutionAsync(projectId, sessionId, run.Id, "run-deferred:0:0:0", laneKey: "l2");

        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(mainLane.Id));
        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(deferredLane.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var rows = await db.ToolExecutions.AsNoTracking().Where(x => x.RunId == run.Id).ToListAsync();
        Assert.Equal("main", Assert.Single(rows, x => x.ToolCallKey == "run-main:0:0:0").LaneKey);
        Assert.Equal("l2", Assert.Single(rows, x => x.ToolCallKey == "run-deferred:0:0:0").LaneKey);
    }

    [Fact]
    public async Task PreAuthorization_LaneScopedGrant_IsPreferredOverRunLevel()
    {
        // A scoped grant must not be shadowed by a broader one, or the budget of
        // the broader grant would be spent where a narrower grant was intended.
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-lane-preferred");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id);
        await InsertGrantAsync(run.Id, grant => grant.LaneKey = "lane-a");
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "prefer:0:0:0", laneKey: "lane-a");

        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var grants = await db.PreAuthorizations.AsNoTracking().Where(x => x.RunId == run.Id).ToListAsync();
        Assert.Equal(1, Assert.Single(grants, x => x.LaneKey == "lane-a").UseCount);
        Assert.Equal(0, Assert.Single(grants, x => x.LaneKey == null).UseCount);
    }

    [Fact]
    public async Task AutoPolicy_ReleasesToolApproval_WithoutHumanDecision()
    {
        UseFactory(new Dictionary<string, string?>
        {
            ["TinadecApproval:AutoApproveEnabled"] = "true",
            // write_file registers as high risk, so the default medium ceiling
            // would make the policy abstain — exactly the safe default this
            // test deliberately overrides.
            ["TinadecApproval:AutoApproveRiskMax"] = "high"
        });
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("autopol-mint");
        var run = await InsertRunAsync(sessionId);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "autopol:0:0:0", laneKey: "l2");

        var minted = await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id);

        Assert.NotNull(minted);
        Assert.Equal("auto_policy", minted!.Source);

        await using var db = await DbFactory().CreateDbContextAsync();
        var approval = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == execution.ApprovalId);
        Assert.Equal("approved", approval.Status);
        Assert.Equal("auto_policy_approved", approval.DecisionReason);

        var decision = await db.ApprovalDecisions.AsNoTracking().SingleAsync(x => x.ApprovalRequestId == execution.ApprovalId);
        Assert.Equal(Guid.Empty, decision.DecidedByPrincipalId);

        var manager = _factory!.Services.GetRequiredService<ILifecycleManager>();
        var events = await manager.ReplayEventsAsync(sessionId, 0);
        var auto = Assert.Single(events, e => e.EventType == "approval.auto_decided");
        var payload = Assert.IsType<JsonElement>(auto.Payload["payload"]!);
        Assert.Equal(execution.Id, payload.GetProperty("execution_id").GetGuid());
        Assert.Equal("l2", payload.GetProperty("lane_key").GetString());
        Assert.Equal("auto-approve-v1", payload.GetProperty("policy_version").GetString());
    }

    [Fact]
    public async Task AutoPolicy_BudgetExhausted_KeepsApprovalPendingInsteadOfDenying()
    {
        UseFactory(new Dictionary<string, string?>
        {
            ["TinadecApproval:AutoApproveEnabled"] = "true",
            ["TinadecApproval:AutoApproveRiskMax"] = "high",
            ["TinadecApproval:AutoApproveMaxPerRun"] = "1"
        });
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("autopol-budget");
        var run = await InsertRunAsync(sessionId);
        var first = await PrepareExecutionAsync(projectId, sessionId, run.Id, "autopol-b1:0:0:0");
        var second = await PrepareExecutionAsync(projectId, sessionId, run.Id, "autopol-b2:0:0:0");

        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(first.Id));
        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(second.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var pending = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == second.ApprovalId);
        // Exhausted budget escalates rather than denies: the request stays
        // pending so a human can still grant it when they return.
        Assert.Equal("pending", pending.Status);
    }

    [Fact]
    public async Task AutoPolicy_DisabledOrOutOfScope_LeavesApprovalPending()
    {
        UseFactory(new Dictionary<string, string?>
        {
            ["TinadecApproval:AutoApproveEnabled"] = "false"
        });
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("autopol-off");
        var run = await InsertRunAsync(sessionId);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "autopol-off:0:0:0");

        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));

        await using var db = await DbFactory().CreateDbContextAsync();
        var pending = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == execution.ApprovalId);
        Assert.Equal("pending", pending.Status);
    }

    [Fact]
    public async Task PreAuthorization_ExpiredGrant_NeverMints()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-expired");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "expired:0:0:0");

        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));
    }

    [Fact]
    public async Task PreAuthorization_ParameterConstraintHash_MustMatch()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-constraint");
        var run = await InsertRunAsync(sessionId);
        await InsertGrantAsync(run.Id, grant => grant.ParameterConstraintHash = ToolParametersHash.Compute("{\"path\":\"other.txt\"}"));
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "constraint:0:0:0");

        Assert.Null(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));

        await InsertGrantAsync(run.Id, grant => grant.ParameterConstraintHash = ToolParametersHash.Compute(Parameters));
        Assert.NotNull(await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id));
    }

    [Fact]
    public async Task FullAccess_FrozenMode_MintsWithoutGrant_AndStarts()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("preauth-fullaccess");
        var run = await InsertRunAsync(sessionId);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "full:0:0:0");

        var content = _factory!.Services.GetRequiredService<IContentStore>();
        var scope = TenantAccessor().Current;
        var bytes = Encoding.UTF8.GetBytes("{\"permissionMode\":\"full-access\"}");
        var stored = await content.PutAsync(new ContentWriteRequest(
            scope.TenantId, scope.WorkspaceId, "run_configuration", "application/json", new MemoryStream(bytes)));
        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            var row = await db.Runs.SingleAsync(x => x.Id == run.Id);
            row.FrozenConfigurationReference = stored.Value;
            row.FrozenConfigurationHash = stored.Sha256;
            row.FrozenConfigurationLength = stored.Length;
            row.FrozenConfigurationSchemaVersion = "preauth-tests";
            row.FrozenConfigurationAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var minted = await ExecutionCoordinator().TryMintPreAuthorizedApprovalAsync(execution.Id);

        Assert.NotNull(minted);
        Assert.Equal("full_access_auto_mint", minted!.Source);

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id);
        Assert.Equal("running", start.Status);
    }

    [Fact]
    public async Task PreAuthorizationEndpoint_CreatesValidatesAndRefusesWildcard()
    {
        var client = _factory!.CreateClient();
        var (_, sessionId) = await CreateProjectAndSessionAsync("preauth-endpoint");
        var run = await InsertRunAsync(sessionId);

        var created = await client.PostAsJsonAsync("/api/v1/approvals/pre-authorizations", new
        {
            run_id = run.Id,
            tool_scope = new[] { "write_file", "read_file" },
            risk_max = "medium",
            max_uses = 3,
            summary = "endpoint smoke"
        }, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal(run.Id, body.GetProperty("run_id").GetGuid());
        Assert.Equal("medium", body.GetProperty("risk_max").GetString());
        Assert.Equal(3, body.GetProperty("max_uses").GetInt32());
        Assert.Equal(0, body.GetProperty("use_count").GetInt32());

        var wildcard = await client.PostAsJsonAsync("/api/v1/approvals/pre-authorizations",
            new { run_id = run.Id, tool_scope = new[] { "*" } }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, wildcard.StatusCode);

        var unknownRun = await client.PostAsJsonAsync("/api/v1/approvals/pre-authorizations",
            new { run_id = Guid.NewGuid(), tool_scope = new[] { "write_file" } }, Json);
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);
    }

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

    private async Task<RunRecord> InsertRunAsync(Guid sessionId)
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
            Status = "planning",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task<PreAuthorizationRecord> InsertGrantAsync(Guid runId, Action<PreAuthorizationRecord>? customize = null)
    {
        var scope = TenantAccessor().Current;
        await using var db = await DbFactory().CreateDbContextAsync();
        var grant = new PreAuthorizationRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            RunId = runId,
            ToolScopeJson = "[\"write_file\"]",
            RiskMax = "high",
            MaxUses = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            GrantedByPrincipalId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        customize?.Invoke(grant);
        db.PreAuthorizations.Add(grant);
        await db.SaveChangesAsync();
        return grant;
    }

    private async Task<ToolExecutionSnapshot> PrepareExecutionAsync(
        Guid projectId,
        Guid sessionId,
        Guid runId,
        string toolCallKey,
        string toolId = "write_file",
        string risk = "high",
        string? laneKey = null)
    {
        var coordinator = ExecutionCoordinator();
        var scope = TenantAccessor().Current;
        var request = new ToolExecutionPrepareRequest(
            scope.TenantId,
            scope.WorkspaceId,
            projectId,
            sessionId,
            runId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            toolId,
            risk,
            MutatesWorkspace: true,
            RequiresApproval: true,
            Parameters,
            ToolParametersHash.Compute(Parameters),
            toolCallKey,
            "pre-authorization test",
            LaneKey: laneKey);
        var preparation = await coordinator.PrepareAsync(request);
        return preparation.Execution;
    }

    private IDbContextFactory<LifecycleDbContext> DbFactory() =>
        _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();

    /// <summary>
    /// Rebuilds the host with extra in-memory configuration. Each test runs on a
    /// fresh instance with a fresh temp root, so at most one host is alive at a
    /// time and the schema bootstrap can safely reconcile the same database.
    /// </summary>
    private WebApplicationFactory<Program> UseFactory(IReadOnlyDictionary<string, string?>? extra)
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _factory = new Factory(_root, extra);
        return _factory;
    }

    private IToolExecutionCoordinator ExecutionCoordinator() =>
        _factory!.Services.GetRequiredService<IToolExecutionCoordinator>();

    private ITenantContextAccessor TenantAccessor() =>
        _factory!.Services.GetRequiredService<ITenantContextAccessor>();

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly IReadOnlyDictionary<string, string?>? _extra;

        public Factory(string root, IReadOnlyDictionary<string, string?>? extra = null)
        {
            _root = root;
            _extra = extra;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            };
            if (_extra is not null)
            {
                foreach (var pair in _extra) values[pair.Key] = pair.Value;
            }
            config.AddInMemoryCollection(values);
        });
    }
}
