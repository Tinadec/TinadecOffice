using TinadecCore.Contracts.Events;

namespace TinadecCore.Contracts.Dtos;

public sealed record MessageReadDto(Guid Id, Guid SessionId, Guid? RunId, long Sequence, string Role, string Content,
    DateTimeOffset CreatedAt, IReadOnlyList<MessageAttachmentReadDto> Attachments);
public sealed record MessageAttachmentReadDto(Guid Id, string FileName, string MediaType, long ContentLength,
    string ContentHash, DateTimeOffset CreatedAt, DateTimeOffset? BoundAt);
public sealed record SessionRunReadDto(Guid Id, Guid SessionId, Guid TriggerMessageId, string Status, string? Summary,
    long TaskRevision, long LatestEventSequence, DateTimeOffset? LatestEventAt, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt);
public sealed record SessionMessagesPageDto(IReadOnlyList<MessageReadDto> Items, IReadOnlyList<SessionRunReadDto> Runs,
    bool HasMore, long? NextBeforeSequence, long HistoryRevision, long RevertEpoch);
public sealed record SessionActivityDto(IReadOnlyList<EventEnvelope> Events, IReadOnlyList<object> ToolExecutions,
    IReadOnlyList<SessionRunReadDto> Runs);
