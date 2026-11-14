using TinadecTools.Tools.FileRW;

namespace TinadecTools.Runtime;

/// <summary>One classification shared by file tools, search and process sandboxes.</summary>
internal static class WorkspaceStoragePolicy
{
    internal static readonly IReadOnlyList<string> InternalDirectories =
        ["data", "state", "logs", "cache", "temp", "packages", "worktrees"];

    internal static string StorageRoot(string workspaceRoot) =>
        ToolExecutionContext.Current?.StorageRoot ?? Path.Combine(workspaceRoot, ".tinadec");

    internal static bool IsPublicScopePath(string workspaceRoot, string path) =>
        new[] { "config", "skills" }.Any(name => WorkspaceRootSet.IsWithin(Path.Combine(StorageRoot(workspaceRoot), name), path));

    internal static bool IsInternal(string workspaceRoot, string path)
    {
        var storageRoot = StorageRoot(workspaceRoot);
        if (!WorkspaceRootSet.IsWithin(storageRoot, path)) return false;
        // A project can live under the storage root — a folder opened inside the app's own data
        // directory, which every test project does. Its tree is project content, not the app's
        // managed storage, and classifying it as internal denied every tool write inside such a
        // project unless whole-scope storage write had been granted. The managed entries below stay
        // internal regardless: they are siblings of the project root, so they never satisfy the
        // containment test. The equality guard keeps the degenerate case (the writable root IS the
        // storage root) on the original path.
        if (!string.Equals(WorkspaceRootSet.Normalize(storageRoot), WorkspaceRootSet.Normalize(workspaceRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && WorkspaceRootSet.IsWithin(workspaceRoot, path))
            return false;
        if (ToolExecutionContext.Current?.WorkingDirectory is { } assigned
            && WorkspaceRootSet.IsWithin(Path.Combine(storageRoot, "worktrees"), assigned)
            && !string.Equals(WorkspaceRootSet.Normalize(assigned), WorkspaceRootSet.Normalize(Path.Combine(storageRoot, "worktrees")),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && WorkspaceRootSet.IsWithin(assigned, path))
        {
            var nested = Path.Combine(assigned, ".tinadec");
            if (!WorkspaceRootSet.IsWithin(nested, path)) return false;
            var child = Path.GetRelativePath(nested, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return child is not ("." or "config" or "skills");
        }
        var relative = Path.GetRelativePath(storageRoot, path);
        if (relative == ".") return false; // The container may be listed; its children are classified separately.
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return !first.Equals("config", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("skills", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CanAccess(string workspaceRoot, string path, bool writable,
        IReadOnlyList<string> explicitReadRoots)
    {
        if (writable && IsGitMetadata(path)) return false;
        if (ForbiddenRoots(workspaceRoot).Any(root => WorkspaceRootSet.IsWithin(root, path)))
            return !writable && !PermanentHostPaths().Any(root => WorkspaceRootSet.IsWithin(root, path))
                && explicitReadRoots.Any(root => WorkspaceRootSet.IsWithin(root, path));
        if (writable && string.Equals(WorkspaceRootSet.Normalize(path), WorkspaceRootSet.Normalize(StorageRoot(workspaceRoot)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return ToolExecutionContext.Current?.ProjectStorageWrite == true;
        if (!IsInternal(workspaceRoot, path)) return true;
        if (ToolExecutionContext.Current?.ProjectStorageWrite == true) return true;
        return !writable && explicitReadRoots.Any(root => WorkspaceRootSet.IsWithin(root, path));
    }

    // Git metadata is an execution surface, not just storage: hooks run on commit, and
    // .git/config names programs through core.fsmonitor, core.sshCommand, core.pager,
    // core.editor, filter.*.clean/smudge and alias.*. An agent that can write there and
    // then reach a git subcommand has arbitrary execution outside the command sandbox.
    //
    // Writes only. Reads stay open so ls/stat keep working, and git itself is unaffected:
    // it writes .git as a subprocess, which never passes through this classification.
    // Segment matching covers nested repositories and the .git file of a worktree; a
    // gitdir outside the workspace is already refused by the root containment check.
    private static bool IsGitMetadata(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase));

    // The sandbox keeps .git/hooks and .git/config read-only even though the workspace around
    // them is writable: a hook, or a core.fsmonitor/core.sshCommand/filter.*.clean entry, is
    // arbitrary execution. The rest of .git stays writable so a person typing git commit inside
    // the sandbox can still write index, objects and refs.
    //
    // Only each writable root's own .git is enumerated. A nested repository's .git is refused by
    // CanAccess above, not here, and a worktree whose .git is a file pointing outside the workspace
    // has no .git/hooks to protect.
    internal static IEnumerable<string> GitWriteDeniedPaths(IEnumerable<string> roots) =>
        roots.SelectMany(root => new[] { Path.Combine(root, ".git", "hooks"), Path.Combine(root, ".git", "config") })
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static IReadOnlyList<string> ProtectedPaths(string workspaceRoot) =>
        InternalDirectories.Select(name => Path.Combine(StorageRoot(workspaceRoot), name))
            .Append(Path.Combine(StorageRoot(workspaceRoot), "project.toml"))
            .Concat(ToolExecutionContext.Current?.WorkingDirectory is { } assigned
                && WorkspaceRootSet.IsWithin(Path.Combine(StorageRoot(workspaceRoot), "worktrees"), assigned)
                ? InternalDirectories.Select(name => Path.Combine(assigned, ".tinadec", name)).Append(Path.Combine(assigned, ".tinadec", "project.toml")) : [])
            .Concat(ForbiddenRoots(workspaceRoot)).Distinct().ToArray();

    internal static IReadOnlyList<string> ForbiddenRoots(string workspaceRoot)
    {
        var current = StorageRoot(workspaceRoot);
        var roots = (ToolExecutionContext.Current?.ProtectedStorageRoots ?? [])
            .Where(root => !WorkspaceRootSet.IsWithin(root, current)).ToList();
        roots.AddRange(PermanentHostPaths());
        return roots.Distinct().ToArray();
    }
    internal static IReadOnlyList<string> PermanentHostPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? [] : [Path.Combine(home, ".tinadec", "security"), Path.Combine(home, ".tinadec", "state")];
    }
}
