using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.TinaChat;

public sealed partial class TinaChatService
{
    private static readonly TimeSpan WakeCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WakeRetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WakeClaimLease = TimeSpan.FromMinutes(5);
    private const int MaxWakeAttempts = 5;
    private static readonly TimeSpan WakeRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan TurnWindow = TimeSpan.FromHours(1);

    private sealed record WakeClaim(long Id, string Token);

    /// <summary>
    /// Queues the turns a committed message owes, inside the caller's Serializable transaction. A
    /// brief is excluded: it is the interpreter's own output and the next move belongs to a human
    /// decision, so waking interpreters on it would only make two of them answer each other.
    ///
    /// A live row keeps new arrivals in SourceMessageIdsJson while a claimed turn owns a separate
    /// ClaimedSourceMessageIdsJson snapshot. This is the ACK boundary: a model failure can put the
    /// claimed snapshot back without losing a message that arrived during the call.
    /// </summary>
    private static async Task EnqueueWakesAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant sender,
        ChatMessage message, ChatParticipant[] recipients, List<ChatAudience> audiences, CancellationToken ct)
    {
        if (message.Kind == "intent_brief") return;
        var now = DateTimeOffset.UtcNow;
        foreach (var recipient in recipients)
        {
            var standing = recipient.OrganizationId is { } organization
                && conversation.OrganizationId == organization
                && recipient.OrgRole == OrganizationRoles.Governance;
            if (recipient.Id == sender.Id || recipient.Kind != "agent" || recipient.Status != "active"
                || !(recipient.CanInterpretIntent || standing || recipient.OrgRole == OrganizationRoles.Executor)) continue;
            var audience = audiences.FirstOrDefault(x => x.ParticipantId == recipient.Id);
            if (audience is null) continue;
            if (!audience.CanReadOriginal && !(message.AllowDerivedSharing && audience.CanReceiveDerived)) continue;
            var live = await db.Wakes.SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ParticipantId == recipient.Id
                && x.Reason == "message" && (x.Status == "pending" || x.Status == "running"), ct);
            if (live is not null)
            {
                // Do not cap this merge: a cap silently loses source ids. Context pressure is handled
                // by the turn's model budget, while the durable queue remains complete and auditable.
                live.SourceMessageIdsJson = Serialize(Merge(Deserialize(live.SourceMessageIdsJson), new[] { message.Id }));
                live.UpdatedAt = now;
                continue;
            }
            var due = await NextWakeSlotAsync(db, conversation.Id, recipient.Id, now, ct);
            db.Wakes.Add(new ChatWake
            {
                TenantId = conversation.TenantId, WorkspaceId = recipient.WorkspaceId, ConversationId = conversation.Id,
                ParticipantId = recipient.Id, Reason = "message", Status = "pending",
                SourceMessageIdsJson = Serialize(new[] { message.Id }),
                AvailableAt = due, DueAtUnixMs = due.ToUnixTimeMilliseconds(),
                CreatedAt = now, UpdatedAt = now
            });
        }
    }

    private static async Task<DateTimeOffset> NextWakeSlotAsync(TinaChatDbContext db, Guid conversationId, Guid participantId, DateTimeOffset now, CancellationToken ct)
    {
        var settled = await db.Wakes.Where(x => x.ConversationId == conversationId && x.ParticipantId == participantId
            && x.Reason == "message" && x.Status != "pending" && x.Status != "running").ToArrayAsync(ct);
        var last = settled.Length == 0 ? default : settled.Max(x => x.UpdatedAt);
        return last == default || last.Add(WakeCooldown) <= now ? now : last.Add(WakeCooldown);
    }

    public async Task<int> ProcessPendingWakesAsync(int maxWakes, CancellationToken ct = default)
    {
        var processed = 0;
        var max = Math.Clamp(maxWakes, 1, 20);
        await ReclaimExpiredWakesAsync(max * 2, ct);
        var claimed = await ClaimDueWakesAsync(max, ct);
        await Parallel.ForEachAsync(claimed, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(wakeOptions?.Value.WakeParallelism ?? 4, 1, 16),
            CancellationToken = ct
        }, async (claim, token) =>
        {
            try
            {
                if (await RunWakeAsync(claim, token)) Interlocked.Increment(ref processed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await RequeueClaimAsync(claim, "interrupted", terminal: false, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await FailWakeAsync(claim, ex, CancellationToken.None);
            }
        });
        await PruneSettledWakesAsync(ct);
        return processed;
    }

    /// <summary>
    /// Requeues a claim whose host disappeared. The claim token is the fencing boundary: a late old
    /// host cannot settle a row after another host has reclaimed it.
    /// </summary>
    private async Task ReclaimExpiredWakesAsync(int max, CancellationToken ct)
    {
        var before = DateTimeOffset.UtcNow.Subtract(WakeClaimLease).ToUnixTimeMilliseconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Wakes.Where(x => x.Status == "running" && x.ClaimedAtUnixMs != null && x.ClaimedAtUnixMs < before)
            .OrderBy(x => x.Id).Take(Math.Clamp(max, 1, 100)).ToArrayAsync(ct);
        foreach (var row in rows)
        {
            row.SourceMessageIdsJson = Serialize(Merge(Deserialize(row.SourceMessageIdsJson), Deserialize(row.ClaimedSourceMessageIdsJson)));
            row.ClaimedSourceMessageIdsJson = "[]";
            row.ClaimToken = null;
            row.ClaimedAtUnixMs = null;
            row.Status = "pending";
            row.AvailableAt = DateTimeOffset.UtcNow;
            row.DueAtUnixMs = row.AvailableAt.ToUnixTimeMilliseconds();
            row.LastError = "reclaimed: previous wake host stopped before acknowledgement";
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (rows.Length > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>Claims a row first, then moves its source snapshot into the claim column.</summary>
    private async Task<IReadOnlyList<WakeClaim>> ClaimDueWakesAsync(int max, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.Wakes.Where(x => x.Status == "pending" && x.DueAtUnixMs <= nowMs)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(max).ToArrayAsync(ct);
        var claimed = new List<WakeClaim>();
        foreach (var id in ids)
        {
            var token = Guid.NewGuid().ToString("N");
            var won = await db.Wakes.Where(x => x.Id == id && x.Status == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "running")
                    .SetProperty(x => x.ClaimToken, token)
                    .SetProperty(x => x.ClaimedAtUnixMs, nowMs)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                    .SetProperty(x => x.UpdatedAt, now), ct);
            if (won != 1) continue;
            var row = await db.Wakes.SingleAsync(x => x.Id == id, ct);
            // Anything arriving before this move is included in the claimed snapshot; anything after
            // it stays in SourceMessageIdsJson for the next turn.
            row.ClaimedSourceMessageIdsJson = row.SourceMessageIdsJson;
            row.SourceMessageIdsJson = "[]";
            await db.SaveChangesAsync(ct);
            claimed.Add(new WakeClaim(id, token));
        }
        return claimed;
    }

    private async Task<bool> RunWakeAsync(WakeClaim claim, CancellationToken ct)
    {
        await using (var probe = await factory.CreateDbContextAsync(ct))
        {
            var target = await (from wake in probe.Wakes
                                join participant in probe.Participants on wake.ParticipantId equals participant.Id
                                where wake.Id == claim.Id && wake.Status == "running" && wake.ClaimToken == claim.Token
                                select new { participant.OrganizationId, participant.OrgRole }).SingleOrDefaultAsync(ct);
            if (target is null) return false;
            if (target.OrganizationId is not null && target.OrgRole == OrganizationRoles.Governance)
                return await RunMemberWakeAsync(claim, ct);
            if (target.OrgRole == OrganizationRoles.Executor)
                return await RunExecutorWakeAsync(claim, ct);
        }

        Guid[] sources;
        ChatWake wakeRow;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, ct);
            if (row is null) return false;
            sources = Deserialize(row.ClaimedSourceMessageIdsJson);
            wakeRow = row;
        }

        if (sources.Length == 0)
        {
            await CompleteWakeAsync(claim, ct);
            return true;
        }

        var scope = await OwnerScopeAsync(wakeRow.TenantId, wakeRow.ParticipantId, ct);
        var key = "wake:" + claim.Id.ToString() + ":" + Hash(sources)[..16];
        for (var attempt = 0; ; attempt++)
        {
            TinaChatGenerateIntentRequest request;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == wakeRow.ConversationId && x.TenantId == scope.TenantId, ct)
                    ?? throw Missing();
                var actor = await OwnedAsync(db, scope, wakeRow.ParticipantId, ct);
                RequireInterpreter(actor);
                var member = await MemberAsync(db, conversation.Id, actor.Id, ct);
                if (member.Status != "active") throw Forbidden("The woken participant is no longer an active member.");
                var messages = await SourcesAsync(db, conversation, actor, sources, ct);
                var audience = (await RecipientsAsync(db, scope, conversation, actor, null, messages, derived: true, ct))
                    .Select(x => x.Id).ToArray();
                request = new TinaChatGenerateIntentRequest(actor.Id, key, conversation.Revision, sources, audience);
            }
            try
            {
                await GenerateIntentAsync(scope, wakeRow.ConversationId, request, ct);
                break;
            }
            catch (TinaChatException ex) when (ex.Code == "tina_chat_revision_conflict" && attempt < 2)
            {
                await Task.Delay(50 * (attempt + 1), ct);
            }
        }
        await CompleteWakeAsync(claim, ct);
        return true;
    }

    private async Task<bool> RunExecutorWakeAsync(WakeClaim claim, CancellationToken ct)
    {
        if (services?.GetService(typeof(IExecutorMessageWakeSink)) is not IExecutorMessageWakeSink sink)
            throw new TinaChatException(503, "executor_message_wake_unavailable", "The execution run cannot receive TinaChat messages in this host.");

        Guid runId;
        Guid instanceId;
        Guid sessionId;
        Guid[] sources;
        ChatParticipant participant;
        ChatWake wake;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, ct)
                ?? throw Missing();
            participant = await db.Participants.SingleAsync(x => x.Id == row.ParticipantId, ct);
            var binding = await db.InstanceBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ParticipantId == participant.Id, ct);
            var organization = participant.OrganizationId is { } organizationId
                ? await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId, ct)
                : null;
            if (binding?.RunId is not { } boundRun || binding.InstanceId == Guid.Empty)
                throw new TinaChatException(409, "executor_message_wake_unbound", "The execution member is no longer bound to a live run.");
            if (organization is null) throw new TinaChatException(409, "executor_message_wake_unbound", "The execution member has no session organization.");
            runId = boundRun;
            instanceId = binding.InstanceId;
            sessionId = organization.SessionId;
            sources = Deserialize(row.ClaimedSourceMessageIdsJson);
            wake = row;
        }

        if (sources.Length == 0)
        {
            await CompleteWakeAsync(claim, ct);
            return true;
        }

        var briefing = await BriefingAsync(participant, wake.ConversationId, sources, ct);
        if (!await sink.DeliverAsync(sessionId, runId, instanceId, briefing, ct).ConfigureAwait(false))
            throw new TinaChatException(409, "executor_message_wake_stale", "The execution run could not accept this message at its context boundary.");
        await CompleteWakeAsync(claim, ct);
        return true;
    }

    private async Task<bool> RunMemberWakeAsync(WakeClaim claim, CancellationToken ct)
    {
        var options = wakeOptions?.Value ?? new TinaChatWakeOptions();
        var now = DateTimeOffset.UtcNow;
        Guid[] sources = [];
        ChatParticipant? member = null;
        ChatOrganization? organization = null;
        ChatWake? wakeRow = null;
        var postponed = false;
        var settleOnly = false;
        var claimScope = new TenantContext(Guid.Empty, Guid.Empty, Guid.Empty, "tina-chat-member-turn");
        await WriteAsync(claimScope, async (db, _) =>
        {
            var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, ct) ?? throw Missing();
            var participant = await db.Participants.SingleAsync(x => x.Id == row.ParticipantId, ct);
            var org = participant.OrganizationId is { } orgId ? await db.Organizations.SingleOrDefaultAsync(x => x.Id == orgId, ct) : null;
            wakeRow = row; member = participant; organization = org;
            if (org is null || org.Status != "active" || participant.Status != "active")
            {
                row.SourceMessageIdsJson = "[]";
                row.ClaimedSourceMessageIdsJson = "[]";
                row.Status = "done";
                row.ClaimToken = null;
                row.ClaimedAtUnixMs = null;
                row.UpdatedAt = now;
                settleOnly = true;
                return true;
            }
            RollWindow(participant, now);
            RollWindow(org, now);
            if (participant.TurnsInWindow >= Math.Max(1, options.MemberTurnsPerHour)
                || org.TurnsInWindow >= Math.Max(1, options.OrganizationTurnsPerHour))
            {
                var memberOpens = participant.TurnsInWindow >= options.MemberTurnsPerHour
                    ? participant.TurnWindowStartedAt!.Value.Add(TurnWindow) : now;
                var orgOpens = org.TurnsInWindow >= options.OrganizationTurnsPerHour
                    ? org.TurnWindowStartedAt!.Value.Add(TurnWindow) : now;
                var next = memberOpens > orgOpens ? memberOpens : orgOpens;
                row.SourceMessageIdsJson = Serialize(Merge(Deserialize(row.SourceMessageIdsJson), Deserialize(row.ClaimedSourceMessageIdsJson)));
                row.ClaimedSourceMessageIdsJson = "[]";
                row.Status = "pending";
                row.ClaimToken = null;
                row.ClaimedAtUnixMs = null;
                row.AvailableAt = next;
                row.DueAtUnixMs = next.ToUnixTimeMilliseconds();
                row.LastError = "budget: postponed to the next turn window";
                row.UpdatedAt = now;
                postponed = true;
                return true;
            }
            participant.TurnsInWindow++;
            org.TurnsInWindow++;
            sources = Deserialize(row.ClaimedSourceMessageIdsJson);
            return true;
        }, ct);
        if (postponed || settleOnly) return false;
        if (sources.Length == 0 || member is null || organization is null || wakeRow is null)
        {
            await CompleteWakeAsync(claim, ct);
            return true;
        }

        if (services?.GetService(typeof(ITinaChatMemberTurnRunner)) is not ITinaChatMemberTurnRunner runner)
            throw new TinaChatException(503, "member_turn_runner_unavailable", "No member turn runner is registered in this host, so a standing member cannot take a turn.");

        var scope = new TenantContext(organization.TenantId, organization.WorkspaceId, organization.OwnerPrincipalId, "tina-chat-member-turn");
        var briefing = await BriefingAsync(member, wakeRow.ConversationId, sources, ct);
        var toolbox = new MemberToolbox(this, scope, organization.Id, member.Id);
        await runner.RunAsync(new TinaChatMemberTurn(
            organization.TenantId, organization.WorkspaceId, organization.OwnerPrincipalId, organization.SessionId,
            organization.Id, member.Id, member.DisplayName, member.AgentSlug ?? member.DisplayName, briefing,
            member.CurrentRunId, $"wake:{claim.Id}:{Hash(sources)[..12]}"), toolbox, ct);
        await CompleteWakeAsync(claim, ct);
        return true;
    }

    private static void RollWindow(ChatParticipant participant, DateTimeOffset now)
    {
        if (participant.TurnWindowStartedAt is { } started && now - started < TurnWindow) return;
        participant.TurnWindowStartedAt = now;
        participant.TurnsInWindow = 0;
    }

    private static void RollWindow(ChatOrganization organization, DateTimeOffset now)
    {
        if (organization.TurnWindowStartedAt is { } started && now - started < TurnWindow) return;
        organization.TurnWindowStartedAt = now;
        organization.TurnsInWindow = 0;
    }

    private async Task<string> BriefingAsync(ChatParticipant member, Guid conversationId, Guid[] sources, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == conversationId, ct);
        var rows = await db.Messages.AsNoTracking().Where(x => sources.Contains(x.Id) && x.ConversationId == conversationId)
            .OrderBy(x => x.Sequence).ToArrayAsync(ct);
        var senders = await db.Participants.AsNoTracking().Where(x => rows.Select(r => r.SenderId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var lines = new List<string>();
        foreach (var row in rows)
        {
            var audience = await db.Audiences.AsNoTracking().AnyAsync(x => x.MessageId == row.Id && x.ParticipantId == member.Id && x.CanReadOriginal, ct);
            if (!audience) continue;
            var sender = senders.TryGetValue(row.SenderId, out var found) ? found.DisplayName : "unknown";
            var place = conversation.Kind == "direct" ? "direct" : conversation.Title;
            lines.Add($"[{row.Kind} from {sender} in {place}, message_id {row.Id:N}] {Clip(await ReadBodyAsync(row, ct))}");
        }
        return lines.Count == 0 ? "(the messages that woke you are no longer readable)" : string.Join("\n", lines);
    }

    private async Task<TenantContext> OwnerScopeAsync(Guid tenantId, Guid participantId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var participant = await ParticipantAsync(db, tenantId, participantId, ct);
        if (!await identity.IsWorkspaceMemberAsync(participant.TenantId, participant.WorkspaceId, participant.OwnerPrincipalId, ct))
            throw Forbidden("The participant owner no longer holds an active workspace membership.");
        return new TenantContext(participant.TenantId, participant.WorkspaceId, participant.OwnerPrincipalId, "tina-chat-service");
    }

    private async Task CompleteWakeAsync(WakeClaim claim, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, ct);
        if (row is null) return;
        var arrived = Deserialize(row.SourceMessageIdsJson);
        row.ClaimedSourceMessageIdsJson = "[]";
        row.ClaimToken = null;
        row.ClaimedAtUnixMs = null;
        row.Status = arrived.Length == 0 ? "done" : "pending";
        if (arrived.Length != 0)
        {
            row.AvailableAt = now.Add(WakeCooldown);
            row.DueAtUnixMs = row.AvailableAt.ToUnixTimeMilliseconds();
        }
        row.LastError = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task RequeueClaimAsync(WakeClaim claim, string note, bool terminal, CancellationToken ct, DateTimeOffset? availableAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, ct);
        if (row is null) return;
        row.SourceMessageIdsJson = Serialize(Merge(Deserialize(row.SourceMessageIdsJson), Deserialize(row.ClaimedSourceMessageIdsJson)));
        row.ClaimedSourceMessageIdsJson = "[]";
        row.ClaimToken = null;
        row.ClaimedAtUnixMs = null;
        row.Status = terminal ? "failed" : "pending";
        if (!terminal)
        {
            row.AvailableAt = availableAt ?? now;
            row.DueAtUnixMs = now.ToUnixTimeMilliseconds();
        }
        row.LastError = note;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task FailWakeAsync(WakeClaim claim, Exception ex, CancellationToken ct)
    {
        var attempts = 0;
        await using (var db = await factory.CreateDbContextAsync(ct))
            attempts = await db.Wakes.Where(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token)
                .Select(x => x.Attempts).SingleOrDefaultAsync(ct);
        var terminal = attempts >= MaxWakeAttempts
            || (ex is TinaChatException chat && chat.StatusCode is 400 or 403 or 404 or 409);
        var detail = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        var retryAt = DateTimeOffset.UtcNow.Add(WakeRetryBackoff * Math.Max(1, attempts));
        await RequeueClaimAsync(claim, detail, terminal, ct, retryAt);
    }

    private async Task PruneSettledWakesAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - WakeRetention;
        await using var db = await factory.CreateDbContextAsync(ct);
        var settled = await db.Wakes.Where(x => x.Status == "done" || x.Status == "failed")
            .OrderBy(x => x.Id).Take(500).ToArrayAsync(ct);
        var doomed = settled.Where(x => x.UpdatedAt < cutoff).Select(x => x.Id).ToArray();
        if (doomed.Length > 0) await db.Wakes.Where(x => doomed.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    private static Guid[] Merge(Guid[] left, Guid[] right) => left.Concat(right).Distinct().Order().ToArray();
    private static Guid[] Deserialize(string json) => JsonSerializer.Deserialize<Guid[]>(json, Json) ?? [];
    private static string Serialize(Guid[] ids) => JsonSerializer.Serialize(ids, Json);
}
