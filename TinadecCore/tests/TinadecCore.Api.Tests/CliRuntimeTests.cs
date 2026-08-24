using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.DmaEA.CliRuntime;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Protocol-level tests for the ACP and opencode-serve chat clients against in-process
/// fake servers, the process host (spawn / port+token parsing / kill), and the factory's
/// CLI composition. Real CLIs are not required; the fakes speak the exact wire protocol.
/// </summary>
public sealed class CliRuntimeTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly List<IAsyncDisposable> _disposables = [];
    private FakeAcpServer? _acp;
    private FakeOpenCodeServer? _opencode;

    public async Task InitializeAsync()
    {
        _acp = await FakeAcpServer.StartAsync();
        _disposables.Add(_acp);
        _opencode = await FakeOpenCodeServer.StartAsync();
        _disposables.Add(_opencode);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    [Fact]
    public async Task AcpChatClient_StreamsTextDeltasAndReusesSession()
    {
        using var client = new AcpChatClient(_acp!.Url, null, NullLogger<AcpChatClient>.Instance);

        Assert.Equal("Hello from fake ACP", await DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])));
        Assert.Equal(1, _acp.InitializeCalls);
        Assert.Equal(1, _acp.Sessions.Count);

        Assert.Equal("Hello from fake ACP", await DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "again")])));
        Assert.Equal(1, _acp.Sessions.Count);
        Assert.Equal(2, _acp.MessageCreates);
    }

    [Fact]
    public async Task AcpChatClient_PermissionRequestThrows()
    {
        _acp!.SendPermissionRequest = true;
        using var client = new AcpChatClient(_acp.Url, null, NullLogger<AcpChatClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])));
        Assert.Contains("permission", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcpChatClient_MessageErrorThrows()
    {
        _acp!.FailMessage = "model exploded";
        using var client = new AcpChatClient(_acp.Url, null, NullLogger<AcpChatClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])));
        Assert.Contains("model exploded", ex.Message);
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
    public async Task Factory_CreateAsync_AcpProtocol_ReturnsAcpChatClient()
    {
        var resolution = new ChatResolution { Protocol = ChatProtocols.Acp, BinaryPath = "C:\\fake\\claude.exe" };
        var factory = new AgentChatClientFactory(new FixedResolver(resolution), new StubProcesses(_acp!.Url), NullLogger<AgentChatClientFactory>.Instance);

        using var client = await factory.CreateAsync(resolution);
        Assert.Equal("AcpChatClient", client.GetType().Name);
    }

    [Fact]
    public async Task Factory_CreateAsync_OpencodeProtocol_ReturnsOpenCodeChatClient()
    {
        var resolution = new ChatResolution { Protocol = ChatProtocols.OpencodeServe, BinaryPath = "C:\\fake\\opencode.exe" };
        var factory = new AgentChatClientFactory(new FixedResolver(resolution), new StubProcesses(_opencode!.Url), NullLogger<AgentChatClientFactory>.Instance);

        using var client = await factory.CreateAsync(resolution);
        Assert.Equal("OpenCodeChatClient", client.GetType().Name);
    }

    [Fact]
    public async Task CliProcessManager_ReusesReachableServerUrlWithoutSpawning()
    {
        var manager = new CliProcessManager(NullLogger<CliProcessManager>.Instance);
        await using (manager)
        {
            var endpoint = await manager.EnsureRunningAsync(new CliRuntimeConfig("claude-cli", "C:\\missing\\claude.exe", ServerUrl: _acp!.Url));
            Assert.Equal(ChatProtocols.Acp, endpoint.Protocol);
            Assert.Equal(_acp.Url, endpoint.ServerUrl);
        }
    }

    [Fact]
    public async Task CliProcessManager_SpawnsServer_ParsesPortAndToken_KillsOnDispose()
    {
        var manager = new CliProcessManager(NullLogger<CliProcessManager>.Instance);
        CliRuntimeEndpoint endpoint;
        await using (manager)
        {
            endpoint = await manager.EnsureRunningAsync(new CliRuntimeConfig("codex-cli", "node", LaunchArgs: $"-e \"{NodeStub}\" -- --acp-port 0"));
            Assert.Equal(ChatProtocols.Acp, endpoint.Protocol);
            Assert.True(Uri.TryCreate(endpoint.ServerUrl, UriKind.Absolute, out var uri), endpoint.ServerUrl);
            Assert.Equal("secret-token-1234567890", endpoint.Token);
        }

        using var http = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(endpoint.ServerUrl));
    }

    [Fact]
    public async Task CliProcessManager_MissingBinaryAndUnreachableServer_Throws()
    {
        var manager = new CliProcessManager(NullLogger<CliProcessManager>.Instance);
        await using (manager)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(new CliRuntimeConfig("codex-cli", "C:\\missing\\codex.exe", ServerUrl: "http://127.0.0.1:59999")));
            Assert.Contains("binary_path", ex.Message);
        }
    }

    private const string NodeStub = """
        const http = require('http');
        const port = Number(process.argv[process.argv.length - 1] || 0);
        const s = http.createServer((q, r) => { r.writeHead(200, { 'content-type': 'text/plain' }); r.end('ok'); });
        s.listen(port, '127.0.0.1', () => { const p = s.address().port; console.log('port: ' + p); console.log('token: secret-token-1234567890'); });
        """;

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

    private sealed class StubProcesses(string url) : ICliProcessManager
    {
        public Task<CliRuntimeEndpoint> EnsureRunningAsync(CliRuntimeConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(new CliRuntimeEndpoint(ChatProtocols.Acp, url, null));
    }
}