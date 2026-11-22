using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Companion;

/// <summary>
/// Canonical <see cref="ICompanionChannel"/> implementation. Handles human/external inbound posts,
/// bounded bot-to-bot <c>@Name</c> group relays governed by <c>max_bot_hop_depth</c> (default 3, 0..10),
/// and persistent schedule ticks.
/// </summary>
public sealed class CompanionChannelService(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ICompanionRuntime runtime) : ICompanionChannel
{
    private const int DefaultTurnBudget = 6;
    private static readonly string[] AllowedChannels =
    [
        "desktop_chat",
        "session_partner",
        "schedule_tick",
        "external_im"
    ];

    public static string NormalizeChannelKind(string? channelKind)
    {
        if (string.IsNullOrWhiteSpace(channelKind)) return "desktop_chat";
        var normalized = channelKind.Trim().ToLowerInvariant();
        return AllowedChannels.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : throw new CompanionException("invalid_request", "channel_kind must be desktop_chat, session_partner, schedule_tick, or external_im.", 400);
    }

    public static List<Guid> ResolveMentions(string body, IReadOnlyList<CompanionAgentRecord> companions, Guid? excludeCompanionId = null)
    {
        var result = new List<Guid>();
        if (string.IsNullOrWhiteSpace(body) || companions.Count == 0) return result;
        var remaining = body;
        foreach (var companion in companions.OrderByDescending(x => x.Name.Length).ThenBy(x => x.Id))
        {
            if (excludeCompanionId is { } selfId && companion.Id == selfId) continue;
            var token = "@" + companion.Name;
            if (!remaining.Contains(token, StringComparison.Ordinal)) continue;
            result.Add(companion.Id);
            remaining = remaining.Replace(token, string.Empty, StringComparison.Ordinal);
        }
        return result;
    }

    public async Task<CompanionChannelPostResult> OnInboundMessageAsync(
        Guid tenantId,
        CompanionEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var body = envelope.Body?.Trim();
        if (string.IsNullOrEmpty(body))
            throw new CompanionException("invalid_request", "body is required.", 400);

        var channelKind = NormalizeChannelKind(envelope.ChannelKind);
        var threadKey = TrimToNull(envelope.ThreadKey);
        if (threadKey is { Length: > 256 })
            throw new CompanionException("invalid_request", "thread_key must be at most 256 characters.", 400);

        var idempotencyKey = TrimToNull(envelope.IdempotencyKey);
        if (idempotencyKey is { Length: > 256 })
            throw new CompanionException("invalid_request", "idempotency_key must be at most 256 characters.", 400);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == envelope.ConversationId && x.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CompanionException("companion_conversation_not_found", "Conversation not found.", 404);

        if (idempotencyKey is not null)
        {
            var replayed = await db.Messages.AsNoTracking().SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ConversationId == envelope.ConversationId && x.IdempotencyKey == idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (replayed is not null)
            {
                var dupRuns = await db.Runs.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConversationId == envelope.ConversationId && x.TriggerMessageId == replayed.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var names = await LoadCompanionNamesAsync(db, tenantId, [replayed.AuthorCompanionId], cancellationToken).ConfigureAwait(false);
                var orderedDupRuns = dupRuns.OrderBy(x => x.CreatedAt).ToList();
                var dupJson = new
                {
                    message = CompanionContracts.MessageJson(
                        replayed,
                        AuthorName(replayed, names),
                        CompanionMessages.DeserializeAddressed(replayed.AddressedToJson)),
                    runs = orderedDupRuns.Select(CompanionContracts.RunJson).ToList(),
                    skipped_runs = Array.Empty<object>(),
                    duplicate = true
                };
                return new CompanionChannelPostResult(dupJson, replayed.Id, orderedDupRuns.Select(x => x.Id).ToList(), Duplicate: true);
            }
        }

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == envelope.ConversationId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var activeCompanionIds = members
            .Where(x => x.MemberKind == "companion" && x.Status == "active" && x.CompanionId is not null)
            .Select(x => x.CompanionId!.Value)
            .Distinct()
            .ToList();
        var companions = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && activeCompanionIds.Contains(x.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var requested = (envelope.AddressedTo ?? []).Distinct().ToList();
        var addressed = requested.Count > 0
            ? requested
            : ResolveMentions(body, companions, envelope.AuthorCompanionId);
        if (addressed.Any(id => !activeCompanionIds.Contains(id)))
            throw new CompanionException("companion_not_member", "addressed_to contains a companion that is not an active member of this conversation.", 400);
        if (addressed.Count == 0 && conversation.Kind == "private")
            addressed = activeCompanionIds;

        var maxBotHopDepth = await LoadMaxBotHopDepthAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        var previousAnchorSeq = await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == envelope.ConversationId && x.AuthorKind != "companion")
            .Select(x => (long?)x.Seq)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;

        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var authorKind = string.IsNullOrWhiteSpace(envelope.AuthorKind) ? "user" : envelope.AuthorKind.Trim().ToLowerInvariant();
        var message = await CompanionMessages.AppendAsync(
            db,
            tenantId,
            envelope.ConversationId,
            authorKind,
            envelope.AuthorCompanionId,
            body,
            addressed,
            idempotencyKey,
            now,
            cancellationToken).ConfigureAwait(false);

        var skipped = new List<object>();
        var runs = new List<CompanionRunRecord>();
        var turnBudget = conversation.TurnBudget <= 0 ? DefaultTurnBudget : conversation.TurnBudget;
        var hopDepth = Math.Max(0, envelope.HopDepth);

        foreach (var companionId in addressed.Distinct())
        {
            if (authorKind == "companion" && envelope.AuthorCompanionId == companionId)
                continue;

            var companion = companions.FirstOrDefault(x => x.Id == companionId);
            if (companion is null)
            {
                skipped.Add(Skip(companionId, "companion_not_found"));
                continue;
            }
            if (conversation.AutonomyPaused)
            {
                skipped.Add(Skip(companionId, "conversation_paused"));
                continue;
            }
            if (companion.Status != "active" || companion.AutonomyPaused)
            {
                skipped.Add(Skip(companionId, "companion_paused"));
                continue;
            }
            if (authorKind == "companion" && (maxBotHopDepth <= 0 || hopDepth > maxBotHopDepth))
            {
                skipped.Add(Skip(companionId, "hop_depth_limit"));
                continue;
            }

            var used = await (
                from run in db.Runs.AsNoTracking()
                join trigger in db.Messages.AsNoTracking() on run.TriggerMessageId equals trigger.Id
                where run.TenantId == tenantId && run.ConversationId == envelope.ConversationId
                    && run.CompanionId == companionId && trigger.Seq >= previousAnchorSeq
                select run.Id).CountAsync(cancellationToken).ConfigureAwait(false);
            if (used >= turnBudget)
            {
                skipped.Add(Skip(companionId, "turn_budget"));
                continue;
            }

            runs.Add(new CompanionRunRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CompanionId = companionId,
                ConversationId = envelope.ConversationId,
                TriggerMessageId = message.Id,
                Status = "queued",
                ChannelKind = channelKind,
                ThreadKey = threadKey,
                Depth = hopDepth,
                AvailableAt = now,
                AvailableAtUnixMs = nowMs,
                ConfigRevision = companion.Revision,
                ConfigSnapshotJson = "{}",
                LoadedMemoryIdsJson = "[]",
                LoadedSkillBindingIdsJson = "[]",
                Rounds = 0,
                CreatedAt = now
            });
        }

        foreach (var run in runs) db.Runs.Add(run);
        conversation.LastMessageAt = now;
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "message_sent",
            CompanionId = envelope.AuthorCompanionId,
            ConversationId = envelope.ConversationId,
            Summary = authorKind == "system"
                ? $"日程唤醒：{Preview(body, 40)}"
                : $"用户发言：{Preview(body, 40)}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                message_id = message.Id,
                author_kind = authorKind,
                channel_kind = channelKind,
                thread_key = threadKey,
                addressed_to = addressed,
                preview = Preview(body, 80),
                run_ids = runs.Select(x => x.Id).ToList()
            }),
            CreatedAt = now
        });

        try
        {
            await CompanionActivities.SaveAsync(
                db,
                tenantId,
                token => CompanionMessages.SaveAsync(db, message, tenantId, envelope.ConversationId, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw new CompanionException("companion_duplicate_message", "The message could not be stored without colliding with a concurrent write.", 409);
        }

        foreach (var run in runs)
            await runtime.EnqueueAsync(run.Id, cancellationToken).ConfigureAwait(false);

        var authorNames = await LoadCompanionNamesAsync(db, tenantId, [message.AuthorCompanionId], cancellationToken).ConfigureAwait(false);
        var json = new
        {
            message = CompanionContracts.MessageJson(message, AuthorName(message, authorNames), addressed),
            runs = runs.Select(CompanionContracts.RunJson).ToList(),
            skipped_runs = skipped,
            duplicate = false
        };
        return new CompanionChannelPostResult(json, message.Id, runs.Select(x => x.Id).ToList(), Duplicate: false);
    }

    public async Task<CompanionChannelRelayResult> OnMentionAsync(
        Guid tenantId,
        CompanionRunRecord sourceRun,
        CompanionMessageRecord replyMessage,
        IReadOnlyList<Guid> mentionedCompanionIds,
        CancellationToken cancellationToken = default)
    {
        var distinctTargets = mentionedCompanionIds
            .Where(id => id != Guid.Empty && id != sourceRun.CompanionId)
            .Distinct()
            .ToList();
        if (distinctTargets.Count == 0)
            return new CompanionChannelRelayResult([], []);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sourceRun.ConversationId && x.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
            return new CompanionChannelRelayResult([], []);

        var maxBotHopDepth = await LoadMaxBotHopDepthAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        var nextHop = sourceRun.Depth + 1;
        var skipped = new List<object>();

        if (maxBotHopDepth <= 0 || nextHop > maxBotHopDepth)
        {
            foreach (var targetId in distinctTargets)
                skipped.Add(Skip(targetId, "hop_depth_limit"));

            var cappedAt = DateTimeOffset.UtcNow;
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = "bot_relay_capped",
                CompanionId = sourceRun.CompanionId,
                ConversationId = sourceRun.ConversationId,
                Summary = $"伙伴追问已达跳数上限（max_bot_hop_depth={maxBotHopDepth}）",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    source_run_id = sourceRun.Id,
                    reply_message_id = replyMessage.Id,
                    hop_depth = nextHop,
                    max_bot_hop_depth = maxBotHopDepth,
                    skipped_companion_ids = distinctTargets
                }),
                CreatedAt = cappedAt
            });
            await CompanionActivities.SaveAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
            return new CompanionChannelRelayResult([], skipped);
        }

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == sourceRun.ConversationId
                && x.MemberKind == "companion" && x.Status == "active" && x.CompanionId != null)
            .Select(x => x.CompanionId!.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var activeSet = members.ToHashSet();

        var companions = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && distinctTargets.Contains(x.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var previousAnchorSeq = await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == sourceRun.ConversationId && x.AuthorKind != "companion")
            .Select(x => (long?)x.Seq)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;

        var turnBudget = conversation.TurnBudget <= 0 ? DefaultTurnBudget : conversation.TurnBudget;
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var runs = new List<CompanionRunRecord>();

        foreach (var targetId in distinctTargets)
        {
            if (!activeSet.Contains(targetId))
            {
                skipped.Add(Skip(targetId, "companion_not_member"));
                continue;
            }
            if (conversation.AutonomyPaused)
            {
                skipped.Add(Skip(targetId, "conversation_paused"));
                continue;
            }
            var companion = companions.FirstOrDefault(x => x.Id == targetId);
            if (companion is null || companion.Status != "active" || companion.AutonomyPaused)
            {
                skipped.Add(Skip(targetId, "companion_paused"));
                continue;
            }

            var used = await (
                from run in db.Runs.AsNoTracking()
                join trigger in db.Messages.AsNoTracking() on run.TriggerMessageId equals trigger.Id
                where run.TenantId == tenantId && run.ConversationId == sourceRun.ConversationId
                    && run.CompanionId == targetId && trigger.Seq >= previousAnchorSeq
                select run.Id).CountAsync(cancellationToken).ConfigureAwait(false);
            if (used >= turnBudget)
            {
                skipped.Add(Skip(targetId, "turn_budget"));
                continue;
            }

            runs.Add(new CompanionRunRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CompanionId = targetId,
                ConversationId = sourceRun.ConversationId,
                TriggerMessageId = replyMessage.Id,
                Status = "queued",
                ChannelKind = sourceRun.ChannelKind,
                ThreadKey = sourceRun.ThreadKey,
                Depth = nextHop,
                AvailableAt = now,
                AvailableAtUnixMs = nowMs,
                ConfigRevision = companion.Revision,
                ConfigSnapshotJson = "{}",
                LoadedMemoryIdsJson = "[]",
                LoadedSkillBindingIdsJson = "[]",
                Rounds = 0,
                CreatedAt = now
            });
        }

        if (runs.Count > 0)
        {
            foreach (var run in runs) db.Runs.Add(run);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var run in runs)
                await runtime.EnqueueAsync(run.Id, cancellationToken).ConfigureAwait(false);
        }

        return new CompanionChannelRelayResult(runs.Select(x => x.Id).ToList(), skipped);
    }

    public async Task<CompanionScheduleTriggerResult> OnScheduleTickAsync(
        Guid tenantId,
        Guid companionId,
        Guid scheduleId,
        bool manualTrigger = false,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        Guid targetConversationId;
        string title;
        string prompt;
        object scheduleJson;

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var companion = await db.Companions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CompanionException("companion_not_found", "Companion not found.", 404);
            if (companion.Status == "archived")
                throw new CompanionException("companion_archived", "An archived companion cannot trigger schedules.", 409);

            var schedule = await db.Schedules
                .SingleOrDefaultAsync(x => x.Id == scheduleId && x.CompanionId == companionId && x.TenantId == tenantId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CompanionException("companion_schedule_not_found", "Schedule not found.", 404);

            title = schedule.Title;
            prompt = schedule.Prompt;
            targetConversationId = await ResolveOrCreateTargetConversationAsync(
                db, tenantId, companion, schedule.TargetConversationId, now, cancellationToken).ConfigureAwait(false);

            schedule.LastRunAt = now;
            if (schedule.OneShot || string.IsNullOrWhiteSpace(schedule.CronExpression))
            {
                schedule.Status = "completed";
                schedule.Enabled = false;
                schedule.NextRunAt = null;
                schedule.NextRunAtUnixMs = null;
            }
            else
            {
                var next = CompanionCron.NextOccurrenceUtc(schedule.CronExpression, now);
                schedule.NextRunAt = next;
                schedule.NextRunAtUnixMs = next?.ToUnixTimeMilliseconds();
            }
            schedule.Revision++;
            schedule.UpdatedAt = now;

            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = "schedule_triggered",
                CompanionId = companionId,
                ConversationId = targetConversationId,
                Summary = $"伙伴日程「{schedule.Title}」已触发",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    schedule_id = schedule.Id,
                    manual = manualTrigger,
                    conversation_id = targetConversationId,
                    next_run_at = schedule.NextRunAt
                }),
                CreatedAt = now
            });
            await CompanionActivities.SaveAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
            scheduleJson = CompanionContracts.ScheduleJson(schedule);
        }

        var idempotencyKey = manualTrigger
            ? $"schedule-manual:{scheduleId:N}:{Guid.NewGuid():N}"
            : $"schedule-tick:{scheduleId:N}:{now.ToUnixTimeSeconds() / 60}";
        var post = await OnInboundMessageAsync(
            tenantId,
            new CompanionEnvelope(
                ChannelKind: "schedule_tick",
                ThreadKey: $"schedule:{scheduleId:N}",
                ConversationId: targetConversationId,
                AuthorKind: "system",
                AuthorCompanionId: null,
                Body: $"[定时任务：{title}]\n{prompt}",
                AddressedTo: [companionId],
                HopDepth: 0,
                IdempotencyKey: idempotencyKey),
            cancellationToken).ConfigureAwait(false);

        return new CompanionScheduleTriggerResult(scheduleJson, targetConversationId, post.RunIds);
    }

    private static async Task<Guid> ResolveOrCreateTargetConversationAsync(
        CompanionDbContext db,
        Guid tenantId,
        CompanionAgentRecord companion,
        Guid? requestedConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (requestedConversationId is { } targetId && targetId != Guid.Empty)
        {
            var validMember = await (
                from conv in db.Conversations.AsNoTracking()
                join member in db.ConversationMembers.AsNoTracking() on conv.Id equals member.ConversationId
                where conv.Id == targetId && conv.TenantId == tenantId
                    && member.TenantId == tenantId && member.MemberKind == "companion"
                    && member.CompanionId == companion.Id && member.Status == "active"
                select conv.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
            if (validMember) return targetId;
        }

        var existingPrivate = await (
            from conv in db.Conversations.AsNoTracking()
            join member in db.ConversationMembers.AsNoTracking() on conv.Id equals member.ConversationId
            where conv.TenantId == tenantId && conv.Kind == "private"
                && member.TenantId == tenantId && member.MemberKind == "companion"
                && member.CompanionId == companion.Id && member.Status == "active"
            select new { conv.Id, conv.CreatedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (existingPrivate.Count > 0)
            return existingPrivate.OrderBy(x => x.CreatedAt).First().Id;

        var created = new CompanionConversationRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "private",
            Origin = "companion",
            Title = companion.Name,
            TurnBudget = DefaultTurnBudget,
            Revision = 1,
            CreatedAt = now
        };
        db.Conversations.Add(created);
        db.ConversationMembers.Add(new CompanionConversationMemberRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = created.Id,
            MemberKind = "user",
            JoinedAt = now,
            Status = "active"
        });
        db.ConversationMembers.Add(new CompanionConversationMemberRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = created.Id,
            MemberKind = "companion",
            CompanionId = companion.Id,
            JoinedAt = now,
            Status = "active"
        });
        return created.Id;
    }

    private static async Task<int> LoadMaxBotHopDepthAsync(CompanionDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var configured = await db.Settings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.MaxBotHopDepth)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return Math.Clamp(configured ?? 3, 0, 10);
    }

    private static async Task<Dictionary<Guid, string>> LoadCompanionNamesAsync(
        CompanionDbContext db, Guid tenantId, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && wanted.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken).ConfigureAwait(false);
    }

    private static string AuthorName(CompanionMessageRecord row, Dictionary<Guid, string> names) => row.AuthorKind switch
    {
        "companion" => row.AuthorCompanionId is { } id ? names.GetValueOrDefault(id, "伙伴") : "伙伴",
        "system" => "系统",
        _ => "用户"
    };

    private static object Skip(Guid companionId, string reason) => new { companion_id = companionId, reason };

    private static string Preview(string body, int max)
    {
        var oneLine = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
