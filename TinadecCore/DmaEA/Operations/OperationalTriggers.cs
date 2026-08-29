namespace TinadecCore.DmaEA;

/// <summary>
/// Deterministic engine anchors where operational roles may be activated.
/// The engine evaluates matches at these points and dispatches bypass
/// governance calls; evaluation itself never mutates run state.
/// </summary>
public enum OperationalTriggerPoint
{
    /// <summary>Task graph materialized after planning (or replanning).</summary>
    TaskGraphCreated,

    /// <summary>A task node reached a terminal worker result.</summary>
    TaskCompleted,

    /// <summary>The run completed finalization and is marked completed.</summary>
    RunFinalized,

    /// <summary>Worker selection failed closed for a task's capability/tool needs.</summary>
    CapabilityMissing
}

/// <summary>A frozen operational agent matched against a trigger point.</summary>
public sealed record OperationalTriggerMatch(
    RuntimeAgentDefinition Agent,
    OperationalTriggerPoint Point,
    string TriggerName);

/// <summary>
/// Decides which frozen operational agents match a trigger point. Pure and
/// side-effect free: the engine owns dispatch, model calls, and failure
/// isolation.
/// </summary>
public interface IOperationalTriggerEvaluator
{
    IReadOnlyList<OperationalTriggerMatch> Evaluate(
        OperationalTriggerPoint point,
        FrozenRunConfigurationV1 configuration);
}

/// <summary>
/// Role-based trigger matching gated by <see cref="TriggersPolicy"/>. Trigger
/// names mirror the full-duplex configuration vocabulary (task_created /
/// task_closed / run_closed / capability_missing); a roster entry that declares
/// explicit <c>Triggers</c> must contain the resolved name to match.
/// </summary>
public sealed class OperationalTriggerEvaluator : IOperationalTriggerEvaluator
{
    public IReadOnlyList<OperationalTriggerMatch> Evaluate(
        OperationalTriggerPoint point,
        FrozenRunConfigurationV1 configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var policy = configuration.Triggers;
        if (!policy.Enabled) return [];

        var matches = new List<OperationalTriggerMatch>();
        foreach (var agent in configuration.OperationAgents)
        {
            if (!agent.Enabled) continue;
            if (Resolve(agent, policy, point) is not { } triggerName) continue;
            if (agent.Triggers.Count > 0
                && !agent.Triggers.Contains(triggerName, StringComparer.OrdinalIgnoreCase)) continue;
            matches.Add(new OperationalTriggerMatch(agent, point, triggerName));
        }
        return matches;
    }

    private static string? Resolve(RuntimeAgentDefinition agent, TriggersPolicy policy, OperationalTriggerPoint point)
    {
        var role = Normalize(agent.Role);
        var slug = Normalize(agent.Id);
        return role switch
        {
            "context_maintenance" or "context_compressor" => Compressor(policy, point),
            "capability_advisor" or "skill_recommender" => Recommender(policy, point),
            "experience_curator" or "evolution" => Curator(policy, point),
            "git_steward" => Steward(policy, point),
            _ => slug switch
            {
                "context_compressor" => Compressor(policy, point),
                "skill_recommender" => Recommender(policy, point),
                "evolution" => Curator(policy, point),
                "git_steward" => Steward(policy, point),
                _ => null
            }
        };
    }

    private static string? Compressor(TriggersPolicy policy, OperationalTriggerPoint point) =>
        policy.CompressOnTaskClosed && point is OperationalTriggerPoint.TaskCompleted or OperationalTriggerPoint.RunFinalized
            ? "task_closed"
            : null;

    private static string? Recommender(TriggersPolicy policy, OperationalTriggerPoint point) =>
        policy.RecommendOnTaskCreated && point is OperationalTriggerPoint.TaskGraphCreated or OperationalTriggerPoint.CapabilityMissing
            ? point == OperationalTriggerPoint.CapabilityMissing ? "capability_missing" : "task_created"
            : null;

    private static string? Curator(TriggersPolicy policy, OperationalTriggerPoint point) =>
        policy.CurateOnRunClosed && point == OperationalTriggerPoint.RunFinalized ? "run_closed" : null;

    private static string? Steward(TriggersPolicy policy, OperationalTriggerPoint point) =>
        policy.GitStewardOnRunClosed && point == OperationalTriggerPoint.RunFinalized ? "run_closed" : null;

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
