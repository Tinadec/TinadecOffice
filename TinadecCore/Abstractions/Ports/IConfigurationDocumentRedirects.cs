namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Lets a storage scope keep one of its configuration documents at another scope's location.
/// A workspace that follows the user's models reads, validates, locks and writes
/// <c>models.toml</c> in the user scope: one file, so the model center's edits (and deletions)
/// are what the workspace's conversations run on, instead of a copy taken when the workspace
/// was created. Documents without a redirect stay in the scope's own Config.
/// </summary>
public interface IConfigurationDocumentRedirects
{
    /// <summary>The scope whose Config/State hold <paramref name="documentId"/>, or null for this scope.</summary>
    IScopeStorageLocations? For(string documentId);
}

/// <summary>
/// Host-local choice, per workspace scope, of where its models come from. Kept in the host
/// registration (this machine's record of the workspace), never in project files.
/// </summary>
public interface IWorkspaceModelSourceRegistry
{
    /// <summary>True when the workspace runs on the user scope's models.toml; false when it keeps its own.</summary>
    bool FollowsUserModels(string storageId);

    /// <summary>
    /// Switches the source and remounts the workspace. Refused while the workspace has work in
    /// flight; a workspace that cannot project the chosen source is switched back.
    /// </summary>
    Task SetFollowsUserModelsAsync(string storageId, bool followUser, CancellationToken ct = default);
}
