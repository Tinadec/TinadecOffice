using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AcpServer;
using TinadecCore.AspNetCore;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests.AcpServer;

/// <summary>
/// Checklist #1–#7:
/// 1) Raw byte-level stdout purity (zero UTF-8 BOM EF BB BF, 100% valid JSON-RPC 2.0 NDJSON under concurrent ILogger &amp; Console.WriteLine noise).
/// 2) Strict ACP v1 wire compliance (camelCase, integer protocolVersion: 1, -32601 unknown method, -32602 invalid params).
/// 3) Full main loop (initialize -&gt; session/new -&gt; session/prompt with text + resource + resource_link + Base64 PNG image; ordered thoughts, normalized tool_call/tool_call_update, plan, deduplicated agent_message_chunk flushed BEFORE stopReason).
/// 4) session/load chronological history replay (user_message_chunk &amp; agent_message_chunk flushed BEFORE response).
/// 5) session/cancel notification (cancels slow streaming turn and unblocks pending session/request_permission).
/// 6) session/set_mode (auto|plan|spec|ask|vibe|agent + current_mode_update) &amp; session/set_model.
/// 7) Bidirectional session/request_permission (allow_once, allow_always command_prefix rule persistence, reject_once, cancelled / invalid optionId fail-closed).
/// </summary>
public sealed class AcpServerProtocolAndModeATests : IAsyncLifetime
{
    // Minimal valid 1x1 transparent PNG
    private const string ValidOnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private string _sandboxRoot = string.Empty;
    private string _dataRoot = string.Empty;
    private string _dbPath = string.Empty;
    private string _workspaceDir = string.Empty;

    public Task InitializeAsync()
    {
        _sandboxRoot = ApiTestStorage.CreateRoot("acp-modea");
        _dataRoot = Path.Combine(_sandboxRoot, "data");
        _dbPath = Path.Combine(_sandboxRoot, "modea.db");
        _workspaceDir = Path.Combine(_sandboxRoot, "project-workspace");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, "README.md"), "# Tinadec ACP Test Workspace\nHello from workspace.\n");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Checklist1_StdoutPurity_ZeroUtf8Bom_AndEveryLineIsValidJsonRpcDespiteLoggerAndConsoleNoise()
    {
        var rig = new ScriptedAcpTestRig { EmitNoiseLogsAndConsoleWrite = true };
        using var pipes = new DuplexAcpPipes();
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var originalOut = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            Console.WriteLine("STARTUP_BANNER_SHOULD_GO_TO_STDERR");
            var runnerTask = StartEmbeddedRunnerAsync(pipes, rig, serverCts.Token);
            await using (var client = new RecordingAcpWireClient(pipes))
            {
                var init = await client.RequestAsync("initialize", new { protocolVersion = 1 });
                Assert.Equal(1, init.GetProperty("protocolVersion").GetInt32());

                var created = await client.RequestAsync("session/new", new { cwd = _workspaceDir, mcpServers = Array.Empty<object>() });
                var sessionId = created.GetProperty("sessionId").GetString()!;

                var promptRes = await client.RequestAsync("session/prompt", new
                {
                    sessionId,
                    prompt = new object[]
                    {
                        new { type = "text", text = "[SCENARIO:FULL_LOOP] Verify pure stdout" }
                    }
                });
                Assert.Equal("end_turn", promptRes.GetProperty("stopReason").GetString());
            }

            pipes.CloseClientStdin();
            var exitCode = await runnerTask.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var rawBytes = pipes.ServerToClientStdout.GetRecordedBytes();
        Assert.NotEmpty(rawBytes);

        // Zero UTF-8 BOM (EF BB BF)
        var hasUtf8Bom = rawBytes.Length >= 3
            && rawBytes[0] == 0xEF
            && rawBytes[1] == 0xBB
            && rawBytes[2] == 0xBF;
        Assert.False(hasUtf8Bom, "ACP stdout must NEVER start with a UTF-8 BOM (EF BB BF).");

        var rawText = Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("STARTUP_BANNER_SHOULD_GO_TO_STDERR", rawText, StringComparison.Ordinal);
        Assert.DoesNotContain("CHAT_CONSOLE_WRITELINE_BANNER", rawText, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL_CONSOLE_WRITELINE_NOISE", rawText, StringComparison.Ordinal);
        Assert.DoesNotContain("Simulated warning", rawText, StringComparison.Ordinal);
        Assert.DoesNotContain("Simulated error", rawText, StringComparison.Ordinal);

        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            using var doc = JsonDocument.Parse(trimmed);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    [Fact]
    public async Task Checklist2_WireCompliance_CamelCase_IntegerProtocolVersion_AndErrorCodes32601And32602()
    {
        var rig = new ScriptedAcpTestRig();
        using var pipes = new DuplexAcpPipes();
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var runnerTask = StartEmbeddedRunnerAsync(pipes, rig, serverCts.Token);

        await using (var client = new RecordingAcpWireClient(pipes))
        {
            // String protocolVersion -> -32602
            var badStrVer = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("initialize", new { protocolVersion = "1" }));
            Assert.Equal(-32602, badStrVer.Code);

            // Unsupported integer protocolVersion -> -32602
            var badNumVer = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("initialize", new { protocolVersion = 99 }));
            Assert.Equal(-32602, badNumVer.Code);

            // Valid initialize -> integer protocolVersion: 1 + camelCase
            var init = await client.RequestAsync("initialize", new { protocolVersion = 1 });
            var pvEl = init.GetProperty("protocolVersion");
            Assert.Equal(JsonValueKind.Number, pvEl.ValueKind);
            Assert.Equal(1, pvEl.GetInt32());

            var caps = init.GetProperty("agentCapabilities");
            Assert.True(caps.GetProperty("loadSession").GetBoolean());
            Assert.True(caps.GetProperty("promptCapabilities").GetProperty("image").GetBoolean());
            Assert.False(caps.GetProperty("sessionCapabilities").GetProperty("fork").GetBoolean());
            Assert.Equal("tinadec", init.GetProperty("agentInfo").GetProperty("name").GetString());

            // Unknown method -> -32601
            var unknownMethod = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("acp/doesNotExist", new { }));
            Assert.Equal(-32601, unknownMethod.Code);

            // Missing, relative, and nonexistent cwd -> -32602
            var missingCwd = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/new", new { }));
            Assert.Equal(-32602, missingCwd.Code);

            var relativeCwd = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/new", new { cwd = "./relative-folder" }));
            Assert.Equal(-32602, relativeCwd.Code);

            var nonexistentCwd = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/new", new { cwd = Path.Combine(_sandboxRoot, "missing-dir-xyz") }));
            Assert.Equal(-32602, nonexistentCwd.Code);

            // Create valid session
            var session = await client.RequestAsync("session/new", new { cwd = _workspaceDir });
            var sessionId = session.GetProperty("sessionId").GetString()!;

            // Invalid sessionId (malformed & unknown GUID) -> -32602
            var malformedSid = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/set_mode", new { sessionId = "not-a-guid", modeId = "ask" }));
            Assert.Equal(-32602, malformedSid.Code);

            var unknownSid = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/set_mode", new { sessionId = Guid.NewGuid().ToString("D"), modeId = "ask" }));
            Assert.Equal(-32602, unknownSid.Code);

            // Invalid modeId & modelId -> -32602
            var badMode = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/set_mode", new { sessionId, modeId = "nonexistent-mode-id" }));
            Assert.Equal(-32602, badMode.Code);

            var badModel = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/set_model", new { sessionId, modelId = "unknown-provider/unknown-model" }));
            Assert.Equal(-32602, badModel.Code);

            // Malformed Base64 image -> -32602
            var badImage = await Assert.ThrowsAsync<AcpRemoteException>(() =>
                client.RequestAsync("session/prompt", new
                {
                    sessionId,
                    prompt = new object[]
                    {
                        new { type = "image", mimeType = "image/png", data = "%%%NOT_VALID_BASE64%%%" }
                    }
                }));
            Assert.Equal(-32602, badImage.Code);
        }

        pipes.CloseClientStdin();
        Assert.Equal(0, await runnerTask.WaitAsync(TimeSpan.FromSeconds(20)));

        // Verify strict camelCase on wire (zero snake_case protocol keys)
        var wireText = Encoding.UTF8.GetString(pipes.ServerToClientStdout.GetRecordedBytes());
        Assert.DoesNotContain("\"protocol_version\"", wireText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"session_id\"", wireText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_reason\"", wireText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"session_update\"", wireText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tool_call_id\"", wireText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checklist3And4_FullMainLoop_ImageAttachment_OrderedUpdatesBeforeStopReason_AndSessionLoadReplay()
    {
        var rig = new ScriptedAcpTestRig();
        await using var backend = await StartEmbeddedBackendDirectAsync(rig);
        using var pipes = new DuplexAcpPipes();
        await using var transport = new StdioServerAcpTransport(pipes.ClientToServerStdin, pipes.ServerToClientStdout, leaveOpen: true);
        var server = new AcpAgentServer(transport, backend);
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var serverTask = server.RunAsync(serverCts.Token);

        Guid sessionGuid;
        await using (var client = new RecordingAcpWireClient(pipes))
        {
            await client.RequestAsync("initialize", new { protocolVersion = 1 });
            var sessionRes = await client.RequestAsync("session/new", new { cwd = _workspaceDir });
            var sessionId = sessionRes.GetProperty("sessionId").GetString()!;
            sessionGuid = Guid.Parse(sessionId);

            // Verify real modes and models returned
            var modeIds = sessionRes.GetProperty("modes").GetProperty("availableModes")
                .EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray();
            foreach (var expectedMode in new[] { "agent", "ask", "auto", "plan", "spec", "vibe" })
            {
                Assert.Contains(expectedMode, modeIds);
            }
            Assert.NotEmpty(sessionRes.GetProperty("models").GetProperty("availableModels").EnumerateArray());

            client.ClearRecorded();

            var promptRes = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[]
                {
                    new { type = "text", text = "[SCENARIO:FULL_LOOP] Analyze workspace README and attached screenshot" },
                    new { type = "resource", resource = new { uri = "file:///workspace/context.txt", text = "Embedded resource snippet" } },
                    new { type = "resource_link", uri = "https://tinadec.local/docs", name = "Docs", description = "Spec link" },
                    new { type = "image", mimeType = "image/png", data = ValidOnePixelPngBase64 }
                }
            });

            Assert.Equal("end_turn", promptRes.GetProperty("stopReason").GetString());

            // Verify Base64 PNG image was stored & bound in IMessageAttachmentStore
            var attachments = await backend.HostServices.GetRequiredService<IMessageAttachmentStore>().ListAsync(sessionGuid);
            var storedPng = Assert.Single(attachments);
            Assert.Equal("image/png", storedPng.MediaType);
            Assert.NotNull(storedPng.MessageId);

            // Verify resource + resource_link text reached model prompt
            Assert.Contains(rig.ObservedPrompts, p =>
                p.Contains("Embedded resource snippet", StringComparison.Ordinal)
                && p.Contains("https://tinadec.local/docs", StringComparison.Ordinal));

            // Verify ordered session/update frames and that ALL arrived BEFORE session/prompt response
            var timeline = client.Timeline;
            var promptResponse = Assert.Single(timeline, e => e.Category == "response" && e.Detail == "session/prompt");
            var updateEvents = timeline.Where(e => e.Category == "notification").ToList();
            Assert.NotEmpty(updateEvents);
            Assert.All(updateEvents, u => Assert.True(
                u.Order < promptResponse.Order,
                $"Trailing update '{u.Detail}' (order={u.Order}) arrived AFTER session/prompt response (order={promptResponse.Order})!"));

            var updates = client.SessionUpdates.Select(u => u.GetProperty("update")).ToList();
            Assert.Contains(updates, u => u.GetProperty("sessionUpdate").GetString() == "agent_thought_chunk");

            var toolCall = Assert.Single(updates, u => u.GetProperty("sessionUpdate").GetString() == "tool_call");
            Assert.Equal("read", toolCall.GetProperty("kind").GetString());
            Assert.Contains("README.md", toolCall.GetProperty("locations")[0].GetProperty("path").GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("README.md", toolCall.GetProperty("rawInput").GetProperty("filepath").GetString());

            var toolCallId = toolCall.GetProperty("toolCallId").GetString()!;
            Assert.Contains(updates, u =>
                u.GetProperty("sessionUpdate").GetString() == "tool_call_update"
                && u.GetProperty("toolCallId").GetString() == toolCallId
                && u.GetProperty("status").GetString() == "completed");

            var planUpdate = updates.LastOrDefault(u => u.GetProperty("sessionUpdate").GetString() == "plan");
            Assert.Equal(JsonValueKind.Object, planUpdate.ValueKind);
            Assert.NotEmpty(planUpdate.GetProperty("entries").EnumerateArray());

            var answerText = string.Concat(updates
                .Where(u => u.GetProperty("sessionUpdate").GetString() == "agent_message_chunk")
                .Select(u => u.GetProperty("content").GetProperty("text").GetString()));
            Assert.Equal("Full loop complete on model=gpt-4o-mini: README verified.", answerText);

            // Checklist #4: session/load replays user_message_chunk & agent_message_chunk in chronological order BEFORE response
            client.ClearRecorded();
            var loadRes = await client.RequestAsync("session/load", new { sessionId, cwd = _workspaceDir });
            Assert.Equal(sessionId, loadRes.GetProperty("sessionId").GetString());

            var loadTimeline = client.Timeline;
            var loadResponseEvt = Assert.Single(loadTimeline, e => e.Category == "response" && e.Detail == "session/load");
            var replayNotifications = loadTimeline.Where(e => e.Category == "notification").ToList();
            Assert.Equal(2, replayNotifications.Count);
            Assert.All(replayNotifications, n => Assert.True(n.Order < loadResponseEvt.Order));

            Assert.Equal("session/update:user_message_chunk", replayNotifications[0].Detail);
            Assert.Contains("[SCENARIO:FULL_LOOP]", replayNotifications[0].Payload.GetProperty("update").GetProperty("content").GetProperty("text").GetString()!);
            Assert.Equal("session/update:agent_message_chunk", replayNotifications[1].Detail);
            Assert.Equal("Full loop complete on model=gpt-4o-mini: README verified.",
                replayNotifications[1].Payload.GetProperty("update").GetProperty("content").GetProperty("text").GetString());
        }

        pipes.CloseClientStdin();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Checklist5_SessionCancel_CancelsSlowStreamingTurn_AndUnblocksPendingPermissionRequest()
    {
        var rig = new ScriptedAcpTestRig
        {
            SlowTurnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseSlowTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        await using var backend = await StartEmbeddedBackendDirectAsync(rig);
        using var pipes = new DuplexAcpPipes();
        await using var transport = new StdioServerAcpTransport(pipes.ClientToServerStdin, pipes.ServerToClientStdout, leaveOpen: true);
        var server = new AcpAgentServer(transport, backend);
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var serverTask = server.RunAsync(serverCts.Token);

        await using (var client = new RecordingAcpWireClient(pipes))
        {
            await client.RequestAsync("initialize", new { protocolVersion = 1 });
            var sessionId = (await client.RequestAsync("session/new", new { cwd = _workspaceDir }))
                .GetProperty("sessionId").GetString()!;

            // Part A: cancel slow streaming turn via notification (no id)
            var slowPromptTask = client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:SLOW_STREAM]" } }
            });

            await rig.SlowTurnEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await client.NotifyAsync("session/cancel", new { sessionId });

            var slowCancelled = await slowPromptTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("cancelled", slowCancelled.GetProperty("stopReason").GetString());

            // Part B: cancel while session/request_permission is awaiting client selection
            var session2 = (await client.RequestAsync("session/new", new { cwd = _workspaceDir }))
                .GetProperty("sessionId").GetString()!;
            await client.RequestAsync("session/set_mode", new { sessionId = session2, modeId = "ask" });

            var permissionArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnPermissionRequest = async (_, ct) =>
            {
                permissionArrived.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                }

                return RecordingAcpWireClient.CancelledOutcome();
            };

            var gatedPromptTask = client.RequestAsync("session/prompt", new
            {
                sessionId = session2,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:WRITE_GATED]" } }
            });

            await permissionArrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await client.NotifyAsync("session/cancel", new { sessionId = session2 });

            var gatedCancelled = await gatedPromptTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("cancelled", gatedCancelled.GetProperty("stopReason").GetString());
        }

        pipes.CloseClientStdin();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Checklist6_SetMode_And_SetModel_PersistToCoreSessionAndEmitCurrentModeUpdate()
    {
        var rig = new ScriptedAcpTestRig();
        await using var backend = await StartEmbeddedBackendDirectAsync(rig);

        // Register a second model on the seeded OpenAI provider so set_model can switch between two real models
        using (var scope = backend.HostServices.CreateScope())
        {
            var cp = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
            var providersRes = await cp.ListProviders(CancellationToken.None);
            var providersVal = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(providersRes).Value!;
            var providersJson = JsonSerializer.SerializeToElement(providersVal, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            var providerId = providersJson[0].GetProperty("id").GetGuid();

            await cp.SaveProvider(JsonSerializer.SerializeToElement(new
            {
                driver = "openai",
                display_name = "OpenAI Test",
                base_url = "http://127.0.0.1/v1",
                model = "gpt-4o-mini",
                models = new[] { "gpt-4o-mini", "gpt-4.1-pro" },
                api_key = "sk-test",
                enabled = true
            }), providerId, "\"0\"", CancellationToken.None);
        }

        using var pipes = new DuplexAcpPipes();
        await using var transport = new StdioServerAcpTransport(pipes.ClientToServerStdin, pipes.ServerToClientStdout, leaveOpen: true);
        var server = new AcpAgentServer(transport, backend);
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var serverTask = server.RunAsync(serverCts.Token);

        await using (var client = new RecordingAcpWireClient(pipes))
        {
            await client.RequestAsync("initialize", new { protocolVersion = 1 });
            var sessionRes = await client.RequestAsync("session/new", new { cwd = _workspaceDir });
            var sessionId = sessionRes.GetProperty("sessionId").GetString()!;
            var sessionGuid = Guid.Parse(sessionId);
            var store = backend.HostServices.GetRequiredService<ProjectSessionStore>();

            // Test every canonical mode: ask, plan, spec, auto, vibe, agent + custom published mode "team"
            var modeExpectations = new (string ModeId, string ExpectedPerm, bool PlanFirst, bool SpecEnabled)[]
            {
                ("ask", "ask", false, false),
                ("plan", "ask", true, false),
                ("spec", "ask", true, true),
                ("auto", "auto-approve", false, false),
                ("vibe", "full-access", false, false),
                ("agent", "default", false, false)
            };

            foreach (var (modeId, expectedPerm, planFirst, specEnabled) in modeExpectations)
            {
                client.ClearRecorded();
                await client.RequestAsync("session/set_mode", new { sessionId, modeId });

                var modeUpdate = Assert.Single(client.SessionUpdates);
                Assert.Equal("current_mode_update", modeUpdate.GetProperty("update").GetProperty("sessionUpdate").GetString());
                Assert.Equal(modeId, modeUpdate.GetProperty("update").GetProperty("currentModeId").GetString());

                var dbSession = await store.GetSessionAsync(sessionGuid);
                Assert.NotNull(dbSession);
                Assert.Equal(expectedPerm, dbSession!.PermissionMode);
                var space = ProjectSessionStore.ReadSpaceOptions(dbSession);
                Assert.Equal(planFirst, space?.PlanFirst ?? false);
                Assert.Equal(specEnabled, space?.SpecEnabled ?? false);
            }

            // Switch model via session/set_model
            await client.RequestAsync("session/set_model", new { sessionId, modelId = "gpt-4.1-pro" });
            var afterModel = await store.GetSessionAsync(sessionGuid);
            Assert.Equal("gpt-4.1-pro", afterModel!.MeetingModelOverrideModel);

            var promptRes = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "Hello model switch" } }
            });
            Assert.Equal("end_turn", promptRes.GetProperty("stopReason").GetString());

            var chunks = string.Concat(client.SessionUpdates
                .Select(u => u.GetProperty("update"))
                .Where(u => u.GetProperty("sessionUpdate").GetString() == "agent_message_chunk")
                .Select(u => u.GetProperty("content").GetProperty("text").GetString()));
            Assert.Contains("gpt-4.1-pro", chunks, StringComparison.Ordinal);
        }

        pipes.CloseClientStdin();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Checklist7_BidirectionalPermissions_AllowOnce_AllowAlwaysCommandPrefix_RejectOnce_AndCancelledFailClosed()
    {
        var rig = new ScriptedAcpTestRig();
        await using var backend = await StartEmbeddedBackendDirectAsync(rig);
        using var pipes = new DuplexAcpPipes();
        await using var transport = new StdioServerAcpTransport(pipes.ClientToServerStdin, pipes.ServerToClientStdout, leaveOpen: true);
        var server = new AcpAgentServer(transport, backend);
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        var serverTask = server.RunAsync(serverCts.Token);

        var gatedFile = Path.Combine(_workspaceDir, "gated-note.txt");

        await using (var client = new RecordingAcpWireClient(pipes))
        {
            await client.RequestAsync("initialize", new { protocolVersion = 1 });
            var sessionId = (await client.RequestAsync("session/new", new { cwd = _workspaceDir }))
                .GetProperty("sessionId").GetString()!;
            var sessionGuid = Guid.Parse(sessionId);
            await client.RequestAsync("session/set_mode", new { sessionId, modeId = "ask" });

            // 1) allow_once -> tool executes and writes gated-note.txt
            if (File.Exists(gatedFile)) File.Delete(gatedFile);
            client.ClearRecorded();
            client.OnPermissionRequest = (req, _) => Task.FromResult(RecordingAcpWireClient.SelectOptionByKindOrId(req, "allow_once"));

            var allowOnceRes = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:WRITE_GATED] allow once" } }
            });
            Assert.Equal("end_turn", allowOnceRes.GetProperty("stopReason").GetString());
            Assert.Single(client.PermissionRequests);
            Assert.True(File.Exists(gatedFile));
            Assert.Equal("GOVERNED_WRITE_OK", await File.ReadAllTextAsync(gatedFile));

            // 2) reject_once -> tool does NOT execute, tool_call_update status == failed, turn finishes cleanly
            File.Delete(gatedFile);
            client.ClearRecorded();
            client.OnPermissionRequest = (req, _) => Task.FromResult(RecordingAcpWireClient.SelectOptionByKindOrId(req, "reject_once"));

            var rejectRes = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:WRITE_GATED] reject once" } }
            });
            Assert.Equal("end_turn", rejectRes.GetProperty("stopReason").GetString());
            Assert.False(File.Exists(gatedFile));
            Assert.Contains(client.SessionUpdates.Select(u => u.GetProperty("update")), u =>
                u.GetProperty("sessionUpdate").GetString() == "tool_call_update"
                && u.GetProperty("status").GetString() == "failed");

            // 3) cancelled outcome & invalid optionId -> both fail closed without executing write_file
            client.ClearRecorded();
            client.OnPermissionRequest = (_, _) => Task.FromResult(RecordingAcpWireClient.CancelledOutcome());
            var cancelOutcomeRes = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:WRITE_GATED] cancelled outcome" } }
            });
            Assert.Equal("end_turn", cancelOutcomeRes.GetProperty("stopReason").GetString());
            Assert.False(File.Exists(gatedFile));

            client.ClearRecorded();
            client.OnPermissionRequest = (_, _) => Task.FromResult(AcpWire.ToParams(new
            {
                outcome = new { outcome = "selected", optionId = "nonexistent_option_id" }
            }));
            var bogusOptRes = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:WRITE_GATED] bogus option" } }
            });
            Assert.Equal("end_turn", bogusOptRes.GetProperty("stopReason").GetString());
            Assert.False(File.Exists(gatedFile));

            // 4) allow_always on shell ("git status") in default "agent" mode -> persists command_prefix rule ("git") in IApprovalRules,
            // so Turn 2 ("git status --short") executes with ZERO permission prompts!
            await client.RequestAsync("session/set_mode", new { sessionId, modeId = "agent" });
            client.ClearRecorded();
            client.OnPermissionRequest = (req, _) => Task.FromResult(RecordingAcpWireClient.SelectOptionByKindOrId(req, "allow_always"));

            var shellTurn1 = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:SHELL_PREFIX] first git command" } }
            });
            Assert.Equal("end_turn", shellTurn1.GetProperty("stopReason").GetString());
            Assert.Single(client.PermissionRequests);

            var rules = await backend.HostServices.GetRequiredService<IApprovalRules>().ListAsync(sessionGuid);
            Assert.Contains(rules, r => r.Kind == ApprovalRuleKinds.CommandPrefix && r.ToolId == "shell" && r.Pattern == "git");

            client.ClearRecorded();
            var shellTurn2 = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:SHELL_PREFIX] [CMD:SECOND] second git command" } }
            });
            Assert.Equal("end_turn", shellTurn2.GetProperty("stopReason").GetString());
            Assert.Empty(client.PermissionRequests);
        }

        pipes.CloseClientStdin();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private Task<int> StartEmbeddedRunnerAsync(DuplexAcpPipes pipes, ScriptedAcpTestRig rig, CancellationToken ct)
    {
        var args = new[]
        {
            "--acp",
            "--environment=Testing",
            "--TinadecStorage:Enabled=false",
            $"--TinadecStorage:UserRoot={_sandboxRoot}",
            $"--TinadecPersistence:DataRoot={_dataRoot}",
            $"--TinadecPersistence:Sqlite:DatabasePath={_dbPath}",
            $"--TinadecTools:DefaultWorkspaceRoot={_workspaceDir}",
            $"--TinadecAgent:BootstrapPackPath={Path.Combine(_sandboxRoot, "no-fixture-pack.json")}"
        };

        return AcpHostRunner.RunWithStreamsAsync(
            args,
            pipes.ClientToServerStdin,
            pipes.ServerToClientStdout,
            ct,
            builder =>
            {
                builder.Services.AddSingleton<ISecretStore>(new AcpTestSecretStore());
                builder.Services.AddSingleton<IAgentChatClientFactory>(rig);
                builder.Services.Replace(ServiceDescriptor.Singleton<IToolProvider>(rig));
            });
    }

    private async Task<EmbeddedAcpCoreBackend> StartEmbeddedBackendDirectAsync(ScriptedAcpTestRig rig)
    {
        var options = AcpCliOptions.Parse(
        [
            "--acp",
            "--environment=Testing",
            "--TinadecStorage:Enabled=false",
            $"--TinadecStorage:UserRoot={_sandboxRoot}",
            $"--TinadecPersistence:DataRoot={_dataRoot}",
            $"--TinadecPersistence:Sqlite:DatabasePath={_dbPath}",
            $"--TinadecTools:DefaultWorkspaceRoot={_workspaceDir}",
            $"--TinadecAgent:BootstrapPackPath={Path.Combine(_sandboxRoot, "no-fixture-pack.json")}"
        ]);

        var backend = await EmbeddedAcpCoreBackend.StartAsync(
            options,
            builder =>
            {
                builder.Services.AddSingleton<ISecretStore>(new AcpTestSecretStore());
                builder.Services.AddSingleton<IAgentChatClientFactory>(rig);
                builder.Services.Replace(ServiceDescriptor.Singleton<IToolProvider>(rig));
            },
            CancellationToken.None);

        rig.HostServices = backend.HostServices;
        return backend;
    }
}
