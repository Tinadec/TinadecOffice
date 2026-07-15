namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Loop detection and budget enforcement.
/// Uses MAF LoopAgent/LoopEvaluator with hard iteration limits;
/// F# strategies supplement with repeat-call fingerprints, no-progress, and budget checks.
/// </summary>
public interface ILoopGuard
{
    Task<LoopGuardDecision> EvaluateAsync(
        string sessionId,
        string runId,
        LoopGuardContext context,
        CancellationToken cancellationToken = default);
}

public sealed class LoopGuardContext
{
    public int Iteration { get; init; }
    public int MaxIterations { get; init; } = 25;
    public int TokenBudget { get; init; }
    public int TokensUsed { get; init; }
    public int ToolCallCount { get; init; }
    public int MaxToolCalls { get; init; } = 50;
    public TimeSpan? ElapsedTime { get; init; }
    public TimeSpan? MaxDuration { get; init; }
    public IReadOnlyList<string> RecentToolCallFingerprints { get; init; } = [];
    public int ConsecutiveErrors { get; init; }
    public int MaxConsecutiveErrors { get; init; } = 3;
}

public sealed class LoopGuardDecision
{
    public bool ShouldContinue { get; init; } = true;
    public string? Reason { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
