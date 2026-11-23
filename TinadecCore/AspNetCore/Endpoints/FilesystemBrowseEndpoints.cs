using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Host filesystem directory browser, inline folder creation, and safe background media
/// streaming for Web and Electron pickers (workspace folder selection, skill import,
/// Live2D SDK/avatar import, custom background image/video). Protected by Core's
/// host-control gate and Gateway Web authentication.
/// </summary>
public static class FilesystemBrowseEndpoints
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 1000;
    private const int MaxDirectoryNameLength = 255;
    private const string RootsSentinel = "__roots__";

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly Dictionary<string, string> AllowedMediaContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".m4v"] = "video/x-m4v",
        [".ogg"] = "video/ogg",
        [".ogv"] = "video/ogg",
    };

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

        app.MapPost("/api/v1/fs/mkdir", (CreateHostDirectoryRequestDto? request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ParentPath))
            {
                return Results.BadRequest(new
                {
                    code = "invalid_path",
                    message = "parent_path is required."
                });
            }

            var rawParent = request.ParentPath.Trim();
            if (rawParent.Contains('\0')
                || string.Equals(rawParent, RootsSentinel, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    code = "invalid_path",
                    message = "parent_path must be a valid host directory path."
                });
            }

            var homePath = ResolveHomePath();
            string resolvedParent;
            try
            {
                var expanded = ExpandTilde(rawParent, homePath);
                resolvedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { code = "invalid_path", message = ex.Message });
            }

            if (!Directory.Exists(resolvedParent))
            {
                return Results.NotFound(new
                {
                    code = "directory_not_found",
                    message = $"Directory '{resolvedParent}' does not exist."
                });
            }

            var trimmedName = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName)
                || trimmedName is "." or ".."
                || trimmedName.Length > MaxDirectoryNameLength
                || trimmedName.IndexOfAny(['/', '\\', ':']) >= 0
                || trimmedName.Any(char.IsControl)
                || trimmedName.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_directory_name",
                    message = "Directory name must be a single valid folder name without path separators."
                });
            }

            string targetPath;
            try
            {
                targetPath = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.Combine(resolvedParent, trimmedName)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { code = "invalid_directory_name", message = ex.Message });
            }

            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var targetParent = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(targetParent)
                || !string.Equals(Path.TrimEndingDirectorySeparator(targetParent), resolvedParent, pathComparison)
                || string.Equals(targetPath, resolvedParent, pathComparison))
            {
                return Results.BadRequest(new
                {
                    code = "invalid_directory_name",
                    message = "Directory name resolves outside the target parent directory."
                });
            }

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                return Results.Conflict(new
                {
                    code = "already_exists",
                    message = $"'{trimmedName}' already exists in '{resolvedParent}'."
                });
            }

            try
            {
                var created = Directory.CreateDirectory(targetPath);
                return Results.Ok(new CreateHostDirectoryResponseDto
                {
                    CreatedPath = created.FullName,
                    Entry = ProjectDirectory(created)
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(
                    new { code = "directory_forbidden", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (IOException ex)
            {
                if (Directory.Exists(targetPath) || File.Exists(targetPath))
                {
                    return Results.Conflict(new
                    {
                        code = "already_exists",
                        message = $"'{trimmedName}' already exists in '{resolvedParent}'."
                    });
                }

                return Results.Json(
                    new { code = "directory_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Results.BadRequest(new { code = "invalid_directory_name", message = ex.Message });
            }
        })
        .WithTags("Filesystem")
        .WithSummary("Create a directory on the host filesystem")
        .Produces<CreateHostDirectoryResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet("/api/v1/fs/media", (string? token, HttpContext httpContext) =>
        {
            if (!TryDecodeMediaPathToken(token, out var decodedPath) || !IsRootedFilePath(decodedPath))
            {
                return Results.BadRequest(new
                {
                    code = "invalid_media_token",
                    message = "Media token is missing or does not encode a valid rooted path."
                });
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(decodedPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_media_token",
                    message = ex.Message
                });
            }

            var extension = Path.GetExtension(fullPath);
            if (string.IsNullOrEmpty(extension)
                || !AllowedMediaContentTypes.TryGetValue(extension, out var contentType))
            {
                return Results.Json(
                    new
                    {
                        code = "unsupported_media_type",
                        message = $"File extension '{extension}' is not an allowed raster-image or video type."
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (!File.Exists(fullPath))
            {
                return Results.NotFound(new
                {
                    code = "file_not_found",
                    message = "Requested media file does not exist."
                });
            }

            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            httpContext.Response.Headers.CacheControl = "private, no-cache";
            return Results.File(fullPath, contentType, enableRangeProcessing: true);
        })
        .WithTags("Filesystem")
        .WithSummary("Stream host background image or video media")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status206PartialContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

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
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
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

    private static bool IsRootedFilePath(string path)
    {
        if (!Path.IsPathRooted(path))
            return false;

        // Reject Windows drive-relative paths like "C:foo.png" that lack a root separator.
        if (path.Length >= 2
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path.Length == 2 || (path[2] != '\\' && path[2] != '/')))
        {
            return false;
        }

        return true;
    }

    private static bool TryDecodeMediaPathToken(string? token, out string decodedPath)
    {
        decodedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var trimmed = token.Trim().TrimEnd('=');
        if (trimmed.Length == 0 || (trimmed.Length % 4) == 1)
            return false;

        foreach (var ch in trimmed)
        {
            var isValid = (ch is >= 'A' and <= 'Z')
                || (ch is >= 'a' and <= 'z')
                || (ch is >= '0' and <= '9')
                || ch is '-' or '_';
            if (!isValid)
                return false;
        }

        var paddedLength = trimmed.Length + ((4 - (trimmed.Length % 4)) % 4);
        var base64Chars = new char[paddedLength];
        for (var i = 0; i < trimmed.Length; i++)
        {
            base64Chars[i] = trimmed[i] switch
            {
                '-' => '+',
                '_' => '/',
                var c => c
            };
        }

        for (var i = trimmed.Length; i < paddedLength; i++)
        {
            base64Chars[i] = '=';
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64CharArray(base64Chars, 0, base64Chars.Length);
        }
        catch (FormatException)
        {
            return false;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || text.Any(char.IsControl))
            return false;

        decodedPath = text;
        return true;
    }
}

public sealed record CreateHostDirectoryRequestDto
{
    [JsonPropertyName("parent_path")]
    public string? ParentPath { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record CreateHostDirectoryResponseDto
{
    [JsonPropertyName("created_path")]
    public string CreatedPath { get; init; } = string.Empty;

    [JsonPropertyName("entry")]
    public FilesystemBrowseEntryDto Entry { get; init; } = new();
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
