namespace TinadecCore.DmaEA;

/// <summary>
/// Deterministic close-out prompt. It is injected on the single text-only model
/// turn that follows a budget or loop-guard stop, after the engine has withdrawn
/// every tool declaration.
///
/// Why a static, in-process prompt rather than the authorable prompt pipeline:
/// <c>IPromptAssembler</c> plus the persisted fragment store exist for *agent*
/// instructions, and routing a Core-owned runtime protocol through them would
/// bind it to the agent-pack contract (version and digest bumps). It also cannot
/// live in the Prompts module: architecture rule
/// <c>ModulesDoNotReferenceEachOtherDirectly</c> forbids DmaEA from referencing
/// that project. The engine's other runtime protocols (<c>WorkerPatchProtocol</c>,
/// the lane directives) are deterministic inline text, so the close-out follows
/// the same shape.
///
/// Both Chinese and English are emitted regardless of the conversation language:
/// the stop is a machine event (which budget fired, with which numbers) and the
/// model must not be able to lose it in translation. The wording is deliberately
/// modelled on codex's <c>budget_limit.md</c> (objective / budget used / budget
/// remaining / close-out requirements).
/// </summary>
internal static class CloseoutPrompts
{
    /// <summary>Everything the close-out turn must state, already resolved to
    /// concrete numbers. Never carries secrets: the trigger lines are budgets,
    /// counters and tool ids.</summary>
    internal sealed record CloseoutContext(
        string Category,
        string Reason,
        bool HardCeiling,
        int Rounds,
        int EffectiveRoundLimit,
        string RoundLimitSource,
        int ToolCalls,
        int MaxToolCalls,
        long TaskTokens,
        int TaskTokenBudget,
        long RunTokens,
        int RunTokenBudget,
        IReadOnlyList<string> LastToolIds);

    /// <summary>
    /// Builds the close-out instruction. The model is asked to answer four
    /// questions and is told that this text becomes the task's recorded
    /// evidence — which is what makes "completed but the goal was not met" an
    /// explicit, auditable statement instead of a silent pass.
    /// </summary>
    internal static string Build(CloseoutContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var roundLimit = context.EffectiveRoundLimit > 0
            ? $"{context.EffectiveRoundLimit} ({context.RoundLimitSource})"
            : $"unlimited / 无限（{context.RoundLimitSource}；由 max_tool_calls 保险丝兜底）";
        var fuse = context.MaxToolCalls > 0 ? context.MaxToolCalls.ToString() : "disabled";
        var taskBudget = context.TaskTokenBudget > 0 ? context.TaskTokenBudget.ToString() : "disabled";
        var runBudget = context.RunTokenBudget > 0 ? context.RunTokenBudget.ToString() : "disabled";
        var lastTools = context.LastToolIds.Count == 0 ? "(none)" : string.Join(", ", context.LastToolIds);
        var hardCeiling = context.HardCeiling
            ? "\n异常路径 / ABNORMAL PATH: this stop came from the absolute call FUSE (`hard_ceiling`), not from a spent budget."
            : string.Empty;

        return $"""

【预算耗尽收尾 / BUDGET CLOSE-OUT】
工具已被收回：本轮不要再请求任何工具调用，只输出文本。
TOOLS ARE WITHDRAWN: do not request any tool call in this turn; answer in plain text only.

触发原因 / trigger: {context.Category}
说明 / detail: {context.Reason}{hardCeiling}
已用回合 / rounds used: {context.Rounds}（生效上限 / effective round limit: {roundLimit}）
已发调用 / tool calls used: {context.ToolCalls}（保险丝 / fuse: {fuse}）
本任务 token / task tokens: {context.TaskTokens} / {taskBudget}
本 run token / run tokens: {context.RunTokens} / {runBudget}
最后 3 次调用 / last 3 tool calls: {lastTools}

收尾要求（以下四项必须逐条回答，中文或英文均可）/ CLOSE-OUT REQUIREMENTS (answer all four):
1. 已完成什么 / what has been completed — 具体到文件、命令或产物。
2. 还剩什么 / what remains — 未完成的部分要写清楚，不要含糊。
3. 为什么停在这里 / why it stopped — 写出上面的触发原因与具体数值（哪个预算、用了多少、上限多少）。
4. 下一步建议 / recommended next step for the user — 可执行的下一步，而不是泛泛而谈。

如果目标尚未达成，必须明确写出「未达成」以及差在哪里；这段文本会作为本任务的证据记录，所以不要用「已完成」掩盖未完成的工作。
If the goal was not reached, say so explicitly and state precisely what is missing. This text becomes the task's recorded evidence, so a false "done" is worse than an honest partial result.
""";
    }
}
