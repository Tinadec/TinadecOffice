using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Strategies;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// F# interop tests: call strategy functions from C# and verify
/// public signatures do not leak FSharp types.
/// </summary>
public sealed class FSharpInteropTests
{
    [Fact]
    public void ContextBudgetAllocateBudgetReturnsIntArray()
    {
        var evidence = new List<ContextEvidence>
        {
            new() { Source = "file1", Content = "content1", EstimatedTokens = 100 },
            new() { Source = "file2", Content = "content2", EstimatedTokens = 200 },
        };

        var result = ContextBudget.allocateBudget(300, evidence);

        Assert.IsType<int[]>(result);
        Assert.Equal(2, result.Length);
        Assert.Equal(100, result[0]);
        Assert.Equal(200, result[1]);
    }

    [Fact]
    public void ContextBudgetAllocateBudgetHandlesEmptyInput()
    {
        var result = ContextBudget.allocateBudget(300, new List<ContextEvidence>());
        Assert.Empty(result);
    }

    [Fact]
    public void LoopDetectionDetectRepeatCallsReturnsBool()
    {
        var fingerprints = new List<string> { "call_a", "call_b", "call_b", "call_b" };
        var result = LoopDetection.detectRepeatCalls(fingerprints);

        Assert.IsType<bool>(result);
        Assert.True(result);
    }

    [Fact]
    public void LoopDetectionDetectRepeatCallsReturnsFalseForUnique()
    {
        var fingerprints = new List<string> { "call_a", "call_b", "call_c" };
        var result = LoopDetection.detectRepeatCalls(fingerprints);

        Assert.False(result);
    }

    [Fact]
    public void StateTransitionValidateTransitionReturnsBoolAndString()
    {
        var (isValid, reason) = StateTransition.validateTransition("pending", "running");

        Assert.IsType<bool>(isValid);
        Assert.IsType<string>(reason);
        Assert.True(isValid);
        Assert.Empty(reason);
    }

    [Fact]
    public void StateTransitionRejectsInvalidTransition()
    {
        var (isValid, reason) = StateTransition.validateTransition("completed", "running");

        Assert.False(isValid);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void ContextBudgetUtilizationRatioReturnsDouble()
    {
        var ratio = ContextBudget.utilizationRatio(1000, 500);

        Assert.IsType<double>(ratio);
        Assert.Equal(0.5, ratio);
    }

    [Fact]
    public void FSharpStrategyFunctionsDoNotReturnFSharpTypes()
    {
        // Verify that all F# public functions return C#-compatible types.
        var evidence = new List<ContextEvidence>
        {
            new() { Source = "test", Content = "test", EstimatedTokens = 10 }
        };

        // allocateBudget returns int[] (not FSharpList)
        var budgetResult = ContextBudget.allocateBudget(100, evidence);
        Assert.IsType<int[]>(budgetResult);

        // isOverBudget returns bool (not FSharpOption<bool> or FSharpValue)
        var overBudget = ContextBudget.isOverBudget(100, 200);
        Assert.IsType<bool>(overBudget);

        // utilizationRatio returns double (not float FSharp type)
        var ratio = ContextBudget.utilizationRatio(100, 50);
        Assert.IsType<double>(ratio);

        // detectRepeatCalls returns bool
        var fingerprints = new List<string> { "a", "a", "a" };
        var loopResult = LoopDetection.detectRepeatCalls(fingerprints);
        Assert.IsType<bool>(loopResult);

        // validateTransition returns tuple (bool, string) — C# compatible
        var (valid, msg) = StateTransition.validateTransition("pending", "running");
        Assert.IsType<bool>(valid);
        Assert.IsType<string>(msg);
    }
}
