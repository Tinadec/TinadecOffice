using TinadecCore.Abstractions.Ports;
using TinadecCore.LoopGuard;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The loop guard's role in the worker tool loop: raised-zone rounds are only
/// allowed while the guard keeps saying "continue".
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
        Assert.Contains("Repeated identical tool call", decision.Reason);
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
}
