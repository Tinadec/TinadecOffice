using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.TinaChat;

/// <summary>
/// The session owner's view of its organization. The owner acts as the organization's human member,
/// with that member's rules (it sees every room, posts where it is a member, decides reports), so the
/// UI and the agents share one set of rules instead of the UI having a back door.
/// </summary>
public sealed partial class TinaChatService : ISessionOrganizationView
{
    private const int ViewMemberLimit = 200;
    private const int ViewRoomLimit = 100;

    async Task<OrganizationDto?> ISessionOrganizationView.GetAsync(Guid sessionId, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await OwnedOrganizationAsync(db, scope, sessionId, ct);
        if (org is null) return null;
        var members = await db.Participants.AsNoTracking().Where(x => x.OrganizationId == org.Id && x.OrgRole != OrganizationRoles.Host)
            .OrderBy(x => x.Presence == Online ? 0 : 1).ThenBy(x => x.OrgRole).ThenBy(x => x.DisplayName)
            .Take(ViewMemberLimit + 1).ToArrayAsync(ct);
        var rooms = await db.Conversations.AsNoTracking().Where(x => x.OrganizationId == org.Id && x.Kind != "direct")
            .OrderBy(x => x.Kind).ThenBy(x => x.Title).Take(ViewRoomLimit + 1).ToArrayAsync(ct);
        var roomIds = rooms.Select(x => x.Id).ToArray();
        var counts = await db.Members.AsNoTracking().Where(x => roomIds.Contains(x.ConversationId) && x.Status == "active")
            .GroupBy(x => x.ConversationId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var mine = await db.Members.AsNoTracking().Where(x => roomIds.Contains(x.ConversationId) && x.ParticipantId == org.HumanParticipantId && x.Status == "active")
            .Select(x => x.ConversationId).ToArrayAsync(ct);
        var open = await db.Reports.AsNoTracking().CountAsync(x => x.OrganizationId == org.Id && x.Status == "open", ct);
        return new OrganizationDto(org.Id, org.SessionId, org.Status, org.HumanParticipantId,
            members.Take(ViewMemberLimit).Select(x => new OrganizationMemberDto(x.Id, x.Handle, x.DisplayName, x.OrgRole ?? "",
                x.Presence ?? Offline, x.AgentSlug, x.ParentParticipantId, x.CurrentRunId, x.VisibilityScope)).ToArray(),
            rooms.Take(ViewRoomLimit).Select(x => new OrganizationRoomDto(x.Id, x.Kind, x.Title, x.LastSequence,
                counts.GetValueOrDefault(x.Id), mine.Contains(x.Id), x.PlanOwnerId)).ToArray(),
            open, members.Length > ViewMemberLimit, rooms.Length > ViewRoomLimit);
    }

    public async Task<OrganizationMessagePage> ReadRoomAsync(Guid sessionId, Guid roomId, long afterSequence = 0, int limit = 50, CancellationToken ct = default)
    {
        ValidatePage(afterSequence, limit);
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await OwnedOrganizationAsync(db, scope, sessionId, ct) ?? throw Missing();
        var human = await db.Participants.AsNoTracking().SingleAsync(x => x.Id == org.HumanParticipantId, ct);
        var room = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId && x.OrganizationId == org.Id, ct) ?? throw Missing();
        var rows = await db.Messages.AsNoTracking().Where(x => x.ConversationId == room.Id && x.Sequence > afterSequence)
            .OrderBy(x => x.Sequence).Take(limit * 2).ToArrayAsync(ct);
        var page = new List<ChatMessage>();
        var cursor = afterSequence;
        foreach (var row in rows)
        {
            cursor = row.Sequence;
            if (await CanReadOrganizationMessageAsync(db, human, row, ct)) page.Add(row);
            if (page.Count == limit) break;
        }
        return new OrganizationMessagePage(await ViewMessagesAsync(db, org, page, ct), cursor);
    }

    public async Task<OrganizationMessageDto> PostAsync(Guid sessionId, Guid roomId, OrganizationPostRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = Required(request.Content, "content", 16384);
        var key = "view:" + Hash(Required(request.ClientMessageId, "client_message_id", 128));
        var scope = await ScopeAsync(ct);
        var messageId = await WriteAsync(scope, async (db, _) =>
        {
            var org = await OwnedOrganizationAsync(db, scope, sessionId, ct) ?? throw Missing();
            RequireWritable(org);
            var human = await db.Participants.SingleAsync(x => x.Id == org.HumanParticipantId, ct);
            var room = await db.Conversations.SingleOrDefaultAsync(x => x.Id == roomId && x.OrganizationId == org.Id, ct) ?? throw Missing();
            await RequirePostableAsync(db, room, human, ct);
            var existing = await db.Messages.SingleOrDefaultAsync(x => x.ConversationId == room.Id && x.SenderId == human.Id && x.ClientMessageId == key, ct);
            if (existing is not null) return existing.Id;
            var recipients = new List<ChatParticipant> { human };
            recipients.AddRange(await MentionedAsync(db, org, human, room, request.Mention ?? [], ct));
            if (request.ReplyToMessageId is { } reply && !await db.Messages.AnyAsync(x => x.Id == reply && x.ConversationId == room.Id, ct))
                throw Invalid("reply_to_message_id must be a message in the same room.");
            var row = await AppendMessageAsync(db, room, human, content, key,
                Hash(new { content, room.Id, request.ReplyToMessageId, mention = recipients.Select(x => x.Id).Order().ToArray() }),
                "message", "normal", allowDerivedSharing: false, request.ReplyToMessageId, sources: [], recipients: recipients.DistinctBy(x => x.Id).ToArray(), ct);
            return row.Id;
        }, ct);
        await using var read = await factory.CreateDbContextAsync(ct);
        var organization = await OwnedOrganizationAsync(read, scope, sessionId, ct) ?? throw Missing();
        var message = await read.Messages.AsNoTracking().SingleAsync(x => x.Id == messageId, ct);
        return (await ViewMessagesAsync(read, organization, [message], ct))[0];
    }

    public async Task<OrganizationReportPage> ListReportsAsync(Guid sessionId, string? status = null, int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 200) throw Invalid("limit must be between 1 and 200.");
        if (status is not (null or "open" or "acted" or "dismissed" or "superseded")) throw Invalid("status must be open, acted, dismissed or superseded.");
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await OwnedOrganizationAsync(db, scope, sessionId, ct);
        if (org is null) return new OrganizationReportPage([], false);
        var query = db.Reports.AsNoTracking().Where(x => x.OrganizationId == org.Id);
        if (status is not null) query = query.Where(x => x.Status == status);
        // SQLite cannot order by DateTimeOffset; a bounded page is sorted in memory.
        var rows = await query.Take(1000).ToArrayAsync(ct);
        var ordered = rows.OrderBy(x => x.Status == "open" ? 0 : 1).ThenBy(x => SeverityRank(x.Severity)).ThenByDescending(x => x.CreatedAt).ToArray();
        var names = await NamesAsync(db, org.Id, ordered.Take(limit).Select(x => x.AuthorId), ct);
        return new OrganizationReportPage(ordered.Take(limit).Select(x => ToReportDto(x, names)).ToArray(), ordered.Length > limit);
    }

    public async Task<OrganizationReportDto> DecideReportAsync(Guid sessionId, Guid reportId, OrganizationReportDecisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Decision is not ("acted" or "dismissed")) throw Invalid("decision must be acted or dismissed.");
        var note = Optional(request.Note, "note", 2000) ?? "";
        var scope = await ScopeAsync(ct);
        var decided = await WriteAsync(scope, async (db, _) =>
        {
            var org = await OwnedOrganizationAsync(db, scope, sessionId, ct) ?? throw Missing();
            RequireWritable(org);
            var human = await db.Participants.SingleAsync(x => x.Id == org.HumanParticipantId, ct);
            var report = await DecideReportCoreAsync(db, org, human, reportId, request.Decision, note, request.ExpectedRevision, ct);
            return (report.MessageId, org.Id);
        }, ct);
        await using var read = await factory.CreateDbContextAsync(ct);
        var row = await read.Reports.AsNoTracking().SingleAsync(x => x.MessageId == decided.MessageId, ct);
        return ToReportDto(row, await NamesAsync(read, decided.Id, [row.AuthorId], ct));
    }

    public async Task<OrganizationMemberDto> SetMemberVisibilityAsync(Guid sessionId, Guid participantId, OrganizationMemberVisibilityRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = request.VisibilityScope?.Trim();
        if (scope is not (null or "down" or "own")) throw Invalid("visibility_scope must be \"down\", \"own\", or null.");
        var tenant = await ScopeAsync(ct);
        ChatParticipant member;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var org = await OwnedOrganizationAsync(db, tenant, sessionId, ct) ?? throw Missing();
            RequireWritable(org);
            member = await db.Participants.SingleOrDefaultAsync(x => x.OrganizationId == org.Id && x.Id == participantId, ct) ?? throw Missing();
            // Machine members only: hiding the user's or the host's own read would blind the panel.
            if (member.OrgRole is OrganizationRoles.Human or OrganizationRoles.Host)
                throw Invalid("The owner's and the host's visibility cannot be changed.");
            // "down" is the default and means unrestricted: store null so the row says exactly that.
            member.VisibilityScope = string.Equals(scope, "own", StringComparison.OrdinalIgnoreCase) ? "own" : null;
            member.Revision++;
            await db.SaveChangesAsync(ct);
        }
        return new OrganizationMemberDto(member.Id, member.Handle, member.DisplayName, member.OrgRole ?? "",
            member.Presence ?? Offline, member.AgentSlug, member.ParentParticipantId, member.CurrentRunId, member.VisibilityScope);
    }

    /// <summary>The organization of a session the caller owns; null otherwise, without saying whether it exists.</summary>
    private static async Task<ChatOrganization?> OwnedOrganizationAsync(TinaChatDbContext db, TenantContext scope, Guid sessionId, CancellationToken ct)
    {
        var org = await db.Organizations.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.SessionId == sessionId, ct);
        return org is not null && org.WorkspaceId == scope.WorkspaceId && org.OwnerPrincipalId == scope.PrincipalId ? org : null;
    }

    private async Task<OrganizationMessageDto[]> ViewMessagesAsync(TinaChatDbContext db, ChatOrganization org, IReadOnlyList<ChatMessage> rows, CancellationToken ct)
    {
        var senders = await db.Participants.AsNoTracking().Where(x => x.OrganizationId == org.Id && rows.Select(r => r.SenderId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var reportIds = rows.Where(x => x.Kind == "report").Select(x => x.Id).ToArray();
        var reports = reportIds.Length == 0
            ? new Dictionary<Guid, ChatReport>()
            : await db.Reports.AsNoTracking().Where(x => reportIds.Contains(x.MessageId)).ToDictionaryAsync(x => x.MessageId, ct);
        var names = senders.ToDictionary(x => x.Key, x => x.Value.DisplayName);
        var result = new List<OrganizationMessageDto>();
        foreach (var row in rows)
        {
            senders.TryGetValue(row.SenderId, out var sender);
            reports.TryGetValue(row.Id, out var report);
            result.Add(new OrganizationMessageDto(row.Id, row.ConversationId, row.SenderId, sender?.DisplayName ?? "unknown",
                sender?.OrgRole ?? "", row.Sequence, row.Kind, await ReadBodyAsync(row, ct), row.Sensitivity, row.CreatedAt,
                report is null ? null : ToReportDto(report, names)));
        }
        return result.ToArray();
    }

    private static OrganizationReportDto ToReportDto(ChatReport x, IReadOnlyDictionary<Guid, string> names) => new(
        x.MessageId, x.ConversationId, x.AuthorId, names.GetValueOrDefault(x.AuthorId) ?? "unknown", x.ReportKind, x.Severity, x.Status,
        x.SubjectKind, x.SubjectId, x.ProposedVerb, x.ProposedArgs, x.Finding,
        JsonSerializer.Deserialize<string[]>(x.EvidenceJson, Json) ?? [], x.Revision, x.CreatedAt, x.DecidedById, x.DecisionNote,
        x.DecidedAt, x.SupersedesMessageId);
}
