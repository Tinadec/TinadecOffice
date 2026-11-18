using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Skills;

/// <summary>One file inside an embedded built-in skill package.</summary>
public sealed record BuiltinSkillFile(string Path, byte[] Bytes, string ContentHash);

/// <summary>
/// A skill shipped inside the Skills assembly. Built-ins never touch the user's storage: they are
/// served from embedded resources, appear at the lowest precedence, and are read-only. A user who
/// wants to change one copies it into the project's canonical skills root.
/// </summary>
public sealed record BuiltinSkillPackage(
    string Name,
    string Description,
    string Version,
    string Content,
    IReadOnlyList<BuiltinSkillFile> Files,
    string ContentHash,
    Guid ResourceId,
    long Revision);

/// <summary>
/// Single loader for the skills embedded beside this assembly. The MSBuild resource names keep the
/// package layout (<c>builtin-skills/&lt;name&gt;/references/x.md</c>), so the body and its resources
/// are read from the same package identity the settings surface shows.
/// </summary>
public static class BuiltinSkillCatalog
{
    public const string ResourcePrefix = "builtin-skills/";
    private const string CatalogFile = "builtin-skills.json";

    private static readonly Lazy<State> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Version => Loaded.Value.Version;

    public static IReadOnlyList<BuiltinSkillPackage> All => Loaded.Value.Skills;

    public static IReadOnlyList<string> Names => Loaded.Value.Skills.Select(x => x.Name).ToArray();

    public static BuiltinSkillPackage? Find(string name) =>
        Loaded.Value.ByName.TryGetValue(name, out var skill) ? skill : null;

    public static BuiltinSkillPackage? Find(Guid resourceId) =>
        Loaded.Value.ById.TryGetValue(resourceId, out var skill) ? skill : null;

    private sealed record State(string Version, IReadOnlyList<BuiltinSkillPackage> Skills,
        IReadOnlyDictionary<string, BuiltinSkillPackage> ByName, IReadOnlyDictionary<Guid, BuiltinSkillPackage> ById);

    private static State Load()
    {
        var assembly = typeof(BuiltinSkillCatalog).Assembly;
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            var relative = resource[ResourcePrefix.Length..].Replace('\\', '/');
            if (relative.Length == 0) continue;
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            resources[relative] = memory.ToArray();
        }

        var version = "0";
        if (resources.TryGetValue(CatalogFile, out var catalogBytes))
        {
            try
            {
                using var document = JsonDocument.Parse(catalogBytes);
                if (document.RootElement.TryGetProperty("catalog_version", out var value) && value.ValueKind == JsonValueKind.String)
                    version = value.GetString() ?? version;
            }
            catch (JsonException) { /* a malformed version file must not hide the skills */ }
        }

        var packages = new List<BuiltinSkillPackage>();
        foreach (var group in resources.Where(x => x.Key != CatalogFile)
                     .GroupBy(x => x.Key.Split('/')[0], StringComparer.Ordinal))
        {
            var name = group.Key;
            if (!group.Any(x => string.Equals(x.Key, name + "/SKILL.md", StringComparison.Ordinal))) continue;
            var contentBytes = group.First(x => string.Equals(x.Key, name + "/SKILL.md", StringComparison.Ordinal)).Value;
            string content;
            try { content = new UTF8Encoding(false, true).GetString(contentBytes); }
            catch (DecoderFallbackException) { continue; }
            var problem = WorkspaceSkillPolicy.Check(content, name, $"tinadec://builtin/{name}/SKILL.md", out var skill);
            if (problem is not null || skill is null) continue;

            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (path, bytes) in group)
                files[path[name.Length..].TrimStart('/')] = bytes;
            var hash = ToolSkillResourceService.PackageDigest(files);
            var resourceId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("tinadec.builtin:" + name)).AsSpan(0, 16));
            var fileDtos = files.Select(x => new BuiltinSkillFile(x.Key, x.Value,
                Convert.ToHexStringLower(SHA256.HashData(x.Value)))).ToArray();
            packages.Add(new BuiltinSkillPackage(name, skill.Description, ReadVersion(content), content, fileDtos,
                hash, resourceId, Revision(hash)));
        }

        var ordered = packages.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        return new State(version, ordered,
            ordered.ToDictionary(x => x.Name, StringComparer.Ordinal),
            ordered.ToDictionary(x => x.ResourceId));
    }

    private static string ReadVersion(string content)
    {
        var end = content.IndexOf("\n---", StringComparison.Ordinal);
        if (!content.StartsWith("---", StringComparison.Ordinal) || end < 0) return "1.0.0";
        foreach (var line in content[3..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            if (!line[..separator].Trim().Equals("version", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0) return value[..Math.Min(value.Length, 64)];
        }
        return "1.0.0";
    }

    private static long Revision(string hash) => hash.Length == 0
        ? 0
        : BinaryPrimitives.ReadInt64BigEndian(Convert.FromHexString(hash)[..8]) & ((1L << 53) - 1);
}
