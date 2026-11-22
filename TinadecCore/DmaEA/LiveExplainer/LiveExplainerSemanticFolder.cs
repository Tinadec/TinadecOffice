using System.Text.Json;
using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA.LiveExplainer;

public sealed record FoldedToolExecution(
    ChapterToolCallDto ToolCall,
    TouchedFileStatDto? TouchedFile,
    string SemanticLine,
    string InferredPhase,
    bool IsFailure,
    string? DetectedSignalCode,
    string? SignalSummary);

public sealed record ScoutAssignment(
    string ScoutId,
    string Reason,
    string FocusPrompt);

/// <summary>
/// L0 zero-latency semantic folder (0ms / 0 tokens): folds verbose raw tool outputs, extracts
/// file +/- statistics, distills <c>&lt;thought&gt;</c> blocks, maps the 7 governance red-line
/// signals into gentle sentinel alerts with actionable steer prompts, and decides when L2
/// read-only scouts are warranted.
/// </summary>
public static partial class LiveExplainerSemanticFolder
{
    public const double ContextPressureThresholdPercent = 80.0;

    [GeneratedRegex(@"<(?:thought|thinking)>([\s\S]*?)(?:</(?:thought|thinking)>|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThoughtBlockRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    public static string? ExtractThoughtExcerpt(string? rawStreamBuffer, int maxChars = 280)
    {
        if (string.IsNullOrWhiteSpace(rawStreamBuffer)) return null;

        var matches = ThoughtBlockRegex().Matches(rawStreamBuffer);
        string candidate;
        if (matches.Count > 0)
        {
            candidate = matches[^1].Groups[1].Value;
        }
        else
        {
            return null;
        }

        var normalized = WhitespaceRegex().Replace(candidate, " ").Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        return normalized.Length <= maxChars ? normalized : normalized[^maxChars..].TrimStart();
    }

    public static FoldedToolExecution FoldToolExecution(ToolExecutionSnapshot execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var toolName = string.IsNullOrWhiteSpace(execution.ToolId) ? "tool" : execution.ToolId.Trim();
        var normalizedStatus = NormalizeToolStatus(execution);
        var isFailure = normalizedStatus == "failed";

        using var paramsDoc = TryParseJson(execution.ParametersJson);
        using var resultDoc = TryParseJson(execution.ResultJson);

        var targetPath = ExtractPath(paramsDoc?.RootElement) ?? ExtractPath(resultDoc?.RootElement);
        var commandPreview = ExtractString(paramsDoc?.RootElement, "command", "cmd", "CommandLine", "query", "pattern");

        var touchedFile = ExtractFileDiffStats(toolName, targetPath, paramsDoc?.RootElement, resultDoc?.RootElement, isFailure);
        var compactSummary = BuildCompactSummary(toolName, normalizedStatus, targetPath, commandPreview, touchedFile, resultDoc?.RootElement, execution.SafeErrorMessage);
        var phase = InferPhaseFromTool(toolName, commandPreview);
        var (signalCode, signalSummary) = ClassifyToolGovernanceSignal(execution, targetPath);

        var toolCallDto = new ChapterToolCallDto
        {
            ToolCallId = string.IsNullOrWhiteSpace(execution.ToolCallKey) ? execution.Id.ToString("N") : execution.ToolCallKey,
            ToolName = toolName,
            Status = normalizedStatus,
            CompactSummary = compactSummary
        };

        var semanticLine = $"[{normalizedStatus}] {toolName}: {compactSummary}";
        return new FoldedToolExecution(toolCallDto, touchedFile, semanticLine, phase, isFailure, signalCode, signalSummary);
    }

    public static (string Code, string Severity, string Summary)? ClassifyGovernanceTopic(GovernanceTopicSignal signal)
    {
        if (signal is null || string.IsNullOrWhiteSpace(signal.Topic)) return null;
        var topic = signal.Topic.Trim().ToLowerInvariant();
        var summaryLower = (signal.Summary ?? string.Empty).ToLowerInvariant();

        return topic switch
        {
            GovernanceTopics.LeaseConflict => (
                GovernanceSignalCodes.WorktreeGuard,
                "warning",
                string.IsNullOrWhiteSpace(signal.Summary) ? "检测到并行文件/工作树资源租约冲突，治理层已拦截写入。" : signal.Summary),
            GovernanceTopics.BudgetTick => (
                GovernanceSignalCodes.ContextPressure,
                "warning",
                string.IsNullOrWhiteSpace(signal.Summary) ? "任务上下文或轮次预算水位接近上限。" : signal.Summary),
            _ when summaryLower.Contains("approval_rejected") || summaryLower.Contains("rejected") => (
                GovernanceSignalCodes.ApprovalRejected,
                "warning",
                signal.Summary ?? "工具调用审批被拒绝。"),
            _ when summaryLower.Contains("stale") || summaryLower.Contains("etag") || summaryLower.Contains("version conflict") => (
                GovernanceSignalCodes.FsStaleVersion,
                "warning",
                signal.Summary ?? "检测到目标文件版本已过期。"),
            _ when summaryLower.Contains("loop") => (
                GovernanceSignalCodes.LoopDetected,
                "critical",
                signal.Summary ?? "治理层检测到重复执行循环。"),
            _ when summaryLower.Contains("timeout") || summaryLower.Contains("timed out") => (
                GovernanceSignalCodes.Timeout,
                "warning",
                signal.Summary ?? "执行步骤发生超时。"),
            _ when summaryLower.Contains("worktree") || summaryLower.Contains("write_scope") => (
                GovernanceSignalCodes.WorktreeGuard,
                "warning",
                signal.Summary ?? "触发工作树隔离守卫。"),
            _ => null
        };
    }

    public static (string Code, string Severity, string Summary)? ClassifyLifecycleEvent(string eventType, string? summary, IReadOnlyDictionary<string, object?>? payload)
    {
        if (string.IsNullOrWhiteSpace(eventType)) return null;
        var lowerType = eventType.Trim().ToLowerInvariant();
        var lowerSummary = (summary ?? string.Empty).ToLowerInvariant();

        if (lowerType.Contains("approval.rejected") || lowerType.Contains("approval_rejected") || lowerType.Contains("tool.rejected"))
            return (GovernanceSignalCodes.ApprovalRejected, "warning", FirstNonEmpty(summary, "工具调用审批被拒绝。"));

        if (lowerType.Contains("stale") || lowerSummary.Contains("fs_stale_version") || lowerSummary.Contains("stale version"))
            return (GovernanceSignalCodes.FsStaleVersion, "warning", FirstNonEmpty(summary, "检测到目标文件版本已过期。"));

        if (lowerType.Contains("loop") || lowerSummary.Contains("loop_guard") || lowerSummary.Contains("loop detected"))
            return (GovernanceSignalCodes.LoopDetected, "critical", FirstNonEmpty(summary, "治理层检测到重复执行循环。"));

        if (lowerType.Contains("timeout") || lowerSummary.Contains("timed out"))
            return (GovernanceSignalCodes.Timeout, "warning", FirstNonEmpty(summary, "执行步骤发生超时。"));

        if (lowerType.Contains("lease.conflict") || lowerType.Contains("worktree_guard") || lowerSummary.Contains("write_scope"))
            return (GovernanceSignalCodes.WorktreeGuard, "warning", FirstNonEmpty(summary, "触发工作树隔离或写权限范围守卫。"));

        if (lowerType.Contains("context_pressure") || lowerSummary.Contains("context pressure"))
            return (GovernanceSignalCodes.ContextPressure, "warning", FirstNonEmpty(summary, "上下文 Token 占用进入高压区间。"));

        return null;
    }

    public static SentinelAlertDto CreateSentinelAlert(string code, string? detailSummary = null, string? teammateHandle = null, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var (severity, title, defaultMessage, steerPrompt) = code switch
        {
            GovernanceSignalCodes.ApprovalRejected => (
                "warning",
                "高风险操作已被拒绝",
                "执行层请求的工具操作未获批准，若不调整策略可能再次卡在同一操作。",
                "刚才的工具操作未获批准，请改用只读检查或更小范围的安全方案继续执行。"),
            GovernanceSignalCodes.FsStaleVersion => (
                "warning",
                "文件版本过期冲突",
                "尝试写入的文件在读取后已被更新，直接覆盖会丢失最新改动。",
                "目标文件版本已更新，请先用只读工具重新读取最新文件内容，再做精确增量修改。"),
            GovernanceSignalCodes.SameArgsRetry => (
                "warning",
                "连续重复相同参数调用",
                "执行层已连续多次使用完全相同的参数调用同一工具，大概率无法通过简单重试恢复。",
                "请停止用相同参数重复调用该工具，先检查报错根因并换一种验证或修复路径。"),
            GovernanceSignalCodes.Timeout => (
                "warning",
                "工具或命令执行超时",
                "当前命令耗时超出预期阈值，可能扫描范围过大或存在阻塞进程。",
                "上一步命令执行超时，请缩小扫描/测试范围（例如指定单个目录或单测过滤条件）后重试。"),
            GovernanceSignalCodes.WorktreeGuard => (
                "warning",
                "工作树与写边界守卫拦截",
                "操作触及了工作树隔离边界或与其他并行队友的文件租约冲突。",
                "请严格在分配给你的目录与文件范围内修改，避免触碰其他队友持有的文件。"),
            GovernanceSignalCodes.LoopDetected => (
                "critical",
                "疑似陷入试错循环",
                "治理层检测到多轮相似工具调用未产生实质推进，建议介入收敛方向。",
                "请暂停当前重复试错，先简要汇总目前已确认的事实与卡点，等待我确认下一步方案。"),
            GovernanceSignalCodes.ContextPressure => (
                "warning",
                "上下文水位接近压缩阈值",
                "当前会话上下文 Token 占用较高，继续读取超大原文可能触发频繁压缩。",
                "当前上下文水位较高，请按行号精确读取关键片段，并尽快收口当前阶段结论。"),
            _ => (
                "warning",
                "治理层运行提示",
                FirstNonEmpty(detailSummary, "检测到需要关注的执行信号。"),
                "请先复核当前步骤假设，确认无误后再继续执行。")
        };

        var message = string.IsNullOrWhiteSpace(detailSummary)
            ? defaultMessage
            : $"{defaultMessage}（{TruncateSingleLine(detailSummary, 100)}）";

        return new SentinelAlertDto
        {
            Id = $"sentinel-{code.ToLowerInvariant()}-{timestamp.UtcTicks:x}",
            Code = code,
            Severity = severity,
            Title = title,
            Message = message,
            SuggestedSteerPrompt = steerPrompt,
            TeammateHandle = teammateHandle,
            CreatedAt = timestamp
        };
    }

    public static string BuildDefaultPhaseTitle(string phase, int chapterIndex) => phase switch
    {
        LiveDigestPhases.Verification => $"第 {chapterIndex} 阶段 · 修改与工具验证",
        LiveDigestPhases.Conclusion => $"第 {chapterIndex} 阶段 · 收敛验证与结论汇总",
        _ => $"第 {chapterIndex} 阶段 · 问题定位与假设建立"
    };

    public static string BuildDefaultHeadline(
        string runStatus,
        string currentPhase,
        IReadOnlyList<ChapterToolCallDto> recentTools,
        IReadOnlyList<TeammateThoughtDto> teammates,
        IReadOnlyList<GovernanceSignalDto> signals)
    {
        if (runStatus == "completed") return "已完成全部验证与汇总，点击各章节可回放完整证据链";
        if (runStatus == "failed") return "运行遇到错误中止，请查看最新章节与哨兵提示定位原因";
        if (runStatus == "cancelled") return "运行已按指令取消";
        if (runStatus == "awaiting_approval") return "正在等待工具操作审批确认";

        if (signals.Count > 0 && (DateTimeOffset.UtcNow - signals[^1].OccurredAt).TotalSeconds < 20)
        {
            return $"治理层提示：{TruncateSingleLine(signals[^1].Summary, 64)}";
        }

        var workingTeammates = teammates.Where(t => t.Status == "working").ToArray();
        if (workingTeammates.Length >= 2)
        {
            return $"{workingTeammates.Length} 位执行层队友正在并行推进（{string.Join("、", workingTeammates.Take(3).Select(t => t.Handle))}）";
        }

        if (recentTools.Count > 0)
        {
            var last = recentTools[^1];
            return $"正在执行 {last.ToolName}：{TruncateSingleLine(last.CompactSummary, 56)}";
        }

        return currentPhase switch
        {
            LiveDigestPhases.Verification => "正在应用代码改动并运行验证检查",
            LiveDigestPhases.Conclusion => "正在核对执行结果并整理最终结论",
            _ => "正在梳理上下文并分析问题切入点"
        };
    }

    public static IReadOnlyList<ScoutAssignment> BuildScoutAssignments(
        IReadOnlyList<TouchedFileStatDto> touchedFiles,
        IReadOnlyList<GovernanceSignalDto> signals,
        IReadOnlyList<TeammateThoughtDto> teammates,
        IReadOnlyList<string> semanticWindow,
        int consecutiveFailures)
    {
        var assignments = new List<ScoutAssignment>(2);

        var totalChurn = touchedFiles.Sum(f => f.AddedLines + f.RemovedLines);
        if (totalChurn >= 120 || touchedFiles.Count >= 4)
        {
            var filesSummary = string.Join("; ", touchedFiles.Take(8).Select(f => $"{f.Path} (+{f.AddedLines}/-{f.RemovedLines})"));
            assignments.Add(new ScoutAssignment(
                "scout-large-diff",
                "large_batch_diff",
                $"请用 2 句话浓缩以下大批量文件改动的架构意图与风险点：{filesSummary}"));
        }

        var hasSameArgsRetry = signals.Any(s => s.Code == GovernanceSignalCodes.SameArgsRetry || s.Code == GovernanceSignalCodes.LoopDetected);
        if (hasSameArgsRetry || consecutiveFailures >= 2)
        {
            var recentLines = string.Join("\n", semanticWindow.TakeLast(6));
            assignments.Add(new ScoutAssignment(
                "scout-retry-diagnosis",
                "repeated_trial_error",
                $"执行层连续试错 >=2 次，请用 2 句话指出失败根因与纠偏方向：\n{recentLines}"));
        }

        var activeTeammates = teammates.Where(t => t.Status is "working" or "awaiting_approval").ToArray();
        if (assignments.Count < 2 && activeTeammates.Length >= 2)
        {
            var lanes = string.Join("\n", activeTeammates.Take(5).Select(t => $"- @{t.Handle} [{t.Status}] task={t.CurrentTask ?? "n/a"}: {t.Thought}"));
            assignments.Add(new ScoutAssignment(
                "scout-teammate-divergence",
                "parallel_teammates",
                $"多位执行层队友正在并行分叉推进，请用 2 句话浓缩各路分工协同状态与潜在冲突：\n{lanes}"));
        }

        return assignments.Take(2).ToArray();
    }

    public static IReadOnlyList<TouchedFileStatDto> MergeTouchedFiles(
        IEnumerable<TouchedFileStatDto> existing,
        TouchedFileStatDto? delta)
    {
        var map = new Dictionary<string, (int Added, int Removed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing)
        {
            if (string.IsNullOrWhiteSpace(item.Path)) continue;
            map[item.Path] = (item.AddedLines, item.RemovedLines);
        }

        if (delta is not null && !string.IsNullOrWhiteSpace(delta.Path))
        {
            if (map.TryGetValue(delta.Path, out var prev))
            {
                map[delta.Path] = (prev.Added + delta.AddedLines, prev.Removed + delta.RemovedLines);
            }
            else
            {
                map[delta.Path] = (delta.AddedLines, delta.RemovedLines);
            }
        }

        return map
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new TouchedFileStatDto
            {
                Path = kv.Key,
                AddedLines = kv.Value.Added,
                RemovedLines = kv.Value.Removed
            })
            .ToArray();
    }

    private static string NormalizeToolStatus(ToolExecutionSnapshot execution)
    {
        var status = (execution.Status ?? string.Empty).Trim().ToLowerInvariant();
        if (status is ToolDispatchStatus.Failed or ToolDispatchStatus.Rejected or ToolDispatchStatus.NotApproved
            or ToolDispatchStatus.Timeout or ToolDispatchStatus.ProcessExit or ToolDispatchStatus.Blocked)
        {
            return "failed";
        }
        if (execution.ToolSuccess == false || string.Equals(execution.Outcome, ToolOutcomes.Error, StringComparison.OrdinalIgnoreCase)
            || string.Equals(execution.Outcome, ToolOutcomes.Blocked, StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }
        if (status == ToolDispatchStatus.Completed || execution.CompletedAt.HasValue)
        {
            return "succeeded";
        }
        return "running";
    }

    private static (string? Code, string? Summary) ClassifyToolGovernanceSignal(ToolExecutionSnapshot execution, string? targetPath)
    {
        var status = (execution.Status ?? string.Empty).Trim().ToLowerInvariant();
        var errorCat = (execution.ErrorCategory ?? string.Empty).Trim().ToLowerInvariant();
        var errMsg = (execution.SafeErrorMessage ?? string.Empty).Trim();
        var errLower = errMsg.ToLowerInvariant();
        var policyLower = (execution.PolicyRef ?? string.Empty).Trim().ToLowerInvariant();

        if (status is ToolDispatchStatus.Rejected or ToolDispatchStatus.NotApproved
            || errorCat.Contains("approval") || errLower.Contains("approval rejected"))
        {
            return (GovernanceSignalCodes.ApprovalRejected,
                $"工具 {execution.ToolId} 审批被拒绝{(targetPath is null ? "" : $" ({targetPath})")}");
        }

        if (status == ToolDispatchStatus.Timeout || errorCat.Contains("timeout") || errLower.Contains("timed out") || errLower.Contains("timeout"))
        {
            return (GovernanceSignalCodes.Timeout,
                $"工具 {execution.ToolId} 执行超时");
        }

        if (errorCat.Contains("stale") || errLower.Contains("stale") || errLower.Contains("etag") || errLower.Contains("expected_hash") || errLower.Contains("version conflict"))
        {
            return (GovernanceSignalCodes.FsStaleVersion,
                $"文件版本过期冲突{(targetPath is null ? "" : $"：{targetPath}")}");
        }

        if (errorCat.Contains("resource_conflict") || errorCat.Contains("lease") || errorCat.Contains("worktree")
            || policyLower.Contains("worktree") || policyLower.Contains("write_scope")
            || errLower.Contains("worktree") || errLower.Contains("write scope") || errLower.Contains("outside workspace"))
        {
            return (GovernanceSignalCodes.WorktreeGuard,
                $"工作树或资源写边界拦截：{FirstNonEmpty(errMsg, execution.ToolId)}");
        }

        if (errorCat.Contains("loop") || errLower.Contains("loop_guard"))
        {
            return (GovernanceSignalCodes.LoopDetected,
                $"工具循环守卫触发：{execution.ToolId}");
        }

        return (null, null);
    }

    private static string InferPhaseFromTool(string toolName, string? commandPreview)
    {
        var lower = toolName.ToLowerInvariant();
        if (lower.Contains("edit") || lower.Contains("write") || lower.Contains("patch") || lower.Contains("create")
            || lower.Contains("delete") || lower.Contains("replace") || lower.Contains("test") || lower.Contains("build"))
        {
            return LiveDigestPhases.Verification;
        }

        if (lower.Contains("shell") || lower.Contains("bash") || lower.Contains("cmd") || lower.Contains("pwsh") || lower.Contains("terminal") || lower.Contains("exec"))
        {
            var cmd = (commandPreview ?? string.Empty).ToLowerInvariant();
            if (cmd.Contains("test") || cmd.Contains("build") || cmd.Contains("tsc") || cmd.Contains("lint") || cmd.Contains("dotnet") || cmd.Contains("bun"))
            {
                return LiveDigestPhases.Verification;
            }
            return LiveDigestPhases.Verification;
        }

        return LiveDigestPhases.Hypothesis;
    }

    private static TouchedFileStatDto? ExtractFileDiffStats(
        string toolName,
        string? targetPath,
        JsonElement? parameters,
        JsonElement? result,
        bool isFailure)
    {
        if (isFailure || string.IsNullOrWhiteSpace(targetPath)) return null;
        var lowerTool = toolName.ToLowerInvariant();
        var isMutatingTool = lowerTool.Contains("edit") || lowerTool.Contains("write") || lowerTool.Contains("patch")
            || lowerTool.Contains("create") || lowerTool.Contains("replace");
        if (!isMutatingTool) return null;

        var added = ExtractInt(result, "added_lines", "lines_added", "insertions", "added");
        var removed = ExtractInt(result, "removed_lines", "lines_removed", "deletions", "removed");

        if (added is null && removed is null && parameters.HasValue)
        {
            var oldText = ExtractString(parameters, "target", "Target", "old_string", "old_text", "search");
            var newText = ExtractString(parameters, "replacement", "Replacement", "new_string", "new_text", "content", "Content");

            if (oldText is not null || newText is not null)
            {
                var oldLines = CountLines(oldText);
                var newLines = CountLines(newText);
                if (oldText is null)
                {
                    added = Math.Max(1, newLines);
                    removed = 0;
                }
                else
                {
                    added = newLines;
                    removed = oldLines;
                }
            }
            else if (ExtractString(parameters, "patch", "diff") is { } patchText)
            {
                var (pAdd, pRem) = CountUnifiedDiffLines(patchText);
                added = pAdd;
                removed = pRem;
            }
        }

        if (added is null && removed is null)
        {
            added = 1;
            removed = 0;
        }

        var normalizedPath = NormalizeDisplayPath(targetPath);
        return new TouchedFileStatDto
        {
            Path = normalizedPath,
            AddedLines = Math.Max(0, added ?? 0),
            RemovedLines = Math.Max(0, removed ?? 0)
        };
    }

    private static string BuildCompactSummary(
        string toolName,
        string status,
        string? path,
        string? command,
        TouchedFileStatDto? touched,
        JsonElement? result,
        string? errorMessage)
    {
        if (status == "failed" && !string.IsNullOrWhiteSpace(errorMessage))
        {
            return $"{(path is not null ? NormalizeDisplayPath(path) + " · " : "")}{TruncateSingleLine(errorMessage, 96)}";
        }

        if (touched is not null)
        {
            return $"{touched.Path} (+{touched.AddedLines}/-{touched.RemovedLines})";
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var displayPath = NormalizeDisplayPath(path);
            var contentText = ExtractString(result, "content", "output", "text");
            if (!string.IsNullOrEmpty(contentText))
            {
                return $"{displayPath} ({CountLines(contentText)} 行)";
            }
            return displayPath;
        }

        if (!string.IsNullOrWhiteSpace(command))
        {
            var exitCode = ExtractInt(result, "exit_code", "exitCode", "code");
            var cmdShort = TruncateSingleLine(command, 68);
            return exitCode.HasValue ? $"`{cmdShort}` → exit {exitCode.Value}" : $"`{cmdShort}`";
        }

        var fallbackText = ExtractString(result, "summary", "message", "status") ?? status;
        return TruncateSingleLine(fallbackText, 96);
    }

    private static JsonDocument? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string? ExtractPath(JsonElement? element) =>
        ExtractString(element, "path", "Path", "file_path", "filepath", "target_file", "filename");

    private static string? ExtractString(JsonElement? element, params string[] keys)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (element.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    private static int? ExtractInt(JsonElement? element, params string[] keys)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (element.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num))
            {
                return num;
            }
        }
        return null;
    }

    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
    }

    private static (int Added, int Removed) CountUnifiedDiffLines(string diff)
    {
        var added = 0;
        var removed = 0;
        foreach (var rawLine in diff.Split('\n'))
        {
            if (rawLine.StartsWith("+++", StringComparison.Ordinal) || rawLine.StartsWith("---", StringComparison.Ordinal))
                continue;
            if (rawLine.StartsWith('+')) added++;
            else if (rawLine.StartsWith('-')) removed++;
        }
        return (added, removed);
    }

    private static string NormalizeDisplayPath(string rawPath)
    {
        var normalized = rawPath.Trim().Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 4 ? string.Join('/', parts) : string.Join('/', parts[^4..]);
    }

    private static string FirstNonEmpty(string? primary, string fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();

    public static string TruncateSingleLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var clean = WhitespaceRegex().Replace(value, " ").Trim();
        return clean.Length <= maxLength ? clean : $"{clean[..Math.Max(1, maxLength - 1)]}…";
    }
}
