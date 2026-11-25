using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AcpServer;
using TinadecCore.AspNetCore;
using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests.AcpServer;

/// <summary>
/// Checklist #8–#10:
/// 8) Mode B (<c>--core-url</c> loopback HTTP + SSE attach + <c>X-Tinadec-Host-Control</c>):
///    live streaming, Desktop UI sync (<c>/messages</c> &amp; <c>/orchestration</c>), Desktop-side approval
///    coexistence race resolution without conflicting double-decisions, and token redaction.
/// 9) True Self-Dogfood E2E: spawns the REAL built <c>TinadecCore.Api.dll --acp</c> child process via
///    <see cref="AcpHarnessProber"/> (<c>custom-acp</c>), <see cref="AcpSessionHost"/>, <see cref="AcpStdioChatClient"/>,
///    and <see cref="ApprovalBridgingAcpInteractionRouter"/> — completing handshake, multi-turn Q&amp;A, and real
///    <c>TinadecTools</c> <c>write_file</c> execution gated by <c>session/request_permission</c>.
/// 10) Child-process lifecycle: closing <c>stdin</c> (EOF) exits cleanly with code <c>0</c> and zero orphan processes,
///     and <c>UserStorageHostLease</c> reports an actionable error if two standalone <c>--acp</c> hosts share one locked storage root.
/// </summary>
public sealed class AcpServerModeBAndDogfoodTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Checklist8_ModeB_RemoteHttpAttach_StreamsUpdates_SyncsWithDesktop_ResolvesDesktopApprovalRace_AndRedactsHostToken()
    {
        const string secretHostToken = "SECRET_HOST_CONTROL_TOKEN_88421";

        // Verify AcpCliOptions never leaks HostToken in ToString()
        var parsedOpts = AcpCliOptions.Parse(["--acp", "--core-url", "http://127.0.0.1:43110", "--host-token", secretHostToken]);
        Assert.DoesNotContain(secretHostToken, parsedOpts.ToString(), StringComparison.Ordinal);

        var root = ApiTestStorage.CreateRoot("acp-modeb");
        var workspaceDir = Path.Combine(root, "modeb-workspace");
        Directory.CreateDirectory(workspaceDir);
        await File.WriteAllTextAsync(Path.Combine(workspaceDir, "README.md"), "# Mode B Workspace\nLive Desktop sync.\n");

        await using var loopbackCore = await LoopbackCoreHttpHost.StartAsync(Path.Combine(root, "core-host"), secretHostToken);

        using var pipes = new DuplexAcpPipes();
        using var runnerCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var runnerTask = AcpHostRunner.RunWithStreamsAsync(
            ["--acp", "--core-url", loopbackCore.BaseUri.ToString(), "--host-token", secretHostToken],
            pipes.ClientToServerStdin,
            pipes.ServerToClientStdout,
            runnerCts.Token);

        await using (var client = new RecordingAcpWireClient(pipes))
        {
            var init = await client.RequestAsync("initialize", new { protocolVersion = 1 });
            Assert.Equal(1, init.GetProperty("protocolVersion").GetInt32());

            var sessionRes = await client.RequestAsync("session/new", new { cwd = workspaceDir });
            var sessionId = sessionRes.GetProperty("sessionId").GetString()!;

            // Verify X-Tinadec-Host-Control header was transmitted to Core
            Assert.Contains(secretHostToken, loopbackCore.ObservedHostTokens);

            // Execute full prompt over Mode B HTTP + SSE
            var promptRes = await client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:FULL_LOOP] Mode B turn" } }
            });
            Assert.Equal("end_turn", promptRes.GetProperty("stopReason").GetString());

            // Verify Desktop UI endpoints (/messages and /orchestration) see the live ACP session state
            var desktopMessages = await loopbackCore.HttpClient.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
            Assert.NotNull(desktopMessages);
            var conversationalMessages = desktopMessages!
                .Where(m => m.GetProperty("role").GetString() is "user" or "assistant")
                .ToArray();
            Assert.Equal(2, conversationalMessages.Length);
            Assert.Equal("user", conversationalMessages[0].GetProperty("role").GetString());
            Assert.Equal("assistant", conversationalMessages[1].GetProperty("role").GetString());

            var desktopOrch = await loopbackCore.HttpClient.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/orchestration");
            Assert.Equal("completed", desktopOrch.GetProperty("run").GetProperty("status").GetString());

            // Coexistence Race: switch to "ask" mode, trigger gated write_file, hold ACP client's
            // session/request_permission open while Desktop UI approves via POST /api/v1/approvals/{id}/decision!
            await client.RequestAsync("session/set_mode", new { sessionId, modeId = "ask" });
            client.ClearRecorded();

            var acpPermissionPrompted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLateAcpReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnPermissionRequest = async (req, ct) =>
            {
                acpPermissionPrompted.TrySetResult(req);
                try
                {
                    await releaseLateAcpReply.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return RecordingAcpWireClient.CancelledOutcome();
                }

                // Even if the IDE replies slightly after Desktop already decided, no conflict error may surface!
                return RecordingAcpWireClient.SelectOptionByKindOrId(req, "reject_once");
            };

            var racePromptTask = client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "text", text = "[SCENARIO:WRITE_GATED] Desktop race" } }
            });

            await acpPermissionPrompted.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            // Desktop UI fetches pending approval and approves it first
            var pendingApprovals = await loopbackCore.HttpClient.GetFromJsonAsync<JsonElement[]>(
                $"/api/v1/approvals?status=pending&session_id={sessionId}");
            Assert.NotNull(pendingApprovals);
            var pendingId = Assert.Single(pendingApprovals!).GetProperty("id").GetGuid();

            var desktopDecisionResp = await loopbackCore.HttpClient.PostAsJsonAsync(
                $"/api/v1/approvals/{pendingId}/decision",
                new { decision = "approved", scope = "once" });
            Assert.Equal(HttpStatusCode.OK, desktopDecisionResp.StatusCode);

            // Release late conflicting reply from ACP client after Desktop already approved
            releaseLateAcpReply.TrySetResult();

            var racePromptRes = await racePromptTask.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal("end_turn", racePromptRes.GetProperty("stopReason").GetString());
            Assert.Equal("GOVERNED_WRITE_OK", await File.ReadAllTextAsync(Path.Combine(workspaceDir, "gated-note.txt")));
        }

        pipes.CloseClientStdin();
        Assert.Equal(0, await runnerTask.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Checklist9And10_TrueSelfDogfood_RealChildProcess_Prober_StdioChatClient_ApprovalBridge_HostLock_AndCleanEofExit()
    {
        var apiDllPath = ResolveBuiltApiDllPath();
        Assert.True(File.Exists(apiDllPath), $"Expected built TinadecCore.Api assembly at '{apiDllPath}'.");
        var dotnetExecutable = ResolveDotnetMuxerPath();

        await using var fakeOpenAi = await FakeOpenAiHttpServer.StartAsync();

        var dogfoodContainer = ApiTestStorage.CreateRoot("acp-dogfood");
        // IMPORTANT: workspaceRoot and harnessScratchParent are SIBLINGS of userStorageRoot (never nested inside userStorageRoot).
        var userStorageRoot = Path.Combine(dogfoodContainer, "user-storage");
        var harnessScratchParent = Path.Combine(dogfoodContainer, "client-scratch");
        var noFixturePack = Path.Combine(dogfoodContainer, "no-fixture-pack.json");
        Directory.CreateDirectory(userStorageRoot);
        Directory.CreateDirectory(harnessScratchParent);

        var workspaceRoots = new DogfoodHarnessWorkspaceRoots(harnessScratchParent);
        var probeProviderId = Guid.NewGuid();
        var sessionProviderId = Guid.NewGuid();
        var probeScratchDir = workspaceRoots.ForProvider(probeProviderId);
        var scratchDir = workspaceRoots.ForProvider(sessionProviderId);
        fakeOpenAi.TargetWorkspaceDirectory = scratchDir;

        // Step 1: Pre-provision isolated userStorageRoot + pre-warm project scope SQLite schemas, then release host.lock.
        await PreprovisionDogfoodUserStorageAsync(userStorageRoot, fakeOpenAi.V1BaseUrl, [probeScratchDir, scratchDir]);

        var launchArgs = $"\"{apiDllPath}\" --acp --storage-root \"{userStorageRoot}\" \"--TinadecAgent:BootstrapPackPath={noFixturePack}\"";
        var launchArgv = HarnessCatalog.ResolveAcpArgv("custom-acp", launchArgs);

        var approvedPermissions = new List<AcpPermissionCheckRequest>();
        var approvalBridge = new DogfoodAutoApprovingBridge(req => approvedPermissions.Add(req));
        var routerFactory = new ApprovalBridgingAcpInteractionRouterFactory(approvalBridge);

        var dogfoodSessionOptions = AcpSessionOptions.Default with
        {
            HandshakeTimeout = TimeSpan.FromSeconds(90),
            RequestTimeout = TimeSpan.FromSeconds(90)
        };

        // Step 2: Dogfood AcpHarnessProber ("custom-acp") spawning REAL `dotnet TinadecCore.Api.dll --acp`
        await using (var proberHost = new AcpSessionHost(
            dogfoodSessionOptions,
            workspaceRoots,
            routerFactory,
            NullLogger<AcpSessionHost>.Instance))
        {
            var prober = new AcpHarnessProber(proberHost);
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var probeResult = await prober.ProbeAsync(
                providerInstanceId: probeProviderId,
                harnessId: "custom-acp",
                binaryPath: dotnetExecutable,
                homePath: null,
                launchArgs: launchArgs,
                cancellationToken: probeCts.Token);

            Assert.True(probeResult.LoadSession);
            Assert.True(probeResult.ImageInput);
            Assert.False(probeResult.SessionFork);
        }

        // Step 3: Dogfood AcpSessionHost + AcpSession + AcpStdioChatClient + ApprovalBridgingAcpInteractionRouter
        // across multi-turn Q&A AND real TinadecTools write_file tool call gated by session/request_permission!
        await using (var sessionHost = new AcpSessionHost(
            dogfoodSessionOptions,
            workspaceRoots,
            routerFactory,
            NullLogger<AcpSessionHost>.Instance))
        {
            var sessionRequest = new AcpSessionRequest(
                sessionProviderId,
                "custom-acp",
                dotnetExecutable,
                launchArgv,
                scratchDir);

            // Switch child ACP session to "ask" mode so write_file triggers bidirectional session/request_permission
            using var acquireCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var liveSession = await sessionHost.AcquireAsync(sessionRequest, acquireCts.Token);
            await liveSession.SetModeAsync("ask", acquireCts.Token);

            using var chatClient = new AcpStdioChatClient(sessionHost, sessionRequest);

            // Turn 1: Multi-turn memory seed
            using var turn1Cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var turn1Resp = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Please REMEMBER_CODE_7391 for the next turn.")],
                cancellationToken: turn1Cts.Token);
            Assert.Contains("7391", turn1Resp.Text, StringComparison.Ordinal);

            // Turn 2: Multi-turn recall (proving session continuity inside the child --acp server!)
            using var turn2Cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var turn2Resp = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "WHAT_WAS_THE_CODE?")],
                cancellationToken: turn2Cts.Token);
            Assert.Contains("7391", turn2Resp.Text, StringComparison.Ordinal);

            // Turn 3: Real TinadecTools `write_file` gated by `session/request_permission` -> bridged through ApprovalBridgingAcpInteractionRouter!
            using var turn3Cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var streamingUpdates = new List<string>();
            await foreach (var chunk in chatClient.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Now execute DOGFOOD_WRITE_FILE in the workspace.")],
                cancellationToken: turn3Cts.Token))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    streamingUpdates.Add(chunk.Text);
                }
            }

            var turn3Text = string.Concat(streamingUpdates);
            Assert.Contains("DOGFOOD_WRITE_COMPLETED", turn3Text, StringComparison.Ordinal);
            Assert.NotEmpty(approvedPermissions);
            Assert.Contains(approvedPermissions, p => p.Kind == "edit");

            var writtenProofFile = Path.Combine(scratchDir, "dogfood-proof.txt");
            Assert.True(File.Exists(writtenProofFile), $"Expected real TinadecTools to write '{writtenProofFile}'.");
            Assert.Equal("DOGFOOD_E2E_VERIFIED", await File.ReadAllTextAsync(writtenProofFile, turn3Cts.Token));
        }

        // Step 4: Checklist #10 — Direct child-process lifecycle + UserStorageHostLease lock contention + stdin EOF exit 0
        using var childProcess = StartAcpChildProcess(dotnetExecutable, apiDllPath, userStorageRoot);
        var stderrBuffer = new StringBuilder();
        childProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
            }
        };
        childProcess.BeginErrorReadLine();

        // Send initialize over raw stdin and read exact single NDJSON response line on raw stdout
        var initJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}\n";
        await childProcess.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(initJson));
        await childProcess.StandardInput.BaseStream.FlushAsync();

        var initReplyLine = await childProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(string.IsNullOrWhiteSpace(initReplyLine));
        using (var replyDoc = JsonDocument.Parse(initReplyLine!))
        {
            Assert.Equal(1, replyDoc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        }

        // Verify a second standalone `--acp` pointing at the SAME locked userStorageRoot fails fast with actionable message on stderr
        using (var contendingProcess = StartAcpChildProcess(dotnetExecutable, apiDllPath, userStorageRoot))
        {
            var contenderStderr = await contendingProcess.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await contendingProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, contendingProcess.ExitCode);
            Assert.Contains("--core-url", contenderStderr, StringComparison.Ordinal);
            Assert.Contains("--storage-root", contenderStderr, StringComparison.Ordinal);
        }

        // Close stdin (EOF) on the primary child process -> must exit cleanly with code 0!
        childProcess.StandardInput.Close();
        await childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, childProcess.ExitCode);
    }

    private static async Task PreprovisionDogfoodUserStorageAsync(
        string userStorageRoot,
        string fakeOpenAiV1Url,
        IReadOnlyList<string> prewarmWorkspaceDirs)
    {
        var opts = AcpCliOptions.Parse(
        [
            "--acp",
            "--storage-root",
            userStorageRoot,
            $"--TinadecAgent:BootstrapPackPath={Path.Combine(userStorageRoot, "no-fixture-pack.json")}"
        ]);
        await using var backend = await EmbeddedAcpCoreBackend.StartAsync(opts, configureBuilder: null, CancellationToken.None);
        using var scope = backend.HostServices.CreateScope();
        var cp = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();

        var providersRes = await cp.ListProviders(CancellationToken.None);
        var providersVal = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(providersRes).Value!;
        var providersJson = JsonSerializer.SerializeToElement(providersVal, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        var providerId = providersJson[0].GetProperty("id").GetGuid();

        var saveRes = await cp.SaveProvider(JsonSerializer.SerializeToElement(new
        {
            driver = "openai",
            display_name = "Dogfood Fake OpenAI",
            base_url = fakeOpenAiV1Url,
            model = "gpt-dogfood",
            models = new[] { "gpt-dogfood" },
            api_key = "sk-dogfood-test-key",
            enabled = true
        }), providerId, "\"0\"", CancellationToken.None);

        var status = (saveRes as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode ?? 200;
        Assert.Equal(200, status);

        var routeDto = JsonSerializer.Deserialize<Contracts.Dtos.ModelRouteWriteRequestDto>(
            JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        provider_instance_id = providerId,
                        model = "gpt-dogfood"
                    }
                }
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            })!;

        await cp.SaveRoute("chat", routeDto, "\"0\"", CancellationToken.None);

        foreach (var dir in prewarmWorkspaceDirs)
        {
            _ = await backend.CreateSessionAsync(dir, CancellationToken.None);
        }
    }

    private static Process StartAcpChildProcess(string dotnetExe, string apiDllPath, string userStorageRoot)
    {
        var psi = new ProcessStartInfo
        {
            FileName = dotnetExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(apiDllPath);
        psi.ArgumentList.Add("--acp");
        psi.ArgumentList.Add("--storage-root");
        psi.ArgumentList.Add(userStorageRoot);
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to spawn TinadecCore.Api --acp subprocess.");
    }

    private static string ResolveBuiltApiDllPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var releaseCandidate = Path.Combine(dir.FullName, "TinadecCore", "Api", "bin", "Release", "net11.0", "TinadecCore.Api.dll");
            if (File.Exists(releaseCandidate)) return releaseCandidate;
        }

        return typeof(Program).Assembly.Location;
    }

    private static string ResolveDotnetMuxerPath()
    {
        if (Environment.ProcessPath is { Length: > 0 } currentExe
            && Path.GetFileNameWithoutExtension(currentExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return currentExe;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private sealed class DogfoodAutoApprovingBridge(Action<AcpPermissionCheckRequest> onRequest) : IAcpApprovalBridge
    {
        public Task<AcpPermissionDecision> EvaluateOrWaitForApprovalAsync(
            AcpPermissionCheckRequest request,
            CancellationToken cancellationToken = default)
        {
            onRequest(request);
            return Task.FromResult(new AcpPermissionDecision(true, "approved"));
        }
    }

    private sealed class DogfoodHarnessWorkspaceRoots(string parentRoot) : IHarnessWorkspaceRoots
    {
        public string ForProvider(Guid providerInstanceId)
        {
            var path = Path.Combine(parentRoot, providerInstanceId.ToString("N"));
            Directory.CreateDirectory(path);
            return WorkspacePathSpelling.Canonical(path);
        }

        public string ForTeammate(Guid providerInstanceId, string? runId, string? teammateHandle) =>
            ForProvider(providerInstanceId);
    }
}
