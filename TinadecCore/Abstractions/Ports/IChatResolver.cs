namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Resolves the configured <c>chat</c> route into a concrete provider endpoint,
/// model name, and API key without exposing credential storage details to callers.
/// Returns a structured unavailable result rather than throwing when configuration is missing.
/// </summary>
public interface IChatResolver
{
    Task<ChatResolution> ResolveChatAsync(
        string? routePurpose = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one explicitly named provider model, bypassing the route table. The companion
    /// domain pins a model per identity; the provider row must belong to the caller's tenant.
    /// Returns a structured unavailable result rather than throwing when the pair is missing.
    /// Default: unsupported, so resolvers that only know routes stay honest instead of guessing.
    /// </summary>
    Task<ChatResolution> ResolveModelChatAsync(
        Guid providerInstanceId,
        string? modelName = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResolution { IsAvailable = false, Error = "This resolver cannot resolve a pinned provider model." });
}
