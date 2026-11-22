using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Task #50 — Multi-Agent Teams v3 end-to-end &amp; concurrency invariants:
/// 1) True async teammate Actor loop (zero <c>Task.WhenAll</c> batch barrier; sub-second live board delivery to running peers while a slow sibling stays blocked).
/// 2) Single teammate hitting <c>awaiting_approval</c> suspends ONLY its own actor; sibling teammates finish concurrently without collateral blocking, and resumed tasks (<c>ToolTurns.Count &gt; 0</c>) stay in the concurrent pool.
/// 3) Senior teammates holding <c>team_spawn</c> execute concurrently in the actor pool (no forced degradation to serial execution) while respecting <c>MaxParallelWorkers</c> and depth bounds.
/// 4) Decentralized atomic claimable task board (<c>SessionPlanService</c> / <c>team_tasks</c> / <c>TeamBoardMcpBridge</c>): mutex + optimistic concurrency prevents double-claiming, <c>complete</c> unlocks <c>depends_on</c> downstream tasks, <c>release</c> returns tasks to the pool, and persistent <c>teammate</c> workers autonomously claim open tasks while <c>ephemeral</c> workers exit cleanly.
/// 5) Direct human-to-teammate control endpoints (<c>GET /teammates</c>, <c>POST /steer</c>, <c>POST /pause</c>, <c>POST /resume</c>).
/// </summary>
public sealed class AgentTeamsV3Tests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-teams-v3-tests", Guid.NewGuid().ToString("N"));
    private TeamsV3HostFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(300);
            }
        }
    }

    [Fact]
    public async Task AsyncActorLoop_DeliversLiveMessageMidTurn_AndSettlesFastTeammateWithoutWaitingForSlowSibling()
    {
        var workspace = Path.Combine(_root, "ws-async-actor");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "# Async Actor Test\n");

        var slowPeerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowPeer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverEnteredFirstTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceiverFirstTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderMessagePosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverObservedLiveToken = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = new TeamsV3FakeToolProvider
        {
            Hold = async req =>
            {
                var path = req.Params is { } prm && prm.TryGetProperty("filepath", out var fp) ? fp.GetString() : null;
                if (string.Equals(path, "slow.txt", StringComparison.OrdinalIgnoreCase))
                {
                    slowPeerStarted.TrySetResult();
                    await releaseSlowPeer.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                else if (string.Equals(path, "receiver-gate.txt", StringComparison.OrdinalIgnoreCase))
                {
                    receiverEnteredFirstTool.TrySetResult();
                    await releaseReceiverFirstTool.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        };

        var chatRouter = new HandleRoutedChatRouter("全部并行队友已完成交付。")
            .OnHandle("solo", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(
                    Call("c-spawn-slow", "team_spawn", new { agent_slug = "search", handle = "slow-peer", mode = "teammate", prompt = "Inspect slow.txt" }),
                    Call("c-spawn-recv", "team_spawn", new { agent_slug = "search", handle = "live-receiver", mode = "teammate", prompt = "Inspect receiver-gate.txt and await live token" }),
                    Call("c-spawn-send", "team_spawn", new { agent_slug = "search", handle = "fast-sender", mode = "teammate", prompt = "Send live token to @live-receiver" }),
                    Call("c-wait-all", "team_wait", new { handles = new[] { "slow-peer", "live-receiver", "fast-sender" }, until = "settled" })),
                _ => new ChatMessage(ChatRole.Assistant, "所有队友均已结算完成。")
            }))
            .OnHandle("slow-peer", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(Call("s-read", "read_file", new { filepath = "slow.txt" })),
                _ => new ChatMessage(ChatRole.Assistant, "慢速队友读取完毕。")
            }))
            .OnHandle("fast-sender", (turn, _, _) =>
            {
                if (turn == 1)
                {
                    return Task.FromResult(AssistantCalls(Call("f-msg", "team_message", new
                    {
                        target = "live-receiver",
                        kind = "update",
                        content = "LIVE_PORT_TOKEN_8899"
                    })));
                }
                senderMessagePosted.TrySetResult();
                return Task.FromResult(new ChatMessage(ChatRole.Assistant, "快速队友已发出实时令牌并交卷。"));
            })
            .OnHandle("live-receiver", (turn, messages, options) =>
            {
                if (turn == 1)
                {
                    return Task.FromResult(AssistantCalls(Call("r-read", "read_file", new { filepath = "receiver-gate.txt" })));
                }
                var fullPromptView = FlattenMessagesAndInstructions(messages, options);
                receiverObservedLiveToken.TrySetResult(fullPromptView);
                return Task.FromResult(new ChatMessage(ChatRole.Assistant, "已实时收到 LIVE_PORT_TOKEN_8899 并完成处理。"));
            });

        var client = await StartHostAndInstallSeedPackAsync(chatRouter, provider, maxParallelWorkers: 4);
        var sessionId = await CreateTeamSessionAsync(client, workspace, "async-actor-session");

        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "验证无屏障异步队友实时通信",
            client_message_id = "v3-actor-1",
            permission_mode = "full-access"
        });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();

        // Wait until slow-peer is blocked inside its tool AND receiver is inside turn 1 AND fast-sender finished posting.
        await slowPeerStarted.Task.WaitAsync(TimeSpan.FromSeconds(25));
        await receiverEnteredFirstTool.Task.WaitAsync(TimeSpan.FromSeconds(25));
        await senderMessagePosted.Task.WaitAsync(TimeSpan.FromSeconds(25));

        // Wait until fast-sender settles into 'waiting' WHILE slow-peer is still blocked!
        await PollUntilAsync(async () =>
        {
            var list = await client.GetFromJsonAsync<TeammateListResponseDto>($"/api/v1/runs/{runId}/teammates", SnakeJson);
            var fast = list?.Teammates.FirstOrDefault(t => t.Handle == "fast-sender");
            var slow = list?.Teammates.FirstOrDefault(t => t.Handle == "slow-peer");
            return fast?.Status == "waiting" && slow?.Status == "working";
        }, TimeSpan.FromSeconds(15), "fast-sender must settle into 'waiting' without waiting for slow-peer");

        // Now release receiver's turn-1 tool while slow-peer STILL blocks: receiver must immediately see LIVE_PORT_TOKEN_8899 in turn 2!
        releaseReceiverFirstTool.TrySetResult();
        var observedContext = await receiverObservedLiveToken.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("LIVE_PORT_TOKEN_8899", observedContext, StringComparison.Ordinal);

        // Receiver also settles into 'waiting' while slow-peer is still working!
        await PollUntilAsync(async () =>
        {
            var list = await client.GetFromJsonAsync<TeammateListResponseDto>($"/api/v1/runs/{runId}/teammates", SnakeJson);
            var recv = list?.Teammates.FirstOrDefault(t => t.Handle == "live-receiver");
            var slow = list?.Teammates.FirstOrDefault(t => t.Handle == "slow-peer");
            return recv?.Status == "waiting" && slow?.Status == "working";
        }, TimeSpan.FromSeconds(15), "live-receiver must settle while slow-peer is still running");

        // Finally release slow-peer and verify clean run completion.
        releaseSlowPeer.TrySetResult();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Contains(chunks, c => c.GetProperty("kind").GetString() == "done");
    }

    [Fact]
    public async Task SingleTeammateAwaitingApproval_SuspendsOnlyItself_WhileSiblingTeammatesFinishConcurrently()
    {
        var workspace = Path.Combine(_root, "ws-approval-isolation");
        Directory.CreateDirectory(workspace);

        var readerStartedAfterApprovalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TeamsV3FakeToolProvider();

        var chatRouter = new HandleRoutedChatRouter("审批隔离与并发续跑全部通过。")
            .OnHandle("solo", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(
                    Call("sp-writer", "team_spawn", new { agent_slug = "global_engineering", handle = "writer-gated", mode = "teammate", prompt = "Read then write gated.txt", write_scope = new[] { "gated.txt" } }),
                    Call("sp-reader", "team_spawn", new { agent_slug = "search", handle = "reader-free", mode = "teammate", prompt = "Perform multi-turn read_file calls concurrently" }),
                    Call("sp-wait", "team_wait", new { handles = new[] { "writer-gated", "reader-free" }, until = "settled" })),
                _ => new ChatMessage(ChatRole.Assistant, "双队友均已完成。")
            }))
            .OnHandle("writer-gated", (turn, _, _) => Task.FromResult(turn switch
            {
                // Turn 1 builds ToolTurns.Count > 0 before Turn 2 hits awaiting_approval!
                1 => AssistantCalls(Call("w-read-1", "read_file", new { filepath = "README.md" })),
                2 => AssistantCalls(Call("w-write-2", "write_file", new { filepath = "gated.txt", content = "approved-payload" })),
                _ => new ChatMessage(ChatRole.Assistant, "审批通过后已完成写入 gated.txt。")
            }))
            .OnHandle("reader-free", async (turn, _, _) =>
            {
                if (turn == 1)
                {
                    return AssistantCalls(Call("r-free-1", "read_file", new { filepath = "a.txt" }));
                }
                if (turn == 2)
                {
                    // Wait until writer-gated is confirmed parked on awaiting_approval, proving reader-free continues concurrently!
                    await readerStartedAfterApprovalGate.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    return AssistantCalls(Call("r-free-2", "read_file", new { filepath = "b.txt" }));
                }
                return new ChatMessage(ChatRole.Assistant, "无审批阻塞队友已全速独立完结。");
            });

        var client = await StartHostAndInstallSeedPackAsync(chatRouter, provider, maxParallelWorkers: 4);
        var sessionId = await CreateTeamSessionAsync(client, workspace, "approval-isolation-session");

        // Default 'ask' permission mode gates write_file on human approval while letting read_file run freely.
        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "并发执行读写队友并验证单队友审批不连坐",
            client_message_id = "v3-approval-1",
            permission_mode = "ask"
        });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();

        var approvalId = await WaitForPendingApprovalAsync(client, runId, TimeSpan.FromSeconds(30));

        // Release reader-free's second turn WHILE writer-gated is still unapproved!
        readerStartedAfterApprovalGate.TrySetResult();

        // Assert: writer-gated is parked in 'awaiting_approval', while reader-free finishes all turns and settles into 'waiting'!
        await PollUntilAsync(async () =>
        {
            var list = await client.GetFromJsonAsync<TeammateListResponseDto>($"/api/v1/runs/{runId}/teammates", SnakeJson);
            var writer = list?.Teammates.FirstOrDefault(t => t.Handle == "writer-gated");
            var reader = list?.Teammates.FirstOrDefault(t => t.Handle == "reader-free");
            return writer is { Status: "awaiting_approval", AwaitingApproval: true }
                && reader is { Status: "waiting", AwaitingApproval: false };
        }, TimeSpan.FromSeconds(20), "reader-free must finish and settle while writer-gated awaits approval");

        // Approve writer-gated (which already has ToolTurns.Count > 0) and verify seamless resumption!
        using var approveRes = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.OK, approveRes.StatusCode);

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Contains(chunks, c => c.GetProperty("kind").GetString() == "done");
    }

    [Fact]
    public async Task SeniorTeammatesWithTeamSpawn_AndResumedTeammates_ExecuteConcurrentlyInActorPool()
    {
        var workspace = Path.Combine(_root, "ws-senior-concurrency");
        Directory.CreateDirectory(workspace);

        var inFlightSeniorTools = 0;
        var maxObservedConcurrentSeniors = 0;
        var bothSeniorsInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = new TeamsV3FakeToolProvider
        {
            Hold = async req =>
            {
                var path = req.Params is { } prm && prm.TryGetProperty("filepath", out var fp) ? fp.GetString() : null;
                if (path is "senior-a-2.txt" or "senior-b-2.txt")
                {
                    var current = Interlocked.Increment(ref inFlightSeniorTools);
                    int snapshotMax;
                    do
                    {
                        snapshotMax = Volatile.Read(ref maxObservedConcurrentSeniors);
                        if (current <= snapshotMax) break;
                    } while (Interlocked.CompareExchange(ref maxObservedConcurrentSeniors, current, snapshotMax) != snapshotMax);

                    if (current >= 2)
                    {
                        bothSeniorsInFlight.TrySetResult();
                    }

                    // Both senior teammates (each carrying team_spawn AND ToolTurns.Count > 0) must overlap in flight!
                    await bothSeniorsInFlight.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Interlocked.Decrement(ref inFlightSeniorTools);
                }
            }
        };

        var chatRouter = new HandleRoutedChatRouter("资深队友并发派生与执行成功。")
            .OnHandle("solo", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(
                    Call("sp-sa", "team_spawn", new { agent_slug = "global_engineering", handle = "senior-a", mode = "teammate", prompt = "Senior A parallel work + nested helper" }),
                    Call("sp-sb", "team_spawn", new { agent_slug = "global_engineering", handle = "senior-b", mode = "teammate", prompt = "Senior B parallel work" }),
                    Call("sp-wait", "team_wait", new { handles = new[] { "senior-a", "senior-b" }, until = "settled" })),
                _ => new ChatMessage(ChatRole.Assistant, "资深队友与子助手全部完成。")
            }))
            .OnHandle("senior-a", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(Call("sa-1", "read_file", new { filepath = "senior-a-1.txt" })),
                2 => AssistantCalls(
                    Call("sa-2", "read_file", new { filepath = "senior-a-2.txt" }),
                    Call("sa-sub", "team_spawn", new { agent_slug = "search", handle = "sub-helper", mode = "ephemeral", prompt = "Quick lookup for Senior A" })),
                3 => AssistantCalls(Call("sa-wait", "team_wait", new { handles = new[] { "sub-helper" }, until = "settled" })),
                _ => new ChatMessage(ChatRole.Assistant, "Senior A 携子助手完成。")
            }))
            .OnHandle("senior-b", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(Call("sb-1", "read_file", new { filepath = "senior-b-1.txt" })),
                2 => AssistantCalls(Call("sb-2", "read_file", new { filepath = "senior-b-2.txt" })),
                _ => new ChatMessage(ChatRole.Assistant, "Senior B 并发完成。")
            }))
            .OnHandle("sub-helper", (_, _, _) => Task.FromResult(new ChatMessage(ChatRole.Assistant, "子助手检索完毕。")));

        var client = await StartHostAndInstallSeedPackAsync(
            chatRouter,
            provider,
            maxParallelWorkers: 4,
            mutateManifest: EnableSeniorTeamSpawnOnGlobalEngineering);
        var sessionId = await CreateTeamSessionAsync(client, workspace, "senior-concurrency-session");

        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "两个带 team_spawn 的资深队友并发工作",
            client_message_id = "v3-senior-1",
            permission_mode = "full-access"
        });
        await active.Completion.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(maxObservedConcurrentSeniors >= 2,
            $"Expected >= 2 senior teammates with team_spawn & ToolTurns.Count > 0 executing concurrently, observed {maxObservedConcurrentSeniors}.");
    }

    [Fact]
    public async Task ClaimableTaskBoard_AtomicConcurrentClaimPreventsDoubleAssignment_UnlocksDownstreamAndBridgesMcp()
    {
        var workspace = Path.Combine(_root, "ws-task-board");
        Directory.CreateDirectory(workspace);

        var provider = new TeamsV3FakeToolProvider();
        var persistentClaimedFollowup = new ConcurrentBag<string>();

        var chatRouter = new HandleRoutedChatRouter("原子抢单任务板与常驻队友自主续领完成。")
            .OnHandle("solo", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(
                    Call("plan-init", "team_plan", new
                    {
                        goal = "Decentralized claimable task board pipeline",
                        steps = new object[]
                        {
                            new { id = "open-1", title = "Build schema layer", status = "pending" },
                            new { id = "open-2", title = "Build service layer", status = "pending", depends_on = new[] { "open-1" } },
                            new { id = "coord-final", title = "Coordinator wrap-up", status = "pending", owner = "coordinator", depends_on = new[] { "open-2" } }
                        }
                    }),
                    Call("sp-persistent", "team_spawn", new { agent_slug = "search", handle = "persistent-worker", mode = "teammate", prompt = "Claim open-1 and complete it" }),
                    Call("sp-ephemeral", "team_spawn", new { agent_slug = "search", handle = "oneshot-worker", mode = "ephemeral", prompt = "Run one-shot verification only" }),
                    Call("wait-team", "team_wait", new { handles = new[] { "persistent-worker", "oneshot-worker" }, until = "settled" })),
                _ => new ChatMessage(ChatRole.Assistant, "任务板全链路完成。")
            }))
            .OnHandle("oneshot-worker", (_, _, _) =>
                Task.FromResult(new ChatMessage(ChatRole.Assistant, "一次性队友已完成独立点检。")))
            .OnHandle("persistent-worker", (turn, messages, options) =>
            {
                var view = FlattenMessagesAndInstructions(messages, options);
                persistentClaimedFollowup.Add(view);
                if (turn == 1)
                {
                    return Task.FromResult(AssistantCalls(
                        Call("pw-claim-1", "team_tasks", new { action = "claim", step_id = "open-1" }),
                        Call("pw-done-1", "team_tasks", new { action = "complete", step_id = "open-1", summary = "Schema layer finished" })));
                }
                if (turn == 2 && !view.Contains("open-2", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ChatMessage(ChatRole.Assistant, "已完成 open-1 交卷。"));
                }
                return Task.FromResult(new ChatMessage(ChatRole.Assistant, "已自主认领并完成解锁后的 open-2 任务。"));
            });

        var client = await StartHostAndInstallSeedPackAsync(chatRouter, provider, maxParallelWorkers: 4);
        var sessionId = await CreateTeamSessionAsync(client, workspace, "claimable-board-session");

        // Part A: Direct high-concurrency atomic claim / release / complete + MCP loopback bridge verification.
        var taskBoard = _factory!.Services.GetRequiredService<ITeamTaskBoardPort>();
        var mcpBridge = _factory.Services.GetRequiredService<ITeamBoardMcpBridge>();
        var planService = _factory.Services.GetRequiredService<SessionPlanService>();

        var concurrencySessionRes = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "atomic-claim-race" });
        var raceSessionId = (await concurrencySessionRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await planService.PutAsync(raceSessionId, new SessionPlanPutRequestDto
        {
            Title = "Atomic Race Plan",
            Goal = "Test 8-way concurrent claim + dependency unlock + release",
            Status = SessionPlanStatuses.Active,
            Phases =
            [
                new SessionPlanPhaseDto
                {
                    Id = "phase-race",
                    Title = "Race",
                    Order = 1,
                    Steps =
                    [
                        new SessionPlanStepDto { Id = "s-hot", Title = "Hot contested task", Status = SessionPlanStatuses.StepPending },
                        new SessionPlanStepDto { Id = "s-downstream", Title = "Downstream task", Status = SessionPlanStatuses.StepPending, DependsOn = ["s-hot"] }
                    ]
                }
            ]
        });

        // 1. Only 's-hot' is ready initially ('s-downstream' is blocked on 's-hot').
        var initialReady = await taskBoard.ListTasksAsync(raceSessionId, onlyReady: true);
        Assert.Single(initialReady.Tasks);
        Assert.Equal("s-hot", initialReady.Tasks[0].Id);

        // 2. 8 concurrent contenders (6 direct + 2 via scoped MCP bridge tokens) race to claim 's-hot'!
        var syntheticRunId = Guid.NewGuid();
        var mcpLeaseA = mcpBridge.IssueToken(Guid.Empty, null, raceSessionId, syntheticRunId, "cli-agent-7");
        var mcpLeaseB = mcpBridge.IssueToken(Guid.Empty, null, raceSessionId, syntheticRunId, "acp-agent-8");

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contendTasks = Enumerable.Range(1, 6).Select(async i =>
        {
            await startGate.Task;
            return await taskBoard.ClaimTaskAsync(raceSessionId, "s-hot", $"worker-{i}");
        }).ToList();

        var mcpClaimTaskA = Task.Run(async () =>
        {
            await startGate.Task;
            var wire = await mcpBridge.ExecuteScopedToolAsync(
                mcpLeaseA,
                "team_tasks",
                JsonSerializer.SerializeToElement(new { action = "claim", step_id = "s-hot" }));
            Assert.True(wire.IsSuccess, wire.Error);
            return JsonSerializer.Deserialize<TeamTaskClaimResult>(wire.Result!.Value.GetRawText(), SnakeJson)!;
        });
        var mcpClaimTaskB = Task.Run(async () =>
        {
            await startGate.Task;
            var wire = await mcpBridge.ExecuteScopedToolAsync(
                mcpLeaseB,
                "team_tasks",
                JsonSerializer.SerializeToElement(new { action = "claim", step_id = "s-hot" }));
            Assert.True(wire.IsSuccess, wire.Error);
            return JsonSerializer.Deserialize<TeamTaskClaimResult>(wire.Result!.Value.GetRawText(), SnakeJson)!;
        });

        startGate.TrySetResult();
        var allOutcomes = await Task.WhenAll([.. contendTasks, mcpClaimTaskA, mcpClaimTaskB]);

        var winners = allOutcomes.Where(o => o.Claimed).ToArray();
        var losers = allOutcomes.Where(o => !o.Claimed).ToArray();
        var soleWinner = Assert.Single(winners);
        Assert.Equal(7, losers.Length);
        Assert.All(losers, loser =>
        {
            Assert.Equal("already_claimed", loser.Reason);
            Assert.Equal(soleWinner.ClaimedBy, loser.AlreadyClaimedBy);
        });

        // 3. Release back to pool -> another teammate claims -> completes -> unlocks 's-downstream'!
        var released = await taskBoard.ReleaseTaskAsync(raceSessionId, "s-hot", soleWinner.ClaimedBy!, "Blocked on env, releasing");
        Assert.True(released.Released);
        Assert.Equal("pending", released.Status);

        var reclaimed = await taskBoard.ClaimTaskAsync(raceSessionId, null, "rescue-worker");
        Assert.True(reclaimed.Claimed);
        Assert.Equal("s-hot", reclaimed.StepId);

        var completed = await taskBoard.CompleteTaskAsync(raceSessionId, "s-hot", "rescue-worker", "Completed cleanly", ["src/hot.cs"]);
        Assert.True(completed.Completed);
        var unlocked = Assert.Single(completed.UnlockedReadyTasks);
        Assert.Equal("s-downstream", unlocked.Id);

        // Part B: End-to-end run where persistent teammate claims 'open-1', completes it, and autonomously claims unlocked 'open-2' upon settling, while ephemeral teammate terminates!
        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "执行去中心化抢单面板任务",
            client_message_id = "v3-board-run-1",
            permission_mode = "full-access"
        });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Contains(chunks, c => c.GetProperty("kind").GetString() == "done");

        var finalTeammates = await client.GetFromJsonAsync<TeammateListResponseDto>($"/api/v1/runs/{runId}/teammates", SnakeJson);
        Assert.NotNull(finalTeammates);
        var oneshot = Assert.Single(finalTeammates!.Teammates, t => t.Handle == "oneshot-worker");
        var persistent = Assert.Single(finalTeammates.Teammates, t => t.Handle == "persistent-worker");
        Assert.Equal("terminated", oneshot.Status);
        Assert.Equal("waiting", persistent.Status);
        Assert.True(persistent.RelayDepth >= 1, "Persistent teammate should have autonomously claimed unlocked open-2 on wake #1.");

        var finalBoard = await taskBoard.ListTasksAsync(sessionId, onlyReady: false);
        Assert.Equal("completed", finalBoard.Tasks.Single(t => t.Id == "open-1").Status);
        Assert.Equal("completed", finalBoard.Tasks.Single(t => t.Id == "open-2").Status);
        Assert.Equal("persistent-worker", finalBoard.Tasks.Single(t => t.Id == "open-2").ClaimedBy);
    }

    [Fact]
    public async Task DirectHumanTeammateControl_SteerRunningAndSleepingTeammates_AndIndependentPauseResume()
    {
        var workspace = Path.Combine(_root, "ws-direct-control");
        Directory.CreateDirectory(workspace);

        var alphaInTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAlphaTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gammaInTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGammaTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinatorGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alphaReceivedSteer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaWokenBySteer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = new TeamsV3FakeToolProvider
        {
            Hold = async req =>
            {
                var path = req.Params is { } prm && prm.TryGetProperty("filepath", out var fp) ? fp.GetString() : null;
                if (path == "alpha-hold.txt")
                {
                    alphaInTool.TrySetResult();
                    await releaseAlphaTool.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                else if (path == "gamma-hold.txt")
                {
                    gammaInTool.TrySetResult();
                    await releaseGammaTool.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                else if (path == "coord-hold.txt")
                {
                    await coordinatorGate.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        };

        var chatRouter = new HandleRoutedChatRouter("单队友直通 Steer 与独立暂停恢复验证通过。")
            .OnHandle("solo", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(
                    Call("sp-a", "team_spawn", new { agent_slug = "search", handle = "worker-alpha", mode = "teammate", prompt = "Inspect alpha-hold.txt" }),
                    Call("sp-b", "team_spawn", new { agent_slug = "search", handle = "worker-beta", mode = "teammate", prompt = "Finish quickly and sleep on board" }),
                    Call("sp-g", "team_spawn", new { agent_slug = "search", handle = "worker-gamma", mode = "teammate", prompt = "Inspect gamma-hold.txt in two turns" }),
                    Call("c-hold", "read_file", new { filepath = "coord-hold.txt" })),
                2 => AssistantCalls(
                    Call("c-wait", "team_wait", new { handles = new[] { "worker-alpha", "worker-beta", "worker-gamma" }, until = "settled" })),
                _ => new ChatMessage(ChatRole.Assistant, "全部受控队友已顺利收敛。")
            }))
            .OnHandle("worker-alpha", (turn, messages, options) =>
            {
                if (turn == 1)
                {
                    return Task.FromResult(AssistantCalls(Call("a-1", "read_file", new { filepath = "alpha-hold.txt" })));
                }
                alphaReceivedSteer.TrySetResult(FlattenMessagesAndInstructions(messages, options));
                return Task.FromResult(new ChatMessage(ChatRole.Assistant, "Alpha 已按人类直通纠偏切换至零拷贝模式。"));
            })
            .OnHandle("worker-beta", (turn, messages, options) =>
            {
                if (turn == 1)
                {
                    return Task.FromResult(new ChatMessage(ChatRole.Assistant, "Beta 首轮任务完成，进入休眠待命。"));
                }
                betaWokenBySteer.TrySetResult(FlattenMessagesAndInstructions(messages, options));
                return Task.FromResult(new ChatMessage(ChatRole.Assistant, "Beta 被人类直通私信唤醒并补充了边界检查。"));
            })
            .OnHandle("worker-gamma", (turn, _, _) => Task.FromResult(turn switch
            {
                1 => AssistantCalls(Call("g-1", "read_file", new { filepath = "gamma-hold.txt" })),
                _ => new ChatMessage(ChatRole.Assistant, "Gamma 恢复运行后顺利完成第 2 回合。")
            }));

        var client = await StartHostAndInstallSeedPackAsync(chatRouter, provider, maxParallelWorkers: 4);
        var sessionId = await CreateTeamSessionAsync(client, workspace, "direct-control-session");

        var active = StartStreamingInvoke(client, sessionId, new
        {
            content = "验证单队友直通控制 API",
            client_message_id = "v3-direct-ctrl-1",
            permission_mode = "full-access"
        });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();

        await alphaInTool.Task.WaitAsync(TimeSpan.FromSeconds(25));
        await gammaInTool.Task.WaitAsync(TimeSpan.FromSeconds(25));

        // Wait for worker-beta to settle into 'waiting' (sleeping).
        await PollUntilAsync(async () =>
        {
            var list = await client.GetFromJsonAsync<TeammateListResponseDto>($"/api/v1/runs/{runId}/teammates", SnakeJson);
            return list?.Teammates.FirstOrDefault(t => t.Handle == "worker-beta")?.Status == "waiting";
        }, TimeSpan.FromSeconds(15), "worker-beta should settle into waiting");

        // 1. Pause ONLY worker-gamma: the run itself and worker-alpha must stay active!
        using var pauseRes = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/teammates/worker-gamma/pause", new { reason = "Hold gamma while alpha & beta proceed" });
        Assert.Equal(HttpStatusCode.OK, pauseRes.StatusCode);
        var pauseReceipt = await pauseRes.Content.ReadFromJsonAsync<TeammateControlReceiptDto>(SnakeJson);
        Assert.NotNull(pauseReceipt);
        Assert.True(pauseReceipt!.Paused);
        Assert.Equal("paused", pauseReceipt.Status);

        releaseGammaTool.TrySetResult(); // Gamma finishes its in-flight tool and parks at its pause boundary without entering turn 2!

        await PollUntilAsync(async () =>
        {
            var list = await client.GetFromJsonAsync<TeammateListResponseDto>($"/api/v1/runs/{runId}/teammates", SnakeJson);
            var gamma = list?.Teammates.FirstOrDefault(t => t.Handle == "worker-gamma");
            return list?.RunStatus == "executing" && gamma is { Paused: true, Status: "paused" };
        }, TimeSpan.FromSeconds(15), "worker-gamma must park as paused while run_status stays executing");

        // 2. Steer RUNNING worker-alpha (urgent=true): injects before alpha's next model turn!
        using var steerAlphaRes = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/teammates/worker-alpha/steer", new
        {
            message = "STEER_ALPHA_ZERO_COPY_9001",
            urgent = true
        });
        Assert.Equal(HttpStatusCode.OK, steerAlphaRes.StatusCode);
        var steerAlphaReceipt = await steerAlphaRes.Content.ReadFromJsonAsync<TeammateSteerReceiptDto>(SnakeJson);
        Assert.NotNull(steerAlphaReceipt);
        Assert.Equal("injected_next_turn", steerAlphaReceipt!.DeliveryMode);
        Assert.False(steerAlphaReceipt.Woken);

        releaseAlphaTool.TrySetResult();
        var alphaPrompt = await alphaReceivedSteer.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("STEER_ALPHA_ZERO_COPY_9001", alphaPrompt, StringComparison.Ordinal);

        // 3. Steer SLEEPING @worker-beta (with '@' prefix in URL): wakes immediately!
        using var steerBetaRes = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/teammates/@worker-beta/steer", new
        {
            message = "STEER_BETA_WAKE_CHECK_9002",
            urgent = true
        });
        Assert.Equal(HttpStatusCode.OK, steerBetaRes.StatusCode);
        var steerBetaReceipt = await steerBetaRes.Content.ReadFromJsonAsync<TeammateSteerReceiptDto>(SnakeJson);
        Assert.NotNull(steerBetaReceipt);
        Assert.True(steerBetaReceipt!.Woken);
        Assert.Equal("woken", steerBetaReceipt.DeliveryMode);

        var betaPrompt = await betaWokenBySteer.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("STEER_BETA_WAKE_CHECK_9002", betaPrompt, StringComparison.Ordinal);

        // 4. Verify orchestration snapshot also exposes teammates array, then resume worker-gamma!
        var orch = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        Assert.True(orch.TryGetProperty("teammates", out var orchTeammates) && orchTeammates.GetArrayLength() >= 3);

        using var resumeRes = await client.PostAsync($"/api/v1/runs/{runId}/teammates/worker-gamma/resume", content: null);
        Assert.Equal(HttpStatusCode.OK, resumeRes.StatusCode);
        var resumeReceipt = await resumeRes.Content.ReadFromJsonAsync<TeammateControlReceiptDto>(SnakeJson);
        Assert.NotNull(resumeReceipt);
        Assert.False(resumeReceipt!.Paused);
        Assert.Equal("resume", resumeReceipt.Action);

        coordinatorGate.TrySetResult();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Contains(chunks, c => c.GetProperty("kind").GetString() == "done");
    }

    // =========================================================================
    // Test Harness Helpers
    // =========================================================================

    private async Task<HttpClient> StartHostAndInstallSeedPackAsync(
        HandleRoutedChatRouter router,
        IToolProvider provider,
        int maxParallelWorkers = 4,
        Action<JsonNode>? mutateManifest = null)
    {
        _ = maxParallelWorkers;
        _factory = new TeamsV3HostFactory(_root, router, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client, mutateManifest);
        return client;
    }

    private async Task<Guid> CreateTeamSessionAsync(HttpClient client, string workspace, string title)
    {
        var projectRes = await client.PostAsJsonAsync("/api/v1/projects", new { name = title, path = workspace });
        var project = await projectRes.Content.ReadFromJsonAsync<JsonElement>();
        var modeVersionId = await LatestPublishedModeVersionIdAsync("free_director");
        var sessionRes = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title,
            mode_version_id = modeVersionId
        });
        var session = await sessionRes.Content.ReadFromJsonAsync<JsonElement>();
        return session.GetProperty("id").GetGuid();
    }

    private async Task<Guid> LatestPublishedModeVersionIdAsync(string modeSlug)
    {
        await using var cfg = await _factory!.Services
            .GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>()
            .CreateDbContextAsync();
        var modeIds = (await cfg.AgentModes.AsNoTracking()
            .Where(m => m.Slug == modeSlug && m.Status == "published")
            .Select(m => m.Id)
            .ToListAsync()).ToHashSet();
        var versions = await cfg.ModeVersions.AsNoTracking()
            .Where(v => v.Status == "published" && modeIds.Contains(v.AgentModeId))
            .ToListAsync();
        return versions.OrderByDescending(v => v.Version).First().Id;
    }

    private static void EnableSeniorTeamSpawnOnGlobalEngineering(JsonNode manifest)
    {
        var eng = manifest["resources"]!["agents"]!.AsArray()
            .First(a => (string?)a!["slug"] == "global_engineering")!;
        var scope = eng["tool_scope"]!.AsArray();
        foreach (var tool in new[] { "team_spawn", "team_wait", "team_message", "team_plan" })
        {
            if (!scope.Any(x => (string?)x == tool))
                scope.Add(tool);
        }
        var caps = eng["capabilities"]?.AsArray() ?? new JsonArray();
        foreach (var cap in new[] { "task.dispatch", "agent.spawn" })
        {
            if (!caps.Any(x => (string?)x == cap))
                caps.Add(cap);
        }
        eng["capabilities"] = caps;
        eng["allowed_dispatch_targets"] = new JsonArray("search");
    }

    private static async Task InstallGraphSeedPackAsync(HttpClient client, Action<JsonNode>? mutate = null)
    {
        var manifestPath = FindGraphSeedManifestPath();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, Encoding.UTF8))!;
        mutate?.Invoke(node);
        var manifest = JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
        var serverOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var roundTripped = JsonSerializer.Deserialize<AgentPackManifestDto>(manifest.GetRawText(), serverOptions);
        var serverElement = JsonSerializer.SerializeToElement(roundTripped, serverOptions);
        var digest = Convert.ToHexString(SHA256.HashData(JsonCanonicalizer.Canonicalize(serverElement))).ToLowerInvariant();
        var envelope = JsonSerializer.SerializeToElement(new
        {
            manifest,
            integrity = new { algorithm = "sha256", digest }
        });
        using var previewResponse = await client.PostAsJsonAsync("/api/v1/agent-packs/install-preview", envelope);
        Assert.True(previewResponse.IsSuccessStatusCode,
            $"seed-pack preview failed ({previewResponse.StatusCode}): {await previewResponse.Content.ReadAsStringAsync()}");
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.graph.seed-pack")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", $"v3-seed-pack-{Guid.NewGuid():N}");
        using var applyResponse = await client.SendAsync(apply);
        Assert.Equal(HttpStatusCode.Created, applyResponse.StatusCode);
    }

    private static string FindGraphSeedManifestPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "desktop", "src", "agentPacks", "GraphSeedPack", "manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("GraphSeedPack manifest.json not found.");
    }

    private static async Task<Guid> WaitForPendingApprovalAsync(HttpClient client, Guid runId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var approvals = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/approvals?status=pending") ?? [];
            var match = approvals.FirstOrDefault(item =>
                item.TryGetProperty("run_id", out var rid)
                && rid.ValueKind == JsonValueKind.String
                && Guid.TryParse(rid.GetString(), out var g)
                && g == runId);
            if (match.ValueKind != JsonValueKind.Undefined)
                return match.GetProperty("id").GetGuid();
            await Task.Delay(150);
        }
        throw new TimeoutException($"No pending approval observed for run {runId} within {timeout}.");
    }

    private static async Task PollUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out ({timeout}): {failureMessage}");
    }

    private sealed record ActiveInvoke(Task<JsonElement> Acknowledgement, Task<List<JsonElement>> Completion);

    private ActiveInvoke StartStreamingInvoke(HttpClient client, Guid sessionId, object body)
    {
        var ackTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
            try
            {
                var chunks = new List<JsonElement>();
                using var postRes = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", body);
                await _factory!.AssertStatusAsync(postRes, HttpStatusCode.Created, "Interaction admission");
                var receipt = await postRes.Content.ReadFromJsonAsync<JsonElement>();
                var runId = receipt.GetProperty("run_id").GetString();
                var turnId = receipt.TryGetProperty("turn_id", out var tid) ? tid.GetString() : null;

                using var streamReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/stream?after_seq=0&turn_id={turnId}");
                using var streamRes = await client.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead);
                await _factory!.AssertStatusAsync(streamRes, HttpStatusCode.OK, "Run SSE stream");
                using var stream = await streamRes.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                var sb = new StringBuilder();
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (sb.Length > 0)
                        {
                            var chunk = JsonSerializer.Deserialize<JsonElement>(sb.ToString());
                            chunks.Add(chunk);
                            if (chunk.GetProperty("kind").GetString() == "ack")
                                ackTcs.TrySetResult(chunk);
                            sb.Clear();
                        }
                        continue;
                    }
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                        sb.Append(line[6..]);
                }
                return chunks;
            }
            catch (Exception ex)
            {
                ackTcs.TrySetException(ex);
                throw;
            }
        });
        return new ActiveInvoke(ackTcs.Task, completion);
    }

    private static FunctionCallContent Call(string callId, string toolName, object args)
    {
        var element = JsonSerializer.SerializeToElement(args);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }
        return new FunctionCallContent(callId, toolName, dict);
    }

    private static ChatMessage AssistantCalls(params FunctionCallContent[] calls) =>
        new(ChatRole.Assistant, calls.Cast<AIContent>().ToList());

    private static string FlattenMessagesAndInstructions(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            sb.AppendLine(options.Instructions);
        foreach (var m in messages)
        {
            sb.AppendLine($"[{m.Role}]: {m.Text}");
            foreach (var fr in m.Contents.OfType<FunctionResultContent>())
            {
                sb.AppendLine($"[ToolResult {fr.CallId}]: {fr.Result}");
            }
        }
        return sb.ToString();
    }

    // =========================================================================
    // Handle-Routed Fake Chat Factory & Tool Provider
    // =========================================================================

    private sealed class HandleRoutedChatRouter(string meetingReply) : IAgentChatClientFactory
    {
        private readonly ConcurrentDictionary<string, Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, Task<ChatMessage>>> _handlers =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _turnCounters = new(StringComparer.OrdinalIgnoreCase);

        public HandleRoutedChatRouter OnHandle(
            string handle,
            Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, Task<ChatMessage>> handler)
        {
            _handlers[handle] = handler;
            return this;
        }

        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution
            {
                IsAvailable = true,
                BaseUrl = "http://localhost",
                Model = "fake-teams-v3",
                ApiKey = "test-key",
                ModelId = "openai/fake-teams-v3"
            });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(new ScopedHandleChatClient(this, resolution.TeammateHandle, meetingReply));

        private sealed class ScopedHandleChatClient(
            HandleRoutedChatRouter router,
            string? handle,
            string meetingReply) : IChatClient
        {
            public ChatClientMetadata Metadata { get; } = new("teams-v3-fake");

            public void Dispose() { }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var list = messages.ToList();
                var combinedPrompt = FlattenMessagesAndInstructions(list, options);

                var effectiveHandle = handle;
                if (string.IsNullOrWhiteSpace(effectiveHandle))
                {
                    var isCoordinatorToolLoop = options?.Tools?.Any(t =>
                        string.Equals(t.Name, "team_spawn", StringComparison.OrdinalIgnoreCase)) == true;
                    if (isCoordinatorToolLoop)
                    {
                        effectiveHandle = "solo";
                    }
                    else
                    {
                        if (combinedPrompt.Contains("revise_task_indexes", StringComparison.Ordinal))
                        {
                            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                "{\"decision\":\"pass\",\"reasons\":[\"verified\"],\"revise_task_indexes\":[],\"criteria_verdicts\":[]}"));
                        }
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant, meetingReply));
                    }
                }

                if (string.Equals(effectiveHandle, "coordinator", StringComparison.OrdinalIgnoreCase)
                    && !router._handlers.ContainsKey("coordinator")
                    && router._handlers.ContainsKey("solo"))
                {
                    effectiveHandle = "solo";
                }

                var turn = router._turnCounters.AddOrUpdate(effectiveHandle!, 1, (_, prev) => prev + 1);
                if (router._handlers.TryGetValue(effectiveHandle!, out var handler))
                {
                    var msg = await handler(turn, list, options).ConfigureAwait(false);
                    return new ChatResponse(EnsureOutcomeMarkerIfTextOnly(msg, options?.Instructions));
                }

                return new ChatResponse(EnsureOutcomeMarkerIfTextOnly(
                    new ChatMessage(ChatRole.Assistant, $"Completed default turn for @{effectiveHandle}."),
                    options?.Instructions));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                foreach (var update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }
            }

            private static ChatMessage EnsureOutcomeMarkerIfTextOnly(ChatMessage msg, string? instructions)
            {
                if (msg.Contents.OfType<FunctionCallContent>().Any() || string.IsNullOrWhiteSpace(msg.Text))
                    return msg;
                if (msg.Text.Contains(WorkerOutcomeProtocol.Marker, StringComparison.OrdinalIgnoreCase))
                    return msg;
                return new ChatMessage(ChatRole.Assistant, msg.Text.TrimEnd() + "\n" + WorkerOutcomeProtocol.Marker + " completed");
            }
        }
    }

    private sealed class TeamsV3HostFactory(
        string root,
        IAgentChatClientFactory chatFactory,
        IToolProvider toolProvider) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                    ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                    ["Logging:LogLevel:Default"] = "Warning"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(chatFactory);
                services.AddSingleton<ISecretStore>(new TestModelSecretStore());
                services.Replace(ServiceDescriptor.Singleton(toolProvider));
            });
        }
    }

    private sealed class TeamsV3FakeToolProvider : IToolProvider
    {
        public Func<ToolWireRequestDto, Task>? Hold { get; init; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (Hold is not null)
            {
                await Hold(request).ConfigureAwait(false);
            }
            return new ToolWireResponseDto
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Result = JsonSerializer.SerializeToElement(new { ok = true, tool = request.ToolId })
            };
        }

        private static ToolManifestDto CreateManifest()
        {
            string[] lowReadTools =
            [
                "read_file", "ls", "stat", "file_search", "mcp_search", "mcp_list",
                "git_status", "git_diff", "git_log", "git_branch_list", "git_blame", "git_file_at_revision",
                "browser_snapshot", "browser_tabs_list", "browser_wait", "browser_screenshot", "browser_profiles_list", "browser_handoff"
            ];
            string[] gatedTools =
            [
                "write_file", "replace_lines", "insert_line", "delete_line", "shell",
                "mcp_invoke", "web_fetch", "git_commit", "git_push", "git_worktree_create", "git_worktree_remove",
                "browser_navigate", "browser_click", "browser_type", "browser_press", "browser_scroll",
                "browser_history", "browser_tab_open", "browser_tab_select", "browser_tab_close", "browser_batch",
                "browser_takeover", "browser_finish", "browser_dialog", "browser_upload", "browser_download_save", "browser_run"
            ];

            var entries = new List<ToolManifestEntryDto>();
            foreach (var id in lowReadTools)
            {
                entries.Add(new ToolManifestEntryDto
                {
                    Id = id,
                    Description = $"Fake {id}",
                    RequiresApproval = false,
                    Risk = "low",
                    MutatesWorkspace = false
                });
            }
            foreach (var id in gatedTools)
            {
                entries.Add(new ToolManifestEntryDto
                {
                    Id = id,
                    Description = $"Fake {id}",
                    RequiresApproval = true,
                    Risk = id is "write_file" or "replace_lines" or "insert_line" or "delete_line" or "mcp_invoke" ? "medium" : "high",
                    MutatesWorkspace = id is "write_file" or "replace_lines" or "insert_line" or "delete_line" or "shell" or "git_commit" or "git_push" or "git_worktree_create" or "git_worktree_remove" or "browser_download_save"
                });
            }

            return new ToolManifestDto
            {
                ProtocolVersion = 2,
                ManifestHash = ToolManifestHasher.Compute(entries),
                Tools = entries
            };
        }
    }
}
