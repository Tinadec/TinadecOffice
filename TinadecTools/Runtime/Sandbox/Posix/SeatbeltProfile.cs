using System.Text;

namespace TinadecTools.Runtime.Sandbox.Posix;

/// <summary>
/// Builds the seatbelt profile passed to <c>/usr/bin/sandbox-exec</c> on macOS.
///
/// The shape is the standard "permissive base, write restricted" idiom: allow everything,
/// then deny writes, then re-allow writes under each granted path. Reads and execute stay
/// allowed for the same reason Landlock leaves them unhandled — confining them would break
/// ordinary toolchains without adding a guarantee this product claims.
///
/// Honest boundary: <c>sandbox-exec</c> is not a documented public Apple API, and profiles
/// like this one are validated by CI on a macOS runner, not by an API contract.
/// </summary>
internal static class SeatbeltProfile
{
    internal static string Build(IReadOnlyList<string> writeTargets, IReadOnlyList<string>? protectedPaths = null,
        IReadOnlyList<string>? readExceptions = null, string? protectedStorageRoot = null,
        IReadOnlyList<string>? writeDeniedPaths = null)
    {
        var sb = new StringBuilder();
        sb.Append("(version 1)\n");
        sb.Append("(allow default)\n");
        sb.Append("(deny file-write*)\n");

        if (writeTargets.Count == 0)
            return sb.ToString();

        var protectedRoots = protectedPaths ?? [];
        // Write-denied paths join the write exclusion only, never protectedRoots: the read-deny
        // block below is built from protectedRoots, and git inside the sandbox must still read
        // its own config. Path expressions also cover files created after the sandbox starts.
        var writeProtected = (protectedStorageRoot is null ? protectedRoots : protectedRoots.Append(protectedStorageRoot))
            .Concat(writeDeniedPaths ?? []).ToArray();
        sb.Append("(allow file-write*\n  (require-all\n    (require-any\n");
        Clauses(writeTargets);
        sb.Append("    )\n");
        if (writeProtected.Length > 0)
        {
            sb.Append("    (require-not (require-any\n");
            ProtectedClauses(writeProtected, writeTargets);
            sb.Append("    ))\n");
        }
        sb.Append("  )\n)\n");
        if (protectedRoots.Count > 0)
        {
            sb.Append("(deny file-read* (require-any\n");
            ProtectedClauses(protectedRoots, (readExceptions ?? []).Concat(writeTargets).ToArray());
            sb.Append("))\n");
        }
        return sb.ToString();

        void Clauses(IEnumerable<string> paths)
        {
            foreach (var path in paths.SelectMany(FormsOf).Distinct(StringComparer.Ordinal))
                sb.Append("      (").Append(IsDirectory(path) ? "subpath" : "literal").Append(' ').Append(Quote(path)).Append(")\n");
        }
        void ProtectedClauses(IEnumerable<string> roots, IReadOnlyList<string> grants)
        {
            foreach (var root in roots)
            {
                var exceptions = grants.Where(path => root != path && Tools.FileRW.WorkspaceRootSet.IsWithin(root, path)).ToArray();
                sb.Append("      (require-all (require-any\n"); Clauses([root]); sb.Append("      )\n");
                if (exceptions.Length > 0) { sb.Append("        (require-not (require-any\n"); Clauses(exceptions); sb.Append("        ))\n"); }
                sb.Append("      )\n");
            }
        }
    }

    /// <summary>
    /// A grant in both its declared and its resolved form. Seatbelt matches the path the kernel
    /// ends up using, and macOS exposes the temp directory as <c>/var</c> — a symlink to
    /// <c>/private/var</c> — so a profile carrying only the declared form denied writes inside the
    /// very directory it granted (measured on the CI runner: <c>Operation not permitted</c> on a
    /// file under <c>/var/folders/…</c>). The declared form stays because a path that does not
    /// exist yet has no resolved form to offer, and granting both cannot widen anything: they name
    /// the same directory.
    /// </summary>
    private static IEnumerable<string> FormsOf(string path)
    {
        yield return path;
        var resolved = Resolve(path);
        if (resolved is not null && !string.Equals(resolved, path, StringComparison.Ordinal))
            yield return resolved;
    }

    private static string? Resolve(string path)
    {
        // .NET's link resolver is asked for the final target and answers null when the last
        // component is itself a real directory — which is exactly the shape of
        // /var/folders/.../T/something: the link is two components up. The measured macOS runner
        // showed both forms of one directory in the same log, so the profile has to ask the OS.
        if (!OperatingSystem.IsWindows())
        {
            var canonical = PosixSysCalls.RealPath(path);
            if (!string.IsNullOrEmpty(canonical)) return canonical;
        }

        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName
                : new FileInfo(path).ResolveLinkTarget(true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Unreadable is treated as a file: the narrower clause grants less, so an odd
            // path cannot silently widen itself to a whole subtree.
            return false;
        }
    }

    /// <summary>
    /// SBPL string escaping. An unescaped quote or backslash in a workspace path would end
    /// the literal early and let the rest of the path be read as profile syntax.
    /// </summary>
    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c is '"' or '\\' or '\n' or '\r' or '\t')
                sb.Append('\\');
            sb.Append(c switch
            {
                '\n' => 'n',
                '\r' => 'r',
                '\t' => 't',
                _ => c
            });
        }
        sb.Append('"');
        return sb.ToString();
    }
}
