using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// High-signal behavioral tests for the upgraded approval control plane:
/// GET /api/v1/approvals/policy, unified run-scope release on kind === "tool",
/// atomic idempotent remember_rule, spec_propose guards, rejection reason propagation,
/// POST /api/v1/approvals/batch-decision, and nonce secret lifecycle cleanup.
/// </summary>
public sealed class ApprovalUpgradeApiTests : IAsyncLifetime
{
    private readonly ApiEndpointFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => _factory.Dispose();

    [Fact]
    public async Task GetApprovalPolicy_ReturnsMediumRiskDefaultAndHardSafetyLists()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/approvals/policy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ToolApprovalPolicy.PolicyVersion, body.GetProperty("policy_version").GetString());
        Assert.Equal("medium", body.GetProperty("risk_max").GetString());
        Assert.Equal("medium", body.GetProperty("delegated_risk_max").GetString());
        Assert.True(body.GetProperty("release_read_only_in_ask_mode").GetBoolean());

        var humanOnly = body.GetProperty("human_only_tools").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("shell", humanOnly);
        Assert.Contains("command_run", humanOnly);
        Assert.Contains("git_push", humanOnly);

        var delegationBlocked = body.GetProperty("delegation_blocked_tools").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("web_fetch", delegationBlocked);
        Assert.Contains("mcp_invoke", delegationBlocked);
    }

    [Fact]
    public async Task DecideApproval_ToolRunScope_MintsPreAuthAndGrant_AndReleasesPendingSiblings()
    {
        var client = _factory.CreateClient();
        var (_, sessionId, runId) = await SeedRunAsync();

        var primary = await SeedToolApprovalAsync(sessionId, runId, "write_file", "{\"path\":\"a.txt\",\"content\":\"1\"}");
        var siblingSameTool = await SeedToolApprovalAsync(sessionId, runId, "write_file", "{\"path\":\"b.txt\",\"content\":\"2\"}");
        var otherTool = await SeedToolApprovalAsync(sessionId, runId, "shell", "{\"command\":\"npm test\"}");

        var response = await client.PostAsJsonAsync($"/api/v1/approvals/{primary.ApprovalId}/decision", new
        {
            decision = "approved",
            scope = "run"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(primary.ApprovalId, body.GetProperty("id").GetGuid());
        Assert.Equal("approved", body.GetProperty("status").GetString());
        Assert.Equal("run", body.GetProperty("scope").GetString());
        Assert.Equal(1, body.GetProperty("run_scope_released").GetInt32());
        Assert.NotEqual(Guid.Empty, body.GetProperty("pre_authorization_id").GetGuid());
        Assert.NotEqual(Guid.Empty, body.GetProperty("capability_grant_id").GetGuid());

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Equal("approved", (await db.ApprovalRequests.SingleAsync(x => x.Id == siblingSameTool.ApprovalId)).Status);
        Assert.Equal("pending", (await db.ApprovalRequests.SingleAsync(x => x.Id == otherTool.ApprovalId)).Status);
        Assert.True(await db.PreAuthorizations.AnyAsync(x => x.RunId == runId));
    }

    [Fact]
    public async Task DecideApproval_SpecPropose_RejectsRunScopeAndRememberRuleWith400()
    {
        var client = _factory.CreateClient();
        var (_, sessionId, runId) = await SeedRunAsync();
        var spec = await SeedToolApprovalAsync(sessionId, runId, CoreVirtualToolPolicy.SpecProposeToolId, "{\"title\":\"Spec\",\"markdown\":\"# Plan\"}");

        var runScopeResp = await client.PostAsJsonAsync($"/api/v1/approvals/{spec.ApprovalId}/decision", new
        {
            decision = "approved",
            scope = "run"
        });
        Assert.Equal(HttpStatusCode.BadRequest, runScopeResp.StatusCode);
        var runScopeErr = await runScopeResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("spec_confirmation_scope", runScopeErr.GetProperty("code").GetString());

        var rememberResp = await client.PostAsJsonAsync($"/api/v1/approvals/{spec.ApprovalId}/decision", new
        {
            decision = "approved",
            remember_rule = new { kind = "command_prefix", pattern = "npm test", scope = "session" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, rememberResp.StatusCode);
        var rememberErr = await rememberResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("spec_confirmation_scope", rememberErr.GetProperty("code").GetString());

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Equal("pending", (await db.ApprovalRequests.SingleAsync(x => x.Id == spec.ApprovalId)).Status);
    }

    [Fact]
    public async Task DecideApproval_RememberRule_ValidatesBeforeMutation_AndReusesExistingRuleIdempotently()
    {
        var client = _factory.CreateClient();
        var (_, sessionId, runId) = await SeedRunAsync();
        var shell1 = await SeedToolApprovalAsync(sessionId, runId, "shell", "{\"command\":\"npm test --watch\"}");
        var shell2 = await SeedToolApprovalAsync(sessionId, runId, "shell", "{\"command\":\"npm test --run\"}");
        var shellFallback = await SeedToolApprovalAsync(sessionId, runId, "shell", "{\"command\":\"bun test src/a.ts\"}");
        var nonShell = await SeedToolApprovalAsync(sessionId, runId, "write_file", "{\"path\":\"a.txt\",\"content\":\"x\"}");

        // Wildcard pattern rejected before mutating state
        var wildcardResp = await client.PostAsJsonAsync($"/api/v1/approvals/{shell1.ApprovalId}/decision", new
        {
            decision = "approved",
            remember_rule = new { kind = "command_prefix", pattern = "npm *", scope = "session" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, wildcardResp.StatusCode);
        Assert.Equal("invalid_rule", (await wildcardResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Non-shell tool rejected before mutating state
        var nonShellResp = await client.PostAsJsonAsync($"/api/v1/approvals/{nonShell.ApprovalId}/decision", new
        {
            decision = "approved",
            remember_rule = new { kind = "command_prefix", pattern = "echo hi", scope = "session" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, nonShellResp.StatusCode);

        await using (var checkDb = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal("pending", (await checkDb.ApprovalRequests.SingleAsync(x => x.Id == shell1.ApprovalId)).Status);
            Assert.Equal("pending", (await checkDb.ApprovalRequests.SingleAsync(x => x.Id == nonShell.ApprovalId)).Status);
        }

        // First approval creates the rule
        var ok1 = await client.PostAsJsonAsync($"/api/v1/approvals/{shell1.ApprovalId}/decision", new
        {
            decision = "approved",
            remember_rule = new { kind = "command_prefix", pattern = "npm test", scope = "session" }
        });
        Assert.Equal(HttpStatusCode.OK, ok1.StatusCode);
        var body1 = await ok1.Content.ReadFromJsonAsync<JsonElement>();
        var ruleId = body1.GetProperty("created_rule_id").GetGuid();
        Assert.NotEqual(Guid.Empty, ruleId);

        // Second approval with identical rule reuses ruleId (no 409 conflict!)
        var ok2 = await client.PostAsJsonAsync($"/api/v1/approvals/{shell2.ApprovalId}/decision", new
        {
            decision = "approved",
            remember_rule = new { kind = "command_prefix", pattern = "npm test", scope = "session" }
        });
        Assert.Equal(HttpStatusCode.OK, ok2.StatusCode);
        var body2 = await ok2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ruleId, body2.GetProperty("created_rule_id").GetGuid());

        // Pattern omitted falls back to first token ("bun") in workspace scope
        var okFallback = await client.PostAsJsonAsync($"/api/v1/approvals/{shellFallback.ApprovalId}/decision", new
        {
            decision = "approved",
            remember_rule = new { kind = "command_prefix", scope = "workspace" }
        });
        Assert.Equal(HttpStatusCode.OK, okFallback.StatusCode);
        var rules = _factory.Services.GetRequiredService<IApprovalRules>();
        Assert.NotNull(await rules.MatchCommandAsync(Guid.NewGuid(), "shell", "bun x vitest run"));
    }

    [Fact]
    public async Task DecideApproval_RejectionReason_PersistsInEventsAndReachesToolExecutionDenial()
    {
        var client = _factory.CreateClient();
        var (_, sessionId, runId) = await SeedRunAsync();
        var seeded = await SeedToolApprovalAsync(sessionId, runId, "shell", "{\"command\":\"npm test\"}");

        var response = await client.PostAsJsonAsync($"/api/v1/approvals/{seeded.ApprovalId}/decision", new
        {
            decision = "rejected",
            reason = "  Use bun x vitest run instead of npm test  "
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal("Use bun x vitest run instead of npm test", body.GetProperty("decision_reason").GetString());

        var coordinator = _factory.Services.GetRequiredService<IToolExecutionCoordinator>();
        var start = await coordinator.TryStartAsync(seeded.ExecutionId);
        Assert.Equal("not_approved", start.Status);
        Assert.Contains("Use bun x vitest run instead of npm test", start.Message ?? string.Empty);
        Assert.Contains("Use bun x vitest run instead of npm test", start.Execution?.AgentGuidance ?? string.Empty);

        await using var eventDb = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        var decidedEvent = await eventDb.EventIndex.AsNoTracking()
            .SingleAsync(e => e.RunId == runId && e.EventType == "approval.decided");
        Assert.Contains("Use bun x vitest run instead of npm test", decidedEvent.Summary);
    }

    [Fact]
    public async Task BatchDecideApprovals_ValidatesBoundsDeduplicatesAndIsolatesItemFailures()
    {
        var client = _factory.CreateClient();
        var (_, sessionId, runId) = await SeedRunAsync();

        var emptyBatch = await client.PostAsJsonAsync("/api/v1/approvals/batch-decision", new
        {
            approval_ids = Array.Empty<Guid>(),
            decision = "approved"
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyBatch.StatusCode);

        var oversizedBatch = await client.PostAsJsonAsync("/api/v1/approvals/batch-decision", new
        {
            approval_ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray(),
            decision = "approved"
        });
        Assert.Equal(HttpStatusCode.BadRequest, oversizedBatch.StatusCode);

        var valid = await SeedToolApprovalAsync(sessionId, runId, "write_file", "{\"path\":\"ok.txt\",\"content\":\"1\"}");
        var spec = await SeedToolApprovalAsync(sessionId, runId, CoreVirtualToolPolicy.SpecProposeToolId, "{\"title\":\"S\",\"markdown\":\"# S\"}");
        var missingId = Guid.NewGuid();

        var batchResp = await client.PostAsJsonAsync("/api/v1/approvals/batch-decision", new
        {
            approval_ids = new[] { valid.ApprovalId, valid.ApprovalId, missingId, spec.ApprovalId },
            decision = "approved",
            scope = "run"
        });
        Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
        var results = (await batchResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(3, results.Length);

        Assert.Equal(valid.ApprovalId, results[0].GetProperty("id").GetGuid());
        Assert.True(results[0].GetProperty("ok").GetBoolean());
        Assert.Equal("approved", results[0].GetProperty("status").GetString());

        Assert.Equal(missingId, results[1].GetProperty("id").GetGuid());
        Assert.False(results[1].GetProperty("ok").GetBoolean());
        Assert.Equal("not_found", results[1].GetProperty("error_code").GetString());

        Assert.Equal(spec.ApprovalId, results[2].GetProperty("id").GetGuid());
        Assert.False(results[2].GetProperty("ok").GetBoolean());
        Assert.Equal("spec_confirmation_scope", results[2].GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task NonceSecretCleanup_DeletesOnConsumeRejectExpire_AndSweepsHistoricalTerminalRows()
    {
        var (_, sessionId, runId) = await SeedRunAsync();
        var nonceStore = _factory.Services.GetRequiredService<INonceMaterialStore>();
        var coordinator = _factory.Services.GetRequiredService<ToolApprovalCoordinator>();
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();

        // 1. Rejected approval deletes nonce secret immediately
        var toReject = await SeedToolApprovalAsync(sessionId, runId, "write_file", "{\"path\":\"r.txt\",\"content\":\"r\"}");
        Assert.NotNull(await nonceStore.GetAsync(toReject.NonceSecretRef));
        await coordinator.DecideAsync(toReject.ApprovalId, "rejected", "no");
        Assert.Null(await nonceStore.GetAsync(toReject.NonceSecretRef));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.Null((await db.ApprovalRequests.SingleAsync(x => x.Id == toReject.ApprovalId)).NonceSecretReference);
        }

        // 2. Approved retains nonce until consumed, then deletes upon consumption
        var toConsume = await SeedToolApprovalAsync(sessionId, runId, "write_file", "{\"path\":\"c.txt\",\"content\":\"c\"}");
        await coordinator.DecideAsync(toConsume.ApprovalId, "approved", null);
        Assert.NotNull(await nonceStore.GetAsync(toConsume.NonceSecretRef));

        var started = await coordinator.TryStartAsync(toConsume.ExecutionId);
        Assert.Equal("running", started.Status);
        Assert.Null(await nonceStore.GetAsync(toConsume.NonceSecretRef));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.Null((await db.ApprovalRequests.SingleAsync(x => x.Id == toConsume.ApprovalId)).NonceSecretReference);
        }

        // 3. Historical terminal row with leftover NonceSecretReference is cleaned by SweepExpiredPendingApprovalsAsync
        var historicalRef = $"approval_nonce_legacy_{Guid.NewGuid():N}";
        await nonceStore.PutAsync(historicalRef, "legacy-secret-value");
        var historicalId = Guid.NewGuid();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.ApprovalRequests.Add(new ApprovalRequestRecord
            {
                Id = historicalId,
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                Kind = "tool",
                ToolId = "shell",
                Risk = "medium",
                RequestHash = "abc123",
                NonceHash = "hash123",
                NonceSecretReference = historicalRef,
                ParametersReference = string.Empty,
                ArgumentsDigest = string.Empty,
                Summary = "legacy consumed approval",
                Status = "consumed",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-2),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
            });
            await db.SaveChangesAsync();
        }

        await coordinator.SweepExpiredPendingApprovalsAsync();
        Assert.Null(await nonceStore.GetAsync(historicalRef));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.Null((await db.ApprovalRequests.SingleAsync(x => x.Id == historicalId)).NonceSecretReference);
        }
    }

    private async Task<(Guid ProjectId, Guid SessionId, Guid RunId)> SeedRunAsync()
    {
        using var client = _factory.CreateClient();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var root = Path.Combine(ApiTestStorage.Home, "approval-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var projectResp = await client.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "approval-upgrade",
            path = root
        });
        Assert.Equal(HttpStatusCode.Created, projectResp.StatusCode);
        var projectElement = await projectResp.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectElement.GetProperty("id").GetGuid();

        var sessionResp = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = projectId.ToString(),
            title = "approval test session"
        });
        Assert.Equal(HttpStatusCode.Created, sessionResp.StatusCode);
        var sessionElement = await sessionResp.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessionElement.GetProperty("id").GetGuid();

        var runId = Guid.NewGuid();
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        db.Runs.Add(new RunRecord
        {
            Id = runId,
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SessionId = sessionId,
            InitiatedByPrincipalId = scope.PrincipalId,
            TriggerMessageId = Guid.NewGuid(),
            Status = "awaiting_approval",
            PermissionMode = "ask",
            RuntimeProfileId = "default",
            ConfigurationHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (projectId, sessionId, runId);
    }

    private async Task<(Guid ExecutionId, Guid ApprovalId, string NonceSecretRef)> SeedToolApprovalAsync(
        Guid sessionId,
        Guid runId,
        string toolId,
        string parametersJson)
    {
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        Guid projectId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var session = await _factory.Services.GetRequiredService<ISessionLocator>().FindAsync(sessionId);
            projectId = session?.ProjectId ?? Guid.Empty;
        }

        var coordinator = _factory.Services.GetRequiredService<IToolExecutionCoordinator>();
        var prep = await coordinator.PrepareAsync(new ToolExecutionPrepareRequest(
            scope.TenantId,
            scope.WorkspaceId,
            projectId,
            sessionId,
            runId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            toolId,
            "medium",
            MutatesWorkspace: true,
            RequiresApproval: true,
            parametersJson,
            TinadecCore.Tools.ToolParametersHash.Compute(parametersJson),
            $"call:{Guid.NewGuid():N}",
            Summary: string.Empty));

        var approvalId = prep.Execution.ApprovalId!.Value;
        await using var readDb = await dbFactory.CreateDbContextAsync();
        var nonceRef = (await readDb.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == approvalId)).NonceSecretReference!;
        return (prep.Execution.Id, approvalId, nonceRef);
    }
}
