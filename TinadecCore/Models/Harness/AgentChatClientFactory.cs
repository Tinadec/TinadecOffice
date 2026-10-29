using System.ClientModel;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Models.Harness.Headless;
using TinadecCore.Models.Harness.Tui;

namespace TinadecCore.Models.Harness;

/// <summary>
/// Protocol-aware chat client factory. Selects the wire protocol from
/// <see cref="ChatResolution.Protocol"/>: OpenAI-compatible chat completions (default),
/// the OpenAI Responses API, the Anthropic Messages API, a stdio ACP harness session, or the
/// opencode serve HTTP surface.
/// </summary>
internal sealed class AgentChatClientFactory : IAgentChatClientFactory
{
    private readonly IChatResolver _resolver;
    private readonly IOpencodeServeProcessManager? _processes;
    private readonly IAcpSessionHost? _acp;
    private readonly IHarnessWorkspaceRoots? _roots;
    private readonly IHeadlessHarnessRunner? _headless;
    private readonly ITuiHarnessRunner? _tui;
    private readonly ILogger<AgentChatClientFactory>? _logger;

    public AgentChatClientFactory(IChatResolver resolver)
    {
        _resolver = resolver;
    }

    public AgentChatClientFactory(
        IChatResolver resolver,
        IOpencodeServeProcessManager processes,
        IAcpSessionHost acp,
        ILogger<AgentChatClientFactory> logger,
        IHarnessWorkspaceRoots? roots = null,
        IHeadlessHarnessRunner? headless = null,
        ITuiHarnessRunner? tui = null)
    {
        _resolver = resolver;
        _processes = processes;
        _acp = acp;
        _logger = logger;
        _roots = roots;
        _headless = headless;
        _tui = tui;
    }

    public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
        _resolver.ResolveChatAsync(routePurpose, cancellationToken);

    public Task<ChatResolution> ResolveModelChatAsync(Guid providerInstanceId, string? modelName = null, CancellationToken cancellationToken = default) =>
        _resolver.ResolveModelChatAsync(providerInstanceId, modelName, cancellationToken);

    public async Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default)
    {
        var client = await (ChatProtocols.Normalize(resolution.Protocol) switch
        {
            ChatProtocols.OpenAiResponses => Task.FromResult(CreateResponsesClient(resolution)),
            ChatProtocols.AnthropicMessages => Task.FromResult(CreateAnthropicClient(resolution)),
            ChatProtocols.Acp => CreateAcpClientAsync(resolution, cancellationToken),
            ChatProtocols.OpencodeServe => CreateOpenCodeClientAsync(resolution, cancellationToken),
            ChatProtocols.HeadlessCli => CreateHeadlessClientAsync(resolution),
            ChatProtocols.Tui => CreateTuiClientAsync(resolution),
            _ => Task.FromResult(CreateOpenAiChatClient(resolution))
        }).ConfigureAwait(false);
        return ConfigureParameters(client, resolution);
    }

    internal static IChatClient ConfigureParameters(IChatClient client, ChatResolution resolution)
        => resolution.Parameters is { IsEmpty: false } settings
            ? new ConfigureOptionsChatClient(client, options => settings.ApplyTo(options, resolution.Model ?? ""))
            : client;

    /// <summary>
    /// A process-backed protocol with no client in this build fails here instead of falling through
    /// to the OpenAI branch: a run against a <c>headless-cli</c> provider would otherwise send an
    /// OpenAI-shaped request to a URL the provider never configured.
    /// </summary>
    private static Task<IChatClient> UnsupportedAsync(string protocol) => Task.FromException<IChatClient>(
        new InvalidOperationException($"Chat protocol '{protocol}' is a harness channel this build can configure but not yet drive; the chat client is a later batch."));

    /// <summary>OpenAI-compatible <c>/chat/completions</c> client.</summary>
    public static IChatClient CreateOpenAiChatClient(ChatResolution resolution)
        => CreateBrandedOpenAiClient(resolution).GetChatClient(resolution.Model!).AsIChatClient();

    /// <summary>OpenAI Responses API client.</summary>
    public static IChatClient CreateResponsesClient(ChatResolution resolution)
    {
        // OPENAI001: the Responses IChatClient adapter is marked experimental by the
        // OpenAI SDK; we accept the surface deliberately and pin the SDK version centrally.
#pragma warning disable OPENAI001
        return CreateBrandedOpenAiClient(resolution).GetResponsesClient().AsIChatClient(resolution.Model!);
#pragma warning restore OPENAI001
    }

    private static OpenAIClient CreateBrandedOpenAiClient(ChatResolution resolution)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(resolution.BaseUrl!),
            UserAgentApplicationId = TinadecBranding.Name
        };
        return new OpenAIClient(new ApiKeyCredential(resolution.ApiKey!), options);
    }

    /// <summary>
    /// Anthropic Messages API client. The SDK's <c>MessagesEndpoint</c> implements
    /// <see cref="IChatClient"/> directly; the base URL is normalized to the SDK's
    /// <c>{version}/{endpoint}</c> format so stored <c>.../v1</c> URLs do not double-prefix.
    /// </summary>
    public static IChatClient CreateAnthropicClient(ChatResolution resolution)
    {
        var http = TinadecBranding.CreateClient();
        var client = new AnthropicClient(new APIAuthentication(resolution.ApiKey!), http, null)
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

    /// <summary>
    /// Opens (or reuses) the provider's stdio ACP session. No process host is involved: an ACP harness
    /// is spawned by the session transport and speaks NDJSON on its own streams, so there is no port
    /// to allocate and no URL to poll.
    /// </summary>
    private Task<IChatClient> CreateAcpClientAsync(ChatResolution resolution, CancellationToken cancellationToken)
    {
        var host = _acp ?? throw new InvalidOperationException($"Chat protocol {ChatProtocols.Acp} requires IAcpSessionHost to be registered.");
        var provider = resolution.ProviderInstanceId
            ?? throw new InvalidOperationException($"Chat protocol {ChatProtocols.Acp} needs a provider instance to own the session.");
        var driver = DriverOf(resolution);
        var request = new AcpSessionRequest(
            provider,
            driver,
            resolution.BinaryPath ?? throw new InvalidOperationException($"Process protocol {ChatProtocols.Acp} requires a binary_path."),
            HarnessCatalog.ChannelArgv(driver, AgentChannels.Acp),
            host.ScratchDirectoryFor(provider),
            EnvironmentOf(resolution));
        return Task.FromResult<IChatClient>(new AcpStdioChatClient(host, request));
    }

    private static IReadOnlyDictionary<string, string?>? EnvironmentOf(ChatResolution resolution) =>
        string.IsNullOrWhiteSpace(resolution.HomePath)
            ? null
            : new Dictionary<string, string?> { ["HOME"] = resolution.HomePath };

    /// <summary>
    /// Opens the one-shot headless channel for a provider. The catalog decides the argv, where the
    /// prompt goes, and which stdout vocabulary counts as an answer, so a harness this build has only
    /// ever seen fail to answer is refused here by name instead of being parsed on assumption.
    /// </summary>
    private Task<IChatClient> CreateHeadlessClientAsync(ChatResolution resolution)
    {
        var driver = DriverOf(resolution);
        var channel = HarnessCatalog.Find(driver)?.Channel(AgentChannels.Cli);
        var envelopeKind = channel?.Envelope ?? HarnessHeadlessEnvelopes.None;
        if (channel is null || HeadlessEnvelopes.For(envelopeKind) is null)
        {
            return Task.FromException<IChatClient>(new InvalidOperationException(
                $"'{driver}' has no headless answer frame that this build has captured, so protocol " +
                $"'{ChatProtocols.HeadlessCli}' stays refused rather than parsed by guesswork."));
        }

        var runner = _headless ?? throw new InvalidOperationException(
            $"Protocol {ChatProtocols.HeadlessCli} requires {nameof(IHeadlessHarnessRunner)} to be registered.");
        var roots = _roots ?? throw new InvalidOperationException(
            $"Protocol {ChatProtocols.HeadlessCli} requires {nameof(IHarnessWorkspaceRoots)} to be registered.");
        var provider = resolution.ProviderInstanceId
            ?? throw new InvalidOperationException($"Chat protocol {ChatProtocols.HeadlessCli} needs a provider instance to own its working directory.");

        return Task.FromResult<IChatClient>(new HeadlessCliChatClient(
            driver,
            channel,
            envelopeKind,
            resolution.BinaryPath ?? throw new InvalidOperationException(
                $"Process protocol {ChatProtocols.HeadlessCli} requires a binary_path."),
            roots.ForProvider(provider),
            EnvironmentOf(resolution),
            runner));
    }

    private Task<IChatClient> CreateTuiClientAsync(ChatResolution resolution)
    {
        var driver = DriverOf(resolution);
        var channel = HarnessCatalog.Find(driver)?.Channel(AgentChannels.Tui);
        if (channel is null)
        {
            return Task.FromException<IChatClient>(new InvalidOperationException(
                $"Harness '{driver}' has no '{AgentChannels.Tui}' channel in the catalog."));
        }

        var runner = _tui ?? throw new InvalidOperationException(
            $"Protocol {ChatProtocols.Tui} requires {nameof(ITuiHarnessRunner)} to be registered.");
        var roots = _roots ?? throw new InvalidOperationException(
            $"Protocol {ChatProtocols.Tui} requires {nameof(IHarnessWorkspaceRoots)} to be registered.");
        var provider = resolution.ProviderInstanceId
            ?? throw new InvalidOperationException($"Chat protocol {ChatProtocols.Tui} needs a provider instance to own its working directory.");

        return Task.FromResult<IChatClient>(new TuiChatClient(
            driver,
            resolution.BinaryPath ?? throw new InvalidOperationException(
                $"Process protocol {ChatProtocols.Tui} requires a binary_path."),
            roots.ForProvider(provider),
            HarnessCatalog.ChannelArgv(driver, AgentChannels.Tui),
            EnvironmentOf(resolution),
            runner));
    }

    private async Task<IChatClient> CreateOpenCodeClientAsync(ChatResolution resolution, CancellationToken cancellationToken)
    {
        var processes = _processes ?? throw new InvalidOperationException(
            $"Chat protocol {ChatProtocols.OpencodeServe} requires IOpencodeServeProcessManager to be registered.");
        var endpoint = await processes.EnsureRunningAsync(new OpencodeServeConfig(
            resolution.BinaryPath ?? throw new InvalidOperationException($"Chat protocol {ChatProtocols.OpencodeServe} needs a binary_path to start its server."),
            resolution.LaunchArgs,
            resolution.ServerUrl,
            resolution.HomePath), cancellationToken).ConfigureAwait(false);
        return new OpenCodeChatClient(endpoint.ServerUrl, resolution.Token, NullLogger<OpenCodeChatClient>.Instance);
    }
}
