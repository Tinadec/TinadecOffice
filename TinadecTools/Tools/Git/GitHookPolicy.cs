using TinadecTools.Runtime;

namespace TinadecTools.Tools.Git;

// A repository hook runs as an ordinary child of TinadecTools: outside the command
// sandbox, with the host's ambient environment. An agent that can write
// .git/hooks/pre-commit and then call git_commit therefore gets arbitrary execution
// with whatever credentials that environment holds.
//
// Routing git through the sandbox is not the fix. On Windows the sandbox account's
// redirected profile cannot reach the user's credential helpers or ssh keys, and on
// POSIX reads and the network stay unconfined — it would break push without stopping
// the leak. Suppressing the hook does stop it, at the cost of not running hooks the
// user may want; git.allow_hooks trades that back deliberately.
//
// Fail-closed: only subcommands known never to run a hook are exempt. A missing entry
// costs two extra argv tokens on a read-only command, while a wrong entry would reopen
// the hole, so the list is conservative (branch and tag are excluded even though they
// run no hook, because they write refs).
internal static class GitHookPolicy
{
    private static readonly HashSet<string> NeverHooks = new(StringComparer.OrdinalIgnoreCase)
    {
        "rev-parse", "status", "log", "diff", "show", "cat-file", "ls-files", "ls-tree",
        "for-each-ref", "check-ref-format", "config", "remote", "describe", "rev-list",
        "merge-base", "name-rev", "shortlog", "blame", "grep", "count-objects",
        "verify-commit", "verify-tag", "fsck", "help", "version", "symbolic-ref"
    };

    // Global options that consume a separate value token; everything else is either
    // boolean or carries its value attached with '='.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-c", "-C", "--git-dir", "--work-tree", "--namespace", "--super-prefix", "--exec-path"
    };

    internal static bool HooksEnabled =>
        ToolExecutionContext.Current?.Boolean("git", "allow_hooks", false) ?? false;

    /// <summary>Arguments to prepend, or an empty array when hooks may run.</summary>
    internal static string[] Prefix(IReadOnlyList<string> arguments, string repoTopLevel)
    {
        if (HooksEnabled) return [];
        var subcommand = FirstSubcommand(arguments);
        if (subcommand is not null && NeverHooks.Contains(subcommand)) return [];
        return ["-c", "core.hooksPath=" + EmptyHooksDirectory(repoTopLevel)];
    }

    private static string? FirstSubcommand(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var token = arguments[i];
            if (ValueOptions.Contains(token)) { i++; continue; }
            if (token.StartsWith('-')) continue;
            return token;
        }
        return null;
    }

    // A real, empty directory rather than a missing path: git accepts both, but an
    // existing one cannot be turned into a hook source by anything that later creates
    // it. It lives in the scope's own scratch, which the tool host owns.
    private static string EmptyHooksDirectory(string repoTopLevel)
    {
        var directory = Path.Combine(WorkspaceStoragePolicy.StorageRoot(repoTopLevel), "temp", "no-hooks");
        Directory.CreateDirectory(directory);
        return directory;
    }
}