using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Read-only host filesystem directory browser for Web mode directory/file pickers
/// (workspace folder selection, skill import, Live2D SDK/avatar import). Protected by
/// Core's host-control gate and Gateway Web authentication.
/// </summary>
public static class FilesystemBrowseEndpoints
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 1000;
    private const string RootsSentinel = "__roots__";

    public static IEndpointRouteBuilder MapFilesystemBrowseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/fs/browse", (
            string? path,
            bool? include_files,
            bool? show_hidden,
            int? limit) =>
        {
            var homePath = ResolveHomePath();
            var roots = BuildRoots(homePath);
            var platform = ResolvePlatform();
            var separator = Path.DirectorySeparatorChar.ToString();
            var includeFiles = include_files ?? false;
            var showHidden = show_hidden ?? false;
            var maxEntries = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            if (string.Equals(path?.Trim(), RootsSentinel, StringComparison.OrdinalIgnoreCase))
            {
                var rootEntries = roots
                    .Where(r => r.Kind == "drive")
                    .Select(r => new FilesystemBrowseEntryDto
                    {
                        Name = r.Name,
                        Path = r.Path,
                        Kind = "drive",
                        IsDir = true,
                        HasChildren = true,
                        IsGitRepo = false,
                        IsHidden = false
                    })
                    .ToArray();

                return Results.Ok(new FilesystemBrowseResponseDto
                {
                    Platform = platform,
                    Separator = separator,
                    HomePath = homePath,
                    CurrentPath = string.Empty,
                    ParentPath = null,
                    Roots = roots,
                    Entries = rootEntries,
                    Truncated = false
                });
            }

            string resolvedPath;
            try
            {
                var raw = string.IsNullOrWhiteSpace(path) ? homePath : ExpandTilde(path.Trim(), homePath);
                resolvedPath = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { code = "invalid_path", message = ex.Message });
            }

            if (!Directory.Exists(resolvedPath))
            {
                return Results.NotFound(new
                {
                    code = "directory_not_found",
                    message = $"Directory '{resolvedPath}' does not exist."
                });
            }

            var directory = new DirectoryInfo(resolvedPath);
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = showHidden
                    ? FileAttributes.System
                    : (FileAttributes.Hidden | FileAttributes.System)
            };

            var dirEntries = new List<FilesystemBrowseEntryDto>();
            var fileEntries = new List<FilesystemBrowseEntryDto>();
            var truncated = false;

            try
            {
                foreach (var sub in directory.EnumerateDirectories("*", enumOptions))
                {
                    if (dirEntries.Count >= maxEntries)
                    {
                        truncated = true;
                        break;
                    }
                    dirEntries.Add(ProjectDirectory(sub));
                }

                if (includeFiles && !truncated)
                {
                    foreach (var file in directory.EnumerateFiles("*", enumOptions))
                    {
                        if (dirEntries.Count + fileEntries.Count >= maxEntries)
                        {
                            truncated = true;
                            break;
                        }
                        fileEntries.Add(ProjectFile(file));
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(
                    new { code = "directory_unreadable", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (IOException ex)
            {
                return Results.Json(
                    new { code = "directory_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status409Conflict);
            }

            dirEntries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            fileEntries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            var entries = new List<FilesystemBrowseEntryDto>(dirEntries.Count + fileEntries.Count);
            entries.AddRange(dirEntries);
            entries.AddRange(fileEntries);

            return Results.Ok(new FilesystemBrowseResponseDto
            {
                Platform = platform,
                Separator = separator,
                HomePath = homePath,
                CurrentPath = directory.FullName,
                ParentPath = Directory.GetParent(directory.FullName)?.FullName,
                Roots = roots,
                Entries = entries,
                Truncated = truncated
            });
        })
        .WithTags("Filesystem")
        .WithSummary("Browse host directories and files")
        .Produces<FilesystemBrowseResponseDto>(StatusCodes.Status200OK);

        return app;
    }

    private static string ResolveHomePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile))
            return Path.GetFullPath(profile);
        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static string ExpandTilde(string input, string homePath)
    {
        if (input == "~") return homePath;
        if (input.StartsWith("~/", StringComparison.Ordinal) || input.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(homePath, input[2..]);
        return input;
    }

    private static string ResolvePlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }

    private static IReadOnlyList<FilesystemBrowseRootDto> BuildRoots(string homePath)
    {
        var roots = new List<FilesystemBrowseRootDto>
        {
            new() { Name = "Home", Path = homePath, Kind = "home" }
        };

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var full = drive.RootDirectory.FullName;
                if (string.Equals(full, homePath, StringComparison.OrdinalIgnoreCase)) continue;
                roots.Add(new FilesystemBrowseRootDto
                {
                    Name = drive.Name,
                    Path = full,
                    Kind = "drive"
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drive enumeration is best-effort; Home root remains available.
        }

        return roots;
    }

    private static FilesystemBrowseEntryDto ProjectDirectory(DirectoryInfo dir)
    {
        var hasChildren = false;
        var isGitRepo = false;
        try
        {
            isGitRepo = Directory.Exists(Path.Combine(dir.FullName, ".git"));
            hasChildren = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true
            }).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hasChildren = false;
        }

        return new FilesystemBrowseEntryDto
        {
            Name = dir.Name,
            Path = dir.FullName,
            Kind = "directory",
            IsDir = true,
            HasChildren = hasChildren,
            IsGitRepo = isGitRepo,
            IsHidden = (dir.Attributes & FileAttributes.Hidden) != 0,
            ModifiedAt = SafeLastWriteUtc(dir)
        };
    }

    private static FilesystemBrowseEntryDto ProjectFile(FileInfo file)
    {
        long? sizeBytes = null;
        try { sizeBytes = file.Length; }
        catch (IOException) { sizeBytes = null; }

        return new FilesystemBrowseEntryDto
        {
            Name = file.Name,
            Path = file.FullName,
            Kind = "file",
            IsDir = false,
            HasChildren = false,
            IsGitRepo = false,
            IsHidden = (file.Attributes & FileAttributes.Hidden) != 0,
            SizeBytes = sizeBytes,
            ModifiedAt = SafeLastWriteUtc(file)
        };
    }

    private static DateTimeOffset? SafeLastWriteUtc(FileSystemInfo info)
    {
        try { return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero); }
        catch (Exception) { return null; }
    }
}

public sealed record FilesystemBrowseRootDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "drive";
}

public sealed record FilesystemBrowseEntryDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "directory";

    [JsonPropertyName("is_dir")]
    public bool IsDir { get; init; }

    [JsonPropertyName("has_children")]
    public bool HasChildren { get; init; }

    [JsonPropertyName("is_git_repo")]
    public bool IsGitRepo { get; init; }

    [JsonPropertyName("is_hidden")]
    public bool IsHidden { get; init; }

    [JsonPropertyName("size_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("modified_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ModifiedAt { get; init; }
}

public sealed record FilesystemBrowseResponseDto
{
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "windows";

    [JsonPropertyName("separator")]
    public string Separator { get; init; } = "\\";

    [JsonPropertyName("home_path")]
    public string HomePath { get; init; } = string.Empty;

    [JsonPropertyName("current_path")]
    public string CurrentPath { get; init; } = string.Empty;

    [JsonPropertyName("parent_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentPath { get; init; }

    [JsonPropertyName("roots")]
    public IReadOnlyList<FilesystemBrowseRootDto> Roots { get; init; } = [];

    [JsonPropertyName("entries")]
    public IReadOnlyList<FilesystemBrowseEntryDto> Entries { get; init; } = [];

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}
