using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Renders the run's plan ledger (B2) as context evidence for an execution-layer worker. The
/// ledger is shared between parallel workers, so the rendering says what is still open and who
/// already owns what; it deliberately carries no conclusions — a record only reaches the final
/// report through the evidence archive.
/// </summary>
internal static class PlanLedgerPrompt
{
    private const int MaxRenderedIntents = 40;
    private const int MaxTitleChars = 240;
    private const int MaxNoteChars = 240;
    private const int MaxEvidenceRefs = 8;

    /// <summary>Returns null for an empty ledger: there is nothing a worker could act on.</summary>
    internal static string? Format(IReadOnlyList<PlanIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(intents);
        if (intents.Count == 0) return null;

        var ordered = intents.OrderBy(intent => intent.Ordinal).ToArray();
        var shown = ordered.Take(MaxRenderedIntents).ToArray();
        var pending = ordered.Count(intent => intent.Status == PlanIntentStatuses.Pending);
        var running = ordered.Count(intent => intent.Status == PlanIntentStatuses.Running);
        var done = ordered.Count(intent => intent.Status == PlanIntentStatuses.Done);
        var stopped = ordered.Count(intent => intent.Status is PlanIntentStatuses.Blocked or PlanIntentStatuses.Abandoned);

        var text = "【run 执行台账 / RUN PLAN LEDGER】\n"
            + "本次运行在并行队友间共享一份执行台账（与 SessionPlan / team_plan 协同），用于明确未完成事项与认领归属。"
            + "This run keeps one plan shared across its workers (aligned with SessionPlan / team_plan). The list is the plan, not evidence: "
            + "a conclusion still needs concrete evidence_refs.\n"
            + "认领未开始的事项后再动手（启用 plan_ledger_update 时用 action=claim；团队协作时同步更新 team_plan 并通过 team_message 同步阻塞或交接）；"
            + "Claim a pending intent with plan_ledger_update (action=claim) before working on it; only the worker "
            + "that claimed an intent may complete or block it. Keep isolated worktree edits within disjoint scopes, and do not retry a blocked intent as-is.\n"
            + $"open: {pending} pending, {running} running; finished: {done} done; stopped: {stopped} blocked/abandoned; total: {ordered.Length}.\n"
            + string.Join("\n", shown.Select(Render));
        if (ordered.Length > shown.Length)
        {
            text += $"\n({ordered.Length - shown.Length} more intents are not shown; read them with plan_ledger_read.)";
        }
        return text;
    }

    private static string Render(PlanIntent intent)
    {
        var line = $"- #{intent.Ordinal} [{intent.Status}] {Clip(intent.Title, MaxTitleChars)}";
        if (intent.ClaimedByAgentInstanceId is { } owner) line += $" (claimed by {owner})";
        if (intent.EvidenceRefs.Count > 0)
        {
            var refs = intent.EvidenceRefs.Take(MaxEvidenceRefs).Select(reference => Clip(reference, 120));
            line += $" evidence: {string.Join(", ", refs)}";
            if (intent.EvidenceRefs.Count > MaxEvidenceRefs) line += $", +{intent.EvidenceRefs.Count - MaxEvidenceRefs} more";
        }
        if (!string.IsNullOrWhiteSpace(intent.Note)) line += $" note: {Clip(intent.Note, MaxNoteChars)}";
        return line;
    }

    private static string Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
