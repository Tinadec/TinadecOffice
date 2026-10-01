using System.Text;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.TinaChat;

public sealed partial class TinaChatService : ITinaChatExecutionResults
{
    /// <summary>
    /// Host sweep over admitted handoffs, deliberately not filtered by the ambient request scope:
    /// a run reaches its terminal state in whichever tenant admitted it. Every row this returns is
    /// re-authorized participant-by-participant in <see cref="RecordResultAsync"/>.
    /// </summary>
    public async Task<TinaChatOpenExecution[]> ListOpenExecutionsAsync(int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Executions.AsNoTracking()
            .Where(x => x.RunId != null && x.ResultMessageId == null)
            .OrderBy(x => x.Id)
            .Take(Math.Clamp(max, 1, 50)).ToArrayAsync(ct);
        return rows.Select(x => new TinaChatOpenExecution(x.Id, x.ConversationId, x.ParticipantId, x.RunId!.Value)).ToArray();
    }

    /// <summary>
    /// Posts the outcome of a handoff back to the conversation as the receiving participant's own
    /// statement, heard only by the members who were entitled to the brief it answers.
    /// </summary>
    public async Task<TinaChatExecutionResultOutcome> RecordResultAsync(Guid executionId, TinaChatRunOutcome outcome, CancellationToken ct = default)
    {
        ChatExecution execution;
        await using (var db = await factory.CreateDbContextAsync(ct))
            execution = await db.Executions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == executionId, ct) ?? throw Missing();
        if (execution.ResultMessageId is not null) return TinaChatExecutionResultOutcome.AlreadyPosted;

        TenantContext scope;
        try
        {
            scope = await OwnerScopeAsync(execution.TenantId, execution.ParticipantId, ct);
        }
        catch (TinaChatException)
        {
            return TinaChatExecutionResultOutcome.Undeliverable;
        }

        return await WriteAsync(scope, async (db, current) =>
        {
            var row = await db.Executions.SingleOrDefaultAsync(x => x.Id == executionId, ct) ?? throw Missing();
            if (row.ResultMessageId is not null) return TinaChatExecutionResultOutcome.AlreadyPosted;
            var actor = await OwnedAsync(db, current, row.ParticipantId, ct);
            var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == row.ConversationId && x.TenantId == current.TenantId, ct) ?? throw Missing();
            var membership = await MemberAsync(db, conversation.Id, actor.Id, ct);
            if (actor.Status != "active" || membership.Status != "active") return TinaChatExecutionResultOutcome.Undeliverable;

            var intent = await db.Intents.SingleAsync(x => x.Id == row.IntentId, ct);
            var brief = await db.Messages.SingleAsync(x => x.Id == intent.MessageId, ct);
            // The outcome speaks about the brief, so a reporter that can no longer read it has
            // nothing it is entitled to say. The run still stands; only the announcement stops.
            if (!await CanReadAsync(db, conversation, actor, brief, ct)) return TinaChatExecutionResultOutcome.Undeliverable;
            var audience = await EntitledAudienceAsync(db, current, conversation, actor, brief, ct);
            if (audience.Length == 0) return TinaChatExecutionResultOutcome.Undeliverable;
            var message = await AppendMessageAsync(db, conversation, actor, RenderOutcome(outcome),
                "result:" + row.Id.ToString("N"), Hash(new { row.Id, outcome.Status, outcome.Summary, outcome.ErrorCategory }),
                "result", brief.Sensitivity, false, null, new[] { brief.Id }, audience, ct);
            row.ResultMessageId = message.Id;
            row.ResultRunStatus = outcome.Status;
            row.Revision++;
            Audit(db, current, actor.Id, "execution.result_posted", row.Id, conversation.Id);
            return TinaChatExecutionResultOutcome.Posted;
        }, ct);
    }

    /// <summary>
    /// Narrows the brief's audience to the members that still satisfy every check the send step
    /// applies, so a revoked member simply stops hearing instead of failing the delivery.
    /// </summary>
    private async Task<ChatParticipant[]> EntitledAudienceAsync(TinaChatDbContext db, TenantContext scope, ChatConversation conversation,
        ChatParticipant actor, ChatMessage brief, CancellationToken ct)
    {
        var entitled = await db.Audiences.Where(x => x.MessageId == brief.Id && x.CanReadOriginal).Select(x => x.ParticipantId).ToListAsync(ct);
        var permitted = new List<ChatParticipant>();
        foreach (var id in entitled.Append(actor.Id).Distinct())
        {
            var participant = await db.Participants.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == scope.TenantId, ct);
            if (participant is null || participant.Status != "active") continue;
            var member = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ParticipantId == id && x.Status == "active", ct);
            if (member is null) continue;
            if (!await identity.IsWorkspaceMemberAsync(scope.TenantId, participant.WorkspaceId, participant.OwnerPrincipalId, ct)) continue;
            if (!await CanCommunicateAsync(db, conversation, actor.WorkspaceId, participant.WorkspaceId, ct)) continue;
            if (!await CanReadAsync(db, conversation, participant, brief, ct)) continue;
            permitted.Add(participant);
        }
        return permitted.ToArray();
    }

    private static string RenderOutcome(TinaChatRunOutcome outcome)
    {
        var text = new StringBuilder("Handoff run " + outcome.Status + ".");
        if (!string.IsNullOrWhiteSpace(outcome.Summary)) text.AppendLine().AppendLine(outcome.Summary.Trim());
        if (!string.IsNullOrWhiteSpace(outcome.ErrorCategory)) text.AppendLine().Append("Error category: ").Append(outcome.ErrorCategory.Trim());
        var result = text.ToString().Trim();
        return result.Length > 60000 ? result[..60000] : result;
    }
}
