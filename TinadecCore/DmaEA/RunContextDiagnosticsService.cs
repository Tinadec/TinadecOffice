using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.DmaEA.LiveExplainer;

namespace TinadecCore.DmaEA;

/// <summary>
/// Authoritative P1 Run Context Diagnostics engine:
/// 1) 20-bucket 5% context window occupancy &amp; high-occupancy (&gt;=70%) Context Rot Detector;
/// 2) 10-category ContextPack hierarchy &amp; multi-pack compaction reconciliation;
/// 3) Unified 9 governance redline signals, consecutive normalized-args retry streaks,
///    file diff line deltas (+added / -removed), and redacted error code frames;
/// 4) Machine- and human-readable <c>executive_findings</c>.
/// </summary>
public sealed class RunContextDiagnosticsService : IRunContextDiagnosticsService
{
    private const int BytesPerTokenEstimate = 4;
    private const int MaxTextExcerpt = 320;
    private const int MaxErrorFrames = 24;
    private const int MaxCodeFrameLines = 12;

    private static readonly HashSet<string> CriticalContextSourceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "workspace_instructions",
        "agents_md",
        "apm_instructions",
        "project_instructions",
        "mode_pipeline",
        "prompt_pipeline",
        "structured_session_state",
        "task_context",
        "reviewed_memory",
        "project_core_memory",
        "user_profile_memory",
        "system_prompt",
        "agent_system_prompt"
    };

    private static readonly (string Category, Regex Pattern)[] ExactSourceCategoryRules =
    [
        (ContextSourceCategories.SystemPrompt, new Regex(@"^(system_prompt|agent_system_prompt|base_prompt|accepted_intent|run_steering)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.ModePipeline, new Regex(@"^(mode_pipeline|prompt_pipeline|structured_session_state|task_context|plan-ledger|plan_ledger|run_recorded_facts)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.ProjectInstructions, new Regex(@"^(workspace_instructions|agents_md|apm_instructions|project_instructions)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.ToolSchemas, new Regex(@"^(tool_schemas?|tool_manifest|mcp_servers?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.SkillsCatalog, new Regex(@"^(workspace_skills|skills_catalog|skills_index|skills?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.Memory, new Regex(@"^(reviewed_memory|user_profile_memory|project_core_memory|l2_memory|l3_memory|memory)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.CompactedDigest, new Regex(@"^(session_compaction|compacted_digest|exploration-digest|exploration_digest|organization_reports)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.ConversationTurns, new Regex(@"^(session_history|conversation_turns?|session_attachments|chat_history)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1))),
        (ContextSourceCategories.ToolOutputs, new Regex(@"^(tool|tool_outputs?|tool_results?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)))
    ];

    private static readonly Regex CompactDiffStatsRegex = new(@"\+(\d+)\s*[/, ]\s*-(\d+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex GitInsertionsRegex = new(@"(\d+)\s+insertions?\(\+\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex GitDeletionsRegex = new(@"(\d+)\s+deletions?\(-\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex AddedFieldRegex = new(@"(?:lines_added|added_lines|added)\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex RemovedFieldRegex = new(@"(?:lines_removed|removed_lines|deleted_lines|removed|deleted)\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex FileLineRegex = new(@"((?:[A-Za-z]:[\\/]|[./\\]|\b[\w-]+[/\\])[\w./\\-]+\.[A-Za-z0-9]{1,8})(?::(\d+)(?::(\d+))?|\((\d+),(\d+)\))", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex NumberedCodeLineRegex = new(@"^\s*(?:>|→)?\s*(\d+)\s*[|:]\s?(.*)$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex ErrorHighlightLineRegex = new(@"(error|exception|failed|rejected|conflict|violation|timeout|stale|not_observed)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex WhitespaceCollapseRegex = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex ApprovalRejectedBlobRegex = new(@"\b(approval[_\s-]*(rejected|denied)|user rejected|denied approval)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex FsNotObservedBlobRegex = new(@"\b(fs_not_observed|file[_\s-]*not[_\s-]*observed|must read before edit|unobserved file|read_file before)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex FsStaleVersionBlobRegex = new(@"\b(fs_stale_version|stale[_\s-]*version|line_hash mismatch|hash mismatch|etag mismatch|modified since last read|precondition failed)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex WorktreeGuardBlobRegex = new(@"\b(worktree[_\s-]*guard|outside worktree|worktree isolation|dirty worktree|branch guard)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex WriteScopeLockedBlobRegex = new(@"\b(write_scope_locked|outside write_scope|write_scope violation|scope\.violation|lease\.conflict|lease[_\s-]*conflict|resource[_\s-]*locked)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex TimeoutBlobRegex = new(@"\b(timed?\s*out|deadline exceeded|did not answer within|gave up on .+ after \d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex SameArgsRetryBlobRegex = new(@"\b(same_args_retry|repeated identical arguments)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex LoopDetectedBlobRegex = new(@"\b(loop[_\s-]*detected|loop_guard|tool_round_limit|tool_call_ceiling|too_many_consecutive_errors)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex BearerSecretRegex = new(@"(?i)(bearer\s+)[A-Za-z0-9._\-]+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex TokenPrefixSecretRegex = new(@"(?i)\b(?:sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{8,}|xox[baprs]-[A-Za-z0-9\-]{8,})\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex KeyValueSecretRegex = new(@"(?i)((?:api[_-]?key|token|secret|password|passwd|pwd|authorization)\s*[=:]\s*)([^\s,;""']+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> ToolTimelineEventTypes = new(StringComparer.Ordinal)
    {
        "tool.execution.requested",
        "tool.execution.completed",
        "tool.execution.failed",
        "tool.execution.outcome_unknown",
        "approval.requested",
        "approval.decided",
        "governance.permission_decided"
    };

    private readonly ILifecycleManager _lifecycle;
    private readonly IRunReplayService? _replay;
    private readonly IToolExecutionCoordinator? _toolExecutions;
    private readonly IRunModelInvocationReader? _modelInvocations;
    private readonly ILogger<RunContextDiagnosticsService>? _logger;

    public RunContextDiagnosticsService(
        ILifecycleManager lifecycle,
        IRunReplayService? replay = null,
        IToolExecutionCoordinator? toolExecutions = null,
        IRunModelInvocationReader? modelInvocations = null,
        ILogger<RunContextDiagnosticsService>? logger = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _replay = replay;
        _toolExecutions = toolExecutions;
        _modelInvocations = modelInvocations;
        _logger = logger;
    }

    /// <summary>
    /// Classifies any raw ContextPack evidence source key into one of the 10 canonical
    /// <see cref="ContextSourceCategories"/> values. Matches <c>classifyContextSource</c> 1:1.
    /// </summary>
    public static string ClassifyContextSource(string? source)
    {
        var trimmed = (source ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ContextSourceCategories.Other;
        }

        foreach (var (category, pattern) in ExactSourceCategoryRules)
        {
            if (pattern.IsMatch(trimmed))
            {
                return category;
            }
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("skill", StringComparison.Ordinal)) return ContextSourceCategories.SkillsCatalog;
        if (lower.Contains("memory", StringComparison.Ordinal)) return ContextSourceCategories.Memory;
        if (lower.Contains("compact", StringComparison.Ordinal) || lower.Contains("digest", StringComparison.Ordinal)) return ContextSourceCategories.CompactedDigest;
        if (lower.Contains("instruction", StringComparison.Ordinal) || lower.Contains("agents", StringComparison.Ordinal)) return ContextSourceCategories.ProjectInstructions;
        if (lower.Contains("mcp", StringComparison.Ordinal) || lower.Contains("schema", StringComparison.Ordinal)) return ContextSourceCategories.ToolSchemas;
        if (lower.Contains("history", StringComparison.Ordinal) || lower.Contains("conversation", StringComparison.Ordinal) || lower.Contains("attachment", StringComparison.Ordinal)) return ContextSourceCategories.ConversationTurns;
        if (lower.Contains("tool", StringComparison.Ordinal)) return ContextSourceCategories.ToolOutputs;
        if (lower.Contains("pipeline", StringComparison.Ordinal) || lower.Contains("mode", StringComparison.Ordinal) || lower.Contains("plan", StringComparison.Ordinal)) return ContextSourceCategories.ModePipeline;
        if (lower.Contains("system", StringComparison.Ordinal) || lower.Contains("prompt", StringComparison.Ordinal)) return ContextSourceCategories.SystemPrompt;
        return ContextSourceCategories.Other;
    }

    public async Task<RunContextDiagnosticsDto?> DiagnoseRunAsync(
        Guid runId,
        Guid? tenantId = null,
        Guid? workspaceId = null,
        int? contextWindowTokens = null,
        string? selectedPackId = null,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            return null;
        }

        var runIdStr = runId.ToString();
        RunState runState;
        try
        {
            runState = await _lifecycle.GetRunStateAsync(runIdStr, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        if (runState is null || !Guid.TryParse(runState.SessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return null;
        }

        Guid? parsedTenant = Guid.TryParse(runState.TenantId, out var rt) ? rt : null;
        Guid? parsedWorkspace = Guid.TryParse(runState.WorkspaceId, out var rw) ? rw : null;

        if (tenantId is { } expectedTenant && parsedTenant != expectedTenant)
        {
            return null;
        }

        if (workspaceId is { } expectedWorkspace && parsedWorkspace != expectedWorkspace)
        {
            return null;
        }

        Guid? modeVersionId = null;
        try
        {
            var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runIdStr, cancellationToken).ConfigureAwait(false);
            if (frozen is not null && !string.IsNullOrWhiteSpace(frozen.Content))
            {
                using var doc = JsonDocument.Parse(frozen.Content);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var mvRaw = ReadJsonString(doc.RootElement, "mode_version_id") ?? ReadJsonString(doc.RootElement, "modeVersionId");
                    if (Guid.TryParse(mvRaw, out var mv) && mv != Guid.Empty)
                    {
                        modeVersionId = mv;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "context_diagnostics: failed to parse frozen run configuration for {RunId}", runId);
        }

        var events = (await _lifecycle.ReplayEventsAsync(sessionGuid, 0, cancellationToken).ConfigureAwait(false))
            .Where(e => string.Equals(e.RunId, runIdStr, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Timestamp)
            .ToList();

        var contextPacks = ExtractContextPacks(events, runIdStr);
        var supervisionFindings = ExtractSupervisionFindings(events);
        var lifecycleSignals = ExtractLifecycleSignalCodes(events, runState);

        if (_replay is not null)
        {
            try
            {
                var replay = await _replay.BuildReplayAsync(runIdStr, cancellationToken).ConfigureAwait(false);
                if (replay is not null)
                {
                    foreach (var round in replay.SupervisionRounds)
                    {
                        var summary = round.Reasons.Count > 0 ? string.Join("; ", round.Reasons) : round.Decision;
                        if (!string.IsNullOrWhiteSpace(summary)
                            && !supervisionFindings.Any(f => string.Equals(f.Summary, summary, StringComparison.Ordinal)))
                        {
                            supervisionFindings.Add(new DiagnosticSupervisionFinding(
                                $"replay-round-{round.RevisionRound}",
                                round.Decision,
                                summary,
                                null,
                                DateTimeOffset.UtcNow));
                        }
                    }

                    foreach (var milestone in replay.Milestones)
                    {
                        if (milestone.EventType is "worker.budget_exhausted")
                        {
                            lifecycleSignals.Add(GovernanceSignalCodes.LoopDetected);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "context_diagnostics: replay read failed for {RunId}", runId);
            }
        }

        IReadOnlyList<ToolExecutionSnapshot> snapshotRows = [];
        if (_toolExecutions is not null)
        {
            try
            {
                snapshotRows = await _toolExecutions.ListForRunAsync(runId, limit: 500, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "context_diagnostics: tool execution list failed for {RunId}", runId);
            }
        }

        var toolItems = BuildDiagnosticToolExecutions(events, snapshotRows, sessionGuid.ToString());

        IReadOnlyList<ModelInvocationDto> invocations = [];
        if (_modelInvocations is not null && parsedTenant is { } tId && parsedWorkspace is { } wId)
        {
            try
            {
                invocations = await _modelInvocations.ListForRunAsync(tId, wId, runId, limit: 500, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "context_diagnostics: model invocation read failed for {RunId}", runId);
            }
        }

        return BuildDiagnostics(new RunContextDiagnosticsBuildInput(
            RunId: runIdStr,
            SessionId: sessionGuid.ToString(),
            Status: runState.Status.ToString().ToLowerInvariant(),
            ModeVersionId: modeVersionId,
            ContextPacks: contextPacks,
            ModelInvocations: invocations,
            ToolExecutions: toolItems,
            SupervisionFindings: supervisionFindings,
            LifecycleSignals: lifecycleSignals,
            ContextWindowTokens: contextWindowTokens,
            SelectedPackId: selectedPackId));
    }

    public RunContextDiagnosticsDto BuildDiagnostics(RunContextDiagnosticsBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var packs = (input.ContextPacks ?? [])
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();
        var invocations = (input.ModelInvocations ?? [])
            .OrderBy(i => i.StartedAt)
            .ThenBy(i => i.Attempt)
            .ThenBy(i => i.Id)
            .ToArray();
        var tools = (input.ToolExecutions ?? [])
            .OrderBy(t => t.RequestedSeq)
            .ThenBy(t => t.RequestedAt)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToArray();
        var findings = input.SupervisionFindings ?? [];
        var lifecycleSignals = input.LifecycleSignals ?? [];

        var windowBuckets = BuildWindowBuckets(packs, invocations, tools, input.ContextWindowTokens);
        var compaction = BuildCompactionDiagnostics(packs, input.SelectedPackId);
        var governance = BuildGovernanceDiagnostics(
            input.RunId,
            tools,
            invocations,
            findings,
            lifecycleSignals,
            windowBuckets,
            compaction);
        var executiveFindings = BuildExecutiveFindings(windowBuckets, compaction, governance);

        return new RunContextDiagnosticsDto
        {
            RunId = input.RunId,
            SessionId = input.SessionId,
            Status = string.IsNullOrWhiteSpace(input.Status) ? "running" : input.Status,
            ModeVersionId = input.ModeVersionId,
            GeneratedAt = DateTimeOffset.UtcNow,
            WindowBuckets = windowBuckets,
            Compaction = compaction,
            Governance = governance,
            ExecutiveFindings = executiveFindings
        };
    }

    // ── 1. 5% Context Window Occupancy Buckets & Context Rot Detector ──

    private static ContextWindowBucketReportDto BuildWindowBuckets(
        IReadOnlyList<DiagnosticContextPackSnapshot> sortedPacks,
        IReadOnlyList<ModelInvocationDto> invocations,
        IReadOnlyList<DiagnosticToolExecutionItem> tools,
        int? contextWindowTokens)
    {
        var explicitWindow = contextWindowTokens is > 0 ? contextWindowTokens.Value : (int?)null;
        var samples = new List<ContextWindowSamplePointDto>();

        // Primary source: Model invocations paired with explicit window or nearest ContextPack budget.
        foreach (var inv in invocations)
        {
            var pack = FindNearestPack(sortedPacks, inv.StartedAt);
            var budget = explicitWindow ?? (pack is { TokenBudget: > 0 } ? pack.TokenBudget : 0);
            var used = inv.InputTokens ?? pack?.EstimatedTokens;
            if (budget <= 0 || used is null or < 0)
            {
                continue;
            }

            var occupancyPct = Math.Clamp((used.Value / (double)budget) * 100.0, 0.0, 100.0);
            var bucketIndex = Math.Clamp((int)Math.Floor(occupancyPct / 5.0), 0, 19);
            var latencyMs = DurationBetweenMs(inv.StartedAt, inv.CompletedAt) ?? 0L;
            var isError = IsErrorStatus(inv.Status)
                || !string.IsNullOrWhiteSpace(inv.ErrorCategory)
                || !string.IsNullOrWhiteSpace(inv.SafeErrorMessage);

            samples.Add(new ContextWindowSamplePointDto
            {
                Id = $"inv:{inv.Id}",
                SourceKind = "invocation",
                OccupancyPct = JsRound(occupancyPct * 10.0) / 10.0,
                BucketIndex = bucketIndex,
                IsError = isError,
                LatencyMs = latencyMs,
                TokensUsed = used.Value,
                TokenBudget = budget,
                Timestamp = inv.StartedAt
            });
        }

        // Secondary source when model invocations are absent or unpriced: pair tool executions with nearest ContextPack.
        if (samples.Count == 0 && sortedPacks.Count > 0 && tools.Count > 0)
        {
            foreach (var exec in tools)
            {
                var pack = FindNearestPack(sortedPacks, exec.RequestedAt);
                if (pack is null || pack.TokenBudget <= 0)
                {
                    continue;
                }

                var occupancyPct = Math.Clamp((pack.EstimatedTokens / (double)pack.TokenBudget) * 100.0, 0.0, 100.0);
                var bucketIndex = Math.Clamp((int)Math.Floor(occupancyPct / 5.0), 0, 19);
                var isError = IsErrorStatus(exec.Status)
                    || exec.ToolSuccess == false
                    || !string.IsNullOrWhiteSpace(exec.ErrorCategory);

                samples.Add(new ContextWindowSamplePointDto
                {
                    Id = $"tool:{exec.Id}",
                    SourceKind = "tool",
                    OccupancyPct = JsRound(occupancyPct * 10.0) / 10.0,
                    BucketIndex = bucketIndex,
                    IsError = isError,
                    LatencyMs = Math.Max(0L, exec.DurationMs),
                    TokensUsed = pack.EstimatedTokens,
                    TokenBudget = pack.TokenBudget,
                    Timestamp = exec.RequestedAt
                });
            }
        }

        // Tertiary fallback when only context packs exist.
        if (samples.Count == 0 && sortedPacks.Count > 0)
        {
            foreach (var pack in sortedPacks)
            {
                if (pack.TokenBudget <= 0)
                {
                    continue;
                }

                var occupancyPct = Math.Clamp((pack.EstimatedTokens / (double)pack.TokenBudget) * 100.0, 0.0, 100.0);
                var bucketIndex = Math.Clamp((int)Math.Floor(occupancyPct / 5.0), 0, 19);
                var hasDrops = pack.DroppedSources.Count > 0;

                samples.Add(new ContextWindowSamplePointDto
                {
                    Id = $"pack:{pack.Id}",
                    SourceKind = "context_pack",
                    OccupancyPct = JsRound(occupancyPct * 10.0) / 10.0,
                    BucketIndex = bucketIndex,
                    IsError = hasDrops,
                    LatencyMs = 0L,
                    TokensUsed = pack.EstimatedTokens,
                    TokenBudget = pack.TokenBudget,
                    Timestamp = pack.CreatedAt
                });
            }
        }

        var sampleCounts = new int[20];
        var errorCounts = new int[20];
        var maxLatencies = new long[20];
        var latencySums = new double[20];
        var occupancySums = new double[20];

        var totalErrors = 0;
        var totalLatency = 0.0;
        var peakOccupancyPct = 0.0;

        var lowCount = 0;
        var lowErrors = 0;
        var lowLatency = 0.0;
        var highCount = 0;
        var highErrors = 0;
        var highLatency = 0.0;

        foreach (var s in samples)
        {
            var idx = s.BucketIndex;
            sampleCounts[idx]++;
            if (s.IsError)
            {
                errorCounts[idx]++;
                totalErrors++;
            }

            latencySums[idx] += s.LatencyMs;
            occupancySums[idx] += s.OccupancyPct;
            if (s.LatencyMs > maxLatencies[idx])
            {
                maxLatencies[idx] = s.LatencyMs;
            }

            totalLatency += s.LatencyMs;
            if (s.OccupancyPct > peakOccupancyPct)
            {
                peakOccupancyPct = s.OccupancyPct;
            }

            if (s.OccupancyPct >= 70.0)
            {
                highCount++;
                if (s.IsError) highErrors++;
                highLatency += s.LatencyMs;
            }
            else
            {
                lowCount++;
                if (s.IsError) lowErrors++;
                lowLatency += s.LatencyMs;
            }
        }

        var buckets = new ContextWindowBucketDto[20];
        for (var i = 0; i < 20; i++)
        {
            var rangeStart = i * 5;
            var rangeEnd = (i + 1) * 5;
            var count = sampleCounts[i];
            buckets[i] = new ContextWindowBucketDto
            {
                Index = i,
                RangeStart = rangeStart,
                RangeEnd = rangeEnd,
                Label = $"{rangeStart}-{rangeEnd}%",
                SampleCount = count,
                ErrorCount = errorCounts[i],
                ErrorRate = count > 0 ? JsRound((errorCounts[i] / (double)count) * 1000.0) / 1000.0 : 0.0,
                AvgLatencyMs = count > 0 ? (long)JsRound(latencySums[i] / count) : 0L,
                MaxLatencyMs = maxLatencies[i],
                AvgOccupancyPct = count > 0 ? JsRound((occupancySums[i] / count) * 10.0) / 10.0 : 0.0
            };
        }

        var totalSamples = samples.Count;
        var overallErrorRate = totalSamples > 0 ? JsRound((totalErrors / (double)totalSamples) * 1000.0) / 1000.0 : 0.0;
        var avgLatencyMs = totalSamples > 0 ? (long)JsRound(totalLatency / totalSamples) : 0L;
        var lowOccupancyErrorRate = lowCount > 0 ? JsRound((lowErrors / (double)lowCount) * 1000.0) / 1000.0 : 0.0;
        var highOccupancyErrorRate = highCount > 0 ? JsRound((highErrors / (double)highCount) * 1000.0) / 1000.0 : 0.0;
        var lowOccupancyAvgLatencyMs = lowCount > 0 ? (long)JsRound(lowLatency / lowCount) : 0L;
        var highOccupancyAvgLatencyMs = highCount > 0 ? (long)JsRound(highLatency / highCount) : 0L;

        var reasonCodes = new List<string>();
        if (highCount > 0 && highOccupancyErrorRate > lowOccupancyErrorRate + 0.1)
        {
            reasonCodes.Add("high_occupancy_error_spike");
        }

        if (highCount > 0 && lowOccupancyAvgLatencyMs > 0 && highOccupancyAvgLatencyMs >= lowOccupancyAvgLatencyMs * 1.5)
        {
            reasonCodes.Add("high_occupancy_latency_spike");
        }

        return new ContextWindowBucketReportDto
        {
            Buckets = buckets,
            Samples = samples,
            TotalSamples = totalSamples,
            TotalErrors = totalErrors,
            OverallErrorRate = overallErrorRate,
            AvgLatencyMs = avgLatencyMs,
            PeakOccupancyPct = JsRound(peakOccupancyPct * 10.0) / 10.0,
            LowOccupancyErrorRate = lowOccupancyErrorRate,
            HighOccupancyErrorRate = highOccupancyErrorRate,
            LowOccupancyAvgLatencyMs = lowOccupancyAvgLatencyMs,
            HighOccupancyAvgLatencyMs = highOccupancyAvgLatencyMs,
            DegradationDetected = reasonCodes.Count > 0,
            DegradationReasonCodes = reasonCodes
        };
    }

    private static DiagnosticContextPackSnapshot? FindNearestPack(
        IReadOnlyList<DiagnosticContextPackSnapshot> sortedPacks,
        DateTimeOffset timestamp)
    {
        if (sortedPacks.Count == 0)
        {
            return null;
        }

        DiagnosticContextPackSnapshot? best = null;
        foreach (var pack in sortedPacks)
        {
            if (pack.CreatedAt <= timestamp || best is null)
            {
                best = pack;
            }
            else
            {
                break;
            }
        }

        return best ?? sortedPacks[^1];
    }

    // ── 2. Authoritative Context Source Classification & Compaction Reconciliation ──

    private static ContextCompactionDiagnosticsDto BuildCompactionDiagnostics(
        IReadOnlyList<DiagnosticContextPackSnapshot> sortedPacks,
        string? selectedPackId)
    {
        if (sortedPacks.Count == 0)
        {
            return new ContextCompactionDiagnosticsDto
            {
                SelectedPackId = null,
                PackCount = 0,
                TotalDroppedTokensAcrossPacks = 0,
                CriticalDroppedSources = [],
                Root = new ContextCategoryNodeDto
                {
                    Id = "root",
                    Label = "Context Window",
                    Category = ContextSourceCategories.Other,
                    Depth = 0
                },
                Reconciliation = new CompactionReconciliationDto(),
                Packs = []
            };
        }

        var packReconciliations = new List<CompactionReconciliationDto>(sortedPacks.Count);
        var totalDroppedAcrossPacks = 0;
        var criticalDropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sortedPacks.Count; i++)
        {
            var p = sortedPacks[i];
            var prev = i > 0 ? sortedPacks[i - 1] : null;
            var (_, rec) = BuildSinglePackTreeAndReconciliation(p, prev);
            packReconciliations.Add(rec);
            totalDroppedAcrossPacks += rec.DroppedTokens;

            foreach (var drop in p.DroppedSources)
            {
                if (drop.Tokens <= 0 || string.IsNullOrWhiteSpace(drop.Source)) continue;
                var cat = ClassifyContextSource(drop.Source);
                if (CriticalContextSourceKeys.Contains(drop.Source)
                    || cat is ContextSourceCategories.ProjectInstructions
                        or ContextSourceCategories.ModePipeline
                        or ContextSourceCategories.Memory
                        or ContextSourceCategories.SystemPrompt)
                {
                    criticalDropped.Add(drop.Source);
                }
            }
        }

        var activeIdx = !string.IsNullOrWhiteSpace(selectedPackId)
            ? FindPackIndex(sortedPacks, selectedPackId)
            : sortedPacks.Count - 1;
        if (activeIdx < 0)
        {
            activeIdx = sortedPacks.Count - 1;
        }

        var activePack = sortedPacks[activeIdx];
        var prevPack = activeIdx > 0 ? sortedPacks[activeIdx - 1] : null;
        var (root, activeReconciliation) = BuildSinglePackTreeAndReconciliation(activePack, prevPack);

        return new ContextCompactionDiagnosticsDto
        {
            SelectedPackId = activePack.Id,
            PackCount = sortedPacks.Count,
            TotalDroppedTokensAcrossPacks = totalDroppedAcrossPacks,
            CriticalDroppedSources = criticalDropped.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            Root = root,
            Reconciliation = activeReconciliation,
            Packs = packReconciliations
        };
    }

    private static int FindPackIndex(IReadOnlyList<DiagnosticContextPackSnapshot> packs, string id)
    {
        for (var i = 0; i < packs.Count; i++)
        {
            if (string.Equals(packs[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static (ContextCategoryNodeDto Root, CompactionReconciliationDto Reconciliation) BuildSinglePackTreeAndReconciliation(
        DiagnosticContextPackSnapshot pack,
        DiagnosticContextPackSnapshot? prevPack)
    {
        var (retained, dropped, hasPricingData) = NormalizePackShares(pack);
        var tokenBudget = Math.Max(0, pack.TokenBudget);

        var categoryMap = new Dictionary<string, Dictionary<string, List<ContextEvidenceShareItemDto>>>(StringComparer.Ordinal);
        var itemSeq = 0;

        void PushItem(DiagnosticContextBudgetShare share, bool isDropped)
        {
            var tokens = Math.Max(0, share.Tokens);
            var category = ClassifyContextSource(share.Source);
            if (!categoryMap.TryGetValue(category, out var bySource))
            {
                bySource = new Dictionary<string, List<ContextEvidenceShareItemDto>>(StringComparer.Ordinal);
                categoryMap[category] = bySource;
            }

            if (!bySource.TryGetValue(share.Source, out var list))
            {
                list = [];
                bySource[share.Source] = list;
            }

            list.Add(new ContextEvidenceShareItemDto
            {
                Id = $"{category}:{share.Source}:{(isDropped ? "drop" : "keep")}:{++itemSeq}",
                Source = share.Source,
                Tokens = tokens,
                BytesEstimate = tokens * BytesPerTokenEstimate,
                Dropped = isDropped
            });
        }

        foreach (var s in retained) PushItem(s, false);
        foreach (var s in dropped) PushItem(s, true);

        var summedRetained = retained.Sum(s => Math.Max(0, s.Tokens));
        var totalRetained = summedRetained > 0 ? summedRetained : Math.Max(0, pack.EstimatedTokens);
        var totalDropped = dropped.Sum(s => Math.Max(0, s.Tokens));
        var preCompactionTokens = totalRetained + totalDropped;

        static double Pct(int part, int whole) =>
            whole > 0 ? Math.Min(100.0, JsRound((part / (double)whole) * 1000.0) / 10.0) : 0.0;

        var categoryChildren = new List<ContextCategoryNodeDto>();
        foreach (var (category, bySource) in categoryMap)
        {
            var sourceChildren = new List<ContextCategoryNodeDto>();
            var catRetained = 0;
            var catDropped = 0;
            var catItemCount = 0;
            var catDroppedCount = 0;
            var catItems = new List<ContextEvidenceShareItemDto>();

            foreach (var (sourceKey, items) in bySource)
            {
                var keptItems = items.Where(i => !i.Dropped).ToList();
                var dropItems = items.Where(i => i.Dropped).ToList();
                var srcRetained = keptItems.Sum(i => i.Tokens);
                var srcDropped = dropItems.Sum(i => i.Tokens);

                catRetained += srcRetained;
                catDropped += srcDropped;
                catItemCount += keptItems.Count;
                catDroppedCount += dropItems.Count;
                catItems.AddRange(items);

                var leafChildren = keptItems.Count > 1
                    ? keptItems.Select((item, idx) => new ContextCategoryNodeDto
                    {
                        Id = $"item:{category}:{sourceKey}:{idx + 1}",
                        Label = $"{sourceKey} #{idx + 1}",
                        Category = category,
                        SourceKey = sourceKey,
                        Depth = 3,
                        RetainedTokens = item.Tokens,
                        DroppedTokens = 0,
                        RetainedBytesEstimate = item.BytesEstimate,
                        DroppedBytesEstimate = 0,
                        SharePercent = Pct(item.Tokens, totalRetained),
                        BudgetSharePercent = Pct(item.Tokens, tokenBudget),
                        ItemCount = 1,
                        DroppedItemCount = 0,
                        Items = [item],
                        Children = []
                    }).ToArray()
                    : Array.Empty<ContextCategoryNodeDto>();

                sourceChildren.Add(new ContextCategoryNodeDto
                {
                    Id = $"src:{category}:{sourceKey}",
                    Label = sourceKey,
                    Category = category,
                    SourceKey = sourceKey,
                    Depth = 2,
                    RetainedTokens = srcRetained,
                    DroppedTokens = srcDropped,
                    RetainedBytesEstimate = srcRetained * BytesPerTokenEstimate,
                    DroppedBytesEstimate = srcDropped * BytesPerTokenEstimate,
                    SharePercent = Pct(srcRetained, totalRetained),
                    BudgetSharePercent = Pct(srcRetained, tokenBudget),
                    ItemCount = keptItems.Count,
                    DroppedItemCount = dropItems.Count,
                    Items = items,
                    Children = leafChildren
                });
            }

            sourceChildren.Sort(static (a, b) =>
            {
                var cmp = b.RetainedTokens.CompareTo(a.RetainedTokens);
                if (cmp != 0) return cmp;
                cmp = b.DroppedTokens.CompareTo(a.DroppedTokens);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
            });

            var catNodeChildren = sourceChildren.Count == 1 && sourceChildren[0].Children.Count > 0
                ? sourceChildren[0].Children
                : sourceChildren;

            categoryChildren.Add(new ContextCategoryNodeDto
            {
                Id = $"cat:{category}",
                Label = ContextSourceCategories.DefaultLabel(category),
                Category = category,
                SourceKey = sourceChildren.Count == 1 ? sourceChildren[0].SourceKey : null,
                Depth = 1,
                RetainedTokens = catRetained,
                DroppedTokens = catDropped,
                RetainedBytesEstimate = catRetained * BytesPerTokenEstimate,
                DroppedBytesEstimate = catDropped * BytesPerTokenEstimate,
                SharePercent = Pct(catRetained, totalRetained),
                BudgetSharePercent = Pct(catRetained, tokenBudget),
                ItemCount = catItemCount,
                DroppedItemCount = catDroppedCount,
                Items = catItems,
                Children = catNodeChildren
            });
        }

        categoryChildren.Sort(static (a, b) =>
        {
            var cmp = b.RetainedTokens.CompareTo(a.RetainedTokens);
            if (cmp != 0) return cmp;
            cmp = b.DroppedTokens.CompareTo(a.DroppedTokens);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
        });

        var root = new ContextCategoryNodeDto
        {
            Id = "root",
            Label = !string.IsNullOrWhiteSpace(pack.LaneKey) ? $"Context ({pack.LaneKey})" : "Context (Planner)",
            Category = ContextSourceCategories.Other,
            Depth = 0,
            RetainedTokens = totalRetained,
            DroppedTokens = totalDropped,
            RetainedBytesEstimate = totalRetained * BytesPerTokenEstimate,
            DroppedBytesEstimate = totalDropped * BytesPerTokenEstimate,
            SharePercent = 100.0,
            BudgetSharePercent = Pct(totalRetained, tokenBudget),
            ItemCount = retained.Count,
            DroppedItemCount = dropped.Count,
            Items = categoryChildren.SelectMany(c => c.Items).ToArray(),
            Children = categoryChildren
        };

        int? prevRetained = null;
        int? prevDropped = null;
        if (prevPack is not null)
        {
            var (pRetainedShares, pDroppedShares, _) = NormalizePackShares(prevPack);
            var pSumRetained = pRetainedShares.Sum(s => Math.Max(0, s.Tokens));
            prevRetained = pSumRetained > 0 ? pSumRetained : Math.Max(0, prevPack.EstimatedTokens);
            prevDropped = pDroppedShares.Sum(s => Math.Max(0, s.Tokens));
        }

        var reconciliation = new CompactionReconciliationDto
        {
            PackId = pack.Id,
            LaneKey = pack.LaneKey,
            CreatedAt = pack.CreatedAt,
            TokenBudget = tokenBudget,
            RetainedTokens = totalRetained,
            DroppedTokens = totalDropped,
            PreCompactionTokens = preCompactionTokens,
            RetainedBytesEstimate = totalRetained * BytesPerTokenEstimate,
            DroppedBytesEstimate = totalDropped * BytesPerTokenEstimate,
            UtilizationPercent = Pct(totalRetained, tokenBudget),
            RetainedRatioPercent = Pct(totalRetained, preCompactionTokens),
            DroppedRatioPercent = Pct(totalDropped, preCompactionTokens),
            HasPricingData = hasPricingData,
            HasCompactionDigest = categoryMap.ContainsKey(ContextSourceCategories.CompactedDigest),
            PreviousPackRetainedDelta = prevRetained.HasValue ? totalRetained - prevRetained.Value : null,
            PreviousPackDroppedDelta = prevDropped.HasValue ? totalDropped - prevDropped.Value : null
        };

        return (root, reconciliation);
    }

    private static (
        IReadOnlyList<DiagnosticContextBudgetShare> Retained,
        IReadOnlyList<DiagnosticContextBudgetShare> Dropped,
        bool HasPricingData)
        NormalizePackShares(DiagnosticContextPackSnapshot pack)
    {
        var validDropped = pack.DroppedSources
            .Where(s => !string.IsNullOrWhiteSpace(s.Source))
            .ToArray();

        if (pack.SourceTokens.Count > 0)
        {
            var validRetained = pack.SourceTokens
                .Where(s => !string.IsNullOrWhiteSpace(s.Source))
                .ToArray();
            if (validRetained.Length > 0)
            {
                return (validRetained, validDropped, true);
            }
        }

        var rawSources = pack.Sources.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (rawSources.Length == 0)
        {
            return ([], validDropped, false);
        }

        var baseTokens = pack.EstimatedTokens > 0 ? pack.EstimatedTokens : rawSources.Length;
        var perItem = Math.Max(1, (int)JsRound(baseTokens / (double)rawSources.Length));
        return (
            rawSources.Select(s => new DiagnosticContextBudgetShare(s, perItem)).ToArray(),
            validDropped,
            false);
    }

    // ── 3. Unified 9 Governance Signals, Retry Streaks, Diff Stats & Error Frames ──

    private static RunGovernanceDiagnosticsDto BuildGovernanceDiagnostics(
        string runId,
        IReadOnlyList<DiagnosticToolExecutionItem> tools,
        IReadOnlyList<ModelInvocationDto> invocations,
        IReadOnlyList<DiagnosticSupervisionFinding> findings,
        IReadOnlyList<string> lifecycleSignals,
        ContextWindowBucketReportDto windowBuckets,
        ContextCompactionDiagnosticsDto compaction)
    {
        var signalAccumulators = new Dictionary<string, SignalAccumulator>(StringComparer.Ordinal);
        foreach (var code in GovernanceSignalCodes.Ordered)
        {
            signalAccumulators[code] = new SignalAccumulator(code, GovernanceSignalCodes.DefaultSeverity(code));
        }

        void RecordSignal(string code, string? detail = null, string? subjectId = null, int increment = 1, int? retryStreak = null)
        {
            if (!signalAccumulators.TryGetValue(code, out var acc))
            {
                return;
            }

            acc.Count += Math.Max(1, increment);
            if (string.IsNullOrWhiteSpace(acc.Detail) && !string.IsNullOrWhiteSpace(detail))
            {
                acc.Detail = Clip(RedactSensitiveText(detail), MaxTextExcerpt);
            }

            if (!string.IsNullOrWhiteSpace(subjectId) && acc.SubjectIds.Count < 16 && !acc.SubjectIds.Contains(subjectId, StringComparer.Ordinal))
            {
                acc.SubjectIds.Add(subjectId);
            }

            if (retryStreak.HasValue && (!acc.MaxRetryStreak.HasValue || retryStreak.Value > acc.MaxRetryStreak.Value))
            {
                acc.MaxRetryStreak = retryStreak.Value;
            }
        }

        var retryStreakMap = DetectRepeatedArgsStreaks(tools, runId);
        var retryStreakDtos = new List<ToolRetryStreakDto>();
        var maxSameArgsStreak = 0;

        var fileEdits = new List<DiagnosticFileEditStatsDto>();
        var totalAddedLines = 0;
        var totalRemovedLines = 0;

        var errorFrames = new List<DiagnosticCodeFrameDto>();
        var errorCount = 0;

        foreach (var exec in tools)
        {
            var execIsError = IsErrorStatus(exec.Status)
                || exec.ToolSuccess == false
                || !string.IsNullOrWhiteSpace(exec.ErrorCategory);
            if (execIsError)
            {
                errorCount++;
            }

            var detected = DetectToolGovernanceSignals(exec);
            var addedSameArgsFromTool = detected.Any(d => d.Code == GovernanceSignalCodes.SameArgsRetry);
            foreach (var sig in detected)
            {
                RecordSignal(sig.Code, sig.Detail, exec.Id);
            }

            if (retryStreakMap.TryGetValue(exec.Id, out var streakInfo) && streakInfo.Streak >= 2)
            {
                if (streakInfo.Streak > maxSameArgsStreak)
                {
                    maxSameArgsStreak = streakInfo.Streak;
                }

                retryStreakDtos.Add(new ToolRetryStreakDto
                {
                    ExecutionId = exec.Id,
                    ToolId = exec.ToolId,
                    ScopeKey = streakInfo.ScopeKey,
                    StreakCount = streakInfo.Streak,
                    ArgumentsSummary = Clip(RedactSensitiveText(exec.ArgumentsSummary ?? exec.Command ?? exec.ResourcePath), MaxTextExcerpt)
                });

                if (!addedSameArgsFromTool)
                {
                    RecordSignal(
                        GovernanceSignalCodes.SameArgsRetry,
                        $"{exec.ToolId} repeated {streakInfo.Streak}x with identical args",
                        exec.Id,
                        increment: 1,
                        retryStreak: streakInfo.Streak);
                }
                else if (signalAccumulators.TryGetValue(GovernanceSignalCodes.SameArgsRetry, out var existingAcc))
                {
                    existingAcc.MaxRetryStreak = Math.Max(existingAcc.MaxRetryStreak ?? 0, streakInfo.Streak);
                }

                if (streakInfo.Streak >= 4 && detected.All(d => d.Code != GovernanceSignalCodes.LoopDetected))
                {
                    RecordSignal(
                        GovernanceSignalCodes.LoopDetected,
                        $"{exec.ToolId} exceeded retry loop threshold ({streakInfo.Streak}x identical calls)",
                        exec.Id,
                        increment: 1,
                        retryStreak: streakInfo.Streak);
                }
            }

            if (ExtractFileEditStats(exec) is { } editStat)
            {
                totalAddedLines += editStat.Added;
                totalRemovedLines += editStat.Removed;
                fileEdits.Add(editStat);
            }

            if (errorFrames.Count < MaxErrorFrames)
            {
                foreach (var frame in ExtractErrorCodeFrames(exec))
                {
                    if (errorFrames.Count < MaxErrorFrames)
                    {
                        errorFrames.Add(frame);
                    }
                }
            }
        }

        foreach (var inv in invocations)
        {
            var invIsError = IsErrorStatus(inv.Status)
                || !string.IsNullOrWhiteSpace(inv.ErrorCategory)
                || !string.IsNullOrWhiteSpace(inv.SafeErrorMessage);
            if (invIsError)
            {
                errorCount++;
            }

            var errLower = $"{inv.ErrorCategory ?? string.Empty} {inv.SafeErrorMessage ?? string.Empty}".ToLowerInvariant();
            if (errLower.Contains("timeout", StringComparison.Ordinal) || errLower.Contains("timed out", StringComparison.Ordinal))
            {
                RecordSignal(GovernanceSignalCodes.Timeout, inv.SafeErrorMessage ?? inv.ErrorCategory, inv.Id.ToString());
            }

            if (errLower.Contains("context_length", StringComparison.Ordinal)
                || errLower.Contains("context_overflow", StringComparison.Ordinal)
                || errLower.Contains("maximum context length", StringComparison.Ordinal))
            {
                RecordSignal(GovernanceSignalCodes.ContextPressure, inv.SafeErrorMessage ?? inv.ErrorCategory, inv.Id.ToString());
            }

            if (invIsError && errorFrames.Count < MaxErrorFrames)
            {
                errorFrames.Add(new DiagnosticCodeFrameDto
                {
                    Id = $"frame:model:{inv.Id}",
                    SourceId = inv.Id.ToString(),
                    Title = !string.IsNullOrWhiteSpace(inv.Model) ? $"{inv.Model} ({inv.Protocol})" : inv.CallId.ToString(),
                    ErrorCategory = inv.ErrorCategory ?? inv.Status,
                    Lines =
                    [
                        new DiagnosticCodeFrameLineDto
                        {
                            Text = Clip(RedactSensitiveText(inv.SafeErrorMessage ?? inv.ErrorCategory ?? $"Model invocation failed ({inv.Status})"), MaxTextExcerpt) ?? "Model invocation failed",
                            Highlight = true
                        }
                    ]
                });
            }
        }

        foreach (var finding in findings)
        {
            var text = $"{finding.Category ?? string.Empty} {finding.Summary ?? string.Empty} {finding.Recommendation ?? string.Empty}".ToLowerInvariant();
            var detail = finding.Summary ?? finding.Category;
            if (text.Contains("write_scope", StringComparison.Ordinal) || text.Contains("scope violation", StringComparison.Ordinal) || text.Contains("lease", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.WriteScopeLocked, detail, finding.Id);
            if (text.Contains("worktree", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.WorktreeGuard, detail, finding.Id);
            if (text.Contains("approval", StringComparison.Ordinal) && (text.Contains("reject", StringComparison.Ordinal) || text.Contains("denied", StringComparison.Ordinal)))
                RecordSignal(GovernanceSignalCodes.ApprovalRejected, detail, finding.Id);
            if (text.Contains("timeout", StringComparison.Ordinal) || text.Contains("timed out", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.Timeout, detail, finding.Id);
            if (text.Contains("stale", StringComparison.Ordinal) && text.Contains("version", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.FsStaleVersion, detail, finding.Id);
            if (text.Contains("not_observed", StringComparison.Ordinal) || text.Contains("not observed", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.FsNotObserved, detail, finding.Id);
            if (text.Contains("same_args", StringComparison.Ordinal) || text.Contains("retry loop", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.SameArgsRetry, detail, finding.Id);
            if (text.Contains("loop", StringComparison.Ordinal) || text.Contains("budget_exhausted", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.LoopDetected, detail, finding.Id);
            if (text.Contains("context_pressure", StringComparison.Ordinal) || text.Contains("context pressure", StringComparison.Ordinal))
                RecordSignal(GovernanceSignalCodes.ContextPressure, detail, finding.Id);
        }

        foreach (var code in lifecycleSignals)
        {
            if (GovernanceSignalCodes.All.Contains(code))
            {
                RecordSignal(code, $"Observed lifecycle governance signal {code}", runId);
            }
        }

        if (windowBuckets.PeakOccupancyPct >= 75.0
            || windowBuckets.DegradationDetected
            || compaction.CriticalDroppedSources.Count > 0)
        {
            var pressureReason = windowBuckets.DegradationDetected
                ? $"High-occupancy degradation detected at peak {windowBuckets.PeakOccupancyPct:0.#}%"
                : compaction.CriticalDroppedSources.Count > 0
                    ? $"Critical context sources dropped: {string.Join(", ", compaction.CriticalDroppedSources)}"
                    : $"Peak context window occupancy reached {windowBuckets.PeakOccupancyPct:0.#}%";
            if (signalAccumulators[GovernanceSignalCodes.ContextPressure].Count == 0)
            {
                RecordSignal(GovernanceSignalCodes.ContextPressure, pressureReason, runId);
            }
        }

        var signalCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var activeSignals = new List<string>();
        var signalList = new List<UnifiedGovernanceSignalDto>();

        foreach (var code in GovernanceSignalCodes.Ordered)
        {
            var acc = signalAccumulators[code];
            signalCounts[code] = acc.Count;
            if (acc.Count > 0)
            {
                activeSignals.Add(code);
                signalList.Add(new UnifiedGovernanceSignalDto
                {
                    Code = acc.Code,
                    Severity = acc.Severity,
                    Count = acc.Count,
                    MaxRetryStreak = acc.MaxRetryStreak,
                    Detail = acc.Detail,
                    SubjectIds = acc.SubjectIds
                });
            }
        }

        return new RunGovernanceDiagnosticsDto
        {
            SignalCounts = signalCounts,
            ActiveSignals = activeSignals,
            Signals = signalList,
            MaxSameArgsRetryStreak = maxSameArgsStreak,
            RetryStreaks = retryStreakDtos,
            TotalToolCalls = tools.Count,
            TotalModelCalls = invocations.Count,
            ErrorCount = errorCount,
            TotalAddedLines = totalAddedLines,
            TotalRemovedLines = totalRemovedLines,
            FileEdits = fileEdits,
            ErrorFrames = errorFrames
        };
    }

    public static IReadOnlyList<(string Code, string? Detail)> DetectToolGovernanceSignals(DiagnosticToolExecutionItem exec)
    {
        var status = (exec.Status ?? string.Empty).Trim().ToLowerInvariant();
        var errCat = (exec.ErrorCategory ?? string.Empty).Trim().ToLowerInvariant();
        var blob = string.Join(
            ' ',
            new[]
            {
                status,
                errCat,
                exec.Summary ?? string.Empty,
                exec.CheckpointSummary ?? string.Empty,
                string.Join(' ', exec.EventTypes),
                string.Join(' ', exec.Evidence)
            });

        var found = new List<(string Code, string? Detail)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string code, string? detail)
        {
            if (seen.Add(code))
            {
                found.Add((code, string.IsNullOrWhiteSpace(detail) ? null : detail));
            }
        }

        if (status is "rejected" or "denied" or "approval_rejected" or "not_approved"
            || errCat.Contains("approval_rejected", StringComparison.Ordinal)
            || errCat.Contains("approval_denied", StringComparison.Ordinal)
            || ApprovalRejectedBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.ApprovalRejected, FirstNonEmpty(exec.Summary, exec.ErrorCategory));
        }

        if (errCat.Contains("fs_not_observed", StringComparison.Ordinal)
            || errCat.Contains("not_observed", StringComparison.Ordinal)
            || FsNotObservedBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.FsNotObserved, FirstNonEmpty(exec.ResourcePath, exec.Summary));
        }

        if (errCat.Contains("fs_stale_version", StringComparison.Ordinal)
            || errCat.Contains("stale_version", StringComparison.Ordinal)
            || errCat.Contains("version_conflict", StringComparison.Ordinal)
            || FsStaleVersionBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.FsStaleVersion, FirstNonEmpty(exec.ResourcePath, exec.Summary));
        }

        if (errCat.Contains("worktree_guard", StringComparison.Ordinal)
            || errCat.Contains("worktree", StringComparison.Ordinal)
            || WorktreeGuardBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.WorktreeGuard, FirstNonEmpty(exec.ResourcePath, exec.Cwd, exec.Summary));
        }

        if (errCat.Contains("write_scope_locked", StringComparison.Ordinal)
            || errCat.Contains("write_scope", StringComparison.Ordinal)
            || errCat.Contains("scope_violation", StringComparison.Ordinal)
            || errCat.Contains("lease_conflict", StringComparison.Ordinal)
            || WriteScopeLockedBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.WriteScopeLocked, FirstNonEmpty(exec.ResourcePath, exec.Summary));
        }

        if (status is "timeout" or "timed_out"
            || errCat.Contains("timeout", StringComparison.Ordinal)
            || TimeoutBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.Timeout, FirstNonEmpty(exec.Summary, $"{exec.DurationMs}ms"));
        }

        if (errCat.Contains("same_args_retry", StringComparison.Ordinal)
            || errCat.Contains("duplicate_call", StringComparison.Ordinal)
            || SameArgsRetryBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.SameArgsRetry, FirstNonEmpty(exec.ArgumentsSummary, exec.Command));
        }

        if (errCat.Contains("loop", StringComparison.Ordinal)
            || errCat.Contains("tool_round_limit", StringComparison.Ordinal)
            || errCat.Contains("tool_call_ceiling", StringComparison.Ordinal)
            || LoopDetectedBlobRegex.IsMatch(blob))
        {
            Add(GovernanceSignalCodes.LoopDetected, FirstNonEmpty(exec.Summary, exec.ErrorCategory));
        }

        return found;
    }

    public static IReadOnlyDictionary<string, (string ScopeKey, int Streak)> DetectRepeatedArgsStreaks(
        IReadOnlyList<DiagnosticToolExecutionItem> toolExecutions,
        string? defaultRunScope = null)
    {
        var streaks = new Dictionary<string, (string ScopeKey, int Streak)>(StringComparer.Ordinal);
        var sorted = toolExecutions
            .OrderBy(a => a.RequestedSeq)
            .ThenBy(a => a.RequestedAt)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToArray();

        var stateByScope = new Dictionary<string, (string Sig, int Count)>(StringComparer.Ordinal);

        foreach (var exec in sorted)
        {
            var scope = FirstNonEmpty(exec.AgentInstanceId, exec.TaskId, exec.RunId, defaultRunScope) ?? "default";
            var sig = CanonicalToolArgSignature(exec);
            if (sig is null)
            {
                stateByScope.Remove(scope);
                continue;
            }

            if (stateByScope.TryGetValue(scope, out var prev) && string.Equals(prev.Sig, sig, StringComparison.Ordinal))
            {
                var nextCount = prev.Count + 1;
                stateByScope[scope] = (sig, nextCount);
                streaks[exec.Id] = (scope, nextCount);
            }
            else
            {
                stateByScope[scope] = (sig, 1);
            }
        }

        return streaks;
    }

    private static string? CanonicalToolArgSignature(DiagnosticToolExecutionItem exec)
    {
        if (string.IsNullOrWhiteSpace(exec.ToolId))
        {
            return null;
        }

        var parts = new[]
        {
            exec.ArgumentsSummary,
            exec.Command,
            exec.ResourcePath,
            exec.Query
        }
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => WhitespaceCollapseRegex.Replace(s!.Trim(), " ").ToLowerInvariant())
        .ToArray();

        if (parts.Length == 0)
        {
            return null;
        }

        return $"{exec.ToolId.Trim().ToLowerInvariant()}::{string.Join('|', parts)}";
    }

    public static DiagnosticFileEditStatsDto? ExtractFileEditStats(DiagnosticToolExecutionItem exec)
    {
        var haystack = string.Join(
            '\n',
            new[]
            {
                exec.Summary ?? string.Empty,
                exec.CheckpointSummary ?? string.Empty,
                exec.ArgumentsSummary ?? string.Empty,
                string.Join('\n', exec.Evidence)
            });

        if (string.IsNullOrWhiteSpace(haystack))
        {
            return null;
        }

        var compact = CompactDiffStatsRegex.Match(haystack);
        if (compact.Success
            && int.TryParse(compact.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cAdded)
            && int.TryParse(compact.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cRemoved))
        {
            return new DiagnosticFileEditStatsDto
            {
                ExecutionId = exec.Id,
                ToolId = exec.ToolId,
                FilePath = exec.ResourcePath,
                Added = cAdded,
                Removed = cRemoved
            };
        }

        var ins = GitInsertionsRegex.Match(haystack);
        var del = GitDeletionsRegex.Match(haystack);
        if (ins.Success || del.Success)
        {
            return new DiagnosticFileEditStatsDto
            {
                ExecutionId = exec.Id,
                ToolId = exec.ToolId,
                FilePath = exec.ResourcePath,
                Added = ins.Success && int.TryParse(ins.Groups[1].Value, out var ia) ? ia : 0,
                Removed = del.Success && int.TryParse(del.Groups[1].Value, out var dr) ? dr : 0
            };
        }

        var addedField = AddedFieldRegex.Match(haystack);
        var removedField = RemovedFieldRegex.Match(haystack);
        if (addedField.Success || removedField.Success)
        {
            return new DiagnosticFileEditStatsDto
            {
                ExecutionId = exec.Id,
                ToolId = exec.ToolId,
                FilePath = exec.ResourcePath,
                Added = addedField.Success && int.TryParse(addedField.Groups[1].Value, out var fa) ? fa : 0,
                Removed = removedField.Success && int.TryParse(removedField.Groups[1].Value, out var fr) ? fr : 0
            };
        }

        var diffAdded = 0;
        var diffRemoved = 0;
        var sawHunk = false;
        foreach (var rawLine in haystack.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                sawHunk = true;
                continue;
            }

            if (!sawHunk) continue;
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) diffAdded++;
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)) diffRemoved++;
        }

        if (sawHunk && (diffAdded > 0 || diffRemoved > 0))
        {
            return new DiagnosticFileEditStatsDto
            {
                ExecutionId = exec.Id,
                ToolId = exec.ToolId,
                FilePath = exec.ResourcePath,
                Added = diffAdded,
                Removed = diffRemoved
            };
        }

        return null;
    }

    public static IReadOnlyList<DiagnosticCodeFrameDto> ExtractErrorCodeFrames(DiagnosticToolExecutionItem exec)
    {
        var failed = IsErrorStatus(exec.Status)
            || exec.ToolSuccess == false
            || !string.IsNullOrWhiteSpace(exec.ErrorCategory);
        if (!failed)
        {
            return [];
        }

        var rawLines = exec.Evidence
            .SelectMany(item => (item ?? string.Empty).Split('\n'))
            .Concat(!string.IsNullOrWhiteSpace(exec.Summary) ? exec.Summary.Split('\n') : [])
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        string? detectedPath = exec.ResourcePath;
        int? detectedLine = null;
        int? detectedCol = null;
        var parsedLines = new List<DiagnosticCodeFrameLineDto>();

        foreach (var rawLine in rawLines)
        {
            var fileMatch = FileLineRegex.Match(rawLine);
            if (fileMatch.Success && detectedLine is null)
            {
                if (!string.IsNullOrWhiteSpace(fileMatch.Groups[1].Value))
                {
                    detectedPath = fileMatch.Groups[1].Value;
                }

                var lineStr = !string.IsNullOrEmpty(fileMatch.Groups[2].Value) ? fileMatch.Groups[2].Value : fileMatch.Groups[4].Value;
                var colStr = !string.IsNullOrEmpty(fileMatch.Groups[3].Value) ? fileMatch.Groups[3].Value : fileMatch.Groups[5].Value;
                if (int.TryParse(lineStr, out var pl) && pl > 0) detectedLine = pl;
                if (int.TryParse(colStr, out var pc) && pc > 0) detectedCol = pc;
            }

            var numMatch = NumberedCodeLineRegex.Match(rawLine);
            if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, out var ln))
            {
                var trimmedStart = rawLine.TrimStart();
                var highlight = trimmedStart.StartsWith('>')
                    || trimmedStart.StartsWith('→')
                    || (detectedLine.HasValue && ln == detectedLine.Value);
                parsedLines.Add(new DiagnosticCodeFrameLineDto
                {
                    LineNumber = ln,
                    Text = RedactSensitiveText(numMatch.Groups[2].Value),
                    Highlight = highlight
                });
            }
            else if (parsedLines.Count < 10)
            {
                parsedLines.Add(new DiagnosticCodeFrameLineDto
                {
                    LineNumber = null,
                    Text = Clip(RedactSensitiveText(rawLine), MaxTextExcerpt) ?? string.Empty,
                    Highlight = ErrorHighlightLineRegex.IsMatch(rawLine)
                });
            }
        }

        if (parsedLines.Count == 0)
        {
            var fallback = FirstNonEmpty(exec.Summary, exec.ErrorCategory) ?? $"Tool {exec.ToolId} failed ({exec.Status})";
            parsedLines.Add(new DiagnosticCodeFrameLineDto
            {
                LineNumber = detectedLine,
                Text = Clip(RedactSensitiveText(fallback), MaxTextExcerpt) ?? "Tool failed",
                Highlight = true
            });
        }

        var title = !string.IsNullOrWhiteSpace(detectedPath)
            ? (detectedLine.HasValue
                ? $"{detectedPath}:{detectedLine.Value}{(detectedCol.HasValue ? $":{detectedCol.Value}" : string.Empty)}"
                : detectedPath)
            : !string.IsNullOrWhiteSpace(exec.Command)
                ? $"$ {RedactSensitiveText(exec.Command)}"
                : FirstNonEmpty(exec.ToolDisplayName, exec.ToolId) ?? "tool";

        return
        [
            new DiagnosticCodeFrameDto
            {
                Id = $"frame:{exec.Id}:0",
                SourceId = exec.Id,
                Title = title,
                FilePath = detectedPath,
                Line = detectedLine,
                Column = detectedCol,
                Command = string.IsNullOrWhiteSpace(exec.Command) ? null : RedactSensitiveText(exec.Command),
                Cwd = exec.Cwd,
                ErrorCategory = exec.ErrorCategory,
                Lines = parsedLines.Take(MaxCodeFrameLines).ToArray()
            }
        ];
    }

    // ── 4. Executive Diagnostic Findings ──

    private static IReadOnlyList<ExecutiveDiagnosticFindingDto> BuildExecutiveFindings(
        ContextWindowBucketReportDto buckets,
        ContextCompactionDiagnosticsDto compaction,
        RunGovernanceDiagnosticsDto governance)
    {
        var findings = new List<ExecutiveDiagnosticFindingDto>();

        if (buckets.DegradationDetected)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "CONTEXT_ROT_DETECTED",
                Severity = "danger",
                Summary = $"High-occupancy (>=70%) context degradation detected: peak {buckets.PeakOccupancyPct:0.#}%, error rate rose from {buckets.LowOccupancyErrorRate * 100:0.#}% (<70%) to {buckets.HighOccupancyErrorRate * 100:0.#}% (>=70%), avg latency {buckets.LowOccupancyAvgLatencyMs}ms -> {buckets.HighOccupancyAvgLatencyMs}ms.",
                EvidenceRefs = buckets.DegradationReasonCodes,
                SuggestedAction = "Lower compaction_trigger_ratio (e.g., 0.75 -> 0.65) or switch context_strategy to 'managed'/'deep_curator' so compaction fires before entering >=70% occupancy."
            });
        }

        if (compaction.CriticalDroppedSources.Count > 0)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "CRITICAL_CONTEXT_DROPPED",
                Severity = "danger",
                Summary = $"High-value context sources were dropped due to token budget overflow ({compaction.TotalDroppedTokensAcrossPacks} dropped tokens across {compaction.PackCount} packs): {string.Join(", ", compaction.CriticalDroppedSources)}.",
                EvidenceRefs = compaction.CriticalDroppedSources,
                SuggestedAction = "Reduce recent_message_limit, deduplicate overlapping prompt/skill prose, or raise context token budget so instructions and reviewed memory survive."
            });
        }
        else if (compaction.TotalDroppedTokensAcrossPacks > 0)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "CONTEXT_BUDGET_DROPS",
                Severity = "warn",
                Summary = $"Context packer dropped {compaction.TotalDroppedTokensAcrossPacks} tokens across {compaction.PackCount} pack(s) to fit within budget.",
                EvidenceRefs = string.IsNullOrWhiteSpace(compaction.SelectedPackId) ? [] : [compaction.SelectedPackId],
                SuggestedAction = "Inspect ContextPack reconciliation to verify dropped conversation turns are covered by session_compaction digests."
            });
        }

        if (governance.MaxSameArgsRetryStreak >= 2 || governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.LoopDetected) > 0)
        {
            var isLoop = governance.MaxSameArgsRetryStreak >= 4 || governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.LoopDetected) > 0;
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = isLoop ? "EXECUTION_LOOP_DETECTED" : "SAME_ARGS_RETRY_STREAK",
                Severity = isLoop ? "danger" : "warn",
                Summary = $"Repeated identical-argument tool retries observed (max streak: {Math.Max(2, governance.MaxSameArgsRetryStreak)}x).",
                EvidenceRefs = governance.RetryStreaks.Take(5).Select(r => r.ExecutionId).ToArray(),
                SuggestedAction = "Record a failure_pattern or supervision_rule memory item so workers inspect root errors instead of repeating identical arguments."
            });
        }

        var staleCount = governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.FsStaleVersion);
        var notObsCount = governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.FsNotObserved);
        if (staleCount > 0 || notObsCount > 0)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "FILESYSTEM_PRECONDITION_VIOLATION",
                Severity = "warn",
                Summary = $"Filesystem observation/version guard triggered (FS_STALE_VERSION={staleCount}, FS_NOT_OBSERVED={notObsCount}).",
                EvidenceRefs = governance.Signals
                    .Where(s => s.Code is GovernanceSignalCodes.FsStaleVersion or GovernanceSignalCodes.FsNotObserved)
                    .SelectMany(s => s.SubjectIds)
                    .Take(6)
                    .ToArray(),
                SuggestedAction = "Always read target file slices immediately prior to editing and refresh stale line hashes after concurrent edits."
            });
        }

        var wtCount = governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.WorktreeGuard);
        var scopeCount = governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.WriteScopeLocked);
        if (wtCount > 0 || scopeCount > 0)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "ISOLATION_BOUNDARY_INTERCEPTED",
                Severity = "danger",
                Summary = $"Workspace isolation or lease boundary intercepted writes (WORKTREE_GUARD={wtCount}, WRITE_SCOPE_LOCKED={scopeCount}).",
                EvidenceRefs = governance.Signals
                    .Where(s => s.Code is GovernanceSignalCodes.WorktreeGuard or GovernanceSignalCodes.WriteScopeLocked)
                    .SelectMany(s => s.SubjectIds)
                    .Take(6)
                    .ToArray(),
                SuggestedAction = "Partition parallel worker write_scope prefixes cleanly so teammates never contend on identical files."
            });
        }

        if (governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.Timeout) > 0)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "EXECUTION_TIMEOUT",
                Severity = "danger",
                Summary = $"Detected {governance.SignalCounts[GovernanceSignalCodes.Timeout]} timeout event(s) across model or tool calls.",
                EvidenceRefs = governance.Signals
                    .Where(s => s.Code == GovernanceSignalCodes.Timeout)
                    .SelectMany(s => s.SubjectIds)
                    .Take(6)
                    .ToArray(),
                SuggestedAction = "Narrow command/test filter scope or split oversized model turns before hitting timeout ceilings."
            });
        }

        if (governance.SignalCounts.GetValueOrDefault(GovernanceSignalCodes.ApprovalRejected) > 0)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "APPROVAL_REJECTED_HALT",
                Severity = "warn",
                Summary = $"User or policy rejected {governance.SignalCounts[GovernanceSignalCodes.ApprovalRejected]} gated tool execution(s).",
                EvidenceRefs = governance.Signals
                    .Where(s => s.Code == GovernanceSignalCodes.ApprovalRejected)
                    .SelectMany(s => s.SubjectIds)
                    .Take(6)
                    .ToArray(),
                SuggestedAction = "Switch to read-only inspection or safer scoped alternatives when a mutating tool call is denied."
            });
        }

        if (findings.Count == 0)
        {
            findings.Add(new ExecutiveDiagnosticFindingDto
            {
                Code = "CONTEXT_HEALTHY",
                Severity = "info",
                Summary = $"Context health nominal: peak occupancy {buckets.PeakOccupancyPct:0.#}%, zero high-occupancy degradation, and zero critical governance violations.",
                EvidenceRefs = [],
                SuggestedAction = "No context policy intervention required."
            });
        }

        return findings;
    }

    // ── Event Journal & Tool Snapshot Reconstruction Helpers ──

    private static List<DiagnosticContextPackSnapshot> ExtractContextPacks(
        IReadOnlyList<EventEnvelope> events,
        string runId)
    {
        var packs = new List<DiagnosticContextPackSnapshot>();
        foreach (var e in events.Where(e => e.EventType == "context.packed"))
        {
            packs.Add(new DiagnosticContextPackSnapshot(
                Id: e.EventId,
                RunId: runId,
                LaneKey: PayloadString(e.Payload, "lane_key"),
                EvidenceCount: PayloadInt(e.Payload, "evidence_count") ?? 0,
                EstimatedTokens: PayloadInt(e.Payload, "estimated_tokens") ?? 0,
                TokenBudget: PayloadInt(e.Payload, "token_budget") ?? 0,
                Sources: PayloadStringArray(e.Payload, "sources"),
                SourceTokens: PayloadShares(e.Payload, "source_tokens"),
                DroppedSources: PayloadShares(e.Payload, "dropped_sources"),
                CreatedAt: e.Timestamp));
        }

        return packs;
    }

    private static List<DiagnosticSupervisionFinding> ExtractSupervisionFindings(IReadOnlyList<EventEnvelope> events)
    {
        var findings = new List<DiagnosticSupervisionFinding>();
        foreach (var e in events.Where(e => e.EventType is "supervision.requested" or "supervision.completed"))
        {
            var reasons = PayloadStringArray(e.Payload, "reasons");
            var decision = PayloadString(e.Payload, "decision");
            var summary = reasons.Count > 0
                ? string.Join("; ", reasons)
                : PayloadString(e.Payload, "summary") ?? decision;
            findings.Add(new DiagnosticSupervisionFinding(
                e.EventId,
                decision,
                summary,
                PayloadString(e.Payload, "recommendation"),
                e.Timestamp));
        }

        return findings;
    }

    private static List<string> ExtractLifecycleSignalCodes(IReadOnlyList<EventEnvelope> events, RunState runState)
    {
        var codes = new List<string>();
        foreach (var e in events)
        {
            var summary = PayloadString(e.Payload, "summary") ?? PayloadString(e.Payload, "message");
            if (LiveExplainerSemanticFolder.ClassifyLifecycleEvent(e.EventType, summary, e.Payload) is { } classified)
            {
                codes.Add(classified.Code);
            }
        }

        if (!string.IsNullOrWhiteSpace(runState.TerminalErrorCategory))
        {
            var cat = runState.TerminalErrorCategory.ToLowerInvariant();
            if (cat.Contains("timeout", StringComparison.Ordinal)) codes.Add(GovernanceSignalCodes.Timeout);
            if (cat.Contains("loop", StringComparison.Ordinal) || cat.Contains("tool_round_limit", StringComparison.Ordinal) || cat.Contains("tool_call_ceiling", StringComparison.Ordinal) || cat.Contains("too_many_consecutive_errors", StringComparison.Ordinal))
                codes.Add(GovernanceSignalCodes.LoopDetected);
            if (cat.Contains("token_budget_exhausted", StringComparison.Ordinal) || cat.Contains("context", StringComparison.Ordinal))
                codes.Add(GovernanceSignalCodes.ContextPressure);
        }

        return codes;
    }

    private static IReadOnlyList<DiagnosticToolExecutionItem> BuildDiagnosticToolExecutions(
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<ToolExecutionSnapshot> snapshotRows,
        string sessionId)
    {
        var relevant = events.Where(e => ToolTimelineEventTypes.Contains(e.EventType)).ToList();
        var executionByPermission = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in relevant.Where(e => e.EventType == "approval.requested"))
        {
            var executionId = PayloadString(e.Payload, "execution_id");
            var permissionId = PayloadString(e.Payload, "permission_request_id");
            if (!string.IsNullOrWhiteSpace(executionId) && !string.IsNullOrWhiteSpace(permissionId))
            {
                executionByPermission[permissionId] = executionId;
            }
        }

        var byId = new Dictionary<string, MutableToolItem>(StringComparer.OrdinalIgnoreCase);
        long fallbackSeq = 0;

        foreach (var e in relevant)
        {
            fallbackSeq++;
            var executionId = PayloadString(e.Payload, "execution_id");
            if (string.IsNullOrWhiteSpace(executionId) && e.EventType == "governance.permission_decided")
            {
                var permissionId = PayloadString(e.Payload, "permission_request_id");
                if (!string.IsNullOrWhiteSpace(permissionId))
                {
                    executionByPermission.TryGetValue(permissionId, out executionId);
                }
            }

            if (string.IsNullOrWhiteSpace(executionId))
            {
                continue;
            }

            var seq = EventSequence(e.Payload);
            if (seq <= 0) seq = fallbackSeq;

            if (!byId.TryGetValue(executionId, out var row))
            {
                row = new MutableToolItem
                {
                    Id = executionId,
                    RunId = e.RunId ?? string.Empty,
                    SessionId = sessionId,
                    RequestedAt = e.Timestamp,
                    UpdatedAt = e.Timestamp,
                    RequestedSeq = seq
                };
                byId[executionId] = row;
            }

            row.ToolId = PayloadString(e.Payload, "tool_id") ?? row.ToolId;
            var summary = PayloadString(e.Payload, "summary");
            if (!string.IsNullOrWhiteSpace(summary)) row.Summary = summary;
            row.TaskId = PayloadString(e.Payload, "task_id") ?? row.TaskId;
            row.AgentInstanceId = PayloadString(e.Payload, "agent_instance_id") ?? row.AgentInstanceId;

            if (e.EventType == "tool.execution.requested")
            {
                row.ArgumentsSummary = PayloadString(e.Payload, "arguments_summary") ?? row.ArgumentsSummary;
                row.ResourcePath = PayloadString(e.Payload, "resource_path") ?? row.ResourcePath;
                row.Command = PayloadString(e.Payload, "command") ?? row.Command;
                row.Cwd = PayloadString(e.Payload, "cwd") ?? row.Cwd;
                row.Query = PayloadString(e.Payload, "query") ?? row.Query;
            }

            if (e.EventType is "tool.execution.failed" or "tool.execution.outcome_unknown")
            {
                row.ErrorCategory = PayloadString(e.Payload, "error_category") ?? PayloadString(e.Payload, "category") ?? row.ErrorCategory;
            }

            if (PayloadBool(e.Payload, "tool_success") is { } ts)
            {
                row.ToolSuccess = ts;
            }

            row.Status = e.EventType switch
            {
                "tool.execution.requested" => "requested",
                "approval.requested" => "waiting_approval",
                "approval.decided" => string.Equals(PayloadString(e.Payload, "decision"), "approved", StringComparison.OrdinalIgnoreCase)
                    ? "running"
                    : "rejected",
                "governance.permission_decided" => string.Equals(PayloadString(e.Payload, "outcome"), "allowed", StringComparison.OrdinalIgnoreCase)
                    ? "running"
                    : "denied",
                "tool.execution.completed" => "completed",
                "tool.execution.failed" => "failed",
                "tool.execution.outcome_unknown" => "outcome_unknown",
                _ => row.Status
            };

            if (!row.EventTypes.Contains(e.EventType, StringComparer.Ordinal))
            {
                row.EventTypes.Add(e.EventType);
            }

            if (seq < row.RequestedSeq) row.RequestedSeq = seq;
            if (e.Timestamp < row.RequestedAt) row.RequestedAt = e.Timestamp;
            if (e.Timestamp > row.UpdatedAt) row.UpdatedAt = e.Timestamp;
        }

        // Enrich or backfill from durable ToolExecutionSnapshot rows.
        foreach (var snap in snapshotRows.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id))
        {
            fallbackSeq++;
            var idStr = snap.Id.ToString();
            if (!byId.TryGetValue(idStr, out var row))
            {
                row = new MutableToolItem
                {
                    Id = idStr,
                    RunId = snap.RunId.ToString(),
                    SessionId = snap.SessionId.ToString(),
                    ToolId = snap.ToolId,
                    Status = snap.Status,
                    RequestedAt = snap.CreatedAt,
                    UpdatedAt = snap.CompletedAt ?? snap.UpdatedAt,
                    RequestedSeq = fallbackSeq
                };
                byId[idStr] = row;
            }

            if (string.IsNullOrWhiteSpace(row.ToolId)) row.ToolId = snap.ToolId;
            if (!string.IsNullOrWhiteSpace(snap.Status) && (row.Status is "requested" or "running" or "waiting_approval"))
            {
                row.Status = snap.Status;
            }

            row.ToolSuccess ??= snap.ToolSuccess;
            row.ErrorCategory ??= snap.ErrorCategory;
            if (string.IsNullOrWhiteSpace(row.Summary) && !string.IsNullOrWhiteSpace(snap.SafeErrorMessage))
            {
                row.Summary = snap.SafeErrorMessage;
            }

            if (snap.TaskId != Guid.Empty) row.TaskId ??= snap.TaskId.ToString();
            if (snap.AgentInstanceId != Guid.Empty) row.AgentInstanceId ??= snap.AgentInstanceId.ToString();

            EnrichFromParametersJson(row, snap.ParametersJson);
            EnrichFromResultJson(row, snap.ResultJson, snap.SafeErrorMessage);

            var endTs = snap.CompletedAt ?? snap.UpdatedAt;
            if (endTs > row.UpdatedAt) row.UpdatedAt = endTs;
        }

        return byId.Values
            .OrderBy(r => r.RequestedSeq)
            .ThenBy(r => r.RequestedAt)
            .Select(r => new DiagnosticToolExecutionItem(
                Id: r.Id,
                RunId: r.RunId,
                SessionId: r.SessionId,
                ToolId: r.ToolId,
                ToolDisplayName: null,
                Status: r.Status,
                ToolSuccess: r.ToolSuccess,
                ErrorCategory: r.ErrorCategory,
                Summary: r.Summary,
                CheckpointSummary: null,
                ArgumentsSummary: r.ArgumentsSummary,
                ResourcePath: r.ResourcePath,
                Command: r.Command,
                Cwd: r.Cwd,
                Query: r.Query,
                TaskId: r.TaskId,
                AgentInstanceId: r.AgentInstanceId,
                DurationMs: Math.Max(0L, (long)(r.UpdatedAt - r.RequestedAt).TotalMilliseconds),
                RequestedSeq: r.RequestedSeq,
                RequestedAt: r.RequestedAt,
                UpdatedAt: r.UpdatedAt,
                EventTypes: r.EventTypes,
                Evidence: r.Evidence))
            .ToArray();
    }

    private static void EnrichFromParametersJson(MutableToolItem row, string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            row.ResourcePath ??= ReadJsonString(root, "path") ?? ReadJsonString(root, "file_path") ?? ReadJsonString(root, "resource_path");
            row.Command ??= ReadJsonString(root, "command") ?? ReadJsonString(root, "cmd");
            row.Cwd ??= ReadJsonString(root, "cwd") ?? ReadJsonString(root, "working_directory");
            row.Query ??= ReadJsonString(root, "query") ?? ReadJsonString(root, "pattern");

            if (string.IsNullOrWhiteSpace(row.ArgumentsSummary))
            {
                var safePairs = new List<string>();
                foreach (var prop in root.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Take(6))
                {
                    if (prop.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("key", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("env", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("headers", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var rawVal = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.GetRawText();
                    if (!string.IsNullOrWhiteSpace(rawVal))
                    {
                        safePairs.Add($"{prop.Name}={Clip(RedactSensitiveText(rawVal), 80)}");
                    }
                }

                if (safePairs.Count > 0)
                {
                    row.ArgumentsSummary = string.Join(", ", safePairs);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON parameters.
        }
    }

    private static void EnrichFromResultJson(MutableToolItem row, string? resultJson, string? safeErrorMessage)
    {
        if (!string.IsNullOrWhiteSpace(safeErrorMessage) && row.Evidence.Count == 0)
        {
            row.Evidence.Add(RedactSensitiveText(safeErrorMessage));
        }

        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var errText = ReadJsonString(root, "error") ?? ReadJsonString(root, "message") ?? ReadJsonString(root, "stderr");
                if (!string.IsNullOrWhiteSpace(errText) && row.Evidence.Count < 8)
                {
                    row.Evidence.Add(Clip(RedactSensitiveText(errText), 600)!);
                }

                if (string.IsNullOrWhiteSpace(row.Summary))
                {
                    var sum = ReadJsonString(root, "summary");
                    if (!string.IsNullOrWhiteSpace(sum)) row.Summary = RedactSensitiveText(sum);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON results.
        }
    }

    // ── Shared Value / JSON Helpers ──

    private static JsonElement PayloadRoot(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is not null
            && payload.TryGetValue("payload", out var inner)
            && inner is JsonElement { ValueKind: JsonValueKind.Object } nested)
        {
            return nested;
        }

        return default;
    }

    private static string? PayloadString(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null) return null;
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var val)
            && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }

        if (payload.TryGetValue(key, out var direct))
        {
            return direct switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        return null;
    }

    private static int? PayloadInt(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null) return null;
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var val)
            && val.ValueKind == JsonValueKind.Number
            && val.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        if (payload.TryGetValue(key, out var direct))
        {
            return direct switch
            {
                int i => i,
                long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
                JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var jp) => jp,
                _ => null
            };
        }

        return null;
    }

    private static bool? PayloadBool(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null) return null;
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var val)
            && val.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return val.GetBoolean();
        }

        if (payload.TryGetValue(key, out var direct))
        {
            return direct switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } je => je.GetBoolean(),
                _ => null
            };
        }

        return null;
    }

    private static IReadOnlyList<string> PayloadStringArray(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null) return [];
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            return arr.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToArray();
        }

        if (payload.TryGetValue(key, out var direct))
        {
            if (direct is IEnumerable<string> strEnum)
            {
                return strEnum.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            }

            if (direct is JsonElement { ValueKind: JsonValueKind.Array } je)
            {
                return je.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? string.Empty)
                    .Where(x => x.Length > 0)
                    .ToArray();
            }
        }

        return [];
    }

    private static IReadOnlyList<DiagnosticContextBudgetShare> PayloadShares(
        IReadOnlyDictionary<string, object?>? payload,
        string key)
    {
        if (payload is null) return [];

        JsonElement target = default;
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Array)
        {
            target = nested;
        }
        else if (payload.TryGetValue(key, out var direct))
        {
            if (direct is JsonElement { ValueKind: JsonValueKind.Array } je)
            {
                target = je;
            }
            else if (direct is not null)
            {
                try
                {
                    target = JsonSerializer.SerializeToElement(direct);
                }
                catch
                {
                    return [];
                }
            }
        }

        if (target.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var shares = new List<DiagnosticContextBudgetShare>();
        foreach (var item in target.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var src = ReadJsonString(item, "source");
            if (string.IsNullOrWhiteSpace(src)) continue;
            if (item.TryGetProperty("tokens", out var tokEl) && tokEl.ValueKind == JsonValueKind.Number && tokEl.TryGetInt32(out var tok))
            {
                shares.Add(new DiagnosticContextBudgetShare(src, tok));
            }
        }

        return shares;
    }

    private static long EventSequence(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is not null && payload.TryGetValue("sequence", out var value))
        {
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is JsonElement { ValueKind: JsonValueKind.Number } json && json.TryGetInt64(out var parsed)) return parsed;
        }

        return 0L;
    }

    private static string? ReadJsonString(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var exact) && exact.ValueKind == JsonValueKind.String)
        {
            return exact.GetString();
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    private static bool IsErrorStatus(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        return s is "failed" or "error" or "rejected" or "denied" or "not_approved" or "timeout" or "timed_out" or "cancelled" or "aborted" or "blocked";
    }

    private static long? DurationBetweenMs(DateTimeOffset start, DateTimeOffset? end)
    {
        if (!end.HasValue || end.Value < start) return null;
        return (long)Math.Max(0, (end.Value - start).TotalMilliseconds);
    }

    private static double JsRound(double value) => Math.Floor(value + 0.5);

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c)) return c;
        }

        return null;
    }

    private static string? Clip(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "…";
    }

    private static string RedactSensitiveText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var redacted = BearerSecretRegex.Replace(input, "$1********");
        redacted = TokenPrefixSecretRegex.Replace(redacted, "********");
        redacted = KeyValueSecretRegex.Replace(redacted, "$1********");
        return redacted;
    }

    private sealed class SignalAccumulator(string code, string severity)
    {
        public string Code { get; } = code;
        public string Severity { get; } = severity;
        public int Count { get; set; }
        public int? MaxRetryStreak { get; set; }
        public string? Detail { get; set; }
        public List<string> SubjectIds { get; } = [];
    }

    private sealed class MutableToolItem
    {
        public string Id { get; init; } = string.Empty;
        public string RunId { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string ToolId { get; set; } = string.Empty;
        public string Status { get; set; } = "requested";
        public bool? ToolSuccess { get; set; }
        public string? ErrorCategory { get; set; }
        public string? Summary { get; set; }
        public string? ArgumentsSummary { get; set; }
        public string? ResourcePath { get; set; }
        public string? Command { get; set; }
        public string? Cwd { get; set; }
        public string? Query { get; set; }
        public string? TaskId { get; set; }
        public string? AgentInstanceId { get; set; }
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public long RequestedSeq { get; set; }
        public List<string> EventTypes { get; } = [];
        public List<string> Evidence { get; } = [];
    }
}
