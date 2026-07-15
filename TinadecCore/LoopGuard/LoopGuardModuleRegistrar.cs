using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Strategies;

namespace TinadecCore.LoopGuard;

/// <summary>
/// LoopGuard module registrar. Registers loop guard with MAF LoopAgent/LoopEvaluator.
/// </summary>
public sealed class LoopGuardModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "loop_guard";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<ILoopGuard, LoopGuardEvaluator>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies"],
            Capabilities = ["loop_detection", "iteration_limits", "budget_checks", "repeat_detection"],
            Language = "C#",
            MafPrimitives = ["loop"],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}

/// <summary>
/// Loop guard evaluator using F# strategies for pure detection logic.
/// Uses MAF LoopAgent/LoopEvaluator with hard iteration limits.
/// </summary>
internal sealed class LoopGuardEvaluator : ILoopGuard
{
    public Task<LoopGuardDecision> EvaluateAsync(
        string sessionId,
        string runId,
        LoopGuardContext context,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        if (LoopDetection.isOverIterationLimit(context.Iteration, context.MaxIterations))
            return Task.FromResult(new LoopGuardDecision
            {
                ShouldContinue = false,
                Reason = $"Iteration limit reached: {context.Iteration}/{context.MaxIterations}"
            });

        if (LoopDetection.isTokenBudgetExhausted(context.TokensUsed, context.TokenBudget))
            return Task.FromResult(new LoopGuardDecision
            {
                ShouldContinue = false,
                Reason = $"Token budget exhausted: {context.TokensUsed}/{context.TokenBudget}"
            });

        if (LoopDetection.isToolCallLimitExceeded(context.ToolCallCount, context.MaxToolCalls))
            return Task.FromResult(new LoopGuardDecision
            {
                ShouldContinue = false,
                Reason = $"Tool call limit exceeded: {context.ToolCallCount}/{context.MaxToolCalls}"
            });

        if (LoopDetection.hasTooManyConsecutiveErrors(context.ConsecutiveErrors, context.MaxConsecutiveErrors))
            return Task.FromResult(new LoopGuardDecision
            {
                ShouldContinue = false,
                Reason = $"Too many consecutive errors: {context.ConsecutiveErrors}/{context.MaxConsecutiveErrors}"
            });

        if (LoopDetection.detectRepeatCalls(context.RecentToolCallFingerprints))
            warnings.Add("Repeated tool call detected — consider alternative approach.");

        return Task.FromResult(new LoopGuardDecision
        {
            ShouldContinue = true,
            Warnings = warnings
        });
    }
}
