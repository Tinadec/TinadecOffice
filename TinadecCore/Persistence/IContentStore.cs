namespace TinadecCore.Persistence;

/// <summary>Immutable large-content store. Database rows retain only opaque references and integrity metadata.</summary>
public interface IContentStore
{
    Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(ContentReference reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken = default);
}

public sealed record ContentWriteRequest(
    Guid TenantId,
    Guid? WorkspaceId,
    string Kind,
    string MediaType,
    Stream Content);

public sealed record ContentReference(string Value, string Sha256, long Length, string MediaType);
