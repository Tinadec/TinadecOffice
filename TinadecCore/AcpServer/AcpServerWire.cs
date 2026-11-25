using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AcpServer;

/// <summary>
/// Server-side ACP v1 wire contracts serialized through <see cref="AcpWire.Options"/>
/// (<c>PropertyNamingPolicy = null</c> + explicit <c>[JsonPropertyName]</c>, strict <c>camelCase</c>,
/// integer <c>protocolVersion: 1</c>).
/// </summary>
internal sealed record AcpServerInitializeResult(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("agentCapabilities")] AcpAgentCapabilities AgentCapabilities,
    [property: JsonPropertyName("agentInfo")] AcpAgentInfo AgentInfo);

internal sealed record AcpAgentInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("version")] string Version);

internal sealed record AcpServerSessionResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("modes")] AcpSessionModesState Modes,
    [property: JsonPropertyName("models")] AcpSessionModelsState Models);

internal sealed record AcpSessionModesState(
    [property: JsonPropertyName("currentModeId")] string CurrentModeId,
    [property: JsonPropertyName("availableModes")] IReadOnlyList<AcpModeOption> AvailableModes);

internal sealed record AcpModeOption(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null);

internal sealed record AcpSessionModelsState(
    [property: JsonPropertyName("currentModelId")] string CurrentModelId,
    [property: JsonPropertyName("availableModels")] IReadOnlyList<AcpModelOption> AvailableModels);

internal sealed record AcpModelOption(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null);

internal sealed record AcpTextContentPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal sealed record AcpMessageChunkUpdatePayload(
    [property: JsonPropertyName("sessionUpdate")] string SessionUpdate,
    [property: JsonPropertyName("content")] AcpTextContentPayload Content);

internal sealed record AcpCurrentModeUpdatePayload(
    [property: JsonPropertyName("sessionUpdate")] string SessionUpdate,
    [property: JsonPropertyName("currentModeId")] string CurrentModeId);

internal sealed record AcpToolLocation(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int? Line = null);

internal sealed record AcpToolCallContentItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] AcpTextContentPayload Content);

internal sealed record AcpToolCallUpdatePayload(
    [property: JsonPropertyName("sessionUpdate")] string SessionUpdate,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("locations")] IReadOnlyList<AcpToolLocation>? Locations = null,
    [property: JsonPropertyName("rawInput")] JsonElement? RawInput = null,
    [property: JsonPropertyName("rawOutput")] JsonElement? RawOutput = null,
    [property: JsonPropertyName("content")] IReadOnlyList<AcpToolCallContentItem>? Content = null);

internal sealed record AcpPermissionToolCallPayload(
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("locations")] IReadOnlyList<AcpToolLocation>? Locations = null,
    [property: JsonPropertyName("rawInput")] JsonElement? RawInput = null);

internal sealed record AcpPlanEntryPayload(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("priority")] string Priority,
    [property: JsonPropertyName("status")] string Status);

internal sealed record AcpPlanUpdatePayload(
    [property: JsonPropertyName("sessionUpdate")] string SessionUpdate,
    [property: JsonPropertyName("entries")] IReadOnlyList<AcpPlanEntryPayload> Entries);

/// <summary>
/// Maps TinadecCore / TinadecTools tool identifiers into the 9 normative ACP v1 tool kinds:
/// <c>read | edit | delete | move | search | execute | think | fetch | other</c>.
/// </summary>
internal static class AcpToolKindMapper
{
    public static readonly IReadOnlySet<string> ValidKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "read", "edit", "delete", "move", "search", "execute", "think", "fetch", "other"
    };

    public static string Normalize(string? toolId, string? fallbackTitle = null)
    {
        var raw = toolId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (raw.StartsWith("acp:", StringComparison.Ordinal))
        {
            raw = raw["acp:".Length..];
        }

        if (ValidKinds.Contains(raw)) return raw;

        return raw switch
        {
            "read_file" or "ls" or "stat" or "read_attachment" or "recall_evidence" or "plan_read"
                or "git_status" or "git_diff" or "git_log" or "git_branch_list" or "git_blame" or "git_file_at_revision"
                or "browser_snapshot" or "browser_tabs_list" or "browser_screenshot" or "browser_profiles_list"
                or "environment_list" => "read",

            "write_file" or "replace_lines" or "insert_line" or "spec_propose" or "write" or "patch" or "create" => "edit",

            "delete_line" or "delete_file" or "rm" or "unlink" or "git_worktree_remove" => "delete",

            "move_file" or "rename_file" or "mv" => "move",

            "file_search" or "grep" or "glob" or "find" or "mcp_search" or "mcp_list" => "search",

            "shell" or "command_run" or "bash" or "terminal" or "cmd" or "powershell" or "pwsh"
                or "git_commit" or "git_push" or "git_worktree_create" or "browser_run" => "execute",

            "think" or "correction_trace" or "plan_update" or "team_plan" or "team_tasks" => "think",

            "web_fetch" or "http_get" or "fetch_url" or "browser_navigate" => "fetch",

            _ => InferFromTitle(fallbackTitle)
        };
    }

    private static string InferFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "other";
        var t = title.Trim().ToLowerInvariant();
        if (t.StartsWith("read ", StringComparison.Ordinal)) return "read";
        if (t.StartsWith("write ", StringComparison.Ordinal) || t.StartsWith("edit ", StringComparison.Ordinal)) return "edit";
        if (t.StartsWith("delete ", StringComparison.Ordinal) || t.StartsWith("remove ", StringComparison.Ordinal)) return "delete";
        if (t.StartsWith("move ", StringComparison.Ordinal) || t.StartsWith("rename ", StringComparison.Ordinal)) return "move";
        if (t.StartsWith("search ", StringComparison.Ordinal)) return "search";
        if (t.StartsWith("run ", StringComparison.Ordinal) || t.StartsWith("exec", StringComparison.Ordinal) || t.Contains("shell", StringComparison.Ordinal)) return "execute";
        if (t.StartsWith("fetch ", StringComparison.Ordinal)) return "fetch";
        return "other";
    }
}

/// <summary>
/// Canonical ACP mode catalog (<c>auto | plan | spec | ask | vibe | agent</c>) plus published Core modes.
/// </summary>
internal static class AcpModeCatalog
{
    public static readonly IReadOnlyList<AcpModeOption> BuiltInModes =
    [
        new("agent", "Agent", "Standard governed interactive agent execution."),
        new("ask", "Ask", "Explicitly ask before every workspace mutation or command."),
        new("auto", "Auto", "Governed policy auto-approval within configured risk bounds."),
        new("plan", "Plan", "Structured plan-first execution before implementation."),
        new("spec", "Spec", "Specification-driven planning and confirmation workflow."),
        new("vibe", "Vibe", "High-autonomy full-access execution within the governed workspace.")
    ];

    private static readonly HashSet<string> BuiltInIds = BuiltInModes
        .Select(m => m.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveBuiltInSettings(
        string modeId,
        SpaceRunOptions? existingSpaceOptions,
        out string permissionMode,
        out SpaceRunOptions? spaceOptions,
        out bool clearSpaceOptions)
    {
        var normalized = modeId.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "agent":
                permissionMode = "default";
                spaceOptions = null;
                clearSpaceOptions = true;
                return true;

            case "ask":
                permissionMode = "ask";
                spaceOptions = null;
                clearSpaceOptions = true;
                return true;

            case "auto":
                permissionMode = "auto-approve";
                spaceOptions = null;
                clearSpaceOptions = true;
                return true;

            case "vibe":
                permissionMode = "full-access";
                spaceOptions = null;
                clearSpaceOptions = true;
                return true;

            case "plan":
                permissionMode = "ask";
                spaceOptions = (existingSpaceOptions ?? new SpaceRunOptions()) with
                {
                    PlanFirst = true,
                    SpecEnabled = false
                };
                clearSpaceOptions = false;
                return true;

            case "spec":
                permissionMode = "ask";
                spaceOptions = (existingSpaceOptions ?? new SpaceRunOptions()) with
                {
                    PlanFirst = true,
                    SpecEnabled = true
                };
                clearSpaceOptions = false;
                return true;

            default:
                permissionMode = "default";
                spaceOptions = null;
                clearSpaceOptions = false;
                return false;
        }
    }

    public static string ResolveCurrentModeId(
        string? permissionMode,
        SpaceRunOptions? spaceOptions,
        Guid? sessionModeVersionId,
        Guid? defaultModeVersionId,
        IReadOnlyList<AcpPublishedModeInfo> publishedModes)
    {
        if (spaceOptions?.SpecEnabled == true) return "spec";
        if (spaceOptions?.PlanFirst == true) return "plan";

        var perm = (permissionMode ?? "default").Trim().ToLowerInvariant();
        switch (perm)
        {
            case "auto-approve":
                return "auto";
            case "full-access":
                return "vibe";
            case "ask":
                return "ask";
        }

        if (sessionModeVersionId is { } activeVersion
            && defaultModeVersionId is { } defVersion
            && activeVersion != defVersion)
        {
            var matched = publishedModes.FirstOrDefault(m => m.ModeVersionId == activeVersion);
            if (matched is not null && !BuiltInIds.Contains(matched.Slug))
            {
                return matched.Slug;
            }
        }

        return "agent";
    }

    public static IReadOnlyList<AcpModeOption> BuildAvailableModes(IReadOnlyList<AcpPublishedModeInfo> publishedModes)
    {
        var list = new List<AcpModeOption>(BuiltInModes);
        foreach (var mode in publishedModes)
        {
            if (string.IsNullOrWhiteSpace(mode.Slug) || BuiltInIds.Contains(mode.Slug)) continue;
            if (list.Any(existing => string.Equals(existing.Id, mode.Slug, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new AcpModeOption(mode.Slug, mode.DisplayName, mode.Description));
        }

        return list;
    }
}

internal sealed record AcpPublishedModeInfo(
    Guid ModeId,
    Guid ModeVersionId,
    string Slug,
    string DisplayName,
    string? Description);
