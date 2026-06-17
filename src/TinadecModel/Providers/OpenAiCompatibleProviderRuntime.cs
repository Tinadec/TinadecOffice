using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Storage;
using TinadecModel.Services;

namespace TinadecModel.Providers;

public sealed class OpenAiCompatibleProviderRuntime(
    OpenAiCompatibleClient client,
    IModelStore? store = null,
    int maxRetryAttempts = 3) : IModelProviderRuntime
{
    private readonly ProviderExecutionPolicy _policy = new(Math.Max(1, maxRetryAttempts));

    public string Id => "openai-compatible";

    public bool CanHandle(ResolvedModelInvocationContextDto context)
        => !ProviderTemplateRules.IsLocalOpenAiCompatibleDriver(context.Driver)
            && !ProviderTemplateRules.IsLocalOpenAiCompatibleDriver(context.Provider?.Driver)
            && (ProviderTemplateRules.IsOpenAiCompatibleDriver(context.Driver)
                || ProviderTemplateRules.IsOpenAiCompatibleDriver(context.Provider?.Driver));

    public async Task<ModelInvocationResultDto> GenerateAsync(
        ResolvedModelInvocationContextDto context, string? apiKey,
        IReadOnlyList<MessageDto> messages, CancellationToken cancellationToken = default,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var outcome = await ProviderPolicyHelpers.ExecuteAsync(
            context.ProviderInstanceId,
            async executionToken =>
            {
                var settings = new StoredModelSettings(
                    context.EffectiveBaseUrl, context.EffectiveModel,
                    context.EncryptedApiKey, DateTimeOffset.UtcNow);
                return await client.CreateAssistantResponseAsync(
                    settings, apiKey, messages, context.ProviderInstanceId,
                    executionToken, tools);
            },
            exception => ProviderErrorMapper.FromException(context.ProviderInstanceId, exception),
            _policy, store, cancellationToken);

        if (outcome.Succeeded)
        {
            var response = outcome.Value!;
            return new ModelInvocationResultDto("executed", response.TextContent, context, false, Id,
                ToolCalls: response.ToolCalls);
        }

        var failure = outcome.Failure!;
        return new ModelInvocationResultDto("failed", failure.SafeMessage, context, false, Id,
            failure.Category, failure.Retryable, failure.StatusCode, failure.ExitCode,
            failure.SafeMessage, failure.ProviderId);
    }
}
