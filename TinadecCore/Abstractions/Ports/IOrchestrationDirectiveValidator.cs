namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// An orchestration directive extracted from a meeting-model output
/// (e.g. a trailing <c>LANE_OPEN: {json}</c> line) before validation.
/// </summary>
public sealed record OrchestrationDirectiveCandidate(string Verb, string LaneKey, string PayloadJson);

/// <summary>Code-owned facts the validator may rely on; never model claims.</summary>
public sealed record OrchestrationDirectiveRules(
    bool LanesEnabled,
    int CurrentLaneCount,
    int MaxLanesPerRun,
    int MaxTasksPerLane,
    IReadOnlyCollection<string> FrozenToolIds,
    IReadOnlyCollection<string> MutatingToolIds,
    IReadOnlyCollection<string> KnownLaneKeys);

public sealed record OrchestrationDirectiveRejection(string Code, string Reason);

/// <summary>
/// Validates a meeting-authored orchestration directive against code facts
/// (frozen manifest, lane budgets, payload graph). Any rejection discards the
/// whole directive; a rejected directive never materializes a lane.
/// </summary>
public interface IOrchestrationDirectiveValidator
{
    /// <returns>null when the directive is acceptable; a rejection otherwise.</returns>
    OrchestrationDirectiveRejection? Validate(OrchestrationDirectiveCandidate candidate, OrchestrationDirectiveRules rules);
}
