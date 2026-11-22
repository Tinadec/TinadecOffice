using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Models.Harness.Headless;
using TinadecCore.Models.Harness.Tui;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Protocol-level tests for the local-agent clients and the opencode serve process host.
/// <para>
/// The ACP client is tested against a recording session host, and the full streamed-turn behaviour is
/// covered with real frames in <c>AcpStdioChatClientTests</c>. What belongs here is the factory's
/// routing decision: which client a protocol gets, where its argv comes from, and which channels this
/// build refuses to pretend it can drive.
/// </para>
/// </summary>
public sealed class CliRuntimeTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly List<IAsyncDisposable> _disposables = [];
    private FakeOpenCodeServer? _opencode;

    public async Task InitializeAsync()
    {
        _opencode = await FakeOpenCodeServer.StartAsync();
        _disposables.Add(_opencode);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    [Fact]
    public async Task OpenCodeChatClient_StreamsTextParts()
    {
        using var client = new OpenCodeChatClient(_opencode!.Url, null, NullLogger<OpenCodeChatClient>.Instance);

        Assert.Equal("Hello from fake opencode", await DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])));
        Assert.Equal(1, _opencode.Sessions);
        Assert.Equal(1, _opencode.Messages);
    }

    [Fact]
    public async Task Factory_CreateAsync_OpencodeProtocol_ReusesTheStoredUrlAndBinary()
    {
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.OpencodeServe,
            BinaryPath = "C:\\fake\\opencode.exe",
            ServerUrl = "http://127.0.0.1:4096",
            LaunchArgs = "serve --port 4096",
            HomePath = "C:\\fake\\home"
        };
        var processes = new StubProcesses();

        using var client = await CreateFactory(resolution, new RecordingAcpSessionHost(), processes).CreateAsync(resolution);

        Assert.Equal("OpenCodeChatClient", client.GetType().Name);
        Assert.Equal("C:\\fake\\opencode.exe", processes.Received?.BinaryPath);
        Assert.Equal("http://127.0.0.1:4096", processes.Received?.ServerUrl);
        Assert.Equal("serve --port 4096", processes.Received?.LaunchArgs);
        Assert.Equal("C:\\fake\\home", processes.Received?.HomePath);
    }

    /// <summary>
    /// An ACP provider is served by a stdio session, not by a spawned HTTP server, and its argv comes
    /// from the harness catalog rather than from the stored <c>launch_args</c>: a free-text argument
    /// string cannot be split into arguments safely, and the catalog is the only place that records
    /// what each harness was actually measured accepting.
    /// </summary>
    [Fact]
    public async Task Factory_CreateAsync_AcpProtocol_TakesCatalogArgvNotStoredLaunchArgs()
    {
        var provider = Guid.NewGuid();
        var host = new RecordingAcpSessionHost();
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.Acp,
            ProviderInstanceId = provider,
            ModelId = "codebuddy/GLM-4.6",
            BinaryPath = "C:\\fake\\codebuddy.exe",
            LaunchArgs = "--acp --yolo",
            HomePath = "C:\\fake\\home"
        };

        using var client = await CreateFactory(resolution, host).CreateAsync(resolution);

        Assert.Equal("AcpStdioChatClient", client.GetType().Name);
        // Building the client must not open a session: a model invocation resolves a route per call,
        // and the process is the conversation, so it is started by the first turn instead.
        Assert.Empty(host.Requests);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None);

        var request = Assert.Single(host.Requests);
        Assert.Equal(provider, request.ProviderInstanceId);
        Assert.Equal("codebuddy", request.HarnessId);
        Assert.Equal(["--acp", "--yolo"], request.Argv.ToArray());
        Assert.Equal("C:\\fake\\codebuddy.exe", request.BinaryPath);
        Assert.Equal("C:\\fake\\home", request.Environment?["HOME"]);
        // The session root is Core's governed scratch directory, never the caller's tree.
        Assert.StartsWith(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tinadec-acp-recorded-host")),
            Path.GetFullPath(request.ScratchDirectory),
            StringComparison.Ordinal);
        Assert.Empty(host.Dropped);

        // When LaunchArgs is omitted, catalog argv ("--acp") is used by default.
        host.Requests.Clear();
        var defaultResolution = resolution with { ProviderInstanceId = Guid.NewGuid(), LaunchArgs = null };
        using var defaultClient = await CreateFactory(defaultResolution, host).CreateAsync(defaultResolution);
        await defaultClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None);
        Assert.Equal(["--acp"], Assert.Single(host.Requests).Argv.ToArray());
    }

    [Fact]
    public async Task Factory_CreateAsync_AcpForUncataloguedDriver_RefusesToGuessArgv()
    {
        var host = new RecordingAcpSessionHost();
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.Acp,
            ProviderInstanceId = Guid.NewGuid(),
            ModelId = "cursor-acp/gpt",
            BinaryPath = "C:\\fake\\cursor-acp.exe"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFactory(resolution, host).CreateAsync(resolution));

        Assert.Contains("no 'acp' channel in the catalog", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Factory_CreateAsync_AcpWithoutProviderInstance_NamesTheMissingIdentity()
    {
        var resolution = new ChatResolution { Protocol = ChatProtocols.Acp, ModelId = "opencode/x", BinaryPath = "C:\\fake\\opencode.exe" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFactory(resolution, new RecordingAcpSessionHost()).CreateAsync(resolution));

        Assert.Contains("provider instance", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A channel the catalog declares but this build cannot drive yet must fail with its own name.
    /// Falling through to the default branch would build an OpenAI client and send a chat-completions
    /// request to a provider that has no endpoint, which reads as a model outage, not a missing driver.
    /// <para>
    /// <c>headless-cli</c> left this list when its chat client landed: the protocol is drivable now, and
    /// what stays refused is decided per harness, below.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ChatProtocols.Tui)]
    public async Task Factory_CreateAsync_ChannelWithoutClientYet_FailsClosedNamingTheProtocol(string protocol)
    {
        var resolution = new ChatResolution
        {
            Protocol = protocol,
            ProviderInstanceId = Guid.NewGuid(),
            ModelId = "claude-code/claude",
            BinaryPath = "C:\\fake\\claude.exe"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFactory(resolution, new RecordingAcpSessionHost()).CreateAsync(resolution));

        Assert.Contains(protocol, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factory_HeadlessChannel_ResolvesTheOneShotClientForAHarnessThatCanAnswer()
    {
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.HeadlessCli,
            ProviderInstanceId = Guid.NewGuid(),
            ModelId = "claude-code/claude",
            BinaryPath = "C:\\fake\\claude.exe"
        };

        using var client = await CreateFactory(
            resolution,
            new RecordingAcpSessionHost(),
            roots: new FixedWorkspaceRoots("C:\\state\\harness-scratch"),
            headless: new StubHeadlessRunner()).CreateAsync(resolution);

        Assert.Equal("TinadecCore.Models.Harness.Headless.HeadlessCliChatClient", client.GetType().FullName);
    }

    /// <summary>
    /// Kimi Code's headless stdout has only ever been seen failing on a reachable host — it is signed
    /// out here — so its answer frame is undeclared. Refusing by name is the honest answer: a client
    /// that parsed it with another vendor's dialect would return either nothing or the error text.
    /// </summary>
    [Fact]
    public async Task Factory_HeadlessChannel_RefusesAHarnessWhoseAnswerFrameWasNeverCaptured()
    {
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.HeadlessCli,
            ProviderInstanceId = Guid.NewGuid(),
            ModelId = "kimi-code/kimi",
            BinaryPath = "C:\\fake\\kimi.exe"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFactory(
                resolution,
                new RecordingAcpSessionHost(),
                roots: new FixedWorkspaceRoots("C:\\state\\harness-scratch"),
                headless: new StubHeadlessRunner()).CreateAsync(resolution));

        Assert.Contains("kimi-code", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no headless answer frame", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A process-backed protocol whose host is not registered must say so rather than build an HTTP
    /// client for a provider that has no endpoint.
    /// </summary>
    [Fact]
    public async Task Factory_HeadlessChannel_RequiresTheProcessRunnerToBeRegistered()
    {
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.HeadlessCli,
            ProviderInstanceId = Guid.NewGuid(),
            ModelId = "claude-code/claude",
            BinaryPath = "C:\\fake\\claude.exe"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFactory(resolution, new RecordingAcpSessionHost()).CreateAsync(resolution));

        Assert.Contains(nameof(IHeadlessHarnessRunner), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factory_TuiChannel_UsesTheTerminalChatClient()
    {
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.Tui,
            ProviderInstanceId = Guid.NewGuid(),
            ModelId = "codex/codex",
            BinaryPath = "C:\\fake\\codex.exe"
        };

        using var client = await CreateFactory(
            resolution,
            new RecordingAcpSessionHost(),
            roots: new FixedWorkspaceRoots("C:\\state\\harness-scratch"),
            tui: new StubTuiRunner()).CreateAsync(resolution);

        Assert.Equal("TinadecCore.Models.Harness.Tui.TuiChatClient", client.GetType().FullName);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with exactly: PONG")]);
        Assert.Equal("PONG", response.Text);
    }

    private sealed class FixedWorkspaceRoots(string root) : IHarnessWorkspaceRoots
    {
        public string ForProvider(Guid providerInstanceId) => root;
    }

    private sealed class StubHeadlessRunner : IHeadlessHarnessRunner
    {
        public Task<HarnessTurnOutcome> RunAsync(
            HarnessTurnRequest request,
            IHeadlessEnvelope envelope,
            Action<string>? textDelta,
            CancellationToken cancellationToken) => Task.FromResult(new HarnessTurnOutcome(0, ""));
    }

    private sealed class StubTuiRunner : ITuiHarnessRunner
    {
        public Task<TuiTurnOutcome> RunAsync(
            TuiTurnRequest request,
            string prompt,
            Action<string>? textDelta,
            CancellationToken cancellationToken)
        {
            textDelta?.Invoke("PONG");
            return Task.FromResult(new TuiTurnOutcome("PONG"));
        }
    }

    [Fact]
    public async Task OpencodeServe_ReachableUrl_IsReusedWithoutSpawning()
    {
        var manager = new OpencodeServeProcessManager(NullLogger<OpencodeServeProcessManager>.Instance);
        await using (manager)
        {
            // The binary path points at a file that does not exist: reaching it would have meant
            // spawning, so the successful reuse below is the proof that nothing was started.
            var endpoint = await manager.EnsureRunningAsync(new OpencodeServeConfig("C:\\missing\\opencode.exe", ServerUrl: _opencode!.Url));
            Assert.Equal(_opencode.Url, endpoint.ServerUrl);
        }
    }

    /// <summary>
    /// The port is decided before the child starts, and the child's stdout is never read for it. The
    /// stub deliberately prints a wrong port: a host that scraped its log would end up polling an
    /// address nobody listens on, which is exactly the fragile coupling the old manager had.
    /// </summary>
    [Fact]
    public async Task OpencodeServe_SpawnsOnThePortItChose_AndIgnoresWhatTheChildPrints()
    {
        var port = FreePort();
        var manager = new OpencodeServeProcessManager(NullLogger<OpencodeServeProcessManager>.Instance);
        OpencodeServeEndpoint endpoint;
        await using (manager)
        {
            endpoint = await manager.EnsureRunningAsync(new OpencodeServeConfig("node", LaunchArgs: $"-e \"{NodeServeStub}\" -- serve --port {port}"));
            Assert.Equal($"http://127.0.0.1:{port}", endpoint.ServerUrl);
        }

        using var http = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(endpoint.ServerUrl));
    }

    /// <summary>
    /// Two different operator mistakes, two different answers: a provider that was never given a
    /// binary to start (fix the configuration) and one whose binary cannot be launched (fix the path).
    /// Both must name what they checked, because the alternative is a 20-second readiness timeout that
    /// points at the URL nobody was going to reach.
    /// </summary>
    [Fact]
    public async Task OpencodeServe_NoBinaryConfigured_ThrowsNamingBinaryPath()
    {
        var url = $"http://127.0.0.1:{FreePort()}";
        var manager = new OpencodeServeProcessManager(NullLogger<OpencodeServeProcessManager>.Instance);
        await using (manager)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(
                new OpencodeServeConfig("", ServerUrl: url)));
            Assert.Contains("no binary_path", ex.Message, StringComparison.Ordinal);
            Assert.Contains(url, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OpencodeServe_BinaryThatDoesNotExist_ThrowsWithThePathItTried()
    {
        var url = $"http://127.0.0.1:{FreePort()}";
        var manager = new OpencodeServeProcessManager(NullLogger<OpencodeServeProcessManager>.Instance);
        await using (manager)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(
                new OpencodeServeConfig("C:\\missing\\opencode.exe", ServerUrl: url)));
            Assert.Contains("C:\\missing\\opencode.exe", ex.Message, StringComparison.Ordinal);
            Assert.Contains("could not be started", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DrainShutdown_ToleratesADrainerThatIsStillRunning_InsteadOfDisposingItsTask()
    {
        // The bug this pins: DisposeAsync called Task.Dispose() on the drainer, which throws while
        // the task is running. On Windows the killed child closed its pipes fast enough to hide it;
        // on Linux and macOS the drain was still open and both CI legs went red. A drainer that
        // never completes makes the timing irrelevant — disposing it throws on every platform.
        var neverFinishes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await CliDrainShutdown.AwaitQuietlyAsync(neverFinishes.Task, TimeSpan.FromMilliseconds(50), NullLogger.Instance);
    }

    [Fact]
    public async Task DrainShutdown_AwaitsTheDrainerSoTheLogTailIsNotDropped()
    {
        var linesWritten = 0;
        var drainer = Task.Run(async () =>
        {
            await Task.Delay(50);
            Interlocked.Increment(ref linesWritten);
        });

        await CliDrainShutdown.AwaitQuietlyAsync(drainer, TimeSpan.FromSeconds(5), NullLogger.Instance);

        Assert.Equal(1, linesWritten);
    }

    [Fact]
    public async Task DrainShutdown_SwallowsTheStreamErrorsAKilledChildLeavesBehind()
    {
        await CliDrainShutdown.AwaitQuietlyAsync(Task.FromException(new IOException("Broken pipe")), TimeSpan.FromSeconds(1), NullLogger.Instance);
        await CliDrainShutdown.AwaitQuietlyAsync(Task.FromCanceled(new CancellationToken(canceled: true)), TimeSpan.FromSeconds(1), NullLogger.Instance);
    }

    /// <summary>
    /// A spawn that never reaches its server must not leave the child behind. The host's registry only
    /// records successes, so an unregistered process keeps holding a loopback port, its own credentials,
    /// and the two redirected pipes — and a test run that fails this way inherits the leak as an
    /// orphan that outlives its host process. This is the same abandon path as the startup timeout,
    /// reached quickly by cancelling.
    /// </summary>
    [Fact]
    public async Task OpencodeServe_FailedStartup_KillsTheChildInsteadOfLeavingItRunning()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"tinadec-serve-orphan-{Guid.NewGuid():N}.pid");
        var port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var manager = new OpencodeServeProcessManager(NullLogger<OpencodeServeProcessManager>.Instance);
        await using (manager)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureRunningAsync(
                new OpencodeServeConfig("node", LaunchArgs: $"-e \"{NeverServeStub}\" -- \"{pidFile}\" --port {port}"),
                cts.Token));

            var pid = int.Parse(await File.ReadAllTextAsync(pidFile, CancellationToken.None));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (IsAlive(pid) && DateTime.UtcNow < deadline) await Task.Delay(100, CancellationToken.None);

            Assert.False(IsAlive(pid), $"opencode serve child pid {pid} outlived its failed startup");
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private const string NeverServeStub = """
        const fs = require('fs');
        fs.writeFileSync(process.argv[1], String(process.pid));
        setTimeout(() => {}, 10 * 60 * 1000);
        """;

    private const string NodeServeStub = """
        const http = require('http');
        const port = Number(process.argv[process.argv.length - 1] || 0);
        const s = http.createServer((q, r) => { r.writeHead(200, { 'content-type': 'text/plain' }); r.end('ok'); });
        s.listen(port, '127.0.0.1', () => { console.log('server is listening on port: 1'); });
        """;

    private static AgentChatClientFactory CreateFactory(
        ChatResolution resolution,
        IAcpSessionHost acp,
        StubProcesses? processes = null,
        IHarnessWorkspaceRoots? roots = null,
        IHeadlessHarnessRunner? headless = null,
        ITuiHarnessRunner? tui = null) =>
        new(new FixedResolver(resolution), processes ?? new StubProcesses(), acp, NullLogger<AgentChatClientFactory>.Instance, roots, headless, tui);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> DrainAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var text = new StringBuilder();
        var deadline = DateTime.UtcNow + Deadline;
        await foreach (var update in updates)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("stream did not complete in time");
            if (!string.IsNullOrEmpty(update.Text)) text.Append(update.Text);
        }
        return text.ToString();
    }

    private sealed class FixedResolver(ChatResolution resolution) : IChatResolver
    {
        public Task<ChatResolution> ResolveChatAsync(string? routePurpose = null, CancellationToken cancellationToken = default)
            => Task.FromResult(resolution);
    }

    /// <summary>
    /// Stands in for the opencode serve host. It has no side effects to observe, so the value of the
    /// double is the argument it records: which binary, which argv, which URL to reuse.
    /// </summary>
    private sealed class StubProcesses : IOpencodeServeProcessManager
    {
        private const string Url = "http://127.0.0.1:1";

        public OpencodeServeConfig? Received { get; private set; }

        public Task<OpencodeServeEndpoint> EnsureRunningAsync(OpencodeServeConfig config, CancellationToken cancellationToken = default)
        {
            Received = config;
            return Task.FromResult(new OpencodeServeEndpoint(Url));
        }
    }
}
