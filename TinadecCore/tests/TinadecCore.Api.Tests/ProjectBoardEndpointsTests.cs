using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime.ProjectBoard;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Phase 1 (#54) API, SSE, Migration, and OpenAPI integration tests for <c>ProjectBoardEndpoints</c>
/// and <c>SessionRecord</c> (<c>origin</c> / <c>board_task_id</c>).
/// </summary>
public sealed class ProjectBoardEndpointsTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("project-board-api");
    private ProjectBoardApiFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new ProjectBoardApiFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SessionBoardOriginMigration_UpgradesExistingTableAndExposesOriginAndBoardTaskIdOverRest()
    {
        await using (var connection = new SqliteConnection("Data Source=:memory:"))
        {
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
            var existingId = Guid.NewGuid();
            await using (var db = new MemoryDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Sessions.Add(new SessionRecord
                {
                    Id = existingId,
                    TenantId = Guid.NewGuid(),
                    WorkspaceId = Guid.NewGuid(),
                    Title = "Legacy chat",
                    ModeVersionId = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();

                await db.Database.ExecuteSqlRawAsync("""
                    alter table sessions drop column origin;
                    alter table sessions drop column board_task_id;
                    """);

                var migration = new TinadecCore.Storage.Migrations.Sqlite.SessionBoardOrigin();
                foreach (var op in migration.UpOperations.Cast<SqlOperation>())
                    await db.Database.ExecuteSqlRawAsync(op.Sql);
                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);
            }

            await using var verifyDb = new MemoryDbContext(options);
            var legacyRow = await verifyDb.Sessions.SingleAsync(x => x.Id == existingId);
            Assert.Null(legacyRow.Origin);
            Assert.Null(legacyRow.BoardTaskId);
        }

        using var client = _factory!.CreateClient();
        var projDir = Path.Combine(_root, "session-origin-proj");
        Directory.CreateDirectory(projDir);
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "OriginProj", path = projDir });
        projResp.EnsureSuccessStatusCode();
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var boardTaskId = Guid.NewGuid();
        var createSessResp = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = projectId,
            title = "[ORI-1] Background Task Session",
            origin = "board_task",
            board_task_id = boardTaskId
        });
        Assert.Equal(HttpStatusCode.Created, createSessResp.StatusCode);
        var createdJson = await createSessResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("board_task", createdJson.GetProperty("origin").GetString());
        Assert.Equal(boardTaskId, createdJson.GetProperty("board_task_id").GetGuid());

        var listResp = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions?project_id={projectId}");
        var listed = Assert.Single(listResp.EnumerateArray());
        Assert.Equal("board_task", listed.GetProperty("origin").GetString());
        Assert.Equal(boardTaskId, listed.GetProperty("board_task_id").GetGuid());
    }

    [Fact]
    public async Task ProjectBoardRestAndSse_CoversCrud_Cycles_ConflictResolution_PassiveGuardrail_Execution_GlobalSummary_Views_AndOpenApi()
    {
        using var client = _factory!.CreateClient();

        // 1. OpenAPI publishes ProjectBoard routes cleanly
        var openApiResp = await client.GetAsync("/openapi/core.json");
        Assert.Equal(HttpStatusCode.OK, openApiResp.StatusCode);
        var openApiJson = await openApiResp.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/board/summary", openApiJson);
        Assert.Contains("/api/v1/board/views", openApiJson);
        Assert.Contains("/board/tasks", openApiJson);
        Assert.Contains("/api/v1/projects/{projectId}/board/tasks/promote-from-spatial", openApiJson);

        // 2. Create two projects for cross-project global board verification
        var repoA = Path.Combine(_root, "project-alpha");
        var repoB = Path.Combine(_root, "project-beta");
        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(repoB);

        var projAResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "AlphaService", path = repoA });
        projAResp.EnsureSuccessStatusCode();
        var projectA = (await projAResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var projBResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "BetaWeb", path = repoB });
        projBResp.EnsureSuccessStatusCode();
        var projectB = (await projBResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // 3. GET & PUT /board/settings
        var settingsA = await client.GetFromJsonAsync<ProjectBoardSettingsDto>($"/api/v1/projects/{projectA}/board/settings");
        Assert.NotNull(settingsA);
        Assert.Equal("ALP", settingsA!.KeyPrefix);
        Assert.Equal(ProjectBoardStorageModes.RepoTinadec, settingsA.StorageMode);

        var putSettingsResp = await client.PutAsJsonAsync($"/api/v1/projects/{projectA}/board/settings", new
        {
            key_prefix = "ALPHA",
            project_instructions = "Always run targeted tests.",
            max_concurrency = 3
        });
        Assert.Equal(HttpStatusCode.OK, putSettingsResp.StatusCode);
        var updatedSettings = await putSettingsResp.Content.ReadFromJsonAsync<ProjectBoardSettingsDto>();
        Assert.Equal("ALPHA", updatedSettings!.KeyPrefix);
        Assert.Equal(3, updatedSettings.MaxConcurrency);

        // 4. Open live SSE stream on /api/v1/board/events before creating tasks
        using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/board/events");
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        Assert.Equal("text/event-stream", sseResponse.Content.Headers.ContentType?.MediaType);
        await using var sseStream = await sseResponse.Content.ReadAsStreamAsync(sseCts.Token);
        using var sseReader = new StreamReader(sseStream, Encoding.UTF8);

        var snapshotEvent = await ReadNextSseEventAsync(sseReader, sseCts.Token);
        Assert.Equal("snapshot", snapshotEvent.EventType);

        // 5. Create tasks & verify .tinadec/board/tasks/<KEY>.md creation + SSE broadcast
        var t1Resp = await client.PostAsJsonAsync($"/api/v1/projects/{projectA}/board/tasks", new
        {
            title = "Design database schema",
            description_md = "Create normalized tables.",
            status = "ready",
            priority = "high",
            labels = new[] { "backend", "db" },
            checklist = new[] { new { id = "chk-1", text = "Add indexes", done = false } }
        });
        Assert.Equal(HttpStatusCode.Created, t1Resp.StatusCode);
        var t1 = await t1Resp.Content.ReadFromJsonAsync<ProjectBoardTaskDto>();
        Assert.NotNull(t1);
        Assert.Equal("ALPHA-1", t1!.TaskKey);
        Assert.True(File.Exists(Path.Combine(repoA, ".tinadec", "board", "tasks", "ALPHA-1.md")));
        Assert.False(Directory.Exists(Path.Combine(repoA, ".agent")));

        var upsertSse = await ReadNextSseEventAsync(sseReader, sseCts.Token);
        Assert.Equal("task_upserted", upsertSse.EventType);

        var t2Resp = await client.PostAsJsonAsync($"/api/v1/projects/{projectA}/board/tasks", new
        {
            title = "Build REST endpoints",
            depends_on = new[] { t1.TaskId }
        });
        Assert.Equal(HttpStatusCode.Created, t2Resp.StatusCode);
        var t2 = (await t2Resp.Content.ReadFromJsonAsync<ProjectBoardTaskDto>())!;

        // 6. Reject DAG cycle with 409 cycle_detected & stale revision with 409 revision_conflict
        var cycleResp = await client.PatchAsJsonAsync($"/api/v1/projects/{projectA}/board/tasks/{t1.TaskId}", new
        {
            depends_on = new[] { t2.TaskId }
        });
        Assert.Equal(HttpStatusCode.Conflict, cycleResp.StatusCode);
        var cycleErr = await cycleResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cycle_detected", cycleErr.GetProperty("code").GetString());

        var staleRevResp = await client.PatchAsJsonAsync($"/api/v1/projects/{projectA}/board/tasks/{t1.TaskId}", new
        {
            title = "Outdated edit",
            expected_revision = 999L
        });
        Assert.Equal(HttpStatusCode.Conflict, staleRevResp.StatusCode);
        var revErr = await staleRevResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("revision_conflict", revErr.GetProperty("code").GetString());

        // 7. Passive .md edit to in_progress + conflict resolution over HTTP (zero auto-dispatch!)
        var t1MdPath = Path.Combine(repoA, ".tinadec", "board", "tasks", "ALPHA-1.md");
        var passiveMd = (await File.ReadAllTextAsync(t1MdPath, Encoding.UTF8))
            .Replace("status: \"ready\"", "status: in_progress", StringComparison.Ordinal);
        await File.WriteAllTextAsync(t1MdPath, passiveMd, Encoding.UTF8);

        var syncResp = await client.PostAsync($"/api/v1/projects/{projectA}/board/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, syncResp.StatusCode);
        Assert.Equal(0, _factory.StubCli.CallCount);
        var sessionsInA = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions?project_id={projectA}");
        Assert.Empty(sessionsInA.EnumerateArray());

        // Create second task on projectB and dispatch via CLI -> moves to in_review -> approve -> done
        var tbResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectB}/board/tasks", new
        {
            title = "Ship board UI",
            status = "ready",
            priority = "urgent"
        });
        var tb = (await tbResp.Content.ReadFromJsonAsync<ProjectBoardTaskDto>())!;

        var dispatchResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectB}/board/tasks/{tb.TaskId}/dispatch", new
        {
            assignee_override = new { kind = "cli", harness_id = "claude-code" },
            use_worktree = false
        });
        Assert.Equal(HttpStatusCode.OK, dispatchResp.StatusCode);
        var dispatchedTb = (await dispatchResp.Content.ReadFromJsonAsync<ProjectBoardTaskDto>())!;
        Assert.Equal(ProjectBoardTaskStatuses.InReview, dispatchedTb.Status);
        Assert.NotNull(dispatchedTb.ActiveSessionId);
        Assert.Equal(1, _factory.StubCli.CallCount);

        // Global summary reflects both projects and needs_action_count >= 1 (tb is in_review)
        var summary = await client.GetFromJsonAsync<ProjectBoardSummaryDto>("/api/v1/board/summary");
        Assert.NotNull(summary);
        Assert.Equal(3, summary!.TotalTasks);
        Assert.Equal(1, summary.InReviewCount);
        Assert.True(summary.NeedsActionCount >= 1);
        Assert.Equal(2, summary.Projects.Count);

        // Global tasks filter by needs_my_action=true
        var actionTasks = await client.GetFromJsonAsync<List<ProjectBoardTaskDto>>("/api/v1/board/tasks?needs_my_action=true");
        Assert.NotNull(actionTasks);
        Assert.Contains(actionTasks!, x => x.TaskId == tb.TaskId);

        // Diff + Approve
        var diffResp = await client.GetFromJsonAsync<ProjectBoardTaskDiffDto>($"/api/v1/projects/{projectB}/board/tasks/{tb.TaskId}/diff");
        Assert.NotNull(diffResp);

        var approveResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectB}/board/tasks/{tb.TaskId}/approve", new
        {
            merge_or_clean_worktree = false,
            note = "Verified clean."
        });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        var approvedTb = (await approveResp.Content.ReadFromJsonAsync<ProjectBoardTaskDto>())!;
        Assert.Equal(ProjectBoardTaskStatuses.Done, approvedTb.Status);

        // 8. Saved Views CRUD (/api/v1/board/views)
        var createViewResp = await client.PostAsJsonAsync("/api/v1/board/views", new
        {
            scope = "global",
            name = "Swimlane By Project",
            config = new
            {
                layout = "swimlane",
                group_by = "status",
                swimlane_by = "project",
                sort_by = "priority",
                filters = new { needs_my_action = true },
                visible_fields = new[] { "project", "live_digest", "diff_stats" }
            }
        });
        Assert.Equal(HttpStatusCode.Created, createViewResp.StatusCode);
        var view = (await createViewResp.Content.ReadFromJsonAsync<ProjectBoardViewDto>())!;
        Assert.Equal("swimlane", view.Config.Layout);
        Assert.Equal("project", view.Config.SwimlaneBy);

        var patchViewResp = await client.PatchAsJsonAsync($"/api/v1/board/views/{view.ViewId}", new { name = "Executive Swimlane" });
        Assert.Equal(HttpStatusCode.OK, patchViewResp.StatusCode);
        Assert.Equal("Executive Swimlane", (await patchViewResp.Content.ReadFromJsonAsync<ProjectBoardViewDto>())!.Name);

        var views = await client.GetFromJsonAsync<List<ProjectBoardViewDto>>("/api/v1/board/views?scope=global");
        Assert.Contains(views!, v => v.ViewId == view.ViewId);

        var delViewResp = await client.DeleteAsync($"/api/v1/board/views/{view.ViewId}");
        Assert.Equal(HttpStatusCode.NoContent, delViewResp.StatusCode);

        var delTaskResp = await client.DeleteAsync($"/api/v1/projects/{projectA}/board/tasks/{t2.TaskId}");
        Assert.Equal(HttpStatusCode.NoContent, delTaskResp.StatusCode);
    }

    [Fact]
    public async Task ManagedStorageScopes_AggregatesCrossProjectTasksSummaryViewsAndLiveSseAcrossMountedScopes()
    {
        var managedRoot = ApiTestStorage.CreateRoot("project-board-managed-scopes");
        var userStorageRoot = Path.Combine(managedRoot, "user-storage");
        Directory.CreateDirectory(userStorageRoot);
        var hostToken = Guid.NewGuid().ToString("N");
        var prevToken = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN");
        Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", hostToken);
        try
        {
            using var managedFactory = new ManagedScopeBoardFactory(userStorageRoot);
            using var client = managedFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", hostToken);

            var repoOne = Path.Combine(managedRoot, "ws-one");
            var repoTwo = Path.Combine(managedRoot, "ws-two");
            Directory.CreateDirectory(repoOne);
            Directory.CreateDirectory(repoTwo);

            var p1Resp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "WorkspaceOne", path = repoOne });
            p1Resp.EnsureSuccessStatusCode();
            var p1Json = await p1Resp.Content.ReadFromJsonAsync<JsonElement>();
            var projectOneId = p1Json.GetProperty("id").GetGuid();
            var storageOneId = p1Json.GetProperty("storage_id").GetString()!;

            var p2Resp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "WorkspaceTwo", path = repoTwo });
            p2Resp.EnsureSuccessStatusCode();
            var p2Json = await p2Resp.Content.ReadFromJsonAsync<JsonElement>();
            var projectTwoId = p2Json.GetProperty("id").GetGuid();

            // Subscribe to host-level /api/v1/board/events SSE stream
            using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var sseReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/board/events");
            using var sseResp = await client.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
            sseResp.EnsureSuccessStatusCode();
            await using var sseStream = await sseResp.Content.ReadAsStreamAsync(sseCts.Token);
            using var sseReader = new StreamReader(sseStream, Encoding.UTF8);
            var initFrame = await ReadNextSseEventAsync(sseReader, sseCts.Token);
            Assert.Equal("snapshot", initFrame.EventType);

            // Create task on projectOne WITH X-Tinadec-Storage-Id header
            using (var createWithHeader = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectOneId}/board/tasks")
            {
                Content = JsonContent.Create(new { title = "Scoped Task One", status = "in_review" })
            })
            {
                createWithHeader.Headers.Add("X-Tinadec-Storage-Id", storageOneId);
                var r1 = await client.SendAsync(createWithHeader, sseCts.Token);
                Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
            }

            // Host global SSE receives forwarded task_upserted from child project scope!
            var forwardedFrame = await ReadNextSseEventAsync(sseReader, sseCts.Token);
            Assert.Equal("task_upserted", forwardedFrame.EventType);

            // Create task on projectTwo WITHOUT X-Tinadec-Storage-Id header (auto-routed by projectId!)
            var r2 = await client.PostAsJsonAsync($"/api/v1/projects/{projectTwoId}/board/tasks", new
            {
                title = "Scoped Task Two",
                blocked = true,
                blocked_reason = "Waiting on API spec"
            }, sseCts.Token);
            Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

            // Cross-project global board aggregates both mounted project scopes
            var globalTasks = await client.GetFromJsonAsync<List<ProjectBoardTaskDto>>("/api/v1/board/tasks", sseCts.Token);
            Assert.NotNull(globalTasks);
            Assert.Equal(2, globalTasks!.Count);

            var globalSummary = await client.GetFromJsonAsync<ProjectBoardSummaryDto>("/api/v1/board/summary", sseCts.Token);
            Assert.NotNull(globalSummary);
            Assert.Equal(2, globalSummary!.TotalTasks);
            Assert.Equal(2, globalSummary.NeedsActionCount);
            Assert.Equal(2, globalSummary.Projects.Count);

            // Project-scoped Saved View created WITH project storage header is still updatable/deletable WITHOUT storage header
            ProjectBoardViewDto createdView;
            using (var viewReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/board/views")
            {
                Content = JsonContent.Create(new { scope = "project", project_id = projectOneId, name = "Project 1 Kanban" })
            })
            {
                viewReq.Headers.Add("X-Tinadec-Storage-Id", storageOneId);
                var vr = await client.SendAsync(viewReq, sseCts.Token);
                Assert.Equal(HttpStatusCode.Created, vr.StatusCode);
                createdView = (await vr.Content.ReadFromJsonAsync<ProjectBoardViewDto>(cancellationToken: sseCts.Token))!;
            }

            var patchWithoutHeader = await client.PatchAsJsonAsync($"/api/v1/board/views/{createdView.ViewId}", new { name = "Renamed View" }, sseCts.Token);
            Assert.Equal(HttpStatusCode.OK, patchWithoutHeader.StatusCode);

            // Promote spatial objects inside managed project scope -> creates tasks & stamps board_task_id/board_task_key
            Guid scopedSessionId;
            using (var createSess = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sessions")
            {
                Content = JsonContent.Create(new { project_id = projectOneId, title = "Scoped Canvas" })
            })
            {
                createSess.Headers.Add("X-Tinadec-Storage-Id", storageOneId);
                var sessResp = await client.SendAsync(createSess, sseCts.Token);
                sessResp.EnsureSuccessStatusCode();
                scopedSessionId = (await sessResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: sseCts.Token)).GetProperty("id").GetGuid();
            }

            using (var opsReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{scopedSessionId}/spatial-board/ops")
            {
                Content = JsonContent.Create(new
                {
                    client_id = "scoped-ui",
                    ops = new[]
                    {
                        new { client_op_id = "s-op-1", object_id = "scoped-sticky-1", kind = "sticky", props = new { text = "Scoped promoted sticky" } }
                    }
                })
            })
            {
                opsReq.Headers.Add("X-Tinadec-Storage-Id", storageOneId);
                var opsResp = await client.SendAsync(opsReq, sseCts.Token);
                Assert.Equal(HttpStatusCode.OK, opsResp.StatusCode);
            }

            var scopedPromoteResp = await client.PostAsJsonAsync(
                $"/api/v1/projects/{projectOneId}/board/tasks/promote-from-spatial",
                new { session_id = scopedSessionId, object_ids = new[] { "scoped-sticky-1" }, mode = "split_many", target_status = "ready" },
                sseCts.Token);
            Assert.Equal(HttpStatusCode.OK, scopedPromoteResp.StatusCode);
            var scopedPromoted = (await scopedPromoteResp.Content.ReadFromJsonAsync<PromoteFromSpatialResultDto>(cancellationToken: sseCts.Token))!;
            Assert.Single(scopedPromoted.Tasks);
            Assert.Null(scopedPromoted.ParentTaskId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", prevToken);
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(managedRoot)) Directory.Delete(managedRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PromoteFromSpatialEndpoint_ValidatesInputs_SupportsAllModes_AndBroadcastsLiveTaskLinksOverSpatialWebSocket()
    {
        using var client = _factory!.CreateClient();

        var repoDir = Path.Combine(_root, "spatial-promote-http");
        Directory.CreateDirectory(repoDir);
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "TinadecStudio", path = repoDir });
        projResp.EnsureSuccessStatusCode();
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var sessResp = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Architecture Jam" });
        sessResp.EnsureSuccessStatusCode();
        var sessionId = (await sessResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // 1. Validation: empty object_ids -> 400, invalid mode -> 400, terminal target_status "done" -> 400, missing object -> 404
        var emptyIdsResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/board/tasks/promote-from-spatial", new
        {
            session_id = sessionId,
            object_ids = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyIdsResp.StatusCode);

        var badModeResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/board/tasks/promote-from-spatial", new
        {
            session_id = sessionId,
            object_ids = new[] { "s1" },
            mode = "unknown_mode"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badModeResp.StatusCode);

        var doneStatusResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/board/tasks/promote-from-spatial", new
        {
            session_id = sessionId,
            object_ids = new[] { "s1" },
            target_status = "done"
        });
        Assert.Equal(HttpStatusCode.BadRequest, doneStatusResp.StatusCode);

        var missingObjResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/board/tasks/promote-from-spatial", new
        {
            session_id = sessionId,
            object_ids = new[] { "does-not-exist" }
        });
        Assert.Equal(HttpStatusCode.NotFound, missingObjResp.StatusCode);

        // 2. Seed spatial section + stickies + connector
        var seedResp = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/spatial-board/ops", new
        {
            client_id = "jam-user",
            ops = new object[]
            {
                new { client_op_id = "op-sec", object_id = "sec-1", kind = "section", x = 50, y = 50, props = new { title = "Phase 2 Bridge" } },
                new { client_op_id = "op-s1", object_id = "s-1", kind = "sticky", parent_id = "sec-1", has_parent_id = true, x = 80, y = 100, props = new { text = "Implement Split Button" } },
                new { client_op_id = "op-s2", object_id = "s-2", kind = "sticky", parent_id = "sec-1", has_parent_id = true, x = 320, y = 100, props = new { text = "Render Live Status Pill" } },
                new { client_op_id = "op-conn", object_id = "conn-1", kind = "connector", props = new { sourceId = "s-1", targetId = "s-2" } }
            }
        });
        Assert.Equal(HttpStatusCode.OK, seedResp.StatusCode);

        // 3. Connect a live spatial WebSocket peer to verify promotion broadcasts board_task_id/board_task_key in real time!
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new Uri(_factory.Server.BaseAddress, $"/api/v1/sessions/{sessionId}/spatial-board/ws?client_id=observer-1");
        using var ws = await wsClient.ConnectAsync(wsUri, cts.Token);
        _ = await ReadWsFrameOfTypeAsync(ws, "snapshot", cts.Token);

        // 4. Promote via parent_with_children and verify parent_task_id JSON serialization, links, and WS broadcast
        var promoteResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/board/tasks/promote-from-spatial", new
        {
            session_id = sessionId,
            object_ids = new[] { "sec-1" },
            mode = "parent_with_children",
            target_status = "ready",
            priority = "high",
            labels = new[] { "bridge" },
            viewport_hint = new { x = 50, y = 50, zoom = 1.0 }
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, promoteResp.StatusCode);
        var rawJson = await promoteResp.Content.ReadAsStringAsync(cts.Token);
        using (var doc = JsonDocument.Parse(rawJson))
        {
            Assert.True(doc.RootElement.TryGetProperty("parent_task_id", out var ptEl));
            Assert.Equal(JsonValueKind.String, ptEl.ValueKind);
        }

        var promoted = JsonSerializer.Deserialize<PromoteFromSpatialResultDto>(rawJson, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        })!;
        Assert.Equal("parent_with_children", promoted.Mode);
        Assert.NotNull(promoted.ParentTaskId);
        Assert.Equal(3, promoted.Tasks.Count);
        Assert.Equal(3, promoted.UpdatedObjectLinks.Count);
        Assert.Contains(promoted.Tasks[1].TaskId, promoted.Tasks[2].DependsOn);

        var broadcast = await ReadWsFrameOfTypeAsync(ws, "ops_broadcast", cts.Token);
        var broadcastObjects = broadcast.GetProperty("objects");
        Assert.Equal(promoted.Tasks[0].TaskKey, broadcastObjects.GetProperty("sec-1").GetProperty("props").GetProperty("board_task_key").GetString());
        Assert.Equal(promoted.Tasks[1].TaskKey, broadcastObjects.GetProperty("s-1").GetProperty("props").GetProperty("board_task_key").GetString());
        Assert.Equal(promoted.Tasks[2].TaskKey, broadcastObjects.GetProperty("s-2").GetProperty("props").GetProperty("board_task_key").GetString());

        // Also verify merge_single serializes "parent_task_id": null explicitly
        var mergeResp = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/board/tasks/promote-from-spatial", new
        {
            session_id = sessionId,
            object_ids = new[] { "s-1", "s-2" },
            mode = "merge_single",
            target_status = "backlog"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, mergeResp.StatusCode);
        var mergeJson = await mergeResp.Content.ReadAsStringAsync(cts.Token);
        using (var mergeDoc = JsonDocument.Parse(mergeJson))
        {
            Assert.True(mergeDoc.RootElement.TryGetProperty("parent_task_id", out var nullParentEl));
            Assert.Equal(JsonValueKind.Null, nullParentEl.ValueKind);
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    private static async Task<JsonElement> ReadWsFrameOfTypeAsync(WebSocket socket, string expectedType, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        for (var i = 0; i < 16; i++)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"WebSocket closed before receiving '{expectedType}'.");
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var root = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
            if (root.TryGetProperty("type", out var t) && t.GetString() == expectedType)
                return root;
        }
        throw new TimeoutException($"Did not observe '{expectedType}' frame.");
    }

    private static async Task<(string EventType, string Data)> ReadNextSseEventAsync(StreamReader reader, CancellationToken ct)
    {
        string? eventType = null;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                throw new EndOfStreamException("SSE stream ended unexpectedly.");

            if (line.Length == 0)
            {
                if (eventType is not null || dataBuilder.Length > 0)
                    return (eventType ?? "message", dataBuilder.ToString());
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                dataBuilder.Append(line["data:".Length..].Trim());
        }

        throw new OperationCanceledException(ct);
    }

    private sealed class ProjectBoardApiFactory(string root) : IsolatedApiFactory
    {
        public StubCliExecutor StubCli { get; } = new();

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(root);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IBoardCliHarnessExecutor>(StubCli));
            });
        }
    }

    private sealed class ManagedScopeBoardFactory(string userRoot) : IsolatedApiFactory
    {
        protected override bool UsesManagedStorage => true;
        protected override string? ManagedUserRoot => userRoot;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecStorage:Enabled"] = "true",
                ["TinadecStorage:UserRoot"] = userRoot,
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(userRoot, "default-ws"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }

    private sealed class StubCliExecutor : IBoardCliHarnessExecutor
    {
        public int CallCount { get; private set; }

        public Task<BoardCliHarnessExecutionResult> ExecuteAsync(
            BoardCliHarnessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BoardCliHarnessExecutionResult(true, $"Completed {request.HarnessId} batch cleanly."));
        }
    }
}
