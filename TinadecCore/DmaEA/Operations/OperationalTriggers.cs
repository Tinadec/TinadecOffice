using TinadecCore.Abstractions.Ports;

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
    CapabilityMissing,

    /// <summary>A tool call was refused because another run holds the resource it would touch.</summary>
    LeaseConflict,

    /// <summary>A tool call parked on an approval gate.</summary>
    ApprovalRequested
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
/// The four behaviours the engine implements itself, and the single place a frozen agent is recognized
/// as one of them. The evaluator and the engine's dispatch both read this table: they used to keep a
/// copy each, and the copies disagreed (an agent with slug <c>git_steward</c> and another role matched in
/// the evaluator, then dispatched to nothing).
/// </summary>
public static class OperationalRoles
{
    public const string ContextCompressor = "context_compressor";
    public const string SkillRecommender = "skill_recommender";
    public const string Evolution = "evolution";
    public const string GitSteward = "git_steward";

    /// <summary>The built-in behaviour an agent maps to, or null when it is a declared role of its own.</summary>
    public static string? BuiltInKind(RuntimeAgentDefinition agent)
    {
        var role = Normalize(agent.Role);
        var slug = Normalize(agent.Id);
        return role switch
        {
            "context_maintenance" or ContextCompressor => ContextCompressor,
            "capability_advisor" or SkillRecommender => SkillRecommender,
            "experience_curator" or Evolution => Evolution,
            GitSteward => GitSteward,
            _ => slug switch
            {
                ContextCompressor => ContextCompressor,
                SkillRecommender => SkillRecommender,
                Evolution => Evolution,
                GitSteward => GitSteward,
                _ => null
            }
        };
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

/// <summary>
/// Matches frozen operation-layer agents against a trigger point, in one of two ways:
///
/// <list type="bullet">
/// <item><b>Declared</b> — the agent's relationship file lists <c>subscriptions</c> (frozen into
/// <see cref="RuntimeAgentDefinition.Triggers"/>). The declaration is the wiring: the agent wakes on
/// exactly those facts, whatever it is called. This is how a mode adds a governance role (a reviewer,
/// a conflict arbiter) without an engine change.</item>
/// <item><b>Derived</b> — an agent that declares nothing is recognized by role name as one of the four
/// built-in behaviours, at the points those behaviours have always run. This keeps every mode
/// published before subscriptions existed behaving exactly as before.</item>
/// </list>
///
/// Either way a built-in behaviour still honours its operator switch in <see cref="TriggersPolicy"/>:
/// the switches are named after those behaviours, so an operator who turned one off meant it.
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

        var topic = TopicOf(point);
        var matches = new List<OperationalTriggerMatch>();
        foreach (var agent in configuration.OperationAgents)
        {
            if (!agent.Enabled) continue;
            var kind = OperationalRoles.BuiltInKind(agent);
            if (agent.Triggers.Count > 0)
            {
                if (!agent.Triggers.Contains(topic, StringComparer.OrdinalIgnoreCase)) continue;
                if (kind is not null && !KindEnabled(kind, policy)) continue;
                matches.Add(new OperationalTriggerMatch(agent, point, topic));
                continue;
            }
            if (Derive(kind, policy, point) is { } triggerName)
                matches.Add(new OperationalTriggerMatch(agent, point, triggerName));
        }
        return matches;
    }

    /// <summary>The subscription topic a trigger point raises. One topic per point, no exceptions.</summary>
    public static string TopicOf(OperationalTriggerPoint point) => point switch
    {
        OperationalTriggerPoint.TaskGraphCreated => GovernanceTopics.TaskCreated,
        OperationalTriggerPoint.TaskCompleted => GovernanceTopics.TaskClosed,
        OperationalTriggerPoint.RunFinalized => GovernanceTopics.RunClosed,
        OperationalTriggerPoint.CapabilityMissing => GovernanceTopics.CapabilityMissing,
        OperationalTriggerPoint.LeaseConflict => GovernanceTopics.LeaseConflict,
        OperationalTriggerPoint.ApprovalRequested => GovernanceTopics.ApprovalRequested,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown trigger point.")
    };

    private static bool KindEnabled(string kind, TriggersPolicy policy) => kind switch
    {
        OperationalRoles.ContextCompressor => policy.CompressOnTaskClosed,
        OperationalRoles.SkillRecommender => policy.RecommendOnTaskCreated,
        OperationalRoles.Evolution => policy.CurateOnRunClosed,
        OperationalRoles.GitSteward => policy.GitStewardOnRunClosed,
        _ => true
    };

    /// <summary>
    /// The historical wiring of the built-in behaviours, for agents that declare no subscriptions. The
    /// compressor answers at run close too and reports it as <c>task_closed</c>, which is the name the
    /// derived path has always recorded; a declared subscription is exact instead.
    /// </summary>
    private static string? Derive(string? kind, TriggersPolicy policy, OperationalTriggerPoint point) => kind switch
    {
        OperationalRoles.ContextCompressor when policy.CompressOnTaskClosed
            && point is OperationalTriggerPoint.TaskCompleted or OperationalTriggerPoint.RunFinalized => GovernanceTopics.TaskClosed,
        OperationalRoles.SkillRecommender when policy.RecommendOnTaskCreated && point == OperationalTriggerPoint.TaskGraphCreated => GovernanceTopics.TaskCreated,
        OperationalRoles.SkillRecommender when policy.RecommendOnTaskCreated && point == OperationalTriggerPoint.CapabilityMissing => GovernanceTopics.CapabilityMissing,
        OperationalRoles.Evolution when policy.CurateOnRunClosed && point == OperationalTriggerPoint.RunFinalized => GovernanceTopics.RunClosed,
        OperationalRoles.GitSteward when policy.GitStewardOnRunClosed && point == OperationalTriggerPoint.RunFinalized => GovernanceTopics.RunClosed,
        _ => null
    };
}
