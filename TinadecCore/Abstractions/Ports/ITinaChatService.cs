using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>Communication authority. Actors are checked against the verified request principal on every operation.</summary>
public interface ITinaChatService
{
    Task<TinaChatParticipantDto> RegisterAsync(TinaChatRegisterParticipantRequest request, CancellationToken ct = default);
    Task<TinaChatParticipantDto> UpdateParticipantAsync(Guid id, TinaChatUpdateParticipantRequest request, CancellationToken ct = default);
    Task<TinaChatParticipantDto[]> DiscoverAsync(string? query = null, CancellationToken ct = default);
    Task<TinaChatParticipantDto> GetParticipantAsync(Guid id, CancellationToken ct = default);
    Task<TinaChatConversationDto> CreateConversationAsync(TinaChatCreateConversationRequest request, CancellationToken ct = default);
    Task<TinaChatConversationDto[]> ListConversationsAsync(Guid actorId, CancellationToken ct = default);
    Task<TinaChatConversationDto> GetConversationAsync(Guid id, Guid actorId, CancellationToken ct = default);
    Task<TinaChatMemberDto[]> ListMembersAsync(Guid conversationId, Guid actorId, CancellationToken ct = default);
    Task<TinaChatConversationDto> ChangeMemberAsync(Guid conversationId, TinaChatMemberRequest request, CancellationToken ct = default);
    Task<TinaChatMessageDto> SendAsync(Guid conversationId, TinaChatSendMessageRequest request, CancellationToken ct = default);
    Task<TinaChatMessagePage> ReadMessagesAsync(Guid conversationId, Guid actorId, long afterSequence = 0, int limit = 50, CancellationToken ct = default);
    Task<TinaChatInboxPage> ReadInboxAsync(Guid actorId, long afterSequence = 0, int limit = 50, CancellationToken ct = default);
    Task AcknowledgeAsync(Guid actorId, Guid messageId, CancellationToken ct = default);
    Task<TinaChatWorkspacePolicyDto> GetPolicyAsync(CancellationToken ct = default);
    Task<TinaChatWorkspacePolicyDto> SetPolicyAsync(TinaChatWorkspacePolicyRequest request, CancellationToken ct = default);
    Task<TinaChatIntentDto> ProposeIntentAsync(Guid conversationId, TinaChatProposeIntentRequest request, CancellationToken ct = default);
    Task<TinaChatIntentDto> GenerateIntentAsync(Guid conversationId, TinaChatGenerateIntentRequest request, CancellationToken ct = default);
    Task<TinaChatIntentDto[]> ListIntentsAsync(Guid conversationId, Guid actorId, CancellationToken ct = default);
    Task<TinaChatIntentDto> DecideIntentAsync(Guid conversationId, Guid intentId, TinaChatIntentDecisionRequest request, CancellationToken ct = default);
    Task<TinaChatExecutionReservation> ReserveExecutionAsync(Guid conversationId, Guid intentId, TinaChatExecuteIntentRequest request, CancellationToken ct = default);
    Task<TinaChatExecutionDto> RecordExecutionAsync(Guid executionId, Guid runId, CancellationToken ct = default);
}

/// <summary>Implemented by composition adapters. No module reads another module's DbContext.</summary>
public interface ITinaChatIdentityBoundary
{
    Task<bool> IsWorkspaceMemberAsync(Guid tenantId, Guid workspaceId, Guid principalId, CancellationToken ct);
    Task<bool> IsWorkspaceAdministratorAsync(Guid tenantId, Guid workspaceId, Guid principalId, CancellationToken ct);
    Task<bool> IsAgentDefinitionAvailableAsync(Guid tenantId, Guid workspaceId, Guid definitionId, CancellationToken ct);
}

public interface ITinaChatIntentInterpreter
{
    Task<TinaChatIntentContent> InterpretAsync(TinaChatInterpretationInput input, CancellationToken ct);
}

public sealed record TinaChatInterpretationInput(string AuthorName, string? JobTitle, string? Description, TinaChatMessageDto[] Sources);

public interface ITinaChatRunService
{
    Task<TinaChatExecutionDto> ExecuteAsync(Guid conversationId, Guid intentId, TinaChatExecuteIntentRequest request, CancellationToken ct = default);
}

/// <summary>Trusted runtime reader. A bound session never falls back to raw chat when authorization is lost.</summary>
public interface ITinaChatRunInput
{
    Task<TinaChatExecutionReservation?> GetForSessionAsync(Guid sessionId, CancellationToken ct = default);
}

public sealed record TinaChatExecutionReservation(
    TinaChatExecutionDto Execution, Guid ModeVersionId, Guid? ProjectId,
    string ClientMessageId, string Content);

public sealed record TinaChatInputBinding(Guid ExecutionId, Guid ParticipantId, Guid IntentId);

public sealed class TinaChatException(int statusCode, string code, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
