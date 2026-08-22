using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Lifecycle;

/// <summary>Captures and restores a tenant-scoped ordinary filesystem workspace.</summary>
public sealed class FileSystemWorkspaceSnapshotProvider : IWorkspaceSnapshotProvider
{
    public string Kind => "filesystem";

    public bool CanHandle(string workspaceRoot) => Directory.Exists(workspaceRoot);

    public async Task<WorkspaceSnapshotDocument> CaptureAsync(
        WorkspaceSnapshotCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var (files, hash) = await WorkspaceSnapshotProviderSupport.CaptureFilesAsync(request, cancellationToken).ConfigureAwait(false);
        return new WorkspaceSnapshotDocument(
            WorkspaceSnapshotProviderSupport.SnapshotSchemaVersion,
            Kind,
            IsGit: false,
            request.IncludeHidden,
            hash,
            files);
    }

    public async Task<WorkspaceSnapshotProviderRestoreResult> RestoreAsync(
        string workspaceRoot,
        WorkspaceSnapshotDocument snapshot,
        WorkspaceSnapshotProviderRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(snapshot.ProviderKind, Kind, StringComparison.OrdinalIgnoreCase)
            && snapshot.IsGit)
            throw new InvalidDataException("A Git snapshot cannot be restored by the filesystem provider.");

        var expected = string.IsNullOrWhiteSpace(request.ExpectedWorkspaceHash)
            ? snapshot.WorkspaceHash
            : request.ExpectedWorkspaceHash.Trim();
        var normalized = snapshot with { WorkspaceHash = expected };
        var (applied, conflicts) = await WorkspaceSnapshotProviderSupport.RestoreFilesAsync(
            workspaceRoot, normalized, request.AllowConflicts, cancellationToken).ConfigureAwait(false);
        var status = conflicts.Count == 0 ? "restored" : "restored_with_conflicts";
        return new WorkspaceSnapshotProviderRestoreResult(status, snapshot.WorkspaceHash, conflicts, applied);
    }
}
