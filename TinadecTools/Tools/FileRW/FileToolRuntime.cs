using System.Collections.Concurrent;
using AsyncLocks;

namespace TinadecTools.Tools.FileRW;

internal static class WorkspacePathResolver
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // A tools process is scoped to the directory from which it was started.
    public static string WorkspaceRoot { get; } = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Environment.CurrentDirectory));

    public static string ResolvePath(string path, bool allowFinalLink = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var resolved = Path.GetFullPath(path, WorkspaceRoot);
        if (!IsWithinWorkspace(resolved))
            throw new UnauthorizedAccessException("Path must be inside the workspace.");

        EnsureNoLinkTraversal(resolved, allowFinalLink);
        return resolved;
    }

    public static string ResolveDirectory(string path)
    {
        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory '{path}' does not exist.");

        return resolved;
    }

    public static string ToWorkspaceRelativePath(string path)
    {
        var relative = Path.GetRelativePath(WorkspaceRoot, path);
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsWithinWorkspace(string path)
    {
        if (string.Equals(path, WorkspaceRoot, PathComparison))
            return true;

        var prefix = WorkspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? WorkspaceRoot
            : WorkspaceRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static void EnsureNoLinkTraversal(string path, bool allowFinalLink)
    {
        var relative = Path.GetRelativePath(WorkspaceRoot, path);
        if (relative == ".")
            return;

        var current = WorkspaceRoot;
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!Exists(current))
                break;

            if (IsLink(current) && (!allowFinalLink || index < segments.Length - 1))
                throw new UnauthorizedAccessException("Paths through symbolic links or junctions are not allowed.");
        }
    }
}

internal sealed class FileSlot
{
    public AsyncReaderWriterLock RwLock { get; } = new();
}

internal static class FileToolRuntime
{
    private static readonly ConcurrentDictionary<string, FileSlot> Locks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string WorkspaceRoot => WorkspacePathResolver.WorkspaceRoot;

    public static void InitializeWorkspace() => _ = WorkspacePathResolver.WorkspaceRoot;

    public static FileSlot GetFileHandle(string path)
    {
        return Locks.GetOrAdd(path, _ => new FileSlot());
    }

    public static FileAccessor OpenRead(string path, CancellationToken cancellationToken = default) =>
        new(path, canWrite: false, cancellationToken);

    public static FileAccessor OpenWrite(string path, CancellationToken cancellationToken = default) =>
        new(path, canWrite: true, cancellationToken);

    public static string ResolvePath(string filePath)
    {
        return WorkspacePathResolver.ResolvePath(filePath);
    }
}
