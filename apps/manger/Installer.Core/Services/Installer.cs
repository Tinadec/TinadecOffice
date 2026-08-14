using System.Formats.Tar;
using System.IO.Compression;
using Installer.Core.Helpers;
using Installer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Installer.Core.Services;

/// <summary>
/// Error categories for structured error handling
/// </summary>
public enum ErrorCategory
{
    None,
    Network,
    DiskSpace,
    Permission,
    Validation,
    Dependency,
    Corruption,
    Generic
}

/// <summary>
/// Installation exceptions with detailed error information
/// </summary>
public sealed class InstallationException : Exception
{
    public ErrorCategory Category { get; }

    public InstallationException(string message, ErrorCategory category = ErrorCategory.Generic)
        : base(message)
    {
        Category = category;
    }

    public InstallationException(string message, Exception inner, ErrorCategory category = ErrorCategory.Generic)
        : base(message, inner)
    {
        Category = category;
    }
}

/// <summary>
/// Installation progress and status reporting
/// </summary>
public sealed class InstallProgress
{
    public string Phase { get; set; } = string.Empty;
    public double Percentage { get; set; }
    public string Message { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }

    public override string ToString() => $"{Phase}: {Message} ({Percentage:F0}%)";
}

/// <summary>
/// Core installation engine orchestrating download -> verify -> extract -> record
/// </summary>
public sealed class Installer
{
    private readonly GitHubApiClient _githubClient;
    private readonly IStorageProvider _storage;
    private readonly ILogger? _logger;

    public Installer(
        GitHubApiClient githubClient,
        IStorageProvider storage,
        ILogger? logger = null)
    {
        _githubClient = githubClient;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Install an application from its GitHub repository
    /// </summary>
    public async Task<InstalledApplication> InstallAsync(
        Application app,
        string? version = null,
        Action<InstallProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var progress = new InstallProgress { Phase = "Initializing", Message = $"Preparing {app.Name}" };
        void Report(string phase, string message, double percentage = 0)
        {
            progress.Phase = phase;
            progress.Message = message;
            progress.Percentage = percentage;
            onProgress?.Invoke(progress);
        }

        try
        {
            var (owner, repo) = ParseRepo(app.GitHubRepo);

            // Phase 1: fetch releases
            Report("Fetching", "Fetching release information from GitHub...");
            var releases = await _githubClient.GetReleasesAsync(owner, repo, cancellationToken);

            var release = (version is null
                    ? releases.Where(r => !r.Prerelease).OrderByDescending(r => r.PublishedAt).FirstOrDefault()
                      ?? releases.OrderByDescending(r => r.PublishedAt).FirstOrDefault()
                    : releases.FirstOrDefault(r =>
                          string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase)) ??
                      releases.FirstOrDefault(r => string.Equals(r.TagName, version, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InstallationException(
                    version is null
                        ? $"No releases published for {app.GitHubRepo}"
                        : $"Version '{version}' not found in {app.GitHubRepo}",
                    ErrorCategory.Validation);

            var effectiveVersion = release.Version;

            // Phase 2: pick asset for the current platform
            Report("Resolving", "Selecting package for current platform...");
            var asset = PlatformHelper.FindBestAsset(release.Assets)
                ?? throw new InstallationException(
                    $"No release assets found in {release.TagName}", ErrorCategory.Validation);

            // Phase 3: pre-flight disk space check
            var requiredBytes = Math.Max(asset.Size * 3, 128L * 1024 * 1024); // download + extract + headroom
            EnsureDiskSpace(requiredBytes);

            // Phase 4: download into cache
            Report("Downloading", $"Downloading {asset.Name}...", 0);
            PathHelper.EnsureDirectories();
            var cacheDir = Path.Combine(PathHelper.GetCacheDirectory(), app.Id, effectiveVersion);
            var archivePath = Path.Combine(cacheDir, asset.Name);

            var download = await _githubClient.DownloadAssetAsync(
                asset.BrowserDownloadUrl,
                archivePath,
                (total, current) =>
                {
                    progress.TotalBytes = total;
                    progress.DownloadedBytes = current;
                    Report("Downloading",
                        $"Downloading {asset.Name}: {FormatSize(current)}{ (total > 0 ? " / " + FormatSize(total) : "") }",
                        total > 0 ? current * 100.0 / total : 0);
                },
                asset.Sha256,
                cancellationToken);

            // Phase 5: extract to versioned directory
            var targetPath = PathHelper.GetInstallPath(app.Id, effectiveVersion);
            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, recursive: true);
            }
            Directory.CreateDirectory(targetPath);

            Report("Extracting", "Extracting package...", 90);
            await ExtractArchiveAsync(archivePath, targetPath, cancellationToken);

            // Phase 6: record installation
            Report("Recording", "Writing installation record...", 95);
            var installedApp = new InstalledApplication
            {
                Id = app.Id,
                Name = app.Name,
                Description = string.IsNullOrEmpty(release.Body) ? app.Description : release.Body,
                Version = effectiveVersion,
                GitHubRepo = app.GitHubRepo,
                HomePage = app.HomePage,
                Tags = new List<string>(app.Tags),
                Dependencies = app.Dependencies
                    .Select(d => new DependencyRequirement
                    {
                        PackageId = d.PackageId,
                        VersionRange = d.VersionRange,
                        Optional = d.Optional,
                        DisplayName = d.DisplayName
                    }).ToList(),
                IsCoreComponent = app.IsCoreComponent,
                InstallPath = targetPath,
                InstalledAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Checksum = download.Sha256,
                IsActive = true
            };

            await _storage.SaveAsync(app.Id, installedApp);
            UpdateCurrentLink(app.Id, targetPath);

            Report("Done", $"Installed {app.Name} v{effectiveVersion}", 100);
            _logger?.LogInformation("Installed {App} v{Version} to {Path}", app.Id, effectiveVersion, targetPath);

            return installedApp;
        }
        catch (GitHubApiException ex)
        {
            throw new InstallationException(ex.Message, ex, ErrorCategory.Network);
        }
        catch (InvalidDataException ex)
        {
            throw new InstallationException(
                "Download verification failed - file may be corrupted",
                ex, ErrorCategory.Corruption);
        }
        catch (OperationCanceledException)
        {
            throw new InstallationException("Installation cancelled by user", ErrorCategory.None);
        }
    }

    /// <summary>
    /// Uninstall an application, optionally preserving its configuration
    /// </summary>
    public async Task UninstallAsync(
        string appId,
        bool preserveConfig = false,
        Action<InstallProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        var existing = await _storage.LoadAsync<InstalledApplication>(appId)
            ?? throw new InstallationException($"Application '{appId}' is not installed", ErrorCategory.Validation);

        onProgress?.Invoke(new InstallProgress { Phase = "Uninstalling", Message = $"Removing {existing.Name}" });

        var configDir = Path.Combine(existing.InstallPath, ".config");
        var configBackup = Path.Combine(PathHelper.GetCacheDirectory(), "config-backup", appId);

        // Backup configuration if requested
        if (preserveConfig && Directory.Exists(configDir))
        {
            if (Directory.Exists(configBackup))
                Directory.Delete(configBackup, recursive: true);
            CopyDirectory(configDir, configBackup);
        }

        // Remove installation directory
        if (Directory.Exists(existing.InstallPath))
        {
            Directory.Delete(existing.InstallPath, recursive: true);
        }

        // Remove versioned parent if empty (apps/{id} with no other versions)
        var versionRoot = Path.GetDirectoryName(existing.InstallPath);
        if (!string.IsNullOrEmpty(versionRoot) &&
            Directory.Exists(versionRoot) &&
            !Directory.EnumerateFileSystemEntries(versionRoot).Any())
        {
            Directory.Delete(versionRoot);
        }

        // Remove current link
        RemoveCurrentLink(appId);

        // Remove from store
        await _storage.RemoveAsync(appId);

        // Persist the config backup for a future reinstall
        if (preserveConfig && Directory.Exists(configBackup))
        {
            onProgress?.Invoke(new InstallProgress
            {
                Phase = "Uninstalling",
                Message = $"Configuration preserved at {configBackup}"
            });
        }

        _logger?.LogInformation("Uninstalled {App}", appId);
    }

    /// <summary>
    /// Upgrade an installed application to a new version (config preserved)
    /// </summary>
    public async Task<InstalledApplication> UpgradeAsync(
        string appId,
        string? newVersion = null,
        Action<InstallProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _storage.LoadAsync<InstalledApplication>(appId)
            ?? throw new InstallationException($"Application '{appId}' is not installed", ErrorCategory.Validation);

        if (newVersion is not null && string.Equals(existing.Version, newVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallationException(
                $"'{appId}' is already at version {newVersion}", ErrorCategory.Validation);
        }

        // Backup configuration before replacing files
        var configDir = Path.Combine(existing.InstallPath, ".config");
        var configBackup = Path.Combine(PathHelper.GetCacheDirectory(), "config-backup", appId);
        var hadConfig = Directory.Exists(configDir);
        if (hadConfig)
        {
            if (Directory.Exists(configBackup))
                Directory.Delete(configBackup, recursive: true);
            CopyDirectory(configDir, configBackup);
        }

        var manifest = new Application
        {
            Id = existing.Id,
            Name = existing.Name,
            Description = existing.Description,
            GitHubRepo = existing.GitHubRepo,
            HomePage = existing.HomePage,
            Tags = existing.Tags,
            Dependencies = existing.Dependencies,
            IsCoreComponent = existing.IsCoreComponent
        };

        var installed = await InstallAsync(manifest, newVersion, onProgress, cancellationToken);
        installed.ConfigPreserved = hadConfig;

        // Restore configuration into the new version directory
        if (hadConfig && Directory.Exists(configBackup))
        {
            var newConfigDir = Path.Combine(installed.InstallPath, ".config");
            CopyDirectory(configBackup, newConfigDir);
            await _storage.SaveAsync(appId, installed);
        }

        return installed;
    }

    /// <summary>
    /// Health check: verify the installation directory still contains an entry point
    /// </summary>
    public async Task<bool> HealthCheckAsync(string appId, CancellationToken ct = default)
    {
        var app = await _storage.LoadAsync<InstalledApplication>(appId);
        if (app is null || !Directory.Exists(app.InstallPath))
            return false;

        return Directory.EnumerateFiles(app.InstallPath, "*", SearchOption.AllDirectories)
            .Any(f => IsExecutableFile(Path.GetFileName(f)));
    }

    #region Helpers

    private static (string Owner, string Repo) ParseRepo(string githubRepo)
    {
        var parts = githubRepo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InstallationException(
                $"Invalid GitHub repository '{githubRepo}'. Expected 'owner/repo'.",
                ErrorCategory.Validation);
        }
        return (parts[0], parts[1]);
    }

    private static void EnsureDiskSpace(long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(PathHelper.GetAppsDirectory())
                ?? Path.GetPathRoot(AppContext.BaseDirectory)!;
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new InstallationException(
                    $"Insufficient disk space on {root}: required ~{FormatSize(requiredBytes)}, " +
                    $"available {FormatSize(drive.AvailableFreeSpace)}",
                    ErrorCategory.DiskSpace);
            }
        }
        catch (InstallationException)
        {
            throw;
        }
        catch (Exception)
        {
            // Disk space probing is best-effort; skip on unsupported platforms
        }
    }

    private static bool IsExecutableFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".exe" or ".dll" or "";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024:F1} GB"
    };

    private static async Task ExtractArchiveAsync(string archivePath, string targetPath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(archivePath).ToLowerInvariant();

        try
        {
            if (fileName.EndsWith(".zip"))
            {
                ZipFile.ExtractToDirectory(archivePath, targetPath, overwriteFiles: true);
            }
            else if (fileName.EndsWith(".tar.gz") || fileName.EndsWith(".tgz"))
            {
                await using var fileStream = File.OpenRead(archivePath);
                await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, targetPath, overwriteFiles: true, ct);
            }
            else if (fileName.EndsWith(".tar"))
            {
                await TarFile.ExtractToDirectoryAsync(archivePath, targetPath, overwriteFiles: true, ct);
            }
            else
            {
                // Unknown package: copy verbatim so custom installers can consume it
                File.Copy(archivePath, Path.Combine(targetPath, Path.GetFileName(archivePath)), overwrite: true);
            }
        }
        catch (IOException ex)
        {
            throw new InstallationException(
                "Failed to extract package - archive may be corrupted",
                ex, ErrorCategory.Corruption);
        }
        catch (InvalidDataException ex)
        {
            throw new InstallationException(
                "Archive format is invalid - re-download required",
                ex, ErrorCategory.Corruption);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Maintain a 'current' pointer: a junction/symlink when possible, marker file otherwise
    /// </summary>
    private void UpdateCurrentLink(string appId, string versionedPath)
    {
        try
        {
            var linkPath = PathHelper.GetSymlinkPath(appId);
            RemoveCurrentLink(appId);

            if (PlatformHelper.IsWindows)
            {
                Directory.CreateSymbolicLink(linkPath, versionedPath);
            }
            else
            {
                Directory.CreateSymbolicLink(linkPath, versionedPath);
            }
        }
        catch (Exception ex)
        {
            // Symlink creation may require elevated privileges; fall back to a pointer file
            _logger?.LogDebug(ex, "Symlink creation failed for {App}; writing pointer file instead", appId);
            try
            {
                var pointer = Path.Combine(PathHelper.GetCurrentSymlinkDirectory(), appId + ".path");
                File.WriteAllText(pointer, versionedPath);
            }
            catch { /* best effort */ }
        }
    }

    private static void RemoveCurrentLink(string appId)
    {
        try
        {
            var linkPath = PathHelper.GetSymlinkPath(appId);
            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                if (File.Exists(linkPath) && !Directory.Exists(linkPath))
                    File.Delete(linkPath);
                else
                    Directory.Delete(linkPath);
            }

            var pointer = Path.Combine(PathHelper.GetCurrentSymlinkDirectory(), appId + ".path");
            if (File.Exists(pointer)) File.Delete(pointer);
        }
        catch { /* best effort cleanup */ }
    }

    #endregion
}

/// <summary>
/// File system abstraction for testability
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool Exists(string path);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void CreateDirectory(string path);
    IEnumerable<string> GetFilesRecursive(string path);
    FileStream FileOpenRead(string path);
}

public sealed class DefaultFileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public void DeleteFile(string path) => File.Delete(path);
    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public IEnumerable<string> GetFilesRecursive(string path) =>
        Directory.GetFiles(path, "*", SearchOption.AllDirectories);
    public FileStream FileOpenRead(string path) => File.OpenRead(path);
}

/// <summary>
/// Storage provider interface for persistence abstraction.
/// Keys are application ids; installed records are the primary entity.
/// </summary>
public interface IStorageProvider
{
    Task<T?> LoadAsync<T>(string key) where T : class;
    Task SaveAsync<T>(string key, T entity) where T : class;
    Task RemoveAsync(string key);
    Task<IReadOnlyList<InstalledApplication>> ListInstalledAsync();
}
