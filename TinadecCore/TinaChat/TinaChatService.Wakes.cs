using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.TinaChat;

public sealed partial class TinaChatService
{
    private static readonly TimeSpan WakeCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WakeRetryBackoff = TimeSpan.FromSeconds(30);
    private const int MaxWakeAttempts = 5;
    private const int MaxWakeSources = 32;
    private static readonly TimeSpan WakeRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan TurnWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Queues the turns a committed message owes, inside the caller's Serializable transaction. A
    /// brief is excluded: it is the interpreter's own output and the next move belongs to a human
    /// decision, so waking interpreters on it would only make two of them answer each other.
    ///
    /// Two kinds of recipient are owed a turn: an interpreter (it drafts a brief), and a standing
    /// governance member of the message's organization (it takes a turn in its own context). A
    /// member is only ever a recipient of what was addressed to it — a notice, a direct message, a
    /// mention — because organization rooms freeze no audience beyond the sender and the mentioned;
    /// everything else is read by cursor. That is what keeps a busy room from waking everyone.
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
                || !(recipient.CanInterpretIntent || standing)) continue;
            var audience = audiences.FirstOrDefault(x => x.ParticipantId == recipient.Id);
            if (audience is null) continue;
            if (!audience.CanReadOriginal && !(message.AllowDerivedSharing && audience.CanReceiveDerived)) continue;
            var live = await db.Wakes.SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ParticipantId == recipient.Id
                && x.Reason == "message" && (x.Status == "pending" || x.Status == "running"), ct);
            if (live is not null)
            {
                // Coalesce instead of stacking: one turn reads everything that arrived meanwhile.
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

    /// <summary>Rate limits turns per recipient without ever dropping one; the row stays durable while it waits.</summary>
    private static async Task<DateTimeOffset> NextWakeSlotAsync(TinaChatDbContext db, Guid conversationId, Guid participantId, DateTimeOffset now, CancellationToken ct)
    {
        // SQLite cannot order by DateTimeOffset server-side; settled rows per recipient are few.
        var settled = await db.Wakes.Where(x => x.ConversationId == conversationId && x.ParticipantId == participantId
            && x.Reason == "message" && x.Status != "pending" && x.Status != "running").ToArrayAsync(ct);
        var last = settled.Length == 0 ? default : settled.Max(x => x.UpdatedAt);
        return last == default || last.Add(WakeCooldown) <= now ? now : last.Add(WakeCooldown);
    }

    public async Task<int> ProcessPendingWakesAsync(int maxWakes, CancellationToken ct = default)
    {
        var processed = 0;
        var claimed = await ClaimDueWakesAsync(Math.Clamp(maxWakes, 1, 20), ct);
        // Turns are independent (each has its own context, row and transaction), so they run side by
        // side: an organization must not wait on its slowest member's model call.
        await Parallel.ForEachAsync(claimed, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(wakeOptions?.Value.WakeParallelism ?? 4, 1, 16),
            CancellationToken = ct
        }, async (wakeId, token) =>
        {
            try
            {
                if (await RunWakeAsync(wakeId, token)) Interlocked.Increment(ref processed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await ReleaseWakeAsync(wakeId, "interrupted", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await FailWakeAsync(wakeId, ex, CancellationToken.None);
            }
        });
        await PruneSettledWakesAsync(ct);
        return processed;
    }

    /// <summary>Single-row CAS so a second host, or a second sweep, cannot take the same turn.</summary>
    private async Task<long[]> ClaimDueWakesAsync(int max, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.Wakes.Where(x => x.Status == "pending" && x.DueAtUnixMs <= nowMs)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(max).ToArrayAsync(ct);
        var claimed = new List<long>();
        foreach (var id in ids)
        {
            // The snapshot is frozen here and emptied, so anything arriving during the turn is what
            // the next turn must read; the settle pass decides between done and immediate requeue.
            var won = await db.Wakes.Where(x => x.Id == id && x.Status == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "running")
                    .SetProperty(x => x.UpdatedAt, now), ct);
            if (won == 1) claimed.Add(id);
        }
        return claimed.ToArray();
    }

    private async Task<bool> RunWakeAsync(long wakeId, CancellationToken ct)
    {
        await using (var probe = await factory.CreateDbContextAsync(ct))
        {
            var target = await (from wake in probe.Wakes
                                join participant in probe.Participants on wake.ParticipantId equals participant.Id
                                where wake.Id == wakeId
                                select new { participant.OrganizationId, participant.OrgRole }).SingleOrDefaultAsync(ct);
            if (target is null) return false;
            if (target.OrganizationId is not null && target.OrgRole == OrganizationRoles.Governance)
                return await RunMemberWakeAsync(wakeId, ct);
        }

        Guid[] sources;
        ChatWake wakeRow;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == wakeId, ct);
            if (row is null) return false;
            sources = LimitSources(Deserialize(row.SourceMessageIdsJson));
            row.SourceMessageIdsJson = Serialize([]);
            row.Attempts++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            wakeRow = row;
        }

        if (sources.Length == 0)
        {
            await SettleWakeAsync(wakeId, ct);
            return true;
        }

        var scope = await OwnerScopeAsync(wakeRow.TenantId, wakeRow.ParticipantId, ct);
        var key = "wake:" + wakeId.ToString() + ":" + Hash(sources)[..16];
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
                // Passing no audience asks the same authorization step the save will run, so the
                // brief reaches exactly the members entitled to it and never fails on a guessed list.
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
        await SettleWakeAsync(wakeId, ct);
        return true;
    }

    /// <summary>
    /// A standing member's turn (architecture §6): it reads what woke it and acts with its own tools,
    /// in its own context, outside anybody's run. The hourly budgets are checked and charged in the
    /// same Serializable write that takes the turn's sources, so two hosts cannot both spend one slot,
    /// and a member over budget is postponed to the next window — its notices stay queued, merged.
    /// </summary>
    private async Task<bool> RunMemberWakeAsync(long wakeId, CancellationToken ct)
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
            postponed = false; settleOnly = false;
            var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == wakeId, ct) ?? throw Missing();
            var participant = await db.Participants.SingleAsync(x => x.Id == row.ParticipantId, ct);
            var org = participant.OrganizationId is { } orgId ? await db.Organizations.SingleOrDefaultAsync(x => x.Id == orgId, ct) : null;
            wakeRow = row; member = participant; organization = org;
            if (org is null || org.Status != "active" || participant.Status != "active")
            {
                // An archived organization is read-only: its owed turns are settled, not run.
                row.SourceMessageIdsJson = Serialize([]);
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
                row.Status = "pending";
                row.AvailableAt = next;
                row.DueAtUnixMs = next.ToUnixTimeMilliseconds();
                row.LastError = "budget: postponed to the next turn window";
                row.UpdatedAt = now;
                postponed = true;
                return true;
            }
            participant.TurnsInWindow++;
            org.TurnsInWindow++;
            sources = LimitSources(Deserialize(row.SourceMessageIdsJson));
            row.SourceMessageIdsJson = Serialize([]);
            row.Attempts++;
            row.UpdatedAt = now;
            return true;
        }, ct);
        if (postponed) return false;
        if (settleOnly || sources.Length == 0 || member is null || organization is null || wakeRow is null)
        {
            await SettleWakeAsync(wakeId, ct);
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
            member.CurrentRunId, $"wake:{wakeId}:{Hash(sources)[..12]}"), toolbox, ct);
        await SettleWakeAsync(wakeId, ct);
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

    /// <summary>What woke the member, as it reads it: the queued messages in order, each attributed and clipped.</summary>
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

    /// <summary>The acting identity of a background turn: the participant's own owner, verified against Tenancy.</summary>
    private async Task<TenantContext> OwnerScopeAsync(Guid tenantId, Guid participantId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var participant = await ParticipantAsync(db, tenantId, participantId, ct);
        if (!await identity.IsWorkspaceMemberAsync(participant.TenantId, participant.WorkspaceId, participant.OwnerPrincipalId, ct))
            throw Forbidden("The participant owner no longer holds an active workspace membership.");
        return new TenantContext(participant.TenantId, participant.WorkspaceId, participant.OwnerPrincipalId, "tina-chat-service");
    }

    private async Task SettleWakeAsync(long wakeId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == wakeId, ct);
        if (row is null) return;
        var arrived = Deserialize(row.SourceMessageIdsJson);
        // Material arrived mid-turn: keep the row live so nothing goes unheard, but let the next
        // turn cool down rather than chain straight into it.
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

    private async Task ReleaseWakeAsync(long wakeId, string note, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == wakeId && x.Status == "running", ct);
        if (row is null) return;
        row.Status = "pending";
        row.AvailableAt = now;
        row.DueAtUnixMs = now.ToUnixTimeMilliseconds();
        row.LastError = note;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task FailWakeAsync(long wakeId, Exception ex, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == wakeId, ct);
        if (row is null) return;
        // A withdrawn member, a stripped capability or an unreadable source set will not heal by
        // retrying. A model fault or a lost revision race will, so it stays queued with backoff.
        var terminal = row.Attempts >= MaxWakeAttempts
            || (ex is TinaChatException chat && chat.StatusCode is 400 or 403 or 404 or 409);
        var detail = ex.Message;
        row.Status = terminal ? "failed" : "pending";
        if (!terminal)
        {
            row.AvailableAt = now.Add(WakeRetryBackoff * Math.Max(1, row.Attempts));
            row.DueAtUnixMs = row.AvailableAt.ToUnixTimeMilliseconds();
        }
        row.LastError = detail.Length > 500 ? detail[..500] : detail;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task PruneSettledWakesAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - WakeRetention;
        await using var db = await factory.CreateDbContextAsync(ct);
        // Oldest rows first and a bounded page per pass: the queue of a large organization must not be
        // read whole every few seconds just to find what can be forgotten.
        var settled = await db.Wakes.Where(x => x.Status == "done" || x.Status == "failed")
            .OrderBy(x => x.Id).Take(500).ToArrayAsync(ct);
        var doomed = settled.Where(x => x.UpdatedAt < cutoff).Select(x => x.Id).ToArray();
        if (doomed.Length > 0) await db.Wakes.Where(x => doomed.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    private static Guid[] LimitSources(Guid[] ids) => ids.Distinct().Order().Take(MaxWakeSources).ToArray();
    private static Guid[] Merge(Guid[] left, Guid[] right) => LimitSources(left.Concat(right).ToArray());
    private static Guid[] Deserialize(string json) => JsonSerializer.Deserialize<Guid[]>(json, Json) ?? [];
    private static string Serialize(Guid[] ids) => JsonSerializer.Serialize(ids, Json);
}
