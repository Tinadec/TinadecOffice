using System.Security.Cryptography;
using System.Text;

namespace TinadecCore.Skills;

public sealed record UnicodeFinding(
    string Category,
    int CodePoint,
    int Line,
    int Column,
    string? FilePath = null)
{
    public string CodePointHex => $"U+{CodePoint:X4}";
}

public sealed record UnicodeScanResult(
    bool HasHiddenUnicode,
    int ZeroWidthCount,
    int BidiOverrideCount,
    int UnicodeTagCount,
    IReadOnlyList<UnicodeFinding> Findings,
    IReadOnlyList<string> Warnings)
{
    public static readonly UnicodeScanResult Clean = new(false, 0, 0, 0, [], []);
}

/// <summary>
/// Supply-chain Unicode smuggling scanner for Skill packages, GitHub fetches, Market proposals,
/// and External CLI / Microsoft APM artifacts.
/// Detects:
/// <list type="bullet">
///   <item>Zero-width characters (<c>U+200B..U+200F</c>, <c>U+2060</c>, and non-leading internal <c>U+FEFF</c>)</item>
///   <item>Bidirectional overrides and isolates (<c>U+202A..U+202E</c>, <c>U+2066..U+2069</c>)</item>
///   <item>Unicode Tag block characters (<c>U+E0000..U+E007F</c>)</item>
/// </list>
/// </summary>
public static class UnicodeSafetyScanner
{
    public const string CategoryZeroWidth = "zero_width";
    public const string CategoryBidiOverride = "bidi_override";
    public const string CategoryUnicodeTag = "unicode_tag";

    private const int MaxRecordedFindings = 32;
    private const int MaxScannedAssetBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static UnicodeScanResult ScanText(string? text, string? filePath = null)
    {
        if (string.IsNullOrEmpty(text))
            return UnicodeScanResult.Clean;

        var findings = new List<UnicodeFinding>();
        var zeroWidthCount = 0;
        var bidiCount = 0;
        var tagCount = 0;
        var line = 1;
        var column = 1;
        var scalarIndex = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp == '\n')
            {
                line++;
                column = 1;
                scalarIndex++;
                continue;
            }

            string? category = null;
            if ((cp >= 0x200B && cp <= 0x200F) || cp == 0x2060 || (cp == 0xFEFF && scalarIndex > 0))
            {
                category = CategoryZeroWidth;
                zeroWidthCount++;
            }
            else if ((cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2066 && cp <= 0x2069))
            {
                category = CategoryBidiOverride;
                bidiCount++;
            }
            else if (cp >= 0xE0000 && cp <= 0xE007F)
            {
                category = CategoryUnicodeTag;
                tagCount++;
            }

            if (category is not null && findings.Count < MaxRecordedFindings)
            {
                findings.Add(new UnicodeFinding(category, cp, line, column, filePath));
            }

            column++;
            scalarIndex++;
        }

        var hasHidden = zeroWidthCount > 0 || bidiCount > 0 || tagCount > 0;
        if (!hasHidden)
            return UnicodeScanResult.Clean;

        var warning = FormatWarning(filePath, zeroWidthCount, bidiCount, tagCount, findings);
        return new UnicodeScanResult(true, zeroWidthCount, bidiCount, tagCount, findings, [warning]);
    }

    public static UnicodeScanResult ScanPackage(IReadOnlyDictionary<string, byte[]> files)
    {
        if (files is null || files.Count == 0)
            return UnicodeScanResult.Clean;

        var allFindings = new List<UnicodeFinding>();
        var warnings = new List<string>();
        var totalZw = 0;
        var totalBidi = 0;
        var totalTag = 0;

        foreach (var (path, bytes) in files.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxScannedAssetBytes)
                continue;
            if (Array.IndexOf(bytes, (byte)0) >= 0)
                continue;

            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            var res = ScanText(decoded, path);
            if (!res.HasHiddenUnicode)
                continue;

            totalZw += res.ZeroWidthCount;
            totalBidi += res.BidiOverrideCount;
            totalTag += res.UnicodeTagCount;
            foreach (var f in res.Findings)
            {
                if (allFindings.Count < MaxRecordedFindings)
                    allFindings.Add(f);
            }
            warnings.AddRange(res.Warnings);
        }

        return totalZw > 0 || totalBidi > 0 || totalTag > 0
            ? new UnicodeScanResult(true, totalZw, totalBidi, totalTag, allFindings, warnings)
            : UnicodeScanResult.Clean;
    }

    public static UnicodeScanResult ScanBase64Package(
        string? skillMarkdown,
        IReadOnlyDictionary<string, string>? base64Assets)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (skillMarkdown is not null)
            files["SKILL.md"] = Encoding.UTF8.GetBytes(skillMarkdown);

        if (base64Assets is not null)
        {
            foreach (var (path, encoded) in base64Assets)
            {
                if (string.Equals(path, "SKILL.md", StringComparison.OrdinalIgnoreCase) && files.ContainsKey("SKILL.md"))
                    continue;
                try
                {
                    files[path] = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    // Skip malformed base64 entries during advisory scan; writer validation rejects them separately.
                }
            }
        }

        return ScanPackage(files);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(string? content) =>
        Sha256Hex(Encoding.UTF8.GetBytes(content ?? string.Empty));

    public static string ComputePackageHash(
        string skillMarkdown,
        IReadOnlyDictionary<string, string>? base64Assets)
    {
        var sorted = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["SKILL.md"] = Encoding.UTF8.GetBytes(skillMarkdown ?? string.Empty),
        };

        if (base64Assets is not null)
        {
            foreach (var (path, encoded) in base64Assets)
            {
                if (string.Equals(path, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                    continue;
                sorted[path] = Convert.FromBase64String(encoded);
            }
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, bytes) in sorted)
        {
            digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + bytes.Length + "\0"));
            digest.AppendData(bytes);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static string FormatWarning(
        string? filePath,
        int zeroWidthCount,
        int bidiCount,
        int tagCount,
        IReadOnlyList<UnicodeFinding> findings)
    {
        var parts = new List<string>(3);
        if (bidiCount > 0)
        {
            var sample = findings.FirstOrDefault(f => f.Category == CategoryBidiOverride);
            parts.Add(sample is null
                ? $"{bidiCount} bidi override/isolate char(s)"
                : $"{bidiCount} bidi override/isolate char(s) (e.g. {sample.CodePointHex} at line {sample.Line}:{sample.Column})");
        }
        if (tagCount > 0)
        {
            var sample = findings.FirstOrDefault(f => f.Category == CategoryUnicodeTag);
            parts.Add(sample is null
                ? $"{tagCount} Unicode Tag char(s)"
                : $"{tagCount} Unicode Tag char(s) (e.g. {sample.CodePointHex} at line {sample.Line}:{sample.Column})");
        }
        if (zeroWidthCount > 0)
        {
            var sample = findings.FirstOrDefault(f => f.Category == CategoryZeroWidth);
            parts.Add(sample is null
                ? $"{zeroWidthCount} zero-width char(s)"
                : $"{zeroWidthCount} zero-width char(s) (e.g. {sample.CodePointHex} at line {sample.Line}:{sample.Column})");
        }

        var target = string.IsNullOrWhiteSpace(filePath) ? "content" : filePath;
        return $"Hidden Unicode detected in {target}: {string.Join(", ", parts)}. Review raw bytes for prompt smuggling before trusting this artifact.";
    }
}
