using TinadecCore.DmaEA;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Pure unit tests for the operational trigger evaluator: policy gating,
/// role/point matching, roster trigger filters, and alias resolution.
/// </summary>
public sealed class OperationalTriggerEvaluatorTests
{
    private static readonly TriggersPolicy AllEnabled = new(
        Enabled: true,
        ContextTokenThreshold: 6000,
        CompressOnTaskClosed: true,
        RecommendOnTaskCreated: true,
        CurateOnRunClosed: true,
        GitStewardOnRunClosed: true);

    private readonly OperationalTriggerEvaluator _evaluator = new();

    [Fact]
    public void DisabledPolicy_ReturnsNoMatches()
    {
        var configuration = Config(TriggersPolicy.Disabled, Compressor(), Recommender(), Curator(), Steward());
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.RunFinalized, configuration));
    }

    [Fact]
    public void TaskGraphCreated_MatchesSkillRecommenderWithTaskCreatedTrigger()
    {
        var configuration = Config(AllEnabled, Compressor(), Recommender(), Curator(), Steward());
        var matches = _evaluator.Evaluate(OperationalTriggerPoint.TaskGraphCreated, configuration);
        var match = Assert.Single(matches);
        Assert.Equal("skill_recommender", match.Agent.Id);
        Assert.Equal("task_created", match.TriggerName);
    }

    [Fact]
    public void TaskCompleted_MatchesContextCompressorWithTaskClosedTrigger()
    {
        var configuration = Config(AllEnabled, Compressor(), Recommender(), Curator(), Steward());
        var matches = _evaluator.Evaluate(OperationalTriggerPoint.TaskCompleted, configuration);
        var match = Assert.Single(matches);
        Assert.Equal("context_compressor", match.Agent.Id);
        Assert.Equal("task_closed", match.TriggerName);
    }

    [Fact]
    public void RunFinalized_MatchesCompressorCuratorAndSteward()
    {
        var configuration = Config(AllEnabled, Compressor(), Recommender(), Curator(), Steward());
        var matches = _evaluator.Evaluate(OperationalTriggerPoint.RunFinalized, configuration);
        var byAgent = matches.ToDictionary(item => item.Agent.Id, item => item.TriggerName);
        Assert.Equal(["context_compressor", "evolution", "git_steward"], byAgent.Keys.OrderBy(value => value).ToArray());
        Assert.Equal("task_closed", byAgent["context_compressor"]);
        Assert.Equal("run_closed", byAgent["evolution"]);
        Assert.Equal("run_closed", byAgent["git_steward"]);
    }

    [Fact]
    public void CapabilityMissing_MatchesSkillRecommender()
    {
        var configuration = Config(AllEnabled, Compressor(), Recommender(), Curator(), Steward());
        var matches = _evaluator.Evaluate(OperationalTriggerPoint.CapabilityMissing, configuration);
        var match = Assert.Single(matches);
        Assert.Equal("skill_recommender", match.Agent.Id);
        Assert.Equal("capability_missing", match.TriggerName);
    }

    [Fact]
    public void MeetingAndSupervisor_NeverMatch()
    {
        var configuration = Config(AllEnabled,
            Agent("meeting", "session_coordinator"),
            Agent("supervisor", "quality_controller"),
            Compressor());
        foreach (var point in Enum.GetValues<OperationalTriggerPoint>())
        {
            var matches = _evaluator.Evaluate(point, configuration);
            Assert.All(matches, match => Assert.Equal("context_compressor", match.Agent.Id));
        }
    }

    [Fact]
    public void IndividualSwitches_GateRoles()
    {
        var compressorOff = AllEnabled with { CompressOnTaskClosed = false };
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.TaskCompleted, Config(compressorOff, Compressor())));

        var recommenderOff = AllEnabled with { RecommendOnTaskCreated = false };
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.TaskGraphCreated, Config(recommenderOff, Recommender())));
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.CapabilityMissing, Config(recommenderOff, Recommender())));

        var curatorOff = AllEnabled with { CurateOnRunClosed = false };
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.RunFinalized, Config(curatorOff, Curator())));

        var stewardOff = AllEnabled with { GitStewardOnRunClosed = false };
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.RunFinalized, Config(stewardOff, Steward())));
    }

    [Fact]
    public void DisabledAgent_DoesNotMatch()
    {
        var configuration = Config(AllEnabled, Compressor(enabled: false));
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.TaskCompleted, configuration));
    }

    [Fact]
    public void RosterTriggers_WithMatchingName_AllowsMatch()
    {
        var configuration = Config(AllEnabled, Compressor(triggers: ["task_closed"]));
        var match = Assert.Single(_evaluator.Evaluate(OperationalTriggerPoint.TaskCompleted, configuration));
        Assert.Equal("context_compressor", match.Agent.Id);
    }

    [Fact]
    public void RosterTriggers_WithoutMatchingName_SkipsAgent()
    {
        var configuration = Config(AllEnabled, Compressor(triggers: ["context_threshold"]));
        Assert.Empty(_evaluator.Evaluate(OperationalTriggerPoint.TaskCompleted, configuration));
    }

    [Theory]
    [InlineData("context_maintenance", "context_compressor")]
    [InlineData("capability_advisor", "skill_recommender")]
    [InlineData("experience_curator", "evolution")]
    public void RoleAliases_ResolveToCanonicalRoles(string role, string slug)
    {
        var point = slug switch
        {
            "skill_recommender" => OperationalTriggerPoint.TaskGraphCreated,
            _ => OperationalTriggerPoint.RunFinalized
        };
        var configuration = Config(AllEnabled, Agent(slug, role));
        var match = Assert.Single(_evaluator.Evaluate(point, configuration));
        Assert.Equal(slug, match.Agent.Id);
    }

    private static RuntimeAgentDefinition Agent(string slug, string role, bool enabled = true, IReadOnlyList<string>? triggers = null) => new(
        slug, "operation", role, "session_background", [], false, "patch")
    {
        Enabled = enabled,
        Triggers = triggers ?? []
    };

    private static RuntimeAgentDefinition Compressor(bool enabled = true, IReadOnlyList<string>? triggers = null) =>
        Agent("context_compressor", "context_maintenance", enabled, triggers);

    private static RuntimeAgentDefinition Recommender(bool enabled = true) =>
        Agent("skill_recommender", "capability_advisor", enabled);

    private static RuntimeAgentDefinition Curator(bool enabled = true) =>
        Agent("evolution", "experience_curator", enabled);

    private static RuntimeAgentDefinition Steward(bool enabled = true) =>
        Agent("git_steward", "git_steward", enabled);

    private static FrozenRunConfigurationV1 Config(TriggersPolicy triggers, params RuntimeAgentDefinition[] operationAgents) => new(
        "frozen-run-configuration/v1",
        "baseline-hash",
        1,
        "space",
        "agent",
        "space.full_duplex",
        "ask",
        new SpawnPolicy(2, 8, 4),
        new SchedulingPolicy(2, 2, true),
        new SupervisionPolicy(true, 2),
        new ContextPolicy(8192, 24, true),
        new MemoryPolicy(true, 8, ["workspace"], ["fact"]),
        new ToolRuntimePolicy("tinadec-tools-process", true, true, 120, 4),
        operationAgents,
        [],
        null,
        [])
    {
        Triggers = triggers
    };
}
