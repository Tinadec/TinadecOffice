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
        // Every veto carries a RunErrorTaxonomy category and the concrete numbers
        // behind it. The engine turns a veto into a graceful close-out (tools are
        // withdrawn and the model is asked to hand off) instead of failing the
        // task, so "why it stopped" must be actionable without parsing prose.
        if (LoopDetection.isOverIterationLimit(context.Iteration, context.MaxIterations))
            return Veto(RunErrorTaxonomy.ToolRoundLimit,
                $"Iteration limit reached: {context.Iteration}/{context.MaxIterations}");

        if (LoopDetection.isTokenBudgetExhausted(context.TokensUsed, context.TokenBudget))
            return Veto(RunErrorTaxonomy.TokenBudgetExhausted,
                $"Task token budget exhausted: {context.TokensUsed}/{context.TokenBudget}");

        if (LoopDetection.isToolCallLimitExceeded(context.ToolCallCount, context.MaxToolCalls))
            return Veto(RunErrorTaxonomy.ToolCallCeiling,
                $"Tool call fuse tripped: {context.ToolCallCount}/{context.MaxToolCalls}", hardCeiling: true);

        if (LoopDetection.hasTooManyConsecutiveErrors(context.ConsecutiveErrors, context.MaxConsecutiveErrors))
            return Veto(RunErrorTaxonomy.TooManyConsecutiveErrors,
                $"Too many consecutive errors: {context.ConsecutiveErrors}/{context.MaxConsecutiveErrors}");

        // A stuck worker repeats the identical call; three in a row is the signal
        // to stop it rather than burn the remaining rounds. Repeat detection is
        // a veto, not advice — callers rely on ShouldContinue to stop the loop.
        if (LoopDetection.detectRepeatCalls(context.RecentToolCallFingerprints))
            return Veto(RunErrorTaxonomy.ToolLoopDetected,
                $"Repeated identical tool call detected ({DescribeRepeat(context.RecentToolCallFingerprints)}); "
                + "the loop guard stopped the task.");

        return Task.FromResult(new LoopGuardDecision
        {
            ShouldContinue = true
        });
    }

    private static Task<LoopGuardDecision> Veto(string category, string reason, bool hardCeiling = false) =>
        Task.FromResult(new LoopGuardDecision
        {
            ShouldContinue = false,
            Reason = reason,
            Category = category,
            HardCeiling = hardCeiling
        });

    /// <summary>
    /// Names the repeated fingerprint (truncated) so the report says which call
    /// the worker was stuck on, e.g. <c>shell:{"command":"dotnet test"}</c>.
    /// Arguments are model-authored tool parameters, never secrets.
    /// </summary>
    private static string DescribeRepeat(IReadOnlyList<string> fingerprints)
    {
        var repeated = fingerprints.Count == 0 ? string.Empty : fingerprints[^1];
        if (repeated.Length == 0) return "fingerprint unavailable";
        return repeated.Length <= 120 ? repeated : repeated[..120] + "...";
    }
}
