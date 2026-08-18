using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Single seam for obtaining chat clients inside the DmaEA runtime. Production resolves
/// the OpenAI-compatible endpoint from <see cref="IChatResolver"/>; tests substitute a
/// deterministic fake so the full duplex pipeline can be exercised end-to-end.
/// </summary>
public interface IAgentChatClientFactory
{
    Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default);

    IChatClient Create(ChatResolution resolution);
}

public sealed class OpenAiAgentChatClientFactory : IAgentChatClientFactory
{
    private readonly IChatResolver _resolver;

    public OpenAiAgentChatClientFactory(IChatResolver resolver) => _resolver = resolver;

    public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
        _resolver.ResolveChatAsync(routePurpose, cancellationToken);

    public IChatClient Create(ChatResolution resolution) => PlanningAgent.DefaultChatClient(resolution);
}
