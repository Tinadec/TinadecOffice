namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Dual-layer agent orchestration (DmaEA module).
/// Planning layer: proactive planning and supervision.
/// Execution layer: passive task execution.
/// Dynamic creation, task dispatch, collaboration, scheduling, result aggregation.
/// </summary>
public interface IAgentOrchestrator
{
    Task<OrchestrationResult> OrchestrateAsync(
        string sessionId,
        string userGoal,
        CancellationToken cancellationToken = default);
}

public sealed class OrchestrationResult
{
    public string RunId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
