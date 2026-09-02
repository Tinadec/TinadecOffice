namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// An orchestration directive extracted from a meeting-model output
/// (e.g. a trailing <c>LANE_OPEN: {json}</c> line) before validation.
/// </summary>
public sealed record OrchestrationDirectiveCandidate(string Verb, string LaneKey, string PayloadJson);

/// <summary>Code-owned facts the validator may rely on; never model claims.</summary>
/// <param name="FrozenPermissionMode">
/// The run's normalized lowercase permission mode (for example <c>full-access</c>).
/// A lane that declares mutating tools is admitted when the run already runs
/// unattended, or when the payload carries a pre-authorization reference; the
/// real release still happens in the approval minting layer, so a mismatched
/// grant parks the lane instead of widening authority.
/// </param>
public sealed record OrchestrationDirectiveRules(
    bool LanesEnabled,
    int CurrentLaneCount,
    int MaxLanesPerRun,
    int MaxTasksPerLane,
    IReadOnlyCollection<string> FrozenToolIds,
    IReadOnlyCollection<string> MutatingToolIds,
    IReadOnlyCollection<string> KnownLaneKeys,
    string? FrozenPermissionMode = null);

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
