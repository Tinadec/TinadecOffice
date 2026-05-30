using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;

namespace TinadecCore.Services;

public sealed class CliProviderRuntime : IModelProviderRuntime
{
    public string Id => "cli-provider";

    public bool CanHandle(ResolvedModelInvocationContextDto context)
    {
        return string.Equals(context.ConnectionKind, "cli", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ModelInvocationResultDto> GenerateAsync(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default)
    {
        var name = context.Provider?.DisplayName ?? context.ProviderInstanceId;
        var content = $"Model provider '{name}' is configured as a CLI provider. TinadecCode can store and route to this provider now; the CLI runtime adapter will execute turns in a later slice.";

        return Task.FromResult(new ModelInvocationResultDto(
            "executed",
            content,
            context,
            true,
            Id));
    }
}
