namespace TinadecCore.Contracts.Dtos;

public sealed record TinaChatObserverWorkspaceDto(Guid Id, string Name);
public sealed record TinaChatObserverAccessDto(string Level, TinaChatObserverWorkspaceDto[] Workspaces);

public sealed record TinaChatObservedConversationDto(
    Guid Id, Guid WorkspaceId, string WorkspaceName, string Title, string Kind,
    long Revision, long LastSequence, int MemberCount, string[] ParticipantNames,
    string? LastMessagePreview, DateTimeOffset? LastMessageAt);

public sealed record TinaChatObservedConversationPage(
    TinaChatObservedConversationDto[] Items, int Total, int NextOffset, bool HasMore);

public sealed record TinaChatObservedMemberDto(
    TinaChatParticipantDto Participant, string Role, string Status, long JoinedAfterSequence);

public sealed record TinaChatObservedConversationDetail(
    TinaChatObservedConversationDto Conversation, TinaChatObservedMemberDto[] Members);

/// <summary>Persisted delivery grants, not a claim that the recipient still has access or understood the message.</summary>
public sealed record TinaChatObservedAudienceDto(
    TinaChatParticipantDto Participant, bool CanReadOriginal, bool CanReceiveDerived, bool Acknowledged);

public sealed record TinaChatObservedMessageDto(
    TinaChatMessageDto Message, TinaChatParticipantDto Sender, TinaChatObservedAudienceDto[] Audience,
    Guid? IntentId, string? IntentStatus);

/// <summary>Items are chronological. before_sequence reads older history; after_sequence follows new messages.</summary>
public sealed record TinaChatObservedMessagePage(
    TinaChatObservedMessageDto[] Items, long OldestSequence, long NewestSequence, bool HasMore);
