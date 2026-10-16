using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Runtime;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The readiness probe is the only place that tells an operator whether the configured chat route
/// actually answers. It had no test at all, which is how a change to its skip-list — the branch that
/// must not send an HTTP request at a harness — could land unseen.
/// </summary>
public sealed class ModelProbeServiceTests
{
    private sealed class FakeResolver(ChatResolution result) : IChatResolver
    {
        public ChatResolution Result { get; set; } = result;
        public Task<ChatResolution> ResolveChatAsync(string? routePurpose = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private sealed class CountingFactory : IAgentChatClientFactory
    {
        public int Calls;
        public ChatResolution? LastResolution;

        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the probe service resolves through IChatResolver");

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastResolution = resolution;
            return Task.FromResult<IChatClient>(new AnsweringClient());
        }
    }

    private sealed class AnsweringClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("probe");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static ChatResolution HttpRoute(string protocol = ChatProtocols.OpenAiChat) => new()
    {
        IsAvailable = true,
        Protocol = protocol,
        BaseUrl = "http://127.0.0.1:48735/v1",
        Model = "probe-model",
        ModelId = "model-id",
        ProviderInstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ProviderVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
    };

    private static (ModelProbeService Service, FakeResolver Resolver, CountingFactory Factory) Create(ChatResolution resolution)
    {
        var resolver = new FakeResolver(resolution);
        var factory = new CountingFactory();
        return (new ModelProbeService(resolver, factory, NullLogger<ModelProbeService>.Instance), resolver, factory);
    }

    /// <summary>
    /// A harness chooses its own model and is reached over a process, not an endpoint, so the server-side
    /// completion probe is not merely useless there — sending one would be a lie about what was tested.
    /// </summary>
    [Theory]
    [InlineData(ChatProtocols.Acp)]
    [InlineData(ChatProtocols.OpencodeServe)]
    [InlineData(ChatProtocols.HeadlessCli)]
    [InlineData(ChatProtocols.Tui)]
    public async Task HarnessChosenProtocol_IsReportedAsNotApplicable_WithoutBuildingAClient(string protocol)
    {
        var (service, _, factory) = Create(HttpRoute(protocol));

        var item = await service.ProbeAsync(force: true);

        Assert.Equal("unavailable", item.Status);
        Assert.Equal("probe_not_applicable", item.ReasonCode);
        Assert.Contains(protocol, item.Reason);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task HttpRoute_IsProbedOnce_AndServedFromCacheWithinTheWindow()
    {
        var (service, _, factory) = Create(HttpRoute());

        var first = await service.ProbeAsync(force: true);
        var second = await service.ProbeAsync();

        Assert.Equal("ready", first.Status);
        Assert.Equal("ready", second.Status);
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>
    /// The cache key is provider identity *and* protocol. A provider row keeps every id and its model
    /// name when its dialect changes, so a key without the protocol would keep answering "ready" — for
    /// an HTTP completion — about a provider that is now a stdio session.
    /// </summary>
    [Fact]
    public async Task ChangingTheProtocolOnTheSameRow_InvalidatesTheCachedAnswer()
    {
        var (service, resolver, factory) = Create(HttpRoute(ChatProtocols.OpenAiChat));
        await service.ProbeAsync(force: true);
        Assert.Equal(1, factory.Calls);

        resolver.Result = HttpRoute(ChatProtocols.OpenAiResponses);
        var second = await service.ProbeAsync();

        Assert.Equal(2, factory.Calls);
        Assert.Contains(ChatProtocols.OpenAiResponses, System.Text.Json.JsonSerializer.Serialize(second.Data));
    }

    [Fact]
    public async Task MissingRoute_IsBlockedWithTheResolversReason()
    {
        var (service, _, factory) = Create(new ChatResolution { IsAvailable = false, Error = "chat 路由未配置" });

        var item = await service.ProbeAsync(force: true);

        Assert.Equal("blocked", item.Status);
        Assert.Equal("probe_route_unavailable", item.ReasonCode);
        Assert.Contains("chat 路由未配置", item.Reason);
        Assert.Equal(0, factory.Calls);
    }
}
