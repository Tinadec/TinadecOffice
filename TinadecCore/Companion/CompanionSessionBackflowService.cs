using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Companion;

/// <summary>
/// Event-driven work-session backflow bridge (<see cref="ICompanionSessionBackflow"/>).
/// Tracks work sessions/runs commanded by Companion Bots, streams immediate
/// <c>companion.work_session</c> SSE frames via <see cref="ICompanionStreamBroker"/> on
/// <c>awaiting_approval</c>, <c>browser_handoff</c>, <c>completed</c>, and <c>failed</c> transitions,
/// and concisely wakes the commanding Companion Bot so open Companion chats update inline cards
/// and surface approvals or browser handoff prompts without polling.
/// </summary>
public sealed class CompanionSessionBackflowService(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ICompanionStreamBroker streamBroker,
    IServiceProvider services,
    ILogger<CompanionSessionBackflowService> logger) : ICompanionSessionBackflow
{
    internal const string BackflowActivityKind = "work_session_backflow";
    private const int MaxTrackedEntries = 2048;

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private sealed record CommandTracker(
        Guid CompanionId,
        Guid? ConversationId,
        DateTimeOffset TrackedAt);

    private readonly ConcurrentDictionary<(Guid TenantId, Guid SessionId), CommandTracker> _sessionTrackers = new();
    private readonly ConcurrentDictionary<(Guid TenantId, Guid RunId), CommandTracker> _runTrackers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentDedupe = new(StringComparer.Ordinal);

    public void TrackCommandedSession(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid sessionId,
        Guid? runId = null)
    {
        if (tenantId == Guid.Empty || companionId == Guid.Empty || sessionId == Guid.Empty)
            return;

        var now = DateTimeOffset.UtcNow;
        var tracker = new CommandTracker(companionId, conversationId, now);
        _sessionTrackers[(tenantId, sessionId)] = tracker;
        if (runId is { } rid && rid != Guid.Empty)
        {
            _runTrackers[(tenantId, rid)] = tracker;
        }

        if (_sessionTrackers.Count > MaxTrackedEntries)
            PruneOldest(_sessionTrackers, MaxTrackedEntries / 2);
        if (_runTrackers.Count > MaxTrackedEntries)
            PruneOldest(_runTrackers, MaxTrackedEntries / 2);
    }

    public async Task NotifySessionTransitionAsync(
        CompanionSessionBackflowEvent transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.TenantId == Guid.Empty || transition.SessionId == Guid.Empty)
            return;

        var status = transition.Status?.Trim().ToLowerInvariant() switch
        {
            "awaiting_approval" => "awaiting_approval",
            "completed" => "completed",
            "failed" => "failed",
            "browser_handoff" => "browser_handoff",
            _ => null
        };
        if (status is null) return;

        var approvals = transition.PendingApprovals ?? [];
        var dedupeFingerprint = ComputeTransitionFingerprint(transition.RunId, status, transition.Summary, approvals);
        var now = DateTimeOffset.UtcNow;
        if (_recentDedupe.TryGetValue(dedupeFingerprint, out var seenAt) && now - seenAt < TimeSpan.FromSeconds(10))
            return;
        _recentDedupe[dedupeFingerprint] = now;
        if (_recentDedupe.Count > MaxTrackedEntries)
        {
            foreach (var stale in _recentDedupe.Where(kv => now - kv.Value > TimeSpan.FromMinutes(15)).Select(kv => kv.Key).ToList())
                _recentDedupe.TryRemove(stale, out _);
        }

        try
        {
            var (commandedTracker, observerCompanionId) = await ResolveTargetCompanionsAsync(transition, cancellationToken).ConfigureAwait(false);
            var primaryCompanionId = commandedTracker?.CompanionId ?? observerCompanionId;
            var primaryConversationId = commandedTracker?.ConversationId;

            var approvalsPayload = approvals.Select(item => new
            {
                approval_id = item.ApprovalId,
                tool_id = item.ToolId,
                summary = item.Summary,
                risk_level = item.RiskLevel,
                created_at = item.CreatedAt
            }).ToList();

            var streamPayload = new
            {
                companion_id = primaryCompanionId,
                conversation_id = primaryConversationId,
                workspace_id = transition.WorkspaceId,
                project_id = transition.ProjectId,
                session_id = transition.SessionId,
                session_title = transition.SessionTitle,
                title = transition.SessionTitle,
                run_id = transition.RunId,
                status,
                mode_name = transition.ModeName,
                summary = transition.Summary,
                plan_summary = transition.PlanSummary,
                pending_approvals = approvalsPayload,
                handoff_reason = status == "browser_handoff" ? transition.Summary : null,
                updated_at = now
            };

            // 1. Broadcast live SSE frame immediately so open Companion chats & dock cards update with zero polling.
            streamBroker.PublishWorkSession(
                transition.TenantId,
                primaryCompanionId,
                primaryConversationId,
                streamPayload);

            // 2. Persist durable activity trail when a Companion is connected or commanding.
            if (primaryCompanionId is { } recordedCompanionId)
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                db.Activities.Add(new CompanionActivityRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = transition.TenantId,
                    Kind = BackflowActivityKind,
                    CompanionId = recordedCompanionId,
                    ConversationId = primaryConversationId,
                    Summary = BuildActivitySummary(transition.SessionTitle, status, approvals.Count),
                    Detail = Truncate(transition.Summary ?? transition.PlanSummary ?? string.Empty, 500),
                    PayloadJson = JsonSerializer.Serialize(streamPayload, SnakeCase),
                    CreatedAt = now
                });
                await CompanionActivities.SaveAsync(db, transition.TenantId, cancellationToken).ConfigureAwait(false);
            }

            // 3. Concisely wake the commanding Companion Bot ONLY for sessions it actively commanded
            // (respectful restraint: never interrupt the Bot with unsolicited turns on purely manual user sessions).
            if (commandedTracker is { } tracker)
            {
                await WakeCommandingCompanionAsync(transition, status, approvals, tracker, dedupeFingerprint, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Companion session backflow failed for session {SessionId} run {RunId} ({Status}).",
                transition.SessionId, transition.RunId, status);
        }
    }

    private async Task<(CommandTracker? Commanded, Guid? ConnectedObserverId)> ResolveTargetCompanionsAsync(
        CompanionSessionBackflowEvent transition,
        CancellationToken cancellationToken)
    {
        if (transition.RunId != Guid.Empty && _runTrackers.TryGetValue((transition.TenantId, transition.RunId), out var byRun))
            return (byRun, byRun.CompanionId);
        if (_sessionTrackers.TryGetValue((transition.TenantId, transition.SessionId), out var bySession))
            return (bySession, bySession.CompanionId);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sessionToken = transition.SessionId.ToString();
        var commandActivities = await db.Activities.AsNoTracking()
            .Where(x => x.TenantId == transition.TenantId
                && x.CompanionId != null
                && (x.Kind == CompanionRuntime.DispatchActivityKind
                    || x.Kind == "companion_steered"
                    || x.Kind == "companion_session_created")
                && x.PayloadJson != null
                && EF.Functions.Like(x.PayloadJson, $"%{sessionToken}%"))
            .OrderByDescending(x => x.Seq)
            .Take(5)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var latestCommand = commandActivities.FirstOrDefault();
        if (latestCommand?.CompanionId is { } cmdCompanionId)
        {
            var recovered = new CommandTracker(cmdCompanionId, latestCommand.ConversationId, latestCommand.CreatedAt);
            _sessionTrackers[(transition.TenantId, transition.SessionId)] = recovered;
            return (recovered, cmdCompanionId);
        }

        // No companion directly commanded this session; check if an active companion holds a work_session grant
        // so SSE frames still carry a companion_id for connected workspace views.
        var bindings = await db.ConnectionBindings.AsNoTracking()
            .Where(x => x.TenantId == transition.TenantId
                && x.Kind == "work_session"
                && x.Enabled
                && x.Authorization == "authorized")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var projectIdText = transition.ProjectId?.ToString();
        var matchingBinding = bindings.FirstOrDefault(b =>
            string.IsNullOrWhiteSpace(b.Endpoint)
            || b.Endpoint.Trim() == "*"
            || (projectIdText is not null && string.Equals(b.Endpoint.Trim(), projectIdText, StringComparison.OrdinalIgnoreCase)));

        if (matchingBinding is not null)
            return (null, matchingBinding.CompanionId);

        var mainCompanionId = await db.Settings.AsNoTracking()
            .Where(x => x.TenantId == transition.TenantId)
            .Select(x => x.MainCompanionId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return (null, mainCompanionId);
    }

    private async Task WakeCommandingCompanionAsync(
        CompanionSessionBackflowEvent transition,
        string status,
        IReadOnlyList<CompanionPendingApprovalItem> approvals,
        CommandTracker tracker,
        string dedupeFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var channel = services.GetService<ICompanionChannel>();
        if (channel is null) return;

        Guid targetConversationId;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var companion = await db.Companions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == tracker.CompanionId && x.TenantId == transition.TenantId && x.Status == "active", cancellationToken)
                .ConfigureAwait(false);
            if (companion is null || companion.AutonomyPaused) return;

            if (tracker.ConversationId is { } existingConv && existingConv != Guid.Empty)
            {
                var convExists = await db.Conversations.AsNoTracking()
                    .AnyAsync(x => x.Id == existingConv && x.TenantId == transition.TenantId && !x.AutonomyPaused, cancellationToken)
                    .ConfigureAwait(false);
                if (convExists)
                {
                    targetConversationId = existingConv;
                }
                else
                {
                    return;
                }
            }
            else
            {
                var privateConv = await (
                    from conv in db.Conversations.AsNoTracking()
                    join member in db.ConversationMembers.AsNoTracking() on conv.Id equals member.ConversationId
                    where conv.TenantId == transition.TenantId && conv.Kind == "private" && !conv.AutonomyPaused
                        && member.TenantId == transition.TenantId && member.MemberKind == "companion"
                        && member.CompanionId == companion.Id && member.Status == "active"
                    select new { conv.Id, conv.CreatedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (privateConv.Count == 0) return;
                targetConversationId = privateConv.OrderBy(x => x.CreatedAt).First().Id;
            }
        }

        var body = BuildWakeMessageBody(transition, status, approvals);
        await channel.OnInboundMessageAsync(
            transition.TenantId,
            new CompanionEnvelope(
                ChannelKind: "desktop_chat",
                ThreadKey: $"work-session:{transition.SessionId:N}",
                ConversationId: targetConversationId,
                AuthorKind: "system",
                AuthorCompanionId: null,
                Body: body,
                AddressedTo: [tracker.CompanionId],
                HopDepth: 0,
                IdempotencyKey: $"backflow:{dedupeFingerprint}"),
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildActivitySummary(string sessionTitle, string status, int approvalCount) => status switch
    {
        "awaiting_approval" => $"工作会话「{sessionTitle}」有 {Math.Max(1, approvalCount)} 项待审批操作",
        "browser_handoff" => $"工作会话「{sessionTitle}」请求人工接管浏览器",
        "completed" => $"工作会话「{sessionTitle}」已完成执行",
        "failed" => $"工作会话「{sessionTitle}」执行失败",
        _ => $"工作会话「{sessionTitle}」状态更新：{status}"
    };

    private static string BuildWakeMessageBody(
        CompanionSessionBackflowEvent transition,
        string status,
        IReadOnlyList<CompanionPendingApprovalItem> approvals)
    {
        var sb = new StringBuilder();
        switch (status)
        {
            case "awaiting_approval":
            {
                sb.AppendLine($"[工作会话回流 · 待审批] 会话「{transition.SessionTitle}」(session_id={transition.SessionId}, run_id={transition.RunId}) 暂停等待用户审批：");
                foreach (var item in approvals.Take(5))
                {
                    sb.AppendLine($"- 工具 `{item.ToolId}` (risk={item.RiskLevel ?? "medium"}, approval_id={item.ApprovalId}): {Truncate(item.Summary ?? "请求执行受控操作", 160)}");
                }
                if (!string.IsNullOrWhiteSpace(transition.PlanSummary))
                    sb.AppendLine($"计划进度：{transition.PlanSummary}");
                sb.Append("请用简洁克制的语气向用户说明待审批内容与风险提示，引导用户在内联会话卡片上点击「批准」或「拒绝」（不要重复派发同一任务）。");
                break;
            }
            case "browser_handoff":
            {
                sb.AppendLine($"[工作会话回流 · 浏览器人工接管] 会话「{transition.SessionTitle}」(session_id={transition.SessionId}, run_id={transition.RunId}) 请求人工接管浏览器。");
                if (!string.IsNullOrWhiteSpace(transition.Summary))
                    sb.AppendLine($"原因：{Truncate(transition.Summary, 240)}");
                sb.Append("请简短提醒用户前往浏览器或内联卡片完成登录/验证码/2FA，并在完成后点击交回智能体继续。");
                break;
            }
            case "completed":
            {
                sb.AppendLine($"[工作会话回流 · 已完成] 会话「{transition.SessionTitle}」(session_id={transition.SessionId}, run_id={transition.RunId}) 已完成。");
                if (!string.IsNullOrWhiteSpace(transition.Summary))
                    sb.AppendLine($"结果摘要：{Truncate(transition.Summary, 400)}");
                if (!string.IsNullOrWhiteSpace(transition.PlanSummary))
                    sb.AppendLine($"计划进度：{transition.PlanSummary}");
                sb.Append("请向用户做精炼、结构化的进展汇报（不要无故再次触发新的派活）。");
                break;
            }
            default:
            {
                sb.AppendLine($"[工作会话回流 · 失败] 会话「{transition.SessionTitle}」(session_id={transition.SessionId}, run_id={transition.RunId}) 执行失败。");
                if (!string.IsNullOrWhiteSpace(transition.Summary))
                    sb.AppendLine($"失败摘要：{Truncate(transition.Summary, 400)}");
                if (!string.IsNullOrWhiteSpace(transition.PlanSummary))
                    sb.AppendLine($"计划进度：{transition.PlanSummary}");
                sb.Append("请向用户简要说明失败原因并给出清晰的下一步建议。");
                break;
            }
        }
        return sb.ToString();
    }

    private static string ComputeTransitionFingerprint(
        Guid runId,
        string status,
        string? summary,
        IReadOnlyList<CompanionPendingApprovalItem> approvals)
    {
        var raw = new StringBuilder()
            .Append(runId.ToString("N")).Append('|')
            .Append(status).Append('|');
        if (approvals.Count > 0)
        {
            foreach (var item in approvals.OrderBy(a => a.ApprovalId))
                raw.Append(item.ApprovalId.ToString("N")).Append(',');
        }
        else if (status == "browser_handoff" && !string.IsNullOrWhiteSpace(summary))
        {
            raw.Append(summary.Trim());
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToString()));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static void PruneOldest<TKey>(ConcurrentDictionary<TKey, CommandTracker> map, int keepCount)
        where TKey : notnull
    {
        foreach (var stale in map.OrderBy(kv => kv.Value.TrackedAt).Take(Math.Max(1, map.Count - keepCount)).Select(kv => kv.Key).ToList())
        {
            map.TryRemove(stale, out _);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
