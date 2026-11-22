using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA.LiveExplainer;

public sealed class LiveExplainerOptions
{
    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromMilliseconds(3200);
    public TimeSpan ModelCallTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    public int DefaultMaxContextTokens { get; init; } = 128_000;
}

/// <summary>
/// Governance-layer independent read-only sidecar narrator (<c>LiveExplainerService</c>).
/// Combines zero-latency L0 structural folding with debounced L1 Lead Narrator synthesis and
/// on-demand L2 ephemeral read-only scouts under strict <c>IsObserverRun = true</c> isolation.
/// </summary>
public sealed class LiveExplainerService : IHostedService, IDisposable
{
    private const string LeadNarratorSystemPrompt =
        "你是 Tinadec 治理层的首席实时解说员（Lead Narrator）。你处于绝对只读旁路观察模式（IsObserverRun=true）。\n" +
        "请根据给定的紧凑语义窗口、原始 <thought> 摘要、文件改动统计、队友状态与治理信号，输出严格合法的单行 JSON（不要 Markdown 代码块）：\n" +
        "{\"headline\":\"一句话此刻在干嘛（<=45字）\",\"phase\":\"hypothesis|verification|conclusion\"," +
        "\"chapter_title\":\"当前阶段短标题（<=22字）\",\"narrative\":\"因果人话解说：为什么这么做、验证了什么、得出什么进展（<=140字）\"," +
        "\"teammate_thoughts\":[{\"handle\":\"队友handle\",\"thought\":\"一句话此刻心声（<=36字）\"}]," +
        "\"sentinel\":null 或 {\"code\":\"SAME_ARGS_RETRY|LOOP_DETECTED|...\",\"title\":\"温和预警标题\",\"message\":\"人话原因\",\"suggested_steer_prompt\":\"可直接发给执行层的一句话纠偏指令\"}}";

    private static readonly JsonSerializerOptions CheckpointJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILifecycleManager _lifecycle;
    private readonly IToolExecutionCoordinator? _toolExecutions;
    private readonly IDbContextFactory<SessionPlanDbContext>? _sessionPlans;
    private readonly RunStreamProjection? _projection;
    private readonly GovernanceTopicSink? _governanceSink;
    private readonly LiveExplainerStore _store;
    private readonly ILiveExplainerModelInvoker _modelInvoker;
    private readonly LiveExplainerScoutRunner _scoutRunner;
    private readonly ILogger<LiveExplainerService>? _logger;
    private readonly LiveExplainerOptions _options;

    private readonly ConcurrentDictionary<Guid, RunTracker> _trackers = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    internal LiveExplainerService(
        ILifecycleManager lifecycle,
        LiveExplainerStore store,
        ILiveExplainerModelInvoker modelInvoker,
        LiveExplainerScoutRunner scoutRunner,
        IToolExecutionCoordinator? toolExecutions = null,
        IDbContextFactory<SessionPlanDbContext>? sessionPlans = null,
        RunStreamProjection? projection = null,
        IGovernanceTopicSink? governanceSink = null,
        LiveExplainerOptions? options = null,
        ILogger<LiveExplainerService>? logger = null)
    {
        _lifecycle = lifecycle;
        _store = store;
        _modelInvoker = modelInvoker;
        _scoutRunner = scoutRunner;
        _toolExecutions = toolExecutions;
        _sessionPlans = sessionPlans;
        _projection = projection;
        _governanceSink = governanceSink as GovernanceTopicSink;
        _options = options ?? new LiveExplainerOptions();
        _logger = logger;

        if (_projection is not null)
        {
            _projection.ChunkObserved += OnChunkObserved;
            _projection.RunSealed += OnRunSealed;
        }

        if (_governanceSink is not null)
        {
            _governanceSink.SignalObserved += OnGovernanceSignalObserved;
        }
    }

    /// <summary>
    /// Direct non-blocking hook for tests or in-process governance signal injection.
    /// </summary>
    public void ObserveGovernanceSignal(Guid runId, string code, string summary, string? subjectId = null, string? teammateHandle = null)
    {
        if (ObserverRunScope.IsCurrentObserverRun)
        {
            ObserverRunScope.RecordSuppressedRecursiveSignal();
            return;
        }

        if (!GovernanceSignalCodes.All.Contains(code)) return;
        var tracker = GetOrCreateTracker(runId);
        _ = Task.Run(() => ApplyGovernanceSignalAsync(tracker, code, "warning", summary, subjectId, teammateHandle));
    }

    public async Task<LiveDigestSnapshotDto> GetSnapshotAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (_trackers.TryGetValue(runId, out var activeTracker))
        {
            await SyncDurableRunFactsAsync(activeTracker, cancellationToken).ConfigureAwait(false);
            return activeTracker.BuildSnapshot();
        }

        // Check durable snapshot first (fast path for completed runs).
        var persisted = await _store.GetSnapshotAsync(runId, cancellationToken: cancellationToken).ConfigureAwait(false);

        RunState runState;
        try
        {
            runState = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            if (persisted is not null) return persisted;
            throw;
        }

        if (IsObserverRunKind(runState))
        {
            throw new KeyNotFoundException($"Observer run '{runId}' does not expose a recursive digest.");
        }

        var normalizedRunStatus = MapRunStatus(runState.Status);
        if (persisted is not null && IsTerminalStatus(persisted.Status) && persisted.Status == normalizedRunStatus)
        {
            return persisted;
        }

        // Either the run is still active or finished before any live follower attached: hydrate L0 state.
        var tracker = GetOrCreateTracker(runId, persisted);
        await SyncDurableRunFactsAsync(tracker, cancellationToken).ConfigureAwait(false);
        var snapshot = tracker.BuildSnapshot();
        await PersistSafeAsync(tracker, snapshot, cancellationToken).ConfigureAwait(false);

        if (IsTerminalStatus(snapshot.Status))
        {
            tracker.MarkCompleted();
        }

        return snapshot;
    }

    public async IAsyncEnumerable<LiveDigestStreamFrame> FollowAsync(
        Guid runId,
        long afterSeq = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var initial = await GetSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);

        if (afterSeq <= 0)
        {
            yield return new LiveDigestStreamFrame
            {
                Seq = initial.Seq,
                EventKind = "snapshot",
                Payload = initial,
                OccurredAt = initial.UpdatedAt
            };
        }

        if (IsTerminalStatus(initial.Status) && (!_trackers.TryGetValue(runId, out var existing) || !existing.HasPendingNarration))
        {
            yield return new LiveDigestStreamFrame
            {
                Seq = initial.Seq,
                EventKind = "done",
                Payload = initial,
                OccurredAt = DateTimeOffset.UtcNow
            };
            yield break;
        }

        var tracker = GetOrCreateTracker(runId, initial);
        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateBounded<LiveDigestStreamFrame>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        tracker.Subscribers[subscriberId] = channel.Writer;
        try
        {
            foreach (var buffered in tracker.FramesAfter(Math.Max(afterSeq, afterSeq <= 0 ? initial.Seq : afterSeq)))
            {
                yield return buffered;
                if (buffered.EventKind == "done") yield break;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
                waitCts.CancelAfter(_options.HeartbeatInterval);

                LiveDigestStreamFrame? nextFrame = null;
                var timedOut = false;
                try
                {
                    if (await channel.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false))
                    {
                        channel.Reader.TryRead(out nextFrame);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdownCts.IsCancellationRequested)
                {
                    timedOut = true;
                }

                if (timedOut)
                {
                    // Refresh durable run facts opportunistically on idle ticks so external tool completions surface promptly.
                    await SyncDurableRunFactsAsync(tracker, cancellationToken).ConfigureAwait(false);
                    while (channel.Reader.TryRead(out var drained))
                    {
                        yield return drained;
                        if (drained.EventKind == "done") yield break;
                    }

                    yield return new LiveDigestStreamFrame
                    {
                        Seq = tracker.CurrentSeq,
                        EventKind = "heartbeat",
                        Payload = string.Empty,
                        OccurredAt = DateTimeOffset.UtcNow
                    };
                    continue;
                }

                if (nextFrame is not null)
                {
                    yield return nextFrame;
                    if (nextFrame.EventKind == "done") yield break;
                }
            }
        }
        finally
        {
            tracker.Subscribers.TryRemove(subscriberId, out _);
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Waits for any queued background L1/L2 narration pass on <paramref name="runId"/> (for deterministic testing).</summary>
    public async Task FlushPendingNarrationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (!_trackers.TryGetValue(runId, out var tracker)) return;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (tracker.HasPendingNarration && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnChunkObserved(RunStreamChunk chunk)
    {
        if (_disposed != 0) return;
        if (ObserverRunScope.IsCurrentObserverRun)
        {
            ObserverRunScope.RecordSuppressedRecursiveSignal();
            return;
        }

        var tracker = GetOrCreateTracker(chunk.RunId);
        _ = Task.Run(() => ProcessStreamChunkSafeAsync(tracker, chunk));
    }

    private void OnRunSealed(Guid runId)
    {
        if (_disposed != 0) return;
        if (ObserverRunScope.IsCurrentObserverRun)
        {
            ObserverRunScope.RecordSuppressedRecursiveSignal();
            return;
        }

        var tracker = GetOrCreateTracker(runId);
        _ = Task.Run(() => HandleRunSealedSafeAsync(tracker));
    }

    private void OnGovernanceSignalObserved(GovernanceTopicSignal signal)
    {
        if (_disposed != 0) return;
        if (ObserverRunScope.IsCurrentObserverRun)
        {
            ObserverRunScope.RecordSuppressedRecursiveSignal();
            return;
        }

        var classified = LiveExplainerSemanticFolder.ClassifyGovernanceTopic(signal);
        if (classified is null) return;

        var tracker = GetOrCreateTracker(signal.RunId);
        _ = Task.Run(() => ApplyGovernanceSignalAsync(
            tracker,
            classified.Value.Code,
            classified.Value.Severity,
            classified.Value.Summary,
            signal.SubjectId,
            teammateHandle: null));
    }

    private RunTracker GetOrCreateTracker(Guid runId, LiveDigestSnapshotDto? seed = null) =>
        _trackers.GetOrAdd(runId, id =>
        {
            var created = new RunTracker(id, _options.DefaultMaxContextTokens, seed);
            created.WorkerTask = Task.Run(() => RunNarratorWorkerAsync(created, _shutdownCts.Token));
            return created;
        });

    private async Task ProcessStreamChunkSafeAsync(RunTracker tracker, RunStreamChunk chunk)
    {
        try
        {
            var ct = _shutdownCts.Token;
            var immediateNarration = false;
            var semanticChange = false;
            GovernanceSignalDto? emittedSignal = null;
            SentinelAlertDto? emittedAlert = null;
            LiveDigestChapterDto? updatedChapter = null;

            lock (tracker.SyncRoot)
            {
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    tracker.AppendRawDelta(chunk.Delta);
                    var excerpt = LiveExplainerSemanticFolder.ExtractThoughtExcerpt(tracker.RawDeltaWindow);
                    if (!string.IsNullOrWhiteSpace(excerpt) && !string.Equals(excerpt, tracker.LatestThoughtExcerpt, StringComparison.Ordinal))
                    {
                        var deltaLen = Math.Abs(excerpt.Length - (tracker.LatestThoughtExcerpt?.Length ?? 0));
                        tracker.LatestThoughtExcerpt = excerpt;
                        updatedChapter = tracker.EnsureCurrentChapterLocked();
                        updatedChapter = updatedChapter with
                        {
                            RawThoughtExcerpt = excerpt,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        tracker.UpsertChapterLocked(updatedChapter);
                        if (deltaLen >= 48) semanticChange = true;
                    }
                }

                if (chunk.Usage is not null && TryExtractUsageTokens(chunk.Usage, out var usedTokens, out var maxTokens))
                {
                    var pressureTriggered = tracker.UpdateWaterlineLocked(usedTokens, maxTokens, chunk.OccurredAt == default ? DateTimeOffset.UtcNow : chunk.OccurredAt);
                    if (pressureTriggered)
                    {
                        (emittedSignal, emittedAlert) = tracker.RecordGovernanceSignalLocked(
                            GovernanceSignalCodes.ContextPressure,
                            "warning",
                            $"上下文占用已达 {tracker.ContextWaterline.UsagePercent:F1}%（{tracker.ContextWaterline.UsedTokens}/{tracker.ContextWaterline.MaxTokens} tokens）",
                            subjectId: null,
                            teammateHandle: null);
                        immediateNarration = true;
                        semanticChange = true;
                    }
                }

                if (string.Equals(chunk.Kind, "error", StringComparison.OrdinalIgnoreCase))
                {
                    tracker.Status = "failed";
                    var errText = chunk.SafeErrorMessage ?? chunk.ErrorCategory ?? "执行层报告运行异常";
                    var errLower = errText.ToLowerInvariant();
                    if (errLower.Contains("timeout"))
                    {
                        (emittedSignal, emittedAlert) = tracker.RecordGovernanceSignalLocked(GovernanceSignalCodes.Timeout, "warning", errText, null, null);
                    }
                    immediateNarration = true;
                    semanticChange = true;
                }
                else if (string.Equals(chunk.Kind, "done", StringComparison.OrdinalIgnoreCase))
                {
                    if (tracker.CurrentPhase != LiveDigestPhases.Conclusion)
                    {
                        tracker.CurrentPhase = LiveDigestPhases.Conclusion;
                        updatedChapter = tracker.EnsureCurrentChapterLocked();
                    }
                    immediateNarration = true;
                    semanticChange = true;
                }
            }

            if (chunk.Kind is "ack" or "assigned" or "control" or "ephemeral_agent" or "done" or "error")
            {
                await SyncDurableRunFactsAsync(tracker, ct).ConfigureAwait(false);
                semanticChange = true;
            }
            else if (updatedChapter is not null || emittedSignal is not null)
            {
                var frame = tracker.AdvanceAndBuildL0Delta(emittedSignal, emittedAlert, updatedChapter);
                BroadcastFrame(tracker, frame);
                await PersistSafeAsync(tracker, tracker.BuildSnapshot(), ct).ConfigureAwait(false);
            }

            if (semanticChange || tracker.Mode == LiveExplainerModes.Always)
            {
                tracker.EnqueueNarration(immediateNarration);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "LiveExplainer ignored L0 chunk processing exception for run {RunId}.", tracker.RunId);
        }
    }

    private async Task HandleRunSealedSafeAsync(RunTracker tracker)
    {
        try
        {
            var ct = _shutdownCts.Token;
            await SyncDurableRunFactsAsync(tracker, ct).ConfigureAwait(false);
            tracker.EnqueueNarration(immediate: true, isTerminalPass: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "LiveExplainer ignored seal processing exception for run {RunId}.", tracker.RunId);
        }
    }

    private async Task ApplyGovernanceSignalAsync(
        RunTracker tracker,
        string code,
        string severity,
        string summary,
        string? subjectId,
        string? teammateHandle)
    {
        try
        {
            GovernanceSignalDto? signal;
            SentinelAlertDto? alert;
            LiveDigestChapterDto? chapter;
            lock (tracker.SyncRoot)
            {
                (signal, alert) = tracker.RecordGovernanceSignalLocked(code, severity, summary, subjectId, teammateHandle);
                if (signal is null) return;
                chapter = tracker.EnsureCurrentChapterLocked();
                tracker.Headline = LiveExplainerSemanticFolder.BuildDefaultHeadline(
                    tracker.Status, tracker.CurrentPhase, chapter.ToolCalls, tracker.TeammateThoughts, tracker.GovernanceSignals);
            }

            var frame = tracker.AdvanceAndBuildL0Delta(signal, alert, chapter);
            BroadcastFrame(tracker, frame);
            await PersistSafeAsync(tracker, tracker.BuildSnapshot(), _shutdownCts.Token).ConfigureAwait(false);
            tracker.EnqueueNarration(immediate: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "LiveExplainer ignored governance signal {Code} for run {RunId}.", code, tracker.RunId);
        }
    }

    private async Task SyncDurableRunFactsAsync(RunTracker tracker, CancellationToken cancellationToken)
    {
        RunState runState;
        try
        {
            runState = await _lifecycle.GetRunStateAsync(tracker.RunId.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return;
        }

        if (IsObserverRunKind(runState))
        {
            ObserverRunScope.RecordSuppressedRecursiveSignal();
            return;
        }

        if (Guid.TryParse(runState.TenantId, out var tId)) tracker.TenantId = tId;
        if (Guid.TryParse(runState.WorkspaceId, out var wId)) tracker.WorkspaceId = wId;
        if (Guid.TryParse(runState.SessionId, out var sId)) tracker.SessionId = sId;

        var (mode, _, _) = await _store.GetSettingsAsync(
            tracker.TenantId == Guid.Empty ? null : tracker.TenantId,
            tracker.WorkspaceId == Guid.Empty ? null : tracker.WorkspaceId,
            cancellationToken).ConfigureAwait(false);

        FullDuplexCheckpointV1? checkpoint = null;
        try
        {
            var cpRow = await _lifecycle.GetCurrentRunCheckpointAsync(tracker.RunId.ToString(), cancellationToken).ConfigureAwait(false);
            if (cpRow is not null && !string.IsNullOrWhiteSpace(cpRow.Content))
            {
                checkpoint = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(cpRow.Content, CheckpointJsonOptions);
            }
        }
        catch { /* ignore malformed intermediate checkpoints */ }

        IReadOnlyList<ToolExecutionSnapshot> executions = [];
        if (_toolExecutions is not null)
        {
            try
            {
                var rawList = await _toolExecutions.ListForRunAsync(tracker.RunId, limit: 120, cancellationToken: cancellationToken).ConfigureAwait(false);
                executions = rawList.OrderBy(x => x.CreatedAt).ThenBy(x => x.Attempt).ToArray();
            }
            catch { /* optional port */ }
        }

        IReadOnlyList<TinadecCore.Contracts.Events.EventEnvelope> newEvents = [];
        if (tracker.SessionId != Guid.Empty)
        {
            try
            {
                var replayed = await _lifecycle.ReplayEventsAsync(tracker.SessionId, tracker.LastEventCursor, cancellationToken).ConfigureAwait(false);
                newEvents = replayed
                    .Where(e => string.Equals(e.RunId, tracker.RunId.ToString(), StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (replayed.Count > 0)
                {
                    tracker.LastEventCursor += replayed.Count;
                }
            }
            catch { /* ignore event replay errors */ }
        }

        string? sessionPlanHint = null;
        if (_sessionPlans is not null && tracker.SessionId != Guid.Empty)
        {
            try
            {
                await using var planDb = await _sessionPlans.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var planRow = await planDb.SessionPlans.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.SessionId == tracker.SessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (planRow is not null)
                {
                    sessionPlanHint = $"{planRow.Title} [{planRow.Status}]";
                }
            }
            catch { /* optional plan context */ }
        }

        var changed = false;
        var immediateL1 = false;
        GovernanceSignalDto? latestSignal = null;
        SentinelAlertDto? latestAlert = null;
        LiveDigestChapterDto? touchedChapter = null;

        lock (tracker.SyncRoot)
        {
            tracker.Mode = mode;
            if (mode == LiveExplainerModes.Off)
            {
                tracker.ExplainerActive = false;
            }
            if (!string.IsNullOrWhiteSpace(sessionPlanHint))
            {
                tracker.SessionPlanSummary = sessionPlanHint;
            }
            if (checkpoint is not null && !string.IsNullOrWhiteSpace(checkpoint.UserGoal))
            {
                tracker.UserGoal = checkpoint.UserGoal;
            }

            var mappedStatus = MapRunStatus(runState.Status);
            if (!string.Equals(tracker.Status, mappedStatus, StringComparison.Ordinal))
            {
                tracker.Status = mappedStatus;
                changed = true;
                if (IsTerminalStatus(mappedStatus))
                {
                    tracker.CurrentPhase = LiveDigestPhases.Conclusion;
                    touchedChapter = tracker.EnsureCurrentChapterLocked();
                    immediateL1 = true;
                }
            }

            if (checkpoint?.ModelUsage is { } cpUsage)
            {
                var used = (cpUsage.TotalTokens ?? 0L) > 0
                    ? cpUsage.TotalTokens!.Value
                    : (cpUsage.InputTokens ?? 0L) + (cpUsage.OutputTokens ?? 0L);
                if (used > 0)
                {
                    var pressured = tracker.UpdateWaterlineLocked((int)Math.Min(int.MaxValue, used), null, DateTimeOffset.UtcNow);
                    changed = true;
                    if (pressured)
                    {
                        (latestSignal, latestAlert) = tracker.RecordGovernanceSignalLocked(
                            GovernanceSignalCodes.ContextPressure,
                            "warning",
                            $"上下文水位达到 {tracker.ContextWaterline.UsagePercent:F1}%",
                            null,
                            null);
                        immediateL1 = true;
                    }
                }
            }

            // Process lifecycle events (compaction points, governance red-lines, teammate direct control).
            foreach (var evt in newEvents)
            {
                var lowerType = evt.EventType.ToLowerInvariant();
                if (lowerType.Contains("compact"))
                {
                    var beforeTok = TryGetPayloadInt(evt.Payload, "before_tokens") ?? tracker.ContextWaterline.UsedTokens;
                    var afterTok = TryGetPayloadInt(evt.Payload, "after_tokens") ?? Math.Max(0, beforeTok / 3);
                    tracker.AddCompactionPointLocked(beforeTok, afterTok, evt.Timestamp, TryGetPayloadString(evt.Payload, "summary") ?? "已压缩历史工具上下文");
                    changed = true;
                }

                var evtSignal = LiveExplainerSemanticFolder.ClassifyLifecycleEvent(evt.EventType, TryGetPayloadString(evt.Payload, "summary"), evt.Payload);
                if (evtSignal is not null)
                {
                    var (sig, alert) = tracker.RecordGovernanceSignalLocked(
                        evtSignal.Value.Code, evtSignal.Value.Severity, evtSignal.Value.Summary,
                        TryGetPayloadString(evt.Payload, "task_id"),
                        TryGetPayloadString(evt.Payload, "handle"));
                    if (sig is not null)
                    {
                        latestSignal = sig;
                        latestAlert = alert;
                        changed = true;
                        immediateL1 = true;
                    }
                }
            }

            // Fold tool executions & detect SAME_ARGS_RETRY.
            foreach (var exec in executions)
            {
                var stateFingerprint = $"{exec.Status}:{exec.Attempt}:{exec.CompletedAt?.UtcTicks ?? 0}";
                if (tracker.ObservedToolExecutions.TryGetValue(exec.Id, out var prevFingerprint)
                    && string.Equals(prevFingerprint, stateFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                var isFirstObservation = !tracker.ObservedToolExecutions.ContainsKey(exec.Id);
                tracker.ObservedToolExecutions[exec.Id] = stateFingerprint;
                changed = true;

                var folded = LiveExplainerSemanticFolder.FoldToolExecution(exec);
                tracker.PushSemanticLineLocked(folded.SemanticLine);

                if (isFirstObservation && !string.IsNullOrWhiteSpace(exec.ParametersHash))
                {
                    var argsKey = $"{exec.ToolId}:{exec.ParametersHash}";
                    if (string.Equals(tracker.LastToolArgsKey, argsKey, StringComparison.Ordinal))
                    {
                        tracker.ConsecutiveSameArgsCount++;
                        if (tracker.ConsecutiveSameArgsCount >= 2)
                        {
                            var (sig, alert) = tracker.RecordGovernanceSignalLocked(
                                GovernanceSignalCodes.SameArgsRetry,
                                "warning",
                                $"工具 {exec.ToolId} 连续 {tracker.ConsecutiveSameArgsCount} 次使用完全相同的参数调用",
                                exec.Id.ToString("N"),
                                null);
                            if (sig is not null)
                            {
                                latestSignal = sig;
                                latestAlert = alert;
                                immediateL1 = true;
                            }
                        }
                    }
                    else
                    {
                        tracker.LastToolArgsKey = argsKey;
                        tracker.ConsecutiveSameArgsCount = 1;
                    }
                }

                if (folded.IsFailure)
                {
                    tracker.ConsecutiveToolFailures++;
                    immediateL1 = true;
                }
                else if (folded.ToolCall.Status == "succeeded")
                {
                    tracker.ConsecutiveToolFailures = 0;
                }

                if (folded.DetectedSignalCode is not null)
                {
                    var (sig, alert) = tracker.RecordGovernanceSignalLocked(
                        folded.DetectedSignalCode,
                        folded.DetectedSignalCode == GovernanceSignalCodes.LoopDetected ? "critical" : "warning",
                        folded.SignalSummary ?? folded.SemanticLine,
                        exec.Id.ToString("N"),
                        null);
                    if (sig is not null)
                    {
                        latestSignal = sig;
                        latestAlert = alert;
                        immediateL1 = true;
                    }
                }

                if (PhaseOrder(folded.InferredPhase) > PhaseOrder(tracker.CurrentPhase) && !IsTerminalStatus(tracker.Status))
                {
                    tracker.CurrentPhase = folded.InferredPhase;
                    immediateL1 = true;
                }

                touchedChapter = tracker.AppendToolToChapterLocked(folded.ToolCall, folded.TouchedFile);
            }

            // Project active & waiting teammates from checkpoint + board.
            if (checkpoint is not null && checkpoint.Teammates.Count > 0)
            {
                var updatedTeammates = BuildTeammateThoughtsLocked(tracker, checkpoint);
                if (!TeammatesEqual(tracker.TeammateThoughts, updatedTeammates))
                {
                    tracker.TeammateThoughts = updatedTeammates;
                    changed = true;
                }
            }

            if (tracker.Chapters.Count == 0)
            {
                touchedChapter = tracker.EnsureCurrentChapterLocked();
                changed = true;
            }

            if (changed && (!tracker.ExplainerActive || string.IsNullOrWhiteSpace(tracker.Headline)))
            {
                var lastChapter = tracker.Chapters[^1];
                tracker.Headline = LiveExplainerSemanticFolder.BuildDefaultHeadline(
                    tracker.Status,
                    tracker.CurrentPhase,
                    lastChapter.ToolCalls,
                    tracker.TeammateThoughts,
                    tracker.GovernanceSignals);
            }
        }

        if (changed)
        {
            var frame = tracker.AdvanceAndBuildL0Delta(latestSignal, latestAlert, touchedChapter);
            BroadcastFrame(tracker, frame);
            await PersistSafeAsync(tracker, tracker.BuildSnapshot(), cancellationToken).ConfigureAwait(false);

            if (tracker.Mode != LiveExplainerModes.Off)
            {
                tracker.EnqueueNarration(immediateL1, IsTerminalStatus(tracker.Status));
            }
            else if (IsTerminalStatus(tracker.Status))
            {
                EmitDoneFrame(tracker);
            }
        }
    }

    private async Task RunNarratorWorkerAsync(RunTracker tracker, CancellationToken cancellationToken)
    {
        try
        {
            while (await tracker.NarrationQueue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!tracker.NarrationQueue.Reader.TryRead(out var item)) continue;
                var immediate = item.Immediate;
                var terminal = item.IsTerminal;

                if (!immediate && _options.DebounceWindow > TimeSpan.Zero)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    delayCts.CancelAfter(_options.DebounceWindow);
                    try
                    {
                        while (await tracker.NarrationQueue.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                        {
                            while (tracker.NarrationQueue.Reader.TryRead(out var coalesced))
                            {
                                immediate |= coalesced.Immediate;
                                terminal |= coalesced.IsTerminal;
                            }
                            if (immediate) break;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Debounce window elapsed normally.
                    }
                }
                else
                {
                    while (tracker.NarrationQueue.Reader.TryRead(out var drained))
                    {
                        terminal |= drained.IsTerminal;
                    }
                }

                try
                {
                    await ExecuteLeadNarratorPassAsync(tracker, terminal, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    tracker.CompletePendingNarrationPass();
                }

                if (terminal && IsTerminalStatus(tracker.Status))
                {
                    EmitDoneFrame(tracker);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LiveExplainer background worker stopped gracefully for run {RunId}.", tracker.RunId);
        }
    }

    private async Task ExecuteLeadNarratorPassAsync(RunTracker tracker, bool isTerminalPass, CancellationToken cancellationToken)
    {
        var (mode, _, _) = await _store.GetSettingsAsync(
            tracker.TenantId == Guid.Empty ? null : tracker.TenantId,
            tracker.WorkspaceId == Guid.Empty ? null : tracker.WorkspaceId,
            cancellationToken).ConfigureAwait(false);

        List<TouchedFileStatDto> touchedSnapshot;
        List<GovernanceSignalDto> signalsSnapshot;
        List<TeammateThoughtDto> teammatesSnapshot;
        List<string> semanticWindow;
        string? rawThought;
        string currentPhase;
        string status;
        string userGoal;
        string? planSummary;
        int consecutiveFailures;
        long currentSeq;

        lock (tracker.SyncRoot)
        {
            tracker.Mode = mode;
            if (mode == LiveExplainerModes.Off)
            {
                tracker.ExplainerActive = false;
                return;
            }

            currentSeq = tracker.CurrentSeq;
            if (mode == LiveExplainerModes.Auto && !isTerminalPass && currentSeq == tracker.LastNarratedSeq)
            {
                return;
            }

            touchedSnapshot = [.. tracker.TouchedFiles];
            signalsSnapshot = [.. tracker.GovernanceSignals];
            teammatesSnapshot = [.. tracker.TeammateThoughts];
            semanticWindow = [.. tracker.SemanticWindow];
            rawThought = tracker.LatestThoughtExcerpt;
            currentPhase = tracker.CurrentPhase;
            status = tracker.Status;
            userGoal = tracker.UserGoal;
            planSummary = tracker.SessionPlanSummary;
            consecutiveFailures = tracker.ConsecutiveToolFailures;
        }

        // L2: On-demand Ephemeral Read-Only Scouts (0-2 max) only for hard/complex slices.
        var scoutAssignments = LiveExplainerSemanticFolder.BuildScoutAssignments(
            touchedSnapshot, signalsSnapshot, teammatesSnapshot, semanticWindow, consecutiveFailures);

        string? scoutNote = null;
        if (scoutAssignments.Count > 0)
        {
            scoutNote = await _scoutRunner.RunScoutsAsync(tracker.RunId, scoutAssignments, cancellationToken).ConfigureAwait(false);
        }

        var promptBuilder = new StringBuilder(1024);
        promptBuilder.AppendLine($"run_status: {status}");
        promptBuilder.AppendLine($"current_phase: {currentPhase}");
        if (!string.IsNullOrWhiteSpace(userGoal))
            promptBuilder.AppendLine($"user_goal: {LiveExplainerSemanticFolder.TruncateSingleLine(userGoal, 200)}");
        if (!string.IsNullOrWhiteSpace(planSummary))
            promptBuilder.AppendLine($"session_plan: {planSummary}");
        if (!string.IsNullOrWhiteSpace(rawThought))
            promptBuilder.AppendLine($"latest_thought: {rawThought}");
        if (semanticWindow.Count > 0)
        {
            promptBuilder.AppendLine("recent_actions:");
            foreach (var line in semanticWindow.TakeLast(10))
                promptBuilder.AppendLine($"- {line}");
        }
        if (touchedSnapshot.Count > 0)
        {
            promptBuilder.AppendLine("touched_files:");
            foreach (var file in touchedSnapshot.Take(8))
                promptBuilder.AppendLine($"- {file.Path} (+{file.AddedLines}/-{file.RemovedLines})");
        }
        if (teammatesSnapshot.Count > 0)
        {
            promptBuilder.AppendLine("teammates:");
            foreach (var tm in teammatesSnapshot.Take(6))
                promptBuilder.AppendLine($"- @{tm.Handle} [{tm.Status}] task={tm.CurrentTask ?? "none"} note={tm.Thought}");
        }
        if (signalsSnapshot.Count > 0)
        {
            promptBuilder.AppendLine("governance_signals:");
            foreach (var sig in signalsSnapshot.TakeLast(4))
                promptBuilder.AppendLine($"- {sig.Code}: {sig.Summary}");
        }
        if (!string.IsNullOrWhiteSpace(scoutNote))
        {
            promptBuilder.AppendLine($"scout_condensed_facts: {scoutNote}");
        }

        using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        modelCts.CancelAfter(_options.ModelCallTimeout);

        ObserverModelResult result;
        try
        {
            using var observerGuard = ObserverRunScope.Enter(tracker.RunId);
            result = await _modelInvoker.InvokeObserverAsync(new ObserverModelInvocation
            {
                ObservedRunId = tracker.RunId,
                Role = "lead_narrator",
                IsObserverRun = true,
                SystemPrompt = LeadNarratorSystemPrompt,
                UserPrompt = promptBuilder.ToString(),
                MaxOutputTokens = 512
            }, modelCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Iron Law #2: 0 blocking, 0 exceptions onto the run when model times out or errors.
            return;
        }

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Text))
        {
            return;
        }

        var parsed = ParseLeadNarration(result.Text);
        LiveDigestChapterDto updatedChapter;
        SentinelAlertDto? addedSentinel = null;

        lock (tracker.SyncRoot)
        {
            tracker.ExplainerActive = true;
            tracker.ExplainerModel = result.ModelId;
            tracker.LastNarratedSeq = tracker.CurrentSeq + 1;

            if (!string.IsNullOrWhiteSpace(parsed.Phase))
            {
                var normalizedPhase = LiveDigestPhases.Normalize(parsed.Phase);
                if (PhaseOrder(normalizedPhase) >= PhaseOrder(tracker.CurrentPhase))
                {
                    tracker.CurrentPhase = normalizedPhase;
                }
            }

            if (!string.IsNullOrWhiteSpace(parsed.Headline))
            {
                tracker.Headline = LiveExplainerSemanticFolder.TruncateSingleLine(parsed.Headline, 120);
            }

            var baseChapter = tracker.EnsureCurrentChapterLocked();
            updatedChapter = baseChapter with
            {
                Title = !string.IsNullOrWhiteSpace(parsed.ChapterTitle)
                    ? LiveExplainerSemanticFolder.TruncateSingleLine(parsed.ChapterTitle, 64)
                    : baseChapter.Title,
                Narrative = !string.IsNullOrWhiteSpace(parsed.Narrative)
                    ? LiveExplainerSemanticFolder.TruncateSingleLine(parsed.Narrative, 360)
                    : baseChapter.Narrative,
                Source = !string.IsNullOrWhiteSpace(scoutNote) ? "l2_scout" : "l1_narrator",
                ScoutNote = scoutNote ?? baseChapter.ScoutNote,
                RawThoughtExcerpt = tracker.LatestThoughtExcerpt ?? baseChapter.RawThoughtExcerpt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            tracker.UpsertChapterLocked(updatedChapter);

            if (parsed.TeammateThoughts is { Count: > 0 } && tracker.TeammateThoughts.Count > 0)
            {
                var thoughtByHandle = parsed.TeammateThoughts
                    .Where(x => !string.IsNullOrWhiteSpace(x.Handle) && !string.IsNullOrWhiteSpace(x.Thought))
                    .ToDictionary(x => x.Handle.TrimStart('@'), x => x.Thought, StringComparer.OrdinalIgnoreCase);

                tracker.TeammateThoughts = tracker.TeammateThoughts
                    .Select(tm => thoughtByHandle.TryGetValue(tm.Handle.TrimStart('@'), out var customThought)
                        ? tm with { Thought = LiveExplainerSemanticFolder.TruncateSingleLine(customThought, 90), UpdatedAt = DateTimeOffset.UtcNow }
                        : tm)
                    .ToList();
            }

            if (parsed.Sentinel is { } s && !string.IsNullOrWhiteSpace(s.SuggestedSteerPrompt))
            {
                var code = GovernanceSignalCodes.All.Contains(s.Code ?? string.Empty)
                    ? s.Code!
                    : GovernanceSignalCodes.LoopDetected;
                if (tracker.SentinelAlerts.All(existing => !string.Equals(existing.Code, code, StringComparison.Ordinal)))
                {
                    addedSentinel = new SentinelAlertDto
                    {
                        Id = $"sentinel-l1-{DateTimeOffset.UtcNow.UtcTicks:x}",
                        Code = code,
                        Severity = "warning",
                        Title = FirstNonEmpty(s.Title, "解说哨兵温和建议"),
                        Message = FirstNonEmpty(s.Message, "检测到可优化的执行轨迹，建议发送纠偏提示。"),
                        SuggestedSteerPrompt = LiveExplainerSemanticFolder.TruncateSingleLine(s.SuggestedSteerPrompt, 180),
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    tracker.SentinelAlerts.Add(addedSentinel);
                }
            }
        }

        var narrationFrame = tracker.AdvanceAndBuildL1Narration(updatedChapter, addedSentinel);
        BroadcastFrame(tracker, narrationFrame);
        await PersistSafeAsync(tracker, tracker.BuildSnapshot(), cancellationToken).ConfigureAwait(false);
    }

    private void EmitDoneFrame(RunTracker tracker)
    {
        LiveDigestStreamFrame? doneFrame = null;
        lock (tracker.SyncRoot)
        {
            if (tracker.DoneEmitted) return;
            tracker.DoneEmitted = true;
            var snapshot = tracker.BuildSnapshotLocked();
            doneFrame = new LiveDigestStreamFrame
            {
                Seq = snapshot.Seq,
                EventKind = "done",
                Payload = snapshot,
                OccurredAt = DateTimeOffset.UtcNow
            };
            tracker.RecordFrameLocked(doneFrame);
        }

        if (doneFrame is not null)
        {
            BroadcastFrame(tracker, doneFrame);
        }
    }

    private static void BroadcastFrame(RunTracker tracker, LiveDigestStreamFrame frame)
    {
        foreach (var (_, writer) in tracker.Subscribers)
        {
            writer.TryWrite(frame);
        }
    }

    private async Task PersistSafeAsync(RunTracker tracker, LiveDigestSnapshotDto snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveSnapshotAsync(tracker.TenantId, tracker.WorkspaceId, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "LiveExplainer skipped snapshot persistence for run {RunId}.", tracker.RunId);
        }
    }

    private static List<TeammateThoughtDto> BuildTeammateThoughtsLocked(RunTracker tracker, FullDuplexCheckpointV1 checkpoint)
    {
        var existingByHandle = tracker.TeammateThoughts.ToDictionary(t => t.Handle, StringComparer.OrdinalIgnoreCase);
        var taskTitleById = checkpoint.Tasks.ToDictionary(t => t.TaskId, t => t.Title);
        var latestBoardBySender = checkpoint.TeamBoard
            .GroupBy(m => m.Sender, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Seq).First().Content, StringComparer.OrdinalIgnoreCase);

        var list = new List<TeammateThoughtDto>(checkpoint.Teammates.Count);
        foreach (var tm in checkpoint.Teammates)
        {
            if (string.IsNullOrWhiteSpace(tm.Handle)) continue;
            var currentTask = tm.CurrentTaskId.HasValue && taskTitleById.TryGetValue(tm.CurrentTaskId.Value, out var title)
                ? title
                : null;

            string fallbackThought;
            if (latestBoardBySender.TryGetValue(tm.Handle, out var boardMsg) && !string.IsNullOrWhiteSpace(boardMsg))
            {
                fallbackThought = LiveExplainerSemanticFolder.TruncateSingleLine(boardMsg, 84);
            }
            else if (!string.IsNullOrWhiteSpace(tm.LastOutcomeSummary))
            {
                fallbackThought = LiveExplainerSemanticFolder.TruncateSingleLine(tm.LastOutcomeSummary, 84);
            }
            else if (!string.IsNullOrWhiteSpace(currentTask))
            {
                fallbackThought = $"正在推进：{LiveExplainerSemanticFolder.TruncateSingleLine(currentTask, 64)}";
            }
            else
            {
                fallbackThought = tm.Status == "waiting" ? "在看板待命，随时响应唤醒或插话指令" : "正在执行分配的子任务";
            }

            var thought = existingByHandle.TryGetValue(tm.Handle, out var prev) && tracker.ExplainerActive && !string.IsNullOrWhiteSpace(prev.Thought)
                ? prev.Thought
                : fallbackThought;

            list.Add(new TeammateThoughtDto
            {
                Handle = tm.Handle,
                DisplayName = tm.Handle,
                Status = string.IsNullOrWhiteSpace(tm.Status) ? "working" : tm.Status.ToLowerInvariant(),
                CurrentTask = currentTask,
                Thought = thought,
                UpdatedAt = tm.UpdatedAt
            });
        }

        return list;
    }

    private static bool TeammatesEqual(IReadOnlyList<TeammateThoughtDto> a, IReadOnlyList<TeammateThoughtDto> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Handle, b[i].Handle, StringComparison.Ordinal)
                || !string.Equals(a[i].Status, b[i].Status, StringComparison.Ordinal)
                || !string.Equals(a[i].CurrentTask, b[i].CurrentTask, StringComparison.Ordinal)
                || !string.Equals(a[i].Thought, b[i].Thought, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static ParsedLeadNarration ParseLeadNarration(string rawText)
    {
        var trimmed = rawText.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        var startBrace = trimmed.IndexOf('{');
        var endBrace = trimmed.LastIndexOf('}');
        if (startBrace >= 0 && endBrace > startBrace)
        {
            var jsonCandidate = trimmed[startBrace..(endBrace + 1)];
            try
            {
                var parsed = JsonSerializer.Deserialize<ParsedLeadNarration>(jsonCandidate, CheckpointJsonOptions);
                if (parsed is not null) return parsed;
            }
            catch (JsonException)
            {
                // Fall back to plain text narrative below.
            }
        }

        return new ParsedLeadNarration
        {
            Headline = LiveExplainerSemanticFolder.TruncateSingleLine(trimmed, 60),
            Narrative = LiveExplainerSemanticFolder.TruncateSingleLine(trimmed, 300)
        };
    }

    private static bool TryExtractUsageTokens(object usage, out int usedTokens, out int? maxTokens)
    {
        usedTokens = 0;
        maxTokens = null;
        try
        {
            var json = usage is JsonElement el ? el : JsonSerializer.SerializeToElement(usage, CheckpointJsonOptions);
            if (json.ValueKind != JsonValueKind.Object) return false;

            int? ReadInt(params string[] names)
            {
                foreach (var n in names)
                {
                    if (json.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
                        return v;
                }
                return null;
            }

            var total = ReadInt("used_tokens", "total_tokens", "totalTokens");
            var input = ReadInt("input_tokens", "inputTokens", "prompt_tokens");
            var output = ReadInt("output_tokens", "outputTokens", "completion_tokens");
            maxTokens = ReadInt("max_tokens", "maxTokens", "context_window");

            usedTokens = total ?? ((input ?? 0) + (output ?? 0));
            return usedTokens > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetPayloadString(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var raw) || raw is null) return null;
        return raw is JsonElement el
            ? (el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString())
            : raw.ToString();
    }

    private static int? TryGetPayloadInt(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var raw) || raw is null) return null;
        if (raw is int i) return i;
        if (raw is long l) return (int)l;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var parsed)) return parsed;
        return int.TryParse(raw.ToString(), out var textInt) ? textInt : null;
    }

    private static bool IsObserverRunKind(RunState state) =>
        string.Equals(state.RunKind, "observer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state.RunKind, "live_explainer", StringComparison.OrdinalIgnoreCase);

    private static string MapRunStatus(RunStatus status) => status switch
    {
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        RunStatus.AwaitingApproval => "awaiting_approval",
        _ => "running"
    };

    private static bool IsTerminalStatus(string? status) =>
        status is "completed" or "failed" or "cancelled";

    private static int PhaseOrder(string phase) => phase switch
    {
        LiveDigestPhases.Verification => 2,
        LiveDigestPhases.Conclusion => 3,
        _ => 1
    };

    private static string FirstNonEmpty(string? a, string fallback) =>
        string.IsNullOrWhiteSpace(a) ? fallback : a.Trim();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_projection is not null)
        {
            _projection.ChunkObserved -= OnChunkObserved;
            _projection.RunSealed -= OnRunSealed;
        }

        if (_governanceSink is not null)
        {
            _governanceSink.SignalObserved -= OnGovernanceSignalObserved;
        }

        _shutdownCts.Cancel();
        foreach (var (_, tracker) in _trackers)
        {
            tracker.NarrationQueue.Writer.TryComplete();
            foreach (var (_, sub) in tracker.Subscribers)
            {
                sub.TryComplete();
            }
        }
        _shutdownCts.Dispose();
    }

    private readonly record struct NarrationWorkItem(bool Immediate, bool IsTerminal);

    private sealed class ParsedLeadNarration
    {
        public string? Headline { get; init; }
        public string? Phase { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("chapter_title")]
        public string? ChapterTitle { get; init; }
        public string? Narrative { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("teammate_thoughts")]
        public List<ParsedTeammateThought>? TeammateThoughts { get; init; }
        public ParsedSentinel? Sentinel { get; init; }
    }

    private sealed class ParsedTeammateThought
    {
        public string Handle { get; init; } = string.Empty;
        public string Thought { get; init; } = string.Empty;
    }

    private sealed class ParsedSentinel
    {
        public string? Code { get; init; }
        public string? Title { get; init; }
        public string? Message { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("suggested_steer_prompt")]
        public string? SuggestedSteerPrompt { get; init; }
    }

    private sealed class RunTracker
    {
        private const int MaxRawDeltaChars = 6000;
        private const int MaxRetainedFrames = 128;

        public readonly object SyncRoot = new();
        public Guid RunId { get; }
        public Guid TenantId { get; set; }
        public Guid WorkspaceId { get; set; }
        public Guid SessionId { get; set; }
        public string Status { get; set; } = "running";
        public string Mode { get; set; } = LiveExplainerModes.Auto;
        public bool ExplainerActive { get; set; }
        public string? ExplainerModel { get; set; }
        public long CurrentSeq { get; private set; }
        public long LastNarratedSeq { get; set; }
        public long LastEventCursor { get; set; }
        public string Headline { get; set; } = "正在准备运行上下文与执行计划";
        public string CurrentPhase { get; set; } = LiveDigestPhases.Hypothesis;
        public string UserGoal { get; set; } = string.Empty;
        public string? SessionPlanSummary { get; set; }
        public string? LatestThoughtExcerpt { get; set; }
        public ContextWaterlineDto ContextWaterline { get; private set; }
        public List<GovernanceSignalDto> GovernanceSignals { get; } = [];
        public List<TeammateThoughtDto> TeammateThoughts { get; set; } = [];
        public List<LiveDigestChapterDto> Chapters { get; } = [];
        public List<TouchedFileStatDto> TouchedFiles { get; private set; } = [];
        public List<SentinelAlertDto> SentinelAlerts { get; } = [];
        public List<string> SemanticWindow { get; } = [];
        public Dictionary<Guid, string> ObservedToolExecutions { get; } = [];
        public string? LastToolArgsKey { get; set; }
        public int ConsecutiveSameArgsCount { get; set; }
        public int ConsecutiveToolFailures { get; set; }
        public bool ContextPressureRaised { get; set; }
        public bool DoneEmitted { get; set; }
        public Task? WorkerTask { get; set; }

        public Channel<NarrationWorkItem> NarrationQueue { get; } = Channel.CreateBounded<NarrationWorkItem>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public ConcurrentDictionary<Guid, ChannelWriter<LiveDigestStreamFrame>> Subscribers { get; } = new();

        private readonly StringBuilder _rawDelta = new();
        private readonly List<LiveDigestStreamFrame> _recentFrames = [];
        private int _pendingNarrationCount;

        public bool HasPendingNarration => Volatile.Read(ref _pendingNarrationCount) > 0;

        public string RawDeltaWindow => _rawDelta.ToString();

        public RunTracker(Guid runId, int defaultMaxTokens, LiveDigestSnapshotDto? seed)
        {
            RunId = runId;
            ContextWaterline = new ContextWaterlineDto { MaxTokens = defaultMaxTokens };

            if (seed is not null)
            {
                SessionId = seed.SessionId;
                Status = seed.Status;
                Mode = seed.Mode;
                ExplainerActive = seed.ExplainerActive;
                ExplainerModel = seed.ExplainerModel;
                CurrentSeq = seed.Seq;
                Headline = seed.Headline;
                CurrentPhase = seed.CurrentPhase;
                ContextWaterline = seed.ContextWaterline;
                GovernanceSignals.AddRange(seed.GovernanceSignals);
                TeammateThoughts = [.. seed.TeammateThoughts];
                Chapters.AddRange(seed.Chapters);
                TouchedFiles = [.. seed.TouchedFiles];
                SentinelAlerts.AddRange(seed.SentinelAlerts);
            }
        }

        public void EnqueueNarration(bool immediate, bool isTerminalPass = false)
        {
            Interlocked.Increment(ref _pendingNarrationCount);
            if (!NarrationQueue.Writer.TryWrite(new NarrationWorkItem(immediate, isTerminalPass)))
            {
                Interlocked.Decrement(ref _pendingNarrationCount);
            }
        }

        public void CompletePendingNarrationPass()
        {
            while (true)
            {
                var current = Volatile.Read(ref _pendingNarrationCount);
                if (current <= 0) return;
                if (Interlocked.CompareExchange(ref _pendingNarrationCount, current - 1, current) == current) return;
            }
        }

        public void MarkCompleted()
        {
            NarrationQueue.Writer.TryComplete();
        }

        public void AppendRawDelta(string delta)
        {
            _rawDelta.Append(delta);
            if (_rawDelta.Length > MaxRawDeltaChars)
            {
                _rawDelta.Remove(0, _rawDelta.Length - MaxRawDeltaChars);
            }
        }

        public void PushSemanticLineLocked(string line)
        {
            SemanticWindow.Add(line);
            if (SemanticWindow.Count > 16)
            {
                SemanticWindow.RemoveRange(0, SemanticWindow.Count - 16);
            }
        }

        public bool UpdateWaterlineLocked(int usedTokens, int? maxTokensOverride, DateTimeOffset occurredAt)
        {
            var maxTokens = maxTokensOverride is > 0 ? maxTokensOverride.Value : ContextWaterline.MaxTokens;
            var previousUsed = ContextWaterline.UsedTokens;
            var points = ContextWaterline.CompactionPoints.ToList();

            // Auto-detect implicit context compaction drop (>= 25% reduction from >= 8000 tokens).
            if (previousUsed >= 8_000 && usedTokens > 0 && usedTokens <= (int)(previousUsed * 0.75))
            {
                points.Add(new CompactionPointDto
                {
                    Seq = CurrentSeq + 1,
                    BeforeTokens = previousUsed,
                    AfterTokens = usedTokens,
                    OccurredAt = occurredAt,
                    Summary = $"上下文自动压缩：{previousUsed} → {usedTokens} tokens"
                });
            }

            var pct = maxTokens > 0 ? Math.Round(Math.Clamp(usedTokens * 100.0 / maxTokens, 0.0, 100.0), 1) : 0.0;
            ContextWaterline = new ContextWaterlineDto
            {
                UsedTokens = usedTokens,
                MaxTokens = maxTokens,
                UsagePercent = pct,
                CompactionCount = points.Count,
                CompactionPoints = points
            };

            if (!ContextPressureRaised && pct >= LiveExplainerSemanticFolder.ContextPressureThresholdPercent)
            {
                ContextPressureRaised = true;
                return true;
            }

            return false;
        }

        public void AddCompactionPointLocked(int beforeTokens, int afterTokens, DateTimeOffset occurredAt, string summary)
        {
            var points = ContextWaterline.CompactionPoints.ToList();
            points.Add(new CompactionPointDto
            {
                Seq = CurrentSeq + 1,
                BeforeTokens = beforeTokens,
                AfterTokens = afterTokens,
                OccurredAt = occurredAt,
                Summary = summary
            });
            var pct = ContextWaterline.MaxTokens > 0
                ? Math.Round(Math.Clamp(afterTokens * 100.0 / ContextWaterline.MaxTokens, 0.0, 100.0), 1)
                : 0.0;
            ContextWaterline = ContextWaterline with
            {
                UsedTokens = afterTokens,
                UsagePercent = pct,
                CompactionCount = points.Count,
                CompactionPoints = points
            };
        }

        public (GovernanceSignalDto? Signal, SentinelAlertDto? Alert) RecordGovernanceSignalLocked(
            string code,
            string severity,
            string summary,
            string? subjectId,
            string? teammateHandle)
        {
            // Deduplicate identical consecutive signal code + subject within 5 seconds.
            var now = DateTimeOffset.UtcNow;
            if (GovernanceSignals.Count > 0)
            {
                var last = GovernanceSignals[^1];
                if (string.Equals(last.Code, code, StringComparison.Ordinal)
                    && string.Equals(last.SubjectId, subjectId, StringComparison.Ordinal)
                    && (now - last.OccurredAt).TotalSeconds < 5)
                {
                    return (null, null);
                }
            }

            var signal = new GovernanceSignalDto
            {
                Id = $"sig-{GovernanceSignals.Count + 1}-{now.UtcTicks:x}",
                Code = code,
                Severity = severity,
                Summary = LiveExplainerSemanticFolder.TruncateSingleLine(summary, 180),
                SubjectId = subjectId,
                OccurredAt = now
            };
            GovernanceSignals.Add(signal);

            SentinelAlertDto? alert = null;
            if (SentinelAlerts.All(a => !string.Equals(a.Code, code, StringComparison.Ordinal) || (now - a.CreatedAt).TotalSeconds >= 20))
            {
                alert = LiveExplainerSemanticFolder.CreateSentinelAlert(code, summary, teammateHandle, now);
                SentinelAlerts.Add(alert);
            }

            return (signal, alert);
        }

        public LiveDigestChapterDto EnsureCurrentChapterLocked()
        {
            if (Chapters.Count > 0 && string.Equals(Chapters[^1].Phase, CurrentPhase, StringComparison.Ordinal))
            {
                return Chapters[^1];
            }

            var idx = Chapters.Count + 1;
            var now = DateTimeOffset.UtcNow;
            var created = new LiveDigestChapterDto
            {
                Id = $"ch-{idx}",
                Index = idx,
                Phase = CurrentPhase,
                Title = LiveExplainerSemanticFolder.BuildDefaultPhaseTitle(CurrentPhase, idx),
                Narrative = CurrentPhase switch
                {
                    LiveDigestPhases.Verification => "正在根据定位结论实施改动，并通过工具回执验证正确性。",
                    LiveDigestPhases.Conclusion => "正在汇总验证证据并收口最终结果。",
                    _ => "正在收集上下文事实、读取关联代码并建立初步假设。"
                },
                Source = "l0_structural",
                RawThoughtExcerpt = LatestThoughtExcerpt,
                StartedAt = now,
                UpdatedAt = now
            };
            Chapters.Add(created);
            return created;
        }

        public void UpsertChapterLocked(LiveDigestChapterDto updated)
        {
            var pos = Chapters.FindIndex(c => string.Equals(c.Id, updated.Id, StringComparison.Ordinal));
            if (pos >= 0) Chapters[pos] = updated;
            else Chapters.Add(updated);
        }

        public LiveDigestChapterDto AppendToolToChapterLocked(ChapterToolCallDto call, TouchedFileStatDto? fileDelta)
        {
            var chapter = EnsureCurrentChapterLocked();
            var tools = chapter.ToolCalls.ToList();
            var existingIdx = tools.FindIndex(t => string.Equals(t.ToolCallId, call.ToolCallId, StringComparison.Ordinal));
            if (existingIdx >= 0) tools[existingIdx] = call;
            else tools.Add(call);

            var chapterFiles = LiveExplainerSemanticFolder.MergeTouchedFiles(chapter.TouchedFiles, fileDelta);
            TouchedFiles = LiveExplainerSemanticFolder.MergeTouchedFiles(TouchedFiles, fileDelta).ToList();

            var narrative = chapter.Source == "l0_structural"
                ? $"已折叠 {tools.Count} 项工具操作，最新：{call.ToolName}（{call.CompactSummary}）"
                : chapter.Narrative;

            var updated = chapter with
            {
                ToolCalls = tools,
                TouchedFiles = chapterFiles,
                Narrative = narrative,
                RawThoughtExcerpt = LatestThoughtExcerpt ?? chapter.RawThoughtExcerpt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            UpsertChapterLocked(updated);
            return updated;
        }

        public LiveDigestStreamFrame AdvanceAndBuildL0Delta(
            GovernanceSignalDto? signal,
            SentinelAlertDto? alert,
            LiveDigestChapterDto? chapterUpsert)
        {
            lock (SyncRoot)
            {
                CurrentSeq++;
                var now = DateTimeOffset.UtcNow;
                var payload = new
                {
                    seq = CurrentSeq,
                    run_id = RunId,
                    status = Status,
                    mode = Mode,
                    explainer_active = ExplainerActive,
                    explainer_model = ExplainerModel,
                    headline = Headline,
                    current_phase = CurrentPhase,
                    context_waterline = ContextWaterline,
                    governance_signal = signal,
                    teammate_thoughts = TeammateThoughts.ToArray(),
                    chapter_upsert = chapterUpsert ?? (Chapters.Count > 0 ? Chapters[^1] : null),
                    touched_files = TouchedFiles.ToArray(),
                    sentinel_alert = alert,
                    occurred_at = now
                };
                var frame = new LiveDigestStreamFrame
                {
                    Seq = CurrentSeq,
                    EventKind = "l0_delta",
                    Payload = payload,
                    OccurredAt = now
                };
                RecordFrameLocked(frame);
                return frame;
            }
        }

        public LiveDigestStreamFrame AdvanceAndBuildL1Narration(
            LiveDigestChapterDto chapterUpsert,
            SentinelAlertDto? addedSentinel)
        {
            lock (SyncRoot)
            {
                CurrentSeq++;
                var now = DateTimeOffset.UtcNow;
                var payload = new
                {
                    seq = CurrentSeq,
                    run_id = RunId,
                    status = Status,
                    mode = Mode,
                    explainer_active = ExplainerActive,
                    explainer_model = ExplainerModel,
                    headline = Headline,
                    current_phase = CurrentPhase,
                    context_waterline = ContextWaterline,
                    chapter_upsert = chapterUpsert,
                    chapters = Chapters.ToArray(),
                    teammate_thoughts = TeammateThoughts.ToArray(),
                    touched_files = TouchedFiles.ToArray(),
                    sentinel_alert = addedSentinel,
                    sentinel_alerts = SentinelAlerts.ToArray(),
                    occurred_at = now
                };
                var frame = new LiveDigestStreamFrame
                {
                    Seq = CurrentSeq,
                    EventKind = "l1_narration",
                    Payload = payload,
                    OccurredAt = now
                };
                RecordFrameLocked(frame);
                return frame;
            }
        }

        public LiveDigestSnapshotDto BuildSnapshot()
        {
            lock (SyncRoot)
            {
                return BuildSnapshotLocked();
            }
        }

        public LiveDigestSnapshotDto BuildSnapshotLocked() => new()
        {
            RunId = RunId,
            SessionId = SessionId,
            Status = Status,
            Mode = Mode,
            ExplainerActive = ExplainerActive,
            ExplainerModel = ExplainerModel,
            Seq = CurrentSeq,
            Headline = Headline,
            CurrentPhase = CurrentPhase,
            ContextWaterline = ContextWaterline,
            GovernanceSignals = [.. GovernanceSignals],
            TeammateThoughts = [.. TeammateThoughts],
            Chapters = [.. Chapters],
            TouchedFiles = [.. TouchedFiles],
            SentinelAlerts = [.. SentinelAlerts],
            UpdatedAt = DateTimeOffset.UtcNow
        };

        public void RecordFrameLocked(LiveDigestStreamFrame frame)
        {
            _recentFrames.Add(frame);
            if (_recentFrames.Count > MaxRetainedFrames)
            {
                _recentFrames.RemoveRange(0, _recentFrames.Count - MaxRetainedFrames);
            }
        }

        public IReadOnlyList<LiveDigestStreamFrame> FramesAfter(long afterSeq)
        {
            lock (SyncRoot)
            {
                return _recentFrames.Where(f => f.Seq > afterSeq).ToArray();
            }
        }
    }
}
