using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Strategies;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The F# strategy kernels called from C#. Their C#-facing types are the compiler's job.
/// </summary>
public sealed class FSharpInteropTests
{
    [Fact]
    public void ContextBudgetAllocateBudgetSplitsByEvidence()
    {
        var evidence = new List<ContextEvidence>
        {
            new() { Source = "file1", Content = "content1", EstimatedTokens = 100 },
            new() { Source = "file2", Content = "content2", EstimatedTokens = 200 },
        };

        var result = ContextBudget.allocateBudget(300, evidence);

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
    public void StateTransitionAcceptsALegalTransition()
    {
        var (isValid, reason) = StateTransition.validateTransition("planning", "understanding");

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
    public void ContextBudgetUtilizationRatioIsUsedOverBudget()
    {
        var ratio = ContextBudget.utilizationRatio(1000, 500);

        Assert.Equal(0.5, ratio);
    }
}
