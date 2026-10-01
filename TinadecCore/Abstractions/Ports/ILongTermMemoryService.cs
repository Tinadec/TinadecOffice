namespace TinadecCore.Abstractions.Ports;

/// <summary>Reviewed long-term memory service. Candidates are never retrieval-visible.</summary>
public interface ILongTermMemoryService
{
    Task<MemoryCandidate> CreateCandidateAsync(MemoryCandidateProposal proposal, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryCandidate>> ListCandidatesAsync(MemoryCandidateQuery? query = null, CancellationToken cancellationToken = default);
    Task<MemoryCandidate> DecideCandidateAsync(Guid candidateId, string decision, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LongTermMemoryItem>> ListItemsAsync(MemoryItemQuery? query = null, CancellationToken cancellationToken = default);
    Task<LongTermMemoryItem> RevokeAsync(Guid itemId, string? reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-side narrowing for the candidate queue. Every member is optional; null means
/// "do not narrow". Filters are applied in the database before the per-row content
/// read, so narrowing also bounds the work a review page costs. Closed vocabularies
/// are validated by <see cref="ReviewVocabulary"/> before they reach the query.
/// </summary>
public sealed record MemoryCandidateQuery(
    string? Status = null,
    string? Scope = null,
    string? Kind = null,
    Guid? RunId = null,
    Guid? ProjectId = null,
    int? Limit = null);

/// <summary>Read-side narrowing for promoted memory.</summary>
public sealed record MemoryItemQuery(
    string? Status = null,
    string? Scope = null,
    string? Kind = null,
    Guid? ProjectId = null,
    int? Limit = null);

public sealed record MemoryCandidateProposal(
    Guid SourceRunId,
    Guid GeneratedByInstanceId,
    string Scope,
    string Kind,
    string Content,
    double Confidence,
    Guid? ProjectId = null,
    Guid? AgentId = null,
    string? Evidence = null,
    string? Applicability = null,
    string? ExpiryCondition = null);

/// <summary>
/// A candidate as a reviewer sees it. Evidence, applicability and expiry travel here
/// because the curator already recorded them in the stored proposal: they are the three
/// facts that decide whether a true statement is worth keeping, and a review page that
/// hides them is asking for a yes/no on the content sentence alone.
/// </summary>
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
    DateTimeOffset UpdatedAt,
    string? Evidence = null,
    string? Applicability = null,
    string? ExpiryCondition = null);

public sealed record LongTermMemoryItem(
    Guid Id,
    string Scope,
    string Kind,
    string Status,
    int Version,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt,
    string? Applicability = null,
    string? ExpiryCondition = null);
