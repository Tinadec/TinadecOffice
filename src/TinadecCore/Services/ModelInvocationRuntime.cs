using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;

namespace TinadecCore.Services;

public sealed class ModelInvocationRuntime(
    IModelRouteResolver routeResolver,
    IModelCredentialResolver credentialResolver,
    IEnumerable<IModelProviderRuntime> providerRuntimes) : IModelInvocationRuntime
{
    public async Task<ModelInvocationResultDto> InvokeAsync(
        string sessionId,
        string purpose,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default)
    {
        var context = routeResolver.Resolve(purpose);
        var apiKey = credentialResolver.ResolveApiKey(context);

        var runtime = providerRuntimes.FirstOrDefault(item => item.CanHandle(context));
        if (runtime is null)
        {
            var content = $"No model runtime is registered for provider '{context.ProviderInstanceId}' (connection kind: {context.ConnectionKind}).";
            return new ModelInvocationResultDto("failed", content, context, true, null);
        }

        return await runtime.GenerateAsync(context, apiKey, messages, cancellationToken);
    }
}
