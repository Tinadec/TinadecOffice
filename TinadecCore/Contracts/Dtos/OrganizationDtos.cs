using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// A session's organization as its owner sees it: the members (with presence and role), the rooms
/// the owner can read, and how many reports are open. Bounded like every other session view.
/// </summary>
public sealed record OrganizationDto(
    Guid Id,
    Guid SessionId,
    string Status,
    Guid YouParticipantId,
    OrganizationMemberDto[] Members,
    OrganizationRoomDto[] Rooms,
    int OpenReports,
    bool MembersTruncated,
    bool RoomsTruncated);

public sealed record OrganizationMemberDto(
    Guid Id,
    string Handle,
    string DisplayName,
    string Role,
    string Presence,
    string? AgentSlug,
    Guid? ParentId,
    Guid? RunId,
    /// <summary>Per-member visibility into run internals (todo E5): "down" (default) or "own"; null = unrestricted.</summary>
    string? VisibilityScope = null);

/// <summary>The user sets one member's visibility (todo E5): "down", "own", or null to clear.</summary>
public sealed record OrganizationMemberVisibilityRequest(string? VisibilityScope);

public sealed record OrganizationRoomDto(
    Guid Id,
    string Kind,
    string Title,
    long LastSequence,
    int MemberCount,
    bool IsMember,
    Guid? PlanOwnerId);

public sealed record OrganizationMessageDto(
    Guid Id,
    Guid RoomId,
    Guid SenderId,
    string SenderDisplayName,
    string SenderRole,
    long Sequence,
    string Kind,
    string Content,
    string Sensitivity,
    DateTimeOffset CreatedAt,
    OrganizationReportDto? Report);

public sealed record OrganizationMessagePage(OrganizationMessageDto[] Items, long NextCursor);

/// <summary>
/// A report (architecture §9.3): a governance member's observation and judgement about one part of
/// the graph. It changes nothing by itself; a proposed verb is executed, if at all, by whoever holds
/// that verb, and the decision is recorded against the revision it was read at.
/// </summary>
public sealed record OrganizationReportDto(
    Guid Id,
    Guid RoomId,
    Guid AuthorId,
    string AuthorDisplayName,
    string ReportKind,
    string Severity,
    string Status,
    string? SubjectKind,
    string? SubjectId,
    string? ProposedVerb,
    string? ProposedArgsJson,
    string Finding,
    string[] Evidence,
    long Revision,
    DateTimeOffset CreatedAt,
    Guid? DecidedById,
    string? DecisionNote,
    DateTimeOffset? DecidedAt,
    Guid? SupersedesReportId,
    OrganizationSubjectStateDto? SubjectState = null);

/// <summary>The current state of what a report is about, resolved at read time so the UI never shows a stale sentence as fact.</summary>
public sealed record OrganizationSubjectStateDto(string Kind, string Id, string? Status, string? Label);

public sealed record OrganizationReportPage(OrganizationReportDto[] Items, bool Truncated);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationPostRequest(string Content, string ClientMessageId, Guid? ReplyToMessageId = null, string[]? Mention = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganizationReportDecisionRequest(string Decision, long ExpectedRevision, string? Note = null);
