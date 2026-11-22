using System.Text.Json;
using System.Text.RegularExpressions;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Canonical Core MCP server management service unifying <c>/api/v1/mcp/servers</c>,
/// <c>/api/v1/tools/mcp/servers</c>, and <c>tinadec_mcp_apply</c> over
/// <see cref="IMcpResourceRegistry"/> and <see cref="ISecretStore"/>.
/// </summary>
public interface IMcpServerService
{
    Task<IReadOnlyList<McpResourceDto>> ListConfiguredAsync(Guid? projectId = null, CancellationToken cancellationToken = default);
    Task<McpResourceDto?> GetAsync(string serverIdOrResourceId, Guid? projectId = null, CancellationToken cancellationToken = default);
    Task<McpResourceDto> CreateAsync(McpResourceWriteDto input, long expectedRevision = 0, CancellationToken cancellationToken = default);
    Task<McpImportJsonResultDto> ImportJsonAsync(McpImportJsonRequestDto request, CancellationToken cancellationToken = default);
    Task<McpResourceDto> UpdateAsync(string serverIdOrResourceId, McpResourceWriteDto input, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<McpResourceDto> SetEnabledAsync(string serverIdOrResourceId, bool enabled, Guid? projectId = null, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<McpServerTestResultDto> TestAsync(string serverIdOrResourceId, Guid? projectId = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(string serverIdOrResourceId, Guid? projectId = null, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<McpExportResultDto> ExportAsync(Guid? projectId = null, string format = "json", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ExportAsync is not implemented by this MCP server service.");
}

public sealed record ParsedMcpEntry(
    string Id,
    string? Name,
    string Transport,
    string? Url,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string?>? Env,
    IReadOnlyDictionary<string, string?>? Headers,
    string? Cwd,
    bool? Enabled);

/// <summary>
/// Shared parser for Claude/Cursor/Antigravity/Gemini/Qwen JSON (<c>mcpServers</c>/<c>servers</c>),
/// Codex <c>[mcp_servers.&lt;id&gt;]</c> TOML, and Microsoft APM <c>apm.yml</c> (<c>dependencies.mcp</c>)
/// MCP configuration snippets, plus <c>${VAR}</c> / <c>${env:VAR}</c> secret placeholder helpers.
/// </summary>
public static partial class McpConfigParser
{
    public static IReadOnlyList<ParsedMcpEntry> ParseConfigEntries(string? rawJson, JsonElement? configElement)
    {
        if (configElement is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } elem)
            return ParseJsonElement(elem);

        var text = (rawJson ?? string.Empty).TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
            return [];

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return ParseJsonElement(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new ToolSettingsException("invalid_mcp_json", $"Invalid MCP JSON: {ex.Message}");
            }
        }

        if (text.Contains("[mcp_servers.", StringComparison.Ordinal))
            return ParseCodexToml(text);

        if (LooksLikeApmOrMcpYaml(text))
            return ParseApmYaml(text);

        throw new ToolSettingsException("invalid_mcp_json", "Expected JSON containing 'mcpServers'/'mcp_servers'/'servers', Codex '[mcp_servers.<id>]' TOML, or APM 'dependencies.mcp' YAML.");
    }

    public static IReadOnlyList<string> ExtractSecretPlaceholders(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return [];
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in SecretPlaceholderRegex().Matches(value))
        {
            var varName = match.Groups[1].Value;
            if (varName.Length > 0 && seen.Add(varName))
                list.Add(varName);
        }
        return list;
    }

    public static IReadOnlyList<string> ExtractSecretPlaceholders(ParsedMcpEntry entry) =>
        ExtractSecretPlaceholders(entry.Url, entry.Args, entry.Env, entry.Headers);

    public static (bool HasHiddenUnicode, IReadOnlyList<string> Warnings) ScanHiddenUnicode(string? text, string? target = null)
    {
        if (string.IsNullOrEmpty(text))
            return (false, []);

        var zw = 0;
        var bidi = 0;
        var tag = 0;
        var idx = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if ((cp >= 0x200B && cp <= 0x200F) || cp == 0x2060 || (cp == 0xFEFF && idx > 0))
                zw++;
            else if ((cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2066 && cp <= 0x2069))
                bidi++;
            else if (cp >= 0xE0000 && cp <= 0xE007F)
                tag++;
            idx++;
        }

        if (zw == 0 && bidi == 0 && tag == 0)
            return (false, []);

        var label = string.IsNullOrWhiteSpace(target) ? "MCP configuration" : target;
        return (true, [$"Hidden Unicode control characters detected in {label} (zero_width={zw}, bidi={bidi}, tag={tag})."]);
    }

    public static IReadOnlyList<string> ExtractSecretPlaceholders(
        string? url,
        IReadOnlyList<string>? args,
        IReadOnlyDictionary<string, string?>? env,
        IReadOnlyDictionary<string, string?>? headers)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Collect(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var v in ExtractSecretPlaceholders(text))
            {
                if (seen.Add(v)) list.Add(v);
            }
        }

        if (env is not null)
            foreach (var kvp in env) Collect(kvp.Value);
        if (headers is not null)
            foreach (var kvp in headers) Collect(kvp.Value);
        if (args is not null)
            foreach (var arg in args) Collect(arg);
        Collect(url);

        return list;
    }

    public static string SubstituteSecretPlaceholders(
        string value,
        IReadOnlyDictionary<string, string>? secretValues,
        out IReadOnlyList<string> allPlaceholders,
        out IReadOnlyList<string> unresolvedPlaceholders)
    {
        if (string.IsNullOrEmpty(value))
        {
            allPlaceholders = [];
            unresolvedPlaceholders = [];
            return value ?? string.Empty;
        }

        var all = new List<string>();
        var allSeen = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        var unresolvedSeen = new HashSet<string>(StringComparer.Ordinal);

        var replaced = SecretPlaceholderRegex().Replace(value, match =>
        {
            var varName = match.Groups[1].Value;
            if (allSeen.Add(varName))
                all.Add(varName);

            if (TryLookupSecretValue(secretValues, varName, out var resolved))
                return resolved;

            if (unresolvedSeen.Add(varName))
                unresolved.Add(varName);
            return match.Value;
        });

        allPlaceholders = all;
        unresolvedPlaceholders = unresolved;
        return replaced;
    }

    public static bool TryLookupSecretValue(
        IReadOnlyDictionary<string, string>? secretValues,
        string variableName,
        out string value)
    {
        value = string.Empty;
        if (secretValues is null || secretValues.Count == 0 || string.IsNullOrWhiteSpace(variableName))
            return false;

        if (secretValues.TryGetValue(variableName, out var exact) && exact is not null)
        {
            value = exact;
            return true;
        }

        foreach (var pair in secretValues)
        {
            if (string.Equals(pair.Key, variableName, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
            {
                value = pair.Value;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ParsedMcpEntry> ParseJsonElement(JsonElement root)
    {
        var results = new List<ParsedMcpEntry>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(item, "id") ?? ReadString(item, "key") ?? ReadString(item, "name") ?? string.Empty;
                results.Add(ParseServerObject(id, item));
            }
            return results;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return results;

        foreach (var containerKey in new[] { "mcpServers", "mcp_servers", "servers", "mcp" })
        {
            if (!root.TryGetProperty(containerKey, out var container)) continue;
            if (container.ValueKind == JsonValueKind.Array)
                return ParseJsonArrayWithScalarSupport(container);
            if (container.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in container.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        results.Add(ParseServerObject(prop.Name, prop.Value));
                }
                return results;
            }
        }

        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object
            && deps.TryGetProperty("mcp", out var depMcp))
        {
            if (depMcp.ValueKind == JsonValueKind.Array)
                return ParseJsonArrayWithScalarSupport(depMcp);
            if (depMcp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in depMcp.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        results.Add(ParseServerObject(prop.Name, prop.Value));
                }
                return results;
            }
        }

        // Single server object with command/url
        if (root.TryGetProperty("command", out _) || root.TryGetProperty("url", out _))
        {
            var id = ReadString(root, "id") ?? ReadString(root, "key") ?? ReadString(root, "name") ?? string.Empty;
            results.Add(ParseServerObject(id, root));
            return results;
        }

        // Direct map of { "<id>": { command/url } }
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object
                && (prop.Value.TryGetProperty("command", out _) || prop.Value.TryGetProperty("url", out _)))
            {
                results.Add(ParseServerObject(prop.Name, prop.Value));
            }
        }

        return results;
    }

    private static ParsedMcpEntry ParseServerObject(string fallbackId, JsonElement obj)
    {
        var id = (ReadString(obj, "id") ?? fallbackId).Trim();
        var name = ReadString(obj, "name") ?? id;
        var command = ReadString(obj, "command");
        var url = ReadString(obj, "url") ?? ReadString(obj, "httpUrl") ?? ReadString(obj, "endpoint");
        var rawTransport = ReadString(obj, "transport") ?? ReadString(obj, "type");
        var transport = NormalizeTransport(rawTransport, command, url);
        var args = ReadStringArray(obj, "args");
        var env = ReadStringMap(obj, "env") ?? ReadStringMap(obj, "environment");
        var headers = ReadStringMap(obj, "headers") ?? ReadStringMap(obj, "http_headers");
        var cwd = ReadString(obj, "cwd") ?? ReadString(obj, "working_directory");

        bool? enabled = null;
        if (obj.TryGetProperty("disabled", out var dis) && dis.ValueKind is JsonValueKind.True or JsonValueKind.False)
            enabled = !dis.GetBoolean();
        else if (obj.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
            enabled = en.GetBoolean();

        return new ParsedMcpEntry(id, name, transport, url, command, args, env, headers, cwd, enabled);
    }

    public static IReadOnlyList<ParsedMcpEntry> ParseCodexToml(string toml)
    {
        var servers = new Dictionary<string, (string? Command, List<string> Args, string? Url, Dictionary<string, string?> Env, Dictionary<string, string?> Headers, string? Cwd, bool? Enabled, string? Transport)>(StringComparer.OrdinalIgnoreCase);
        string? currentServer = null;
        string? currentSubTable = null;

        foreach (var rawLine in toml.Split('\n'))
        {
            var line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var header = line[1..^1].Trim();
                currentServer = null;
                currentSubTable = null;

                var match = CodexSectionRegex().Match(header);
                if (match.Success)
                {
                    currentServer = UnquoteToml(match.Groups[1].Value);
                    currentSubTable = match.Groups[2].Success ? match.Groups[2].Value : null;
                    if (!servers.ContainsKey(currentServer))
                        servers[currentServer] = (null, [], null, new Dictionary<string, string?>(StringComparer.Ordinal), new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), null, null, null);
                }
                continue;
            }

            if (currentServer is null) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = UnquoteToml(line[..eq].Trim());
            var val = line[(eq + 1)..].Trim();
            var entry = servers[currentServer];

            if (string.Equals(currentSubTable, "env", StringComparison.OrdinalIgnoreCase))
            {
                entry.Env[key] = UnquoteToml(val);
                continue;
            }

            if (string.Equals(currentSubTable, "http_headers", StringComparison.OrdinalIgnoreCase)
                || string.Equals(currentSubTable, "headers", StringComparison.OrdinalIgnoreCase))
            {
                entry.Headers[key] = UnquoteToml(val);
                continue;
            }

            switch (key)
            {
                case "command":
                    entry.Command = UnquoteToml(val);
                    break;
                case "url":
                    entry.Url = UnquoteToml(val);
                    break;
                case "transport":
                case "type":
                    entry.Transport = UnquoteToml(val);
                    break;
                case "cwd":
                    entry.Cwd = UnquoteToml(val);
                    break;
                case "enabled":
                    if (bool.TryParse(val, out var en)) entry.Enabled = en;
                    break;
                case "disabled":
                    if (bool.TryParse(val, out var dis)) entry.Enabled = !dis;
                    break;
                case "args":
                    entry.Args.AddRange(ParseTomlStringArray(val));
                    break;
                case "env":
                    foreach (var pair in ParseTomlInlineTable(val)) entry.Env[pair.Key] = pair.Value;
                    break;
                case "http_headers":
                case "headers":
                    foreach (var pair in ParseTomlInlineTable(val)) entry.Headers[pair.Key] = pair.Value;
                    break;
            }
            servers[currentServer] = entry;
        }

        return servers.Select(kvp =>
        {
            var e = kvp.Value;
            var transport = NormalizeTransport(e.Transport, e.Command, e.Url);
            return new ParsedMcpEntry(
                kvp.Key,
                kvp.Key,
                transport,
                e.Url,
                e.Command,
                e.Args,
                e.Env.Count > 0 ? e.Env : null,
                e.Headers.Count > 0 ? e.Headers : null,
                e.Cwd,
                e.Enabled);
        }).ToList();
    }

    private static string NormalizeTransport(string? rawTransport, string? command, string? url)
    {
        var normalized = (rawTransport ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "stdio" => "stdio",
            "http" or "streamable-http" or "streamable_http" => "http",
            "sse" => "sse",
            _ when !string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(command) =>
                url.Trim().EndsWith("/sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "http",
            _ => "stdio",
        };
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var val) || val.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in val.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        }
        return list;
    }

    private static IReadOnlyDictionary<string, string?>? ReadStringMap(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var val) || val.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var prop in val.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                map[prop.Name] = prop.Value.GetString();
            else if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                map[prop.Name] = prop.Value.ToString();
        }
        return map;
    }

    private static string StripTomlComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && inDouble && i + 1 < line.Length) { i++; continue; }
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble) return line[..i];
        }
        return line;
    }

    private static string UnquoteToml(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return trimmed;
    }

    private static IEnumerable<string> ParseTomlStringArray(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) yield break;
        foreach (Match m in TomlQuotedStringRegex().Matches(trimmed[1..^1]))
            yield return UnquoteToml(m.Value);
    }

    private static Dictionary<string, string?> ParseTomlInlineTable(string value)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return map;
        foreach (Match m in TomlInlinePairRegex().Matches(trimmed[1..^1]))
        {
            map[UnquoteToml(m.Groups[1].Value)] = UnquoteToml(m.Groups[2].Value);
        }
        return map;
    }

    private static IReadOnlyList<ParsedMcpEntry> ParseJsonArrayWithScalarSupport(JsonElement array)
    {
        var results = new List<ParsedMcpEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } scalar)
            {
                if (ParseScalarMcpSpecifier(scalar) is { } specEntry)
                    results.Add(specEntry);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var id = ReadString(item, "id") ?? ReadString(item, "key") ?? ReadString(item, "name") ?? string.Empty;
                results.Add(ParseServerObject(id, item));
            }
        }
        return results;
    }

    public static bool LooksLikeApmOrMcpYaml(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = StripYamlComment(rawLine).Trim();
            if (trimmed.Length == 0 || trimmed is "---" or "...") continue;
            if (trimmed.StartsWith("dependencies:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("mcpServers:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("mcp_servers:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("servers:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pure-file parser for Microsoft APM <c>apm.yml</c> <c>dependencies.mcp</c> (and top-level YAML <c>mcp:</c>/<c>mcpServers:</c>)
    /// supporting scalar package/URL specifiers, sequences of server objects, and keyed server maps.
    /// </summary>
    public static IReadOnlyList<ParsedMcpEntry> ParseApmYaml(string yaml)
    {
        var lines = new List<(int Indent, string Text)>();
        foreach (var rawLine in (yaml ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var uncommented = StripYamlComment(rawLine).TrimEnd();
            if (uncommented.Trim().Length == 0) continue;
            var trimmed = uncommented.Trim();
            if (trimmed is "---" or "...") continue;

            var indent = 0;
            while (indent < uncommented.Length && (uncommented[indent] == ' ' || uncommented[indent] == '\t'))
                indent += uncommented[indent] == '\t' ? 2 : 1;
            lines.Add((indent, trimmed));
        }

        if (lines.Count == 0)
            return [];

        for (var i = 0; i < lines.Count; i++)
        {
            var (indent, text) = lines[i];
            if (!TrySplitYamlKeyValue(text, out var key, out var inlineVal))
                continue;

            if (string.Equals(key, "dependencies", StringComparison.OrdinalIgnoreCase) && inlineVal.Length == 0)
            {
                for (var j = i + 1; j < lines.Count && lines[j].Indent > indent; j++)
                {
                    if (TrySplitYamlKeyValue(lines[j].Text, out var depKey, out var depInline)
                        && string.Equals(depKey, "mcp", StringComparison.OrdinalIgnoreCase))
                    {
                        return ParseYamlMcpSection(lines, j + 1, lines[j].Indent, depInline);
                    }
                }
            }
            else if (key.Equals("mcp", StringComparison.OrdinalIgnoreCase)
                || key.Equals("mcpServers", StringComparison.OrdinalIgnoreCase)
                || key.Equals("mcp_servers", StringComparison.OrdinalIgnoreCase)
                || key.Equals("servers", StringComparison.OrdinalIgnoreCase))
            {
                return ParseYamlMcpSection(lines, i + 1, indent, inlineVal);
            }
        }

        return [];
    }

    private static IReadOnlyList<ParsedMcpEntry> ParseYamlMcpSection(
        List<(int Indent, string Text)> lines,
        int startIndex,
        int headerIndent,
        string inlineValue)
    {
        var results = new List<ParsedMcpEntry>();
        if (inlineValue.StartsWith('[') && inlineValue.EndsWith(']'))
        {
            foreach (var item in ParseYamlFlowArray(inlineValue))
            {
                if (ParseScalarMcpSpecifier(item) is { } parsed)
                    results.Add(parsed);
            }
            return results;
        }
        if (inlineValue == "{}")
            return results;

        var block = new List<(int Indent, string Text)>();
        for (var i = startIndex; i < lines.Count; i++)
        {
            var (ind, txt) = lines[i];
            if (ind > headerIndent || (ind == headerIndent && IsYamlSequenceItem(txt)))
                block.Add((ind, txt));
            else
                break;
        }

        if (block.Count == 0)
            return results;

        if (IsYamlSequenceItem(block[0].Text))
        {
            var seqIndent = block[0].Indent;
            var idx = 0;
            while (idx < block.Count)
            {
                var (ind, txt) = block[idx];
                if (ind == seqIndent && IsYamlSequenceItem(txt))
                {
                    var firstRest = txt.Length == 1 ? string.Empty : txt[1..].Trim();
                    var itemProps = new List<(int Indent, string Text)>();
                    if (firstRest.Length > 0)
                        itemProps.Add((seqIndent + 2, firstRest));
                    idx++;
                    while (idx < block.Count && block[idx].Indent > seqIndent)
                    {
                        itemProps.Add(block[idx]);
                        idx++;
                    }

                    if (itemProps.Count == 1 && !LooksLikeYamlServerProperty(itemProps[0].Text))
                    {
                        if (ParseScalarMcpSpecifier(UnquoteYaml(itemProps[0].Text)) is { } spec)
                            results.Add(spec);
                    }
                    else if (itemProps.Count > 0)
                    {
                        if (ParseYamlServerProperties(string.Empty, itemProps) is { } objEntry)
                            results.Add(objEntry);
                    }
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
                if (ind == mapIndent && TrySplitYamlKeyValue(txt, out var serverId, out _))
                {
                    var itemProps = new List<(int Indent, string Text)>();
                    idx++;
                    while (idx < block.Count && block[idx].Indent > mapIndent)
                    {
                        itemProps.Add(block[idx]);
                        idx++;
                    }
                    if (ParseYamlServerProperties(UnquoteYaml(serverId), itemProps) is { } objEntry)
                        results.Add(objEntry);
                }
                else
                {
                    idx++;
                }
            }
        }

        return results;
    }

    private static ParsedMcpEntry? ParseYamlServerProperties(string fallbackId, List<(int Indent, string Text)> props)
    {
        if (props.Count == 0) return null;
        var baseIndent = props.Min(p => p.Indent);
        string? id = null;
        string? name = null;
        string? command = null;
        string? packageSpec = null;
        string? url = null;
        string? transport = null;
        string? cwd = null;
        bool? enabled = null;
        var args = new List<string>();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var i = 0;
        while (i < props.Count)
        {
            var (ind, txt) = props[i];
            if (ind > baseIndent || !TrySplitYamlKeyValue(txt, out var rawKey, out var rawVal))
            {
                i++;
                continue;
            }

            var key = UnquoteYaml(rawKey).Trim();
            var val = rawVal.Trim();
            i++;

            var children = new List<(int Indent, string Text)>();
            while (i < props.Count && props[i].Indent > ind)
            {
                children.Add(props[i]);
                i++;
            }

            switch (key.ToLowerInvariant())
            {
                case "id":
                case "key":
                    id = UnquoteYaml(val);
                    break;
                case "name":
                    name = UnquoteYaml(val);
                    break;
                case "command":
                    command = UnquoteYaml(val);
                    break;
                case "package":
                case "registry":
                    packageSpec = UnquoteYaml(val);
                    break;
                case "url":
                case "httpurl":
                case "endpoint":
                    url = UnquoteYaml(val);
                    break;
                case "transport":
                case "type":
                    transport = UnquoteYaml(val);
                    break;
                case "cwd":
                case "working_directory":
                    cwd = UnquoteYaml(val);
                    break;
                case "enabled":
                    if (bool.TryParse(UnquoteYaml(val), out var en)) enabled = en;
                    break;
                case "disabled":
                    if (bool.TryParse(UnquoteYaml(val), out var dis)) enabled = !dis;
                    break;
                case "args":
                    if (val.StartsWith('[') && val.EndsWith(']'))
                    {
                        args.AddRange(ParseYamlFlowArray(val));
                    }
                    else
                    {
                        foreach (var child in children)
                        {
                            if (IsYamlSequenceItem(child.Text))
                            {
                                var itemText = child.Text.Length == 1 ? string.Empty : child.Text[1..].Trim();
                                args.Add(UnquoteYaml(itemText));
                            }
                        }
                    }
                    break;
                case "env":
                case "environment":
                    ReadYamlMapInto(val, children, env);
                    break;
                case "headers":
                case "http_headers":
                    ReadYamlMapInto(val, children, headers);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(packageSpec))
        {
            if (ParseScalarMcpSpecifier(packageSpec) is { } spec)
            {
                command = spec.Command;
                url = spec.Url;
                transport ??= spec.Transport;
                if (args.Count == 0) args.AddRange(spec.Args);
                id ??= spec.Id;
            }
        }

        var resolvedId = !string.IsNullOrWhiteSpace(id) ? SanitizeMcpServerId(id!)
            : !string.IsNullOrWhiteSpace(fallbackId) ? SanitizeMcpServerId(fallbackId)
            : !string.IsNullOrWhiteSpace(name) ? SanitizeMcpServerId(name!)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(resolvedId))
            return null;

        var resolvedTransport = NormalizeTransport(transport, command, url);
        return new ParsedMcpEntry(
            resolvedId,
            string.IsNullOrWhiteSpace(name) ? resolvedId : name,
            resolvedTransport,
            url,
            command,
            args,
            env.Count > 0 ? env : null,
            headers.Count > 0 ? headers : null,
            cwd,
            enabled);
    }

    public static ParsedMcpEntry? ParseScalarMcpSpecifier(string rawSpecifier)
    {
        var spec = UnquoteYaml(rawSpecifier ?? string.Empty).Trim();
        if (spec.Length == 0)
            return null;

        if (spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var id = Uri.TryCreate(spec, UriKind.Absolute, out var uri)
                ? SanitizeMcpServerId((uri.Host + "-" + uri.AbsolutePath.Trim('/')).Trim('-'))
                : "mcp-http";
            var transport = spec.EndsWith("/sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "http";
            return new ParsedMcpEntry(id, id, transport, spec, null, [], null, null, null, true);
        }

        if (spec.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase)
            || spec.StartsWith("docker.io/", StringComparison.OrdinalIgnoreCase)
            || spec.StartsWith("mcr.microsoft.com/", StringComparison.OrdinalIgnoreCase))
        {
            var last = spec.Split('/')[^1];
            var colonIdx = last.IndexOf(':');
            if (colonIdx > 0) last = last[..colonIdx];
            var id = SanitizeMcpServerId(last);
            return new ParsedMcpEntry(id, id, "stdio", null, "docker", ["run", "-i", "--rm", spec], null, null, null, null);
        }

        var slug = spec.Split('/')[^1];
        var atIdx = slug.LastIndexOf('@');
        if (atIdx > 0) slug = slug[..atIdx];
        if (slug.StartsWith("server-", StringComparison.OrdinalIgnoreCase) && slug.Length > 7)
            slug = slug[7..];
        var serverId = SanitizeMcpServerId(slug);
        return new ParsedMcpEntry(serverId, spec, "stdio", null, "npx", ["-y", spec], null, null, null, null);
    }

    private static string SanitizeMcpServerId(string raw)
    {
        var cleaned = Regex.Replace((raw ?? string.Empty).Trim(), @"[^A-Za-z0-9_.\-]+", "-").Trim('-', '.');
        if (cleaned.Length == 0) return "mcp-server";
        return cleaned.Length <= 64 ? cleaned : cleaned[..64].TrimEnd('-', '.');
    }

    private static bool IsYamlSequenceItem(string text) =>
        text == "-" || text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("-\t", StringComparison.Ordinal);

    private static bool LooksLikeYamlServerProperty(string text)
    {
        if (text.StartsWith('"') || text.StartsWith('\'') || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        return TrySplitYamlKeyValue(text, out var key, out _)
            && key.ToLowerInvariant() is "id" or "key" or "name" or "command" or "package" or "registry" or "url" or "httpurl" or "endpoint" or "transport" or "type" or "args" or "env" or "environment" or "headers" or "http_headers" or "cwd" or "working_directory" or "enabled" or "disabled";
    }

    private static bool TrySplitYamlKeyValue(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || IsYamlSequenceItem(text))
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

    private static void ReadYamlMapInto(string inlineVal, List<(int Indent, string Text)> children, Dictionary<string, string?> target)
    {
        if (inlineVal.StartsWith('{') && inlineVal.EndsWith('}'))
        {
            var inner = inlineVal[1..^1].Trim();
            foreach (var part in SplitYamlFlowItems(inner))
            {
                if (TrySplitYamlKeyValue(part, out var k, out var v))
                    target[UnquoteYaml(k)] = UnquoteYaml(v);
            }
            return;
        }

        foreach (var (_, txt) in children)
        {
            if (TrySplitYamlKeyValue(txt, out var k, out var v))
                target[UnquoteYaml(k)] = UnquoteYaml(v);
        }
    }

    private static List<string> ParseYamlFlowArray(string flow)
    {
        var list = new List<string>();
        var trimmed = flow.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) return list;
        foreach (var item in SplitYamlFlowItems(trimmed[1..^1]))
        {
            var unquoted = UnquoteYaml(item);
            if (item.Trim().Length > 0)
                list.Add(unquoted);
        }
        return list;
    }

    private static IEnumerable<string> SplitYamlFlowItems(string content)
    {
        var inSingle = false;
        var inDouble = false;
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\\' && inDouble && i + 1 < content.Length) { i++; continue; }
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ',' && !inSingle && !inDouble)
            {
                var seg = content[start..i].Trim();
                if (seg.Length > 0) yield return seg;
                start = i + 1;
            }
        }
        var tail = content[start..].Trim();
        if (tail.Length > 0) yield return tail;
    }

    private static string StripYamlComment(string line)
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

    private static string UnquoteYaml(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            return trimmed[1..^1].Replace("''", "'");
        return trimmed;
    }

    [GeneratedRegex(@"\$\{(?:(?:env|input):)?([A-Za-z_][A-Za-z0-9_\-]*)\}")]
    private static partial Regex SecretPlaceholderRegex();

    [GeneratedRegex(@"^mcp_servers\.([""']?[^.\]\s""']+[""']?)(?:\.(env|http_headers|headers))?$", RegexOptions.IgnoreCase)]
    private static partial Regex CodexSectionRegex();

    [GeneratedRegex(@"""(?:\\.|[^""\\])*""|'(?:[^'])*'")]
    private static partial Regex TomlQuotedStringRegex();

    [GeneratedRegex(@"([a-zA-Z0-9_.\-]+|""[^""]+"")\s*=\s*(""(?:\\.|[^""\\])*""|'(?:[^'])*')")]
    private static partial Regex TomlInlinePairRegex();
}
