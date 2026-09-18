namespace TinadecCore.DmaEA;

/// <summary>
/// Deterministic end-of-task protocol for worker text turns. Tool calls remain
/// structured function content; when a worker stops calling tools, its final lines
/// declare both the task outcome and the evidence for every success criterion.
/// </summary>
internal static class WorkerOutcomeProtocol
{
    internal const string Marker = "TASK_OUTCOME:";
    internal const string CriterionMarker = "CRITERION_EVIDENCE:";

    internal const string Instructions =
        "最终一轮不再调用工具时，先按每条成功标准原文各输出一行 "
        + "CRITERION_EVIDENCE: <成功标准原文> || <具体、可核对的证据>，然后把回复的最后一个非空行写成 "
        + "TASK_OUTCOME: completed、TASK_OUTCOME: blocked 或 TASK_OUTCOME: failed。"
        + "只有所有成功标准都已满足并有实际证据时才能写 completed；缺少工具、权限、信息或外部条件时写 blocked；"
        + "已经尝试但结果错误、无效或需要重做时写 failed。不要把‘未完成’写进正文后仍标 completed。";

    internal sealed record CriterionEvidence(string Criterion, string Evidence);

    internal sealed record Parsed(
        string Status,
        string Summary,
        IReadOnlyList<CriterionEvidence> Criteria,
        bool Explicit,
        bool MissingRequiredMarker = false,
        bool ContradictedCompletion = false);

    internal static Parsed Parse(string? value, bool requireExplicit = false)
    {
        var text = (value ?? string.Empty).Replace("\r\n", "\n").Trim();
        var lines = text.Split('\n').ToList();
        var lastNonEmpty = lines.FindLastIndex(line => !string.IsNullOrWhiteSpace(line));
        if (lastNonEmpty >= 0)
        {
            var finalLine = lines[lastNonEmpty].Trim();
            if (finalLine.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
            {
                var status = NormalizeStatus(finalLine[Marker.Length..]);
                if (status is not null)
                {
                    lines.RemoveAt(lastNonEmpty);
                    var criteria = ExtractCriterionEvidence(lines);
                    var summary = NormalizeSummary(lines, status);
                    var contradiction = status == "completed" ? InferLegacyFailure(summary) : null;
                    if (contradiction is not null)
                    {
                        return new Parsed(
                            contradiction,
                            summary,
                            criteria,
                            Explicit: true,
                            ContradictedCompletion: true);
                    }
                    return new Parsed(status, summary, criteria, Explicit: true);
                }
            }
        }

        var legacyCriteria = ExtractCriterionEvidence(lines);
        var legacySummary = NormalizeSummary(lines, "completed");
        var legacyStatus = InferLegacyFailure(legacySummary);
        if (legacyStatus is not null)
        {
            return new Parsed(legacyStatus, legacySummary, legacyCriteria, Explicit: false);
        }
        if (requireExplicit)
        {
            return new Parsed(
                "blocked",
                legacySummary,
                legacyCriteria,
                Explicit: false,
                MissingRequiredMarker: true);
        }
        return new Parsed("completed", legacySummary, legacyCriteria, Explicit: false);
    }

    private static IReadOnlyList<CriterionEvidence> ExtractCriterionEvidence(List<string> lines)
    {
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith(CriterionMarker, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveAt(index);
            var body = line[CriterionMarker.Length..].Trim();
            var separator = body.IndexOf("||", StringComparison.Ordinal);
            if (separator <= 0 || separator >= body.Length - 2) continue;
            var criterion = body[..separator].Trim();
            var detail = body[(separator + 2)..].Trim();
            if (criterion.Length == 0 || detail.Length == 0) continue;
            evidence[criterion] = detail;
        }
        return evidence.Select(pair => new CriterionEvidence(pair.Key, pair.Value)).ToArray();
    }

    private static string? NormalizeStatus(string candidate)
    {
        var token = candidate.Trim().Trim('`', '*', '_', '.', ';', ':').ToLowerInvariant();
        return token switch
        {
            "completed" => "completed",
            "blocked" => "blocked",
            "failed" => "failed",
            _ => null
        };
    }

    private static string NormalizeSummary(IReadOnlyList<string> lines, string status)
    {
        var summary = string.Join("\n", lines).Trim();
        if (summary.Length != 0) return summary;
        return status switch
        {
            "blocked" => "The worker reported that the task is blocked.",
            "failed" => "The worker reported that the task failed.",
            _ => "The worker reported that the task completed."
        };
    }

    /// <summary>
    /// Backward-compatible guard for providers or old prompt versions that omit the
    /// marker. It recognizes only direct, high-confidence failure declarations; all
    /// other legacy text keeps the previous completed default.
    /// </summary>
    private static string? InferLegacyFailure(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('-', '*', '>', '•').TrimStart();
            if (line.StartsWith("failed to complete", StringComparison.OrdinalIgnoreCase)) return "failed";
            if (line.StartsWith("task failed", StringComparison.OrdinalIgnoreCase)) return "failed";

            if (line.StartsWith("task not completed", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("task is not completed", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("not completed", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("unable to complete", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("cannot complete", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("could not complete", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("task blocked", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("blocked", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("任务未完成", StringComparison.Ordinal)
                || line.StartsWith("未能完成任务", StringComparison.Ordinal)
                || line.StartsWith("无法完成任务", StringComparison.Ordinal)
                || line.StartsWith("任务无法完成", StringComparison.Ordinal)
                || line.StartsWith("任务受阻", StringComparison.Ordinal))
            {
                return "blocked";
            }
        }
        return null;
    }
}
