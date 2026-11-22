using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Companion;

public sealed record CompanionServedAsset(
    Stream Stream,
    string ContentType,
    string ETag);

/// <summary>
/// Bring-your-own Live2D model and Cubism Core SDK registry. Core never bundles Cubism Core or a
/// model; it only copies what the user selected into its own storage root, blocks executable scripts
/// inside model directories, and serves pure-data model assets plus the user-imported SDK.
/// </summary>
public sealed class CompanionAvatarStore(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ITenantContextAccessor tenant,
    StoragePaths paths)
{
    private const string AvatarDirectory = "companion-avatars";
    private const string SdkJsFileName = "live2dcubismcore.min.js";
    private const string SdkWasmFileName = "live2dcubismcore.wasm";
    private const string SdkManifestFileName = "sdk-manifest.json";
    private const int MaxAvatarFiles = 2048;
    private const long MaxAvatarBytes = 256L * 1024 * 1024;
    private const long MaxSdkBytes = 32L * 1024 * 1024;

    private static readonly HashSet<string> ForbiddenModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx",
        ".html", ".htm", ".xhtml", ".svg", ".wasm",
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh"
    };

    private static readonly Dictionary<string, string> AllowedModelAssetContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json; charset=utf-8",
        [".moc3"] = "application/octet-stream",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".txt"] = "text/plain; charset=utf-8"
    };

    private Guid TenantId => tenant.Current.TenantId;

    public async Task<object> ListAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Avatars.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // SQLite refuses ORDER BY on DateTimeOffset, so the ordering happens after materialising.
        // CompanionService orders the same way for the same reason.
        return rows
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(ToJson)
            .ToList();
    }

    public async Task<object> ImportAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new CompanionException("avatar_path_required", "A model directory path is required.", 400);
        if (!Path.IsPathRooted(inputPath))
            throw new CompanionException("avatar_path_not_absolute", "The model directory path must be absolute.", 400);

        string source;
        try
        {
            source = Path.GetFullPath(inputPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CompanionException("avatar_path_invalid", "The model directory path is invalid: " + error.Message, 400);
        }

        if (File.Exists(source))
            throw new CompanionException("avatar_path_not_directory", "The model path is a file, not a directory.", 400);
        if (!Directory.Exists(source))
            throw new CompanionException("avatar_path_not_found", "The model directory does not exist.", 400);
        if (IsLink(File.GetAttributes(source)))
            throw new CompanionException("avatar_symlink_rejected", "The model directory must not be a symbolic link or junction.", 400);
        // Importing an ancestor of the data root would copy the destination into itself while it
        // is being written. That is not a security boundary but it is an unbounded copy.
        if (IsSameOrAncestor(source, paths.Root))
            throw new CompanionException("avatar_path_overlaps_storage", "The model directory must not contain Core's storage root.", 400);

        var modelJson = Path.Combine(source, "model3.json");
        if (!File.Exists(modelJson))
        {
            var candidateModels = Directory.EnumerateFiles(source, "*.model3.json", SearchOption.TopDirectoryOnly).ToArray();
            if (candidateModels.Length == 1)
                modelJson = candidateModels[0];
            else
                throw new CompanionException("model3_missing", "The model directory does not contain model3.json.", 400);
        }
        if (IsLink(File.GetAttributes(modelJson)))
            throw new CompanionException("avatar_symlink_rejected", "model3.json must not be a symbolic link or junction.", 400);

        string capabilitiesJson;
        try
        {
            capabilitiesJson = ParseCapabilities(await File.ReadAllTextAsync(modelJson, cancellationToken).ConfigureAwait(false));
        }
        catch (IOException error)
        {
            throw new CompanionException("model3_unreadable", "model3.json could not be read: " + error.Message, 400);
        }

        var id = Guid.NewGuid();
        // A GUID segment is minted here, never taken from the source directory name, so a name
        // like ".." cannot influence where the copy lands.
        var relativePath = $"{AvatarDirectory}/tenants/{tenantId:N}/{id:N}";
        var destination = StorageScopePaths.Contained(paths.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        // The precise self-copy guard: the copy must not land inside the directory it is reading.
        // Picking Core's own companion-avatars folder as the source is the realistic way to hit
        // this, and it would recurse into its own output until the disk filled. A source merely
        // *inside* the data root is otherwise fine — it is not an ancestor of the destination.
        if (IsSameOrAncestor(source, destination))
            throw new CompanionException("avatar_path_overlaps_storage", "The model directory must not contain Core's storage root.", 400);

        try
        {
            CopyTree(source, source, destination, new CopyBudget(), cancellationToken);
        }
        catch (CompanionException)
        {
            TryDeleteTree(destination);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDeleteTree(destination);
            throw new CompanionException("avatar_copy_failed", "Copying the model directory failed: " + error.Message, 409);
        }

        var row = new CompanionAvatarRecord
        {
            Id = id,
            TenantId = tenantId,
            Name = AvatarName(source),
            Kind = "live2d",
            SourcePath = relativePath,
            CapabilitiesJson = capabilitiesJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Avatars.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The copy is only reachable through this row. Leaving it behind on a failed insert
            // would accumulate directories nobody can list or delete.
            TryDeleteTree(destination);
            throw;
        }

        return ToJson(row);
    }

    public async Task<bool> DeleteAsync(Guid avatarId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Avatars.SingleOrDefaultAsync(
            x => x.Id == avatarId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return false;

        var directory = ResolveStoredPath(row.SourcePath);
        // A directory that is already gone is not an error: the user may have cleaned the data
        // root by hand, and the row must still be removable.
        if (Directory.Exists(directory)) DeleteTreeNoFollow(directory);
        else if (File.Exists(directory)) File.Delete(directory);

        db.Avatars.Remove(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private string ResolveStoredPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new CompanionException("avatar_source_invalid", "The stored avatar path is not a Core-owned relative path.", 409);
        return StorageScopePaths.Contained(paths.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private object ToJson(CompanionAvatarRecord row)
    {
        var entryModel = "model3.json";
        try
        {
            var dir = ResolveStoredPath(row.SourcePath);
            if (Directory.Exists(dir) && !File.Exists(Path.Combine(dir, "model3.json")))
            {
                var found = Directory.EnumerateFiles(dir, "*.model3.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (found is not null) entryModel = Path.GetFileName(found);
            }
        }
        catch (CompanionException)
        {
            // Keep default entry_model_json if stored path was manually removed.
        }

        return new
        {
            id = row.Id,
            name = row.Name,
            kind = row.Kind,
            source_path = row.SourcePath,
            entry_model_json = entryModel,
            capabilities = JsonNode.Parse(row.CapabilitiesJson) ?? new JsonObject(),
            created_at = row.CreatedAt
        };
    }

    /// <summary>
    /// Opens a pure-data asset file inside an imported Live2D model directory for this tenant.
    /// Rejects path traversal, symlinks, and executable/active web extensions.
    /// </summary>
    public async Task<CompanionServedAsset?> OpenAssetAsync(
        Guid avatarId, string? relativePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new CompanionException("avatar_asset_invalid", "Asset path is required.", 400);

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(normalized)
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg is "." or ".."))
        {
            throw new CompanionException("avatar_path_escape", "Asset path must not escape the model directory.", 400);
        }

        var ext = Path.GetExtension(normalized);
        if (ForbiddenModelExtensions.Contains(ext))
            throw new CompanionException("avatar_script_rejected", "Executable or active web assets cannot be served from a Live2D model pack.", 400);
        if (!AllowedModelAssetContentTypes.TryGetValue(ext, out var contentType))
            throw new CompanionException("avatar_asset_unsupported", $"Unsupported Live2D asset extension '{ext}'.", 400);

        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Avatars.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == avatarId && x.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;

        var avatarRoot = ResolveStoredPath(row.SourcePath);
        var filePath = StorageScopePaths.Contained(avatarRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(filePath)) return null;
        if (IsLink(File.GetAttributes(filePath)))
            throw new CompanionException("avatar_symlink_rejected", "Symbolic links cannot be served.", 400);

        var info = new FileInfo(filePath);
        var etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return new CompanionServedAsset(stream, contentType, etag);
    }

    // ── BYO Cubism Core SDK ──

    public Task<object> GetSdkStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sdkDir = GetTenantSdkDirectory(TenantId);
        var jsPath = Path.Combine(sdkDir, SdkJsFileName);
        var wasmPath = Path.Combine(sdkDir, SdkWasmFileName);
        var manifestPath = Path.Combine(sdkDir, SdkManifestFileName);

        if (!File.Exists(jsPath))
        {
            return Task.FromResult<object>(new
            {
                ready = false,
                installed = false,
                file_name = (string?)null,
                sha256 = (string?)null,
                size_bytes = 0L,
                has_wasm = false,
                script_url = (string?)null,
                wasm_url = (string?)null,
                updated_at = (DateTimeOffset?)null
            });
        }

        string? sha256 = null;
        string fileName = SdkJsFileName;
        DateTimeOffset updatedAt = new FileInfo(jsPath).LastWriteTimeUtc;
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("file_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                    fileName = fn.GetString() ?? SdkJsFileName;
                if (root.TryGetProperty("sha256", out var sh) && sh.ValueKind == JsonValueKind.String)
                    sha256 = sh.GetString();
                if (root.TryGetProperty("updated_at", out var up) && up.TryGetDateTimeOffset(out var parsedTime))
                    updatedAt = parsedTime;
            }
            catch (JsonException)
            {
                // Recompute below if manifest was corrupted.
            }
        }

        sha256 ??= Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(jsPath))).ToLowerInvariant();
        var hasWasm = File.Exists(wasmPath);
        var sizeBytes = new FileInfo(jsPath).Length;

        return Task.FromResult<object>(new
        {
            ready = true,
            installed = true,
            file_name = fileName,
            sha256,
            size_bytes = sizeBytes,
            has_wasm = hasWasm,
            script_url = $"/api/v1/companion-avatars/sdk/{SdkJsFileName}",
            wasm_url = hasWasm ? $"/api/v1/companion-avatars/sdk/{SdkWasmFileName}" : null,
            updated_at = (DateTimeOffset?)updatedAt
        });
    }

    public async Task<object> ImportSdkAsync(CompanionSdkImportRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        byte[] jsBytes;
        byte[]? wasmBytes = null;
        string importedFileName = string.IsNullOrWhiteSpace(input.FileName) ? SdkJsFileName : Path.GetFileName(input.FileName.Trim());

        if (!string.IsNullOrWhiteSpace(input.ContentBase64))
        {
            try
            {
                jsBytes = Convert.FromBase64String(StripDataUrlPrefix(input.ContentBase64));
            }
            catch (FormatException)
            {
                throw new CompanionException("sdk_invalid_base64", "content_base64 is not valid base64.", 400);
            }

            if (!string.IsNullOrWhiteSpace(input.WasmBase64))
            {
                try
                {
                    wasmBytes = Convert.FromBase64String(StripDataUrlPrefix(input.WasmBase64));
                }
                catch (FormatException)
                {
                    throw new CompanionException("sdk_invalid_base64", "wasm_base64 is not valid base64.", 400);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(input.Path))
        {
            if (!Path.IsPathRooted(input.Path))
                throw new CompanionException("sdk_path_not_absolute", "The Cubism Core SDK path must be absolute.", 400);

            var fullPath = Path.GetFullPath(input.Path);
            string jsSourcePath;
            if (Directory.Exists(fullPath))
            {
                jsSourcePath = Path.Combine(fullPath, SdkJsFileName);
                if (!File.Exists(jsSourcePath))
                {
                    var alt = Path.Combine(fullPath, "live2dcubismcore.js");
                    if (File.Exists(alt)) jsSourcePath = alt;
                    else throw new CompanionException("sdk_file_not_found", $"Directory does not contain {SdkJsFileName}.", 400);
                }
            }
            else if (File.Exists(fullPath))
            {
                var ext = Path.GetExtension(fullPath);
                if (!string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ext, ".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CompanionException("sdk_invalid_extension", "Cubism Core SDK file must be a .js file.", 400);
                }
                jsSourcePath = fullPath;
            }
            else
            {
                throw new CompanionException("sdk_file_not_found", "The specified Cubism Core SDK path does not exist.", 400);
            }

            if (IsLink(File.GetAttributes(jsSourcePath)))
                throw new CompanionException("sdk_symlink_rejected", "Cubism Core SDK file must not be a symbolic link.", 400);

            var jsInfo = new FileInfo(jsSourcePath);
            if (jsInfo.Length <= 0 || jsInfo.Length > MaxSdkBytes)
                throw new CompanionException("sdk_invalid_size", "Cubism Core SDK file is empty or exceeds 32 MiB.", 400);

            importedFileName = jsInfo.Name;
            jsBytes = await File.ReadAllBytesAsync(jsSourcePath, cancellationToken).ConfigureAwait(false);

            var siblingWasm = Path.Combine(Path.GetDirectoryName(jsSourcePath)!, SdkWasmFileName);
            if (File.Exists(siblingWasm) && !IsLink(File.GetAttributes(siblingWasm)))
            {
                var wasmInfo = new FileInfo(siblingWasm);
                if (wasmInfo.Length > 0 && wasmInfo.Length <= MaxSdkBytes)
                    wasmBytes = await File.ReadAllBytesAsync(siblingWasm, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            throw new CompanionException("sdk_input_required", "Provide either path or content_base64 to import Cubism Core SDK.", 400);
        }

        if (jsBytes.Length == 0 || jsBytes.Length > MaxSdkBytes)
            throw new CompanionException("sdk_invalid_size", "Cubism Core SDK script is empty or exceeds 32 MiB.", 400);

        var headSample = Encoding.UTF8.GetString(jsBytes, 0, Math.Min(jsBytes.Length, 512)).TrimStart();
        if (headSample.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || headSample.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            throw new CompanionException("sdk_invalid_content", "Imported file appears to be an HTML document, not live2dcubismcore.min.js.", 400);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(jsBytes)).ToLowerInvariant();
        var wasmSha256 = wasmBytes is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(wasmBytes)).ToLowerInvariant()
            : null;
        var now = DateTimeOffset.UtcNow;

        var sdkDir = GetTenantSdkDirectory(tenantId);
        Directory.CreateDirectory(sdkDir);
        await File.WriteAllBytesAsync(Path.Combine(sdkDir, SdkJsFileName), jsBytes, cancellationToken).ConfigureAwait(false);

        var targetWasm = Path.Combine(sdkDir, SdkWasmFileName);
        if (wasmBytes is { Length: > 0 })
            await File.WriteAllBytesAsync(targetWasm, wasmBytes, cancellationToken).ConfigureAwait(false);
        else if (File.Exists(targetWasm))
            File.Delete(targetWasm);

        var manifest = JsonSerializer.Serialize(new
        {
            file_name = importedFileName,
            sha256,
            size_bytes = (long)jsBytes.Length,
            has_wasm = wasmBytes is { Length: > 0 },
            wasm_sha256 = wasmSha256,
            updated_at = now
        });
        await File.WriteAllTextAsync(Path.Combine(sdkDir, SdkManifestFileName), manifest, cancellationToken).ConfigureAwait(false);

        return await GetSdkStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<CompanionServedAsset?> OpenSdkAssetAsync(string? assetName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trimmed = assetName?.Trim();
        string fileName;
        string contentType;
        if (string.Equals(trimmed, SdkJsFileName, StringComparison.Ordinal))
        {
            fileName = SdkJsFileName;
            contentType = "text/javascript; charset=utf-8";
        }
        else if (string.Equals(trimmed, SdkWasmFileName, StringComparison.Ordinal))
        {
            fileName = SdkWasmFileName;
            contentType = "application/wasm";
        }
        else
        {
            return Task.FromResult<CompanionServedAsset?>(null);
        }

        var sdkDir = GetTenantSdkDirectory(TenantId);
        var fullPath = Path.Combine(sdkDir, fileName);
        if (!File.Exists(fullPath) || IsLink(File.GetAttributes(fullPath)))
            return Task.FromResult<CompanionServedAsset?>(null);

        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return Task.FromResult<CompanionServedAsset?>(new CompanionServedAsset(stream, contentType, $"\"{sha256}\""));
    }

    public Task DeleteSdkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sdkDir = GetTenantSdkDirectory(TenantId);
        if (Directory.Exists(sdkDir))
            DeleteTreeNoFollow(sdkDir);
        return Task.CompletedTask;
    }

    private string GetTenantSdkDirectory(Guid tenantId) =>
        StorageScopePaths.Contained(
            paths.Root,
            $"{AvatarDirectory}/sdk/tenants/{tenantId:N}".Replace('/', Path.DirectorySeparatorChar));

    private static string StripDataUrlPrefix(string raw)
    {
        var trimmed = raw.Trim();
        var comma = trimmed.IndexOf(',');
        return trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
            ? trimmed[(comma + 1)..]
            : trimmed;
    }

    private sealed class CopyBudget
    {
        public long Bytes;
        public int Files;
    }

    private static void CopyTree(
        string sourceRoot, string current, string destinationRoot, CopyBudget budget, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            var relative = Path.GetRelativePath(sourceRoot, entry);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
                throw new CompanionException("avatar_path_escape", "The model directory contains a path that escapes it: " + relative, 400);
            if (IsLink(attributes))
                throw new CompanionException(
                    "avatar_symlink_rejected",
                    "The model directory contains a symbolic link or junction: " + relative, 400);

            var target = StorageScopePaths.Contained(destinationRoot, relative);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyTree(sourceRoot, entry, destinationRoot, budget, cancellationToken);
            }
            else
            {
                var ext = Path.GetExtension(entry);
                if (ForbiddenModelExtensions.Contains(ext))
                {
                    throw new CompanionException(
                        "avatar_script_rejected",
                        $"Executable or active web file is not permitted inside a Live2D model directory: {relative}",
                        400);
                }
                // Bounded before the copy, not after: a model is a few hundred files, and selecting
                // a multi-gigabyte folder by mistake must fail loudly instead of filling the disk.
                budget.Files++;
                if (budget.Files > MaxAvatarFiles)
                    throw new CompanionException("avatar_too_many_files", $"The model directory has more than {MaxAvatarFiles} files.", 400);
                budget.Bytes += new FileInfo(entry).Length;
                if (budget.Bytes > MaxAvatarBytes)
                    throw new CompanionException("avatar_too_large", $"The model directory is larger than {MaxAvatarBytes / (1024 * 1024)} MiB.", 400);
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private static void DeleteTreeNoFollow(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (IsLink(attributes))
            {
                // Deleting the link itself, never what it points at: a link planted after import
                // must not turn a delete into a write outside the storage root.
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, recursive: false);
                else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTreeNoFollow(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(directory, recursive: false);
    }

    private static void TryDeleteTree(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) DeleteTreeNoFollow(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The original failure is the one worth reporting; a cleanup miss only leaves an
            // orphaned directory under Core's own root.
        }
    }

    private static bool IsLink(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsSameOrAncestor(string candidate, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ancestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(ancestor, target, comparison)
            || target.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }

    private static string AvatarName(string source)
    {
        var name = new DirectoryInfo(source).Name;
        return name.Length <= 128 ? name : name[..128];
    }

    /// <summary>
    /// Reads the capability shape the renderer will need from model3.json. Absent sections stay
    /// empty; a section that exists but has the wrong shape fails with the exact field path, so
    /// the user can fix the model instead of reading "invalid".
    /// </summary>
    private static string ParseCapabilities(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new CompanionException("model3_invalid_json", "model3.json is not valid JSON: " + error.Message, 400);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid("model3.json root", "a JSON object");

            var motions = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            var expressions = new List<string>();
            var parameters = new SortedDictionary<string, ParameterRange>(StringComparer.Ordinal);
            var hitAreas = new List<string>();
            var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

            if (TryGet(root, "FileReferences", out var fileReferences))
            {
                if (fileReferences.ValueKind != JsonValueKind.Object)
                    throw Invalid("FileReferences", "an object");

                if (TryGet(fileReferences, "Motions", out var motionGroups))
                {
                    if (motionGroups.ValueKind != JsonValueKind.Object)
                        throw Invalid("FileReferences.Motions", "an object");
                    foreach (var group in motionGroups.EnumerateObject())
                    {
                        if (group.Value.ValueKind != JsonValueKind.Array)
                            throw Invalid($"FileReferences.Motions.{group.Name}", "an array");
                        var names = new List<string>();
                        var index = 0;
                        foreach (var motion in group.Value.EnumerateArray())
                        {
                            if (motion.ValueKind != JsonValueKind.Object)
                                throw Invalid($"FileReferences.Motions.{group.Name}[{index}]", "an object");
                            var name = ReadString(motion, "Name");
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                var file = ReadString(motion, "File");
                                if (string.IsNullOrWhiteSpace(file))
                                    throw Invalid($"FileReferences.Motions.{group.Name}[{index}]", "a string Name or File");
                                name = MotionNameFromFile(file);
                            }
                            if (string.IsNullOrWhiteSpace(name))
                                throw Invalid($"FileReferences.Motions.{group.Name}[{index}].File", "a file name that yields a motion name");
                            names.Add(name);
                            index++;
                        }
                        motions[group.Name] = names;
                    }
                }

                if (TryGet(fileReferences, "Expressions", out var expressionList))
                {
                    if (expressionList.ValueKind != JsonValueKind.Array)
                        throw Invalid("FileReferences.Expressions", "an array");
                    var index = 0;
                    foreach (var expression in expressionList.EnumerateArray())
                    {
                        if (expression.ValueKind != JsonValueKind.Object)
                            throw Invalid($"FileReferences.Expressions[{index}]", "an object");
                        var name = ReadString(expression, "Name");
                        if (string.IsNullOrWhiteSpace(name))
                            throw Invalid($"FileReferences.Expressions[{index}].Name", "a non-empty string");
                        expressions.Add(name);
                        index++;
                    }
                }
            }

            if (TryGet(root, "Groups", out var groupList))
            {
                if (groupList.ValueKind != JsonValueKind.Array)
                    throw Invalid("Groups", "an array");
                var index = 0;
                foreach (var group in groupList.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object)
                        throw Invalid($"Groups[{index}]", "an object");
                    // Target=Part groups name parts, not parameters; the capability contract is
                    // parameter ids, so they are not mixed in.
                    var target = ReadString(group, "Target");
                    if (!string.IsNullOrWhiteSpace(target) && !string.Equals(target, "Parameter", StringComparison.OrdinalIgnoreCase))
                    {
                        index++;
                        continue;
                    }
                    var name = ReadString(group, "Name");
                    if (string.IsNullOrWhiteSpace(name))
                        throw Invalid($"Groups[{index}].Name", "a non-empty string");
                    if (!TryGet(group, "Ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                        throw Invalid($"Groups[{index}].Ids", "an array of parameter ids");
                    var values = new List<string>();
                    var idIndex = 0;
                    foreach (var id in ids.EnumerateArray())
                    {
                        if (id.ValueKind != JsonValueKind.String)
                            throw Invalid($"Groups[{index}].Ids[{idIndex}]", "a string");
                        values.Add(id.GetString()!);
                        idIndex++;
                    }
                    groups[name] = values;
                    index++;
                }
            }

            if (TryGet(root, "HitAreas", out var hitAreaList))
            {
                if (hitAreaList.ValueKind != JsonValueKind.Array)
                    throw Invalid("HitAreas", "an array");
                var index = 0;
                foreach (var hitArea in hitAreaList.EnumerateArray())
                {
                    if (hitArea.ValueKind != JsonValueKind.Object)
                        throw Invalid($"HitAreas[{index}]", "an object");
                    var id = ReadString(hitArea, "Id");
                    if (string.IsNullOrWhiteSpace(id))
                        throw Invalid($"HitAreas[{index}].Id", "a non-empty string");
                    hitAreas.Add(id);
                    index++;
                }
            }

            // Standard model3.json carries no parameter ranges (they live in the moc/cdi files).
            // A model that does declare them here is read; otherwise the map stays empty rather
            // than inventing min/max/default values.
            if (TryGet(root, "Parameters", out var parameterMap))
            {
                if (parameterMap.ValueKind != JsonValueKind.Object)
                    throw Invalid("Parameters", "an object");
                foreach (var parameter in parameterMap.EnumerateObject())
                {
                    if (parameter.Value.ValueKind != JsonValueKind.Object)
                        throw Invalid($"Parameters.{parameter.Name}", "an object");
                    parameters[parameter.Name] = new ParameterRange(
                        ReadNumber(parameter.Value, parameter.Name, "min"),
                        ReadNumber(parameter.Value, parameter.Name, "max"),
                        ReadNumber(parameter.Value, parameter.Name, "default"));
                }
            }

            return SerializeCapabilities(motions, expressions, parameters, hitAreas, groups);
        }
    }

    private static string SerializeCapabilities(
        SortedDictionary<string, List<string>> motions,
        List<string> expressions,
        SortedDictionary<string, ParameterRange> parameters,
        List<string> hitAreas,
        SortedDictionary<string, List<string>> groups)
    {
        var motionObject = new JsonObject();
        foreach (var (name, values) in motions) motionObject[name] = StringArray(values);

        var parameterObject = new JsonObject();
        foreach (var (name, range) in parameters)
        {
            parameterObject[name] = new JsonObject
            {
                ["min"] = range.Min,
                ["max"] = range.Max,
                ["default"] = range.Default
            };
        }

        var groupObject = new JsonObject();
        foreach (var (name, values) in groups) groupObject[name] = StringArray(values);

        return new JsonObject
        {
            ["motions"] = motionObject,
            ["expressions"] = StringArray(expressions),
            ["parameters"] = parameterObject,
            ["hit_areas"] = StringArray(hitAreas),
            ["groups"] = groupObject
        }.ToJsonString();
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static string MotionNameFromFile(string file)
    {
        var name = Path.GetFileName(file.Replace('\\', '/'));
        const string suffix = ".motion3.json";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double ReadNumber(JsonElement element, string parameter, string field)
    {
        if (!TryGet(element, field, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            throw Invalid($"Parameters.{parameter}.{field}", "a number");
        return number;
    }

    private static CompanionException Invalid(string field, string expected) =>
        new("model3_invalid_capability", $"{field} must be {expected}.", 400);

    private readonly record struct ParameterRange(double Min, double Max, double Default);
}
