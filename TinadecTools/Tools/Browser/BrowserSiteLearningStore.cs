using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TinadecTools.Runtime;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tools.Browser;

public sealed record BrowserSiteLearningEntry(
    string Domain,
    string SkillName,
    string Description,
    string RelativePath,
    string FullPath,
    string Content);

/// <summary>
/// Governed store for reusable per-domain browser navigation &amp; stable-locator notes under
/// <c>.tinadec/skills/browser-learnings/&lt;domain&gt;/SKILL.md</c>.
///
/// Follows both Tinadec's <c>WorkspaceSkillPolicy</c> (depth-2 skill directory under
/// <c>.tinadec/skills/</c>, lowercase alphanumeric + single-hyphen <c>name</c> matching its
/// directory) and ego-lite's site-learning invariant (never persist temporary <c>@N</c> or
/// <c>ref=N</c> snapshot refs; persist only verified stable locators and procedural notes).
/// </summary>
public static class BrowserSiteLearningStore
{
    public const int MaxSkillBytes = 256 * 1024;
    public const int MaxSkillNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const string RelativeLearningsDir = ".tinadec/skills/browser-learnings";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly Regex TempRefPattern = new(
        @"(?:(?<![A-Za-z0-9._%+-])@\d+\b|\bref=\d+\b|\[ref=\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex EmbeddedCredentialsPattern = new(
        @"https?://[^/\s:@]+:[^/\s@]+@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex FrontmatterPattern = new(
        @"\A\uFEFF?^---\s*$(.+?)^---\s*$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static bool TryNormalizeDomain(string? urlOrDomain, out string domain, out string error)
    {
        domain = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(urlOrDomain))
        {
            error = "A domain or http(s) URL is required.";
            return false;
        }

        var raw = urlOrDomain.Trim();
        if (raw.Contains("..", StringComparison.Ordinal) || raw.Contains('\\'))
        {
            error = "Invalid domain or URL.";
            return false;
        }

        Uri? parsed;
        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                error = "Only http and https URLs or bare hostnames are supported.";
                return false;
            }
            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                error = "URLs with embedded credentials are not allowed.";
                return false;
            }
        }
        else
        {
            if (raw.Contains('/') || raw.Contains('@') || raw.Contains('*'))
            {
                error = "Bare domain must not contain paths, wildcards, or credentials.";
                return false;
            }
            if (!Uri.TryCreate("https://" + raw, UriKind.Absolute, out parsed))
            {
                error = "Invalid domain hostname.";
                return false;
            }
        }

        var host = parsed.DnsSafeHost.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host) || host.Length > 253)
        {
            error = "Invalid domain hostname.";
            return false;
        }

        foreach (var ch in host)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not ('.' or '-' or ':'))
            {
                error = "Domain contains unsupported characters.";
                return false;
            }
        }

        domain = host;
        return true;
    }

    /// <summary>
    /// Converts a normalized hostname (e.g. <c>app.example.com</c>) into a valid
    /// <c>WorkspaceSkillPolicy</c> directory/skill name (<c>app-example-com</c>).
    /// </summary>
    public static string ToSkillDirectoryName(string domain)
    {
        var builder = new StringBuilder(domain.Length);
        var prevHyphen = false;
        foreach (var ch in domain.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
                prevHyphen = false;
            }
            else if (builder.Length > 0 && !prevHyphen)
            {
                builder.Append('-');
                prevHyphen = true;
            }
        }

        while (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        var slug = builder.Length == 0 ? "site" : builder.ToString();
        if (slug.Length <= MaxSkillNameLength) return slug;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(domain)))[..8].ToLowerInvariant();
        var prefix = slug[..(MaxSkillNameLength - 9)].TrimEnd('-');
        return $"{prefix}-{hash}";
    }

    public static bool ValidateLearningContent(string? markdownBody, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(markdownBody))
        {
            error = "Site learning body must not be empty.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(markdownBody) > MaxSkillBytes - 2048)
        {
            error = $"Site learning exceeds maximum size ({MaxSkillBytes} bytes).";
            return false;
        }

        if (TempRefPattern.IsMatch(markdownBody))
        {
            error = "Site learnings must not contain temporary snapshot refs (@N or ref=N); use verified stable locators (loc=role:... or loc=css:...) instead.";
            return false;
        }

        if (EmbeddedCredentialsPattern.IsMatch(markdownBody))
        {
            error = "Site learnings must not contain embedded URL credentials.";
            return false;
        }

        return true;
    }

    public static BrowserSiteLearningEntry Save(
        string urlOrDomain,
        string? description,
        string markdownBody,
        string? workspaceRootOverride = null)
    {
        if (!TryNormalizeDomain(urlOrDomain, out var domain, out var domainError))
            throw new InvalidOperationException(domainError);

        if (!ValidateLearningContent(markdownBody, out var contentError))
            throw new InvalidOperationException(contentError);

        var skillName = ToSkillDirectoryName(domain);
        var cleanDescription = NormalizeDescription(description, domain);
        var content = RenderSkillMarkdown(skillName, domain, cleanDescription, markdownBody);
        var bytes = Utf8NoBom.GetBytes(content);
        if (bytes.Length > MaxSkillBytes)
            throw new InvalidOperationException($"Rendered SKILL.md exceeds {MaxSkillBytes} bytes.");

        ToolExecutionContext.CheckFileSize("write", bytes.Length);

        var (root, fullPath, relativePath) = ResolveLearningSkillPath(skillName, writable: true, workspaceRootOverride);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        EnsureNoLinkTraversal(root, fullPath);

        var tempPath = Path.Combine(dir, $".SKILL.md.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        return new BrowserSiteLearningEntry(domain, skillName, cleanDescription, relativePath, fullPath, content);
    }

    public static bool TryLoad(
        string? urlOrDomain,
        out BrowserSiteLearningEntry? entry,
        string? workspaceRootOverride = null)
    {
        entry = null;
        if (!TryNormalizeDomain(urlOrDomain, out var domain, out _)) return false;

        var skillName = ToSkillDirectoryName(domain);
        var candidates = string.Equals(skillName, domain, StringComparison.Ordinal)
            ? new[] { skillName }
            : new[] { skillName, domain };

        foreach (var candidateDir in candidates)
        {
            try
            {
                var (root, fullPath, relativePath) = ResolveLearningSkillPath(candidateDir, writable: false, workspaceRootOverride);
                if (!File.Exists(fullPath)) continue;
                EnsureNoLinkTraversal(root, fullPath);

                var info = new FileInfo(fullPath);
                if (info.Length > MaxSkillBytes) continue;
                ToolExecutionContext.CheckFileSize("read", info.Length);

                var content = File.ReadAllText(fullPath, Encoding.UTF8);
                var (parsedName, parsedDescription, parsedDomain) = ParseFrontmatter(content, candidateDir, domain);
                entry = new BrowserSiteLearningEntry(
                    parsedDomain,
                    parsedName,
                    parsedDescription,
                    relativePath,
                    fullPath,
                    content);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }

        return false;
    }

    public static IReadOnlyList<BrowserSiteLearningEntry> List(string? workspaceRootOverride = null)
    {
        var results = new List<BrowserSiteLearningEntry>();
        string root;
        string learningsDir;
        try
        {
            root = EffectiveWorkspaceRoot(workspaceRootOverride);
            var candidate = Path.Combine(root, ".tinadec", "skills", "browser-learnings");
            learningsDir = workspaceRootOverride is null
                ? WorkspacePathResolver.ResolvePath(candidate, allowFinalLink: false, writable: false)
                : GovernedResolve(root, candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        if (!Directory.Exists(learningsDir)) return results;

        foreach (var dir in Directory.GetDirectories(learningsDir).Order(StringComparer.Ordinal))
        {
            var dirName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.')) continue;
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            try
            {
                EnsureNoLinkTraversal(root, skillFile);
                var info = new FileInfo(skillFile);
                if (info.Length > MaxSkillBytes) continue;
                var content = File.ReadAllText(skillFile, Encoding.UTF8);
                var (parsedName, parsedDescription, parsedDomain) = ParseFrontmatter(content, dirName, dirName);
                var relativePath = $"{RelativeLearningsDir}/{dirName}/SKILL.md";
                results.Add(new BrowserSiteLearningEntry(
                    parsedDomain,
                    parsedName,
                    parsedDescription,
                    relativePath,
                    skillFile,
                    content));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable or symlinked entries.
            }
        }

        return results;
    }

    private static string NormalizeDescription(string? description, string domain)
    {
        var raw = string.IsNullOrWhiteSpace(description)
            ? $"Verified browser navigation and stable locator notes for {domain}."
            : description.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (raw.Length > MaxDescriptionLength) raw = raw[..MaxDescriptionLength].TrimEnd();
        return raw;
    }

    private static string RenderSkillMarkdown(string skillName, string domain, string description, string body)
    {
        var escapedDescription = description.Replace("\"", "'");
        var normalizedBody = body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return $"---\nname: {skillName}\ndescription: \"{escapedDescription}\"\ndomain: {domain}\n---\n\n{normalizedBody}\n";
    }

    private static (string Name, string Description, string Domain) ParseFrontmatter(
        string content,
        string fallbackName,
        string fallbackDomain)
    {
        var match = FrontmatterPattern.Match(content);
        if (!match.Success) return (fallbackName, string.Empty, fallbackDomain);

        string? name = null;
        string? description = null;
        string? domain = null;
        foreach (var line in match.Groups[1].Value.Split('\n'))
        {
            var sep = line.IndexOf(':');
            if (sep <= 0) continue;
            var key = line[..sep].Trim();
            var val = line[(sep + 1)..].Trim().Trim('"', '\'');
            if (val.Length == 0) continue;
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name ??= val;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description ??= val;
            else if (key.Equals("domain", StringComparison.OrdinalIgnoreCase)) domain ??= val;
        }

        return (name ?? fallbackName, description ?? string.Empty, domain ?? fallbackDomain);
    }

    private static (string Root, string FullPath, string RelativePath) ResolveLearningSkillPath(
        string directoryName,
        bool writable,
        string? workspaceRootOverride)
    {
        if (string.IsNullOrWhiteSpace(directoryName)
            || directoryName is "." or ".."
            || directoryName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new UnauthorizedAccessException("Invalid site learning directory name.");
        }

        var root = EffectiveWorkspaceRoot(workspaceRootOverride);
        var combined = Path.Combine(root, ".tinadec", "skills", "browser-learnings", directoryName, "SKILL.md");
        var resolved = workspaceRootOverride is null
            ? WorkspacePathResolver.ResolvePath(combined, allowFinalLink: false, writable: writable)
            : GovernedResolve(root, combined);
        var relative = $"{RelativeLearningsDir}/{directoryName}/SKILL.md";
        return (root, resolved, relative);
    }

    private static string EffectiveWorkspaceRoot(string? workspaceRootOverride) =>
        workspaceRootOverride is { Length: > 0 }
            ? WorkspaceRootSet.Normalize(workspaceRootOverride)
            : WorkspacePathResolver.WorkspaceRoot;

    private static string GovernedResolve(string normalizedRoot, string candidatePath)
    {
        var resolved = WorkspaceRootSet.Normalize(candidatePath);
        if (!WorkspaceRootSet.IsWithin(normalizedRoot, resolved))
        {
            throw new UnauthorizedAccessException($"Path '{candidatePath}' resolves outside workspace root '{normalizedRoot}'.");
        }
        EnsureNoLinkTraversal(normalizedRoot, resolved);
        return resolved;
    }

    private static void EnsureNoLinkTraversal(string normalizedRoot, string targetPath)
    {
        var relative = Path.GetRelativePath(normalizedRoot, targetPath);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal)) return;

        var current = normalizedRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!WorkspacePathResolver.Exists(current)) break;
            if (WorkspacePathResolver.IsLink(current))
            {
                throw new UnauthorizedAccessException("Paths through symbolic links or junctions are not allowed.");
            }
        }
    }
}
