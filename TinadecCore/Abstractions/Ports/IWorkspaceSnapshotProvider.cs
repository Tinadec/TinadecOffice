namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Provider-neutral workspace capture and restore boundary. A provider describes
/// external workspace state; the lifecycle service owns persistence, tenancy,
/// idempotency and the distinction from MAF/run checkpoints.
/// </summary>
public interface IWorkspaceSnapshotProvider
{
    /// <summary>Stable provider kind written into the snapshot document.</summary>
    string Kind { get; }

    /// <summary>Returns whether this provider can safely handle the workspace root.</summary>
    bool CanHandle(string workspaceRoot);

    Task<WorkspaceSnapshotDocument> CaptureAsync(
        WorkspaceSnapshotCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkspaceSnapshotProviderRestoreResult> RestoreAsync(
        string workspaceRoot,
        WorkspaceSnapshotDocument snapshot,
        WorkspaceSnapshotProviderRestoreRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkspaceSnapshotCaptureRequest(
    string WorkspaceRoot,
    bool IncludeHidden = false,
    int MaxFiles = 10_000,
    long MaxBytes = 64 * 1024 * 1024);

/// <summary>
/// Versioned payload stored by <see cref="IWorkspaceSnapshotService"/>. The Git
/// state is optional so the same schema handles ordinary filesystem workspaces.
/// File contents are bounded by the provider and may be absent for large files;
/// hashes are always captured.
/// </summary>
public sealed record WorkspaceSnapshotDocument(
    int SchemaVersion,
    string ProviderKind,
    bool IsGit,
    bool IncludeHidden,
    string WorkspaceHash,
    IReadOnlyList<WorkspaceSnapshotFile> Files,
    GitWorkspaceSnapshotState? Git = null);

public sealed record WorkspaceSnapshotFile(
    string Path,
    long Length,
    string Sha256,
    string? ContentBase64);

/// <summary>Git metadata captured without relying on shell command concatenation.</summary>
public sealed record GitWorkspaceSnapshotState(
    string? Head,
    string? Branch,
    string? HeadReference,
    bool HeadReferenceExists,
    string? IndexTreeHash,
    string WorktreeHash,
    string StagedPatch,
    string UnstagedPatch,
    IReadOnlyList<string> UntrackedPaths,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<string> ConflictPaths,
    IReadOnlyList<string> References);

public sealed record WorkspaceSnapshotProviderRestoreRequest(
    string? ExpectedWorkspaceHash = null,
    bool AllowConflicts = false);

public sealed record WorkspaceSnapshotProviderRestoreResult(
    string Status,
    string WorkspaceHash,
    IReadOnlyList<string> Conflicts,
    int AppliedFileCount);
