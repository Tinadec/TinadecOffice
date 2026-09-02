using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA.Orchestration;
using Xunit;

namespace TinadecCore.AgentFramework.Tests;

public class OrchestrationDirectiveValidatorTests
{
    private static readonly string[] FrozenTools = ["shell", "write_file", "read_file"];
    private static readonly string[] MutatingTools = ["shell", "write_file"];

    private static OrchestrationDirectiveRules Rules(
        bool lanesEnabled = true,
        int currentLanes = 1,
        int maxLanes = 4,
        int maxTasks = 6,
        string[]? knownLanes = null,
        string? permissionMode = null) => new(
        lanesEnabled,
        currentLanes,
        maxLanes,
        maxTasks,
        FrozenTools,
        MutatingTools,
        knownLanes ?? ["main"],
        permissionMode);

    private static string Payload(string json) => json;

    [Fact]
    public void AcceptsAWellFormedLaneOpen()
    {
        var rejection = new OrchestrationDirectiveValidator().Validate(
            new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", Payload(
                """{"lane_key":"l2","goal":"test and commit","tasks":[{"task_key":"t1","title":"Test","dependencies":[]}],"waits":[{"lane":"main"}]}""")),
            Rules());

        Assert.Null(rejection);
    }

    [Fact]
    public void RejectsUnknownVerbs()
    {
        var rejection = new OrchestrationDirectiveValidator().Validate(
            new OrchestrationDirectiveCandidate("LANE_REBOOT", "l2", Payload("""{"lane_key":"l2","tasks":[{"task_key":"t1"}]}""")),
            Rules());

        Assert.NotNull(rejection);
        Assert.Equal("unknown_verb", rejection.Code);
    }

    [Fact]
    public void RejectsWhenLanesAreDisabled()
    {
        var rejection = new OrchestrationDirectiveValidator().Validate(
            new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", Payload("""{"lane_key":"l2","tasks":[{"task_key":"t1"}]}""")),
            Rules(lanesEnabled: false));

        Assert.Equal("lanes_disabled", rejection?.Code);
    }

    [Fact]
    public void RejectsDuplicateOrOverBudgetLanes()
    {
        var payload = Payload("""{"lane_key":"l2","tasks":[{"task_key":"t1"}]}""");

        Assert.Equal("duplicate_lane", new OrchestrationDirectiveValidator()
            .Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "main", payload), Rules())?.Code);
        Assert.Equal("lane_budget_exhausted", new OrchestrationDirectiveValidator()
            .Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", payload), Rules(currentLanes: 4, maxLanes: 4))?.Code);
    }

    [Fact]
    public void RejectsTaskBudgetAndDuplicateTaskKeys()
    {
        var validator = new OrchestrationDirectiveValidator();
        var overBudget = Payload("""{"lane_key":"l2","goal":"g","tasks":[{"task_key":"t1"},{"task_key":"t2"},{"task_key":"t3"},{"task_key":"t4"},{"task_key":"t5"},{"task_key":"t6"},{"task_key":"t7"}]}""");
        Assert.Equal("task_budget_exceeded", validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", overBudget), Rules(maxTasks: 6))?.Code);

        var duplicate = Payload("""{"lane_key":"l2","goal":"g","tasks":[{"task_key":"t1"},{"task_key":"t1"}]}""");
        Assert.Equal("duplicate_task_key", validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", duplicate), Rules())?.Code);
    }

    [Fact]
    public void RejectsCrossLaneDependenciesAndCycles()
    {
        var validator = new OrchestrationDirectiveValidator();
        var crossLane = Payload("""{"lane_key":"l2","goal":"g","tasks":[{"task_key":"t1","dependencies":["ghost"]}]}""");
        Assert.Equal("cross_lane_dependency", validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", crossLane), Rules())?.Code);

        var cycle = Payload("""{"lane_key":"l2","goal":"g","tasks":[{"task_key":"a","dependencies":["b"]},{"task_key":"b","dependencies":["a"]}]}""");
        Assert.Equal("cyclic_dependencies", validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", cycle), Rules())?.Code);
    }

    [Fact]
    public void DirectiveValidator_RejectsToolScopeWidening_AndMissingPreAuthorization()
    {
        var validator = new OrchestrationDirectiveValidator();
        var widening = Payload("""{"lane_key":"l2","goal":"g","tool_scope":["shell","git_push"],"tasks":[{"task_key":"t1"}]}""");
        var rejection = validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", widening), Rules());
        Assert.NotNull(rejection);
        Assert.Equal("tool_scope_widening", rejection.Code);
        Assert.Contains("git_push", rejection.Reason, StringComparison.Ordinal);

        var taskLevel = Payload("""{"lane_key":"l2","goal":"g","tasks":[{"task_key":"t1","tool_scope":["net_new_tool"]}]}""");
        var taskRejection = validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", taskLevel), Rules());
        Assert.NotNull(taskRejection);
        Assert.Equal("tool_scope_widening", taskRejection.Code);
        Assert.Contains("net_new_tool", taskRejection.Reason, StringComparison.Ordinal);

        var wildcard = Payload("""{"lane_key":"l2","goal":"g","tool_scope":["*"],"tasks":[{"task_key":"t1"}]}""");
        Assert.Equal("tool_scope_widening", validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", wildcard), Rules())?.Code);

        // A mutating declaration without a pre-authorization reference and
        // without an unattended run still fails closed.
        var mutating = Payload("""{"lane_key":"l2","goal":"g","tool_scope":["shell"],"tasks":[{"task_key":"t1"}]}""");
        Assert.Equal("lane_requires_preauthorization", validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", mutating), Rules())?.Code);

        // A pre-authorization reference admits the declaration. The reference is
        // only consumed by the approval minting layer, so a grant that does not
        // match parks the lane instead of widening authority.
        var withReference = Payload("""{"lane_key":"l2","goal":"g","tool_scope":["shell"],"pre_authorization":"pre-1","tasks":[{"task_key":"t1"}]}""");
        Assert.Null(validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", withReference), Rules()));
    }

    [Fact]
    public void DirectiveValidator_AdmitsMutatingLanes_OnUnattendedRuns_AndKeepsRejectingOtherwise()
    {
        var validator = new OrchestrationDirectiveValidator();
        var mutating = Payload("""{"lane_key":"l2","goal":"g","tool_scope":["shell"],"tasks":[{"task_key":"t1"}]}""");

        Assert.Null(validator.Validate(
            new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", mutating),
            Rules(permissionMode: "full-access")));

        // A wildcard scope must still resolve against the frozen manifest: an
        // unattended permission mode does not license escaping the manifest check.
        var wildcardOnUnattended = Payload("""{"lane_key":"l2","goal":"g","tool_scope":["*"],"tasks":[{"task_key":"t1"}]}""");
        Assert.Equal("tool_scope_widening", validator.Validate(
            new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", wildcardOnUnattended),
            Rules(permissionMode: "full-access"))?.Code);

        // Any other permission mode keeps requiring an explicit grant reference.
        foreach (var mode in new[] { "default", "accept-edits", "plan", null })
        {
            Assert.Equal("lane_requires_preauthorization", validator.Validate(
                new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", mutating),
                Rules(permissionMode: mode))?.Code);
        }
    }

    [Fact]
    public void AcceptsGoalOnlyLaneOpen_WithoutTasks()
    {
        var validator = new OrchestrationDirectiveValidator();

        // A goal-only directive defers the task graph to the lane's own
        // planning agent, so no task-level validation applies here.
        var goalOnly = Payload("""{"lane_key":"l2","goal":"完成后测试并提交"}""");
        Assert.Null(validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", goalOnly), Rules()));

        // A title still satisfies the goal-or-title rule, and declared tool
        // scopes on a goal-only lane are still checked against the manifest.
        var titleOnly = Payload("""{"lane_key":"l2","title":"收尾"}""");
        Assert.Null(validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", titleOnly), Rules()));

        var goalWithScope = Payload("""{"lane_key":"l2","goal":"g","tool_scope":["net_new_tool"]}""");
        var rejection = validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", goalWithScope), Rules());
        Assert.Equal("tool_scope_widening", rejection?.Code);

        var emptyTasks = Payload("""{"lane_key":"l2","goal":"g","tasks":[]}""");
        Assert.Equal("invalid_lane_payload", validator.Validate(new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", emptyTasks), Rules())?.Code);
    }

    [Fact]
    public void RejectsWaitsTargetingUnknownLanes()
    {
        var rejection = new OrchestrationDirectiveValidator().Validate(
            new OrchestrationDirectiveCandidate("LANE_OPEN", "l2", Payload(
                """{"lane_key":"l2","goal":"g","tasks":[{"task_key":"t1"}],"waits":[{"lane":"l9"}]}""")),
            Rules());

        Assert.Equal("unknown_wait_lane", rejection?.Code);
    }
}
