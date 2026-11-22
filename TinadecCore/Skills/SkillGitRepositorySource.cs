using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Skills;

/// <summary>Reads a public GitHub Git tree at an immutable commit through the guarded provider transport.</summary>
internal static partial class SkillGitRepositorySource
{
    internal const string Kind = "skill_git";
    private const int MaxFiles = 256;
    private const long MaxPackageBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Source(string Owner, string Repository, string Commit, string Prefix)
    {
        public string Api => $"https://api.github.com/repos/{Owner}/{Repository}/git";
        public string Homepage => $"https://github.com/{Owner}/{Repository}/tree/{Commit}";
    }
    private sealed record Entry(string Path, string Sha, string Mode, long Size, string Type);

    internal static bool ValidateLocation(string location, out string? error) => TrySource(location, out _, out error);

    internal static async Task<MarketListing> FetchAsync(IToolProvider provider, string workspaceRoot, string location, CancellationToken ct)
    {
        if (!TrySource(location, out var source, out var error)) return MarketListing.Failed(error!);
        var tree = await ReadTree(provider, workspaceRoot, source!, ct);
        if (tree.Error is not null) return MarketListing.Failed(tree.Error, tree.Blocked);
        var roots = SkillRoots(tree.Entries!, source!.Prefix);
        var entries = new List<MarketEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var refused = 0;
        foreach (var root in roots)
        {
            if (entries.Count >= SkillRepositorySource.MaxRows) return MarketListing.Complete(entries, refused, 1, true);
            var files = PackageEntries(tree.Entries!, root);
            if (ValidatePackage(files) is not null) { refused++; continue; }
            var skillFile = files.Single(x => RelativePath(root, x.Path) == "SKILL.md");
            var body = await ReadBlob(provider, workspaceRoot, source, skillFile, ct);
            if (body.Error is not null) return MarketListing.Failed(body.Error);
            string text;
            try { text = Utf8.GetString(body.Bytes!); }
            catch (DecoderFallbackException) { refused++; continue; }
            var name = root.Length == 0 ? ReadName(text) : root[(root.LastIndexOf('/') + 1)..];
            if (name is null || !WorkspaceSkillPolicy.ValidateName(name, out _) || !names.Add(name)) { refused++; continue; }
            if (!WorkspaceSkillPolicy.TryRead(text, name, "SKILL.md", out var skill, out _)) { refused++; continue; }
            var unicodeScan = UnicodeSafetyScanner.ScanText(text, "SKILL.md");
            var contentHash = UnicodeSafetyScanner.Sha256Hex(body.Bytes!);
            var detail = JsonSerializer.Serialize(new
            {
                homepage = source.Homepage + (root.Length == 0 ? "" : "/" + root),
                commit = source.Commit,
                content_hash = contentHash,
                has_hidden_unicode = unicodeScan.HasHiddenUnicode,
                security_warnings = unicodeScan.Warnings,
                install = new { name, package_path = root, commit = source.Commit, content_hash = contentHash },
            });
            entries.Add(new(name, source.Commit, MarketEntryKinds.Skill, name, skill!.Description, detail, ManifestHash(files)));
        }
        return MarketListing.Complete(entries, refused, 1, false);
    }

    /// <summary>Returns exact package bytes, including SKILL.md. Addresses and names come from the pinned tree, never blob URLs.</summary>
    internal static async Task<(Dictionary<string, string>? Files, string? Error)> ReadPackageAsync(
        IToolProvider provider, string workspaceRoot, string location, string extensionId, CancellationToken ct, string? expectedManifestHash = null)
    {
        if (!TrySource(location, out var source, out var error)) return (null, error);
        if (!WorkspaceSkillPolicy.ValidateName(extensionId, out var reason)) return (null, reason);
        var tree = await ReadTree(provider, workspaceRoot, source!, ct);
        if (tree.Error is not null) return (null, tree.Error);
        var roots = SkillRoots(tree.Entries!, source!.Prefix);
        var matches = roots.Where(x => x.Length == 0 || x[(x.LastIndexOf('/') + 1)..] == extensionId).ToArray();
        if (matches.Length != 1) return (null, "The pinned repository must contain exactly one package with the selected skill name.");
        var root = matches[0];
        var entries = PackageEntries(tree.Entries!, root);
        if (ValidatePackage(entries) is { } invalid) return (null, invalid);
        if (expectedManifestHash is not null && expectedManifestHash != ManifestHash(entries))
            return (null, "The repository package tree changed since the catalog was refreshed; refresh and preview again.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var blob = await ReadBlob(provider, workspaceRoot, source, entry, ct);
            if (blob.Error is not null) return (null, blob.Error);
            files.Add(RelativePath(root, entry.Path), Convert.ToBase64String(blob.Bytes!));
        }
        try
        {
            if (!WorkspaceSkillPolicy.TryRead(Utf8.GetString(Convert.FromBase64String(files["SKILL.md"])), extensionId, "SKILL.md", out _, out var validation))
                return (null, validation);
        }
        catch (DecoderFallbackException) { return (null, "SKILL.md must be UTF-8 text."); }
        return (files, null);
    }

    private static bool TrySource(string location, out Source? source, out string? error)
    {
        source = null;
        error = "Use a public https://github.com/owner/repository/tree/<40-character commit>/<optional directory> URL. Branches, credentials and private repositories are not supported.";
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var path = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped).Trim('/');
        var parts = path.Split('/');
        if (parts.Length < 4 || parts[2] != "tree" || !Segment().IsMatch(parts[0]) || !Segment().IsMatch(parts[1])
            || !Sha().IsMatch(parts[3]) || parts.Skip(4).Any(x => !SafePath(x))) return false;
        source = new(parts[0], parts[1], parts[3].ToLowerInvariant(), string.Join('/', parts.Skip(4)));
        error = null;
        return true;
    }

    private static async Task<(Entry[]? Entries, string? Error, bool Blocked)> ReadTree(IToolProvider provider, string root, Source source, CancellationToken ct)
    {
        var page = await MarketFetch.FetchPageAsync(provider, root, $"{source.Api}/trees/{source.Commit}?recursive=1", ct);
        if (page.Error is not null) return (null, page.Error, page.Blocked);
        try
        {
            using var json = JsonDocument.Parse(page.Body!);
            var value = json.RootElement;
            if (!value.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False)
                return (null, "The repository tree is truncated; no partial package can be installed.", false);
            if (!value.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array) return (null, "The repository returned no Git tree.", false);
            var entries = new List<Entry>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in tree.EnumerateArray())
            {
                var path = MarketFetch.Text(item, "path");
                var sha = MarketFetch.Text(item, "sha");
                var mode = MarketFetch.Text(item, "mode");
                var type = MarketFetch.Text(item, "type");
                if (path is null || !SafePath(path) || sha is null || !Sha().IsMatch(sha) || mode is null || type is null || !paths.Add(path))
                    return (null, "The repository contains an unsafe or ambiguous path.", false);
                var size = item.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var length) ? length : 0;
                entries.Add(new(path, sha.ToLowerInvariant(), mode, size, type));
            }
            return (entries.ToArray(), null, false);
        }
        catch (JsonException) { return (null, "The repository tree is not valid JSON.", false); }
    }

    private static string[] SkillRoots(Entry[] entries, string prefix)
    {
        var candidates = entries.Where(x => (x.Path == "SKILL.md" || x.Path.EndsWith("/SKILL.md", StringComparison.Ordinal)) && (prefix.Length == 0 || x.Path.StartsWith(prefix + "/", StringComparison.Ordinal)))
            .Select(x => x.Path == "SKILL.md" ? "" : x.Path[..^9]).OrderBy(x => x.Length).ThenBy(x => x, StringComparer.Ordinal).ToArray();
        // A package's own references/SKILL.md is data, not another installable package.
        return candidates.Where(x => !candidates.Any(parent => parent.Length < x.Length && (parent.Length == 0 || x.StartsWith(parent + "/", StringComparison.Ordinal)))).ToArray();
    }
    private static string RelativePath(string root, string path) => root.Length == 0 ? path : path[(root.Length + 1)..];
    private static string ManifestHash(Entry[] files) => CanonicalJson.Sha256Hex(JsonSerializer.SerializeToElement(
        files.OrderBy(x => x.Path, StringComparer.Ordinal).Select(x => new { x.Path, x.Sha, x.Mode, x.Size })));
    private static Entry[] PackageEntries(Entry[] tree, string root) => tree.Where(x => (root.Length == 0 || x.Path.StartsWith(root + "/", StringComparison.Ordinal)) && x.Type != "tree").ToArray();
    private static string? ValidatePackage(Entry[] entries)
    {
        if (entries.Length == 0 || entries.Length > MaxFiles || entries.Sum(x => x.Size) > MaxPackageBytes)
            return "Skill packages are limited to 256 files and 16 MiB.";
        if (entries.Any(x => x.Type != "blob" || x.Mode is not ("100644" or "100755") || x.Size < 0 || x.Size > 4 * 1024 * 1024))
            return "Skill packages cannot contain links, submodules or files larger than 4 MiB.";
        if (entries.Any(x => (x.Path == "SKILL.md" || x.Path.EndsWith("/SKILL.md", StringComparison.Ordinal)) && x.Size > WorkspaceSkillPolicy.MaxFileBytes))
            return "SKILL.md is limited to 256 KiB.";
        return null;
    }
    private static async Task<(byte[]? Bytes, string? Error)> ReadBlob(IToolProvider provider, string root, Source source, Entry entry, CancellationToken ct)
    {
        var page = await MarketFetch.FetchPageAsync(provider, root, $"{source.Api}/blobs/{entry.Sha}", ct);
        if (page.Error is not null) return (null, page.Error);
        try
        {
            using var json = JsonDocument.Parse(page.Body!);
            var value = json.RootElement;
            if (MarketFetch.Text(value, "encoding") != "base64" || MarketFetch.Text(value, "sha") != entry.Sha) return (null, "The repository blob does not match its pinned tree.");
            var bytes = Convert.FromBase64String(MarketFetch.Text(value, "content") ?? "");
            if (bytes.LongLength != entry.Size) return (null, "The repository blob has an unexpected length.");
            var header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0");
            var objectBytes = new byte[header.Length + bytes.Length];
            header.CopyTo(objectBytes, 0); bytes.CopyTo(objectBytes, header.Length);
            if (Convert.ToHexStringLower(SHA1.HashData(objectBytes)) != entry.Sha) return (null, "The repository blob failed its Git object hash check.");
            return (bytes, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return (null, "The repository returned an invalid base64 blob."); }
    }
    private static bool SafePath(string value) => value.Length > 0 && !value.Contains('\\') && !value.Contains(':') && !value.StartsWith('/')
        && value.Split('/').All(x => x.Length > 0 && x is not ("." or "..") && !x.Any(char.IsControl) && x.IndexOfAny(['<', '>', '|', '?', '*']) < 0
            && !x.EndsWith('.') && !x.EndsWith(' ') && !DeviceName().IsMatch(x));
    private static string? ReadName(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].TrimStart('\uFEFF').Trim() != "---") return null;
        return lines.Skip(1).TakeWhile(x => x.Trim() != "---").FirstOrDefault(x => x.StartsWith("name:", StringComparison.Ordinal))?[5..].Trim().Trim('\'', '"');
    }
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?$", RegexOptions.IgnoreCase)] private static partial Regex DeviceName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")] private static partial Regex Segment();
    [GeneratedRegex("^[0-9a-fA-F]{40}$")] private static partial Regex Sha();
}
