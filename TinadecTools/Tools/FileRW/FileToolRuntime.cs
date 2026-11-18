using System.Collections.Concurrent;
using AsyncLocks;
using TinadecTools.Runtime;

namespace TinadecTools.Tools.FileRW;

/// <summary>
/// Immutable root set of one tools process: exactly one writable root plus any
/// number of read-only roots. It is a pure value, so the boundary rules can be
/// exercised directly without standing up a process or mutating the environment.
/// </summary>
internal sealed record WorkspaceRootSet(string WritableRoot, IReadOnlyList<string> ReadOnlyRoots)
{
    internal const string ReadOnlyRootsEnvironmentVariable = "TINADEC_TOOLS_READ_ROOTS";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// The root set of this process: the directory it was started from is the
    /// bootstrap writable root. A trusted invocation may freeze additional source
    /// folders, while the host may also declare strictly read-only resource roots.
    /// </summary>
    public static WorkspaceRootSet FromProcess() => new(
        Normalize(Environment.CurrentDirectory),
        ParseReadOnlyRoots(Environment.GetEnvironmentVariable(ReadOnlyRootsEnvironmentVariable)));

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Parses the host's path-separator separated root list. Entries the writable
    /// root already covers are dropped, and an unusable entry is ignored rather
    /// than failing process startup.
    /// </summary>
    public static IReadOnlyList<string> ParseReadOnlyRoots(string? raw, string? writableRoot = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var root = writableRoot ?? Normalize(Environment.CurrentDirectory);
        var roots = new List<string>();
        foreach (var entry in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try
            {
                full = Normalize(entry);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // The writable root already covers itself and everything inside it.
            if (IsWithin(root, full)) continue;
            if (!roots.Any(existing => string.Equals(existing, full, PathComparison))) roots.Add(full);
        }

        return roots;
    }

    /// <summary>
    /// True when the path is inside the writable root, or — for reads only —
    /// inside one of the declared read-only roots.
    /// </summary>
    public bool IsAllowed(string path, bool writable = false) =>
        (IsWithin(WritableRoot, path)
            || (ToolExecutionContext.Current?.WorkspaceRoots.Any(root => IsWithin(root.Path, path)) == true)
            || (ToolExecutionContext.Current?.WorkspaceFiles.Any(file => IsExactFile(file.Path, path)) == true)
            || (ToolExecutionContext.Current?.StorageRoot is not null && WorkspaceStoragePolicy.IsPublicScopePath(WritableRoot, path))
            || (!writable && ReadOnlyRoots.Any(root => IsWithin(root, path)))
            || (ToolExecutionContext.Current is { ProjectStorageWrite: true, StorageRoot: { } storage }
                && IsWithin(storage, path)))
        && WorkspaceStoragePolicy.CanAccess(WritableRoot, path, writable, ReadOnlyRoots);

    /// <summary>
    /// Resolves a path against the writable root (absolute paths pass through) and
    /// refuses anything outside the allowed roots, naming what was attempted and
    /// which roots are allowed so the caller can correct itself.
    /// </summary>
    public string Resolve(string path, bool writable)
    {
        var resolved = Path.GetFullPath(path, WritableRoot);
        if (!IsAllowed(resolved, writable)) throw OutsideWorkspace(path, resolved, writable);
        return resolved;
    }

    /// <summary>The allowed root that contains the path, so link walking starts at that root.</summary>
    public (string Root, string Relative) OwningRoot(string path) =>
        OwningRootIn(path, new[] { WritableRoot }.Concat(ReadOnlyRoots)
            .Concat(ToolExecutionContext.Current?.WorkspaceRoots.Select(root => root.Path) ?? [])
            .Concat(ToolExecutionContext.Current?.WorkspaceFiles.Select(file => file.Path) ?? [])
            .Concat(ToolExecutionContext.Current is { StorageRoot: { } storage } ? [storage] : Array.Empty<string>()).ToList(),
            static candidate => WorkspacePathForm.Canonical(candidate));

    /// <summary>
    /// The root and the path have to come back in <b>one</b> spelling: a relative computed by mixing
    /// the declared path against the resolved root walks out of the workspace and straight back
    /// through <c>/var</c>, which the link policy then refuses as a symbolic link — the failure this
    /// replaced a green containment check with.
    /// </summary>
    internal static (string Root, string Relative) OwningRootIn(
        string path,
        IReadOnlyList<string> roots,
        Func<string, string> canonical)
    {
        foreach (var root in roots)
        {
            if (TextuallyWithin(root, path)) return (root, Path.GetRelativePath(root, path));

            var canonicalRoot = canonical(root);
            var canonicalPath = canonical(path);
            if (TextuallyWithin(canonicalRoot, canonicalPath))
                return (canonicalRoot, Path.GetRelativePath(canonicalRoot, canonicalPath));
        }

        var writable = roots[0];
        return (writable, Path.GetRelativePath(writable, path));
    }

    /// <summary>
    /// True when <paramref name="path"/> sits under <paramref name="root"/>.
    ///
    /// <para>
    /// The prefix test is the rule, and it is kept: nothing it allowed becomes denied. The second
    /// comparison exists because the two sides of this border do not always speak one spelling of one
    /// directory — this process takes its root from <see cref="Environment.CurrentDirectory"/>, which
    /// macOS hands back with <c>/var</c> already resolved to <c>/private/var</c>, while the host asks
    /// for the path it declared. A CI runner showed the result: the tool refused to read a file the
    /// human had just approved a write to, inside its own workspace root.
    /// </para>
    /// </summary>
    public static bool IsWithin(string root, string path) =>
        IsWithin(root, path, static candidate => WorkspacePathForm.Canonical(candidate));

    internal static bool IsWithin(string root, string path, Func<string, string> canonical)
    {
        if (TextuallyWithin(root, path)) return true;
        return TextuallyWithin(canonical(root), canonical(path));
    }

    private static bool TextuallyWithin(string root, string path)
    {
        if (string.Equals(path, root, PathComparison))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool IsExactFile(string root, string path) =>
        string.Equals(Normalize(root), Normalize(path), PathComparison);

    public UnauthorizedAccessException OutsideWorkspace(string attempted, string resolved, bool writable)
    {
        // Mirrors IsAllowed: the writable root this process was started in is authorized even when
        // the call envelope declares no source folders. Listing only WorkspaceRoots printed "()" in
        // exactly that case, which hid the one folder the caller could actually use and sent
        // diagnosis after the wrong thing.
        var sources = ToolExecutionContext.Current?.WorkspaceRoots.Select(root => root.Path).ToArray() ?? [];
        var files = ToolExecutionContext.Current?.WorkspaceFiles.Select(file => file.Path).ToArray() ?? [];
        var allowed = $"the writable root ('{WritableRoot}')";
        if (sources.Length > 0) allowed += $" or authorized source folders ({string.Join(", ", sources.Select(root => $"'{root}'"))})";
        if (files.Length > 0) allowed += $" or exact files ({string.Join(", ", files.Select(file => $"'{file}'"))})";
        if (!writable && ReadOnlyRoots.Count > 0)
            allowed += $" or a read-only root ({string.Join(", ", ReadOnlyRoots.Select(root => $"'{root}'"))})";
        // Named separately because a path can sit inside the writable root and still be refused
        // there: a protected storage root wins over the writable root, and without this the message
        // reported a root that visibly contains the path.
        var forbidden = WorkspaceStoragePolicy.ForbiddenRoots(WritableRoot);
        if (forbidden.Count > 0)
            allowed += $"; refused inside protected storage ({string.Join(", ", forbidden.Select(root => $"'{root}'"))})";
        return new UnauthorizedAccessException(
            $"Path '{attempted}' (resolved to '{resolved}') is outside the allowed workspace. "
            + $"Only paths inside {allowed} may be {(writable ? "written" : "read")}. "
            + "Retry with an absolute path inside an authorized source folder.");
    }
}

/// <summary>
/// Workspace path boundary of one tools process, built on its
/// <see cref="WorkspaceRootSet"/>.
///
/// Absolute paths are the documented contract; a relative path is resolved
/// against the writable root for compatibility. Reads may reach the declared
/// read-only roots, writes never can.
/// </summary>
internal static class WorkspacePathResolver
{
    internal const string ReadOnlyRootsEnvironmentVariable = WorkspaceRootSet.ReadOnlyRootsEnvironmentVariable;

    private static readonly WorkspaceRootSet BootstrapRoots = WorkspaceRootSet.FromProcess();
    private static WorkspaceRootSet Roots => ToolExecutionContext.Current is { } context
        ? new WorkspaceRootSet(BootstrapRoots.WritableRoot, context.ReadRoots) : BootstrapRoots;

    /// <summary>The single writable root: the directory this process was started from.</summary>
    public static string WorkspaceRoot => Roots.WritableRoot;

    /// <summary>Extra roots the host declared readable but never writable (empty by default).</summary>
    public static IReadOnlyList<string> ReadOnlyRoots => Roots.ReadOnlyRoots;
    public static IReadOnlyList<(string Id, string Path)> SourceRoots => ToolExecutionContext.Current is { WorkspaceRoots.Count: > 0 } context
        ? context.WorkspaceRoots : [("primary", WorkspaceRoot)];

    public static string ResolvePath(string path, bool allowFinalLink = false, bool writable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var resolved = Roots.Resolve(path, writable);
        EnsureNoLinkTraversal(resolved, allowFinalLink);
        return resolved;
    }

    public static string ResolveDirectory(string path, bool writable = false)
    {
        var resolved = ResolvePath(path, writable: writable);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory '{path}' does not exist.");

        return resolved;
    }

    public static bool IsAllowed(string path, bool writable = false) => Roots.IsAllowed(path, writable);

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

    private static void EnsureNoLinkTraversal(string path, bool allowFinalLink)
    {
        var (root, relative) = Roots.OwningRoot(path);
        if (relative == ".")
            return;

        var current = root;
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

    public static string ResolvePath(string filePath, bool writable = false)
    {
        return WorkspacePathResolver.ResolvePath(filePath, writable: writable);
    }
}
