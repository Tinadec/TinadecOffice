using Microsoft.Extensions.Options;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Governance;

namespace TinadecCore.Governance.Tests;

/// <summary>
/// Auto-approve policy contract: when enabled it acts as a third decision
/// source ("auto_policy") inside the same binding-checked grant path, never
/// as a bypass. Every invariant here survives an enabled policy — disabled
/// behavior is pinned by the pre-existing unauthorized_approver tests.
/// </summary>
public sealed class AutoApprovePolicyTests
{
    private static readonly CapabilityRule AllowToolFile = new("allow", "tool.file", "tool.invoke", "tool://*");

    [Fact]
    public async Task AutoPolicy_DisabledByDefault_KeepsUnauthorizedApprover()
    {
        await using var harness = await GovernanceHarness.CreateAsync();
        harness.Context.Boundaries = [new("hard_policy", [AllowToolFile])];
        var pending = await harness.Service.RequestPermissionAsync(
            ToolRequest(Guid.NewGuid(), "auto-off", "write_file"));
        Assert.Equal(PermissionRequestStatuses.AwaitingUser, pending.Request.Status);

        var denied = await harness.Service.DecidePermissionAsync(
            new PermissionDecisionCommand(pending.Request.Id, true, Guid.NewGuid(), null, "agent attempts to decide"));

        Assert.Equal(PermissionRequestStatuses.Denied, denied.Request.Status);
        Assert.Equal("unauthorized_approver", denied.Decision.ReasonCode);
    }

    [Fact]
    public async Task AutoPolicy_ApprovesWithinEnvelope_AsNonHumanDecision()
    {
        await using var harness = await GovernanceHarness.CreateAsync(Enabled());
        harness.Context.Boundaries = [new("hard_policy", [AllowToolFile])];
        var pending = await harness.Service.RequestPermissionAsync(
            ToolRequest(Guid.NewGuid(), "auto-ok", "write_file"));
        Assert.Equal(PermissionRequestStatuses.AwaitingUser, pending.Request.Status);

        var approved = await harness.Service.DecidePermissionAsync(
            new PermissionDecisionCommand(pending.Request.Id, true, Guid.NewGuid(), null, "unattended run"));

        Assert.Equal(PermissionRequestStatuses.Granted, approved.Request.Status);
        Assert.Equal("auto_policy_approved", approved.Decision.ReasonCode);
        Assert.Equal("auto_policy", approved.Decision.DecisionSource);
        Assert.Contains(AutoApproveOptions.PolicyVersion, approved.Decision.Reason);
        Assert.NotNull(approved.Grant);
        Assert.NotNull(approved.Lease);
        Assert.NotEmpty(approved.Lease.Nonce);
    }

    [Theory]
    [InlineData("git_push")]
    [InlineData("command_run")]
    [InlineData("shell")]
    [InlineData("git_worktree_remove")]
    [InlineData("mcp_invoke")]
    [InlineData("workspace_delete")]
    [InlineData("delete_file")]
    public async Task AutoPolicy_RespectsHumanOnlyToolList(string toolId)
    {
        await using var harness = await GovernanceHarness.CreateAsync(Enabled());
        harness.Context.Boundaries = [new("hard_policy", [AllowToolFile])];
        var pending = await harness.Service.RequestPermissionAsync(
            ToolRequest(Guid.NewGuid(), $"human-only-{toolId}", toolId));

        var denied = await harness.Service.DecidePermissionAsync(
            new PermissionDecisionCommand(pending.Request.Id, true, Guid.NewGuid(), null, "try the human-only tool"));

        Assert.Equal(PermissionRequestStatuses.Denied, denied.Request.Status);
        Assert.Equal("unauthorized_approver", denied.Decision.ReasonCode);
        Assert.Null(denied.Lease);
    }

    [Fact]
    public async Task AutoApprove_RespectsPerRunBudgetThenEscalates()
    {
        await using var harness = await GovernanceHarness.CreateAsync(Enabled(maxPerRun: 1));
        harness.Context.Boundaries = [new("hard_policy", [AllowToolFile])];
        var runId = Guid.NewGuid();
        var first = await harness.Service.RequestPermissionAsync(
            ToolRequest(Guid.NewGuid(), "budget-1", "write_file", runId));
        var approved = await harness.Service.DecidePermissionAsync(
            new PermissionDecisionCommand(first.Request.Id, true, Guid.NewGuid(), null, "first use"));
        Assert.Equal(PermissionRequestStatuses.Granted, approved.Request.Status);

        var second = await harness.Service.RequestPermissionAsync(
            ToolRequest(Guid.NewGuid(), "budget-2", "write_file", runId));
        var escalated = await harness.Service.DecidePermissionAsync(
            new PermissionDecisionCommand(second.Request.Id, true, Guid.NewGuid(), null, "second use"));

        // Escalate, not deny: a denied request is terminal and a human could
        // never decide it afterwards.
        Assert.Equal(PermissionRequestStatuses.AwaitingUser, escalated.Request.Status);
        Assert.Equal("auto_policy_budget_exhausted", escalated.Decision.ReasonCode);
        Assert.Null(escalated.Lease);
    }

    [Fact]
    public async Task AutoApproval_CannotOverrideRequireUserReview()
    {
        await using var harness = await GovernanceHarness.CreateAsync(Enabled());
        harness.Context.Boundaries = [new("hard_policy", [AllowToolFile])];
        var approverAgent = Guid.NewGuid();
        var approverVersion = Guid.NewGuid();
        harness.Context.AgentVersions[approverAgent] = approverVersion;
        var delegation = await harness.Service.CreateApprovalDelegationAsync(new CreateApprovalDelegationCommand(
            approverVersion, approverAgent, [AllowToolFile], "medium", 10, 2,
            harness.Time.GetUtcNow().AddHours(1), RequireUserReview: true));
        var pending = await harness.Service.RequestPermissionAsync(
            ToolRequest(Guid.NewGuid(), "review-required", "write_file"));

        var escalated = await harness.Service.DecidePermissionAsync(new PermissionDecisionCommand(
            pending.Request.Id, true, approverAgent, delegation.Id, "delegate decides"));

        Assert.Equal(PermissionRequestStatuses.AwaitingUser, escalated.Request.Status);
        Assert.Equal("delegation_requires_user", escalated.Decision.ReasonCode);
        Assert.Null(escalated.Lease);
    }

    [Fact]
    public async Task AutoApproval_CannotWidenBeyondRequestedClaim()
    {
        await using var harness = await GovernanceHarness.CreateAsync(Enabled());
        harness.Context.Boundaries = [new("hard_policy", [AllowToolFile])];
        var subject = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var pending = await harness.Service.RequestPermissionAsync(
            ToolRequest(subject, "widen", "write_file", agent: agent));
        var approved = await harness.Service.DecidePermissionAsync(
            new PermissionDecisionCommand(pending.Request.Id, true, Guid.NewGuid(), null, "auto"));
        Assert.Equal(PermissionRequestStatuses.Granted, approved.Request.Status);

        var consume = await harness.Service.ConsumeToolLeaseAsync(new ToolLeaseConsumptionCommand(
            approved.Lease!.Id, approved.Lease.Nonce, subject, agent,
            new CapabilityClaim("tool.git", "tool.invoke", "tool://git_push"),
            null, null, "widen-consume"));

        Assert.Equal("blocked", consume.Status);
        Assert.Equal("lease_scope_mismatch", consume.Decision.ReasonCode);
    }

    [Fact]
    public async Task ToolApprovalAutoPolicy_DisabledByDefault_Abstains()
    {
        var policy = new ToolApprovalAutoPolicy(Options.Create(new AutoApproveOptions()));

        var verdict = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("write_file", "medium", Guid.NewGuid()));

        Assert.Equal(ToolApprovalAutoPolicyOutcome.NotEngaged, verdict.Outcome);
        Assert.False(verdict.Approved);
    }

    [Theory]
    [InlineData("low", "medium", true)]
    [InlineData("medium", "medium", true)]
    [InlineData("high", "medium", false)]
    [InlineData("critical", "critical", true)]
    [InlineData("elevated", "medium", false)]
    [InlineData("elevated", "high", false)]
    [InlineData("elevated", "critical", false)]
    [InlineData("unknown_risk", "critical", false)]
    [InlineData("high", null, false)]
    public async Task ToolApprovalAutoPolicy_EngagesOnlyWithinRiskCeiling(
        string risk,
        string? ceiling,
        bool expectedApproved)
    {
        var options = new AutoApproveOptions { AutoApproveEnabled = true, AutoApproveRiskMax = ceiling ?? "medium" };
        var policy = new ToolApprovalAutoPolicy(Options.Create(options));

        var verdict = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("write_file", risk, Guid.NewGuid()));

        Assert.Equal(expectedApproved ? ToolApprovalAutoPolicyOutcome.Approved : ToolApprovalAutoPolicyOutcome.NotEngaged, verdict.Outcome);
        if (expectedApproved) Assert.Equal(AutoApproveOptions.PolicyVersion, verdict.PolicyVersion);
    }

    [Fact]
    public async Task ToolApprovalAutoPolicy_NeverEngagesForHumanOnlyTools()
    {
        var options = new AutoApproveOptions { AutoApproveEnabled = true, AutoApproveRiskMax = "critical" };
        var policy = new ToolApprovalAutoPolicy(Options.Create(options));

        var listed = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("git_push", "low", Guid.NewGuid()));
        var shell = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("shell", "low", Guid.NewGuid()));
        var suffix = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("file_delete", "low", Guid.NewGuid()));

        Assert.Equal(ToolApprovalAutoPolicyOutcome.NotEngaged, listed.Outcome);
        Assert.Equal(ToolApprovalAutoPolicyOutcome.NotEngaged, shell.Outcome);
        Assert.Equal(ToolApprovalAutoPolicyOutcome.NotEngaged, suffix.Outcome);
    }

    /// <summary>
    /// The shared vocabulary orders medium &lt; elevated &lt; high, but elevated is
    /// never auto-approved at any ceiling (M8 invariant): it is refused even
    /// when the ceiling would rank above it.
    /// </summary>
    [Fact]
    public async Task ToolApprovalAutoPolicy_ElevatedNeverEngages_EvenBelowTheCeiling()
    {
        var options = new AutoApproveOptions { AutoApproveEnabled = true, AutoApproveRiskMax = "high" };
        var policy = new ToolApprovalAutoPolicy(Options.Create(options));

        var elevated = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("write_file", "elevated", Guid.NewGuid()));
        var high = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("write_file", "high", Guid.NewGuid()));

        Assert.Equal(ToolApprovalAutoPolicyOutcome.NotEngaged, elevated.Outcome);
        Assert.Equal(ToolApprovalAutoPolicyOutcome.Approved, high.Outcome);
    }

    [Fact]
    public async Task ToolApprovalAutoPolicy_HonorsTheCallerCountedBudget_AndNeverDenies()
    {
        var options = new AutoApproveOptions { AutoApproveEnabled = true, AutoApproveRiskMax = "high", AutoApproveMaxPerRun = 2 };
        var policy = new ToolApprovalAutoPolicy(Options.Create(options));
        var runId = Guid.NewGuid();

        var first = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("write_file", "low", runId, AutoPolicyUsesThisRun: 0));
        var second = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("write_file", "low", runId, AutoPolicyUsesThisRun: 1));
        var third = await policy.EvaluateAsync(new ToolApprovalAutoPolicyContext("write_file", "low", runId, AutoPolicyUsesThisRun: 2));

        Assert.True(first.Approved);
        Assert.Equal(1, first.BudgetRemaining);
        Assert.True(second.Approved);
        // Exhausted budget is not a denial: the caller keeps the approval pending.
        Assert.Equal(ToolApprovalAutoPolicyOutcome.BudgetExhausted, third.Outcome);
        Assert.False(third.Approved);
    }

    private static AutoApproveOptions Enabled(int maxPerRun = 5) => new()
    {
        AutoApproveEnabled = true,
        AutoApproveMaxPerRun = maxPerRun,
        AutoApproveRiskMax = "medium"
    };

    private static PermissionRequestCommand ToolRequest(
        Guid subject,
        string idempotencyKey,
        string toolId,
        Guid? runId = null,
        Guid? agent = null) => new(
            subject, agent ?? Guid.NewGuid(), null,
            new CapabilityClaim("tool.file", "tool.invoke", $"tool://{toolId}"),
            runId, null, TimeSpan.FromMinutes(30), 1, "low", 0,
            "auto-policy probe", idempotencyKey);
}
