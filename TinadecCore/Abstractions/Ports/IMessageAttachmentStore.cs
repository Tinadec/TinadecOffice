namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Durable storage for files a user parks on a session. Kept separate from
/// <see cref="IConversationStore"/> because an attachment is written from an HTTP request
/// body, not from an agent turn, and because its bytes live in ContentStore while its row
/// lives with the conversation.
///
/// Everything here is scoped to the caller's tenant and workspace: a session id from
/// another scope must read as "not found", never as "exists but hidden".
/// </summary>
public interface IMessageAttachmentStore
{
    /// <summary>
    /// Stores one upload against a session, or returns null when no session in the
    /// current scope has that id. The row is created unbound; binding happens when the
    /// message that carries it is appended.
    /// </summary>
    Task<StoredAttachment?> StoreAsync(
        StoreAttachmentRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredAttachment>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<StoredAttachment?> FindAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the stored bytes for reading. The reference is resolved from the row, never
    /// from the request, so a caller cannot name a path.
    /// </summary>
    Task<Stream?> OpenContentAsync(
        StoredAttachment attachment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the row only. The bytes stay: the content store is keyed by SHA-256, so a
    /// second attachment with identical bytes would lose its content.
    /// </summary>
    Task<bool> DeleteAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims previously uploaded rows for a message in the same session, which is what
    /// makes an attachment part of the conversation instead of a file someone once sent.
    ///
    /// Idempotent for the same message on purpose: the caller appends the message first
    /// and claims the rows afterwards, so a retry of an interrupted send (same
    /// client_message_id, same attachment ids) must land on the existing message rather
    /// than be refused for rows it already owns.
    ///
    /// Implementations that persist rows must override this. The default body exists so
    /// the port can be held by a caller that never binds (fakes, read-only views); it
    /// throws rather than returning success, because a bind that quietly did nothing is
    /// the failure this whole feature exists to avoid.
    /// </summary>
    Task<int> BindToMessageAsync(
        Guid sessionId,
        Guid messageId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This attachment store does not support binding rows to messages.");
}

/// <summary>
/// A binding request that cannot be honoured: an id that is not in this session, a row
/// already carried by a different message, or a message that does not exist. Thrown
/// before anything is written, so the caller either claims every id it named or none.
/// </summary>
public sealed class AttachmentBindingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record StoreAttachmentRequest(
    Guid SessionId,
    string FileName,
    string MediaType,
    Stream Content);

/// <summary>
/// An attachment as Core sees it after the upload. <paramref name="ContentHash"/> is the
/// store's own SHA-256 over the bytes it received, which is what makes "did the upload
/// arrive intact" an answerable question rather than a client assertion.
/// </summary>
public sealed record StoredAttachment(
    Guid Id,
    Guid SessionId,
    Guid? MessageId,
    string FileName,
    string MediaType,
    string ContentHash,
    long ContentLength,
    DateTimeOffset CreatedAt,
    DateTimeOffset? BoundAt);
