using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Context;

/// <summary>
/// Non-destructive, audit-commented TOML updater for <c>runtime.toml</c>.
/// Preserves existing comments and layout, writes structured provenance comments
/// (proposal ID, triggering <c>run_id</c> list, degradation metrics &amp; before/after values,
/// applied UTC timestamp) next to every updated key, and maintains the documented
/// conservative switch <c>auto_tune_low_risk_context_policy = false</c>.
/// </summary>
public static class RuntimeTomlCommentedWriter
{
    public const string AutoTuneSwitchKey = "auto_tune_low_risk_context_policy";
    private const string AuditCommentPrefix = "# [context-evolution]";

    private static readonly Regex TomlHeaderRegex = new(
        @"^\s*\[(?<table>[A-Za-z0-9_.\-]+)\]\s*(#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TomlAssignmentRegex = new(
        @"^(?<indent>\s*)(?<key>[A-Za-z0-9_\-]+)(?<eq>\s*=\s*)(?<value>[^#]*?)(?<comment>\s*#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CommentedAutoTuneRegex = new(
        @"^\s*#\s*auto_tune_low_risk_context_policy\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads <c>auto_tune_low_risk_context_policy</c> from <c>[evolution]</c> or <c>[context]</c>.
    /// Strictly defaults to <c>false</c> when omitted, commented-out, or unparseable.
    /// </summary>
    public static bool ReadAutoTuneLowRiskSwitch(string? tomlText)
    {
        if (string.IsNullOrWhiteSpace(tomlText)) return false;

        var lines = tomlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? currentTable = null;
        bool? contextValue = null;
        bool? evolutionValue = null;

        foreach (var rawLine in lines)
        {
            var header = TomlHeaderRegex.Match(rawLine);
            if (header.Success)
            {
                currentTable = header.Groups["table"].Value.Trim();
                continue;
            }

            if (currentTable is not ("context" or "evolution")) continue;
            var assign = TomlAssignmentRegex.Match(rawLine);
            if (!assign.Success) continue;
            if (!string.Equals(assign.Groups["key"].Value, AutoTuneSwitchKey, StringComparison.Ordinal)) continue;

            var rawVal = assign.Groups["value"].Value.Trim();
            if (bool.TryParse(rawVal, out var parsed))
            {
                if (currentTable == "evolution") evolutionValue = parsed;
                else contextValue = parsed;
            }
        }

        return evolutionValue ?? contextValue ?? false;
    }

    /// <summary>
    /// Validates and applies a <see cref="ContextEvolutionProposalDto"/> policy patch onto <paramref name="baseTomlText"/>,
    /// writing explicit audit comments above and inline beside every updated key and ensuring the
    /// documented <c>auto_tune_low_risk_context_policy = false</c> switch is present.
    /// </summary>
    public static string ApplyContextPolicyProposal(
        string baseTomlText,
        ContextEvolutionProposalDto proposal,
        DateTimeOffset? appliedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var patch = proposal.PolicyPatch
            ?? throw new ArgumentException("Proposal does not contain a policy_patch.", nameof(proposal));

        ValidatePatch(patch);

        var timestamp = (appliedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var normalizedBase = string.IsNullOrWhiteSpace(baseTomlText)
            ? BuildMinimalRuntimeToml()
            : baseTomlText;

        var newline = normalizedBase.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = normalizedBase.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var existingValues = ScanCurrentValues(lines);
        var updates = BuildTableUpdates(patch);
        if (updates.Count == 0)
        {
            throw new ArgumentException("Proposal policy_patch contains no non-null fields to apply.", nameof(proposal));
        }

        var hasAutoTuneKeyOrComment = HasAutoTuneDeclaration(lines);

        var output = new List<string>(lines.Count + 24);
        var seenTables = new HashSet<string>(StringComparer.Ordinal);
        var writtenKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? currentTable = null;

        void FlushRemainingTableUpdates(string? table)
        {
            if (table is null) return;
            if (updates.TryGetValue(table, out var tableUpdates))
            {
                if (!writtenKeys.TryGetValue(table, out var written))
                {
                    written = new HashSet<string>(StringComparer.Ordinal);
                    writtenKeys[table] = written;
                }

                foreach (var (key, formattedNewValue) in tableUpdates)
                {
                    if (!written.Add(key)) continue;
                    var previousDisplay = ResolveBeforeValue(existingValues, proposal, table, key);
                    var nextDisplay = StripQuotes(formattedNewValue);
                    var commentLines = BuildAuditCommentBlock(proposal, key, previousDisplay, nextDisplay, timestamp);
                    TrimTrailingBlankBeforeAppend(output);
                    output.AddRange(commentLines);
                    output.Add($"{key} = {formattedNewValue} # proposal={proposal.Id:D} ({previousDisplay} -> {nextDisplay})");
                }
            }

            if (table == "context" && !hasAutoTuneKeyOrComment)
            {
                AppendReservedAutoTuneSwitch(output);
                hasAutoTuneKeyOrComment = true;
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var headerMatch = TomlHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                FlushRemainingTableUpdates(currentTable);
                currentTable = headerMatch.Groups["table"].Value.Trim();
                seenTables.Add(currentTable);
                output.Add(line);
                continue;
            }

            if (currentTable is not null && updates.TryGetValue(currentTable, out var tableUpdates))
            {
                var assignMatch = TomlAssignmentRegex.Match(line);
                if (assignMatch.Success && tableUpdates.TryGetValue(assignMatch.Groups["key"].Value, out var replacementValue))
                {
                    var key = assignMatch.Groups["key"].Value;
                    var indent = assignMatch.Groups["indent"].Value;
                    var eq = assignMatch.Groups["eq"].Value;

                    // Remove any immediately preceding [context-evolution] comment lines from an earlier proposal on this key
                    while (output.Count > 0 && output[^1].TrimStart().StartsWith(AuditCommentPrefix, StringComparison.Ordinal))
                    {
                        output.RemoveAt(output.Count - 1);
                    }

                    var previousDisplay = ResolveBeforeValue(existingValues, proposal, currentTable, key);
                    var nextDisplay = StripQuotes(replacementValue);
                    foreach (var auditLine in BuildAuditCommentBlock(proposal, key, previousDisplay, nextDisplay, timestamp))
                    {
                        output.Add(indent + auditLine);
                    }

                    output.Add($"{indent}{key}{eq}{replacementValue} # proposal={proposal.Id:D} ({previousDisplay} -> {nextDisplay})");
                    writtenKeys.TryAdd(currentTable, new HashSet<string>(StringComparer.Ordinal));
                    writtenKeys[currentTable].Add(key);
                    continue;
                }
            }

            output.Add(line);
        }

        FlushRemainingTableUpdates(currentTable);

        // Append any target section ([context] or [memory]) that was completely absent from the input TOML
        foreach (var (table, tableUpdates) in updates)
        {
            if (seenTables.Contains(table)) continue;
            if (output.Count > 0 && !string.IsNullOrWhiteSpace(output[^1]))
            {
                output.Add(string.Empty);
            }
            output.Add($"[{table}]");
            seenTables.Add(table);
            foreach (var (key, formattedNewValue) in tableUpdates)
            {
                var previousDisplay = ResolveBeforeValue(existingValues, proposal, table, key);
                var nextDisplay = StripQuotes(formattedNewValue);
                output.AddRange(BuildAuditCommentBlock(proposal, key, previousDisplay, nextDisplay, timestamp));
                output.Add($"{key} = {formattedNewValue} # proposal={proposal.Id:D} ({previousDisplay} -> {nextDisplay})");
            }
            if (table == "context" && !hasAutoTuneKeyOrComment)
            {
                AppendReservedAutoTuneSwitch(output);
                hasAutoTuneKeyOrComment = true;
            }
        }

        // Also ensure [evolution] or [context] carries the reserved switch even if only [memory] was updated
        if (!hasAutoTuneKeyOrComment)
        {
            if (output.Count > 0 && !string.IsNullOrWhiteSpace(output[^1]))
            {
                output.Add(string.Empty);
            }
            output.Add("[evolution]");
            AppendReservedAutoTuneSwitch(output);
        }

        return string.Join(newline, output);
    }

    public static void ValidatePatch(ContextPolicyPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (patch.ContextStrategy is not null)
        {
            var strategy = patch.ContextStrategy.Trim().ToLowerInvariant();
            if (strategy is not ("direct" or "managed" or "deep_curator"))
            {
                throw new ArgumentException($"Invalid context_strategy '{patch.ContextStrategy}'. Expected: direct, managed, deep_curator.");
            }
        }

        if (patch.CompactionTriggerRatio is { } ratio)
        {
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio is < 0.40 or > 0.95)
            {
                throw new ArgumentException("compaction_trigger_ratio must be between 0.40 and 0.95.");
            }
        }

        if (patch.RecentMessageLimit is { } recentLimit && recentLimit is < 4 or > 200)
        {
            throw new ArgumentException("recent_message_limit must be between 4 and 200.");
        }

        if (patch.RecallFidelity is not null)
        {
            var fidelity = patch.RecallFidelity.Trim().ToLowerInvariant();
            if (fidelity is not ("summary_only" or "balanced" or "verbatim_first"))
            {
                throw new ArgumentException($"Invalid recall_fidelity '{patch.RecallFidelity}'. Expected: summary_only, balanced, verbatim_first.");
            }
        }

        if (patch.EvolutionMode is not null)
        {
            var mode = patch.EvolutionMode.Trim().ToLowerInvariant();
            if (mode is not ("auto_evolve" or "candidate_only"))
            {
                throw new ArgumentException($"Invalid evolution_mode '{patch.EvolutionMode}'. Expected: auto_evolve, candidate_only.");
            }
        }

        if (patch.CoreMarkdownMaxTokens is { } maxTokens && maxTokens is < 256 or > 8000)
        {
            throw new ArgumentException("core_markdown_max_tokens must be between 256 and 8000.");
        }
    }

    private static IReadOnlyList<string> BuildAuditCommentBlock(
        ContextEvolutionProposalDto proposal,
        string key,
        string previousValue,
        string newValue,
        string timestampUtc)
    {
        var runs = proposal.EvidenceRunIds is { Count: > 0 }
            ? string.Join(", ", proposal.EvidenceRunIds.Take(8))
            : (proposal.SourceRunId?.ToString("D") ?? "manual");

        var metrics = proposal.MetricsComparison;
        var degradationSummary = metrics is not null && metrics.WindowRunCount > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"degraded={metrics.DegradedRunCount}/{metrics.WindowRunCount} ({metrics.DegradationRate * 100.0:0.#}%), avg_peak={metrics.AvgPeakOccupancyPct:0.#}%, high_occ_err={metrics.HighOccupancyErrorRate * 100.0:0.#}% vs low_occ_err={metrics.LowOccupancyErrorRate * 100.0:0.#}%")
            : "human-approved context policy adjustment";

        return
        [
            $"{AuditCommentPrefix} proposal_id={proposal.Id:D} | applied_at={timestampUtc} | key={key} ({previousValue} -> {newValue})",
            $"{AuditCommentPrefix} evidence_run_ids=[{runs}] | {degradationSummary}"
        ];
    }

    private static void AppendReservedAutoTuneSwitch(List<string> output)
    {
        if (output.Count > 0 && !string.IsNullOrWhiteSpace(output[^1]))
        {
            output.Add(string.Empty);
        }
        output.Add("# 高级自进化开关（保守默认 false）：设为 false 时所有上下文策略微调提案均进入待人工审批队列；仅当显式改为 true 时允许自动应用低风险全局水位微调");
        output.Add("# Conservative default (false): keep false so context policy proposals always wait for human approval before mutating runtime.toml.");
        output.Add($"{AutoTuneSwitchKey} = false");
    }

    private static void TrimTrailingBlankBeforeAppend(List<string> output)
    {
        while (output.Count > 1 && string.IsNullOrWhiteSpace(output[^1]) && string.IsNullOrWhiteSpace(output[^2]))
        {
            output.RemoveAt(output.Count - 1);
        }
    }

    private static bool HasAutoTuneDeclaration(List<string> lines)
    {
        foreach (var line in lines)
        {
            if (CommentedAutoTuneRegex.IsMatch(line)) return true;
            var assign = TomlAssignmentRegex.Match(line);
            if (assign.Success && string.Equals(assign.Groups["key"].Value, AutoTuneSwitchKey, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, Dictionary<string, string>> ScanCurrentValues(List<string> lines)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? currentTable = null;
        foreach (var line in lines)
        {
            var header = TomlHeaderRegex.Match(line);
            if (header.Success)
            {
                currentTable = header.Groups["table"].Value.Trim();
                continue;
            }
            if (currentTable is null) continue;
            var assign = TomlAssignmentRegex.Match(line);
            if (!assign.Success) continue;
            map.TryAdd(currentTable, new Dictionary<string, string>(StringComparer.Ordinal));
            map[currentTable][assign.Groups["key"].Value] = StripQuotes(assign.Groups["value"].Value.Trim());
        }
        return map;
    }

    private static string ResolveBeforeValue(
        Dictionary<string, Dictionary<string, string>> existingValues,
        ContextEvolutionProposalDto proposal,
        string table,
        string key)
    {
        if (existingValues.TryGetValue(table, out var tableVals) && tableVals.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
        {
            return val;
        }
        if (proposal.MetricsComparison?.BeforeValues is { } before)
        {
            if (before.TryGetValue(key, out var b1) && !string.IsNullOrWhiteSpace(b1)) return b1;
            if (key == "strategy" && before.TryGetValue("context_strategy", out var b2) && !string.IsNullOrWhiteSpace(b2)) return b2;
        }
        return "default";
    }

    private static Dictionary<string, Dictionary<string, string>> BuildTableUpdates(ContextPolicyPatchDto patch)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        void Put(string table, string key, string formattedValue)
        {
            if (!result.TryGetValue(table, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
                result[table] = dict;
            }
            dict[key] = formattedValue;
        }

        if (!string.IsNullOrWhiteSpace(patch.ContextStrategy))
        {
            Put("context", "strategy", FormatTomlString(patch.ContextStrategy.Trim().ToLowerInvariant()));
        }
        if (patch.CompactionTriggerRatio is { } ratio)
        {
            Put("context", "compaction_trigger_ratio", Math.Round(ratio, 4).ToString("0.##", CultureInfo.InvariantCulture));
        }
        if (patch.RecentMessageLimit is { } recentLimit)
        {
            Put("context", "recent_message_limit", recentLimit.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(patch.RecallFidelity))
        {
            Put("context", "recall_fidelity", FormatTomlString(patch.RecallFidelity.Trim().ToLowerInvariant()));
        }
        if (patch.AllowCrossSessionRecall is { } allowCross)
        {
            Put("context", "allow_cross_session_recall", allowCross ? "true" : "false");
        }
        if (!string.IsNullOrWhiteSpace(patch.EvolutionMode))
        {
            var mode = patch.EvolutionMode.Trim().ToLowerInvariant();
            Put("memory", "evolution_mode", FormatTomlString(mode));
            Put("memory", "candidate_only", mode == "candidate_only" ? "true" : "false");
        }
        if (patch.CoreMarkdownMaxTokens is { } maxTokens)
        {
            Put("memory", "core_markdown_max_tokens", maxTokens.ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    private static string FormatTomlString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append(@"\"""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string StripQuotes(string raw) =>
        raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;

    private static string BuildMinimalRuntimeToml() =>
        """
        schema_version = 1

        [spawn]
        max_depth = 2
        max_agents_per_run = 16
        max_parallel_workers = 4

        [scheduling]
        max_active_runs_per_session = 2
        worker_retry_limit = 2
        preserve_partial_results = true

        [supervision]
        required_before_final = true
        max_revision_rounds = 2

        [context]
        strategy = "managed"
        default_token_budget = 131072
        recent_message_limit = 128
        compaction_trigger_ratio = 0.75
        recall_fidelity = "balanced"
        allow_cross_session_recall = true
        micro_compact_stale_tool_outputs = true
        offload_large_tool_payload_bytes = 16384
        optimistic_revision = true

        [memory]
        evolution_mode = "auto_evolve"
        candidate_only = false
        retrieval_limit = 8
        core_markdown_max_tokens = 1500
        project_markdown_projection = true
        project_memory_path = ".tinadec/memory/project-core.md"
        companion_can_write_project_memory = false
        allowed_scopes = ["principal", "workspace", "project", "agent", "companion"]
        allowed_kinds = ["fact", "preference", "decision", "success_pattern", "failure_pattern", "task_template", "supervision_rule", "bond", "episode"]

        [tools]
        provider = "tinadec-tools-process"
        mutation_requires_approval = true
        serialize_workspace_writes = true
        default_timeout_seconds = 600
        max_tool_rounds = 0
        max_tool_calls = 500
        """;
}
