using System.ClientModel;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA.CliRuntime;

namespace TinadecCore.DmaEA;

/// <summary>
/// Single seam for obtaining chat clients inside the DmaEA runtime. Production resolves
/// the configured route from <see cref="IChatResolver"/> and builds a protocol-appropriate
/// client; tests substitute a deterministic fake so the full duplex pipeline can be
/// exercised end-to-end.
/// </summary>
public interface IAgentChatClientFactory
{
    Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the protocol-appropriate client. Asynchronous because CLI protocols
    /// (<see cref="ChatProtocols.Acp"/>, <see cref="ChatProtocols.OpencodeServe"/>) may spawn
    /// or probe a local agent server process.
    /// </summary>
    Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default);
}

/// <summary>
/// Protocol-aware chat client factory. Selects the wire protocol from
/// <see cref="ChatResolution.Protocol"/>: OpenAI-compatible chat completions (default),
/// the OpenAI Responses API, the Anthropic Messages API, the Agent Client Protocol (ACP),
/// or the opencode serve protocol.
/// </summary>
public sealed class AgentChatClientFactory : IAgentChatClientFactory
{
    private readonly IChatResolver _resolver;
    private readonly ICliProcessManager? _processes;
    private readonly ILogger<AgentChatClientFactory>? _logger;

    public AgentChatClientFactory(IChatResolver resolver)
    {
        _resolver = resolver;
    }

    public AgentChatClientFactory(IChatResolver resolver, ICliProcessManager processes, ILogger<AgentChatClientFactory> logger)
    {
        _resolver = resolver;
        _processes = processes;
        _logger = logger;
    }

    public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
        _resolver.ResolveChatAsync(routePurpose, cancellationToken);

    public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
        ChatProtocols.Normalize(resolution.Protocol) switch
        {
            ChatProtocols.OpenAiResponses => Task.FromResult(CreateResponsesClient(resolution)),
            ChatProtocols.AnthropicMessages => Task.FromResult(CreateAnthropicClient(resolution)),
            ChatProtocols.Acp => CreateAcpClientAsync(resolution, cancellationToken),
            ChatProtocols.OpencodeServe => CreateOpenCodeClientAsync(resolution, cancellationToken),
            _ => Task.FromResult(CreateOpenAiChatClient(resolution))
        };

    /// <summary>OpenAI-compatible <c>/chat/completions</c> client.</summary>
    public static IChatClient CreateOpenAiChatClient(ChatResolution resolution)
        => new OpenAIClient(new ApiKeyCredential(resolution.ApiKey!), new OpenAIClientOptions { Endpoint = new Uri(resolution.BaseUrl!) })
            .GetChatClient(resolution.Model!)
            .AsIChatClient();

    /// <summary>OpenAI Responses API client.</summary>
    public static IChatClient CreateResponsesClient(ChatResolution resolution)
    {
        // OPENAI001: the Responses IChatClient adapter is marked experimental by the
        // OpenAI SDK; we accept the surface deliberately and pin the SDK version centrally.
#pragma warning disable OPENAI001
        return new OpenAIClient(new ApiKeyCredential(resolution.ApiKey!), new OpenAIClientOptions { Endpoint = new Uri(resolution.BaseUrl!) })
            .GetResponsesClient()
            .AsIChatClient(resolution.Model!);
#pragma warning restore OPENAI001
    }

    /// <summary>
    /// Anthropic Messages API client. The SDK's <c>MessagesEndpoint</c> implements
    /// <see cref="IChatClient"/> directly; the base URL is normalized to the SDK's
    /// <c>{version}/{endpoint}</c> format so stored <c>.../v1</c> URLs do not double-prefix.
    /// </summary>
    public static IChatClient CreateAnthropicClient(ChatResolution resolution)
    {
        var client = new AnthropicClient(new APIAuthentication(resolution.ApiKey!))
        {
            ApiUrlFormat = NormalizeAnthropicApiUrlFormat(resolution.BaseUrl!)
        };
        return client.Messages;
    }

    /// <summary>
    /// Converts a stored Anthropic base URL into the SDK's <c>ApiUrlFormat</c>
    /// (<c>https://host/{version}/{endpoint}</c>). The SDK appends its own
    /// <c>v1</c> version segment, so a trailing <c>/v1</c> on the configured URL is
    /// stripped instead of duplicated.
    /// </summary>
    public static string NormalizeAnthropicApiUrlFormat(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }
        return $"{trimmed}/{{0}}/{{1}}";
    }

    private string DriverOf(ChatResolution resolution)
    {
        var driver = resolution.ModelId?.Split('/')[0];
        return string.IsNullOrWhiteSpace(driver) ? "cli" : driver;
    }

    private async Task<IChatClient> CreateAcpClientAsync(ChatResolution resolution, CancellationToken cancellationToken)
    {
        var processes = RequiredProcesses();
        var endpoint = await processes.EnsureRunningAsync(ConfigOf(resolution), cancellationToken).ConfigureAwait(false);
        return new AcpChatClient(endpoint.ServerUrl, endpoint.Token ?? resolution.Token, NullLogger<AcpChatClient>.Instance);
    }

    private async Task<IChatClient> CreateOpenCodeClientAsync(ChatResolution resolution, CancellationToken cancellationToken)
    {
        var processes = RequiredProcesses();
        var endpoint = await processes.EnsureRunningAsync(ConfigOf(resolution), cancellationToken).ConfigureAwait(false);
        return new OpenCodeChatClient(endpoint.ServerUrl, endpoint.Token ?? resolution.Token, NullLogger<OpenCodeChatClient>.Instance);
    }

    private CliRuntimeConfig ConfigOf(ChatResolution resolution) => new(
        DriverOf(resolution),
        resolution.BinaryPath ?? throw new InvalidOperationException($"CLI protocol {resolution.Protocol} requires a binary_path."),
        resolution.LaunchArgs,
        resolution.ServerUrl,
        resolution.HomePath);

    private ICliProcessManager RequiredProcesses()
        => _processes ?? throw new InvalidOperationException($"CLI protocol {ChatProtocols.Acp}/{ChatProtocols.OpencodeServe} requires ICliProcessManager to be registered.");
}
