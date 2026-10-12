using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Persistence;

public static class StorageScopePaths
{
    public static string UserRoot(string? configured = null) => Path.GetFullPath(
        configured ?? Environment.GetEnvironmentVariable("TINADEC_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tinadec"));

    public static StorageScopeDescriptor User(string? configured = null) => new("user", "user", UserRoot(configured));

    public static string NormalizeProjectRoot(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Path.IsPathRooted(input))
            throw new ArgumentException("Project path must be absolute.", nameof(input));
        var directory = new DirectoryInfo(Path.GetFullPath(input));
        if (!directory.Exists) throw new DirectoryNotFoundException("The selected project directory does not exist.");
        var resolved = directory.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator(resolved?.FullName ?? directory.FullName);
    }

    public static string Contained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new ArgumentException("A relative owned path is required.", nameof(relative));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("The path escaped its storage scope.");
        RejectLinks(fullRoot, result);
        return result;
    }

    /// <summary>
    /// The scope root, and every entry inside it, must be a real directory.
    ///
    /// The walk deliberately starts at the root rather than at the volume root: on macOS `/var` is
    /// a symlink to `/private/var`, so starting above the scope rejected the host's own layout —
    /// every temp-rooted scope tripped on an ancestor the user never chose and cannot change. The
    /// claim is "nothing in the storage tree is a link"; where the tree happens to live is the
    /// platform's business, and <see cref="Contained"/> already re-roots every owned path under it.
    /// The root itself is still checked, so a scope registered on a symlink is still refused.
    /// </summary>
    /// <param name="isReparsePoint">
    /// Injection seam, the same shape <see cref="WorkspacePathSpelling.Canonical"/> uses. An
    /// unprivileged Windows account cannot create a symlink or junction, so a test that had to make
    /// a real one would silently pass here while proving nothing — which is how the macOS half of
    /// this rule went unverified until CI. Tests pass a fake so the rule is pinned on every host;
    /// production passes null and asks the file system.
    /// </param>
    public static void RejectLinks(string root, string? destination = null, Func<string, bool>? isReparsePoint = null)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(destination ?? root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(fullRoot, Path.TrimEndingDirectorySeparator(target), comparison)
            && !target.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("The path escaped its storage scope.");
        RejectLink(fullRoot, isReparsePoint);
        var current = fullRoot;
        foreach (var segment in Path.GetRelativePath(fullRoot, target).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "." or "..") continue;
            current = Path.Combine(current, segment);
            RejectLink(current, isReparsePoint);
        }
    }

    private static void RejectLink(string path, Func<string, bool>? isReparsePoint)
    {
        try
        {
            var linked = isReparsePoint is null
                ? (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                : isReparsePoint(path);
            if (linked)
                throw new InvalidOperationException("Owned storage must not contain symbolic links or junctions: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
