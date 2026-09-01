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

        // A stuck worker repeats the identical call; three in a row is the signal
        // to stop it rather than burn the remaining rounds. Repeat detection is
        // a veto, not advice — callers rely on ShouldContinue to fail the task.
        if (LoopDetection.detectRepeatCalls(context.RecentToolCallFingerprints))
            return Task.FromResult(new LoopGuardDecision
            {
                ShouldContinue = false,
                Reason = "Repeated identical tool call detected; the loop guard rejected further rounds."
            });

        return Task.FromResult(new LoopGuardDecision
        {
            ShouldContinue = true
        });
    }
}
