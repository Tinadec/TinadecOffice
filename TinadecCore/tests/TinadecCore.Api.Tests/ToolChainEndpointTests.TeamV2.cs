using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Task #33 Multi-Agent v2 E2E &amp; structural invariants:
/// 1) <c>FullDuplexRunEngine.Lanes.cs</c> is deleted while <c>lane_key</c> checkpoint/DTO contract fields remain intact.
/// 2) Shipped v3.7.0 <c>free_director</c> (<c>Team</c>) executes as an Explore-First coordinator without invoking
///    <c>PlanningAgent</c>, exercises <c>team_plan</c>, <c>team_spawn</c>, <c>team_message</c>, <c>team_wait</c>, and
///    <c>team_integrate</c>, auto-enrolls coordinator &amp; teammates in <see cref="ISessionOrganization"/>, and drives
///    a persistent teammate through <c>working -&gt; waiting -&gt; wake</c> rework.
/// </summary>
public sealed partial class ToolChainEndpointTests
{
    [Fact]
    public void FullDuplexRunEngineLanesFile_IsDeleted_WhileCheckpointLaneKeyContractRemainsIntact()
    {
        var manifestPath = FindGraphSeedManifestPath();
        var repoRoot = Directory.GetParent(manifestPath)!.Parent!.Parent!.Parent!.Parent!.FullName;
        var deletedLanesFile = Path.Combine(repoRoot, "TinadecCore", "DmaEA", "FullDuplexRunEngine.Lanes.cs");
        Assert.False(File.Exists(deletedLanesFile), "FullDuplexRunEngine.Lanes.cs must stay deleted (#33).");

        // Checkpoint & CrossLaneWaitsFor contract compatibility preserved for replay/DTO readers.
        var mainTask = new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "main-1", LaneKey = "main", Status = "pending" };
        var followTask = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "follow-1",
            LaneKey = "lane-2",
            Status = "pending",
            Dependencies = ["main-1"]
        };
        Assert.Equal(["main"], FullDuplexRunEngine.CrossLaneWaitsFor([mainTask, followTask], "lane-2")!);
    }

    [Fact]
    public async Task ExploreFirstTeam_CoordinatorExploresPlansSpawnsWakesPersistentTeammateAndIntegratesWithoutPlanner()
    {
        var workspace = Path.Combine(_root, "workspace-explore-first-team");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "README.md"), "# Team v2 Workspace\n");
        RunGitInWorkspace(workspace, "init");
        RunGitInWorkspace(workspace, "config", "user.email", "qa@tinadec.local");
        RunGitInWorkspace(workspace, "config", "user.name", "Tinadec QA");
        RunGitInWorkspace(workspace, "add", "README.md");
        RunGitInWorkspace(workspace, "commit", "-m", "initial commit");

        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenWorkerTurns(
                // Coordinator turn 1: read-only explore + shared team_plan
                [
                    new FunctionCallContent("coord-read", "read_file", new Dictionary<string, object?> { ["filepath"] = "README.md" }),
                    new FunctionCallContent("coord-plan", "team_plan", new Dictionary<string, object?>
                    {
                        ["goal"] = "Implement and refine feature module",
                        ["steps"] = new object[]
                        {
                            new Dictionary<string, object?> { ["id"] = "s1", ["title"] = "Draft feature slice", ["status"] = "in_progress", ["owner"] = "impl" },
                            new Dictionary<string, object?> { ["id"] = "s2", ["title"] = "Verify & integrate", ["status"] = "pending", ["owner"] = "coordinator" }
                        }
                    })
                ],
                // Coordinator turn 2: spawn persistent teammate @impl + park on team_wait
                [
                    new FunctionCallContent("coord-spawn", "team_spawn", new Dictionary<string, object?>
                    {
                        ["agent_slug"] = "global_engineering",
                        ["handle"] = "impl",
                        ["mode"] = "teammate",
                        ["prompt"] = "Draft src/feature.ts v1",
                        ["write_scope"] = new[] { "src" }
                    }),
                    new FunctionCallContent("coord-wait-1", "team_wait", new Dictionary<string, object?>
                    {
                        ["handles"] = new[] { "impl" }
                    })
                ],
                // Persistent teammate @impl turn 1 (working -> waiting): posts board update and settles into waiting
                [
                    new FunctionCallContent("impl-msg-1", "team_message", new Dictionary<string, object?>
                    {
                        ["target"] = "coordinator",
                        ["kind"] = "result",
                        ["content"] = "v1 drafted in src/feature.ts"
                    })
                ],
                [new TextContent("已完成 src/feature.ts 初稿。")],
                // Coordinator turn 3 (resumed from team_wait): wakes waiting @impl via team_message(wake=true) + team_wait
                [
                    new FunctionCallContent("coord-wake-msg", "team_message", new Dictionary<string, object?>
                    {
                        ["target"] = "impl",
                        ["kind"] = "review",
                        ["content"] = "请补充导出类型注解后回报。",
                        ["wake"] = true
                    }),
                    new FunctionCallContent("coord-wait-2", "team_wait", new Dictionary<string, object?>
                    {
                        ["handles"] = new[] { "impl" }
                    })
                ],
                // Persistent teammate @impl turn 2 (woken from waiting -> working -> waiting, RelayDepth == 1)
                [new TextContent("已补充导出类型注解并通过自检。")],
                // Coordinator turn 4: calls team_integrate(inspect) + updates plan to completed
                [
                    new FunctionCallContent("coord-integrate-inspect", "team_integrate", new Dictionary<string, object?>
                    {
                        ["action"] = "inspect"
                    }),
                    new FunctionCallContent("coord-plan-done", "team_plan", new Dictionary<string, object?>
                    {
                        ["goal"] = "Implement and refine feature module",
                        ["steps"] = new object[]
                        {
                            new Dictionary<string, object?> { ["id"] = "s1", ["title"] = "Draft feature slice", ["status"] = "completed", ["owner"] = "impl" },
                            new Dictionary<string, object?> { ["id"] = "s2", ["title"] = "Verify & integrate", ["status"] = "completed", ["owner"] = "coordinator" }
                        }
                    })
                ],
                // Coordinator turn 5: final synthesis
                [new TextContent("特性模块已完成初稿、唤醒返工与集成预检。")])
            .WhenMeeting("特性模块已完成初稿、唤醒返工与集成预检。");

        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        // Install unmodified v3.7.0 GraphSeedPack: free_director has team_spawn on meeting!
        await InstallGraphSeedPackAsync(client);

        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "explore-first team", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "explore-first session",
            mode_version_id = await LatestPublishedModeVersionIdAsync("free_director")
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "实现并打磨特性模块",
            client_message_id = "team-v2-1",
            permission_mode = "full-access"
        });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        var done = Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error");
        Assert.Equal("done", KindOf(done));

        // 1. Explore-First coordinator never invokes PlanningAgent, and executed read_file directly.
        Assert.Equal(0, script.PlannerCalls);
        Assert.Contains("read_file", provider.ReceivedToolIds);

        // 2. SessionPlan synced via team_plan.
        var sessionPlan = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/plan");
        Assert.Equal("Implement and refine feature module", sessionPlan.GetProperty("goal").GetString());

        // 3. Auto organization enrollment registered both coordinator ("meeting") and teammate ("impl").
        var organization = await client.GetFromJsonAsync<OrganizationDto>($"/api/v1/sessions/{sessionId}/organization", SnakeWire);
        Assert.NotNull(organization);
        Assert.Contains(organization!.Members, m => m.DisplayName == "meeting" && m.Role == OrganizationRoles.Conversation);
        Assert.Contains(organization.Members, m => m.DisplayName == "impl" && m.Role == OrganizationRoles.Executor);

        // 4. Durable checkpoint & events prove persistent working -> waiting -> wake rework cycle (RelayDepth >= 1).
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var savedCp = await lifecycle.GetCurrentRunCheckpointAsync(runId.ToString());
        Assert.NotNull(savedCp);
        var cp = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(savedCp!.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var implTeammate = Assert.Single(cp.Teammates, t => t.Handle == "impl");
        Assert.Equal("teammate", implTeammate.Mode);
        Assert.True(implTeammate.RelayDepth >= 1, $"Expected persistent teammate RelayDepth >= 1 after board wake, got {implTeammate.RelayDepth}");
        Assert.True(cp.TeamBoard.Count >= 3);

        var runEvents = (await lifecycle.ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(e => e.EventType)
            .ToList();
        Assert.Contains("team.teammate_spawned", runEvents);
        Assert.Contains("team.board_message", runEvents);
    }

    private static void RunGitInWorkspace(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }
}
