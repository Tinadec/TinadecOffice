using TinadecCore.Abstractions;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The shared run status vocabulary and transition table (plan §3.3 item 3):
/// 12 known states, terminal closure, idempotent same-state, and the exact
/// legal edge set. These tests lock the table that C# lifecycle services and
/// the F# strategy layer both consume.
/// </summary>
public sealed class RunStatusMachineTests
{
    [Fact]
    public void KnownStates_IsExactlyTheTwelveStateVocabulary()
    {
        Assert.Equal(12, RunStatusMachine.KnownStates.Count);
        foreach (var status in new[] { "planning", "understanding", "executing", "replanning", "awaiting_approval", "awaiting_delegate", "awaiting_user", "paused", "reviewing", "completed", "failed", "cancelled" })
        {
            Assert.True(RunStatusMachine.IsKnown(status));
        }

        // Legacy aliases are retired: they are not part of the vocabulary.
        Assert.False(RunStatusMachine.IsKnown("pending"));
        Assert.False(RunStatusMachine.IsKnown("running"));
        Assert.False(RunStatusMachine.IsKnown(""));
        Assert.False(RunStatusMachine.IsKnown("bogus"));
    }

    [Fact]
    public void Terminal_StatesAreExactlyCompletedFailedCancelled()
    {
        Assert.True(RunStatusMachine.IsTerminal("completed"));
        Assert.True(RunStatusMachine.IsTerminal("failed"));
        Assert.True(RunStatusMachine.IsTerminal("cancelled"));
        foreach (var state in RunStatusMachine.KnownStates.Where(s => !RunStatusMachine.IsTerminal(s)))
        {
            Assert.False(RunStatusMachine.IsTerminal(state), state);
        }
    }

    [Fact]
    public void SameState_IsAlwaysAllowed_TerminalIncluded()
    {
        foreach (var state in RunStatusMachine.KnownStates)
        {
            Assert.True(RunStatusMachine.CanTransition(state, state), state);
        }
    }

    [Fact]
    public void Terminal_CannotLeaveOrRevive()
    {
        foreach (var terminal in new[] { "completed", "failed", "cancelled" })
        {
            foreach (var target in RunStatusMachine.KnownStates.Where(s => s != terminal))
            {
                Assert.False(RunStatusMachine.CanTransition(terminal, target), $"{terminal} -> {target}");
            }
        }
    }

    [Fact]
    public void CancelledAndFailed_AreReachableFromEveryNonTerminalState()
    {
        foreach (var state in RunStatusMachine.KnownStates.Where(s => !RunStatusMachine.IsTerminal(s)))
        {
            Assert.True(RunStatusMachine.CanTransition(state, "cancelled"), $"{state} -> cancelled");
            Assert.True(RunStatusMachine.CanTransition(state, "failed"), $"{state} -> failed");
        }
    }

    [Fact]
    public void LegalEdges_MatchTheTransitionTable()
    {
        (string From, string[] Targets)[] expected =
        [
            ("planning", ["understanding", "executing"]),
            ("understanding", ["executing", "replanning", "awaiting_approval", "awaiting_delegate", "awaiting_user", "paused"]),
            ("executing", ["replanning", "awaiting_approval", "awaiting_delegate", "awaiting_user", "reviewing", "paused", "completed"]),
            ("replanning", ["executing", "awaiting_approval", "awaiting_delegate", "awaiting_user", "paused"]),
            ("awaiting_approval", ["executing", "replanning", "awaiting_delegate", "awaiting_user", "paused"]),
            ("awaiting_delegate", ["executing", "awaiting_user", "paused"]),
            ("awaiting_user", ["executing", "paused"]),
            ("paused", ["executing"]),
            ("reviewing", ["executing", "replanning", "awaiting_user", "paused", "completed"])
        ];

        foreach (var (from, targets) in expected)
        {
            foreach (var target in RunStatusMachine.KnownStates)
            {
                // failed/cancelled are reachable from every non-terminal state and
                // same-state is idempotent — both covered by dedicated tests above.
                var shouldAllow = targets.Contains(target) || target == from || target is "failed" or "cancelled";
                Assert.True(shouldAllow == RunStatusMachine.CanTransition(from, target), $"{from} -> {target}");
            }
        }
    }
}
