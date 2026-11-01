using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Tools;

/// <summary>The versioned configuration contract, including immutable provider safety ceilings.</summary>
public static class ToolSettingsSchema
{
    public static JsonElement Defaults => JsonSerializer.SerializeToElement(new
    {
        shell = new { enabled = true, shell = "auto", timeout_ms = 600000, command_timeout_ms = 30000, max_timeout_ms = 1800000, allow_long_lived = true, max_output_chars = 65536 },
        read = new { enabled = true, sentinel_line_limit = 150, directory_page_size = 100, max_file_bytes = (long?)null },
        write = new { enabled = true, max_file_bytes = (long?)null, create_parent_directories = true },
        search = new { enabled = true, max_results = 50, timeout_ms = (int?)null, rg_path = (string?)null, case_sensitive = false, fixed_strings = false, context_lines = 0, include_hidden = false, respect_ignore_files = true },
        git = new { enabled = true, timeout_ms = 60000, max_output_chars = 4194304, protected_branches = new[] { "main", "master" }, log_limit = 50, log_list_limit = 100, tool_budgets = new Dictionary<string,object>() },
        web = new { enabled = true, timeout_ms = 15000, max_bytes = 262144, max_chars = 12000, max_redirects = 3 },
        browser = new { enabled = true, max_snapshot_chars = 24000, action_timeout_ms = 15000, wait_timeout_ms = 30000, allow_private_hosts = false },
        mcp = new { enabled = true, timeout_ms = 30000, server_resource_ids = (Guid[]?)null },
        skills = new { enabled = true, resource_ids = (Guid[]?)null, search_depth = 2, max_skills = 40, max_index_chars = 8000, max_file_bytes = 262144 }
    });

    private static readonly IReadOnlyDictionary<string, (long Min, long Max)> Ranges = new Dictionary<string, (long, long)>
    {
        ["shell.timeout_ms"] = (1,1800000), ["shell.command_timeout_ms"] = (1,1800000), ["shell.max_timeout_ms"] = (1,1800000), ["shell.max_output_chars"] = (1,65536),
        ["read.sentinel_line_limit"] = (0,100000), ["read.directory_page_size"] = (1,500), ["read.max_file_bytes"] = (1,1073741824), ["write.max_file_bytes"] = (1,1073741824),
        ["search.max_results"] = (1,10000), ["search.context_lines"] = (0,1000), ["search.timeout_ms"] = (1,1800000), ["git.timeout_ms"] = (1,1800000), ["git.max_output_chars"] = (1,4194304), ["git.log_limit"] = (1,500), ["git.log_list_limit"] = (1,10000),
        ["web.timeout_ms"] = (1000,60000), ["web.max_bytes"] = (1,4194304), ["web.max_chars"] = (1,60000), ["web.max_redirects"] = (0,3),
        ["browser.max_snapshot_chars"] = (2048,64000), ["browser.action_timeout_ms"] = (1000,60000), ["browser.wait_timeout_ms"] = (100,60000),
        ["mcp.timeout_ms"] = (1,1800000), ["skills.search_depth"] = (2,2), ["skills.max_skills"] = (0,1000), ["skills.max_index_chars"] = (0,1048576), ["skills.max_file_bytes"] = (262144,262144)
    };
    private static readonly IReadOnlyList<string> GitTools = ["git_checkout", "git_branch_create", "git_branch_delete", "git_branch_rename", "git_conflict_resolve", "git_discard", "git_stage", "git_unstage", "git_worktree_create", "git_worktree_remove", "git_fetch", "git_push", "git_pull", "git_file_history", "git_log_list", "git_commit", "git_log_detail", "git_status", "git_push_readiness", "git_log", "git_diff", "git_branch_list", "git_worktree_list", "git_ref_list", "git_remote_list", "git_blame", "git_file_at_revision", "git_conflict_preview", "git_merge", "git_rebase"];

    public static JsonElement Schema()
    {
        var sections = new JsonObject();
        foreach (var section in Defaults.EnumerateObject())
        {
            var fields = new JsonObject();
            foreach (var field in section.Value.EnumerateObject())
            {
                var key = section.Name + "." + field.Name;
                JsonObject property;
                if (key == "git.tool_budgets")
                {
                    var tools = new JsonObject();
                    foreach (var tool in GitTools)
                        tools[tool] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject { ["timeout_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1800000, ["description"] = "Deadline for this Git tool in milliseconds." }, ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 4194304, ["description"] = "Maximum returned characters for this Git tool." } } };
                    property = new() { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = tools };
                }
                else if (field.Name is "resource_ids" or "server_resource_ids") property = new() { ["type"] = new JsonArray("array", "null"), ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" }, ["uniqueItems"] = true };
                else if (field.Name == "protected_branches") property = new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
                else if (field.Name == "rg_path") property = new() { ["type"] = new JsonArray("string", "null") };
                else if (Ranges.TryGetValue(key, out var range)) property = new() { ["type"] = field.Value.ValueKind == JsonValueKind.Null ? new JsonArray("integer", "null") : JsonValue.Create("integer"), ["minimum"] = range.Min, ["maximum"] = range.Max };
                else if (field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) property = new() { ["type"] = "boolean" };
                else property = new() { ["type"] = "string", ["enum"] = new JsonArray("auto", "cmd", "powershell", "pwsh", "bash") };
                property["default"] = JsonNode.Parse(field.Value.GetRawText());
                property["description"] = Description(key);
                fields[field.Name] = property;
            }
            sections[section.Name] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = fields };
        }
        return JsonSerializer.SerializeToElement(new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["additionalProperties"] = false, ["properties"] = sections });
    }

    public static JsonElement Validate(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw Invalid("Settings must be a JSON object.");
        var defaults = Defaults;
        var seenSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in input.EnumerateObject())
        {
            if (!seenSections.Add(section.Name) || !defaults.TryGetProperty(section.Name, out var defaultsSection) || section.Value.ValueKind != JsonValueKind.Object) throw Invalid($"Unknown, duplicated or invalid section '{section.Name}'.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in section.Value.EnumerateObject())
            {
                if (!seen.Add(field.Name) || !defaultsSection.TryGetProperty(field.Name, out var baseline)) throw Invalid($"Unknown or duplicated setting '{section.Name}.{field.Name}'.");
                var key = section.Name + "." + field.Name;
                var value = field.Value;
                if (value.ValueKind == JsonValueKind.Null && baseline.ValueKind == JsonValueKind.Null) continue;
                if (Ranges.TryGetValue(key, out var range))
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < range.Min || number > range.Max) throw Invalid($"'{key}' must be an integer between {range.Min} and {range.Max}.");
                }
                else if (key == "git.tool_budgets")
                {
                    if (value.ValueKind != JsonValueKind.Object) throw Invalid("git.tool_budgets must be an object keyed by Git tool id.");
                    var seenTools = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var tool in value.EnumerateObject())
                    {
                        if (!seenTools.Add(tool.Name) || !GitTools.Contains(tool.Name) || tool.Value.ValueKind != JsonValueKind.Object) throw Invalid($"Unknown or invalid Git tool budget '{tool.Name}'.");
                        var seenFields = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var budget in tool.Value.EnumerateObject())
                        {
                            if (!seenFields.Add(budget.Name) || budget.Name is not ("timeout_ms" or "max_output_chars") || budget.Value.ValueKind != JsonValueKind.Number || !budget.Value.TryGetInt64(out var number) || number < 1 || number > (budget.Name == "timeout_ms" ? 1800000 : 4194304)) throw Invalid($"Invalid Git budget '{tool.Name}.{budget.Name}'.");
                        }
                    }
                }
                else if (field.Name is "resource_ids" or "server_resource_ids")
                {
                    if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || !Guid.TryParse(v.GetString(), out var id) || id == Guid.Empty)) throw Invalid($"'{key}' must contain resource UUIDs or null.");
                    if (value.GetArrayLength() > 1000 || value.EnumerateArray().Select(v => v.GetString()).Distinct().Count() != value.GetArrayLength()) throw Invalid($"'{key}' contains duplicate or too many resources.");
                }
                else if (field.Name == "protected_branches")
                {
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100 || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()) || v.GetString()!.Length > 256)) throw Invalid($"'{key}' must contain branch names.");
                }
                else if (field.Name == "rg_path")
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 2048) throw Invalid($"'{key}' must be a path or null.");
                }
                else if (field.Name == "shell")
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("auto" or "cmd" or "powershell" or "pwsh" or "bash")) throw Invalid("Unsupported shell.");
                }
                else if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid($"'{key}' must be boolean.");
            }
        }
        return Canonical(input);
    }

    public static JsonElement Merge(JsonElement defaults, JsonElement patch)
    {
        var result = JsonNode.Parse(defaults.GetRawText())!.AsObject();
        MergeObject(result, patch);
        var merged = Canonical(JsonSerializer.SerializeToElement(result));
        var shell = merged.GetProperty("shell");
        if (shell.GetProperty("timeout_ms").GetInt64() > shell.GetProperty("max_timeout_ms").GetInt64() || shell.GetProperty("command_timeout_ms").GetInt64() > shell.GetProperty("max_timeout_ms").GetInt64()) throw Invalid("Shell default timeouts must not exceed max_timeout_ms.");
        return merged;
    }
    /// <summary>Agent defaults may narrow shared safety ceilings; they cannot remove shared guards.</summary>
    public static JsonElement MergeAgent(JsonElement shared, JsonElement patch)
    {
        var merged = Merge(shared, patch);
        foreach (var (section, field) in new[] { ("shell", "max_timeout_ms"), ("shell", "max_output_chars"), ("read", "max_file_bytes"), ("write", "max_file_bytes"), ("git", "max_output_chars"), ("web", "max_bytes"), ("web", "max_chars"), ("web", "max_redirects"), ("skills", "max_skills"), ("skills", "max_index_chars") })
            RequireNarrower(shared.GetProperty(section).GetProperty(field), merged.GetProperty(section).GetProperty(field), section + "." + field);
        var sharedGit = shared.GetProperty("git");
        var agentGit = merged.GetProperty("git");
        foreach (var tool in agentGit.GetProperty("tool_budgets").EnumerateObject())
            if (tool.Value.TryGetProperty("max_output_chars", out var output))
            {
                var ceiling = sharedGit.GetProperty("tool_budgets").TryGetProperty(tool.Name, out var sharedBudget) && sharedBudget.TryGetProperty("max_output_chars", out var specific) ? specific : sharedGit.GetProperty("max_output_chars");
                RequireNarrower(ceiling, output, "git.tool_budgets." + tool.Name + ".max_output_chars");
            }
        var guards = sharedGit.GetProperty("protected_branches").EnumerateArray().Concat(agentGit.GetProperty("protected_branches").EnumerateArray()).Select(branch => branch.GetString()).Distinct(StringComparer.Ordinal).ToArray();
        var result = JsonNode.Parse(merged.GetRawText())!.AsObject();
        result["git"]!["protected_branches"] = JsonSerializer.SerializeToNode(guards);
        return Canonical(JsonSerializer.SerializeToElement(result));
    }
    private static void RequireNarrower(JsonElement shared, JsonElement agent, string path)
    {
        if (shared.ValueKind == JsonValueKind.Number && (agent.ValueKind != JsonValueKind.Number || agent.GetInt64() > shared.GetInt64())) throw Invalid($"Agent setting '{path}' must not exceed or remove the shared limit.");
    }
    private static void MergeObject(JsonObject result, JsonElement patch)
    {
        foreach (var field in patch.EnumerateObject())
            if (field.Value.ValueKind == JsonValueKind.Object && result[field.Name] is JsonObject nested) MergeObject(nested, field.Value);
            else result[field.Name] = JsonNode.Parse(field.Value.GetRawText());
    }

    public static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value).GetRawText()))).ToLowerInvariant();
    public static JsonElement Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    private static ToolSettingsException Invalid(string message) => new("invalid_tool_settings", message);
    private static string Description(string key) => key switch
    {
        _ when key.EndsWith(".enabled", StringComparison.Ordinal) => "Offer this tool family to the Agent. Existing governance and approval limits continue to apply.",
        "shell.shell" => "Default shell. Auto selects an available shell for this host.",
        "shell.timeout_ms" => "Default foreground shell deadline in milliseconds. Explicit calls remain bounded by max_timeout_ms.",
        "shell.command_timeout_ms" => "Default command_run foreground wait in milliseconds.",
        "shell.max_timeout_ms" => "Maximum allowed shell deadline, including explicit call requests. Agent overrides cannot exceed the shared limit.",
        "shell.allow_long_lived" => "Allow commands to continue as managed terminal sessions.",
        "shell.max_output_chars" => "Maximum shell output characters returned per response stream, capped at 65,536.",
        "read.sentinel_line_limit" => "Lines returned by an unbounded read_file request. Zero permits the full file, subject to byte limits.",
        "read.directory_page_size" => "Default number of entries in a directory listing (at most 500).",
        "read.max_file_bytes" => "Maximum readable file size in bytes. Agent overrides cannot exceed or remove a shared limit; null is unlimited only when the shared limit is null.",
        "write.max_file_bytes" => "Maximum resulting file size in bytes after a write or edit. Agent overrides cannot exceed or remove a shared limit; null is unlimited only when the shared limit is null.",
        "write.create_parent_directories" => "Create missing parent directories when writing a new file.",
        "search.max_results" => "Default search result count when a call does not supply its own limit.",
        "search.timeout_ms" => "Search execution deadline in milliseconds. Null uses the outer Core call deadline.",
        "search.rg_path" => "Optional ripgrep executable path. Null uses the host's executable search.",
        "search.case_sensitive" => "Use case-sensitive matching by default.",
        "search.fixed_strings" => "Treat the search pattern as literal text by default.",
        "search.context_lines" => "Default number of surrounding lines for each text match.",
        "search.include_hidden" => "Include hidden files in search by default.",
        "search.respect_ignore_files" => "Honor ignore files in search by default. Symbolic links are never followed.",
        "git.timeout_ms" => "Default Git process deadline in milliseconds; operation safety limits still apply.",
        "git.max_output_chars" => "Maximum Git output characters returned by one operation.",
        "git.protected_branches" => "Branch names for which protected-branch safeguards apply. Agent names are added to the shared safeguards.",
        "git.log_limit" => "Default number of commits returned by git_log (at most 500).",
        "git.log_list_limit" => "Default number of commits returned by one git_log_list page.",
        "git.tool_budgets" => "Optional timeout_ms and max_output_chars per Git tool. Missing fields inherit the global Git budgets.",
        "web.timeout_ms" => "Default fetch deadline in milliseconds, between 1,000 and 60,000.",
        "web.max_bytes" => "Maximum downloaded response bytes, capped at 4 MiB.",
        "web.max_chars" => "Maximum text characters returned by a fetch, capped at 60,000.",
        "web.max_redirects" => "Maximum redirects allowed, at most three. Address checks apply at every hop.",
        "mcp.timeout_ms" => "Maximum wait in milliseconds for an MCP tool operation.",
        "mcp.server_resource_ids" => "Selected MCP resource UUIDs. Null selects all applicable enabled resources; [] selects none.",
        "skills.resource_ids" => "Selected Skill resource UUIDs. Null selects all applicable enabled resources; [] selects none.",
        "skills.search_depth" => "Managed Skill discovery depth is fixed at two directory levels.",
        "skills.max_file_bytes" => "Managed SKILL.md discovery is limited to 262,144 bytes per file.",
        "skills.max_skills" => "Maximum Skills included in the Agent's progressive-disclosure index.",
        "skills.max_index_chars" => "Maximum characters in the Skill index sent to the Agent.",
        _ => key
    };

    public static bool IsEnabled(JsonElement settings, string toolId)
    {
        var section = toolId switch
        {
            "read_file" or "ls" or "stat" => "read",
            "write_file" or "delete_file" or "replace_lines" or "replace_bytes" or "insert_line" or "insert_bytes" or "insert_byte" or "delete_line" or "delete_bytes" => "write",
            "shell" or "sandbox_status" or "sandbox_reset" or "command_run" or "command_wait" or "command_read" or "command_input" or "command_list" or "command_stop" => "shell",
            _ when toolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase) => "git",
            _ when toolId.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase) => "mcp",
            _ when toolId.StartsWith("web_", StringComparison.OrdinalIgnoreCase) => "web",
            _ when toolId.Contains("search", StringComparison.OrdinalIgnoreCase) || toolId == "glob_files" => "search",
            _ => null
        };
        return section is null || !settings.TryGetProperty(section, out var node) || !node.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False;
    }

    public static TimeSpan WireBudget(JsonElement settings, string toolId, JsonElement? parameters, TimeSpan fallback)
    {
        long? milliseconds = null;
        if (parameters is { ValueKind: JsonValueKind.Object } values && values.TryGetProperty("timeout_ms", out var explicitTimeout) && explicitTimeout.ValueKind == JsonValueKind.Number && explicitTimeout.TryGetInt64(out var explicitMs) && explicitMs > 0) milliseconds = explicitMs;
        else
        {
            var (section, field) = toolId switch
            {
                "shell" => ("shell", "timeout_ms"), "command_run" => ("shell", "command_timeout_ms"),
                "file_search" => ("search", "timeout_ms"), "web_fetch" => ("web", "timeout_ms"),
                _ when toolId.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase) => ("mcp", "timeout_ms"),
                _ when toolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase) => ("git", "timeout_ms"), _ => ("", "")
            };
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(section, out var node) && node.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number) milliseconds = value.GetInt64();
            if (section == "git" && settings.GetProperty("git").TryGetProperty("tool_budgets", out var budgets) && budgets.TryGetProperty(toolId, out var budget) && budget.TryGetProperty("timeout_ms", out var specific) && specific.ValueKind == JsonValueKind.Number) milliseconds = specific.GetInt64();
        }
        var seconds = Math.Clamp(Math.Ceiling(fallback.TotalSeconds), 1, 1830);
        if (milliseconds is { } ms) seconds = Math.Max(seconds, Math.Clamp(Math.Ceiling(ms / 1000d), 1, 1800));
        return TimeSpan.FromSeconds(Math.Min(1830, seconds + 30));
    }
}
