using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed class OpenAiCompatibleProviderRuntime(OpenAiCompatibleClient client) : IModelProviderRuntime
{
    public string Id => "openai-compatible";

    public bool CanHandle(ResolvedModelInvocationContextDto context)
    {
        return !string.Equals(context.ConnectionKind, "cli", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ModelInvocationResultDto> GenerateAsync(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default)
    {
        var settings = new StoredModelSettings(
            context.EffectiveBaseUrl,
            context.EffectiveModel,
            context.EncryptedApiKey,
            DateTimeOffset.UtcNow);

        var content = await client.CreateAssistantReplyAsync(settings, apiKey, messages, cancellationToken);
        return new ModelInvocationResultDto(
            "executed",
            content,
            context,
            false,
            Id);
    }
}
