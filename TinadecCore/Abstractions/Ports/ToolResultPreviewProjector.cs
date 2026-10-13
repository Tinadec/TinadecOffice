using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// What a finished tool call returned, cut down to something a conversation feed can
/// show beside the call: a few file lines, the first search matches, the tail of a
/// command's output.
/// <para>
/// The full result never leaves Core. Every preview is bounded twice — per kind (lines,
/// entries, characters) and by one byte budget for the whole response — and goes through
/// the same secret-key table as <see cref="ApprovalEvidenceProjector"/>. Results that come
/// from a credential-shaped path are withheld rather than trimmed. The arguments shown are
/// the approval evidence projection, never the stored parameters.
/// </para>
/// <para>Pure and never throws: anything it cannot read degrades to <c>withheld</c>.</para>
/// </summary>
public static class ToolResultPreviewProjector
{
    public const int DefaultMaxBytes = 16 * 1024;
    public const int MinimumMaxBytes = 1024;
    public const int MaximumMaxBytes = 64 * 1024;

    private const int MaxFileLines = 60;
    private const int MaxLineChars = 400;
    private const int MaxSearchLines = 40;
    private const int MaxDirectoryEntries = 80;
    private const int MaxStreamBytes = 8 * 1024;
    private const int MaxTextBytes = 8 * 1024;
    private const int MaxJsonDepth = 6;
    private const int MaxJsonArrayItems = 20;
    private const int MaxJsonStringChars = 400;

    /// <summary>Clamps a caller's <c>max_bytes</c> into 1–64 KiB; null means the 16 KiB default.</summary>
    public static int ClampMaxBytes(int? requested) =>
        requested is null ? DefaultMaxBytes : Math.Clamp(requested.Value, MinimumMaxBytes, MaximumMaxBytes);

    /// <summary>Statuses after which an execution's stored result can no longer change.</summary>
    public static bool IsTerminal(string? status) =>
        status is "completed" or "failed" or "timed_out" or "cancelled" or "outcome_unknown";

    public static ToolExecutionPreviewDto Project(ToolExecutionSnapshot execution, int maxBytes)
    {
        var evidence = ApprovalEvidenceProjector.Project(execution.ToolId, execution.ParametersJson);
        var budget = new Budget(ClampMaxBytes(maxBytes));
        var preview = IsTerminal(execution.Status)
            ? Preview(execution.ToolId, evidence.ResourcePath, execution.ResultJson, budget)
            : null;

        return new ToolExecutionPreviewDto
        {
            ExecutionId = execution.Id.ToString(),
            RunId = execution.RunId.ToString(),
            TaskId = execution.TaskId == Guid.Empty ? null : execution.TaskId.ToString(),
            ToolId = execution.ToolId,
            Status = execution.Status,
            ToolSuccess = execution.ToolSuccess,
            ErrorCategory = execution.ErrorCategory,
            SafeErrorMessage = execution.SafeErrorMessage,
            Arguments = ParseArguments(evidence.Arguments),
            ResultLength = execution.ResultJson?.Length,
            Preview = preview,
            Truncated = budget.Truncated,
        };
    }

    /// <summary>
    /// The preview of one stored result, or null when there is nothing stored. Exposed for
    /// tests; the endpoint goes through <see cref="Project"/>.
    /// </summary>
    internal static ToolResultPreviewDto? Preview(string toolId, string? resourcePath, string? resultJson, Budget budget)
    {
        if (string.IsNullOrWhiteSpace(resultJson) || resultJson.Trim() == "null") return null;
        if (IsSensitivePath(resourcePath)) return Withheld("sensitive_path");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            return Withheld("unparsed_result");
        }

        using (document)
        {
            var root = document.RootElement;
            // A tool that reports its own failure is shown as what it said, not as an empty listing.
            var reportedFailure = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False;

            try
            {
                var typed = reportedFailure && toolId is not ("shell" or "command_run")
                    ? null
                    : toolId switch
                    {
                        "read_file" => FileLines(root, resourcePath, budget),
                        "file_search" => SearchMatches(root, budget),
                        "ls" => Directory(root, resourcePath, budget),
                        "shell" or "command_run" => CommandOutput(root, budget),
                        "git_diff" => Diff(root, budget),
                        _ => null,
                    };
                return typed ?? Json(root, budget);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
            {
                // A result whose members hold an unexpected JSON kind is the tool's
                // business, not a reason for the preview route to fail.
                return Withheld("unreadable_result");
            }
        }
    }

    private static ToolResultPreviewDto? FileLines(JsonElement root, string? path, Budget budget)
    {
        if (!TryArray(root, "all_contents", out var contents)) return null;

        var lines = new List<ToolPreviewLineDto>();
        var total = 0;
        foreach (var item in contents.EnumerateArray())
        {
            total++;
            if (lines.Count >= MaxFileLines || budget.Exhausted)
            {
                budget.MarkTruncated();
                continue;
            }

            var line = item.ValueKind == JsonValueKind.Object && TryMember(item, "content", out var inner) ? inner : item;
            lines.Add(new ToolPreviewLineDto
            {
                LineNumber = LineNumber(line),
                Text = budget.Take(LineString(line), MaxLineChars),
            });
        }

        return new ToolResultPreviewDto { Kind = "file_lines", Path = path, Lines = lines, TotalCount = total };
    }

    private static ToolResultPreviewDto? SearchMatches(JsonElement root, Budget budget)
    {
        if (!TryArray(root, "lines", out var matches)) return null;

        var lines = new List<ToolPreviewLineDto>();
        foreach (var item in matches.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var path = String(item, "relative_path") ?? String(item, "filepath");
            // A match inside a credential file says too much even as one line.
            if (IsSensitivePath(path))
            {
                budget.MarkTruncated();
                continue;
            }
            if (lines.Count >= MaxSearchLines || budget.Exhausted)
            {
                budget.MarkTruncated();
                continue;
            }

            lines.Add(new ToolPreviewLineDto
            {
                Path = path is null ? null : budget.Take(path, MaxLineChars),
                LineNumber = Int(item, "line_number"),
                Text = budget.Take(String(item, "content") ?? string.Empty, MaxLineChars),
                IsMatch = Bool(item, "is_match"),
            });
        }

        if (Bool(root, "truncated") == true) budget.MarkTruncated();
        return new ToolResultPreviewDto
        {
            Kind = "search_matches",
            Lines = lines,
            TotalCount = Long(root, "total_match_count"),
        };
    }

    private static ToolResultPreviewDto? Directory(JsonElement root, string? path, Budget budget)
    {
        if (!TryArray(root, "entries", out var items)) return null;

        var entries = new List<ToolPreviewEntryDto>();
        var total = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            total++;
            if (entries.Count >= MaxDirectoryEntries || budget.Exhausted)
            {
                budget.MarkTruncated();
                continue;
            }

            entries.Add(new ToolPreviewEntryDto
            {
                Name = budget.Take(String(item, "name") ?? string.Empty, MaxLineChars),
                Type = String(item, "type") ?? string.Empty,
                Size = Long(item, "size"),
            });
        }

        if (Bool(root, "has_more") == true) budget.MarkTruncated();
        return new ToolResultPreviewDto { Kind = "directory", Path = path, Entries = entries, TotalCount = total };
    }

    private static ToolResultPreviewDto CommandOutput(JsonElement root, Budget budget)
    {
        if (Bool(root, "stdout_truncated") == true || Bool(root, "stderr_truncated") == true) budget.MarkTruncated();
        // Output that matters is at the end (the failing test, the final error), so keep the tail.
        var stdout = budget.TakeTail(String(root, "stdout") ?? string.Empty, MaxStreamBytes);
        var stderr = budget.TakeTail(String(root, "stderr") ?? string.Empty, MaxStreamBytes);
        return new ToolResultPreviewDto
        {
            Kind = "command_output",
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = Int(root, "exit_code"),
            TimedOut = Bool(root, "timed_out"),
        };
    }

    private static ToolResultPreviewDto? Diff(JsonElement root, Budget budget)
    {
        if (!TryArray(root, "sections", out var sections)) return null;

        var text = new StringBuilder();
        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object) continue;
            if (Bool(section, "truncated") == true) budget.MarkTruncated();
            var diff = String(section, "diff");
            if (string.IsNullOrEmpty(diff)) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(diff);
        }

        var full = text.ToString();
        if (TouchesSensitivePath(full)) return Withheld("sensitive_path");
        if (Bool(root, "truncated") == true) budget.MarkTruncated();
        return new ToolResultPreviewDto { Kind = "text", Language = "diff", Text = budget.TakeHead(full, MaxTextBytes) };
    }

    private static ToolResultPreviewDto Json(JsonElement root, Budget budget) =>
        new() { Kind = "json", Value = BoundedNode(root, 0, budget) };

    private static JsonNode? BoundedNode(JsonElement value, int depth, Budget budget)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= MaxJsonDepth)
                {
                    budget.MarkTruncated();
                    return JsonValue.Create("{…}");
                }
                var item = new JsonObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (ApprovalEvidenceProjector.IsSecretKey(property.Name)) continue;
                    if (budget.Exhausted)
                    {
                        budget.MarkTruncated();
                        break;
                    }
                    budget.Spend(property.Name);
                    item[property.Name] = BoundedNode(property.Value, depth + 1, budget);
                }
                return item;
            case JsonValueKind.Array:
                if (depth >= MaxJsonDepth)
                {
                    budget.MarkTruncated();
                    return JsonValue.Create("[…]");
                }
                var items = new JsonArray();
                var count = 0;
                foreach (var child in value.EnumerateArray())
                {
                    if (count++ >= MaxJsonArrayItems || budget.Exhausted)
                    {
                        budget.MarkTruncated();
                        break;
                    }
                    items.Add(BoundedNode(child, depth + 1, budget));
                }
                return items;
            case JsonValueKind.String:
                return JsonValue.Create(budget.Take(value.GetString() ?? string.Empty, MaxJsonStringChars));
            case JsonValueKind.Number:
                budget.Spend(value.GetRawText());
                return JsonNode.Parse(value.GetRawText());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    private static ToolResultPreviewDto Withheld(string reason) => new() { Kind = "withheld", Reason = reason };

    private static JsonNode? ParseArguments(string arguments)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly string[] SensitiveExtensions = [".pem", ".key", ".pfx", ".p12", ".kdbx"];
    private static readonly string[] SensitiveNamePrefixes = [".env", "id_rsa", "id_ed25519", "credentials"];
    private static readonly string[] SensitiveNames = [".npmrc", ".pypirc"];

    /// <summary>
    /// File names that hold credentials by convention. Matched on the last path segment,
    /// case-insensitively, whatever the separator.
    /// </summary>
    public static bool IsSensitivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = path.Trim().TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        var name = (slash >= 0 ? trimmed[(slash + 1)..] : trimmed).ToLowerInvariant();
        if (name.Length == 0) return false;
        return SensitiveNames.Contains(name)
            || SensitiveNamePrefixes.Any(name.StartsWith)
            || SensitiveExtensions.Any(name.EndsWith);
    }

    /// <summary>Whether a unified diff names a credential-shaped file in any header.</summary>
    private static bool TouchesSensitivePath(string diff)
    {
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (line["diff --git ".Length..].Split(' ').Any(part => IsSensitivePath(StripDiffPrefix(part)))) return true;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (IsSensitivePath(StripDiffPrefix(line[4..].Split('\t')[0]))) return true;
            }
        }
        return false;
    }

    private static string StripDiffPrefix(string part) =>
        part.StartsWith("a/", StringComparison.Ordinal) || part.StartsWith("b/", StringComparison.Ordinal) ? part[2..] : part;

    private static bool TryArray(JsonElement root, string name, out JsonElement array)
    {
        array = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out array)
            && array.ValueKind == JsonValueKind.Array;
    }

    /// <summary>Member lookup that ignores casing: some tool records carry no JSON naming.</summary>
    private static bool TryMember(JsonElement item, string name, out JsonElement value)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name.Replace("_", string.Empty), name.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string LineString(JsonElement line)
    {
        if (line.ValueKind == JsonValueKind.String) return line.GetString() ?? string.Empty;
        if (line.ValueKind != JsonValueKind.Object) return string.Empty;
        return TryMember(line, "content", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int? LineNumber(JsonElement line) =>
        line.ValueKind == JsonValueKind.Object && TryMember(line, "line_number", out var number)
            && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string? String(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? Long(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static bool? Bool(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    /// <summary>
    /// The response-wide UTF-8 byte allowance. Every string that goes into a preview is
    /// paid for here, so no combination of per-kind limits can exceed the caller's
    /// <c>max_bytes</c> by more than the fixed structure around the strings.
    /// </summary>
    internal sealed class Budget(int maxBytes)
    {
        private int _remaining = maxBytes;

        public bool Truncated { get; private set; }

        public bool Exhausted => _remaining <= 0;

        public void MarkTruncated() => Truncated = true;

        public void Spend(string text) => _remaining -= Encoding.UTF8.GetByteCount(text);

        /// <summary>The head of <paramref name="text"/>, at most <paramref name="maxChars"/> characters and what the budget allows.</summary>
        public string Take(string text, int maxChars)
        {
            var cut = text.Length > maxChars ? text[..SafeCut(text, maxChars)] : text;
            return TakeHead(cut, int.MaxValue, cut.Length < text.Length);
        }

        public string TakeHead(string text, int maxBytes) => TakeHead(text, maxBytes, false);

        public string TakeTail(string text, int maxBytes)
        {
            var allowance = Math.Max(0, Math.Min(maxBytes, _remaining));
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes <= allowance)
            {
                _remaining -= bytes;
                return text;
            }

            Truncated = true;
            var start = text.Length;
            var used = 0;
            while (start > 0)
            {
                var width = CharWidth(text, start - 1, out var span);
                if (used + width > allowance) break;
                used += width;
                start -= span;
            }
            _remaining -= used;
            return text[start..];
        }

        private string TakeHead(string text, int maxBytes, bool alreadyCut)
        {
            if (alreadyCut) Truncated = true;
            var allowance = Math.Max(0, Math.Min(maxBytes, _remaining));
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes <= allowance)
            {
                _remaining -= bytes;
                return text;
            }

            Truncated = true;
            var end = 0;
            var used = 0;
            while (end < text.Length)
            {
                var width = CharWidth(text, end, out var span, forward: true);
                if (used + width > allowance) break;
                used += width;
                end += span;
            }
            _remaining -= used;
            return text[..end];
        }

        /// <summary>UTF-8 width of the code point at <paramref name="index"/>, never splitting a surrogate pair.</summary>
        private static int CharWidth(string text, int index, out int span, bool forward = false)
        {
            var c = text[index];
            if (forward && char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                span = 2;
                return 4;
            }
            if (!forward && char.IsLowSurrogate(c) && index > 0 && char.IsHighSurrogate(text[index - 1]))
            {
                span = 2;
                return 4;
            }
            span = 1;
            return c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
        }

        private static int SafeCut(string text, int maxChars) =>
            maxChars > 0 && char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
    }
}
