using System.Diagnostics;

namespace TinadecTools.Runtime.Sandbox.Posix;

/// <summary>The reviewed mount-view backend. No fallback launcher is permitted.</summary>
internal static class BubblewrapLauncher
{
    internal const string RequiredVersion = "0.13.0";
    internal const string PathEnvironmentVariable = "TINADEC_TOOLS_BWRAP_PATH";
    private static readonly SemaphoreSlim VerificationGate = new(1, 1);
    private static string? _verifiedPath;

    internal static string ResolvePath()
    {
        var configured = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var bundled = Path.Combine(AppContext.BaseDirectory, "bwrap");
        if (File.Exists(bundled)) return bundled;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var candidate = Path.Combine(folder, "bwrap");
            if (File.Exists(candidate)) return candidate;
        }
        return bundled;
    }

    internal static bool IsVerified => _verifiedPath == ResolvePath();

    internal static async Task EnsureAvailableAsync(CancellationToken ct)
    {
        var path = ResolvePath();
        if (!File.Exists(path)) throw Unavailable("the executable is missing");
        await VerificationGate.WaitAsync(ct);
        try
        {
            if (_verifiedPath == path) return;
            var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start) ?? throw Unavailable("the executable could not start");
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                if (process.ExitCode != 0 || output.Trim() != "bubblewrap " + RequiredVersion)
                    throw Unavailable("the installed executable is not the reviewed version " + RequiredVersion);
            }
            catch { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
            if (LandlockApi.QueryAbiVersion() < 1) throw Unavailable("Landlock is unavailable in this kernel");
            _verifiedPath = path;
        }
        finally { VerificationGate.Release(); }
    }

    internal static void AddMountView(ProcessStartInfo start, SandboxPermissions permissions, IReadOnlyList<string> writePaths)
    {
        Add("--die-with-parent", "--new-session", "--unshare-user", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--disable-userns");
        Add("--ro-bind", "/", "/", "--proc", "/proc", "--dev", "/dev");
        foreach (var path in writePaths.Where(path => Directory.Exists(path) || File.Exists(path)).Distinct(StringComparer.Ordinal))
            // A plain bind is nodev: rebinding /dev/null that way breaks even shell stdin
            // redirection. This grants only the declared null device, never its parent.
            Add(WritableBindOption(path), path, path);
        if (!permissions.StorageWrite && permissions.StorageRoot is { } storageRoot && Directory.Exists(storageRoot))
        {
            Add("--ro-bind", storageRoot, storageRoot);
            foreach (var path in writePaths.Where(Directory.Exists))
                if (TinadecTools.Tools.FileRW.WorkspaceRootSet.IsWithin(storageRoot, path) && path != storageRoot)
                    Add("--bind", path, path);
        }

        static string Normalize(string path) => path.Length > 1 ? Path.TrimEndingDirectorySeparator(path) : path;

        var protectedPaths = permissions.ProtectedPaths
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path.Length)
            .ToList();
        var protectedDirectories = protectedPaths.Where(Directory.Exists).ToList();
        var readExceptions = permissions.ReadExceptions
            .Select(Normalize)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        bool IsWithinProtected(string path) =>
            protectedDirectories.Any(root => TinadecTools.Tools.FileRW.WorkspaceRootSet.IsWithin(root, path));

        // Read exceptions outside protected storage (e.g. AppContext.BaseDirectory) must be mounted
        // before tmpfs storage masks so a parent --ro-bind never covers a child --tmpfs prior to --remount-ro.
        foreach (var path in readExceptions.Where(path => !IsWithinProtected(path)))
            Add("--ro-bind", path, path);

        var maskedDirectories = new List<string>();
        foreach (var path in protectedPaths)
        {
            if (Directory.Exists(path)) { Add("--tmpfs", path); maskedDirectories.Add(path); }
            else if (File.Exists(path)) Add("--ro-bind", "/dev/null", path);
        }
        var postMaskMounts = new List<string>();
        foreach (var path in readExceptions.Where(IsWithinProtected))
        {
            Add("--ro-bind", path, path);
            postMaskMounts.Add(path);
        }
        // Host-owned scratch paths are the only writable exceptions within masked storage.
        foreach (var path in writePaths.Select(Normalize).Where(Directory.Exists).Distinct(StringComparer.Ordinal))
            if (maskedDirectories.Any(root => TinadecTools.Tools.FileRW.WorkspaceRootSet.IsWithin(root, path)))
            {
                Add("--bind", path, path);
                postMaskMounts.Add(path);
            }
        // Rebinding the assigned checkout (or nested read exceptions) must not expose nested runtime directories.
        foreach (var path in protectedPaths)
        {
            if (!postMaskMounts.Any(mount => mount != path && TinadecTools.Tools.FileRW.WorkspaceRootSet.IsWithin(mount, path))) continue;
            if (Directory.Exists(path)) Add("--tmpfs", path);
            else if (File.Exists(path)) Add("--ro-bind", "/dev/null", path);
        }
        foreach (var path in maskedDirectories) Add("--remount-ro", path);
        // Last, so no later bind re-grants write. A read-only bind keeps the contents visible —
        // unlike --tmpfs, which would hide git's own config and break every git command. Landlock
        // is additive and still grants the whole tree, so this refusal rests on the mount view alone.
        foreach (var path in permissions.WriteDeniedPaths.Where(path => Directory.Exists(path) || File.Exists(path)).Distinct(StringComparer.Ordinal))
            Add("--ro-bind", path, path);

        void Add(params string[] args) { foreach (var arg in args) start.ArgumentList.Add(arg); }
    }

    internal static string WritableBindOption(string path) => path == "/dev/null" ? "--dev-bind" : "--bind";

    private static PlatformNotSupportedException Unavailable(string reason) => new(
        $"Linux command sandbox requires bubblewrap {RequiredVersion} and Landlock; {reason}. The command was not started.");
}
