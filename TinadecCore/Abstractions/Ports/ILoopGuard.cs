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
    /// <summary>Completed rounds for this task; the engine reports ToolRounds - 1 so
    /// its own "a round may dispatch while ToolRounds == limit" boundary holds.</summary>
    public int Iteration { get; init; }

    /// <summary>Positive = the effective per-task round limit. Zero or less means
    /// "no round gate" (unlimited rounds), mirroring the TOML semantics.</summary>
    public int MaxIterations { get; init; } = 25;

    /// <summary>Context budget for this task's own loop, in tokens. Zero or less
    /// disables the check.</summary>
    public int TokenBudget { get; init; }

    /// <summary>Tokens this task has consumed so far (all turns).</summary>
    public int TokensUsed { get; init; }

    /// <summary>Calls dispatched so far by this task.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Absolute per-task call fuse. Zero or less disables the check, so a
    /// caller that supplies nothing is not silently capped; the engine passes the
    /// frozen policy value.</summary>
    public int MaxToolCalls { get; init; }

    /// <summary>Most recent call fingerprints, newest last. Repeat detection only
    /// inspects the trailing run, so callers should bound this to the last few
    /// entries instead of rebuilding the whole transcript every round.</summary>
    public IReadOnlyList<string> RecentToolCallFingerprints { get; init; } = [];

    /// <summary>Trailing consecutive failed calls; a success resets it.</summary>
    public int ConsecutiveErrors { get; init; }
    public int MaxConsecutiveErrors { get; init; } = 3;
}

public sealed class LoopGuardDecision
{
    public bool ShouldContinue { get; init; } = true;
    public string? Reason { get; init; }

    /// <summary>
    /// <see cref="TinadecCore.Abstractions.RunErrorTaxonomy"/> category for the
    /// veto, so callers can report "why it stopped" without parsing prose.
    /// Null on an allow decision.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// True when the veto came from an absolute fuse (the per-task tool-call
    /// ceiling) rather than a budget. Hitting a fuse usually means a bug, so the
    /// event and supervision surfaces mark it as an abnormal path.
    /// </summary>
    public bool HardCeiling { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
