namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Works out which finite resource a tool call is about to touch, so the run engine can lease it
/// before the call leaves Core.
///
/// Why a pure mapping rather than a per-tool declaration: the tools already name their targets in
/// their own arguments, and a mapping here keeps the ledger's inputs in one auditable place. A tool
/// that names no resource simply has nothing to lease — read-only inspection is not a claim on
/// anything a second agent could destroy.
/// </summary>
public static class ResourceClaimResolver
{
    /// <summary>Argument names tools use for the file they operate on.</summary>
    private static readonly string[] FilePathKeys = ["filepath", "file_path", "path"];

    /// <summary>Argument names tools use for the repository or directory they operate in.</summary>
    private static readonly string[] RepositoryKeys = ["repository_path", "repo_path"];

    /// <summary>
    /// The claim a call makes, or null when it makes none. Every claim is exclusive: a lease exists to
    /// stop a second writer, and reads claim nothing at all. <paramref name="workspaceRoot"/> resolves
    /// relative paths.
    ///
    /// A call that names no file of its own (a shell command, <c>command_run</c>, a git mutation)
    /// claims NOTHING. Nobody can know what a command will write before it runs, so the only honest
    /// claim would be the whole workspace — and holding that until the task ends turns every parallel
    /// run in the workspace into a queue. None of the harnesses surveyed (Codex, DeepSeek Harness,
    /// Claude Code) lock commands either. Parallel writers are kept apart by a write scope declared at
    /// dispatch and by separate worktrees; what a command actually changed is checked after it runs
    /// (see docs/agent-graph/architecture.zh-CN.md §7.4).
    /// </summary>
    public static ResourceClaim? Resolve(string toolId, IReadOnlyDictionary<string, string?> arguments, string? workspaceRoot, bool mutatesWorkspace)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return null;

        // A worktree is its own kind of resource: two instances working in the same worktree is the
        // collision the ledger exists to catch. Creation and removal are mutations of the worktree
        // itself, so they claim it even though the manifest may not flag them as writes — and they
        // claim exactly that worktree: a claim on the repository would contain (and so collide with)
        // every other worktree in it. Listing worktrees is a read and claims nothing.
        if (WorktreeTools.IsMutation(toolId))
        {
            var target = WorktreeTools.TargetOf(toolId, arguments, workspaceRoot);
            return string.IsNullOrWhiteSpace(target) ? null : new ResourceClaim(ResourceLeaseKinds.Worktree, target, Exclusive: true);
        }

        if (!mutatesWorkspace)
        {
            // A read claims nothing: the envelope already bounds which paths it may read, and a
            // shared claim would make two agents unable to look at the same file at once.
            return null;
        }

        // Only a tool that names the file it writes claims it. Commands and git mutations claim
        // nothing (see the summary); git guards its own index with index.lock.
        var path = FirstNonEmpty(arguments, FilePathKeys);
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new ResourceClaim(ResourceLeaseKinds.Path, Absolute(path, workspaceRoot), Exclusive: true);
    }

    /// <summary>
    /// Resolves the entries of a declared write scope into claims: each entry is a path inside the
    /// workspace (absolute, or relative to the workspace root). Blank entries are dropped; an entry
    /// that names the workspace root itself is kept, because declaring "the whole workspace" is a
    /// legitimate — if coarse — scope that the dispatcher chose knowingly.
    /// </summary>
    public static IReadOnlyList<ResourceClaim> ResolveWriteScope(IEnumerable<string> entries, string? workspaceRoot) =>
        entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => new ResourceClaim(ResourceLeaseKinds.Path, Absolute(entry, workspaceRoot), Exclusive: true))
            .DistinctBy(claim => ResourceLeasePolicy.Normalize(claim.ResourceKey))
            .ToArray();

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string?> arguments, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    /// <summary>
    /// The canonical absolute key. Tools resolve a relative path against the workspace root, so the
    /// ledger must too: otherwise <c>probe.txt</c> and <c>C:\ws\probe.txt</c> would be two different
    /// resources and two runs could write the same file without the ledger noticing.
    /// </summary>
    public static string Absolute(string value, string? workspaceRoot)
    {
        var trimmed = value.Trim();
        if (!Path.IsPathRooted(trimmed) && !string.IsNullOrWhiteSpace(workspaceRoot))
        {
            try
            {
                trimmed = Path.GetFullPath(Path.Combine(workspaceRoot.Trim(), trimmed));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A path the OS cannot resolve is still a claim; keep a readable joined form so the
                // lease names it, rather than dropping the claim and letting the write through unseen.
                trimmed = workspaceRoot.Trim().TrimEnd('/', '\\') + "/" + trimmed;
            }
        }
        return ResourceLeasePolicy.Canonical(trimmed);
    }
}

/// <summary>
/// The worktree tools the ledger stewards (todo R5): creating one assigns it to the run that created
/// it, removing one releases that assignment. The target mirrors TinadecTools' own resolution — an
/// omitted <c>path</c> means <c>{repository}/.tinadec/worktrees/{branch-slug}</c> — so the claim
/// names the worktree the tool will really touch.
/// </summary>
public static class WorktreeTools
{
    public const string Create = "git_worktree_create";
    public const string Remove = "git_worktree_remove";

    public static bool IsMutation(string? toolId) =>
        string.Equals(toolId, Create, StringComparison.OrdinalIgnoreCase) || string.Equals(toolId, Remove, StringComparison.OrdinalIgnoreCase);

    /// <summary>The canonical key of the worktree a create/remove call targets; null when it cannot be known up front.</summary>
    public static string? TargetOf(string toolId, IReadOnlyDictionary<string, string?> arguments, string? workspaceRoot)
    {
        var path = Value(arguments, "path");
        var repository = Value(arguments, "repository_path") ?? Value(arguments, "repo_path") ?? workspaceRoot;
        if (path is not null) return ResourceClaimResolver.Absolute(path, repository ?? workspaceRoot);
        // Only creation has a default location; removal without a path is refused by the tool.
        if (!string.Equals(toolId, Create, StringComparison.OrdinalIgnoreCase) || repository is null) return null;
        var branch = Value(arguments, "branch");
        if (branch is null) return null;
        var slug = string.Concat(branch.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')).Trim('-');
        return slug.Length == 0 ? null : ResourceClaimResolver.Absolute(Path.Combine(".tinadec", "worktrees", slug), repository);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> arguments, string key) =>
        arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
