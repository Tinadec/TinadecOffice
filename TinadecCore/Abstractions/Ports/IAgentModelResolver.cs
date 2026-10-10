using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

public interface IAgentModelResolver
{
    Task<ModelResolutionPreviewDto> PreviewAsync(ModelResolutionPreviewRequestDto request, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<ModelResolutionPreviewDto>> PreviewBatchAsync(IReadOnlyList<ModelResolutionPreviewRequestDto> requests,
        CancellationToken cancellationToken = default)
    {
        var previews = new List<ModelResolutionPreviewDto>(requests.Count);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { previews.Add(await PreviewAsync(request, cancellationToken).ConfigureAwait(false)); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException)
            { previews.Add(ModelPreviewFailure.Unavailable(error.Message)); }
        }
        return previews;
    }
    Task<FrozenModelPlan> FreezeAsync(AgentModelFreezeRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(FrozenModelPlan plan, Guid? parentInstanceId, CancellationToken cancellationToken = default);
    Task<Guid> StartInvocationAsync(ModelInvocationStart request, CancellationToken cancellationToken = default);
    Task CompleteInvocationAsync(Guid invocationId, string status, ModelUsage? usage = null, string? errorCategory = null, string? safeErrorMessage = null, CancellationToken cancellationToken = default);
}

public static class ModelPreviewFailure
{
    public static ModelResolutionPreviewDto Unavailable(string reason) => new()
    {
        StrategySource = "unavailable",
        Candidates = [new ModelResolutionCandidatePreviewDto { Available = false, UnavailableReason = reason }]
    };
}

public sealed record AgentModelFreezeRequest(
    Guid SessionId,
    Guid ModeVersionId,
    Guid AgentDefinitionId,
    Guid AgentVersionId,
    string AgentId,
    string StrategyJson,
    string StrategySource,
    // True for the conversation root — the agent that talks to the user and
    // therefore receives the session/turn model override. Keyed on the frozen
    // conversation identity; the literal "meeting" slug is only the legacy fallback.
    bool IsConversationRoot,
    SessionModelOverride? MeetingModelOverride = null,
    bool UseSessionModelOverride = true);

public sealed record FrozenModelCandidate(
    int Position,
    Guid ProviderInstanceId,
    Guid ProviderVersionId,
    string? Model,
    string Protocol,
    Guid? RouteId = null,
    Guid? RouteVersionId = null);

public sealed record FrozenModelPlan(
    string StrategyKind,
    string StrategySource,
    IReadOnlyList<FrozenModelCandidate> Candidates,
    bool InheritFromParent = false);

public sealed record ModelInvocationStart(
    Guid CallId,
    int Attempt,
    Guid SessionId,
    Guid RunId,
    Guid? TurnId,
    Guid? AgentInstanceId,
    Guid AgentDefinitionId,
    Guid AgentVersionId,
    Guid ModeVersionId,
    string StrategySource,
    ChatResolution Resolution);
