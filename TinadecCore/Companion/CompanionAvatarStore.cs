using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Companion;

/// <summary>
/// Bring-your-own Live2D model registry. Core never ships Cubism Core or a model; it only copies
/// what the user selected into its own storage root and records what model3.json declares. The
/// copy is what makes the row durable: the user may move or delete the original directory, and a
/// later renderer still has bytes to load.
/// </summary>
public sealed class CompanionAvatarStore(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ITenantContextAccessor tenant,
    StoragePaths paths)
{
    private const string AvatarDirectory = "companion-avatars";
    // A Live2D model is a model3.json, a .moc3 and a texture atlas — a few hundred files at most.
    // The ceilings exist so a mistakenly selected folder fails loudly instead of filling the disk.
    private const int MaxAvatarFiles = 2048;
    private const long MaxAvatarBytes = 256L * 1024 * 1024;

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
            throw new CompanionException("model3_missing", "The model directory does not contain model3.json.", 400);
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

    private static object ToJson(CompanionAvatarRecord row) => new
    {
        id = row.Id,
        name = row.Name,
        kind = row.Kind,
        source_path = row.SourcePath,
        capabilities = JsonNode.Parse(row.CapabilitiesJson) ?? new JsonObject(),
        created_at = row.CreatedAt
    };

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
