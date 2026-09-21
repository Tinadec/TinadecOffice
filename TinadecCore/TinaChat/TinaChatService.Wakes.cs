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

    /// <summary>
    /// Queues the turns a committed message owes, inside the caller's Serializable transaction. A
    /// brief is excluded: it is the interpreter's own output and the next move belongs to a human
    /// decision, so waking interpreters on it would only make two of them answer each other.
    /// </summary>
    private static async Task EnqueueWakesAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant sender,
        ChatMessage message, ChatParticipant[] recipients, List<ChatAudience> audiences, CancellationToken ct)
    {
        if (message.Kind == "intent_brief") return;
        var now = DateTimeOffset.UtcNow;
        foreach (var recipient in recipients)
        {
            if (recipient.Id == sender.Id || recipient.Kind != "agent" || recipient.Status != "active" || !recipient.CanInterpretIntent) continue;
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
            db.Wakes.Add(new ChatWake
            {
                TenantId = conversation.TenantId, WorkspaceId = recipient.WorkspaceId, ConversationId = conversation.Id,
                ParticipantId = recipient.Id, Reason = "message", Status = "pending",
                SourceMessageIdsJson = Serialize(new[] { message.Id }),
                AvailableAt = await NextWakeSlotAsync(db, conversation.Id, recipient.Id, now, ct),
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
        foreach (var wakeId in await ClaimDueWakesAsync(Math.Clamp(maxWakes, 1, 20), ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RunWakeAsync(wakeId, ct)) processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ReleaseWakeAsync(wakeId, "interrupted", ct);
                throw;
            }
            catch (Exception ex)
            {
                await FailWakeAsync(wakeId, ex, ct);
            }
        }
        await PruneSettledWakesAsync(ct);
        return processed;
    }

    /// <summary>Single-row CAS so a second host, or a second sweep, cannot take the same turn.</summary>
    private async Task<long[]> ClaimDueWakesAsync(int max, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        var due = await db.Wakes.Where(x => x.Status == "pending").ToArrayAsync(ct);
        var ids = due.Where(x => x.AvailableAt <= now).OrderBy(x => x.Id).Select(x => x.Id).Take(max).ToArray();
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
        Guid[] sources;
        ChatWake wake;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var row = await db.Wakes.SingleOrDefaultAsync(x => x.Id == wakeId, ct);
            if (row is null) return false;
            sources = LimitSources(Deserialize(row.SourceMessageIdsJson));
            row.SourceMessageIdsJson = Serialize([]);
            row.Attempts++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            wake = row;
        }

        if (sources.Length == 0)
        {
            await SettleWakeAsync(wakeId, ct);
            return true;
        }

        var scope = await OwnerScopeAsync(wake.TenantId, wake.ParticipantId, ct);
        var key = "wake:" + wakeId.ToString() + ":" + Hash(sources)[..16];
        for (var attempt = 0; ; attempt++)
        {
            TinaChatGenerateIntentRequest request;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == wake.ConversationId && x.TenantId == scope.TenantId, ct)
                    ?? throw Missing();
                var actor = await OwnedAsync(db, scope, wake.ParticipantId, ct);
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
                await GenerateIntentAsync(scope, wake.ConversationId, request, ct);
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
        row.AvailableAt = arrived.Length == 0 ? row.AvailableAt : now.Add(WakeCooldown);
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
        row.AvailableAt = terminal ? row.AvailableAt : now.Add(WakeRetryBackoff * row.Attempts);
        row.LastError = detail.Length > 500 ? detail[..500] : detail;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task PruneSettledWakesAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - WakeRetention;
        await using var db = await factory.CreateDbContextAsync(ct);
        var settled = await db.Wakes.Where(x => x.Status == "done" || x.Status == "failed").ToArrayAsync(ct);
        var doomed = settled.Where(x => x.UpdatedAt < cutoff).Select(x => x.Id).ToArray();
        if (doomed.Length > 0) await db.Wakes.Where(x => doomed.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    private static Guid[] LimitSources(Guid[] ids) => ids.Distinct().Order().Take(MaxWakeSources).ToArray();
    private static Guid[] Merge(Guid[] left, Guid[] right) => LimitSources(left.Concat(right).ToArray());
    private static Guid[] Deserialize(string json) => JsonSerializer.Deserialize<Guid[]>(json, Json) ?? [];
    private static string Serialize(Guid[] ids) => JsonSerializer.Serialize(ids, Json);
}
