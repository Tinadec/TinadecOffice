using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>Administrator-only observation. Does not impersonate participants, acknowledge delivery, or invoke agents.</summary>
public interface ITinaChatObserver
{
    Task<TinaChatObserverAccessDto> GetObserverAccessAsync(CancellationToken ct = default);
    Task<TinaChatObservedConversationPage> ObserveConversationsAsync(string? query = null, string? kind = null,
        Guid? workspaceId = null, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<TinaChatObservedConversationDetail> ObserveConversationAsync(Guid id, CancellationToken ct = default);
    Task<TinaChatObservedMessagePage> ObserveMessagesAsync(Guid id, long? beforeSequence = null,
        long? afterSequence = null, int limit = 50, CancellationToken ct = default);
}

/// <summary>Host adapter resolves actual administrator memberships; caller-supplied roles are not authority.</summary>
public interface ITinaChatObserverAuthority
{
    Task<TinaChatObserverAccessDto?> ResolveObserverAccessAsync(Guid tenantId, Guid principalId, CancellationToken ct);
}
