namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// A session attachment as the HTTP surface exposes it. Deliberately no
/// <c>content_reference</c>: that is a Core-owned storage path, and a client has no use
/// for it while having every use for guessing at it.
/// </summary>
public sealed class MessageAttachmentDto
{
    public string Id { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? MessageId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public long ContentLength { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? BoundAt { get; init; }
}

/// <summary>The ceiling Core accepts for one upload, so a client can fail before sending.</summary>
public sealed class AttachmentLimitsDto
{
    public long MaxAttachmentBytes { get; init; }
    public int MaxFileNameLength { get; init; }
}
