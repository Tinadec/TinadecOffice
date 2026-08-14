using System.Runtime.InteropServices;
using Installer.Core.Models;

namespace Installer.Core.Helpers;

/// <summary>
/// Cross-platform runtime detection utilities
/// </summary>
public static class PlatformHelper
{
    /// <summary>
    /// Is running on Windows
    /// </summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Is running on Linux
    /// </summary>
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Is running on macOS
    /// </summary>
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// Current runtime identifier (e.g., "win-x64", "linux-musl-x64")
    /// </summary>
    public static string RuntimeId => RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// Current platform string (win, linux, macos) - lowercase for asset matching
    /// </summary>
    public static string CurrentPlatformString => IsWindows ? "win" : IsLinux ? "linux" : IsMacOS ? "macos" : "unknown";

    /// <summary>
    /// Current architecture string (x64, arm64) - lowercase for asset matching
    /// </summary>
    public static string CurrentArchitectureString => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "unknown"
    };

    /// <summary>
    /// Current process bitness (32-bit or 64-bit)
    /// </summary>
    public static bool Is64BitProcess => IntPtr.Size == 8;

    /// <summary>
    /// Determine if current platform matches the specified platform string
    /// </summary>
    public static bool MatchesPlatform(string platform) => 
        CurrentPlatformString.ToLowerInvariant() == platform.ToLowerInvariant();

    /// <summary>
    /// Determine if current architecture matches the specified architecture string
    /// </summary>
    public static bool MatchesArchitecture(string arch) => 
        CurrentArchitectureString.ToLowerInvariant() == arch.ToLowerInvariant();

    /// <summary>
    /// Find the best matching asset from a list based on current platform/architecture
    /// </summary>
    public static ReleaseAsset? FindBestAsset(IReadOnlyList<ReleaseAsset> assets)
    {
        if (assets.Count == 0)
            return null;

        // First try: exact match on platform and architecture
        var exactMatch = assets.FirstOrDefault(a =>
            MatchesPlatform(a.Platform) && MatchesArchitecture(a.Architecture));

        if (exactMatch != null)
            return exactMatch;

        // Second try: platform-only match
        var platformOnly = assets.FirstOrDefault(a =>
            MatchesPlatform(a.Platform));

        if (platformOnly != null)
            return platformOnly;

        // Third try: any compatible asset (heuristic fallback)
        return assets[0];
    }
}

/// <summary>
/// Path helper utilities with cross-platform support
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Get Tinadec base directory (~/.tinadec on Unix, %LOCALAPPDATA%/Tinadec on Windows)
    /// </summary>
    public static string GetTinadecDirectory()
    {
        return PlatformHelper.IsWindows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tinadec")
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE") ?? "/home/user", ".tinadec");
    }

    /// <summary>
    /// Get apps installation root directory
    /// </summary>
    public static string GetAppsDirectory() => Path.Combine(GetTinadecDirectory(), "apps");

    /// <summary>
    /// Get current version symlink directory
    /// </summary>
    public static string GetCurrentSymlinkDirectory() => Path.Combine(GetTinadecDirectory(), "current");

    /// <summary>
    /// Get cache directory (TEMP folder on Windows, /tmp on Unix)
    /// </summary>
    public static string GetCacheDirectory() => 
        Path.Combine(Path.GetTempPath(), "Tinadec", "Installer");

    /// <summary>
    /// Get store data directory for persisted records
    /// </summary>
    public static string GetStoreDirectory() => Path.Combine(GetTinadecDirectory(), "store");

    /// <summary>
    /// Build full path for an installed application
    /// </summary>
    public static string GetInstallPath(string appId, string version) => 
        Path.Combine(GetAppsDirectory(), appId, version);

    /// <summary>
    /// Build symlink path for an application
    /// </summary>
    public static string GetSymlinkPath(string appId) => 
        Path.Combine(GetCurrentSymlinkDirectory(), appId);

    /// <summary>
    /// Ensure directories exist
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(GetAppsDirectory());
        Directory.CreateDirectory(GetCurrentSymlinkDirectory());
        Directory.CreateDirectory(GetCacheDirectory());
        Directory.CreateDirectory(GetStoreDirectory());
    }

    /// <summary>
    /// Resolve user path with tilde expansion (~)
    /// </summary>
    public static string ExpandTilde(string path)
    {
        if (path.StartsWith("~/"))
        {
            return Path.Combine(GetTinadecDirectory(), path.Substring(2));
        }
        return Environment.ExpandEnvironmentVariables(path);
    }

    /// <summary>
    /// Normalize path separators for current platform
    /// </summary>
    public static string NormalizePath(string path)
    {
        return path.Replace('\\', Path.DirectorySeparatorChar)
                  .Replace('/', Path.DirectorySeparatorChar);
    }
}
