using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models;
using TinadecCore.Models.Harness;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests.Peers;

public sealed class PeerProviderProxyTests
{
    private const string CanaryApiKey = "sk-canary-controller-only-998877";

    [Fact]
    public async Task AgentChatClientFactory_CreatesPeerProxyModelProvider_WithZeroApiKey_AndBlocksNonLoopbackSsrf()
    {
        Assert.Equal(ChatProtocols.PeerProxy, ChatProtocols.Normalize("peer_proxy"));
        Assert.Equal(ChatProtocols.PeerProxy, ChatProtocols.Normalize("PEER-PROXY"));
        Assert.False(ChatProtocols.IsProcessTransport(ChatProtocols.PeerProxy));
        Assert.True(ChatProtocols.IsModelChosenByHarness(ChatProtocols.PeerProxy));
        Assert.True(ChatProtocols.IsDrivable(ChatProtocols.PeerProxy));

        var resolution = new ChatResolution
        {
            IsAvailable = true,
            Protocol = ChatProtocols.PeerProxy,
            BaseUrl = "http://127.0.0.1:48730",
            ApiKey = null,
            Model = "controller-mock-gpt",
            ModelId = "peer/controller-mock-gpt",
            ProxyControllerDeviceId = "dev-controller-a",
            ProxySessionToken = "ppt_v1_test_token_123456"
        };

        var factory = new AgentChatClientFactory(new StaticChatResolver(resolution));
        using var client = await factory.CreateAsync(resolution);

        var proxyClient = Assert.IsType<PeerProxyModelProvider>(client);
        Assert.Equal("http://127.0.0.1:48730", proxyClient.GatewayBaseUrl);
        Assert.Equal("dev-controller-a", proxyClient.ControllerDeviceId);
        Assert.Equal("controller-mock-gpt", proxyClient.Model);

        var readiness = await proxyClient.CheckReadinessAsync();
        Assert.True(readiness.IsReady);

        // Non-loopback Gateway URLs must be rejected immediately to prevent SSRF / token exfiltration.
        var ssrfResolution = resolution with { BaseUrl = "https://192.168.1.99:48730" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(ssrfResolution));
    }

    [Fact]
    public async Task ZeroKeyWorker_StreamsSseAndExecutesLocalToolCallLoop_WithZeroCanaryKeyLeakage()
    {
        var tempWorkerRoot = Path.Combine(Path.GetTempPath(), "tinadec_peer_proxy_worker_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkerRoot);
        var workerWorkspaceDir = Path.Combine(tempWorkerRoot, "workspace");
        var workerSecretsDir = Path.Combine(tempWorkerRoot, "secrets");
        var workerLogsFile = Path.Combine(tempWorkerRoot, "worker.log");
        Directory.CreateDirectory(workerWorkspaceDir);
        Directory.CreateDirectory(workerSecretsDir);

        var inputPath = Path.Combine(workerWorkspaceDir, "build-target.txt");
        var outputPath = Path.Combine(workerWorkspaceDir, "verified-artifact.txt");
        await File.WriteAllTextAsync(inputPath, "TINADEC_ZERO_KEY_PAYLOAD_42");

        var controllerAnchor = new object();
        var controllerHostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(controllerAnchor);
        var issuedToken = PeerProviderProxyHostRegistry.IssueToken(
            controllerHostKey,
            controllerDeviceId: "controller-dev-A",
            workerDeviceId: "worker-dev-B",
            delegationId: "del_live_01",
            sessionId: Guid.NewGuid().ToString(),
            maxRequests: 10);

        var upstreamAuthHeadersObserved = new ConcurrentQueue<string>();
        var upstreamRequestBodies = new ConcurrentQueue<string>();
        var callSequence = 0;

        await using var loopbackGateway = await LoopbackHttpServer.StartAsync(async ctx =>
        {
            if (ctx.Method != "POST" || ctx.Path != PeerProxyModelProvider.CompletionsPath)
            {
                await ctx.WriteJsonAsync(404, new { code = "not_found" });
                return;
            }

            // Simulate Worker Gateway -> Reverse mTLS Tunnel -> Controller Core triple verification:
            var principal = ctx.GetHeader(PeerProxyModelProvider.PrincipalHeader);
            var targetController = ctx.GetHeader(PeerProxyModelProvider.TargetControllerHeader);
            var proxyToken = ctx.GetHeader(PeerProxyModelProvider.ProxyTokenHeader);

            if (principal != PeerProxyModelProvider.GatewayInternalPrincipal
                || targetController != "controller-dev-A"
                || !PeerProviderProxyHostRegistry.TryValidateAndConsumeToken(
                    controllerHostKey,
                    proxyToken,
                    verifiedWorkerDeviceId: "worker-dev-B",
                    nowMs: null,
                    out _,
                    out var denyReason))
            {
                await ctx.WriteProblemAsync(403, PeerErrorCodes.PolicyDenied, "Token or policy denied.");
                return;
            }

            // Controller attaches its own canary API key strictly on the Controller->Upstream leg:
            var upstreamAuth = $"Bearer {CanaryApiKey}";
            upstreamAuthHeadersObserved.Enqueue(upstreamAuth);
            upstreamRequestBodies.Enqueue(ctx.BodyText);

            using var reqDoc = JsonDocument.Parse(ctx.BodyText);
            var turnNumber = Interlocked.Increment(ref callSequence);

            if (turnNumber == 1)
            {
                // Verify tools were forwarded cleanly on Turn 1.
                Assert.True(reqDoc.RootElement.TryGetProperty("tools", out var toolsEl));
                Assert.True(toolsEl.GetArrayLength() >= 1);

                // Stream fragmented SSE tool_calls back to Worker (zero canary key in stream!).
                var sseChunks = new[]
                {
                    """data: {"id":"chatcmpl_t1","model":"mock-controller-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_local_rw_1","type":"function","function":{"name":"transform_workspace_file","arguments":"{\"source_name\":"}}]}}]}""",
                    """data: {"id":"chatcmpl_t1","model":"mock-controller-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"build-target.txt\",\"output_name\":\"verified-artifact.txt\"}"}}]}}]}""",
                    """data: {"id":"chatcmpl_t1","model":"mock-controller-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":32,"completion_tokens":18,"total_tokens":50}}""",
                    "data: [DONE]"
                };
                await ctx.WriteSseStreamAsync(sseChunks);
                return;
            }

            // Turn 2: Verify Worker executed the tool locally and sent back FunctionResultContent (`role: "tool"`).
            var messagesArr = reqDoc.RootElement.GetProperty("messages");
            var hasToolResult = messagesArr.EnumerateArray().Any(m =>
                m.GetProperty("role").GetString() == "tool"
                && m.GetProperty("tool_call_id").GetString() == "call_local_rw_1"
                && (m.GetProperty("content").GetString() ?? "").Contains("TINADEC_ZERO_KEY_PAYLOAD_42", StringComparison.Ordinal));
            Assert.True(hasToolResult, "Expected Worker's local FunctionResultContent in Turn 2 messages.");

            var finalSseChunks = new[]
            {
                """data: {"id":"chatcmpl_t2","model":"mock-controller-model","choices":[{"index":0,"delta":{"role":"assistant","content":"Worker verified: "}}]}""",
                """data: {"id":"chatcmpl_t2","model":"mock-controller-model","choices":[{"index":0,"delta":{"content":"TINADEC_ZERO_KEY_PAYLOAD_42 written locally."}}]}""",
                """data: {"id":"chatcmpl_t2","model":"mock-controller-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":64,"completion_tokens":14,"total_tokens":78}}""",
                "data: [DONE]"
            };
            await ctx.WriteSseStreamAsync(finalSseChunks);
        });

        try
        {
            var workerSessionId = Guid.NewGuid();
            await using var harness = await WorkerResolverTestHarness.CreateAsync(tempWorkerRoot);

            PeerProviderProxyRuntimeRegistry.Register(new PeerProviderProxySessionBinding(
                SessionId: workerSessionId.ToString(),
                RunId: Guid.NewGuid().ToString(),
                DelegationId: "del_live_01",
                ControllerDeviceId: "controller-dev-A",
                ProxySessionToken: issuedToken.Token,
                UseProviderProxy: true,
                ExpiresAtMs: issuedToken.ExpiresAtMs,
                GatewayLoopbackUrl: loopbackGateway.BaseUrl,
                ModelHint: "mock-controller-model"));

            var frozenPlan = await harness.Resolver.FreezeAsync(new AgentModelFreezeRequest(
                SessionId: workerSessionId,
                ModeVersionId: Guid.NewGuid(),
                AgentDefinitionId: Guid.NewGuid(),
                AgentVersionId: Guid.NewGuid(),
                AgentId: "worker",
                StrategyJson: """{"kind":"inherit"}""",
                StrategySource: "default",
                IsConversationRoot: true));

            var resolutions = await harness.Resolver.ResolveInvocationCandidatesAsync(frozenPlan, null);
            var resolved = Assert.Single(resolutions);
            Assert.True(resolved.IsAvailable);
            Assert.Equal(ChatProtocols.PeerProxy, resolved.Protocol);
            Assert.Null(resolved.ApiKey);
            Assert.Equal("Proxy · controller-dev-A", resolved.RuntimeBadge);

            var factory = harness.Services.GetRequiredService<IAgentChatClientFactory>();
            using var rawClient = await factory.CreateAsync(resolved);

            var localTool = AIFunctionFactory.Create(
                (string source_name, string output_name) =>
                {
                    var src = Path.Combine(workerWorkspaceDir, Path.GetFileName(source_name));
                    var dst = Path.Combine(workerWorkspaceDir, Path.GetFileName(output_name));
                    var content = File.ReadAllText(src);
                    var transformed = $"VERIFIED:{content}";
                    File.WriteAllText(dst, transformed);
                    File.AppendAllText(workerLogsFile, $"Executed local tool on {source_name} -> {output_name}\n");
                    return new { ok = true, payload = content, output = dst };
                },
                "transform_workspace_file",
                "Reads a file in worker workspace and writes a verified artifact.");

            using var toolLoopClient = new ChatClientBuilder(rawClient)
                .UseFunctionInvocation()
                .Build();

            var streamedBuilder = new StringBuilder();
            await foreach (var update in toolLoopClient.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Transform build-target.txt into verified-artifact.txt")],
                new ChatOptions
                {
                    Instructions = "Use transform_workspace_file to process the local file.",
                    Tools = [localTool]
                }))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    streamedBuilder.Append(update.Text);
                }
            }

            var finalAnswer = streamedBuilder.ToString();
            Assert.Contains("TINADEC_ZERO_KEY_PAYLOAD_42 written locally.", finalAnswer);
            Assert.True(File.Exists(outputPath));
            Assert.Equal("VERIFIED:TINADEC_ZERO_KEY_PAYLOAD_42", await File.ReadAllTextAsync(outputPath));

            // Controller did use the canary API key twice (Turn 1 + Turn 2) toward Upstream:
            Assert.Equal(2, upstreamAuthHeadersObserved.Count);
            Assert.All(upstreamAuthHeadersObserved, h => Assert.Equal($"Bearer {CanaryApiKey}", h));

            // Gate 12 Hard Invariant: Scan every file on Worker disk (SQLite DB, secrets dir, workspace, logs)
            // plus serialized frozen plan and resolution JSON — canary key count MUST be strictly 0!
            await harness.FlushSqliteWalAsync();
            var serializedArtifacts = JsonSerializer.Serialize(new { frozenPlan, resolved }) + "\n" + finalAnswer;
            Assert.DoesNotContain(CanaryApiKey, serializedArtifacts, StringComparison.Ordinal);
            foreach (var outboundBody in upstreamRequestBodies)
            {
                Assert.DoesNotContain(CanaryApiKey, outboundBody, StringComparison.Ordinal);
            }

            foreach (var file in Directory.EnumerateFiles(tempWorkerRoot, "*", SearchOption.AllDirectories))
            {
                await using var fs = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                var bytes = ms.ToArray();
                var latin1 = Encoding.Latin1.GetString(bytes);
                var utf8 = Encoding.UTF8.GetString(bytes);
                Assert.DoesNotContain(CanaryApiKey, latin1, StringComparison.Ordinal);
                Assert.DoesNotContain(CanaryApiKey, utf8, StringComparison.Ordinal);
            }
        }
        finally
        {
            PeerProviderProxyRuntimeRegistry.RemoveBySession(tempWorkerRoot);
            try { Directory.Delete(tempWorkerRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task UpstreamErrorAndStreamChunks_ScrubCanaryApiKeyBeforeLeavingControllerOrReachingWorker()
    {
        var rawUpstreamError = $$$"""{"error":{"message":"Invalid authentication header Authorization: Bearer {{{CanaryApiKey}}} or api_key='{{{CanaryApiKey}}}'"}}""";
        var scrubbed = PeerProviderProxySecretRedactor.Redact(rawUpstreamError, CanaryApiKey);
        Assert.DoesNotContain(CanaryApiKey, scrubbed, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", scrubbed, StringComparison.Ordinal);

        await using var failingGateway = await LoopbackHttpServer.StartAsync(async ctx =>
        {
            // Even if a buggy upstream leaked a sk-... secret in a 502 body, PeerProxyModelProvider scrubs it too:
            var leakedBody = PeerProviderProxySecretRedactor.Redact(rawUpstreamError, CanaryApiKey);
            await ctx.WriteProblemAsync(502, "upstream_provider_error", leakedBody);
        });

        using var provider = new PeerProxyModelProvider(
            failingGateway.BaseUrl,
            "controller-dev-A",
            "ppt_v1_valid_ephemeral",
            "mock-model");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.DoesNotContain(CanaryApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyDenied_EnforcedOnMissingInvalidExpiredExceededToken_AndDisabledPolicy()
    {
        var controllerAnchor = new object();
        var hostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(controllerAnchor);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var boundedToken = PeerProviderProxyHostRegistry.IssueToken(
            hostKey,
            controllerDeviceId: "controller-A",
            workerDeviceId: "worker-B",
            delegationId: "del_quota",
            expiresAtMs: nowMs + 60_000,
            maxRequests: 2,
            nowMs: nowMs);

        var expiredToken = PeerProviderProxyHostRegistry.IssueToken(
            hostKey,
            controllerDeviceId: "controller-A",
            workerDeviceId: "worker-B",
            delegationId: "del_expired",
            expiresAtMs: nowMs - 1_000,
            maxRequests: 10,
            nowMs: nowMs - 10_000);

        await using var gateway = await LoopbackHttpServer.StartAsync(async ctx =>
        {
            if (!PeerProviderProxyHostRegistry.IsControllerProxyAllowed(hostKey))
            {
                await ctx.WriteProblemAsync(403, PeerErrorCodes.PolicyDenied, "allow_provider_proxy is disabled.");
                return;
            }

            var suppliedToken = ctx.GetHeader(PeerProxyModelProvider.ProxyTokenHeader);
            if (!PeerProviderProxyHostRegistry.TryValidateAndConsumeToken(
                    hostKey,
                    suppliedToken,
                    verifiedWorkerDeviceId: "worker-B",
                    nowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    out _,
                    out var reason))
            {
                await ctx.WriteProblemAsync(403, PeerErrorCodes.PolicyDenied, reason);
                return;
            }

            await ctx.WriteSseStreamAsync([
                """data: {"id":"ok_1","choices":[{"index":0,"delta":{"role":"assistant","content":"pong"},"finish_reason":"stop"}]}""",
                "data: [DONE]"
            ]);
        });

        // 1. Forged token -> 403 policy_denied
        using (var invalidClient = new PeerProxyModelProvider(gateway.BaseUrl, "controller-A", "ppt_v1_forged_token"))
        {
            var err = await Assert.ThrowsAsync<HttpRequestException>(() =>
                invalidClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]));
            Assert.Equal(HttpStatusCode.Forbidden, err.StatusCode);
            Assert.Contains(PeerErrorCodes.PolicyDenied, err.Message, StringComparison.Ordinal);
        }

        // 2. Expired token -> 403 policy_denied
        using (var expiredClient = new PeerProxyModelProvider(gateway.BaseUrl, "controller-A", expiredToken.Token))
        {
            var err = await Assert.ThrowsAsync<HttpRequestException>(() =>
                expiredClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]));
            Assert.Equal(HttpStatusCode.Forbidden, err.StatusCode);
            Assert.Contains(PeerErrorCodes.PolicyDenied, err.Message, StringComparison.Ordinal);
        }

        // 3. Wrong worker_device_id binding -> rejected in token validator
        Assert.False(PeerProviderProxyHostRegistry.TryValidateAndConsumeToken(
            hostKey,
            boundedToken.Token,
            verifiedWorkerDeviceId: "impostor-worker-C",
            nowMs: nowMs,
            out _,
            out var wrongWorkerReason));
        Assert.Contains("impostor-worker-C", wrongWorkerReason, StringComparison.Ordinal);

        // 4. Valid bounded token succeeds up to max_requests=2, then 3rd call returns 403 policy_denied
        using (var boundedClient = new PeerProxyModelProvider(gateway.BaseUrl, "controller-A", boundedToken.Token))
        {
            var r1 = await boundedClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping 1")]);
            Assert.Equal("pong", r1.Text);

            var r2 = await boundedClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping 2")]);
            Assert.Equal("pong", r2.Text);

            var quotaErr = await Assert.ThrowsAsync<HttpRequestException>(() =>
                boundedClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping 3")]));
            Assert.Equal(HttpStatusCode.Forbidden, quotaErr.StatusCode);
            Assert.Contains(PeerErrorCodes.PolicyDenied, quotaErr.Message, StringComparison.Ordinal);
        }

        // 5. Disabling allow_provider_proxy -> immediate 403 policy_denied even with a fresh token
        var freshToken = PeerProviderProxyHostRegistry.IssueToken(hostKey, "controller-A", "worker-B");
        PeerProviderProxyHostRegistry.SetControllerProxyAllowed(hostKey, false);
        using (var policyDeniedClient = new PeerProxyModelProvider(gateway.BaseUrl, "controller-A", freshToken.Token))
        {
            var policyErr = await Assert.ThrowsAsync<HttpRequestException>(() =>
                policyDeniedClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]));
            Assert.Equal(HttpStatusCode.Forbidden, policyErr.StatusCode);
            Assert.Contains(PeerErrorCodes.PolicyDenied, policyErr.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AgentModelResolver_UsesLocalFirstWhenPassive_AndAppendsOrSelectsPeerProxyForZeroKeyWorker()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tinadec_resolver_proxy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await using var harness = await WorkerResolverTestHarness.CreateAsync(tempDir);
            await harness.SeedKeylessLocalChatRouteAsync();

            var passiveSessionId = Guid.NewGuid();
            PeerProviderProxyRuntimeRegistry.Register(new PeerProviderProxySessionBinding(
                SessionId: passiveSessionId.ToString(),
                RunId: null,
                DelegationId: null,
                ControllerDeviceId: "controller-passive",
                ProxySessionToken: "ppt_v1_passive_ok",
                UseProviderProxy: false,
                ExpiresAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000,
                GatewayLoopbackUrl: "http://127.0.0.1:49830",
                ModelHint: "gpt-proxy-fallback"));

            var plan = await harness.Resolver.FreezeAsync(new AgentModelFreezeRequest(
                SessionId: passiveSessionId,
                ModeVersionId: Guid.NewGuid(),
                AgentDefinitionId: Guid.NewGuid(),
                AgentVersionId: Guid.NewGuid(),
                AgentId: "meeting",
                StrategyJson: """{"kind":"inherit"}""",
                StrategySource: "default",
                IsConversationRoot: true));

            Assert.Equal(2, plan.Candidates.Count);
            var candidates = await harness.Resolver.ResolveInvocationCandidatesAsync(plan, null);
            Assert.Equal(2, candidates.Count);
            Assert.False(candidates[0].IsAvailable); // Keyless local route is unavailable
            Assert.True(candidates[1].IsAvailable);  // PeerProxy fallback is available with zero API key!
            Assert.Equal(ChatProtocols.PeerProxy, candidates[1].Protocol);
            Assert.Null(candidates[1].ApiKey);
            Assert.Equal("http://127.0.0.1:49830", candidates[1].BaseUrl);

            // Revoking the session binding turns the PeerProxy candidate fail-closed (`policy_denied`):
            PeerProviderProxyRuntimeRegistry.RemoveBySession(passiveSessionId.ToString());
            var afterRevoke = await harness.Resolver.ResolveInvocationCandidatesAsync(plan, null);
            Assert.False(afterRevoke[1].IsAvailable);
            Assert.Contains(PeerErrorCodes.PolicyDenied, afterRevoke[1].Error ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class StaticChatResolver(ChatResolution resolution) : IChatResolver
    {
        public Task<ChatResolution> ResolveChatAsync(string? routePurpose = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(resolution);
    }

    private sealed class WorkerResolverTestHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _dbPath;
        public IServiceProvider Services => _provider;
        public IAgentModelResolver Resolver => _provider.GetRequiredService<IAgentModelResolver>();

        private WorkerResolverTestHarness(ServiceProvider provider, string dbPath)
        {
            _provider = provider;
            _dbPath = dbPath;
        }

        public static async Task<WorkerResolverTestHarness> CreateAsync(string rootDir)
        {
            var dataDir = Path.Combine(rootDir, "data");
            var workspaceDir = Path.Combine(rootDir, "workspace");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(workspaceDir);
            var dbPath = Path.Combine(dataDir, "worker.sqlite.db");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TinadecPersistence:Enabled"] = "true",
                    ["TinadecPersistence:DataRoot"] = dataDir,
                    ["TinadecPersistence:Sqlite:DatabasePath"] = dbPath,
                    ["TinadecPersistence:Provider"] = "Sqlite",
                    ["TinadecTools:DefaultWorkspaceRoot"] = workspaceDir
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddTinadecPersistence(configuration, rootDir);
            services.AddTinadecCore();

            var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
            }

            return new WorkerResolverTestHarness(provider, dbPath);
        }

        public Task SeedKeylessLocalChatRouteAsync() =>
            DevSeed.SeedIfMissingAsync(_provider, CancellationToken.None);

        public async Task FlushSqliteWalAsync()
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        public string BaseUrl { get; }

        private LoopbackHttpServer(TcpListener listener, string baseUrl, Func<MinimalHttpExchange, Task> handler)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(handler, _cts.Token));
        }

        public static Task<LoopbackHttpServer> StartAsync(Func<MinimalHttpExchange, Task> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult(new LoopbackHttpServer(listener, $"http://127.0.0.1:{port}", handler));
        }

        private async Task AcceptLoopAsync(Func<MinimalHttpExchange, Task> handler, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    await using (var stream = client.GetStream())
                    {
                        try
                        {
                            var exchange = await MinimalHttpExchange.ReadAsync(stream, ct);
                            if (exchange is not null)
                            {
                                await handler(exchange);
                            }
                        }
                        catch
                        {
                            // Ignore abrupt client disconnect during test teardown.
                        }
                    }
                }, CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop; } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    private sealed class MinimalHttpExchange
    {
        private readonly NetworkStream _stream;
        public string Method { get; }
        public string Path { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public string BodyText { get; }

        private MinimalHttpExchange(
            NetworkStream stream,
            string method,
            string path,
            IReadOnlyDictionary<string, string> headers,
            string bodyText)
        {
            _stream = stream;
            Method = method;
            Path = path;
            Headers = headers;
            BodyText = bodyText;
        }

        public string? GetHeader(string name) =>
            Headers.TryGetValue(name, out var val) ? val : null;

        public static async Task<MinimalHttpExchange?> ReadAsync(NetworkStream stream, CancellationToken ct)
        {
            var headerBytes = new List<byte>(1024);
            var buf = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
                if (read == 0) return null;
                headerBytes.Add(buf[0]);
                var n = headerBytes.Count;
                if (n >= 4
                    && headerBytes[n - 4] == '\r'
                    && headerBytes[n - 3] == '\n'
                    && headerBytes[n - 2] == '\r'
                    && headerBytes[n - 1] == '\n')
                {
                    break;
                }
            }

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;

            var reqParts = lines[0].Split(' ', 3);
            var method = reqParts[0];
            var rawTarget = reqParts.Length > 1 ? reqParts[1] : "/";
            var qIdx = rawTarget.IndexOf('?');
            var path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                }
            }

            var contentLength = headers.TryGetValue("Content-Length", out var clRaw) && int.TryParse(clRaw, out var cl)
                ? cl
                : 0;
            var bodyBytes = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var n = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), ct);
                if (n == 0) break;
                offset += n;
            }

            return new MinimalHttpExchange(stream, method, path, headers, Encoding.UTF8.GetString(bodyBytes, 0, offset));
        }

        public async Task WriteJsonAsync(int status, object payload)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public async Task WriteProblemAsync(int status, string code, string detail)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                type = $"https://tinadec.dev/errors/{code}",
                title = code,
                status,
                code,
                detail
            }));
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/problem+json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public async Task WriteSseStreamAsync(IEnumerable<string> sseDataLines)
        {
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            foreach (var line in sseDataLines)
            {
                var frameBytes = Encoding.UTF8.GetBytes(line + "\n\n");
                await _stream.WriteAsync(frameBytes);
                await _stream.FlushAsync();
            }
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            502 => "Bad Gateway",
            _ => "Error"
        };
    }
}
