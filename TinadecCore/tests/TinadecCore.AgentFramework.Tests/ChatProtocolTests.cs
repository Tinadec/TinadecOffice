using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Protocol-aware chat client factory tests. Client construction is offline (no HTTP);
/// assertions verify the protocol branch selects the correct IChatClient implementation.
/// The API key is a non-secret placeholder matching the existing fake-resolver convention.
/// </summary>
public sealed class ChatProtocolTests
{
    private static ChatResolution Resolution(string? protocol) => new()
    {
        IsAvailable = true,
        BaseUrl = "https://api.example.com/v1",
        Model = "test-model",
        ApiKey = "x",
        ModelId = "driver/test-model",
        Protocol = protocol
    };

    [Fact]
    public void Normalize_KnownProtocols_RoundTrip()
    {
        Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.Normalize("openai-chat"));
        Assert.Equal(ChatProtocols.OpenAiResponses, ChatProtocols.Normalize("openai-responses"));
        Assert.Equal(ChatProtocols.AnthropicMessages, ChatProtocols.Normalize("anthropic-messages"));
        Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.Normalize("OPENAI-CHAT"));
    }

    [Fact]
    public void Normalize_BlankOrUnknown_FallsBackToOpenAiChat()
    {
        Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.Normalize(null));
        Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.Normalize(""));
        Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.Normalize("grpc"));
    }

    [Fact]
    public void InferFromDriver_MapsKnownDrivers()
    {
        Assert.Equal(ChatProtocols.AnthropicMessages, ChatProtocols.InferFromDriver("anthropic"));
        Assert.Equal(ChatProtocols.AnthropicMessages, ChatProtocols.InferFromDriver("claude"));
        Assert.Equal(ChatProtocols.OpenAiResponses, ChatProtocols.InferFromDriver("openai-responses"));
        Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.InferFromDriver("openai"));
        Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.InferFromDriver(null));
    }

    [Fact]
    public async Task Create_DefaultsToOpenAiChatClient()
    {
        var factory = new AgentChatClientFactory(new FixedResolver(Resolution(null)));

        using var client = await factory.CreateAsync(Resolution(null));

        Assert.Equal("Microsoft.Extensions.AI.OpenAIChatClient", client.GetType().FullName);
    }

    [Fact]
    public async Task Create_OpenAiChatProtocol_UsesChatCompletionsClient()
    {
        var factory = new AgentChatClientFactory(new FixedResolver(Resolution(ChatProtocols.OpenAiChat)));

        using var client = await factory.CreateAsync(Resolution(ChatProtocols.OpenAiChat));

        Assert.Equal("Microsoft.Extensions.AI.OpenAIChatClient", client.GetType().FullName);
    }

    [Fact]
    public async Task Create_OpenAiResponsesProtocol_UsesResponsesClient()
    {
        var factory = new AgentChatClientFactory(new FixedResolver(Resolution(ChatProtocols.OpenAiResponses)));

        using var client = await factory.CreateAsync(Resolution(ChatProtocols.OpenAiResponses));

        Assert.Equal("Microsoft.Extensions.AI.OpenAIResponsesChatClient", client.GetType().FullName);
    }

    [Fact]
    public async Task Create_AnthropicProtocol_UsesAnthropicMessagesEndpoint()
    {
        var factory = new AgentChatClientFactory(new FixedResolver(Resolution(ChatProtocols.AnthropicMessages)));

        using var client = await factory.CreateAsync(Resolution(ChatProtocols.AnthropicMessages));

        Assert.Equal("Anthropic.SDK.Messaging.MessagesEndpoint", client.GetType().FullName);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/{0}/{1}")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/{0}/{1}")]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/{0}/{1}")]
    [InlineData("https://proxy.example.com/anthropic/v1", "https://proxy.example.com/anthropic/{0}/{1}")]
    public void NormalizeAnthropicApiUrlFormat_StripsTrailingVersionSegment(string baseUrl, string expected)
    {
        Assert.Equal(expected, AgentChatClientFactory.NormalizeAnthropicApiUrlFormat(baseUrl));
    }

    private sealed class FixedResolver(ChatResolution resolution) : IChatResolver
    {
        public Task<ChatResolution> ResolveChatAsync(string? routePurpose = null, CancellationToken cancellationToken = default)
            => Task.FromResult(resolution);
    }
}
