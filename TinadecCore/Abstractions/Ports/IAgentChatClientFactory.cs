using Microsoft.Extensions.AI;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The seam for obtaining chat clients. Production resolves the configured route from
/// <see cref="IChatResolver"/> and builds a protocol-appropriate client; tests substitute a
/// deterministic fake so the full-duplex pipeline can be exercised end to end.
/// </summary>
/// <remarks>
/// The port is here rather than in the module that consumes it because the implementation belongs to
/// the model-interface module: choosing a wire protocol, building an SDK client, and starting or
/// probing a local harness are all "how Core reaches a model", which is not the execution module's
/// business. DmaEA references only Abstractions and Persistence, so the port is the whole surface it
/// is allowed to see — and the reason a harness transport can move without any engine file changing.
/// </remarks>
public interface IAgentChatClientFactory
{
    Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one explicitly named provider model, bypassing the route table — the seam the
    /// companion domain uses to honour a companion's pinned model. Default: unavailable, so
    /// existing fakes and resolvers keep compiling and stay honest about what they cannot do.
    /// </summary>
    Task<ChatResolution> ResolveModelChatAsync(Guid providerInstanceId, string? modelName = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResolution { IsAvailable = false, Error = "This factory cannot resolve a pinned provider model." });

    /// <summary>
    /// Builds the protocol-appropriate client. Asynchronous because CLI protocols
    /// (<see cref="ChatProtocols.Acp"/>, <see cref="ChatProtocols.OpencodeServe"/>) may spawn
    /// or probe a local agent server process.
    /// </summary>
    Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default);
}
