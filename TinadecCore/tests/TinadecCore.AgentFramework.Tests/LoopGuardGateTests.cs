using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.LoopGuard;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The loop guard's role in the worker tool loop: it is now the primary
/// convergence mechanism, so every veto must name the budget that fired and
/// distinguish an exhausted budget from the absolute fuse. It also owns the
/// "0 = no gate" semantics the TOML exposes, which the previous hard-coded
/// defaults silently contradicted.
/// </summary>
public sealed class LoopGuardGateTests
{
    private readonly LoopGuardEvaluator _guard = new();

    [Fact]
    public async Task RepeatedIdenticalCalls_AreVetoed()
    {
        var fingerprint = "shell:{\"command\":\"dotnet test\"}";
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 5,
            MaxIterations = 12,
            RecentToolCallFingerprints = [fingerprint, fingerprint, fingerprint]
        });

        Assert.False(decision.ShouldContinue);
        Assert.Contains("Repeated identical tool call", decision.Reason ?? string.Empty);
        Assert.Equal(RunErrorTaxonomy.ToolLoopDetected, decision.Category);
        Assert.False(decision.HardCeiling);
        // The report names the call the worker was stuck on; without it the reader
        // (and the model) cannot tell which side effect was repeating.
        Assert.Contains("dotnet test", decision.Reason ?? string.Empty);
    }

    [Fact]
    public async Task TwoRepeatedCalls_StillContinue()
    {
        var fingerprint = "shell:{\"command\":\"dotnet test\"}";
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 5,
            MaxIterations = 12,
            RecentToolCallFingerprints = [fingerprint, fingerprint]
        });

        Assert.True(decision.ShouldContinue);
        Assert.Null(decision.Category);
    }

    [Fact]
    public async Task FinalPermittedRound_IsAllowed()
    {
        // Engine boundary: a round may dispatch while ToolRounds == roundLimit.
        // The guard receives completed rounds (ToolRounds - 1), so the last
        // permitted round must not trip the F# >= iteration check.
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 11,
            MaxIterations = 12,
            RecentToolCallFingerprints = ["read_file:a", "write_file:b", "read_file:c"]
        });

        Assert.True(decision.ShouldContinue);
    }

    [Fact]
    public async Task UnlimitedRounds_NeverTripTheIterationCheck()
    {
        // max_tool_rounds <= 0 means "no round gate"; the check must not veto on
        // the first round of a long task.
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 0,
            MaxIterations = 0,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.True(decision.ShouldContinue);

        var deep = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 4000,
            MaxIterations = 0,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.True(deep.ShouldContinue);
    }

    [Fact]
    public async Task RoundLimitVeto_ReportsTheRoundBudget()
    {
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 12,
            MaxIterations = 12,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.False(decision.ShouldContinue);
        Assert.Equal(RunErrorTaxonomy.ToolRoundLimit, decision.Category);
        Assert.False(decision.HardCeiling);
    }

    [Fact]
    public async Task TokenBudgetVeto_ReportsTheTaskBudget()
    {
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 1,
            MaxIterations = 0,
            TokensUsed = 4096,
            TokenBudget = 4096,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.False(decision.ShouldContinue);
        Assert.Equal(RunErrorTaxonomy.TokenBudgetExhausted, decision.Category);
        Assert.False(decision.HardCeiling);
        Assert.Contains("4096/4096", decision.Reason);
    }

    [Fact]
    public async Task NoTokenBudgetSupplied_DisablesTheCheck()
    {
        // A caller that supplies no budget must not be capped by a hard-coded
        // default: the engine supplies the frozen policy value, everything else
        // means "not enforced".
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 1,
            MaxIterations = 0,
            TokensUsed = 10_000_000,
            TokenBudget = 0,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.True(decision.ShouldContinue);
    }

    [Fact]
    public async Task ConsecutiveErrors_ReportTheErrorStreak()
    {
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 3,
            MaxIterations = 0,
            ConsecutiveErrors = 3,
            MaxConsecutiveErrors = 3,
            RecentToolCallFingerprints = ["read_file:a", "read_file:b", "read_file:c"]
        });

        Assert.False(decision.ShouldContinue);
        Assert.Equal(RunErrorTaxonomy.TooManyConsecutiveErrors, decision.Category);
        Assert.Contains("3/3", decision.Reason);
    }

    [Fact]
    public async Task ToolCallFuse_IsReportedAsAHardCeiling()
    {
        // The call count is an absolute fuse rather than a budget: tripping it
        // usually means a bug, and the marker is what keeps the two causes from
        // being conflated in the event log and in supervision.
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 1,
            MaxIterations = 0,
            ToolCallCount = 500,
            MaxToolCalls = 500,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.False(decision.ShouldContinue);
        Assert.Equal(RunErrorTaxonomy.ToolCallCeiling, decision.Category);
        Assert.True(decision.HardCeiling);
    }

    [Fact]
    public async Task NoCallFuseSupplied_DisablesTheCheck()
    {
        // The previous hard-coded default of 50 made an unsupplied budget fire;
        // "not supplied" must mean "not enforced".
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 1,
            MaxIterations = 0,
            ToolCallCount = 5000,
            MaxToolCalls = 0,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.True(decision.ShouldContinue);
    }

    [Fact]
    public async Task ZeroMaxConsecutiveErrors_DisablesTheCheck()
    {
        var decision = await _guard.EvaluateAsync("session", "run", new LoopGuardContext
        {
            Iteration = 1,
            MaxIterations = 0,
            ConsecutiveErrors = 12,
            MaxConsecutiveErrors = 0,
            RecentToolCallFingerprints = ["read_file:a"]
        });

        Assert.True(decision.ShouldContinue);
    }
}
