using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.AspNetCore.Endpoints;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Peers;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests.Peers;

public sealed class PeerDelegationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _root = ApiTestStorage.CreateRoot("peer-delegation-w3");
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        PeerProviderProxyRuntimeRegistry.ClearForTesting();
        PeerInboundDelegationRuntimeRegistry.ClearForTesting();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        PeerProviderProxyRuntimeRegistry.ClearForTesting();
        PeerInboundDelegationRuntimeRegistry.ClearForTesting();
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            try { _disposables[i].Dispose(); }
            catch { /* Best-effort test host teardown */ }
        }
        _disposables.Clear();
        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250);
            }
        }
    }

    [Fact]
    public async Task FiveToolsRegistered_EndToEndDelegation_BidirectionalApproval_RuntimeBadge_AndProviderProxyToken()
    {
        var cluster = new InMemoryPeerCluster();
        var observedControllerToolNames = new ConcurrentBag<string>();
        var workerWriteExecuted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteApprovalReadyForHuman = new TaskCompletionSource<(string DelegationId, Guid ApprovalId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var humanApprovedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        string? capturedDelegationId = null;
        var tokenIssuer = new RecordingProxyTokenIssuer();

        // Worker B chat behavior:
        // Before wb-write-1 completes -> calls gated write_file (triggers awaiting_approval in default permission mode!)
        // After remote controller approval executes wb-write-1 -> returns final build verification summary.
        var workerChat = new ScriptedNodeChatRouter("WORKER_B_BUILD_GREEN_42", (_, messages, _) =>
        {
            if (TryExtractLatestCompletedToolCall(messages) is null)
            {
                return Task.FromResult(AssistantCalls(
                    Call("wb-write-1", "write_file", new { filepath = "src/build-report.txt", content = "all checks passed" })));
            }
            return Task.FromResult(new ChatMessage(ChatRole.Assistant, "WORKER_B_BUILD_GREEN_42: remote build & test verification succeeded."));
        });

        var workerTools = new GatedFakeToolProvider
        {
            OnCall = req =>
            {
                if (string.Equals(req.ToolId, "write_file", StringComparison.OrdinalIgnoreCase))
                {
                    workerWriteExecuted.TrySetResult(req.ToolId);
                }
                return Task.CompletedTask;
            }
        };

        var nodeB = await CreateNodeAsync("node-b", "Mac Build Rig", cluster, workerChat, workerTools);
        var workerProjectId = await CreateProjectAsync(nodeB.Client, Path.Combine(nodeB.Root, "mac-repo"), "mac-repo");

        // Controller A chat behavior: exercises peer_list_nodes -> peer_delegate_task -> peer_wait_tasks (early wake on awaiting_approval) -> peer_read_task -> peer_wait_tasks (completed)
        var controllerChat = new ScriptedNodeChatRouter("跨机协同构建验证已圆满完成。", async (_, messages, options) =>
        {
            if (options?.Tools is not null)
            {
                foreach (var t in options.Tools)
                {
                    observedControllerToolNames.Add(t.Name);
                }
            }

            var lastCompleted = TryExtractLatestCompletedToolCall(messages);
            if (lastCompleted is null)
            {
                return AssistantCalls(Call("ca-list-1", "peer_list_nodes", new { }));
            }

            var (lastCallId, lastResultJson) = lastCompleted.Value;
            if (lastCallId == "ca-list-1")
            {
                using var listDoc = JsonDocument.Parse(lastResultJson);
                var nodes = listDoc.RootElement.GetProperty("nodes");
                Assert.Equal(1, nodes.GetArrayLength());
                var discoveredDeviceId = nodes[0].GetProperty("device_id").GetString()!;
                var discoveredProjId = nodes[0].GetProperty("workspaces")[0].GetProperty("remote_project_id").GetString()!;

                return AssistantCalls(
                    Call("ca-del-2", "peer_delegate_task", new
                    {
                        remote_device_id = discoveredDeviceId,
                        remote_project_id = discoveredProjId,
                        goal = "Run build verification and write src/build-report.txt",
                        branch_or_commit = "main",
                        write_scope = new[] { "src/build-report.txt" },
                        use_provider_proxy = true,
                        teammate_handle = "mac-builder"
                    }));
            }

            if (lastCallId == "ca-del-2")
            {
                using var delDoc = JsonDocument.Parse(lastResultJson);
                capturedDelegationId = delDoc.RootElement.GetProperty("delegation").GetProperty("delegation_id").GetString()!;

                // Wait for remote task: must wake early as soon as Worker B hits awaiting_approval!
                return AssistantCalls(Call("ca-wait-3", "peer_wait_tasks", new
                {
                    delegation_ids = new[] { capturedDelegationId },
                    timeout_seconds = 25
                }));
            }

            if (lastCallId == "ca-wait-3")
            {
                using var waitDoc = JsonDocument.Parse(lastResultJson);
                var isAwaiting = waitDoc.RootElement.GetProperty("awaiting_approval").GetBoolean();
                var wokenReason = waitDoc.RootElement.GetProperty("woken_reason").GetString();
                if (!isAwaiting || wokenReason != "awaiting_approval")
                {
                    remoteApprovalReadyForHuman.TrySetException(new InvalidOperationException(
                        $"ca-wait-3 peer_wait_tasks unexpected result: {lastResultJson}"));
                    throw new InvalidOperationException($"ca-wait-3 peer_wait_tasks unexpected result: {lastResultJson}");
                }

                // Also call peer_read_task explicitly to verify live approval card inspection!
                return AssistantCalls(Call("ca-read-4", "peer_read_task", new
                {
                    delegation_id = capturedDelegationId!
                }));
            }

            if (lastCallId == "ca-read-4")
            {
                using var readDoc = JsonDocument.Parse(lastResultJson);
                if (!readDoc.RootElement.TryGetProperty("task", out var taskEl))
                {
                    throw new InvalidOperationException($"ca-read-4 peer_read_task missing 'task' property: {lastResultJson}");
                }
                Assert.True(taskEl.GetProperty("awaiting_approval").GetBoolean());
                var approvalId = Guid.Parse(taskEl.GetProperty("pending_approvals")[0].GetProperty("approval_id").GetString()!);

                remoteApprovalReadyForHuman.TrySetResult((capturedDelegationId!, approvalId));
                await humanApprovedSignal.Task.WaitAsync(TimeSpan.FromSeconds(25));

                return AssistantCalls(Call("ca-wait-5", "peer_wait_tasks", new
                {
                    delegation_ids = new[] { capturedDelegationId! },
                    timeout_seconds = 25
                }));
            }

            Assert.Contains("WORKER_B_BUILD_GREEN_42", lastResultJson, StringComparison.Ordinal);
            return new ChatMessage(ChatRole.Assistant, "主控端已收到远端 Mac Build Rig 的验证结果：WORKER_B_BUILD_GREEN_42。");
        });

        var nodeA = await CreateNodeAsync("node-a", "Dev Workstation", cluster, controllerChat, new GatedFakeToolProvider(), tokenIssuer);

        // Pair A -> B with role='operator', allow_agent_delegation=true, allow_provider_proxy=true
        var pairedBOnA = await PairControllerToWorkerAsync(
            nodeA,
            nodeB,
            cluster,
            role: "operator",
            allowAgentDelegation: true,
            allowProviderProxy: true);
        Assert.NotEmpty(pairedBOnA.DeviceId);

        // Create workspace & session on Controller A (use 'ask' on Worker B via default delegation permission_mode='ask'? Wait: default session permission_mode in TinadecCore is 'default' which asks on mutations!)
        var ctrlProjectId = await CreateProjectAsync(nodeA.Client, Path.Combine(nodeA.Root, "ctrl-repo"), "ctrl-repo");
        var ctrlSessionId = await CreateSoloSessionAsync(nodeA, ctrlProjectId, "cross-device-e2e");

        var interactRes = await nodeA.Client.PostAsJsonAsync($"/api/v1/sessions/{ctrlSessionId}/interactions", new
        {
            content = "派发跨机构建任务到 Mac Build Rig 并等待完工",
            client_message_id = "w3-e2e-msg-1",
            permission_mode = "full-access"
        });
        var interactBodyText = await interactRes.Content.ReadAsStringAsync();
        Assert.True(
            interactRes.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created on Controller A interaction, got {(int)interactRes.StatusCode} ({interactRes.StatusCode}): {interactBodyText}");
        var runId = JsonDocument.Parse(interactBodyText).RootElement.GetProperty("run_id").GetGuid();

        // Wait until Coordinator A observes Worker B's awaiting_approval card via peer_wait_tasks + peer_read_task!
        var (delegationId, remoteApprovalId) = await WaitForRemoteApprovalOrDumpFailureAsync(
            nodeA,
            nodeB,
            ctrlSessionId,
            runId,
            remoteApprovalReadyForHuman.Task,
            TimeSpan.FromSeconds(30));

        // 1. Verify all 5 peer_* tools were registered and advertised in the run tool surface!
        foreach (var expectedTool in PeerDelegationTools.ToolIds)
        {
            Assert.Contains(expectedTool, observedControllerToolNames, StringComparer.OrdinalIgnoreCase);
        }

        // 2. Verify ITeammateControlPort.ListTeammatesAsync (/api/v1/runs/{runId}/teammates) projects runtime_badge="peer:Mac Build Rig" & awaiting_approval=true!
        var teammatesWhileWaiting = await nodeA.Client.GetFromJsonAsync<TeammateListResponseDto>(
            $"/api/v1/runs/{runId}/teammates",
            SnakeJson);
        Assert.NotNull(teammatesWhileWaiting);
        var peerTeammate = Assert.Single(teammatesWhileWaiting!.Teammates, t => t.Handle == "mac-builder");
        Assert.Equal("peer:Mac Build Rig", peerTeammate.RuntimeBadge);
        Assert.Equal("peer", peerTeammate.Mode);
        Assert.Equal("awaiting_approval", peerTeammate.Status);
        Assert.True(peerTeammate.AwaitingApproval);
        Assert.Contains("src/build-report.txt", peerTeammate.WriteScope);
        Assert.Equal(1, peerTeammate.RelayDepth);

        // 3. Verify proxy_session_token was issued on Controller A and bound in memory on Worker B (with ZERO secret leakage in Worker B SQLite!)
        Assert.Equal(1, tokenIssuer.IssuedCount);
        var linkRowBeforeApprove = await GetDelegationLinkRowAsync(nodeA, delegationId);
        Assert.Equal(1, linkRowBeforeApprove.ProviderProxyEnabled);
        Assert.Equal("awaiting_approval", linkRowBeforeApprove.Status);
        Assert.True(PeerProviderProxyRuntimeRegistry.TryGetBySession(linkRowBeforeApprove.RemoteSessionId, out var proxyBinding));
        Assert.NotNull(proxyBinding);
        Assert.Equal(tokenIssuer.LastToken, proxyBinding!.ProxySessionToken);
        Assert.Equal(nodeA.LocalDeviceId, proxyBinding.ControllerDeviceId);

        // 4. Approve the remote approval from Controller A via delegation approval forwarding endpoint!
        var approveResp = await nodeA.Client.PostAsJsonAsync(
            $"/api/v1/peers/delegations/{delegationId}/approvals/{remoteApprovalId}/decision",
            new { approve = true, reason = "Verified safe build report path from Dev Workstation" });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        var approveResult = await approveResp.Content.ReadFromJsonAsync<PeerRemoteApprovalDecisionResultDto>(SnakeJson);
        Assert.NotNull(approveResult);
        Assert.True(approveResult!.Approved);
        Assert.Equal(nodeA.LocalDeviceId, approveResult.DecidedByDeviceId);

        // Confirm Worker B's authoritative /api/v1/approvals/{id} permanently audited decided_by_device_id = Controller A!
        var workerApprovalAudit = await nodeB.Client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{remoteApprovalId}");
        Assert.Equal(nodeA.LocalDeviceId, workerApprovalAudit.GetProperty("decided_by_device_id").GetString());
        Assert.Equal("Dev Workstation", workerApprovalAudit.GetProperty("decided_by_device_name").GetString());

        humanApprovedSignal.TrySetResult();

        // 5. Wait for Worker B to execute write_file and Controller A's run to complete cleanly!
        await workerWriteExecuted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForRunTerminalAsync(nodeA, runId, TimeSpan.FromSeconds(30), expectedStatus: "completed");

        // 6. Verify final peer_delegation_links row, ListTeammatesAsync settled snapshot, and run observability events!
        var finalLinkRow = await GetDelegationLinkRowAsync(nodeA, delegationId);
        if (finalLinkRow.Status != "completed")
        {
            var dbgEventsA = await nodeA.Factory.Services.GetRequiredService<StorageLifecycleService>()
                .ReplayRunEventsAsync(ctrlSessionId, runId, CancellationToken.None);
            var dbgStateB = await nodeB.Factory.Services.GetRequiredService<ILifecycleManager>()
                .GetRunStateAsync(finalLinkRow.RemoteRunId!, CancellationToken.None);
            using var dbgProbe = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/peers/delegations/{finalLinkRow.RemoteSessionId}/status?run_id={finalLinkRow.RemoteRunId}");
            dbgProbe.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, nodeA.LocalDeviceId);
            dbgProbe.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, nodeA.DisplayName);
            dbgProbe.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, "operator");
            using var dbgResp = await nodeB.Client.SendAsync(dbgProbe);
            var dbgBody = await dbgResp.Content.ReadAsStringAsync();
            Assert.Fail(
                $"Expected finalLinkRow.Status='completed', got '{finalLinkRow.Status}' (sum='{finalLinkRow.SummaryText}'). " +
                $"WorkerB={dbgStateB.Status} ('{dbgStateB.Summary}'), probe={(int)dbgResp.StatusCode}:{dbgBody}, " +
                $"CtrlA events=[{string.Join(" | ", dbgEventsA.Select(e => $"{e.EventType}:{JsonSerializer.Serialize(e.Payload)}"))}]");
        }
        Assert.Equal("completed", finalLinkRow.Status);
        Assert.Equal(1, finalLinkRow.HopDepth);
        Assert.Contains("WORKER_B_BUILD_GREEN_42", finalLinkRow.SummaryText ?? string.Empty, StringComparison.Ordinal);

        var finalTeammates = await nodeA.Client.GetFromJsonAsync<TeammateListResponseDto>(
            $"/api/v1/runs/{runId}/teammates",
            SnakeJson);
        var settledPeer = Assert.Single(finalTeammates!.Teammates, t => t.Handle == "mac-builder");
        Assert.Equal("peer:Mac Build Rig", settledPeer.RuntimeBadge);
        Assert.Equal("terminated", settledPeer.Status);
        Assert.False(settledPeer.AwaitingApproval);
        Assert.Contains("WORKER_B_BUILD_GREEN_42", settledPeer.LatestSummary ?? string.Empty, StringComparison.Ordinal);

        var lifecycle = nodeA.Factory.Services.GetRequiredService<StorageLifecycleService>();
        var events = await lifecycle.ReplayRunEventsAsync(ctrlSessionId, runId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "peer.task_delegated");
        Assert.Contains(events, e => e.EventType == "peer.task_awaiting_approval");
        Assert.Contains(events, e => e.EventType == "peer.task_completed");
    }

    [Fact]
    public async Task MaxDelegationHopDepthTwo_AllowsTwoHops_AndHardBlocksHopThreeAndLoopsAtBothEnds()
    {
        var cluster = new InMemoryPeerCluster();
        var dummyChat = new ScriptedNodeChatRouter("done", (_, _, _) =>
            Task.FromResult(new ChatMessage(ChatRole.Assistant, "Hop completed.")));

        var nodeA = await CreateNodeAsync("hop-a", "Node A", cluster, dummyChat, new GatedFakeToolProvider());
        var nodeB = await CreateNodeAsync("hop-b", "Node B", cluster, dummyChat, new GatedFakeToolProvider());
        var nodeC = await CreateNodeAsync("hop-c", "Node C", cluster, dummyChat, new GatedFakeToolProvider());
        var nodeD = await CreateNodeAsync("hop-d", "Node D", cluster, dummyChat, new GatedFakeToolProvider());

        var projB = await CreateProjectAsync(nodeB.Client, Path.Combine(nodeB.Root, "ws-b"), "ws-b");
        var projC = await CreateProjectAsync(nodeC.Client, Path.Combine(nodeC.Root, "ws-c"), "ws-c");
        var projD = await CreateProjectAsync(nodeD.Client, Path.Combine(nodeD.Root, "ws-d"), "ws-d");

        var bOnA = await PairControllerToWorkerAsync(nodeA, nodeB, cluster, role: "operator", allowAgentDelegation: true, allowProviderProxy: false);
        var cOnB = await PairControllerToWorkerAsync(nodeB, nodeC, cluster, role: "operator", allowAgentDelegation: true, allowProviderProxy: false);
        var dOnC = await PairControllerToWorkerAsync(nodeC, nodeD, cluster, role: "operator", allowAgentDelegation: true, allowProviderProxy: false);

        // Hop 1: A (root hop=0) -> B (hop=1) succeeds!
        var coordA = nodeA.Factory.Services.GetRequiredService<IPeerDelegationCoordinator>();
        var hop1Link = await coordA.DelegateTaskAsync(new PeerDelegateTaskCommand
        {
            ParentRunId = Guid.NewGuid(),
            ParentSessionId = Guid.NewGuid(),
            RemoteDeviceId = bOnA.DeviceId,
            RemoteProjectId = projB.ToString(),
            Goal = "Hop 1 task on B"
        });
        Assert.Equal(1, hop1Link.HopDepth);
        Assert.Equal("peer:Node B", hop1Link.RuntimeBadge);

        // Hop 2: B (delegated session hop=1) -> C (hop=2) succeeds!
        var coordB = nodeB.Factory.Services.GetRequiredService<IPeerDelegationCoordinator>();
        var bSessionGuid = Guid.Parse(hop1Link.RemoteSessionId);
        var bRunGuid = Guid.Parse(hop1Link.RemoteRunId!);
        Assert.Equal(1, coordB.ResolveCurrentHopDepth(bSessionGuid, bRunGuid));

        var hop2Link = await coordB.DelegateTaskAsync(new PeerDelegateTaskCommand
        {
            ParentRunId = bRunGuid,
            ParentSessionId = bSessionGuid,
            RemoteDeviceId = cOnB.DeviceId,
            RemoteProjectId = projC.ToString(),
            Goal = "Hop 2 task on C"
        });
        Assert.Equal(2, hop2Link.HopDepth);
        Assert.Equal("peer:Node C", hop2Link.RuntimeBadge);

        // Hop 3 (Local Outbound Hard Gate): C (delegated session hop=2) -> D (nextHop=3 > MAX_DELEGATION_HOP_DEPTH=2) MUST be rejected locally!
        var coordC = nodeC.Factory.Services.GetRequiredService<IPeerDelegationCoordinator>();
        var cSessionGuid = Guid.Parse(hop2Link.RemoteSessionId);
        var cRunGuid = Guid.Parse(hop2Link.RemoteRunId!);
        Assert.Equal(2, coordC.ResolveCurrentHopDepth(cSessionGuid, cRunGuid));

        var sessionsOnDBefore = await nodeD.Factory.Services.GetRequiredService<ProjectSessionStore>().ListSessionsAsync(projD);
        Assert.Empty(sessionsOnDBefore);

        var localHopEx = await Assert.ThrowsAsync<PeerException>(() => coordC.DelegateTaskAsync(new PeerDelegateTaskCommand
        {
            ParentRunId = cRunGuid,
            ParentSessionId = cSessionGuid,
            RemoteDeviceId = dOnC.DeviceId,
            RemoteProjectId = projD.ToString(),
            Goal = "Illegal hop 3 task on D"
        }));
        Assert.Equal(403, localHopEx.StatusCode);
        Assert.Equal(PeerErrorCodes.DelegationHopLimitExceeded, localHopEx.Code);

        // Confirm ZERO sessions were created on Node D!
        var sessionsOnDAfter = await nodeD.Factory.Services.GetRequiredService<ProjectSessionStore>().ListSessionsAsync(projD);
        Assert.Empty(sessionsOnDAfter);

        // Hop 3 & Cycle (Remote Inbound Hard Gate): tampered direct inbound POST /api/v1/peers/delegate with hop_depth=3 or cycle MUST be rejected 403 delegation_hop_limit_exceeded!
        var sessionsOnBBeforeTamper = await nodeB.Factory.Services.GetRequiredService<ProjectSessionStore>().ListSessionsAsync(projB);
        using (var tamperedHopReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers/delegate")
        {
            Content = JsonContent.Create(new
            {
                delegation_id = "tampered-hop-3",
                remote_project_id = projB.ToString(),
                goal = "Tampered hop_depth=3",
                hop_depth = 3,
                hop_chain = new[] { nodeA.LocalDeviceId }
            })
        })
        {
            tamperedHopReq.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, nodeA.LocalDeviceId);
            tamperedHopReq.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, "Node A");
            tamperedHopReq.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, "operator");

            var tamperedResp = await nodeB.Client.SendAsync(tamperedHopReq);
            Assert.Equal(HttpStatusCode.Forbidden, tamperedResp.StatusCode);
            var errJson = await tamperedResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(PeerErrorCodes.DelegationHopLimitExceeded, errJson.GetProperty("code").GetString());
        }

        using (var cycleReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers/delegate")
        {
            Content = JsonContent.Create(new
            {
                delegation_id = "tampered-cycle",
                remote_project_id = projB.ToString(),
                goal = "Cycle back to B",
                hop_depth = 2,
                hop_chain = new[] { nodeB.LocalDeviceId, nodeA.LocalDeviceId }
            })
        })
        {
            cycleReq.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, nodeA.LocalDeviceId);
            cycleReq.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, "Node A");
            cycleReq.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, "operator");

            var cycleResp = await nodeB.Client.SendAsync(cycleReq);
            Assert.Equal(HttpStatusCode.Forbidden, cycleResp.StatusCode);
            var cycleJson = await cycleResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(PeerErrorCodes.DelegationHopLimitExceeded, cycleJson.GetProperty("code").GetString());
        }

        var sessionsOnBAfterTamper = await nodeB.Factory.Services.GetRequiredService<ProjectSessionStore>().ListSessionsAsync(projB);
        Assert.Equal(sessionsOnBBeforeTamper.Count, sessionsOnBAfterTamper.Count);
    }

    [Fact]
    public async Task PolicyEnforcement_DeniesWhenDelegationDisabledOrObserver_AllowsOwner_AndCancelTaskStopsRemoteRun()
    {
        var cluster = new InMemoryPeerCluster();
        var workerStartedSlowTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSlowWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slowWorkerTools = new GatedFakeToolProvider
        {
            OnCall = async req =>
            {
                if (string.Equals(req.ToolId, "read_file", StringComparison.OrdinalIgnoreCase))
                {
                    workerStartedSlowTool.TrySetResult();
                    await holdSlowWorker.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        };

        var workerChat = new ScriptedNodeChatRouter("slow finished", (_, messages, _) =>
        {
            if (TryExtractLatestCompletedToolCall(messages) is null)
            {
                return Task.FromResult(AssistantCalls(Call("slow-r1", "read_file", new { filepath = "README.md" })));
            }
            return Task.FromResult(new ChatMessage(ChatRole.Assistant, "slow finished"));
        });

        var nodeA = await CreateNodeAsync("pol-a", "Controller Rig", cluster, new ScriptedNodeChatRouter("ok", (_, _, _) => Task.FromResult(new ChatMessage(ChatRole.Assistant, "ok"))), new GatedFakeToolProvider());
        var nodeB = await CreateNodeAsync("pol-b", "Worker Linux", cluster, workerChat, slowWorkerTools);
        var projB = await CreateProjectAsync(nodeB.Client, Path.Combine(nodeB.Root, "linux-ws"), "linux-ws");

        // 1. Pair with allow_agent_delegation = FALSE (default operator) -> must deny 403 policy_denied & omit from peer_list_nodes!
        var pairedB = await PairControllerToWorkerAsync(
            nodeA,
            nodeB,
            cluster,
            role: "operator",
            allowAgentDelegation: false,
            allowProviderProxy: false);

        var coordA = nodeA.Factory.Services.GetRequiredService<IPeerDelegationCoordinator>();
        var listedWhenDisabled = await coordA.ListNodesAsync();
        Assert.Empty(listedWhenDisabled.Nodes);

        var deniedEx = await Assert.ThrowsAsync<PeerException>(() => coordA.DelegateTaskAsync(new PeerDelegateTaskCommand
        {
            ParentRunId = Guid.NewGuid(),
            ParentSessionId = Guid.NewGuid(),
            RemoteDeviceId = pairedB.DeviceId,
            RemoteProjectId = projB.ToString(),
            Goal = "Should be blocked by allow_agent_delegation=0"
        }));
        Assert.Equal(403, deniedEx.StatusCode);
        Assert.Equal(PeerErrorCodes.PolicyDenied, deniedEx.Code);

        // 2. Observer role even with allow_agent_delegation=true -> must still deny 403 policy_denied!
        await UpdateWorkerGrantPolicyAsync(nodeB, nodeA.LocalDeviceId, role: "observer", allowAgentDelegation: true, allowProviderProxy: false);
        cluster.SetPeerRole(nodeA.LocalDeviceId, nodeB.LocalDeviceId, "observer");

        var observerEx = await Assert.ThrowsAsync<PeerException>(() => coordA.DelegateTaskAsync(new PeerDelegateTaskCommand
        {
            ParentRunId = Guid.NewGuid(),
            ParentSessionId = Guid.NewGuid(),
            RemoteDeviceId = pairedB.DeviceId,
            RemoteProjectId = projB.ToString(),
            Goal = "Observer cannot delegate"
        }));
        Assert.Equal(403, observerEx.StatusCode);
        Assert.Equal(PeerErrorCodes.PolicyDenied, observerEx.Code);

        // 3. Owner role (even with allow_agent_delegation=false) -> ALLOWED per §5.2 permission matrix!
        await UpdateWorkerGrantPolicyAsync(nodeB, nodeA.LocalDeviceId, role: "owner", allowAgentDelegation: false, allowProviderProxy: false);
        cluster.SetPeerRole(nodeA.LocalDeviceId, nodeB.LocalDeviceId, "owner");

        var listedForOwner = await coordA.ListNodesAsync();
        var ownerNode = Assert.Single(listedForOwner.Nodes);
        Assert.Equal("Worker Linux", ownerNode.DisplayName);
        Assert.Contains(ownerNode.Platform, new[] { "darwin", "linux", "win32" });

        // Delegate long-running task and cancel it via CancelTaskAsync (peer_cancel_task)!
        var delegated = await coordA.DelegateTaskAsync(new PeerDelegateTaskCommand
        {
            ParentRunId = Guid.NewGuid(),
            ParentSessionId = Guid.NewGuid(),
            RemoteDeviceId = pairedB.DeviceId,
            RemoteProjectId = projB.ToString(),
            Goal = "Long running inspection to cancel",
            TeammateHandle = "linux-runner",
            PermissionMode = "full-access"
        });
        Assert.Equal("running", delegated.Status);

        await workerStartedSlowTool.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var cancelled = await coordA.CancelTaskAsync(delegated.DelegationId, "Superseded by newer commit");
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal("Superseded by newer commit", cancelled.SummaryText);

        var persistedCancelled = await GetDelegationLinkRowAsync(nodeA, delegated.DelegationId);
        Assert.Equal("cancelled", persistedCancelled.Status);

        var remoteRunState = await nodeB.Factory.Services
            .GetRequiredService<ILifecycleManager>()
            .GetRunStateAsync(delegated.RemoteRunId!, CancellationToken.None);
        Assert.Equal(RunStatus.Cancelled, remoteRunState.Status);

        holdSlowWorker.TrySetResult();
    }

    // =========================================================================
    // Multi-Node In-Memory Test Infrastructure
    // =========================================================================

    private sealed record TestPeerNode(
        string Key,
        string DisplayName,
        string Root,
        string LocalDeviceId,
        string LocalFingerprint,
        PeerNodeHostFactory Factory,
        HttpClient Client);

    private async Task<TestPeerNode> CreateNodeAsync(
        string key,
        string displayName,
        InMemoryPeerCluster cluster,
        IAgentChatClientFactory chatFactory,
        IToolProvider toolProvider,
        IPeerProviderProxyTokenIssuer? tokenIssuer = null)
    {
        var nodeRoot = Path.Combine(_root, key);
        Directory.CreateDirectory(nodeRoot);

        var loopbackTransport = new ClusterPeerTransportClient(cluster, key);
        var factory = new PeerNodeHostFactory(nodeRoot, chatFactory, toolProvider, loopbackTransport, tokenIssuer);
        _disposables.Add(factory);

        var client = factory.CreateClient();
        _disposables.Add(client);

        await InstallGraphSeedPackAsync(client);

        var inboundResp = await client.PutAsJsonAsync("/api/v1/peers/inbound", new
        {
            enabled = true,
            display_name = displayName,
            listen_port = 48735
        });
        Assert.Equal(HttpStatusCode.OK, inboundResp.StatusCode);
        var inbound = (await inboundResp.Content.ReadFromJsonAsync<PeerInboundSettingsDto>())!;

        var node = new TestPeerNode(
            key,
            displayName,
            nodeRoot,
            inbound.LocalDeviceId,
            inbound.LocalFingerprint,
            factory,
            client);
        cluster.RegisterNode(node);
        return node;
    }

    private static async Task<PeerDeviceDto> PairControllerToWorkerAsync(
        TestPeerNode controller,
        TestPeerNode worker,
        InMemoryPeerCluster cluster,
        string role,
        bool allowAgentDelegation,
        bool allowProviderProxy)
    {
        var codeResp = await worker.Client.PostAsJsonAsync("/api/v1/peers/pairing-code", new { ttl_seconds = 300 });
        Assert.Equal(HttpStatusCode.OK, codeResp.StatusCode);
        var codeDto = (await codeResp.Content.ReadFromJsonAsync<PeerPairingCodeStatusDto>())!;

        using (var verifyReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify")
        {
            Content = JsonContent.Create(new
            {
                transport = "lan",
                controller_device_id = controller.LocalDeviceId,
                display_name = controller.DisplayName,
                remote_fingerprint = controller.LocalFingerprint,
                protocol_major = 1,
                protocol_minor = 0,
                pairing_mode = "code",
                pairing_code = codeDto.PairingCode,
                expected_remote_fingerprint = worker.LocalFingerprint
            })
        })
        {
            verifyReq.Headers.Add(PeerDeviceEndpoints.PrincipalHeader, PeerDeviceEndpoints.GatewayInternalPrincipal);
            var verifyResp = await worker.Client.SendAsync(verifyReq);
            Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        }

        await UpdateWorkerGrantPolicyAsync(worker, controller.LocalDeviceId, role, allowAgentDelegation, allowProviderProxy);

        var pairResp = await controller.Client.PostAsJsonAsync("/api/v1/peers/devices/pair", new
        {
            remote_device_id = worker.LocalDeviceId,
            display_name = worker.DisplayName,
            transport = "lan",
            address = "127.0.0.1:48735",
            certificate_fingerprint = worker.LocalFingerprint,
            expected_fingerprint = worker.LocalFingerprint,
            protocol_major = 1,
            protocol_minor = 0,
            last_state = "ready"
        });
        Assert.Equal(HttpStatusCode.OK, pairResp.StatusCode);
        var paired = (await pairResp.Content.ReadFromJsonAsync<PeerDeviceDto>())!;

        cluster.MapDeviceRoute(controller.Key, paired.DeviceId, worker.Key, role);
        return paired;
    }

    private static async Task UpdateWorkerGrantPolicyAsync(
        TestPeerNode worker,
        string controllerDeviceId,
        string role,
        bool allowAgentDelegation,
        bool allowProviderProxy)
    {
        var policyResp = await worker.Client.PutAsJsonAsync($"/api/v1/peers/grants/{controllerDeviceId}/policy", new
        {
            role,
            workspace_mode = "all",
            sync_all_sessions = true,
            allow_agent_delegation = allowAgentDelegation,
            allow_provider_proxy = allowProviderProxy
        });
        Assert.Equal(HttpStatusCode.OK, policyResp.StatusCode);
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string path, string name)
    {
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), $"# {name}\n");
        var resp = await client.PostAsJsonAsync("/api/v1/projects", new { name, path });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateSoloSessionAsync(TestPeerNode node, Guid projectId, string title)
    {
        await using var cfg = await node.Factory.Services
            .GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>()
            .CreateDbContextAsync();
        var freeDirectorIds = (await cfg.AgentModes.AsNoTracking()
            .Where(m => m.Slug == "free_director" && m.Status == "published")
            .Select(m => m.Id)
            .ToListAsync()).ToHashSet();
        var publishedVersions = await cfg.ModeVersions.AsNoTracking()
            .Where(v => v.Status == "published" && freeDirectorIds.Contains(v.AgentModeId))
            .ToListAsync();
        var publishedVersion = publishedVersions.OrderByDescending(v => v.Version).First();

        var sessResp = await node.Client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = projectId.ToString(),
            title,
            mode_version_id = publishedVersion.Id
        });
        Assert.Equal(HttpStatusCode.Created, sessResp.StatusCode);
        return (await sessResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(string DelegationId, Guid ApprovalId)> WaitForRemoteApprovalOrDumpFailureAsync(
        TestPeerNode nodeA,
        TestPeerNode nodeB,
        Guid ctrlSessionId,
        Guid ctrlRunId,
        Task<(string DelegationId, Guid ApprovalId)> approvalTask,
        TimeSpan timeout)
    {
        var lifecycleA = nodeA.Factory.Services.GetRequiredService<ILifecycleManager>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (approvalTask.IsCompletedSuccessfully)
            {
                return await approvalTask.ConfigureAwait(false);
            }

            var stateA = await lifecycleA.GetRunStateAsync(ctrlRunId.ToString(), CancellationToken.None);
            if (stateA.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled || approvalTask.IsFaulted)
            {
                var dbFactoryEarly = nodeA.Factory.Services.GetRequiredService<IDbContextFactory<PeerDbContext>>();
                await using var dbEarly = await dbFactoryEarly.CreateDbContextAsync();
                var linksEarly = await dbEarly.DelegationLinks.AsNoTracking().ToListAsync();
                string wDiag = "none";
                if (linksEarly.FirstOrDefault() is { RemoteRunId: { Length: > 0 } rRunId, RemoteSessionId: { Length: > 0 } rSessId })
                {
                    var stB = await nodeB.Factory.Services.GetRequiredService<ILifecycleManager>().GetRunStateAsync(rRunId, CancellationToken.None);
                    var evB = await nodeB.Factory.Services.GetRequiredService<StorageLifecycleService>()
                        .ReplayRunEventsAsync(Guid.Parse(rSessId), Guid.Parse(rRunId), CancellationToken.None);
                    await using var ldbB = await nodeB.Factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
                    var appRowsB = await ldbB.ApprovalRequests.AsNoTracking().ToListAsync();
                    using var directProbe = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/peers/delegations/{rSessId}/status?run_id={rRunId}");
                    directProbe.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, nodeA.LocalDeviceId);
                    directProbe.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, nodeA.DisplayName);
                    directProbe.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, "operator");
                    using var probeResp = await nodeB.Client.SendAsync(directProbe);
                    var probeBody = await probeResp.Content.ReadAsStringAsync();
                    wDiag = $"WorkerB status={stB.Status}, sum='{stB.Summary}', appRows={appRowsB.Count}, probe={(int)probeResp.StatusCode}:{probeBody}, evs=[{string.Join(" | ", evB.Select(e => $"{e.EventType}:{JsonSerializer.Serialize(e.Payload)}"))}]";
                }
                var innerMsg = approvalTask.IsFaulted ? approvalTask.Exception?.GetBaseException().Message : "none";
                throw new InvalidOperationException(
                    $"Controller A failed ({innerMsg}) status={stateA.Status}, summary='{stateA.Summary}'. {wDiag}");
            }

            await Task.Delay(100);
        }

        var finalStateA = await lifecycleA.GetRunStateAsync(ctrlRunId.ToString(), CancellationToken.None);
        var slFinalA = nodeA.Factory.Services.GetRequiredService<StorageLifecycleService>();
        var finalEvsA = await slFinalA.ReplayRunEventsAsync(ctrlSessionId, ctrlRunId, CancellationToken.None);
        var finalDigestA = string.Join(" | ", finalEvsA.TakeLast(15).Select(e => $"{e.EventType}"));

        var dbFactoryA = nodeA.Factory.Services.GetRequiredService<IDbContextFactory<PeerDbContext>>();
        await using var dbA = await dbFactoryA.CreateDbContextAsync();
        var linksA = await dbA.DelegationLinks.AsNoTracking().ToListAsync();
        var linksDigest = string.Join("; ", linksA.Select(l => $"{l.DelegationId}:{l.Status}:sess={l.RemoteSessionId}:run={l.RemoteRunId}:sum={l.SummaryText}"));

        string workerDiag = "none";
        if (linksA.FirstOrDefault() is { RemoteRunId: { Length: > 0 } remRunId, RemoteSessionId: { Length: > 0 } remSessId })
        {
            var stateB = await nodeB.Factory.Services.GetRequiredService<ILifecycleManager>().GetRunStateAsync(remRunId, CancellationToken.None);
            var evsB = await nodeB.Factory.Services.GetRequiredService<StorageLifecycleService>()
                .ReplayRunEventsAsync(Guid.Parse(remSessId), Guid.Parse(remRunId), CancellationToken.None);
            workerDiag = $"WorkerB status={stateB.Status}, sum='{stateB.Summary}', evs=[{string.Join(", ", evsB.TakeLast(15).Select(e => e.EventType))}]";
        }

        throw new TimeoutException(
            $"Timed out waiting for remote approval. CtrlA status={finalStateA.Status}, sum='{finalStateA.Summary}', evs=[{finalDigestA}], links=[{linksDigest}], {workerDiag}");
    }

    private static async Task<PeerDelegationLinkRecord> GetDelegationLinkRowAsync(TestPeerNode node, string delegationId)
    {
        var dbFactory = node.Factory.Services.GetRequiredService<IDbContextFactory<PeerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.DelegationLinks.AsNoTracking().SingleAsync(x => x.DelegationId == delegationId);
    }

    private static async Task WaitForRunTerminalAsync(TestPeerNode node, Guid runId, TimeSpan timeout, string expectedStatus)
    {
        var lifecycle = node.Factory.Services.GetRequiredService<ILifecycleManager>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None);
            if (state.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
            {
                Assert.Equal(expectedStatus, state.Status.ToString().ToLowerInvariant());
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Run {runId} on {node.DisplayName} did not reach '{expectedStatus}' within {timeout}.");
    }

    private static (string CallId, string ResultJson)? TryExtractLatestCompletedToolCall(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var fr = messages[i].Contents.OfType<FunctionResultContent>().LastOrDefault();
            if (fr is not null)
            {
                var json = fr.Result switch
                {
                    null => "null",
                    JsonElement je => je.GetRawText(),
                    string s => s,
                    _ => JsonSerializer.Serialize(fr.Result)
                };
                return (fr.CallId ?? string.Empty, json);
            }
        }
        return null;
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

    private static async Task InstallGraphSeedPackAsync(HttpClient client)
    {
        var manifestPath = FindGraphSeedManifestPath();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, Encoding.UTF8))!;
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
        Assert.True(previewResponse.IsSuccessStatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var apply = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent-packs/tinadec.graph.seed-pack")
        {
            Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetGuid(), envelope })
        };
        apply.Headers.TryAddWithoutValidation("Idempotency-Key", $"w3-seed-pack-{Guid.NewGuid():N}");
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

    private sealed class RecordingProxyTokenIssuer : IPeerProviderProxyTokenIssuer
    {
        private int _issuedCount;
        public int IssuedCount => Volatile.Read(ref _issuedCount);
        public string LastToken { get; private set; } = string.Empty;

        public Task<PeerProviderProxyTokenGrantDto> IssueTokenAsync(
            PeerProviderProxyTokenIssueRequestDto request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _issuedCount);
            LastToken = $"ptk_test_{request.DelegationId}_{Guid.NewGuid():N}";
            return Task.FromResult(new PeerProviderProxyTokenGrantDto
            {
                ProxySessionToken = LastToken,
                ExpiresAtMs = request.ExpiresAtMs,
                MaxRequests = request.MaxRequests
            });
        }
    }

    private sealed class InMemoryPeerCluster
    {
        private readonly ConcurrentDictionary<string, TestPeerNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(string FromKey, string LocalDeviceId), (string TargetNodeKey, string Role)> _routes = new();

        public void RegisterNode(TestPeerNode node) => _nodes[node.Key] = node;

        public TestPeerNode GetNode(string key) => _nodes[key];

        public void MapDeviceRoute(string controllerKey, string localPairedDeviceId, string workerKey, string role) =>
            _routes[(controllerKey, localPairedDeviceId)] = (workerKey, role);

        public void SetPeerRole(string controllerLocalDeviceId, string workerLocalDeviceId, string newRole)
        {
            foreach (var kv in _routes.ToArray())
            {
                var ctrlNode = _nodes[kv.Key.FromKey];
                var workerNode = _nodes[kv.Value.TargetNodeKey];
                if (ctrlNode.LocalDeviceId == controllerLocalDeviceId && workerNode.LocalDeviceId == workerLocalDeviceId)
                {
                    _routes[kv.Key] = (kv.Value.TargetNodeKey, newRole);
                }
            }
        }

        public (TestPeerNode Controller, TestPeerNode Worker, string Role) ResolveRoute(string controllerKey, string deviceId)
        {
            var controller = _nodes[controllerKey];
            if (_routes.TryGetValue((controllerKey, deviceId), out var route))
            {
                return (controller, _nodes[route.TargetNodeKey], route.Role);
            }
            throw new PeerException(404, "not_found", $"Unmapped test route from '{controllerKey}' to device '{deviceId}'.");
        }
    }

    private sealed class ClusterPeerTransportClient(InMemoryPeerCluster cluster, string callerNodeKey) : IPeerTransportLoopbackClient
    {
        public async Task<PeerProxyHttpResponse> SendProxyAsync(
            string deviceId,
            HttpMethod method,
            string remoteApiPath,
            string? jsonBody = null,
            IReadOnlyDictionary<string, string>? extraHeaders = null,
            CancellationToken cancellationToken = default)
        {
            var (controller, worker, role) = cluster.ResolveRoute(callerNodeKey, deviceId);
            using var req = new HttpRequestMessage(method, remoteApiPath);
            req.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, controller.LocalDeviceId);
            req.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, controller.DisplayName);
            req.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, role);

            if (extraHeaders is not null)
            {
                foreach (var (k, v) in extraHeaders)
                {
                    req.Headers.TryAddWithoutValidation(k, v);
                }
            }

            if (jsonBody is not null && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var resp = await worker.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new PeerProxyHttpResponse((int)resp.StatusCode, body, resp.Content.Headers.ContentType?.MediaType);
        }

        public async Task<PeerProxyHttpResponse> SendGatewayLocalAsync(
            HttpMethod method,
            string gatewayPath,
            string? jsonBody = null,
            IReadOnlyDictionary<string, string>? extraHeaders = null,
            CancellationToken cancellationToken = default)
        {
            var self = cluster.GetNode(callerNodeKey);
            using var req = new HttpRequestMessage(method, gatewayPath);
            if (gatewayPath.StartsWith("/api/v1/peers/internal/", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Add(PeerDeviceEndpoints.PrincipalHeader, PeerDeviceEndpoints.GatewayInternalPrincipal);
            }
            if (jsonBody is not null && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var resp = await self.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new PeerProxyHttpResponse((int)resp.StatusCode, body, resp.Content.Headers.ContentType?.MediaType);
        }
    }

    private sealed class PeerNodeHostFactory(
        string root,
        IAgentChatClientFactory chatFactory,
        IToolProvider toolProvider,
        IPeerTransportLoopbackClient transportClient,
        IPeerProviderProxyTokenIssuer? tokenIssuer) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            var wsRoot = Path.Combine(root, "default-ws");
            Directory.CreateDirectory(wsRoot);

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                    ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                    ["TinadecTools:DefaultWorkspaceRoot"] = wsRoot,
                    ["Logging:LogLevel:Default"] = "Warning"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IAgentChatClientFactory>(chatFactory));
                services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new TestModelSecretStore()));
                services.Replace(ServiceDescriptor.Singleton<IToolProvider>(toolProvider));
                services.Replace(ServiceDescriptor.Singleton<IPeerTransportLoopbackClient>(transportClient));
                if (tokenIssuer is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<IPeerProviderProxyTokenIssuer>(tokenIssuer));
                }
            });
        }
    }

    private sealed class ScriptedNodeChatRouter(
        string finalMeetingReply,
        Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, Task<ChatMessage>> workerTurnHandler) : IAgentChatClientFactory
    {
        private readonly string _finalMeetingReply = finalMeetingReply;
        private readonly Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, Task<ChatMessage>> _workerTurnHandler = workerTurnHandler;
        private int _workerTurn;

        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution
            {
                IsAvailable = true,
                BaseUrl = "http://localhost",
                Model = "fake-peer-w3",
                ApiKey = "test-local-only",
                ModelId = "openai/fake-peer-w3"
            });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(new Client(this));

        private sealed class Client(ScriptedNodeChatRouter owner) : IChatClient
        {
            private static readonly string[] KnownSuccessCriteria =
            [
                "The goal is satisfied and the answer reports what what actually happened.",
                "The goal is satisfied and the answer reports what actually happened.",
                "Task is complete when the goal is satisfied",
                "自己做的与派出去的工作都在运行证据里可追溯",
                "目标被满足或明确向用户说明阻塞",
                "派发前已通过只读工具核实真实文件与约束",
                "并发写队友持有互不重叠的 write_scope 且经 team_integrate 合并门禁检查",
                "工件通过验证且风险被显式列出",
                "证据带出处且满足边契约字段"
            ];

            public ChatClientMetadata Metadata { get; } = new("peer-w3-fake");
            public void Dispose() { }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var list = messages.ToList();
                var hasPrimaryTools = options?.Tools is { Count: > 0 } tools
                    && tools.Any(t => t.Name is "read_file" or "write_file" or "peer_list_nodes" or "peer_delegate_task");
                if (!hasPrimaryTools)
                {
                    var fullText = (options?.Instructions ?? string.Empty) + "\n" + string.Join("\n", list.Select(m => m.Text));
                    if (fullText.Contains("Execution-layer Task Planner", StringComparison.Ordinal))
                    {
                        if (Volatile.Read(ref owner._workerTurn) > 0
                            || fullText.Contains("本次运行没有可派发的执行者", StringComparison.Ordinal))
                        {
                            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]"));
                        }

                        var assignee = fullText.Contains("- global_engineering:", StringComparison.Ordinal)
                            ? "global_engineering"
                            : fullText.Contains("- worker.general:", StringComparison.Ordinal)
                                ? "worker.general"
                                : fullText.Contains("- search:", StringComparison.Ordinal)
                                    ? "search"
                                    : null;
                        if (assignee is null)
                        {
                            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]"));
                        }

                        var planJson = JsonSerializer.Serialize(new[]
                        {
                            new
                            {
                                task_key = "delegated-task",
                                assignee,
                                title = "Execute delegated task",
                                description = "Execute the delegated workspace goal and report verified evidence.",
                                success_criteria = new[] { "Task is complete when the goal is satisfied" },
                                dependencies = Array.Empty<string>(),
                                required_capabilities = Array.Empty<string>(),
                                required_tools = Array.Empty<string>(),
                                priority = 1,
                                risk = "low"
                            }
                        });
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant, planJson));
                    }

                    if (fullText.Contains("revise_task_indexes", StringComparison.Ordinal))
                    {
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                            "{\"decision\":\"pass\",\"reasons\":[\"verified\"],\"revise_task_indexes\":[],\"criteria_verdicts\":[]}"));
                    }

                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, owner._finalMeetingReply));
                }

                var turn = Interlocked.Increment(ref owner._workerTurn);
                var reply = await owner._workerTurnHandler(turn, list, options).ConfigureAwait(false);
                if (!reply.Contents.OfType<FunctionCallContent>().Any()
                    && !string.IsNullOrWhiteSpace(reply.Text)
                    && !reply.Text.Contains(WorkerOutcomeProtocol.Marker, StringComparison.OrdinalIgnoreCase))
                {
                    var fullInput = (options?.Instructions ?? string.Empty) + "\n" + string.Join("\n", list.Select(m => m.Text));
                    var sb = new StringBuilder(reply.Text.TrimEnd());
                    foreach (var criterion in KnownSuccessCriteria)
                    {
                        if (fullInput.Contains(criterion, StringComparison.Ordinal))
                        {
                            sb.Append('\n').Append(WorkerOutcomeProtocol.CriterionMarker).Append(' ').Append(criterion).Append(" || verified and completed");
                        }
                    }
                    sb.Append('\n').Append(WorkerOutcomeProtocol.Marker).Append(" completed");
                    reply = new ChatMessage(ChatRole.Assistant, sb.ToString());
                }
                return new ChatResponse(reply);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var res = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                foreach (var u in res.ToChatResponseUpdates())
                {
                    yield return u;
                }
            }
        }
    }

    private sealed class GatedFakeToolProvider : IToolProvider
    {
        public Func<ToolWireRequestDto, Task>? OnCall { get; init; }

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
            if (OnCall is not null)
            {
                await OnCall(request).ConfigureAwait(false);
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
