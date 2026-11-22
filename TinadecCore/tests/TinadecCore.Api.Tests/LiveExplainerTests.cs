using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.DmaEA.LiveExplainer;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

[Collection("HostControlToken")]
public sealed class LiveExplainerTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("live-explainer-tests");
    private RecordingObserverInvoker _invoker = new();
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _invoker = new RecordingObserverInvoker();
        _factory = new Factory(_root, _invoker);
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
            catch (IOException)
            {
                await Task.Delay(250);
            }
        }
    }

    [Fact]
    public void L0SemanticFolder_CompactsHugeToolOutputs_ExtractsFileStats_AndBuildsSentinelPrompts()
    {
        var hugeContent = string.Join("\n", Enumerable.Range(1, 600).Select(i => $"line {i}: const value_{i} = {i};"));
        var readExec = new ToolExecutionSnapshot(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            RunId: Guid.NewGuid(),
            TaskId: Guid.NewGuid(),
            AgentInstanceId: Guid.NewGuid(),
            ApprovalId: null,
            ToolId: "read_file",
            ToolCallKey: "call_read_1",
            Risk: "safe",
            MutatesWorkspace: false,
            RequiresApproval: false,
            Status: ToolDispatchStatus.Completed,
            ParametersJson: JsonSerializer.Serialize(new { path = "C:/repo/apps/desktop/src/components/LiveDigestPanel.vue" }),
            ParametersHash: "hash-read-1",
            Attempt: 1,
            ResultJson: JsonSerializer.Serialize(new { content = hugeContent }),
            ErrorCategory: null,
            SafeErrorMessage: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow);

        var foldedRead = LiveExplainerSemanticFolder.FoldToolExecution(readExec);
        Assert.Equal("succeeded", foldedRead.ToolCall.Status);
        Assert.True(foldedRead.ToolCall.CompactSummary.Length <= 160);
        Assert.Contains("600 行", foldedRead.ToolCall.CompactSummary, StringComparison.Ordinal);
        Assert.Null(foldedRead.TouchedFile);
        Assert.Equal(LiveDigestPhases.Hypothesis, foldedRead.InferredPhase);

        var editExec = readExec with
        {
            Id = Guid.NewGuid(),
            ToolId = "edit_file",
            ToolCallKey = "call_edit_1",
            MutatesWorkspace = true,
            ParametersJson = JsonSerializer.Serialize(new
            {
                path = "apps/desktop/src/lib/liveDigestApi.ts",
                target = "const a = 1;\nconst b = 2;",
                replacement = "const a = 1;\nconst b = 2;\nconst c = 3;\nconst d = 4;\nexport { a, b, c, d };"
            }),
            ResultJson = JsonSerializer.Serialize(new { ok = true })
        };

        var foldedEdit = LiveExplainerSemanticFolder.FoldToolExecution(editExec);
        Assert.NotNull(foldedEdit.TouchedFile);
        Assert.Equal(5, foldedEdit.TouchedFile!.AddedLines);
        Assert.Equal(2, foldedEdit.TouchedFile.RemovedLines);
        Assert.Equal(LiveDigestPhases.Verification, foldedEdit.InferredPhase);

        var thought = LiveExplainerSemanticFolder.ExtractThoughtExcerpt(
            "prefix <thought>Need to verify observer isolation   and SSE framing</thought> answer");
        Assert.Equal("Need to verify observer isolation and SSE framing", thought);

        foreach (var signalCode in GovernanceSignalCodes.All)
        {
            var alert = LiveExplainerSemanticFolder.CreateSentinelAlert(signalCode, "单元测试详情");
            Assert.Equal(signalCode, alert.Code);
            Assert.False(string.IsNullOrWhiteSpace(alert.SuggestedSteerPrompt));
        }
    }

    [Fact]
    public async Task Settings_DefaultAutoPicksFastestCheapestModel_AndSupportsExplicitSlotBinding()
    {
        using var client = _factory!.CreateClient();

        // Initially unconfigured when no providers exist; default mode is "auto".
        var initial = await client.GetFromJsonAsync<LiveDigestSettingsDto>("/api/v1/live-digest/settings", LiveDigestJson.Options);
        Assert.NotNull(initial);
        Assert.Equal(LiveExplainerModes.Auto, initial!.Mode);
        Assert.Equal("live_explainer", initial.RoutePurpose);
        Assert.Equal("unconfigured", initial.ResolvedSource);

        // Register a provider with both a heavy deep model and a cheap fast flash-lite model + API key.
        var createResp = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "gemini",
            display_name = "Workspace Gemini",
            connection_kind = "api-key",
            base_url = "https://generativelanguage.googleapis.com/v1beta/openai",
            api_key = "sk-test-secret-key",
            model = "gemini-2.5-pro",
            models = new[] { "gemini-2.5-pro", "gemini-2.5-flash-lite" },
            model_profiles = new Dictionary<string, object>
            {
                ["gemini-2.5-pro"] = new { tier = "deep", cost_tier = "high", input_price_per_1m = 3.5m },
                ["gemini-2.5-flash-lite"] = new { tier = "fast", cost_tier = "low", input_price_per_1m = 0.075m }
            }
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var provider = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = provider.GetProperty("id").GetGuid();

        // Without an explicit live_explainer route, auto resolution picks gemini-2.5-flash-lite!
        var autoResolved = await client.GetFromJsonAsync<LiveDigestSettingsDto>("/api/v1/live-digest/settings", LiveDigestJson.Options);
        Assert.NotNull(autoResolved);
        Assert.Equal("auto_fast_model", autoResolved!.ResolvedSource);
        Assert.Equal("gemini-2.5-flash-lite", autoResolved.ResolvedModel);
        Assert.Null(autoResolved.ProviderInstanceId);

        // Explicitly bind the live_explainer slot and switch mode to "always".
        var putResp = await client.PutAsJsonAsync("/api/v1/live-digest/settings", new
        {
            mode = "always",
            provider_instance_id = providerId,
            model = "gemini-2.5-pro"
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<LiveDigestSettingsDto>(LiveDigestJson.Options);
        Assert.NotNull(updated);
        Assert.Equal("always", updated!.Mode);
        Assert.Equal("explicit_route", updated.ResolvedSource);
        Assert.Equal(providerId, updated.ProviderInstanceId);
        Assert.Equal("gemini-2.5-pro", updated.ResolvedModel);

        // Clearing explicit binding falls back to auto_fast_model seamlessly.
        var clearResp = await client.PutAsJsonAsync("/api/v1/live-digest/settings", new
        {
            mode = "auto",
            clear_model_binding = true
        });
        Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);
        var cleared = await clearResp.Content.ReadFromJsonAsync<LiveDigestSettingsDto>(LiveDigestJson.Options);
        Assert.Equal("auto_fast_model", cleared!.ResolvedSource);
        Assert.Equal("gemini-2.5-flash-lite", cleared.ResolvedModel);
    }

    [Fact]
    public async Task IronLaw1_IsObserverRun_NeverPollutesSessionMessages_AndBlocksRecursiveSelfObservation()
    {
        using var client = _factory!.CreateClient();
        var (runId, sessionId, turnId) = await StartTestRunAsync(client);

        var projection = _factory.Services.GetRequiredService<RunStreamProjection>();
        var governanceSink = _factory.Services.GetRequiredService<IGovernanceTopicSink>();
        var explainer = _factory.Services.GetRequiredService<LiveExplainerService>();
        var conversations = _factory.Services.GetRequiredService<IConversationStore>();

        // Verify DefaultLiveExplainerModelInvoker rejects IsObserverRun = false outright.
        var defaultInvoker = new DefaultLiveExplainerModelInvoker(_factory.Services.GetRequiredService<IAgentChatClientFactory>());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            defaultInvoker.InvokeObserverAsync(new ObserverModelInvocation
            {
                ObservedRunId = runId,
                IsObserverRun = false,
                UserPrompt = "forbidden"
            }));

        var messagesBefore = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        var revisionBefore = await conversations.GetContextRevisionAsync(sessionId);
        var suppressedBefore = ObserverRunScope.SuppressedRecursiveSignals;

        // Inside the fake observer model call, attempt to emit stream chunks & governance signals recursively!
        _invoker.OnInvoke = invocation =>
        {
            Assert.True(invocation.IsObserverRun);
            Assert.True(ObserverRunScope.IsCurrentObserverRun);

            // Attempt recursive self-triggering from inside observer execution:
            projection.Publish(new RunStreamChunk(
                invocation.ObservedRunId,
                turnId,
                null,
                999,
                "delta",
                "<thought>recursive observer output that must be ignored</thought>",
                OccurredAt: DateTimeOffset.UtcNow));
            explainer.ObserveGovernanceSignal(invocation.ObservedRunId, GovernanceSignalCodes.LoopDetected, "recursive signal");

            return new ObserverModelResult(
                true,
                "{\"headline\":\"正在安全旁路解说代码验证\",\"phase\":\"verification\",\"chapter_title\":\"验证观察者隔离\",\"narrative\":\"主解说员已确认零上下文污染且阻断递归信号。\"}",
                "gemini-2.5-flash-lite");
        };

        await client.PutAsJsonAsync("/api/v1/live-digest/settings", new { mode = "always" });

        projection.Publish(new RunStreamChunk(
            runId,
            turnId,
            null,
            1,
            "delta",
            "<thought>Inspecting RunStreamProjection hooks and testing observer isolation boundary carefully.</thought>",
            Usage: new { used_tokens = 16_000, max_tokens = 100_000 },
            OccurredAt: DateTimeOffset.UtcNow));

        await Task.Delay(80);
        await explainer.FlushPendingNarrationAsync(runId);

        var snapshot = await client.GetFromJsonAsync<LiveDigestSnapshotDto>($"/api/v1/runs/{runId}/live-digest", LiveDigestJson.Options);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.ExplainerActive);
        Assert.Equal("正在安全旁路解说代码验证", snapshot.Headline);
        Assert.DoesNotContain(snapshot.GovernanceSignals, s => s.Summary.Contains("recursive", StringComparison.Ordinal));
        Assert.True(ObserverRunScope.SuppressedRecursiveSignals > suppressedBefore);

        // Main session messages and context revision must be 100% untouched!
        var messagesAfter = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        var revisionAfter = await conversations.GetContextRevisionAsync(sessionId);
        Assert.Equal(messagesBefore!.Length, messagesAfter!.Length);
        Assert.Equal(revisionBefore, revisionAfter);
        Assert.Equal(1, _invoker.LeadNarratorCalls);
        Assert.All(_invoker.RecordedInvocations, inv => Assert.True(inv.IsObserverRun));

        _ = governanceSink;
    }

    [Fact]
    public async Task IronLaw2_OffModeAndModelFailure_DegradeZeroBlockingToPureL0Stream()
    {
        using var client = _factory!.CreateClient();
        var (runId, _, turnId) = await StartTestRunAsync(client);

        var projection = _factory.Services.GetRequiredService<RunStreamProjection>();
        var explainer = _factory.Services.GetRequiredService<LiveExplainerService>();

        // 1. Switch mode to "off": 0 model calls, pure L0 structural stream works instantaneously.
        var offResp = await client.PutAsJsonAsync("/api/v1/live-digest/settings", new { mode = "off" });
        Assert.Equal(HttpStatusCode.OK, offResp.StatusCode);

        projection.Publish(new RunStreamChunk(
            runId,
            turnId,
            null,
            1,
            "delta",
            "<thought>Checking waterline and compaction markers in pure L0 mode.</thought>",
            Usage: new { used_tokens = 88_000, max_tokens = 100_000 },
            OccurredAt: DateTimeOffset.UtcNow));

        // Simulate a compaction drop from 88k -> 24k tokens.
        await Task.Delay(40);
        projection.Publish(new RunStreamChunk(
            runId,
            turnId,
            null,
            2,
            "delta",
            "Continuing after context compaction.",
            Usage: new { used_tokens = 24_000, max_tokens = 100_000 },
            OccurredAt: DateTimeOffset.UtcNow));

        await Task.Delay(80);
        await explainer.FlushPendingNarrationAsync(runId);

        Assert.Equal(0, _invoker.TotalCalls);
        var l0Snapshot = await client.GetFromJsonAsync<LiveDigestSnapshotDto>($"/api/v1/runs/{runId}/live-digest", LiveDigestJson.Options);
        Assert.NotNull(l0Snapshot);
        Assert.Equal("off", l0Snapshot!.Mode);
        Assert.False(l0Snapshot.ExplainerActive);
        Assert.Equal(24_000, l0Snapshot.ContextWaterline.UsedTokens);
        Assert.Equal(1, l0Snapshot.ContextWaterline.CompactionCount);
        Assert.Contains(l0Snapshot.GovernanceSignals, s => s.Code == GovernanceSignalCodes.ContextPressure);
        Assert.Contains(l0Snapshot.SentinelAlerts, a => a.Code == GovernanceSignalCodes.ContextPressure && !string.IsNullOrEmpty(a.SuggestedSteerPrompt));

        // 2. Switch to "always" with a throwing/failing model: zero exceptions, L0 stays healthy.
        _invoker.OnInvoke = _ => throw new HttpRequestException("Simulated upstream 429 rate limit");
        await client.PutAsJsonAsync("/api/v1/live-digest/settings", new { mode = "always" });

        projection.Publish(new RunStreamChunk(
            runId,
            turnId,
            null,
            3,
            "delta",
            "<thought>Even when the narrator model throws 429, main run and L0 digest never block.</thought>",
            OccurredAt: DateTimeOffset.UtcNow));

        await Task.Delay(80);
        await explainer.FlushPendingNarrationAsync(runId);

        var degraded = await client.GetFromJsonAsync<LiveDigestSnapshotDto>($"/api/v1/runs/{runId}/live-digest", LiveDigestJson.Options);
        Assert.NotNull(degraded);
        Assert.NotEmpty(degraded!.Chapters);
    }

    [Fact]
    public async Task SameArgsRetry_DispatchesEphemeralScout_StreamsSseEvents_AndPersistsPostRunReplay()
    {
        using var client = _factory!.CreateClient();
        var (runId, sessionId, projectId, turnId) = await StartTestRunWithProjectAsync(client);

        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var toolCoordinator = _factory.Services.GetRequiredService<IToolExecutionCoordinator>();
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var projection = _factory.Services.GetRequiredService<RunStreamProjection>();
        var explainer = _factory.Services.GetRequiredService<LiveExplainerService>();
        var store = _factory.Services.GetRequiredService<LiveExplainerStore>();

        _invoker.OnInvoke = invocation =>
        {
            if (invocation.Role.StartsWith("ephemeral_scout:", StringComparison.Ordinal))
            {
                return new ObserverModelResult(
                    true,
                    "侦察确认：edit_file 连续两次因相同旧行内容匹配失败，需先重新读取文件最新行。",
                    "gemini-2.5-flash-lite");
            }

            return new ObserverModelResult(
                true,
                "{\"headline\":\"侦察发现重复参数编辑失败，建议先重读目标文件\",\"phase\":\"verification\",\"chapter_title\":\"修复编辑冲突\",\"narrative\":\"执行层连续两次以相同参数调用 edit_file 未果，侦察小弟确认目标片段已变化。\"}",
                "gemini-2.5-flash-lite");
        };

        await client.PutAsJsonAsync("/api/v1/live-digest/settings", new { mode = "auto" });

        // Record two consecutive failed tool executions with identical ParametersHash -> triggers SAME_ARGS_RETRY & L2 Scout!
        var taskId = Guid.NewGuid();
        var agentInstanceId = Guid.NewGuid();
        for (var i = 1; i <= 2; i++)
        {
            var prep = await toolCoordinator.PrepareAsync(new ToolExecutionPrepareRequest(
                scope.TenantId,
                scope.WorkspaceId,
                projectId,
                sessionId,
                runId,
                taskId,
                agentInstanceId,
                "edit_file",
                "low",
                MutatesWorkspace: true,
                RequiresApproval: false,
                ParametersJson: "{\"path\":\"src/service.ts\",\"target\":\"old\",\"replacement\":\"new\"}",
                ParametersHash: "identical-args-hash-001",
                ToolCallKey: $"call_retry_{i}",
                Summary: $"Attempt {i}"));
            await toolCoordinator.FailAsync(prep.Execution.Id, ToolDispatchStatus.Failed, "stale_version", "File expected_hash mismatch (stale)");
        }

        // Open real-time SSE stream before sealing the run.
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/live-digest/stream");
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        Assert.Equal("text/event-stream", sseResponse.Content.Headers.ContentType?.MediaType);

        await using var bodyStream = await sseResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(bodyStream, Encoding.UTF8);

        // First SSE event must be the immediate "snapshot" frame.
        var firstEventBlock = await ReadNextSseBlockAsync(reader);
        Assert.Contains("event: snapshot", firstEventBlock, StringComparison.Ordinal);

        // Complete the run and seal projection -> triggers final L1/L2 pass and "done" SSE event.
        await lifecycle.CompleteRunAsync(runId.ToString());
        projection.Publish(new RunStreamChunk(runId, turnId, null, 10, "done", OccurredAt: DateTimeOffset.UtcNow));
        projection.Seal(runId);

        var observedEvents = new List<string> { firstEventBlock };
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!readCts.IsCancellationRequested)
        {
            var block = await ReadNextSseBlockAsync(reader, readCts.Token);
            if (string.IsNullOrEmpty(block)) break;
            observedEvents.Add(block);
            if (block.Contains("event: done", StringComparison.Ordinal)) break;
        }

        Assert.Contains(observedEvents, b => b.Contains("event: done", StringComparison.Ordinal));
        await explainer.FlushPendingNarrationAsync(runId);

        // Verify L2 scout was dispatched alongside L1 lead narrator.
        Assert.True(_invoker.ScoutCalls >= 1);
        Assert.True(_invoker.LeadNarratorCalls >= 1);

        // Verify durable SQLite snapshot persists chapters, SAME_ARGS_RETRY, FS_STALE_VERSION, scout_note & sentinel alerts for post-run replay!
        var persistedFromDb = await store.GetSnapshotAsync(runId, scope.TenantId, scope.WorkspaceId);
        Assert.NotNull(persistedFromDb);
        Assert.Equal("completed", persistedFromDb!.Status);
        Assert.Contains(persistedFromDb.GovernanceSignals, s => s.Code == GovernanceSignalCodes.SameArgsRetry);
        Assert.Contains(persistedFromDb.GovernanceSignals, s => s.Code == GovernanceSignalCodes.FsStaleVersion);
        Assert.Contains(persistedFromDb.SentinelAlerts, a => a.Code == GovernanceSignalCodes.SameArgsRetry && !string.IsNullOrWhiteSpace(a.SuggestedSteerPrompt));
        Assert.Contains(persistedFromDb.Chapters, c => c.Source == "l2_scout" && !string.IsNullOrWhiteSpace(c.ScoutNote));

        // HTTP GET replay on completed run matches durable snapshot.
        var replayHttp = await client.GetFromJsonAsync<LiveDigestSnapshotDto>($"/api/v1/runs/{runId}/live-digest", LiveDigestJson.Options);
        Assert.NotNull(replayHttp);
        Assert.Equal("completed", replayHttp!.Status);
        Assert.Equal(persistedFromDb.Headline, replayHttp.Headline);
    }

    private async Task<(Guid RunId, Guid SessionId, Guid TurnId)> StartTestRunAsync(HttpClient client)
    {
        var (runId, sessionId, _, turnId) = await StartTestRunWithProjectAsync(client);
        return (runId, sessionId, turnId);
    }

    private async Task<(Guid RunId, Guid SessionId, Guid ProjectId, Guid TurnId)> StartTestRunWithProjectAsync(HttpClient client)
    {
        var projPath = Path.Combine(_root, "proj-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(projPath);
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "LiveDigestProj", path = projPath });
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);
        var proj = await projResp.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = proj.GetProperty("id").GetGuid();

        var sessionResp = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Live Digest Session" });
        Assert.Equal(HttpStatusCode.Created, sessionResp.StatusCode);
        var session = await sessionResp.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var msgResp = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Verify LiveExplainerService" });
        var msg = await msgResp.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = msg.GetProperty("id").GetGuid();

        var storageLifecycle = _factory!.Services.GetRequiredService<StorageLifecycleService>();
        var run = await storageLifecycle.StartRunAsync(sessionId, messageId);
        return (run.Id, sessionId, projectId, Guid.NewGuid());
    }

    private static async Task<string> ReadNextSseBlockAsync(StreamReader reader, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (sb.Length > 0) return sb.ToString();
                continue;
            }
            if (line.StartsWith(':')) continue; // skip SSE comment keepalive
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private sealed class RecordingObserverInvoker : ILiveExplainerModelInvoker
    {
        public ConcurrentQueue<ObserverModelInvocation> RecordedInvocations { get; } = new();
        public Func<ObserverModelInvocation, ObserverModelResult>? OnInvoke { get; set; }

        public int TotalCalls => RecordedInvocations.Count;
        public int LeadNarratorCalls => RecordedInvocations.Count(x => x.Role == "lead_narrator");
        public int ScoutCalls => RecordedInvocations.Count(x => x.Role.StartsWith("ephemeral_scout:", StringComparison.Ordinal));

        public Task<ObserverModelResult> InvokeObserverAsync(ObserverModelInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (!invocation.IsObserverRun)
            {
                throw new InvalidOperationException("Observer invocation must have IsObserverRun = true.");
            }
            RecordedInvocations.Enqueue(invocation);
            if (OnInvoke is not null)
            {
                return Task.FromResult(OnInvoke(invocation));
            }
            return Task.FromResult(new ObserverModelResult(
                true,
                "{\"headline\":\"默认测试解说\",\"phase\":\"hypothesis\",\"chapter_title\":\"定位阶段\",\"narrative\":\"正在读取上下文。\"}",
                "mock-fast-model"));
        }
    }

    private sealed class NoEmbeddings : IEmbeddingProvider
    {
        public Task<EmbeddingResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult { IsAvailable = false, Detail = "Disabled in tests." });
    }

    private sealed class Factory(string root, RecordingObserverInvoker invoker) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder
            .ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecEvidence:IndexEnabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            }))
            .ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IEmbeddingProvider, NoEmbeddings>());
                services.Replace(ServiceDescriptor.Singleton(new LiveExplainerOptions
                {
                    DebounceWindow = TimeSpan.FromMilliseconds(25),
                    ModelCallTimeout = TimeSpan.FromSeconds(4),
                    HeartbeatInterval = TimeSpan.FromSeconds(10)
                }));
                services.Replace(ServiceDescriptor.Singleton<ILiveExplainerModelInvoker>(invoker));
            });
    }
}
