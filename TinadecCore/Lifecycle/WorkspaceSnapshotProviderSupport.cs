using System.Security.Cryptography;
using System.Text;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Lifecycle;

/// <summary>Shared, provider-neutral file capture and restore primitives.</summary>
internal static class WorkspaceSnapshotProviderSupport
{
    internal const long MaxSingleFileBytes = 8 * 1024 * 1024;
    internal const int SnapshotSchemaVersion = 2;

    internal static async Task<(IReadOnlyList<WorkspaceSnapshotFile> Files, string WorkspaceHash)> CaptureFilesAsync(
        WorkspaceSnapshotCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(request));

        var root = Path.GetFullPath(request.WorkspaceRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Workspace root was not found.");
        var maxFiles = Math.Clamp(request.MaxFiles, 1, 100_000);
        var maxBytes = Math.Clamp(request.MaxBytes, 1, 1024L * 1024 * 1024);
        var files = new List<WorkspaceSnapshotFile>();
        long capturedBytes = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(root, path);
            if (IsExcluded(relative, request.IncludeHidden)) continue;
            if (files.Count >= maxFiles)
                throw new InvalidOperationException($"Workspace snapshot exceeds the {maxFiles} file limit.");

            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            string? content = null;
            if (info.Length <= MaxSingleFileBytes && capturedBytes + info.Length <= maxBytes)
            {
                content = Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                capturedBytes += info.Length;
            }
            files.Add(new WorkspaceSnapshotFile(relative, info.Length, hash, content));
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return (files, ComputeWorkspaceHash(files));
    }

    internal static async Task<(int AppliedFileCount, IReadOnlyList<string> Conflicts)> RestoreFilesAsync(
        string root,
        WorkspaceSnapshotDocument snapshot,
        bool allowConflicts,
        CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Workspace root was not found.");
        var (currentFiles, currentHash) = await CaptureFilesAsync(new WorkspaceSnapshotCaptureRequest(
            root, snapshot.IncludeHidden, 100_000, 1024L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        var conflicts = new List<string>();
        if (!FixedEquals(snapshot.WorkspaceHash, currentHash)) conflicts.Add("workspace_hash");

        var currentByPath = currentFiles.ToDictionary(x => x.Path, StringComparer.Ordinal);
        foreach (var expected in snapshot.Files)
        {
            if (expected.ContentBase64 is null && (!currentByPath.TryGetValue(expected.Path, out var current)
                || !string.Equals(current.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add($"file:{expected.Path}:content_unavailable");
        }
        if (conflicts.Count != 0 && !allowConflicts)
            throw new WorkspaceSnapshotConflictException("Workspace changed after the snapshot was created.", conflicts);

        var applied = 0;
        var snapshotPaths = snapshot.Files.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var file in snapshot.Files.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.ContentBase64 is null) continue;
            var path = SafePath(root, file.Path);
            var bytes = Convert.FromBase64String(file.ContentBase64);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tinadec-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch (PlatformNotSupportedException) { File.Move(temporary, path, true); }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            applied++;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System
        }).OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(root, path);
            if (IsExcluded(relative, snapshot.IncludeHidden) || snapshotPaths.Contains(relative)) continue;
            File.Delete(path);
            applied++;
        }

        return (applied, conflicts);
    }

    internal static bool IsExcluded(string relative, bool includeHidden)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => string.Equals(x, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(x, ".tinadec", StringComparison.OrdinalIgnoreCase))) return true;
        return !includeHidden && segments.Any(x => x.Length > 0 && x[0] == '.');
    }

    internal static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Snapshot contains an invalid path.");
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snapshot path escaped the workspace root.");
        return path;
    }

    internal static string ComputeWorkspaceHash(IEnumerable<WorkspaceSnapshotFile> files)
    {
        var canonical = string.Join('\n', files.OrderBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => $"{x.Path}\t{x.Length}\t{x.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    internal static bool FixedEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
