using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatRegisterParticipantRequest(
    string Handle,
    string DisplayName,
    string Kind = "agent",
    string? JobTitle = null,
    string? Description = null,
    Guid? AgentDefinitionId = null,
    bool ReceiveHumanMessages = false,
    bool CanInterpretIntent = false,
    bool Discoverable = true);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatUpdateParticipantRequest(
    long ExpectedRevision,
    string DisplayName,
    string? JobTitle = null,
    string? Description = null,
    bool ReceiveHumanMessages = false,
    bool CanInterpretIntent = false,
    bool Discoverable = true,
    string Status = "active");

public sealed record TinaChatParticipantDto(
    Guid Id, Guid WorkspaceId, string Handle, string DisplayName, string Kind,
    string? JobTitle, string? Description, Guid? AgentDefinitionId,
    bool ReceiveHumanMessages, bool CanInterpretIntent, bool Discoverable,
    string Status, long Revision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatCreateConversationRequest(
    Guid ActorId, string Title, Guid[] ParticipantIds, string Kind = "group",
    bool AllowCrossWorkspace = false, string? ClientRequestId = null);

public sealed record TinaChatConversationDto(
    Guid Id, Guid WorkspaceId, string Title, string Kind, bool AllowCrossWorkspace,
    long Revision, long LastSequence, Guid? AcceptedIntentId, string MembershipStatus);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatMemberRequest(
    Guid ActorId, Guid ParticipantId, string Action, long ExpectedRevision,
    string Role = "member");

public sealed record TinaChatMemberDto(Guid ParticipantId, string Role, string Status, long JoinedAfterSequence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatSendMessageRequest(
    Guid ActorId, string Content, string ClientMessageId,
    Guid[]? AudienceParticipantIds = null,
    Guid? ReplyToMessageId = null,
    Guid[]? SourceMessageIds = null,
    bool AllowDerivedSharing = false,
    string Sensitivity = "normal");

public sealed record TinaChatMessageDto(
    Guid Id, Guid ConversationId, Guid SenderId, string SenderKind, long Sequence,
    string Kind, string Content, string Sensitivity, bool AllowDerivedSharing,
    Guid? ReplyToMessageId, Guid[] SourceMessageIds, DateTimeOffset CreatedAt);

public sealed record TinaChatMessagePage(TinaChatMessageDto[] Items, long NextCursor);
public sealed record TinaChatInboxItem(TinaChatMessageDto Message, bool Acknowledged);
public sealed record TinaChatInboxPage(TinaChatInboxItem[] Items, long NextCursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatWorkspacePolicyRequest(
    long ExpectedRevision, bool AllowCrossWorkspaceDiscovery = false,
    bool AllowCrossWorkspaceMessaging = false);

public sealed record TinaChatWorkspacePolicyDto(
    Guid WorkspaceId, long Revision, bool AllowCrossWorkspaceDiscovery, bool AllowCrossWorkspaceMessaging);

/// <summary>User statements remain claims, not verified facts. Blocking questions prevent execution.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatIntentContent(
    string Goal, string[] UserStatements, string[] Constraints, string[] Assumptions,
    string[] OpenQuestions, string[] BlockingQuestions, string[] AcceptanceCriteria);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatProposeIntentRequest(
    Guid ActorId, string ClientRequestId, long ExpectedRevision,
    Guid[] SourceMessageIds, Guid[] AudienceParticipantIds, TinaChatIntentContent Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatGenerateIntentRequest(
    Guid ActorId, string ClientRequestId, long ExpectedRevision,
    Guid[] SourceMessageIds, Guid[] AudienceParticipantIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatIntentDecisionRequest(Guid ActorId, string Decision, long ExpectedRevision);

public sealed record TinaChatIntentDto(
    Guid Id, Guid ConversationId, Guid AuthorId, long Revision, string Status,
    TinaChatIntentContent Content, Guid[] SourceMessageIds, Guid? DecidedById, DateTimeOffset CreatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TinaChatExecuteIntentRequest(
    Guid ActorId, Guid ModeVersionId, Guid? ProjectId = null);

public sealed record TinaChatExecutionDto(
    Guid Id, Guid ConversationId, Guid IntentId, Guid ParticipantId,
    Guid SessionId, Guid? RunId, string Status);
