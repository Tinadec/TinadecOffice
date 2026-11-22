using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Skills;

public sealed record ApmManifest(
    string? Name,
    string? Version,
    string? Description,
    IReadOnlyList<string> ApmDependencies,
    IReadOnlyList<ParsedMcpEntry> McpDependencies);

public sealed record ApmLockedPackage(
    string Key,
    string? RepoUrl,
    string? Commit,
    string? Version,
    string? ContentHash,
    string? InstallPath);

public sealed class ApmLockfile
{
    public static readonly ApmLockfile Empty = new([]);

    public IReadOnlyList<ApmLockedPackage> Packages { get; }

    public ApmLockfile(IReadOnlyList<ApmLockedPackage> packages)
    {
        Packages = packages ?? [];
    }

    public ApmLockedPackage? ResolveForModulePath(string relativeModulePath)
    {
        if (Packages.Count == 0 || string.IsNullOrWhiteSpace(relativeModulePath))
            return null;

        var normalized = relativeModulePath.Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("apm_modules/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["apm_modules/".Length..];

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var firstOne = segments[0];
        var firstTwo = segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : null;

        foreach (var pkg in Packages)
        {
            if (!string.IsNullOrWhiteSpace(pkg.InstallPath))
            {
                var inst = pkg.InstallPath!.Replace('\\', '/').Trim('/');
                if (inst.StartsWith("apm_modules/", StringComparison.OrdinalIgnoreCase))
                    inst = inst["apm_modules/".Length..];
                if (inst.Length > 0 && (string.Equals(normalized, inst, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(inst + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    return pkg;
                }
            }
        }

        if (firstTwo is not null)
        {
            var twoMatch = Packages.FirstOrDefault(p =>
                string.Equals(p.Key, firstTwo, StringComparison.OrdinalIgnoreCase)
                || p.Key.StartsWith(firstTwo + "#", StringComparison.OrdinalIgnoreCase)
                || p.Key.StartsWith(firstTwo + "/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ExtractOwnerRepo(p.RepoUrl), firstTwo, StringComparison.OrdinalIgnoreCase));
            if (twoMatch is not null)
                return twoMatch;
        }

        return Packages.FirstOrDefault(p =>
            string.Equals(p.Key, firstOne, StringComparison.OrdinalIgnoreCase)
            || string.Equals(LastSegment(p.Key), firstOne, StringComparison.OrdinalIgnoreCase)
            || string.Equals(LastSegment(ExtractOwnerRepo(p.RepoUrl)), firstOne, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractOwnerRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path[..^4];
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{parts[0]}/{parts[1]}";
        }
        return null;
    }

    private static string? LastSegment(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var clean = key.Split('#')[0].Trim('/');
        var idx = clean.LastIndexOf('/');
        return idx >= 0 ? clean[(idx + 1)..] : clean;
    }
}

public sealed record ApmMarkdownDocument(
    string? Name,
    string? Description,
    string? Role,
    string? Layer,
    string? Model,
    IReadOnlyList<string> Tools,
    string Body);

/// <summary>
/// Pure-file parser for Microsoft APM (<c>microsoft/apm</c>) workspace manifests (<c>apm.yml</c>),
/// lockfiles (<c>apm.lock.yaml</c>), <c>.apm/prompts/*.prompt.md</c>, and <c>.apm/agents/*.agent.md</c>.
/// Never invokes the <c>apm</c> CLI binary.
/// </summary>
public static partial class ApmPackageParser
{
    public static ApmManifest ParseManifest(string yaml)
    {
        var lines = TokenizeYaml(yaml);
        string? name = null;
        string? version = null;
        string? description = null;
        var apmDeps = new List<string>();

        if (lines.Count > 0)
        {
            var rootIndent = lines[0].Indent;
            for (var i = 0; i < lines.Count; i++)
            {
                var (ind, txt) = lines[i];
                if (ind != rootIndent || !TrySplitKeyValue(txt, out var key, out var val))
                    continue;

                switch (key.ToLowerInvariant())
                {
                    case "name":
                        name = Unquote(val);
                        break;
                    case "version":
                        version = Unquote(val);
                        break;
                    case "description":
                        description = Unquote(val);
                        break;
                    case "dependencies":
                        for (var j = i + 1; j < lines.Count && lines[j].Indent > ind; j++)
                        {
                            if (TrySplitKeyValue(lines[j].Text, out var depKey, out var depInline)
                                && string.Equals(depKey, "apm", StringComparison.OrdinalIgnoreCase))
                            {
                                var apmIndent = lines[j].Indent;
                                if (depInline.StartsWith('[') && depInline.EndsWith(']'))
                                {
                                    apmDeps.AddRange(ParseFlowArray(depInline));
                                }
                                else
                                {
                                    for (var k = j + 1; k < lines.Count; k++)
                                    {
                                        var (ci, ct) = lines[k];
                                        if (ci > apmIndent || (ci == apmIndent && IsSequenceItem(ct)))
                                        {
                                            if (IsSequenceItem(ct))
                                            {
                                                var rest = ct.Length == 1 ? string.Empty : ct[1..].Trim();
                                                if (rest.Length > 0 && !TrySplitKeyValue(rest, out _, out _))
                                                    apmDeps.Add(Unquote(rest));
                                                else if (TrySplitKeyValue(rest, out var rk, out var rv) && rk is "repo" or "git" or "name" or "url")
                                                    apmDeps.Add(Unquote(rv));
                                            }
                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
            }
        }

        var mcpEntries = McpConfigParser.ParseApmYaml(yaml);
        return new ApmManifest(name, version, description, apmDeps, mcpEntries);
    }

    public static ApmLockfile ParseLockfile(string yaml)
    {
        var lines = TokenizeYaml(yaml);
        if (lines.Count == 0)
            return ApmLockfile.Empty;

        var packages = new List<ApmLockedPackage>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (indent, text) = lines[i];
            if (!TrySplitKeyValue(text, out var key, out _))
                continue;

            if (key.ToLowerInvariant() is "dependencies" or "packages" or "locked_dependencies" or "locked" or "modules")
            {
                var block = new List<(int Indent, string Text)>();
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (lines[j].Indent > indent || (lines[j].Indent == indent && IsSequenceItem(lines[j].Text)))
                        block.Add(lines[j]);
                    else
                        break;
                }
                ParseLockBlock(block, packages);
            }
        }

        return new ApmLockfile(packages);
    }

    private static void ParseLockBlock(List<(int Indent, string Text)> block, List<ApmLockedPackage> output)
    {
        if (block.Count == 0) return;

        if (IsSequenceItem(block[0].Text))
        {
            var seqIndent = block[0].Indent;
            var idx = 0;
            while (idx < block.Count)
            {
                var (ind, txt) = block[idx];
                if (ind == seqIndent && IsSequenceItem(txt))
                {
                    var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var firstRest = txt.Length == 1 ? string.Empty : txt[1..].Trim();
                    if (firstRest.Length > 0 && TrySplitKeyValue(firstRest, out var fk, out var fv))
                        props[Unquote(fk)] = Unquote(fv);

                    idx++;
                    while (idx < block.Count && block[idx].Indent > seqIndent)
                    {
                        if (TrySplitKeyValue(block[idx].Text, out var k, out var v) && v.Length > 0)
                            props[Unquote(k)] = Unquote(v);
                        idx++;
                    }

                    if (BuildLockedPackage(null, props) is { } pkg)
                        output.Add(pkg);
                }
                else
                {
                    idx++;
                }
            }
        }
        else
        {
            var mapIndent = block[0].Indent;
            var idx = 0;
            while (idx < block.Count)
            {
                var (ind, txt) = block[idx];
                if (ind == mapIndent && TrySplitKeyValue(txt, out var pkgKey, out var inlineScalar))
                {
                    var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (inlineScalar.Length > 0)
                        props["commit"] = Unquote(inlineScalar);

                    idx++;
                    while (idx < block.Count && block[idx].Indent > mapIndent)
                    {
                        if (TrySplitKeyValue(block[idx].Text, out var k, out var v) && v.Length > 0)
                            props[Unquote(k)] = Unquote(v);
                        idx++;
                    }

                    if (BuildLockedPackage(Unquote(pkgKey), props) is { } pkg)
                        output.Add(pkg);
                }
                else
                {
                    idx++;
                }
            }
        }
    }

    private static ApmLockedPackage? BuildLockedPackage(string? fallbackKey, Dictionary<string, string> props)
    {
        var repoUrl = FirstNonEmpty(props, "repo_url", "url", "git", "repository");
        var key = FirstNonEmpty(props, "key", "name", "id", "repo", "package", "source")
            ?? fallbackKey
            ?? repoUrl;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var rawCommit = FirstNonEmpty(props, "resolved_commit", "commit", "commit_sha", "sha", "revision");
        if (string.IsNullOrWhiteSpace(rawCommit) && key.Contains('#', StringComparison.Ordinal))
        {
            var afterHash = key[(key.IndexOf('#') + 1)..].Trim();
            if (HexCommitRegex().IsMatch(afterHash))
                rawCommit = afterHash;
        }
        var commit = !string.IsNullOrWhiteSpace(rawCommit) && HexCommitRegex().IsMatch(rawCommit.Trim())
            ? rawCommit.Trim().ToLowerInvariant()
            : rawCommit?.Trim();

        var version = FirstNonEmpty(props, "version", "resolved_ref", "tag", "ref");
        var rawHash = FirstNonEmpty(props, "sha256", "content_hash", "package_hash", "integrity");
        var contentHash = NormalizeDigest(rawHash);
        var installPath = FirstNonEmpty(props, "install_path", "installed_path", "path", "virtual_path");

        return new ApmLockedPackage(key.Trim(), repoUrl, commit, version, contentHash, installPath);
    }

    private static string? NormalizeDigest(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return trimmed[7..].Trim().ToLowerInvariant();
        if (trimmed.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase))
        {
            var b64 = trimmed[7..].Trim();
            try
            {
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length == 32)
                    return Convert.ToHexStringLower(bytes);
            }
            catch (FormatException)
            {
                return b64.ToLowerInvariant();
            }
        }
        return HexSha256Regex().IsMatch(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    public static ApmMarkdownDocument ParseMarkdownDocument(string rawContent)
    {
        var normalized = (rawContent ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal) && normalized != "---")
        {
            return new ApmMarkdownDocument(null, null, null, null, null, [], normalized.Trim());
        }

        var closingIdx = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        int bodyStart;
        string frontmatter;
        if (closingIdx >= 0)
        {
            frontmatter = normalized[4..closingIdx];
            bodyStart = closingIdx + 5;
        }
        else if (normalized.EndsWith("\n---", StringComparison.Ordinal))
        {
            frontmatter = normalized[4..^4];
            bodyStart = normalized.Length;
        }
        else
        {
            return new ApmMarkdownDocument(null, null, null, null, null, [], normalized.Trim());
        }

        var body = bodyStart < normalized.Length ? normalized[bodyStart..].Trim() : string.Empty;
        var lines = TokenizeYaml(frontmatter);
        string? name = null;
        string? description = null;
        string? role = null;
        string? layer = null;
        string? model = null;
        var tools = new List<string>();

        for (var i = 0; i < lines.Count; i++)
        {
            var (ind, txt) = lines[i];
            if (!TrySplitKeyValue(txt, out var key, out var val))
                continue;

            switch (key.ToLowerInvariant())
            {
                case "name":
                case "title":
                case "display_name":
                    name ??= Unquote(val);
                    break;
                case "description":
                    description = Unquote(val);
                    break;
                case "role":
                    role = Unquote(val);
                    break;
                case "layer":
                    layer = Unquote(val);
                    break;
                case "model":
                    model = Unquote(val);
                    break;
                case "tools":
                case "allowed_tools":
                case "tool_scope":
                    if (val.StartsWith('[') && val.EndsWith(']'))
                    {
                        tools.AddRange(ParseFlowArray(val));
                    }
                    else
                    {
                        for (var j = i + 1; j < lines.Count; j++)
                        {
                            if (lines[j].Indent > ind || (lines[j].Indent == ind && IsSequenceItem(lines[j].Text)))
                            {
                                if (IsSequenceItem(lines[j].Text))
                                {
                                    var item = lines[j].Text.Length == 1 ? string.Empty : lines[j].Text[1..].Trim();
                                    var unquoted = Unquote(item);
                                    if (unquoted.Length > 0)
                                        tools.Add(unquoted);
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    break;
            }
        }

        return new ApmMarkdownDocument(name, description, role, layer, model, tools, body);
    }

    public static string StripPromptName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".prompt.md".Length];
        else if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".md".Length];
        return ExternalCliDiscoveryService.NormalizeCommandFileName(fileName);
    }

    public static string StripAgentSlug(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".agent.md".Length];
        else if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".md".Length];
        return ExternalCliDiscoveryService.NormalizeSkillName(fileName);
    }

    private static string? FirstNonEmpty(Dictionary<string, string> map, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    private static List<(int Indent, string Text)> TokenizeYaml(string yaml)
    {
        var result = new List<(int Indent, string Text)>();
        foreach (var rawLine in (yaml ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var uncommented = StripComment(rawLine).TrimEnd();
            if (uncommented.Trim().Length == 0) continue;
            var trimmed = uncommented.Trim();
            if (trimmed is "---" or "...") continue;

            var indent = 0;
            while (indent < uncommented.Length && (uncommented[indent] == ' ' || uncommented[indent] == '\t'))
                indent += uncommented[indent] == '\t' ? 2 : 1;
            result.Add((indent, trimmed));
        }
        return result;
    }

    private static bool IsSequenceItem(string text) =>
        text == "-" || text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("-\t", StringComparison.Ordinal);

    private static bool TrySplitKeyValue(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || IsSequenceItem(text))
            return false;

        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && inDouble && i + 1 < text.Length) { i++; continue; }
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ':' && !inSingle && !inDouble && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                key = text[..i].Trim();
                value = i + 1 < text.Length ? text[(i + 1)..].Trim() : string.Empty;
                return key.Length > 0;
            }
        }
        return false;
    }

    private static List<string> ParseFlowArray(string flow)
    {
        var list = new List<string>();
        var inner = flow.Trim()[1..^1];
        var inSingle = false;
        var inDouble = false;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\\' && inDouble && i + 1 < inner.Length) { i++; continue; }
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ',' && !inSingle && !inDouble)
            {
                var item = Unquote(inner[start..i]);
                if (item.Length > 0) list.Add(item);
                start = i + 1;
            }
        }
        var tail = Unquote(inner[start..]);
        if (tail.Length > 0) list.Add(tail);
        return list;
    }

    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && inDouble && i + 1 < line.Length) { i++; continue; }
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }
        return line;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            return trimmed[1..^1].Replace("''", "'");
        return trimmed;
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{7,64}$")]
    private static partial Regex HexCommitRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex HexSha256Regex();
}
