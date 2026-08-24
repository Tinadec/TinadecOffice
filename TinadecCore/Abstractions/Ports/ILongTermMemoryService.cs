namespace TinadecCore.Abstractions.Ports;

/// <summary>Reviewed long-term memory service. Candidates are never retrieval-visible.</summary>
public interface ILongTermMemoryService
{
    Task<MemoryCandidate> CreateCandidateAsync(MemoryCandidateProposal proposal, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryCandidate>> ListCandidatesAsync(string? status = null, CancellationToken cancellationToken = default);
    Task<MemoryCandidate> DecideCandidateAsync(Guid candidateId, string decision, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LongTermMemoryItem>> ListItemsAsync(CancellationToken cancellationToken = default);
    Task<LongTermMemoryItem> RevokeAsync(Guid itemId, string? reason, CancellationToken cancellationToken = default);
}

public sealed record MemoryCandidateProposal(
    Guid SourceRunId,
    Guid GeneratedByInstanceId,
    string Scope,
    string Kind,
    string Content,
    double Confidence,
    Guid? ProjectId = null,
    Guid? AgentProfileId = null,
    string? Evidence = null,
    string? Applicability = null,
    string? ExpiryCondition = null);

public sealed record MemoryCandidate(
    Guid Id,
    Guid SourceRunId,
    Guid GeneratedByInstanceId,
    string Scope,
    string Kind,
    string Status,
    double Confidence,
    string Content,
    string? DecisionReason,
    Guid? PromotedMemoryItemId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LongTermMemoryItem(
    Guid Id,
    string Scope,
    string Kind,
    string Status,
    int Version,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt);
