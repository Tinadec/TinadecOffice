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
}
