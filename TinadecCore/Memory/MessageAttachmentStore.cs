using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Memory;

/// <summary>
/// Relational <see cref="IMessageAttachmentStore"/>. Rows carry references and integrity
/// metadata only; bytes go to <see cref="IContentStore"/> under the "attachment" kind,
/// which is the documented shape for large content (docs/architecture.md: "Full content
/// remains in immutable ContentStore, while relational projections … hold references,
/// hashes, counts, and summaries").
///
/// The kind segment is a constant rather than a per-file value because
/// <see cref="StoragePaths"/> sanitizes it to letters/digits/-/_ and it becomes part of
/// the on-disk layout. The client's media type is stored on the row, where it can be
/// reviewed, and never influences the path.
/// </summary>
public sealed class MessageAttachmentStore : IMessageAttachmentStore
{
    public const string ContentKind = "attachment";

    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenantContext;

    public MessageAttachmentStore(
        IDbContextFactory<MemoryDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenantContext)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenantContext = tenantContext;
    }

    public async Task<StoredAttachment?> StoreAsync(
        StoreAttachmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty) throw new ArgumentException("Session id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.FileName)) throw new ArgumentException("File name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("Media type is required.", nameof(request));
        if (request.Content is null) throw new ArgumentException("Attachment content is required.", nameof(request));

        var scope = _tenantContext.Current;

        // The session must exist in THIS scope. A foreign session id and a missing one
        // answer identically (null), so an upload cannot probe other tenants' sessions.
        await using (var probe = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var ownsSession = await probe.Sessions.AsNoTracking()
                .AnyAsync(x => x.Id == request.SessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken)
                .ConfigureAwait(false);
            if (!ownsSession) return null;
        }

        var written = await _content.PutAsync(
            new ContentWriteRequest(scope.TenantId, scope.WorkspaceId, ContentKind, request.MediaType, request.Content),
            cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var record = new MessageAttachmentRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SessionId = request.SessionId,
            MessageId = null,
            FileName = request.FileName,
            MediaType = written.MediaType,
            ContentReference = written.Value,
            ContentHash = written.Sha256,
            ContentLength = written.Length,
            CreatedByPrincipalId = scope.PrincipalId,
            CreatedAt = now,
            BoundAt = null,
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.MessageAttachments.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToStored(record);
    }

    public async Task<IReadOnlyList<StoredAttachment>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.MessageAttachments.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.SessionId == sessionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // SQLite stores DateTimeOffset as TEXT and cannot translate ordering over it, so
        // the sort happens after materialization — the same way the memory lists do it.
        rows.Sort((left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
        return rows.Select(ToStored).ToArray();
    }

    public async Task<StoredAttachment?> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.MessageAttachments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == attachmentId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    public async Task<Stream?> OpenContentAsync(
        StoredAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var reference = await db.MessageAttachments.AsNoTracking()
            .Where(x => x.Id == attachment.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .Select(x => x.ContentReference)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(reference)) return null;

        // Reading by the stored reference means the caller-supplied id is the only
        // addressable handle; a path can never be named from the request.
        return await _content.OpenReadAsync(new ContentReference(reference, attachment.ContentHash, attachment.ContentLength, attachment.MediaType), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var affected = await db.MessageAttachments
            .Where(x => x.Id == attachmentId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> BindToMessageAsync(
        Guid sessionId,
        Guid messageId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        var distinct = attachmentIds.Distinct().ToArray();
        if (distinct.Length == 0) return 0;
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The message has to be in the session the rows are in. Without this, a caller
        // that names a message from another session could move those rows out of reach of
        // the transcript that uploaded them.
        var ownsMessage = await db.Messages.AsNoTracking().AnyAsync(
            x => x.Id == messageId && x.SessionId == sessionId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        if (!ownsMessage)
            throw new AttachmentBindingException("message_not_found",
                $"No message in this session has the id {messageId}.");

        var rows = await db.MessageAttachments
            .Where(x => distinct.Contains(x.Id) && x.SessionId == sessionId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Foreign and missing ids answer with the same code, so this cannot be used to
        // probe whether another workspace holds an attachment.
        if (rows.Count != distinct.Length)
        {
            var missing = distinct.Except(rows.Select(x => x.Id)).ToArray();
            throw new AttachmentBindingException("attachment_not_found",
                $"No attachment in this session has the id(s): {string.Join(", ", missing)}.");
        }

        var taken = rows.FirstOrDefault(x => x.MessageId is { } owner && owner != messageId);
        if (taken is not null)
            throw new AttachmentBindingException("attachment_already_bound",
                $"Attachment {taken.Id} is already carried by another message.");

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows.Where(x => x.MessageId != messageId))
        {
            row.MessageId = messageId;
            row.BoundAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    private static StoredAttachment ToStored(MessageAttachmentRecord record) => new(
        record.Id,
        record.SessionId,
        record.MessageId,
        record.FileName,
        record.MediaType,
        record.ContentHash,
        record.ContentLength,
        record.CreatedAt,
        record.BoundAt);
}
