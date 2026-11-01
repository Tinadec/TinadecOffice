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
    public const int MaximumSpecificationDocumentLength = 3000;

    /// <summary>A specification is approved only when its entire document fits the review payload.</summary>
    public static string? SpecificationArguments(string stage, string document)
    {
        if (string.IsNullOrWhiteSpace(document) || document.Length > MaximumSpecificationDocumentLength) return null;
        var result = JsonSerializer.Serialize(new { stage, document }, Readable);
        // Reserve room for Encode's summary and metadata in the 4096-character
        // durable digest column. Unicode is stored as Unicode, not \u escapes.
        return result.Length <= MaximumDigestLength - 512 ? result : null;
    }

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
        "content", "content_base64", "contents", "file_contents", "body", "text", "data", "payload",
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

    private static readonly string[] QueryKeys = ["pattern", "query", "q"];

    /// <summary>
    /// The search term of a call (<c>file_search</c>'s pattern, an MCP or web query), read back out of
    /// the already-redacted, already-truncated projection so it can never carry more than the approval does.
    /// Null when the call names none.
    /// </summary>
    public static string? QueryOf(ApprovalEvidence evidence)
    {
        try
        {
            using var document = JsonDocument.Parse(evidence.Arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in QueryKeys)
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One secret-key table for every projection that leaves Core (approvals and result previews).</summary>
    internal static bool IsSecretKey(string name) => SecretKeys.Contains(name);

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

            if (CoreVirtualToolPolicy.IsSpecPropose(toolId)
                && root.TryGetProperty("stage", out var stageValue) && stageValue.ValueKind == JsonValueKind.String
                && root.TryGetProperty("document", out var documentValue) && documentValue.ValueKind == JsonValueKind.String
                && SpecificationArguments(stageValue.GetString()!, documentValue.GetString()!) is { } specification)
                return new ApprovalEvidence(specification, $"Confirm {stageValue.GetString()} specification", null, null, null)
                    { CompleteSpecification = true };

            if (toolId is not null && toolId.StartsWith("browser_", StringComparison.Ordinal))
                return ProjectBrowser(toolId, root, parametersJson);

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

    /// <summary>
    /// Browser approvals need a sentence a person can judge: which page/ref and what the step
    /// does. Typed text and wait conditions are never echoed; a size and a short hash pin them
    /// instead, at any depth (a batch step's args included).
    /// </summary>
    private static ApprovalEvidence ProjectBrowser(string toolId, JsonElement root, string? parametersJson)
    {
        var digest = new JsonObject { ["tool"] = toolId };
        var summary = toolId;
        string? resource = null;

        var url = StringField(root, "url");
        var reference = IntField(root, "ref");
        var key = StringField(root, "key");
        var action = StringField(root, "action");
        var tabId = StringField(root, "tab_id");
        var button = StringField(root, "button");
        var clickCount = IntField(root, "click_count");
        var deltaX = DoubleField(root, "delta_x");
        var deltaY = DoubleField(root, "delta_y");

        if (url is not null) digest["url"] = url;
        if (reference is not null) digest["ref"] = reference.Value;
        if (key is not null) digest["key"] = key;
        if (action is not null) digest["action"] = action;
        if (tabId is not null) digest["tab_id"] = tabId;
        if (button is not null) digest["button"] = button;
        if (clickCount is not null) digest["click_count"] = clickCount.Value;
        if (deltaX is not null) digest["delta_x"] = deltaX.Value;
        if (deltaY is not null) digest["delta_y"] = deltaY.Value;

        var text = StringField(root, "text");
        if (text is not null || toolId is "browser_type" or "browser_wait")
        {
            var fingerprint = TextFingerprint(text);
            digest["text_chars"] = fingerprint.Chars;
            digest["text_sha256"] = fingerprint.Hash;
        }

        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            var stepArray = new JsonArray();
            var labels = new List<string>();
            var index = 0;
            foreach (var step in steps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object) continue;
                var stepTool = StringField(step, "tool") ?? "step";
                var stepNode = new JsonObject { ["tool"] = stepTool };
                if (step.TryGetProperty("args", out var stepArgs) && stepArgs.ValueKind == JsonValueKind.Object)
                {
                    if (IntField(stepArgs, "ref") is { } stepRef) stepNode["ref"] = stepRef;
                    if (StringField(stepArgs, "button") is { } stepButton) stepNode["button"] = stepButton;
                    if (StringField(stepArgs, "key") is { } stepKey) stepNode["key"] = stepKey;
                    if (DoubleField(stepArgs, "delta_x") is { } stepDeltaX) stepNode["delta_x"] = stepDeltaX;
                    if (DoubleField(stepArgs, "delta_y") is { } stepDeltaY) stepNode["delta_y"] = stepDeltaY;
                    if (stepArgs.TryGetProperty("text", out _) || stepTool == "browser_type")
                    {
                        var stepFingerprint = TextFingerprint(StringField(stepArgs, "text"));
                        stepNode["text_chars"] = stepFingerprint.Chars;
                        stepNode["text_sha256"] = stepFingerprint.Hash;
                    }
                }
                stepArray.Add(stepNode);
                if (index < 3) labels.Add(stepTool);
                index++;
            }
            digest["steps"] = stepArray;
            summary = index == 0
                ? "Run browser steps"
                : $"Run {index} browser step{(index == 1 ? "" : "s")}: {string.Join(", ", labels)}{(index > 3 ? ", …" : "")}";
        }
        else
        {
            summary = toolId switch
            {
                "browser_navigate" => url is null ? "Browse" : $"Browse to {TruncateText(url, 160)}",
                "browser_click" => reference is null ? "Click" : $"Click ref {reference}",
                "browser_type" => reference is null ? "Type into the page" : $"Type into ref {reference} ({text?.Length ?? 0} chars)",
                "browser_press" => key is null ? "Press a key" : $"Press {key}{(reference is null ? "" : $" on ref {reference}")}",
                "browser_scroll" => reference is null
                    ? $"Scroll the page by ({deltaX ?? 0}, {deltaY ?? 0})"
                    : $"Scroll ref {reference} by ({deltaX ?? 0}, {deltaY ?? 0})",
                "browser_history" => $"Browser {action ?? "history"}",
                "browser_tab_open" => url is null ? "Open a browser tab" : $"Open a browser tab at {TruncateText(url, 160)}",
                "browser_tab_select" => $"Switch to browser tab {tabId ?? "?"}",
                "browser_tab_close" => $"Close browser tab {tabId ?? "?"}",
                _ => toolId,
            };
        }

        resource = toolId switch
        {
            "browser_navigate" or "browser_tab_open" => TruncateText(url, MaximumScalarLength),
            "browser_tab_select" or "browser_tab_close" => TruncateText(tabId, MaximumScalarLength),
            "browser_click" or "browser_type" or "browser_press" or "browser_scroll" when reference is not null => "ref:" + reference,
            _ => null,
        };

        var json = digest.ToJsonString(Readable);
        if (json.Length > MaximumDigestLength)
        {
            json = "{\"[digest omitted]\":\"" + DescribeBlob(parametersJson ?? string.Empty) + "\"}";
        }
        return new ApprovalEvidence(json, TruncateText(summary, MaximumScalarLength) ?? toolId, null, null, resource);
    }

    private static (int Chars, string Hash) TextFingerprint(string? text)
    {
        var value = text ?? string.Empty;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        return (value.Length, hash);
    }

    private static string? StringField(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntField(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : (int?)null;

    private static double? DoubleField(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.TryGetDouble(out var number)
            ? number
            : (double?)null;

    private static string? TruncateText(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// The parameters with every secret-keyed value replaced, at any depth, and everything else
    /// kept. For a reader that must judge the call itself — a delegated approval gate — hashing
    /// the bulk away (as <see cref="Project"/> does for listings) would hide exactly what it is
    /// asked to judge; a token's value, though, never helps a judgement and never leaves Core.
    /// Never throws: unparseable input degrades to an opaque descriptor.
    /// </summary>
    public static string RedactSecrets(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return "{}";
        try
        {
            var node = JsonNode.Parse(parametersJson);
            RedactNode(node);
            return node?.ToJsonString(Readable) ?? "{}";
        }
        catch (JsonException)
        {
            return "{\"[unparsed parameters]\":\"" + DescribeBlob(parametersJson) + "\"}";
        }
    }

    // Read by a model, not embedded in HTML: non-ASCII text stays readable instead of escaped.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (var key in item.Select(property => property.Key).ToList())
                {
                    if (SecretKeys.Contains(key)) item[key] = "[redacted]";
                    else RedactNode(item[key]);
                }
                break;
            case JsonArray items:
                foreach (var child in items) RedactNode(child);
                break;
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
        if (evidence.CompleteSpecification) payload["complete_specification"] = true;
        return evidence.CompleteSpecification ? payload.ToJsonString(Readable) : payload.ToJsonString();
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

            var completeSpecification = payload["complete_specification"] is JsonValue flag && flag.TryGetValue<bool>(out var complete) && complete;
            evidence = new ApprovalEvidence(
                (completeSpecification ? payload["arguments"]?.ToJsonString(Readable) : payload["arguments"]?.ToJsonString()) ?? "{}",
                payload["summary"]?.GetValue<string>() ?? string.Empty,
                payload["command"]?.GetValue<string>(),
                payload["cwd"]?.GetValue<string>(),
                payload["path"]?.GetValue<string>()) { CompleteSpecification = completeSpecification };
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
    string? ResourcePath)
{
    public bool CompleteSpecification { get; init; }
}
