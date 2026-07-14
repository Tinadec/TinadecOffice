using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Runtime.Sandbox;

internal static class SandboxPaths
{
    internal const uint TnadIdentifierAuthority = 0x54494E41; // "TINA"

    private static readonly StringComparison Cmp = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // ── workspace confinement ─────────────────────────────────────────────────

    internal static string ValidateWorkingDirectory(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var full = Path.GetFullPath(workingDirectory, WorkspacePathResolver.WorkspaceRoot);
        full = Path.TrimEndingDirectorySeparator(full);
        if (!IsWithinWorkspace(full))
            throw new UnauthorizedAccessException("working_directory must be inside the workspace root.");
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Directory does not exist: {full}");
        return full;
    }

    internal static bool IsWithinWorkspace(string full)
    {
        var root = WorkspacePathResolver.WorkspaceRoot;
        if (string.Equals(full, root, Cmp)) return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Cmp);
    }

    // ── external grant normalization ──────────────────────────────────────────

    internal static string NormalizeGrantPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path, WorkspacePathResolver.WorkspaceRoot);
        full = Path.TrimEndingDirectorySeparator(full);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Directory does not exist: {full}");
        return full;
    }

    internal static void EnsureNotBroadWriteTarget(string full)
    {
        if (IsDiskRoot(full))
            throw new UnauthorizedAccessException("Disk root is too broad for write authorization.");

        foreach (var broad in ProhibitedBroadTargets)
        {
            if (string.Equals(full, broad, Cmp))
                throw new UnauthorizedAccessException($"'{full}' is too broad for write authorization.");
        }
    }

    internal static bool IsDiskRoot(string full)
    {
        var root = Path.GetPathRoot(full);
        return !string.IsNullOrEmpty(root) && string.Equals(full, root, Cmp);
    }

    private static readonly string[] ProhibitedBroadTargets = BuildProhibited();

    private static string[] BuildProhibited()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var list = new List<string>(8);
        Add(list, () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(list, () => Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(list, () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(list, () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(list, () => Environment.GetFolderPath(Environment.SpecialFolder.System));
        Add(list, () => Environment.GetFolderPath(Environment.SpecialFolder.Personal));
        Add(list, () => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..")));
        return list.ToArray();
    }

    private static void Add(List<string> list, Func<string> path)
    {
        try { var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path())); if (!string.IsNullOrEmpty(full)) list.Add(full); }
        catch { }
    }

    // ── capability SID ────────────────────────────────────────────────────────

    private static readonly string WorkspaceRootKey = WorkspacePathResolver.WorkspaceRoot
        .TrimEnd('\\', '/')
        .Replace("\\", "/")
        .ToLowerInvariant();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static SecurityIdentifier DeriveCapabilitySid()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(WorkspaceRootKey));
        var sid = $"S-1-12-1-{TnadIdentifierAuthority}-{BitConverter.ToUInt32(hash.AsSpan(0))}-{BitConverter.ToUInt32(hash.AsSpan(4))}-{BitConverter.ToUInt32(hash.AsSpan(8))}-{BitConverter.ToUInt32(hash.AsSpan(12))}";
        return new SecurityIdentifier(sid);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static SecurityIdentifier DeriveCapabilitySid(string workspaceRoot)
    {
        var key = workspaceRoot.TrimEnd('\\', '/').Replace("\\", "/").ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var sid = $"S-1-12-1-{TnadIdentifierAuthority}-{BitConverter.ToUInt32(hash.AsSpan(0))}-{BitConverter.ToUInt32(hash.AsSpan(4))}-{BitConverter.ToUInt32(hash.AsSpan(8))}-{BitConverter.ToUInt32(hash.AsSpan(12))}";
        return new SecurityIdentifier(sid);
    }
}