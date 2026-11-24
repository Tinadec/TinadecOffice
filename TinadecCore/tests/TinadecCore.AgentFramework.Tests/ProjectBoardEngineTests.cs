using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Runtime.ProjectBoard;
using TinadecCore.Runtime.SpatialBoard;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// High-signal engine tests for Phase 1 (#54) <c>TinadecCore.Runtime.ProjectBoard</c>:
/// - Base62 Fractional Indexing ordering &amp; normalization;
/// - Strict DAG cycle rejection (<c>cycle_detected</c>) across <c>depends_on</c> &amp; <c>parent_task_id</c>;
/// - Three storage modes (<c>repo_tinadec</c>, <c>app_data</c>, <c>custom_dir</c>) and <c>.agent/</c> prohibition;
/// - Deterministic <c>.tinadec/board/tasks/&lt;KEY&gt;.md</c> sync, SHA-256 anti-echo, 3-way disjoint merge,
///   non-destructive conflict preservation, and <c>keep_file</c>/<c>keep_board</c> resolution;
/// - HARD GUARDRAIL: passive Markdown edits NEVER dispatch or start any Agent or CLI run;
/// - Explicit dispatch (<c>human</c> &amp; <c>cli</c> with Git worktree, resource lease, <c>origin = "board_task"</c>
///   session isolation, diff inspection, request-changes, and approve/cleanup).
/// </summary>
public sealed class ProjectBoardEngineTests : IAsyncLifetime
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"tinadec-pboard-engine-{Guid.NewGuid():N}");
    private ServiceProvider? _provider;
    private RecordingCliHarnessExecutor _cliExecutor = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var scopeRoot = Path.Combine(_tempRoot, "scope");
        var dataRoot = Path.Combine(scopeRoot, "data");
        Directory.CreateDirectory(dataRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Provider"] = "Sqlite",
                ["TinadecPersistence:DataRoot"] = dataRoot,
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(dataRoot, "tinadec.db")
            })
            .Build();

        _cliExecutor = new RecordingCliHarnessExecutor();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddTinadecPersistence(config, scopeRoot);
        services.AddTinadecCore();
        services.Replace(ServiceDescriptor.Singleton<IBoardCliHarnessExecutor>(_cliExecutor));

        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            _provider.GetService<ProjectAutopilotService>()?.Dispose();
            _provider.GetService<ProjectBoardFileSyncService>()?.Dispose();
            await _provider.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void FractionalIndexing_MaintainsStrictOrdinalOrderAcrossSequentialAndMidpointInsertions()
    {
        var keys = new List<string>();
        string? last = null;
        for (var i = 0; i < 40; i++)
        {
            var next = ProjectBoardFractionalIndex.Between(last, null);
            Assert.True(ProjectBoardFractionalIndex.IsValid(next), $"Invalid key '{next}'");
            if (last is not null)
                Assert.True(string.CompareOrdinal(last, next) < 0, $"Expected '{last}' < '{next}'");
            keys.Add(next);
            last = next;
        }

        // Repeated insertions before the very first key ("V" -> "F" -> "7" -> "3" -> "1" -> "0V" -> "0F" ...)
        var head = keys[0];
        for (var i = 0; i < 20; i++)
        {
            var before = ProjectBoardFractionalIndex.Between(null, head);
            Assert.True(ProjectBoardFractionalIndex.IsValid(before), $"Invalid head key '{before}'");
            Assert.True(string.CompareOrdinal(before, head) < 0, $"Expected '{before}' < '{head}'");
            head = before;
        }

        // Midpoint insertions between adjacent digits ("V" and "W")
        var mid1 = ProjectBoardFractionalIndex.Between("V", "W");
        Assert.True(string.CompareOrdinal("V", mid1) < 0 && string.CompareOrdinal(mid1, "W") < 0);
        var mid2 = ProjectBoardFractionalIndex.Between("V", mid1);
        Assert.True(string.CompareOrdinal("V", mid2) < 0 && string.CompareOrdinal(mid2, mid1) < 0);

        // Trailing '0' normalization
        var normalized = ProjectBoardFractionalIndex.Normalize("a0");
        Assert.True(ProjectBoardFractionalIndex.IsValid(normalized));
        Assert.NotEqual('0', normalized[^1]);
    }

    [Fact]
    public async Task Coordinator_RejectsDirectTransitiveAndCrossHierarchyDagCycles_AndEnforcesOptimisticConcurrency()
    {
        var store = _provider!.GetRequiredService<ProjectSessionStore>();
        var coordinator = _provider!.GetRequiredService<ProjectBoardCoordinator>();

        var repoDir = Path.Combine(_tempRoot, "dag-repo");
        Directory.CreateDirectory(repoDir);
        var project = await store.CreateProjectAsync("Tinadec Dag", repoDir);

        var t1 = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest { Title = "Task 1" });
        var t2 = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Task 2",
            DependsOn = [t1.TaskId]
        });
        var t3 = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Task 3",
            DependsOn = [t2.TaskId]
        });

        Assert.Equal("TIN-1", t1.TaskKey);
        Assert.Equal("TIN-2", t2.TaskKey);
        Assert.Equal("TIN-3", t3.TaskKey);

        // 1. Direct self-dependency & self-parent
        var selfDepEx = await Assert.ThrowsAsync<ProjectBoardCycleException>(() =>
            coordinator.UpdateTaskAsync(project.Id, t1.TaskId, new UpdateProjectBoardTaskRequest { DependsOn = [t1.TaskId] }));
        Assert.Equal("cycle_detected", selfDepEx.Code);

        var selfParentEx = await Assert.ThrowsAsync<ProjectBoardCycleException>(() =>
            coordinator.UpdateTaskAsync(project.Id, t1.TaskId, new UpdateProjectBoardTaskRequest { ParentTaskId = t1.TaskId }));
        Assert.Equal("cycle_detected", selfParentEx.Code);

        // 2. Transitive depends_on cycle: t1 -> t3 (while t3 -> t2 -> t1)
        var transitiveEx = await Assert.ThrowsAsync<ProjectBoardCycleException>(() =>
            coordinator.UpdateTaskAsync(project.Id, t1.TaskId, new UpdateProjectBoardTaskRequest { DependsOn = [t3.TaskId] }));
        Assert.Equal("cycle_detected", transitiveEx.Code);

        // 3. Cross parent_task_id + depends_on cycle: t1.parent_task_id = t3 (while t3 -> t2 -> t1)
        var crossEx = await Assert.ThrowsAsync<ProjectBoardCycleException>(() =>
            coordinator.UpdateTaskAsync(project.Id, t1.TaskId, new UpdateProjectBoardTaskRequest { ParentTaskId = t3.TaskId }));
        Assert.Equal("cycle_detected", crossEx.Code);

        // 4. Valid parent + sequential sibling subtasks succeeds
        var parent = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest { Title = "Umbrella Epic" });
        var subA = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Subtask A",
            ParentTaskId = parent.TaskId
        });
        var subB = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Subtask B",
            ParentTaskId = parent.TaskId,
            DependsOn = [subA.TaskId]
        });
        Assert.Equal(parent.TaskId, subB.ParentTaskId);
        Assert.Contains(subA.TaskId, subB.DependsOn);

        // Subtask depending on its own parent is rejected as cycle_detected
        await Assert.ThrowsAsync<ProjectBoardCycleException>(() =>
            coordinator.UpdateTaskAsync(project.Id, subA.TaskId, new UpdateProjectBoardTaskRequest { DependsOn = [parent.TaskId] }));

        // 5. Optimistic concurrency expected_revision check
        var updatedT1 = await coordinator.UpdateTaskAsync(project.Id, t1.TaskId, new UpdateProjectBoardTaskRequest
        {
            Title = "Task 1 Revised",
            ExpectedRevision = t1.Revision
        });
        Assert.NotNull(updatedT1);
        Assert.Equal(t1.Revision + 1, updatedT1!.Revision);

        var revEx = await Assert.ThrowsAsync<ProjectBoardRevisionConflictException>(() =>
            coordinator.UpdateTaskAsync(project.Id, t1.TaskId, new UpdateProjectBoardTaskRequest
            {
                Title = "Stale Write",
                ExpectedRevision = t1.Revision
            }));
        Assert.Equal("revision_conflict", revEx.Code);
        Assert.Equal(updatedT1.Revision, revEx.CurrentRevision);
    }

    [Fact]
    public async Task FileSync_SupportsBidirectionalSync_AntiEcho_ThreeWayMerge_NonDestructiveConflicts_AndNeverAutoDispatches()
    {
        var store = _provider!.GetRequiredService<ProjectSessionStore>();
        var coordinator = _provider!.GetRequiredService<ProjectBoardCoordinator>();
        var syncService = _provider!.GetRequiredService<ProjectBoardFileSyncService>();
        var execution = _provider!.GetRequiredService<ProjectTaskExecutionService>();

        var repoDir = Path.Combine(_tempRoot, "sync-repo");
        Directory.CreateDirectory(repoDir);
        var project = await store.CreateProjectAsync("SyncProj", repoDir);

        // Forbid .agent custom_sync_root
        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.UpdateSettingsAsync(project.Id, new UpdateProjectBoardSettingsRequest
            {
                StorageMode = ProjectBoardStorageModes.CustomDir,
                CustomSyncRoot = Path.Combine(_tempRoot, ".agent", "board")
            }));

        // Create task -> writes <repo>/.tinadec/board/tasks/SYN-1.md (and NEVER creates .agent/)
        var task = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Implement sync engine",
            DescriptionMd = "Initial description.",
            Priority = ProjectBoardPriorities.Medium,
            Checklist =
            [
                new ProjectBoardChecklistItemDto { Id = "c1", Text = "Write codec", Done = false },
                new ProjectBoardChecklistItemDto { Id = "c2", Text = "Verify SHA-256", Done = false }
            ],
            Labels = ["core", "sync"]
        });

        Assert.False(Directory.Exists(Path.Combine(repoDir, ".agent")), ".agent directory must NEVER be created.");
        var mdPath = Path.Combine(repoDir, ".tinadec", "board", "tasks", $"{task.TaskKey}.md");
        Assert.True(File.Exists(mdPath));

        var initialMd = await File.ReadAllTextAsync(mdPath, Encoding.UTF8);
        Assert.Contains("## 目标描述", initialMd);
        Assert.Contains("## 验收标准", initialMd);
        Assert.DoesNotContain("active_session_id", initialMd);
        Assert.DoesNotContain("active_run_id", initialMd);
        Assert.DoesNotContain("worktree_path", initialMd);
        Assert.DoesNotContain("lease_id", initialMd);

        // 1. SHA-256 Anti-Echo: syncing when nothing changed reports unchanged_count == 1
        var echoSync = await syncService.SyncProjectAsync(project.Id);
        Assert.Equal(1, echoSync.UnchangedCount);
        Assert.Equal(0, echoSync.ImportedCount);
        Assert.Equal(0, echoSync.ExportedCount);
        Assert.Equal(0, echoSync.MergedCount);
        Assert.Equal(0, echoSync.ConflictCount);

        // 2. Disjoint 3-way merge:
        //    - Board UI updates priority -> high
        //    - Wait/simulate offline edit on pre-priority baseline? To test true simultaneous 3-way merge:
        //      capture baseline markdown BEFORE board update, update board priority, then write file editing ONLY description & checklist!
        var baseMd = initialMd;
        await coordinator.UpdateTaskAsync(project.Id, task.TaskId, new UpdateProjectBoardTaskRequest
        {
            Priority = ProjectBoardPriorities.High
        });
        // Wait: UpdateTaskAsync exported priority: "high" to mdPath! Wait — if a user had the file open in an editor
        // BEFORE priority changed? No, if UpdateTaskAsync updated LastSyncedPayloadJson to priority="high", writing old priority="medium"
        // would look like the user changed priority back to medium unless we edit only disjoint fields or test conflict!
        // Let's test disjoint edit where file changes description + checklist while keeping current priority, AND test true 3-way merge!
        var afterBoardMd = await File.ReadAllTextAsync(mdPath, Encoding.UTF8);
        var editedFileDisjoint = afterBoardMd
            .Replace("Initial description.", "Updated from external editor.", StringComparison.Ordinal)
            .Replace("- [ ] Write codec", "- [x] Write codec", StringComparison.Ordinal);
        await File.WriteAllTextAsync(mdPath, editedFileDisjoint, Encoding.UTF8);

        var disjointSync = await syncService.SyncProjectAsync(project.Id);
        Assert.Equal(0, disjointSync.ConflictCount);
        var afterDisjoint = await coordinator.GetTaskAsync(project.Id, task.TaskId);
        Assert.NotNull(afterDisjoint);
        Assert.Equal("Updated from external editor.", afterDisjoint!.DescriptionMd);
        Assert.Equal(ProjectBoardPriorities.High, afterDisjoint.Priority);
        Assert.True(afterDisjoint.Checklist.First(c => c.Id == "c1").Done);

        // 3. HARD GUARDRAIL: Passive .md edit setting status: in_progress and assignee: cli MUST NEVER start any run or session!
        var passiveInProgressMd = (await File.ReadAllTextAsync(mdPath, Encoding.UTF8))
            .Replace("status: \"backlog\"", "status: in_progress", StringComparison.Ordinal)
            .Replace("assignee: null", "assignee:\n  kind: cli\n  harness_id: claude-code", StringComparison.Ordinal);
        await File.WriteAllTextAsync(mdPath, passiveInProgressMd, Encoding.UTF8);

        // Also drop a brand-new external task file SYN-50.md already marked in_progress for native agent
        var externalNewMdPath = Path.Combine(repoDir, ".tinadec", "board", "tasks", "SYN-50.md");
        await File.WriteAllTextAsync(externalNewMdPath, """
            ---
            assignee:
              kind: native
            blocked: false
            key: SYN-50
            priority: urgent
            status: in_progress
            title: Imported external markdown task
            ---

            ## 目标描述

            Should import passively without starting any run!

            ## 验收标准

            - [ ] Safe passive sync
            """, Encoding.UTF8);

        var passiveSync = await syncService.SyncProjectAsync(project.Id);
        Assert.Equal(1, passiveSync.ImportedCount);
        Assert.Equal(0, execution.DispatchCount);
        Assert.Equal(0, _cliExecutor.CallCount);
        var sessionsAfterPassive = await store.ListSessionsAsync(project.Id);
        Assert.Empty(sessionsAfterPassive);

        var passiveTask = await coordinator.GetTaskAsync(project.Id, task.TaskId);
        Assert.Equal(ProjectBoardTaskStatuses.InProgress, passiveTask!.Status);
        Assert.Equal("claude-code", passiveTask.Assignee?.HarnessId);
        Assert.Null(passiveTask.ActiveSessionId);
        Assert.Null(passiveTask.ActiveRunId);

        // 4. Non-destructive Conflict Snapshot + keep_file / keep_board resolution:
        //    Simulate concurrent board DB mutation (without touching disk) + external user .md edit on `title`.
        await coordinator.MutateTaskRuntimeStateAsync(project.Id, task.TaskId, row =>
        {
            row.Title = "Title From Board DB";
        }, exportDeclarativeMarkdown: false);

        var userConflictingMd = (await File.ReadAllTextAsync(mdPath, Encoding.UTF8))
            .Replace("Implement sync engine", "Title Hand-Edited By User", StringComparison.Ordinal);
        await File.WriteAllTextAsync(mdPath, userConflictingMd, Encoding.UTF8);

        var conflictSync = await syncService.SyncProjectAsync(project.Id);
        Assert.Equal(1, conflictSync.ConflictCount);

        // User's .md file on disk MUST remain completely untouched!
        var diskTextDuringConflict = await File.ReadAllTextAsync(mdPath, Encoding.UTF8);
        Assert.Equal(userConflictingMd, diskTextDuringConflict);

        var conflictedTask = await coordinator.GetTaskAsync(project.Id, task.TaskId);
        Assert.True(conflictedTask!.HasSyncConflict);
        Assert.NotNull(conflictedTask.SyncConflict);
        Assert.Contains("title", conflictedTask.SyncConflict!.ConflictingFields);
        Assert.Equal("Title Hand-Edited By User", conflictedTask.SyncConflict.FileSnapshot.Title);
        Assert.Equal("Title From Board DB", conflictedTask.SyncConflict.BoardSnapshot.Title);

        // Resolve via keep_file -> board adopts user's file title and clears conflict
        var resolvedKeepFile = await syncService.ResolveConflictAsync(
            project.Id,
            task.TaskId,
            new ResolveProjectBoardConflictRequest { Resolution = "keep_file" });
        Assert.NotNull(resolvedKeepFile);
        Assert.False(resolvedKeepFile!.HasSyncConflict);
        Assert.Null(resolvedKeepFile.SyncConflict);
        Assert.Equal("Title Hand-Edited By User", resolvedKeepFile.Title);

        // Trigger a second conflict and resolve via keep_board -> disk adopts board title
        await coordinator.MutateTaskRuntimeStateAsync(project.Id, task.TaskId, row =>
        {
            row.Title = "Authoritative Board Title";
        }, exportDeclarativeMarkdown: false);
        var secondUserMd = (await File.ReadAllTextAsync(mdPath, Encoding.UTF8))
            .Replace("Title Hand-Edited By User", "Transient File Title", StringComparison.Ordinal);
        await File.WriteAllTextAsync(mdPath, secondUserMd, Encoding.UTF8);
        _ = await syncService.SyncProjectAsync(project.Id);

        var resolvedKeepBoard = await syncService.ResolveConflictAsync(
            project.Id,
            task.TaskId,
            new ResolveProjectBoardConflictRequest { Resolution = "keep_board" });
        Assert.NotNull(resolvedKeepBoard);
        Assert.False(resolvedKeepBoard!.HasSyncConflict);
        Assert.Equal("Authoritative Board Title", resolvedKeepBoard.Title);
        Assert.Contains("Authoritative Board Title", await File.ReadAllTextAsync(mdPath, Encoding.UTF8));

        // 5. app_data storage mode: zero intrusion into clean repo directory
        var cleanRepoDir = Path.Combine(_tempRoot, "clean-zero-intrusion-repo");
        Directory.CreateDirectory(cleanRepoDir);
        var cleanProject = await store.CreateProjectAsync("CleanRepo", cleanRepoDir);
        var appDataSettings = await coordinator.UpdateSettingsAsync(cleanProject.Id, new UpdateProjectBoardSettingsRequest
        {
            StorageMode = ProjectBoardStorageModes.AppData
        });
        _ = await coordinator.CreateTaskAsync(cleanProject.Id, new CreateProjectBoardTaskRequest { Title = "Zero repo intrusion task" });
        Assert.False(Directory.Exists(Path.Combine(cleanRepoDir, ".tinadec")), "app_data mode must not create .tinadec inside repo.");
        Assert.True(Directory.Exists(appDataSettings.ResolvedBoardDir));

        // 6. Real FileSystemWatcher 250ms debounce imports dropped .md file automatically (without auto-dispatch)
        using var sub = coordinator.SubscribeProjectEvents(project.Id, out var evtReader);
        var watchedNewMd = Path.Combine(repoDir, ".tinadec", "board", "tasks", "SYN-77.md");
        await File.WriteAllTextAsync(watchedNewMd, """
            ---
            assignee:
              kind: cli
              harness_id: codex
            key: SYN-77
            priority: high
            status: in_progress
            title: Live watcher imported task
            ---

            ## 目标描述

            Dropped on disk while watcher is active.
            """, Encoding.UTF8);

        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var observedWatcherUpsert = false;
        while (await evtReader.WaitToReadAsync(watchCts.Token))
        {
            while (evtReader.TryRead(out var evt))
            {
                if (evt.EventType == "task_upserted" && evt.Task?.TaskKey == "SYN-77")
                {
                    observedWatcherUpsert = true;
                    break;
                }
            }
            if (observedWatcherUpsert) break;
        }
        Assert.True(observedWatcherUpsert, "FileSystemWatcher should debounce and import SYN-77.md.");
        Assert.Equal(0, execution.DispatchCount);
    }

    [Fact]
    public async Task ExecutionService_AllocatesGitWorktreeAndLease_TagsBoardTaskSession_InspectsDiff_AndCompletesReviewLoop()
    {
        var store = _provider!.GetRequiredService<ProjectSessionStore>();
        var coordinator = _provider!.GetRequiredService<ProjectBoardCoordinator>();
        var execution = _provider!.GetRequiredService<ProjectTaskExecutionService>();
        var leases = _provider!.GetRequiredService<IResourceLeaseService>();

        var gitRepo = InitGitRepo(Path.Combine(_tempRoot, "exec-git-repo"));
        var project = await store.CreateProjectAsync("ExecRepo", gitRepo);
        _ = await coordinator.UpdateSettingsAsync(project.Id, new UpdateProjectBoardSettingsRequest
        {
            ProjectInstructions = "Follow strict TypeScript types."
        });

        var task = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Add feature module",
            DescriptionMd = "Create feature.ts.",
            Status = ProjectBoardTaskStatuses.Ready
        });

        // 1. Human dispatch marks in_progress with zero AI session/CLI run
        var humanDispatched = await execution.DispatchTaskAsync(project.Id, task.TaskId, new DispatchProjectBoardTaskRequest
        {
            AssigneeOverride = new ProjectBoardAssigneeDto { Kind = ProjectBoardAssigneeKinds.Human, DisplayName = "Alice" }
        });
        Assert.Equal(ProjectBoardTaskStatuses.InProgress, humanDispatched!.Status);
        Assert.Null(humanDispatched.ActiveSessionId);
        Assert.Equal(0, _cliExecutor.CallCount);

        // 2. CLI dispatch (Claude Code via HarnessCatalog) allocates isolated worktree + lease + origin="board_task" session
        _cliExecutor.OnExecute = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "feature.ts"), "export const feature = 1;\n");
            return new BoardCliHarnessExecutionResult(true, "Implemented feature.ts");
        };

        var cliDispatched = await execution.DispatchTaskAsync(project.Id, task.TaskId, new DispatchProjectBoardTaskRequest
        {
            AssigneeOverride = new ProjectBoardAssigneeDto
            {
                Kind = ProjectBoardAssigneeKinds.Cli,
                HarnessId = "claude-code"
            },
            UseWorktree = true
        });

        Assert.NotNull(cliDispatched);
        Assert.Equal(ProjectBoardTaskStatuses.InReview, cliDispatched!.Status);
        Assert.NotNull(cliDispatched.ActiveSessionId);
        Assert.NotNull(cliDispatched.WorktreePath);
        Assert.True(Directory.Exists(cliDispatched.WorktreePath));
        Assert.NotNull(cliDispatched.LeaseId);
        Assert.Equal("Implemented feature.ts", cliDispatched.LiveDigestSummary);
        Assert.NotNull(cliDispatched.DiffStats);
        Assert.Equal(1, cliDispatched.DiffStats!.Files);

        // Verify session has origin = "board_task" and board_task_id = task.TaskId
        var createdSession = await store.GetSessionAsync(cliDispatched.ActiveSessionId.Value);
        Assert.NotNull(createdSession);
        Assert.Equal("board_task", createdSession!.Origin);
        Assert.Equal(task.TaskId, createdSession.BoardTaskId);

        // Verify prompt inherited project_instructions
        Assert.Contains("Follow strict TypeScript types.", _cliExecutor.LastPrompt);

        // 3. Inspect Git Diff inside worktree
        var diff = await execution.GetTaskDiffAsync(project.Id, task.TaskId);
        Assert.NotNull(diff);
        Assert.Contains("feature.ts", diff!.DiffText);
        Assert.Contains(diff.Files, f => f.Path == "feature.ts" && f.Added >= 1);

        // 4. Request Changes reuses exact same session & worktree
        _cliExecutor.OnExecute = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "feature.ts"), "export const feature = 2;\nexport const tested = true;\n");
            return new BoardCliHarnessExecutionResult(true, "Addressed review feedback");
        };

        var afterFeedback = await execution.RequestTaskChangesAsync(
            project.Id,
            task.TaskId,
            new RequestTaskChangesRequest { Feedback = "Export tested flag as well." });
        Assert.NotNull(afterFeedback);
        Assert.Equal(ProjectBoardTaskStatuses.InReview, afterFeedback!.Status);
        Assert.Equal(cliDispatched.ActiveSessionId, afterFeedback.ActiveSessionId);
        Assert.Equal(cliDispatched.WorktreePath, afterFeedback.WorktreePath);
        Assert.Contains("Export tested flag as well.", _cliExecutor.LastPrompt);

        // 5. Approve with merge_or_clean_worktree = true integrates into main repo, cleans worktree & releases lease
        var approved = await execution.ApproveTaskAsync(project.Id, task.TaskId, new ApproveProjectBoardTaskRequest
        {
            MergeOrCleanWorktree = true,
            Note = "LGTM!"
        });
        Assert.NotNull(approved);
        Assert.Equal(ProjectBoardTaskStatuses.Done, approved!.Status);
        Assert.Null(approved.LeaseId);
        Assert.Null(approved.WorktreePath);
        Assert.True(File.Exists(Path.Combine(gitRepo, "feature.ts")), "Integrated file must exist in target repo after approval.");
        Assert.Empty(await leases.ListActiveAsync(createdSession.Id));
    }

    [Fact]
    public async Task SpatialBridge_EnforcesZeroPayloadDuplicationOnBoardNodes_AndPromotesAllThreeModesWithConnectorsAndLwwLinks()
    {
        var store = _provider!.GetRequiredService<ProjectSessionStore>();
        var boardCoordinator = _provider!.GetRequiredService<ProjectBoardCoordinator>();
        var spatialCoordinator = _provider!.GetRequiredService<SpatialBoardCoordinator>();
        var execution = _provider!.GetRequiredService<ProjectTaskExecutionService>();

        var repoDir = Path.Combine(_tempRoot, "spatial-bridge-repo");
        Directory.CreateDirectory(repoDir);
        var project = await store.CreateProjectAsync("BridgeApp", repoDir);
        var sessionId = Guid.NewGuid();

        // 1. SpatialBoardCoordinator accepts kind = "board" with view metadata and throws SpatialBoardValidationException("board_props_invalid") on task payloads
        var boardNodeAck = await spatialCoordinator.ApplyOpsAsync(sessionId, new SpatialBoardOpsRequestDto
        {
            ClientId = "spatial-test",
            Ops =
            [
                new SpatialPropertyOpDto
                {
                    ClientOpId = "create-board-view",
                    ObjectId = "board-view-1",
                    Kind = "board",
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["project_id"] = JsonSerializer.SerializeToElement(project.Id.ToString("D")),
                        ["layout"] = JsonSerializer.SerializeToElement("swimlane"),
                        ["swimlane_by"] = JsonSerializer.SerializeToElement("assignee")
                    }
                }
            ]
        });
        Assert.Contains("create-board-view", boardNodeAck.AcceptedOpIds);

        var dupEx = await Assert.ThrowsAsync<SpatialBoardValidationException>(() =>
            spatialCoordinator.ApplyOpsAsync(sessionId, new SpatialBoardOpsRequestDto
            {
                ClientId = "spatial-test",
                Ops =
                [
                    new SpatialPropertyOpDto
                    {
                        ClientOpId = "dup-tasks",
                        ObjectId = "board-view-1",
                        Props = new Dictionary<string, JsonElement>
                        {
                            ["tasks"] = JsonSerializer.SerializeToElement(new[] { new { id = 1 } })
                        }
                    }
                ]
            }));
        Assert.Equal("board_props_invalid", dupEx.Code);

        // 2. Seed spatial stickies (with high existing Lamport = 250 to prove maxLamport + 1 wins LWW!), sections, and connectors
        var seedAck = await spatialCoordinator.ApplyOpsAsync(sessionId, new SpatialBoardOpsRequestDto
        {
            ClientId = "designer-1",
            Ops =
            [
                new SpatialPropertyOpDto
                {
                    ClientOpId = "m1",
                    ObjectId = "merge-sticky-1",
                    Kind = "sticky",
                    X = 10,
                    Y = 10,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("Unify authentication flow\n- [ ] Add OAuth callback\n- [x] Document token TTL")
                    },
                    Timestamps = new Dictionary<string, SpatialLwwTimestampDto>
                    {
                        ["props.text"] = new(250, "zzzz-high-writer"),
                        ["props.board_task_key"] = new(250, "zzzz-high-writer")
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "m2",
                    ObjectId = "merge-sticky-2",
                    Kind = "sticky",
                    X = 260,
                    Y = 10,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("Add refresh token rotation")
                    },
                    Timestamps = new Dictionary<string, SpatialLwwTimestampDto>
                    {
                        ["props.text"] = new(120, "designer-1")
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "sec-split",
                    ObjectId = "sec-architecture",
                    Kind = "section",
                    X = 100,
                    Y = 300,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["title"] = JsonSerializer.SerializeToElement("Backend Pipeline")
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "c1",
                    ObjectId = "child-sticky-1",
                    Kind = "sticky",
                    ParentId = "sec-architecture",
                    HasParentId = true,
                    X = 120,
                    Y = 340,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("Create SQLite migration\nInclude indexes on project_id.")
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "c2",
                    ObjectId = "child-sticky-2",
                    Kind = "text",
                    ParentId = "sec-architecture",
                    HasParentId = true,
                    X = 360,
                    Y = 340,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("Expose Fastify proxy route")
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "c3",
                    ObjectId = "child-sticky-3",
                    Kind = "sticky",
                    ParentId = "sec-architecture",
                    HasParentId = true,
                    X = 600,
                    Y = 340,
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("Mount Vue Kanban widget")
                    }
                },
                // Connectors: child-sticky-1 -> child-sticky-2 -> child-sticky-3 (plus a cyclic back-arrow child-sticky-3 -> child-sticky-1 that must be ignored cleanly!)
                new SpatialPropertyOpDto
                {
                    ClientOpId = "conn-1-2",
                    ObjectId = "conn-1-2",
                    Kind = "connector",
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["sourceId"] = JsonSerializer.SerializeToElement("child-sticky-1"),
                        ["targetId"] = JsonSerializer.SerializeToElement("child-sticky-2")
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "conn-2-3",
                    ObjectId = "conn-2-3",
                    Kind = "connector",
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["sourceId"] = JsonSerializer.SerializeToElement("child-sticky-2"),
                        ["targetId"] = JsonSerializer.SerializeToElement("child-sticky-3")
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "conn-cycle-back",
                    ObjectId = "conn-cycle-back",
                    Kind = "connector",
                    Props = new Dictionary<string, JsonElement>
                    {
                        ["sourceId"] = JsonSerializer.SerializeToElement("child-sticky-3"),
                        ["targetId"] = JsonSerializer.SerializeToElement("child-sticky-1")
                    }
                }
            ]
        });
        Assert.Empty(seedAck.RejectedOps);

        var viewportHint = JsonSerializer.SerializeToElement(new { x = 120.0, y = 240.0, zoom = 0.85 });

        // 3. Mode 1: merge_single -> 1 task, links BOTH stickies to BRI-1, LWW Lamport 251 beats 250!
        var merged = await boardCoordinator.PromoteFromSpatialAsync(project.Id, new PromoteFromSpatialRequest
        {
            SessionId = sessionId,
            ObjectIds = ["merge-sticky-1", "merge-sticky-2"],
            Mode = SpatialPromotionModes.MergeSingle,
            TargetStatus = ProjectBoardTaskStatuses.Backlog,
            Priority = ProjectBoardPriorities.High,
            Labels = ["auth", "spatial"],
            ViewportHint = viewportHint
        });
        Assert.Equal(SpatialPromotionModes.MergeSingle, merged.Mode);
        Assert.Null(merged.ParentTaskId);
        var mergedTask = Assert.Single(merged.Tasks);
        Assert.Equal("BRI-1", mergedTask.TaskKey);
        Assert.Equal("Unify authentication flow", mergedTask.Title);
        Assert.Contains("Add refresh token rotation", mergedTask.DescriptionMd);
        Assert.Equal(3, mergedTask.Checklist.Count);
        Assert.NotNull(mergedTask.SpatialRef);
        Assert.Equal(sessionId, mergedTask.SpatialRef!.SessionId);
        Assert.Equal("merge-sticky-1", mergedTask.SpatialRef.ObjectId);
        Assert.Equal(2, merged.UpdatedObjectLinks.Count);
        Assert.All(merged.UpdatedObjectLinks, l => Assert.Equal(mergedTask.TaskId, l.TaskId));

        var snapAfterMerge = await spatialCoordinator.GetSnapshotAsync(sessionId);
        Assert.Equal(mergedTask.TaskId.ToString("D"), snapAfterMerge.Objects["merge-sticky-1"].Props["board_task_id"].GetString());
        Assert.Equal("BRI-1", snapAfterMerge.Objects["merge-sticky-1"].Props["board_task_key"].GetString());
        Assert.Equal(251, snapAfterMerge.Objects["merge-sticky-1"].PropTimestamps["props.board_task_key"].Lamport);
        Assert.Equal("BRI-1", snapAfterMerge.Objects["merge-sticky-2"].Props["board_task_key"].GetString());

        // 4. Mode 2: split_many on section "sec-architecture" -> expands 3 children as independent tasks + acyclic connector depends_on!
        var splitResult = await boardCoordinator.PromoteFromSpatialAsync(project.Id, new PromoteFromSpatialRequest
        {
            SessionId = sessionId,
            ObjectIds = ["sec-architecture"],
            Mode = SpatialPromotionModes.SplitMany,
            TargetStatus = ProjectBoardTaskStatuses.Ready
        });
        Assert.Equal(SpatialPromotionModes.SplitMany, splitResult.Mode);
        Assert.Null(splitResult.ParentTaskId);
        Assert.Equal(3, splitResult.Tasks.Count);
        var splitT1 = splitResult.Tasks[0];
        var splitT2 = splitResult.Tasks[1];
        var splitT3 = splitResult.Tasks[2];
        Assert.Equal("Create SQLite migration", splitT1.Title);
        Assert.Equal("Include indexes on project_id.", splitT1.DescriptionMd);
        Assert.Empty(splitT1.DependsOn); // cyclic back-arrow ignored!
        Assert.Equal([splitT1.TaskId], splitT2.DependsOn);
        Assert.Equal([splitT2.TaskId], splitT3.DependsOn);

        // 5. Mode 3: parent_with_children on section "sec-architecture" -> 1 parent task + 3 child tasks with parent_task_id & connector depends_on!
        var hierarchyResult = await boardCoordinator.PromoteFromSpatialAsync(project.Id, new PromoteFromSpatialRequest
        {
            SessionId = sessionId,
            ObjectIds = ["sec-architecture"],
            Mode = SpatialPromotionModes.ParentWithChildren,
            TargetStatus = ProjectBoardTaskStatuses.Ready
        });
        Assert.Equal(SpatialPromotionModes.ParentWithChildren, hierarchyResult.Mode);
        Assert.NotNull(hierarchyResult.ParentTaskId);
        Assert.Equal(4, hierarchyResult.Tasks.Count);

        var parentTask = hierarchyResult.Tasks[0];
        Assert.Equal(hierarchyResult.ParentTaskId, parentTask.TaskId);
        Assert.Equal("Backend Pipeline", parentTask.Title);
        Assert.Equal("sec-architecture", parentTask.SpatialRef!.ObjectId);

        var childTasks = hierarchyResult.Tasks.Skip(1).ToList();
        Assert.All(childTasks, c => Assert.Equal(parentTask.TaskId, c.ParentTaskId));
        Assert.Equal([childTasks[0].TaskId], childTasks[1].DependsOn);
        Assert.Equal([childTasks[1].TaskId], childTasks[2].DependsOn);

        var snapAfterHierarchy = await spatialCoordinator.GetSnapshotAsync(sessionId);
        Assert.Equal(parentTask.TaskKey, snapAfterHierarchy.Objects["sec-architecture"].Props["board_task_key"].GetString());
        Assert.Equal(childTasks[0].TaskKey, snapAfterHierarchy.Objects["child-sticky-1"].Props["board_task_key"].GetString());

        // Markdown files synced cleanly to .tinadec/board/tasks/ with spatial_ref and ZERO auto-started runs
        var parentMdPath = Path.Combine(repoDir, ".tinadec", "board", "tasks", $"{parentTask.TaskKey}.md");
        Assert.True(File.Exists(parentMdPath));
        Assert.Contains("spatial_ref:", await File.ReadAllTextAsync(parentMdPath, Encoding.UTF8));
        Assert.Equal(0, execution.DispatchCount);
        Assert.Equal(0, _cliExecutor.CallCount);
    }

    [Fact]
    public async Task ProjectNotes_SupportsGetPut_OptimisticRevision_ExternalWatcherSync_AntiEcho_AppendProgress_AndContextInjection()
    {
        var store = _provider!.GetRequiredService<ProjectSessionStore>();
        var coordinator = _provider!.GetRequiredService<ProjectBoardCoordinator>();
        var syncService = _provider!.GetRequiredService<ProjectBoardFileSyncService>();
        var execution = _provider!.GetRequiredService<ProjectTaskExecutionService>();
        var contextProvider = _provider!.GetRequiredService<IContextProvider>();

        var repoDir = Path.Combine(_tempRoot, "notes-sync-repo");
        Directory.CreateDirectory(repoDir);
        var project = await store.CreateProjectAsync("NotesApp", repoDir);
        _ = await coordinator.UpdateSettingsAsync(project.Id, new UpdateProjectBoardSettingsRequest
        {
            ProjectInstructions = "Never create .agent directories; keep tests deterministic."
        });

        using var sub = coordinator.SubscribeProjectEvents(project.Id, out var evtReader);

        // 1. Bootstrap GET /notes creates <repo>/.tinadec/project/notes.md cleanly
        var initialNotes = await syncService.GetProjectNotesAsync(project.Id);
        Assert.Equal(project.Id, initialNotes.ProjectId);
        Assert.Equal(ProjectBoardStorageModes.RepoTinadec, initialNotes.StorageMode);
        Assert.Equal(1L, initialNotes.Revision);
        Assert.True(File.Exists(initialNotes.ResolvedFilePath));
        Assert.Contains("## 项目目标", initialNotes.ContentMd);
        Assert.Contains("## 决策与进展记录", initialNotes.ContentMd);

        // 2. PUT /notes with expected_revision = 1 writes atomically, updates SHA-256 hash & revision = 2, and emits notes_updated SSE
        var putNotes = await syncService.UpdateProjectNotesAsync(project.Id, new UpdateProjectNotesRequest
        {
            ContentMd = "# NotesApp — Project Notes\n\n## 项目目标\nShip Phase 3 Autopilot.\n\n## 架构约定\nMonochrome UI + SQLite LWW.\n",
            ExpectedRevision = 1L
        });
        Assert.Equal(2L, putNotes.Revision);
        Assert.NotEqual(initialNotes.LastSyncedHash, putNotes.LastSyncedHash);
        Assert.Contains("Ship Phase 3 Autopilot.", await File.ReadAllTextAsync(putNotes.ResolvedFilePath, Encoding.UTF8));

        // Drain SSE and verify notes_updated arrived
        var sawPutEvent = false;
        while (evtReader.TryRead(out var e))
        {
            if (e.EventType == "notes_updated" && e.Notes?.Revision == 2L)
                sawPutEvent = true;
        }
        Assert.True(sawPutEvent);

        // 3. Stale expected_revision throws ProjectBoardRevisionConflictException (revision_conflict)
        var conflictEx = await Assert.ThrowsAsync<ProjectBoardRevisionConflictException>(() =>
            syncService.UpdateProjectNotesAsync(project.Id, new UpdateProjectNotesRequest
            {
                ContentMd = "Stale overwrite",
                ExpectedRevision = 1L
            }));
        Assert.Equal("revision_conflict", conflictEx.Code);
        Assert.Equal(2L, conflictEx.CurrentRevision);

        // 4. SHA-256 Anti-Echo: SyncProjectAsync without external file changes does NOT bump notes revision or emit echo event
        _ = await syncService.SyncProjectAsync(project.Id);
        var afterEchoCheck = await syncService.GetProjectNotesAsync(project.Id);
        Assert.Equal(2L, afterEchoCheck.Revision);
        while (evtReader.TryRead(out var echoEvt))
        {
            Assert.NotEqual("notes_updated", echoEvt.EventType);
        }

        // 5. External disk edit on notes.md is picked up by 250ms debounced FileSystemWatcher -> bumps revision = 3 & emits notes_updated SSE!
        var externallyEditedContent = putNotes.ContentMd + "\n## 外部补充\nEdited directly in VS Code.\n";
        await File.WriteAllTextAsync(putNotes.ResolvedFilePath, externallyEditedContent, Encoding.UTF8);

        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ProjectNotesDto? watchedNotes = null;
        while (await evtReader.WaitToReadAsync(watchCts.Token))
        {
            while (evtReader.TryRead(out var evt))
            {
                if (evt.EventType == "notes_updated" && evt.Notes is not null && evt.Notes.ContentMd.Contains("Edited directly in VS Code."))
                {
                    watchedNotes = evt.Notes;
                    break;
                }
            }
            if (watchedNotes is not null) break;
        }
        Assert.NotNull(watchedNotes);
        Assert.Equal(3L, watchedNotes!.Revision);

        // 6. Task completion & approval automatically append entries under "## 决策与进展记录" AND inject notes into prompt & ContextPack!
        var session = await store.CreateSessionAsync(project.Id, "Architecture Chat", Guid.NewGuid());
        await store.AddMessageAsync(session.Id, "What are our project goals?", "user", null);
        var pack = await contextProvider.BuildContextAsync(new ContextBuildRequest(session.Id.ToString("D"), RunId: null));
        var livingNotesEvidence = Assert.Single(pack.Evidence, ev => ev.Source == "project_living_notes");
        Assert.Contains("Never create .agent directories", livingNotesEvidence.Content);
        Assert.Contains("Ship Phase 3 Autopilot.", livingNotesEvidence.Content);

        var task = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Verify notes prompt & append",
            Status = ProjectBoardTaskStatuses.Ready
        });
        _cliExecutor.OnExecute = _ => new BoardCliHarnessExecutionResult(true, "Verified living document flow");
        var dispatched = await execution.DispatchTaskAsync(project.Id, task.TaskId, new DispatchProjectBoardTaskRequest
        {
            AssigneeOverride = new ProjectBoardAssigneeDto { Kind = ProjectBoardAssigneeKinds.Cli, HarnessId = "claude-code" },
            UseWorktree = false
        });
        Assert.Equal(ProjectBoardTaskStatuses.InReview, dispatched!.Status);
        Assert.Contains("Ship Phase 3 Autopilot.", _cliExecutor.LastPrompt);

        var approved = await execution.ApproveTaskAsync(project.Id, task.TaskId, new ApproveProjectBoardTaskRequest
        {
            Note = "Clean delivery"
        });
        Assert.Equal(ProjectBoardTaskStatuses.Done, approved!.Status);

        var finalNotes = await syncService.GetProjectNotesAsync(project.Id);
        Assert.Contains("## 决策与进展记录", finalNotes.ContentMd);
        Assert.Contains(task.TaskKey, finalNotes.ContentMd);
        Assert.Contains("Verified living document flow", finalNotes.ContentMd);
        Assert.Contains("Approved & Done", finalNotes.ContentMd);
    }

    [Fact]
    public async Task CoordinatorPlanner_DecomposesGoalsDeterministically_MaterializesTopologicalDag_AndRejectsCyclicDrafts()
    {
        var store = _provider!.GetRequiredService<ProjectSessionStore>();
        var coordinator = _provider!.GetRequiredService<ProjectBoardCoordinator>();
        var planner = _provider!.GetRequiredService<IProjectCoordinatorPlanner>();

        var repoDir = Path.Combine(_tempRoot, "planner-repo");
        Directory.CreateDirectory(repoDir);
        var project = await store.CreateProjectAsync("PlanProj", repoDir);

        var prereq = await coordinator.CreateTaskAsync(project.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Seed existing auth token task",
            Status = ProjectBoardTaskStatuses.Done
        });

        // 1. Deterministic heuristic decomposition with existing task key reference & max_tasks clamping
        var plan = await planner.PlanAsync(project.Id, new CoordinatorPlanRequest
        {
            Goal = $"""
                ## Phase 1: Database & DTOs
                - Add SQLite migration after {prereq.TaskKey}
                - Expose typed DTO contracts

                ## Phase 2: Fastify Proxy & OpenAPI
                - Proxy routes and update schema snapshot

                ## Phase 3: End-to-End Verification
                - Run xUnit and Vitest suites
                """,
            DefaultStatus = ProjectBoardTaskStatuses.Ready,
            DefaultAssignee = new ProjectBoardAssigneeDto { Kind = ProjectBoardAssigneeKinds.Cli, HarnessId = "claude-code" },
            AutoApply = false,
            MaxTasks = 5
        });

        Assert.Equal(CoordinatorPlannerSources.Heuristic, plan.PlannerSource);
        Assert.False(plan.AutoApplied);
        Assert.Empty(plan.CreatedTasks);
        Assert.Equal(3, plan.Drafts.Count);
        Assert.Contains(prereq.TaskId, plan.Drafts[0].DependsOnTaskIds);
        Assert.Contains("draft-1", plan.Drafts[1].DependsOnDraftIds);
        Assert.Contains("draft-2", plan.Drafts[2].DependsOnDraftIds);

        // 2. Cyclic drafts on apply-plan throw ProjectBoardCycleException ("cycle_detected") without creating partial tasks!
        var cyclicEx = await Assert.ThrowsAsync<ProjectBoardCycleException>(() =>
            planner.ApplyPlanAsync(project.Id, new CoordinatorApplyPlanRequest
            {
                Goal = "Cyclic test",
                Drafts =
                [
                    new CoordinatorTaskDraftDto { DraftId = "d-a", Title = "Task A", DependsOnDraftIds = ["d-b"] },
                    new CoordinatorTaskDraftDto { DraftId = "d-b", Title = "Task B", DependsOnDraftIds = ["d-a"] }
                ]
            }));
        Assert.Equal("cycle_detected", cyclicEx.Code);
        Assert.Single(await coordinator.ListProjectTasksAsync(project.Id)); // Only prereq exists!

        // 3. Out-of-order topological drafts ("d-ui" listed BEFORE "d-api" which it depends on!) materialize in topological order
        var applied = await planner.ApplyPlanAsync(project.Id, new CoordinatorApplyPlanRequest
        {
            Goal = "Ship modular dashboard",
            UpdateNotes = true,
            Drafts =
            [
                new CoordinatorTaskDraftDto
                {
                    DraftId = "d-ui",
                    Title = "Build Vue dashboard panel",
                    DependsOnDraftIds = ["d-api"]
                },
                new CoordinatorTaskDraftDto
                {
                    DraftId = "d-api",
                    Title = "Build backend metrics endpoint",
                    DependsOnTaskIds = [prereq.TaskId]
                }
            ]
        });

        Assert.Equal(2, applied.CreatedTasks.Count);
        var createdApi = applied.CreatedTasks[0];
        var createdUi = applied.CreatedTasks[1];
        Assert.Equal("Build backend metrics endpoint", createdApi.Title);
        Assert.Equal("Build Vue dashboard panel", createdUi.Title);
        Assert.Equal([prereq.TaskId], createdApi.DependsOn);
        Assert.Equal([createdApi.TaskId], createdUi.DependsOn);

        var notesAfterPlan = await coordinator.GetProjectNotesAsync(project.Id);
        Assert.Contains("[Coordinator Plan]", notesAfterPlan.ContentMd);
        Assert.Contains(createdApi.TaskKey, notesAfterPlan.ContentMd);
    }

    [Fact]
    public async Task Autopilot_EnforcesMaxConcurrency_WaitsForDoneDependencies_RespectsResourceLeases_IsolatesBlockedFailures_AndPausesInstantly()
    {
        var store = _provider!.GetRequiredService<ProjectSessionStore>();
        var coordinator = _provider!.GetRequiredService<ProjectBoardCoordinator>();
        var execution = _provider!.GetRequiredService<ProjectTaskExecutionService>();
        var autopilot = _provider!.GetRequiredService<ProjectAutopilotService>();
        var leases = _provider!.GetRequiredService<IResourceLeaseService>();

        // Part A: Strict max_concurrency = 2 with 4 ready tasks + human exclusion + unmet depends_on gating + instant pause
        var repoA = Path.Combine(_tempRoot, "autopilot-concurrency-repo");
        Directory.CreateDirectory(repoA);
        var projA = await store.CreateProjectAsync("AutoConc", repoA);
        _ = await coordinator.UpdateSettingsAsync(projA.Id, new UpdateProjectBoardSettingsRequest
        {
            DefaultAssigneeKind = ProjectBoardAssigneeKinds.Cli,
            MaxConcurrency = 2
        });

        // Create 4 independent ready tasks + 1 human-assigned ready task + 1 dependent ready task (depends on r1 == done)
        var r1 = await coordinator.CreateTaskAsync(projA.Id, new CreateProjectBoardTaskRequest { Title = "Ready 1", Status = ProjectBoardTaskStatuses.Ready, Priority = ProjectBoardPriorities.Urgent });
        var r2 = await coordinator.CreateTaskAsync(projA.Id, new CreateProjectBoardTaskRequest { Title = "Ready 2", Status = ProjectBoardTaskStatuses.Ready, Priority = ProjectBoardPriorities.High });
        var r3 = await coordinator.CreateTaskAsync(projA.Id, new CreateProjectBoardTaskRequest { Title = "Ready 3", Status = ProjectBoardTaskStatuses.Ready, Priority = ProjectBoardPriorities.Medium });
        var r4 = await coordinator.CreateTaskAsync(projA.Id, new CreateProjectBoardTaskRequest { Title = "Ready 4", Status = ProjectBoardTaskStatuses.Ready, Priority = ProjectBoardPriorities.Low });
        var humanTask = await coordinator.CreateTaskAsync(projA.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Manual Human Task",
            Status = ProjectBoardTaskStatuses.Ready,
            Assignee = new ProjectBoardAssigneeDto { Kind = ProjectBoardAssigneeKinds.Human, DisplayName = "Bob" }
        });
        var depOnR1 = await coordinator.CreateTaskAsync(projA.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Downstream After R1 Done",
            Status = ProjectBoardTaskStatuses.Ready,
            Priority = ProjectBoardPriorities.Urgent,
            DependsOn = [r1.TaskId]
        });

        var runningCount = 0;
        var maxObservedConcurrent = 0;
        var startedTasks = new ConcurrentDictionary<Guid, TaskCompletionSource<BoardCliHarnessExecutionResult>>();
        var twoStartedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _cliExecutor.OnExecuteAsync = async (req, ct) =>
        {
            var nowRunning = Interlocked.Increment(ref runningCount);
            while (true)
            {
                var prevMax = Volatile.Read(ref maxObservedConcurrent);
                if (nowRunning <= prevMax || Interlocked.CompareExchange(ref maxObservedConcurrent, nowRunning, prevMax) == prevMax)
                    break;
            }

            var gateTcs = startedTasks.GetOrAdd(req.TaskId, static _ => new TaskCompletionSource<BoardCliHarnessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (startedTasks.Count >= 2)
                twoStartedSignal.TrySetResult();

            try
            {
                using var reg = ct.Register(() => gateTcs.TrySetCanceled(ct));
                return await gateTcs.Task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref runningCount);
            }
        };

        var stateEnabled = await autopilot.UpdateAutopilotAsync(projA.Id, new UpdateProjectAutopilotRequest
        {
            AutopilotEnabled = true,
            MaxConcurrency = 2
        });

        await twoStartedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Strictly 2 tasks claimed (r1 & r2); r3, r4, depOnR1, and humanTask were NOT started!
        Assert.True(stateEnabled.AutopilotEnabled);
        Assert.Equal(2, startedTasks.Count);
        Assert.Contains(r1.TaskId, startedTasks.Keys);
        Assert.Contains(r2.TaskId, startedTasks.Keys);
        Assert.DoesNotContain(depOnR1.TaskId, startedTasks.Keys);
        Assert.DoesNotContain(humanTask.TaskId, startedTasks.Keys);
        Assert.True(maxObservedConcurrent <= 2, $"Expected max concurrency <= 2, observed {maxObservedConcurrent}.");

        var liveState = await autopilot.GetAutopilotStateAsync(projA.Id);
        Assert.Equal(2, liveState.ActiveTaskCount);
        Assert.Equal(3, liveState.QueuedReadyCount); // r3, r4, depOnR1 (humanTask excluded)

        // Instant Pause BEFORE completing r1/r2 -> stops claiming r3 or r4 when r1/r2 finish!
        var pausedState = await autopilot.UpdateAutopilotAsync(projA.Id, new UpdateProjectAutopilotRequest
        {
            AutopilotEnabled = false
        });
        Assert.False(pausedState.AutopilotEnabled);

        startedTasks[r1.TaskId].TrySetResult(new BoardCliHarnessExecutionResult(true, "R1 finished"));
        startedTasks[r2.TaskId].TrySetResult(new BoardCliHarnessExecutionResult(true, "R2 finished"));

        await WaitUntilAsync(async () =>
        {
            var t1 = await coordinator.GetTaskAsync(projA.Id, r1.TaskId);
            var t2 = await coordinator.GetTaskAsync(projA.Id, r2.TaskId);
            return t1?.Status == ProjectBoardTaskStatuses.InReview && t2?.Status == ProjectBoardTaskStatuses.InReview;
        }, TimeSpan.FromSeconds(5));

        // Because Autopilot was paused instantly, r3 & r4 MUST still have zero starts!
        Assert.Equal(2, startedTasks.Count);

        // Now switch executor to immediate completion, re-enable Autopilot with max_concurrency = 2:
        // Observe that depOnR1 STILL does NOT run yet because r1 is in_review (NOT done), while r3 & r4 run!
        _cliExecutor.OnExecuteAsync = null;
        _cliExecutor.OnExecute = req => new BoardCliHarnessExecutionResult(true, $"Done {req.TaskId}");

        _ = await autopilot.UpdateAutopilotAsync(projA.Id, new UpdateProjectAutopilotRequest
        {
            AutopilotEnabled = true,
            MaxConcurrency = 2
        });

        await WaitUntilAsync(async () =>
        {
            var t3 = await coordinator.GetTaskAsync(projA.Id, r3.TaskId);
            var t4 = await coordinator.GetTaskAsync(projA.Id, r4.TaskId);
            return t3?.Status == ProjectBoardTaskStatuses.InReview && t4?.Status == ProjectBoardTaskStatuses.InReview;
        }, TimeSpan.FromSeconds(5));

        // depOnR1 MUST STILL BE READY because r1 is in_review, NOT done!
        var depBeforeApprove = await coordinator.GetTaskAsync(projA.Id, depOnR1.TaskId);
        Assert.Equal(ProjectBoardTaskStatuses.Ready, depBeforeApprove!.Status);

        // Approving r1 -> transitions r1 to `done` -> automatically wakes Autopilot and dispatches depOnR1!
        _ = await execution.ApproveTaskAsync(projA.Id, r1.TaskId, new ApproveProjectBoardTaskRequest { Note = "Ship R1" });

        await WaitUntilAsync(async () =>
        {
            var depAfter = await coordinator.GetTaskAsync(projA.Id, depOnR1.TaskId);
            return depAfter?.Status == ProjectBoardTaskStatuses.InReview;
        }, TimeSpan.FromSeconds(5));

        // Human task remained untouched in Ready the entire time!
        Assert.Equal(ProjectBoardTaskStatuses.Ready, (await coordinator.GetTaskAsync(projA.Id, humanTask.TaskId))!.Status);
        _ = await autopilot.UpdateAutopilotAsync(projA.Id, new UpdateProjectAutopilotRequest { AutopilotEnabled = false });

        // Part B: ResourceLease contention holds candidate in Ready without failing until lease releases,
        //         AND execution failure sets blocked = true + blocked_reason and halts downstream dependents!
        var repoB = Path.Combine(_tempRoot, "autopilot-lease-and-block-repo");
        Directory.CreateDirectory(repoB);
        var projB = await store.CreateProjectAsync("AutoBlock", repoB);
        _ = await coordinator.UpdateSettingsAsync(projB.Id, new UpdateProjectBoardSettingsRequest
        {
            DefaultAssigneeKind = ProjectBoardAssigneeKinds.Cli,
            MaxConcurrency = 2
        });

        // Acquire an exclusive host ResourceLease on repoB BEFORE enabling Autopilot
        var externalLeaseDecision = await leases.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Path, repoB, Exclusive: true),
            SessionId: null,
            RunId: null,
            TaskId: Guid.NewGuid(),
            Reason: "External exclusive refactoring lock",
            Purpose: ResourceLeasePurposes.WriteScope));
        Assert.True(externalLeaseDecision.Granted);

        var failingPrereq = await coordinator.CreateTaskAsync(projB.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Migrate auth provider",
            Status = ProjectBoardTaskStatuses.Ready
        });
        var downstreamHeld = await coordinator.CreateTaskAsync(projB.Id, new CreateProjectBoardTaskRequest
        {
            Title = "Deploy auth service",
            Status = ProjectBoardTaskStatuses.Ready,
            DependsOn = [failingPrereq.TaskId]
        });

        _cliExecutor.OnExecute = _ => new BoardCliHarnessExecutionResult(false, string.Empty, "OAuth client_id secret decision needed from human.");

        var stateWhileLeased = await autopilot.UpdateAutopilotAsync(projB.Id, new UpdateProjectAutopilotRequest
        {
            AutopilotEnabled = true,
            MaxConcurrency = 2
        });

        // Because repoB has an exclusive ResourceLease conflict, failingPrereq stays cleanly in Ready (NOT failed/blocked)!
        var prereqDuringContention = await coordinator.GetTaskAsync(projB.Id, failingPrereq.TaskId);
        Assert.Equal(ProjectBoardTaskStatuses.Ready, prereqDuringContention!.Status);
        Assert.False(prereqDuringContention.Blocked);
        Assert.Equal(0, stateWhileLeased.BlockedTaskCount);

        // Release external lease -> Autopilot automatically wakes, dispatches failingPrereq -> fails -> sets blocked = true & halts downstreamHeld!
        await leases.ReleaseAsync(externalLeaseDecision.Lease!.Id);

        await WaitUntilAsync(async () =>
        {
            var st = await autopilot.GetAutopilotStateAsync(projB.Id);
            return st.BlockedTaskCount == 1;
        }, TimeSpan.FromSeconds(5));

        var blockedState = await autopilot.GetAutopilotStateAsync(projB.Id);
        Assert.Equal(1, blockedState.BlockedTaskCount);
        Assert.Equal(0, blockedState.ActiveTaskCount);
        var blockedTask = Assert.Single(blockedState.BlockedTasks);
        Assert.Equal(failingPrereq.TaskId, blockedTask.TaskId);
        Assert.True(blockedTask.Blocked);
        Assert.Contains("OAuth client_id secret decision needed", blockedTask.BlockedReason);

        // Downstream dependent task MUST remain safely held in Ready (never dispatched)!
        var heldDownstream = await coordinator.GetTaskAsync(projB.Id, downstreamHeld.TaskId);
        Assert.Equal(ProjectBoardTaskStatuses.Ready, heldDownstream!.Status);
        Assert.Null(heldDownstream.ActiveSessionId);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await predicate().ConfigureAwait(false))
                return;
            await Task.Delay(25).ConfigureAwait(false);
        }
        Assert.True(await predicate().ConfigureAwait(false), $"Condition was not satisfied within {timeout.TotalSeconds:0.#}s.");
    }

    private static string InitGitRepo(string path)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "config", "user.email", "test@tinadec.local");
        RunGit(path, "config", "user.name", "Tinadec Test");
        File.WriteAllText(Path.Combine(path, "README.md"), "# Test Repo\n");
        RunGit(path, "add", "README.md");
        RunGit(path, "commit", "-m", "initial");
        return path;
    }

    private static void RunGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(10_000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
    }

    private sealed class RecordingCliHarnessExecutor : IBoardCliHarnessExecutor
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public string LastPrompt { get; private set; } = string.Empty;
        public Func<BoardCliHarnessExecutionRequest, BoardCliHarnessExecutionResult>? OnExecute { get; set; }
        public Func<BoardCliHarnessExecutionRequest, CancellationToken, Task<BoardCliHarnessExecutionResult>>? OnExecuteAsync { get; set; }

        public Task<BoardCliHarnessExecutionResult> ExecuteAsync(
            BoardCliHarnessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastPrompt = request.Prompt;
            if (OnExecuteAsync is { } asyncHandler)
                return asyncHandler(request, cancellationToken);
            var res = OnExecute?.Invoke(request) ?? new BoardCliHarnessExecutionResult(true, "OK");
            return Task.FromResult(res);
        }
    }
}
