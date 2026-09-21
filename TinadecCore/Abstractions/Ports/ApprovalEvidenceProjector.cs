using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// What a human is actually being asked to approve, projected out of the tool
/// call parameters.
/// <para>
/// The approval row historically carried only <c>tool_id</c> plus a one-line
/// summary, so a person was asked to approve a write, a shell command or a remote
/// push without ever seeing which path, which command, or which branch. The facts
/// already exist in the frozen parameters; this projector is the single place that
/// turns them into a decision-grade, bounded, non-leaking view.
/// </para>
/// <para>
/// It lives in Abstractions because the mint site (Lifecycle) and the projection
/// (AspNetCore) both need it while business modules may not reference each other —
/// the same reason <see cref="CoreVirtualToolPolicy"/> lives here.
/// </para>
/// </summary>
public static class ApprovalEvidenceProjector
{
    /// <summary>Hard ceiling on the projected payload, matching the durable summary column budget.</summary>
    public const int MaximumDigestLength = 4096;

    private const int MaximumScalarLength = 240;
    private const int MaximumArrayItems = 8;

    /// <summary>
    /// Keys whose value is the bulk an agent is writing rather than something a
    /// reviewer can read at a glance. They are replaced by a size and a hash prefix,
    /// so the approval still pins exactly what was authorized (the request hash binds
    /// the full bytes) without shipping file bodies, prompt text or MCP payloads to
    /// every UI that lists approvals.
    /// </summary>
    private static readonly HashSet<string> BulkKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "contents", "file_contents", "body", "text", "data", "payload",
        "patch", "diff", "message", "messages", "prompt", "stdin", "input",
        "arguments", "parameters", "env", "environment", "rows", "cells", "source",
    };

    /// <summary>Keys that must never leave the server, not even as a length.</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key", "apikey", "key", "token", "access_token", "refresh_token", "secret",
        "client_secret", "password", "credential", "credentials", "authorization", "nonce",
        "nonce_material", "private_key",
    };

    private static readonly string[] CommandKeys = ["command", "executable", "script"];

    private static readonly string[] WorkingDirectoryKeys = ["cwd", "working_directory", "workdir"];

    private static readonly string[] PathKeys =
    [
        "filepath", "file_path", "path", "paths", "target", "target_path", "target_project_path",
        "file", "files", "ref", "branch", "start_ref", "remote", "url", "server_id", "tool_name",
    ];

    /// <summary>
    /// Projects a JSON object parameter payload. Never throws: an unparseable or
    /// non-object blob degrades to an opaque descriptor, because a failed projection
    /// must not be able to stop an approval from being minted.
    /// </summary>
    public static ApprovalEvidence Project(string? toolId, string? parametersJson)
    {
        JsonDocument? document = null;
        JsonElement root;
        try
        {
            document = string.IsNullOrWhiteSpace(parametersJson)
                ? null
                : JsonDocument.Parse(parametersJson);
            root = document?.RootElement ?? default;
        }
        catch (JsonException)
        {
            document = null;
            root = default;
        }

        using (document)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                var opaque = string.IsNullOrWhiteSpace(parametersJson)
                    ? "{}"
                    : "{\"[unparsed parameters]\":\"" + DescribeBlob(parametersJson) + "\"}";
                return new ApprovalEvidence(opaque, Name(toolId, null), null, null, null);
            }

            var builder = new StringBuilder("{");
            string? command = null;
            string? workingDirectory = null;
            string? resourcePath = null;
            var written = 0;

            foreach (var property in root.EnumerateObject())
            {
                if (written >= MaximumDigestLength)
                {
                    break;
                }

                if (written > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(Escape(property.Name)).Append("\":");

                var value = SecretKeys.Contains(property.Name)
                    ? "\"[redacted]\""
                    : BulkKeys.Contains(property.Name)
                        ? "\"[" + DescribeBulk(property.Value) + "]\""
                        : DescribeValue(property.Value);

                builder.Append(value);
                written += property.Name.Length + value.Length + 4;

                command ??= CommandKeys.Contains(property.Name) ? ScalarText(property.Value) : null;
                workingDirectory ??= WorkingDirectoryKeys.Contains(property.Name) ? ScalarText(property.Value) : null;
                resourcePath ??= PathKeys.Contains(property.Name)
                    ? ScalarText(property.Value) ?? FirstScalarText(property.Value)
                    : null;
            }

            builder.Append('}');
            // Values are accumulated only while under budget, so the only way to
            // overrun is one single enormous scalar. Cutting that mid-token would
            // hand the UI malformed JSON, so it degrades to an opaque fingerprint
            // of the whole payload instead.
            var digest = builder.Length <= MaximumDigestLength
                ? builder.ToString()
                : "{\"[digest omitted]\":\"" + DescribeBlob(parametersJson!) + "\"}";

            var subject = command is not null ? $"`{command}`" : resourcePath;
            return new ApprovalEvidence(
                digest,
                Name(toolId, subject ?? FirstFact(root)),
                Truncate(command),
                Truncate(workingDirectory),
                Truncate(resourcePath));
        }
    }

    private static string Name(string? toolId, string? subject)
    {
        var name = string.IsNullOrWhiteSpace(toolId) ? "tool call" : toolId;
        return subject is null ? name : Truncate($"{name} → {subject}")!;
    }

    /// <summary>
    /// Serialises an evidence bundle for the durable column. Encoding and decoding
    /// live here so a mint site can never write a shape the projection cannot read
    /// back, and the stored payload stays one bounded JSON object.
    /// </summary>
    public static string Encode(ApprovalEvidence evidence)
    {
        var payload = new JsonObject
        {
            ["summary"] = evidence.Summary,
            ["command"] = evidence.Command,
            ["cwd"] = evidence.WorkingDirectory,
            ["path"] = evidence.ResourcePath,
            ["arguments"] = ParseOrOpaque(evidence.Arguments),
        };
        return payload.ToJsonString();
    }

    /// <summary>
    /// Reads a stored bundle. Rows written before this field existed (and any row
    /// whose payload is not the expected object) decode to <c>false</c> so the
    /// caller falls back to the plain summary instead of inventing facts.
    /// </summary>
    public static bool TryDecode(string? stored, out ApprovalEvidence evidence)
    {
        evidence = new ApprovalEvidence(string.Empty, string.Empty, null, null, null);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            var payload = JsonNode.Parse(stored)?.AsObject();
            if (payload is null || !payload.ContainsKey("arguments"))
            {
                return false;
            }

            evidence = new ApprovalEvidence(
                payload["arguments"]?.ToJsonString() ?? "{}",
                payload["summary"]?.GetValue<string>() ?? string.Empty,
                payload["command"]?.GetValue<string>(),
                payload["cwd"]?.GetValue<string>(),
                payload["path"]?.GetValue<string>());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // A stored field holding the wrong JSON kind is a legacy or hand-edited
            // row, not a reason to fail the whole approvals list.
            return false;
        }
    }

    private static JsonNode ParseOrOpaque(string arguments)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return JsonValue.Create(arguments);
        }
    }

    /// <summary>
    /// The first readable, non-sensitive scalar — the fallback when a tool names no
    /// command and no path (git ref operations, worktree removal, conflict resolve).
    /// </summary>
    private static string? FirstFact(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (SecretKeys.Contains(property.Name) || BulkKeys.Contains(property.Name))
            {
                continue;
            }

            var text = ScalarText(property.Value) ?? FirstScalarText(property.Value);
            if (text is not null)
            {
                return $"{property.Name}={text}";
            }
        }

        return null;
    }

    private static string DescribeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String => "\"" + TruncateJsonString(value.GetString()) + "\"",
        JsonValueKind.Array => DescribeArray(value),
        JsonValueKind.Object => "\"{…} " + value.EnumerateObject().Count() + " keys\"",
        _ => "\"[unsupported]\"",
    };

    private static string DescribeArray(JsonElement value)
    {
        var items = value.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            return "[]";
        }

        var kept = items.Take(MaximumArrayItems).Select(DescribeValue);
        var suffix = items.Count > MaximumArrayItems ? $",\"+{items.Count - MaximumArrayItems} more\"" : string.Empty;
        return '[' + string.Join(',', kept) + suffix + ']';
    }

    /// <summary>
    /// Size plus a stable hash prefix: enough to show that a 40 KB patch is not the
    /// 200 byte one the user just approved, without reproducing the content.
    /// </summary>
    private static string DescribeBulk(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return $"{raw.Length} chars, sha256 {Fingerprint(raw)}";
    }

    private static string DescribeBlob(string parametersJson) =>
        $"{parametersJson.Length} bytes, sha256 {Fingerprint(parametersJson)}";

    private static string Fingerprint(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static string? ScalarText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FirstScalarText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                return item.GetString();
            }
        }

        return null;
    }

    private static string TruncateJsonString(string? text) =>
        Escape(Truncate(text) ?? string.Empty);

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string? Truncate(string? text)
    {
        if (text is null || text.Length <= MaximumScalarLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, MaximumScalarLength), "…[truncated]");
    }
}

/// <summary>
/// The decision-grade view of one tool call. <paramref name="arguments"/> is the
/// redacted projection, <paramref name="summary"/> the human headline, and the
/// three named fields the same data pulled out for the affordances that need them
/// verbatim (a command line, a working directory, a target path).
/// </summary>
public sealed record ApprovalEvidence(
    string Arguments,
    string Summary,
    string? Command,
    string? WorkingDirectory,
    string? ResourcePath);
