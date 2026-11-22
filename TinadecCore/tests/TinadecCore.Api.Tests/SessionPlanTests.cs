using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

public sealed class SessionPlanTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("session-plan");
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
    public async Task SessionPlanEndpoints_CrudPatchApproveAndCasValidation()
    {
        using var client = _factory!.CreateClient();
        var sessionRes = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Plan CRUD Session" });
        sessionRes.EnsureSuccessStatusCode();
        var sessionJson = await sessionRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessionJson.GetProperty("id").GetGuid();

        // Initial GET returns empty revision 0 draft plan
        var emptyResp = await client.GetAsync($"/api/v1/sessions/{sessionId}/plan");
        Assert.Equal(HttpStatusCode.OK, emptyResp.StatusCode);
        var emptyPlan = await emptyResp.Content.ReadFromJsonAsync<SessionPlanDto>();
        Assert.NotNull(emptyPlan);
        Assert.Equal(0, emptyPlan!.Revision);
        Assert.Equal(SessionPlanStatuses.Draft, emptyPlan.Status);
        Assert.Empty(emptyPlan.Phases);

        // PUT creates structured phases/steps with DAG dependencies
        var putResp = await client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/plan", new
        {
            title = "Wave 3 Rollout",
            goal = "Build structured session plans",
            phases = new[]
            {
                new
                {
                    id = "phase-1",
                    title = "Backend",
                    order = 1,
                    steps = new object[]
                    {
                        new { id = "step-1", title = "Create schema", status = "pending", depends_on = Array.Empty<string>(), bound_task_key = "schema", assigned_agent_slug = "builder" },
                        new { id = "step-2", title = "Wire endpoints", status = "pending", depends_on = new[] { "step-1" }, bound_task_key = "endpoints", assigned_agent_slug = "builder" }
                    }
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        Assert.Equal("\"1\"", putResp.Headers.ETag?.Tag);
        var created = await putResp.Content.ReadFromJsonAsync<SessionPlanDto>();
        Assert.NotNull(created);
        Assert.Equal(1, created!.Revision);
        Assert.Equal(2, created.Progress.TotalSteps);
        Assert.Equal(2, created.Progress.PendingSteps);

        // Stale If-Match triggers 412 Precondition Failed
        using var stalePatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/sessions/{sessionId}/plan")
        {
            Content = JsonContent.Create(new { step_updates = new[] { new { id = "step-1", status = "completed" } } })
        };
        stalePatch.Headers.IfMatch.Add(new EntityTagHeaderValue("\"0\""));
        var staleResp = await client.SendAsync(stalePatch);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResp.StatusCode);

        // Valid PATCH advances step-1 to completed and computes progress
        using var validPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/sessions/{sessionId}/plan")
        {
            Content = JsonContent.Create(new
            {
                step_updates = new[] { new { id = "step-1", status = "completed", summary = "Schema migrated" } },
                add_steps = new[] { new { phase_id = "phase-1", id = "step-3", title = "Run verification", depends_on = new[] { "step-2" } } }
            })
        };
        validPatch.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        var patchResp = await client.SendAsync(validPatch);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = await patchResp.Content.ReadFromJsonAsync<SessionPlanDto>();
        Assert.NotNull(patched);
        Assert.Equal(2, patched!.Revision);
        Assert.Equal(3, patched.Progress.TotalSteps);
        Assert.Equal(1, patched.Progress.CompletedSteps);
        Assert.Equal(SessionPlanStatuses.Active, patched.Status);

        // Cyclic depends_on returns 400 Bad Request
        var cycleResp = await client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/plan", new
        {
            title = "Cyclic Plan",
            phases = new[]
            {
                new
                {
                    id = "p1",
                    title = "P1",
                    steps = new[]
                    {
                        new { id = "a", title = "A", depends_on = new[] { "b" } },
                        new { id = "b", title = "B", depends_on = new[] { "a" } }
                    }
                }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, cycleResp.StatusCode);

        // POST /approve stamps ApprovedAt
        using var approveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/plan/approve");
        approveReq.Headers.IfMatch.Add(new EntityTagHeaderValue("\"2\""));
        var approveResp = await client.SendAsync(approveReq);
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        var approved = await approveResp.Content.ReadFromJsonAsync<SessionPlanDto>();
        Assert.NotNull(approved);
        Assert.Equal(3, approved!.Revision);
        Assert.NotNull(approved.ApprovedAt);
    }

    [Fact]
    public async Task SessionPlanService_EngineSyncLifecycleEventsAndPlanReadTool()
    {
        using var client = _factory!.CreateClient();
        var sessionRes = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Engine Sync Session" });
        sessionRes.EnsureSuccessStatusCode();
        var sessionId = (await sessionRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var plans = _factory.Services.GetRequiredService<SessionPlanService>();
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var runIdStr = await lifecycle.StartRunAsync(sessionId.ToString());
        var runId = Guid.Parse(runIdStr);

        // Single-turn un-planned task does NOT create synthetic plan
        var greetingNode = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "greeting-solo",
            Title = "Answer greeting",
            Status = "completed"
        };
        var skipped = await plans.SyncTaskTransitionAsync(sessionId, runId, greetingNode);
        Assert.Null(skipped);

        // Planned tasks sync into session plan and emit session.plan.updated
        var taskA = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "build-api",
            Title = "Implement API",
            Status = "pending",
            WorkerAgentSlug = "backend",
            SuccessCriteria = ["GET returns 200"]
        };
        var taskB = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "verify-api",
            Title = "Verify API",
            Status = "pending",
            Dependencies = ["build-api"],
            WorkerAgentSlug = "auditor"
        };

        var synced = await plans.SyncFromPlannedTasksAsync(sessionId, runId, [taskA, taskB], "Deliver feature");
        Assert.NotNull(synced);
        Assert.Equal(2, synced!.Progress.TotalSteps);
        Assert.Contains("step-build-api", synced.Phases[0].Steps[1].DependsOn);

        // Existing plan instructions include bound task key
        var promptText = PlanningAgent.BuildExistingPlanInstructions(synced);
        Assert.Contains("task_key=build-api", promptText);

        // Task transition records worktree + evidence artifacts
        taskA.Status = "completed";
        taskA.ResultSummary = "Endpoints implemented";
        var transitioned = await plans.SyncTaskTransitionAsync(
            sessionId,
            runId,
            taskA,
            summary: "Endpoints implemented",
            evidence: ["TinadecCore/AspNetCore/Endpoints/SessionPlanEndpoints.cs"],
            worktreePath: @"C:\git\agent\wt-plan",
            worktreeBranch: "feat/plan");
        Assert.NotNull(transitioned);
        var stepA = transitioned!.Phases[0].Steps.Single(s => s.BoundTaskKey == "build-api");
        Assert.Equal(SessionPlanStatuses.StepCompleted, stepA.Status);
        Assert.Contains(stepA.Artifacts, a => a.Kind == "worktree" && a.Label == "feat/plan");
        Assert.Contains(stepA.Artifacts, a => a.Path == "TinadecCore/AspNetCore/Endpoints/SessionPlanEndpoints.cs");

        // plan_update sub-steps sync via SyncFromTaskPlanToolAsync
        var afterTool = await plans.SyncFromTaskPlanToolAsync(
            sessionId,
            runId,
            taskB,
            [new TaskPlanStep("Run test suite", "in_progress"), new TaskPlanStep("Post audit", "pending")],
            "Executing verification");
        Assert.Equal(SessionPlanStatuses.StepInProgress, afterTool.Phases[0].Steps.Single(s => s.BoundTaskKey == "verify-api").Status);

        // Verify session.plan.updated SSE event was persisted to lifecycle journal
        var events = await lifecycle.ReplayEventsAsync(sessionId, 0);
        Assert.Contains(events, e => e.EventType == "session.plan.updated");

        // Verify plan_read virtual tool policy and manifest entry
        Assert.True(CoreVirtualToolPolicy.IsCoreVirtual(CoreVirtualToolPolicy.PlanReadToolId));
        var manifest = CoreSessionPlanTools.ReadManifestEntry();
        Assert.Equal("plan_read", manifest.Id);
    }

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
