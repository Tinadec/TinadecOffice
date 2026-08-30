using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// User-triggered tool transport. The request cwd is only a lookup key; the
/// provider always receives the registered project root and an explicit Git
/// repository_path when Git arguments are used.
/// </summary>
public static class DirectToolEndpoints
{
    public static IEndpointRouteBuilder MapDirectToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/tools/{toolId}/execute", async (
            string toolId,
            ToolDirectExecuteRequestDto? input,
            IWorkspaceRootResolver roots,
            IToolProvider provider,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(toolId))
                return Results.BadRequest(new { code = "invalid_request", message = "tool_id is required." });
            if (input is null)
                return Results.BadRequest(new { code = "invalid_request", message = "A tool request body is required." });
            if (string.IsNullOrWhiteSpace(input.Cwd))
                return Results.BadRequest(new { code = "workspace_root_required", message = "cwd must identify a registered project workspace." });

            var project = await roots.FindByRootAsync(input.Cwd, ct).ConfigureAwait(false);
            if (project is null)
                return Results.NotFound(new { code = "workspace_root_not_registered", message = "cwd does not identify an active project in the current workspace." });
            if (!Directory.Exists(project.RootPath))
                return Results.Conflict(new { code = "workspace_root_unavailable", message = "The registered project workspace root no longer exists." });

            ToolManifestDto manifest;
            try
            {
                manifest = await provider.GetManifestAsync(project.RootPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new { code = "tool_timeout", message = "The tool provider timed out while resolving its manifest." }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception)
            {
                return Results.Json(new { code = "tool_provider_unavailable", message = "The tool provider manifest is unavailable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!TryValidateManifest(manifest, out var manifestError))
                return Results.Json(new { code = manifestError!.Value.Code, message = manifestError.Value.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var requestedToolId = toolId.Trim();
            if (!IsObjectOrNull(input.Arguments))
                return Results.BadRequest(new { code = "invalid_request", message = "arguments must be a JSON object or null." });

            if (string.Equals(requestedToolId, "git_worktree_manager", StringComparison.OrdinalIgnoreCase))
                return await ExecuteGitFacadeAsync(requestedToolId, input.Arguments, project.RootPath, manifest, provider, ct).ConfigureAwait(false);

            return await ExecuteConcreteToolAsync(
                requestedToolId,
                requestedToolId,
                input.Arguments ?? EmptyObject(),
                project.RootPath,
                manifest,
                provider,
                ct).ConfigureAwait(false);
        });

        return app;
    }

    private static async Task<IResult> ExecuteConcreteToolAsync(
        string publicToolId,
        string providerToolId,
        JsonElement arguments,
        string root,
        ToolManifestDto manifest,
        IToolProvider provider,
        CancellationToken cancellationToken)
    {
        var call = await CallReadOnlyToolAsync(publicToolId, providerToolId, arguments, root, manifest, provider, cancellationToken).ConfigureAwait(false);
        if (call.Failure is not null) return call.Failure;

        var response = call.Response!;
        var result = ToResult(
            publicToolId,
            response,
            response.Result?.Clone() ?? EmptyObject(),
            call.Descriptor!,
            $"Tool '{publicToolId}' {(response.IsSuccess ? "completed" : "failed")}.");
        return Results.Json(result, statusCode: response.IsSuccess ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ExecuteGitFacadeAsync(
        string publicToolId,
        JsonElement? arguments,
        string root,
        ToolManifestDto manifest,
        IToolProvider provider,
        CancellationToken cancellationToken)
    {
        var action = ReadString(arguments, "action")?.ToLowerInvariant();
        if (action is null)
            return Results.BadRequest(new { code = "invalid_git_action", message = "git_worktree_manager requires an action." });

        switch (action)
        {
            case "status":
            {
                var call = await CallReadOnlyToolAsync(publicToolId, "git_status", arguments ?? EmptyObject(), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                return call.Failure ?? FacadeResult(publicToolId, action, [call.Response!], AdaptStatusData(root, call.Response!.Result));
            }
            case "diff_preview":
            {
                var status = await CallReadOnlyToolAsync(publicToolId, "git_status", arguments ?? EmptyObject(), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                if (status.Failure is not null) return status.Failure;
                var diff = await CallReadOnlyToolAsync(publicToolId, "git_diff", WithProperties(arguments, ("target", "all")), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                if (diff.Failure is not null) return diff.Failure;
                return FacadeResult(publicToolId, action, [status.Response!, diff.Response!], AdaptDiffData(root, status.Response!.Result, diff.Response!.Result, action, null, null));
            }
            case "diff_compare":
            {
                var baseRef = ReadString(arguments, "base_ref");
                if (string.IsNullOrWhiteSpace(baseRef))
                    return Results.BadRequest(new { code = "invalid_git_action", message = "diff_compare requires base_ref." });
                var headRef = ReadString(arguments, "head_ref") ?? "HEAD";
                var diff = await CallReadOnlyToolAsync(publicToolId, "git_diff", WithProperties(arguments, ("target", "ref_range"), ("head_ref", headRef)), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                if (diff.Failure is not null) return diff.Failure;
                return FacadeResult(publicToolId, action, [diff.Response!], AdaptDiffData(root, null, diff.Response!.Result, action, baseRef, headRef));
            }
            case "push_plan":
            {
                var readiness = await CallReadOnlyToolAsync(publicToolId, "git_push_readiness", arguments ?? EmptyObject(), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                if (readiness.Failure is not null) return readiness.Failure;
                var responses = new List<ToolWireResponseDto> { readiness.Response! };
                JsonElement? worktrees = null;
                JsonElement? commits = null;
                JsonElement? remotes = null;

                if (HasManifestTool(manifest, "git_worktree_list"))
                {
                    var call = await CallReadOnlyToolAsync(publicToolId, "git_worktree_list", arguments ?? EmptyObject(), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                    if (call.Failure is not null) return call.Failure;
                    responses.Add(call.Response!);
                    worktrees = call.Response!.Result;
                }
                if (HasManifestTool(manifest, "git_log"))
                {
                    var call = await CallReadOnlyToolAsync(publicToolId, "git_log", WithProperties(arguments, ("limit", 10)), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                    if (call.Failure is not null) return call.Failure;
                    responses.Add(call.Response!);
                    commits = call.Response!.Result;
                }
                if (HasManifestTool(manifest, "git_remote_list"))
                {
                    var call = await CallReadOnlyToolAsync(publicToolId, "git_remote_list", arguments ?? EmptyObject(), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                    if (call.Failure is not null) return call.Failure;
                    responses.Add(call.Response!);
                    remotes = call.Response!.Result;
                }

                return FacadeResult(publicToolId, action, responses, AdaptPushPlanData(root, readiness.Response!.Result, worktrees, commits, remotes));
            }
            case "branch_list":
            {
                var includeRemote = ReadBoolean(arguments, "all") ?? true;
                var call = await CallReadOnlyToolAsync(publicToolId, "git_branch_list", WithProperties(arguments, ("include_remote", includeRemote)), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                return call.Failure ?? FacadeResult(publicToolId, action, [call.Response!], AddRoot(call.Response!.Result, root));
            }
            case "worktrees":
            {
                var call = await CallReadOnlyToolAsync(publicToolId, "git_worktree_list", arguments ?? EmptyObject(), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                return call.Failure ?? FacadeResult(publicToolId, action, [call.Response!], AddRoot(call.Response!.Result, root));
            }
            case "log":
            {
                var call = await CallReadOnlyToolAsync(publicToolId, "git_log", arguments ?? EmptyObject(), root, manifest, provider, cancellationToken).ConfigureAwait(false);
                return call.Failure ?? FacadeResult(publicToolId, action, [call.Response!], AdaptLogData(call.Response!.Result, arguments));
            }
            default:
                return Results.BadRequest(new { code = "invalid_git_action", message = $"Unsupported git_worktree_manager action '{action}'." });
        }
    }

    private static async Task<(ToolManifestEntryDto? Descriptor, ToolWireResponseDto? Response, IResult? Failure)> CallReadOnlyToolAsync(
        string publicToolId,
        string providerToolId,
        JsonElement arguments,
        string root,
        ToolManifestDto manifest,
        IToolProvider provider,
        CancellationToken cancellationToken)
    {
        if (!TryGetManifestEntry(manifest, providerToolId, out var descriptor))
            return (null, null, Results.NotFound(new { code = "tool_not_found", message = $"Tool '{providerToolId}' was not found in the live manifest." }));

        if (descriptor!.MutatesWorkspace || descriptor.RequiresApproval)
        {
            var blocked = new CodeToolExecuteResultDto
            {
                ToolId = publicToolId,
                Status = "blocked",
                Summary = "Mutating user tools must use the Core UserToolAction approval path.",
                Evidence = ["state_owner: core", "approval_path: user_tool_action"],
                Data = EmptyObject(),
                RequiresApproval = true,
                ApprovalSummary = "Use /api/v1/user/tool-actions for governed writes."
            };
            return (descriptor, null, Results.Json(blocked, statusCode: StatusCodes.Status202Accepted));
        }

        var parameters = IsGitTool(providerToolId) ? InjectRepositoryPath(arguments, root) : arguments.Clone();
        try
        {
            var response = await provider.CallAsync(root, new ToolWireRequestDto
            {
                ToolId = providerToolId,
                SessionId = "user",
                Approved = false,
                Params = parameters
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (descriptor, response, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (descriptor, null, Results.Json(new { code = "tool_timeout", message = "The tool provider timed out." }, statusCode: StatusCodes.Status504GatewayTimeout));
        }
        catch (Exception)
        {
            return (descriptor, null, Results.Json(new { code = "tool_provider_unavailable", message = "The tool provider request failed." }, statusCode: StatusCodes.Status503ServiceUnavailable));
        }
    }

    private static IResult FacadeResult(string publicToolId, string action, IReadOnlyList<ToolWireResponseDto> responses, JsonElement data)
    {
        var failed = responses.FirstOrDefault(response => !response.IsSuccess);
        var success = failed is null;
        var result = new CodeToolExecuteResultDto
        {
            ToolId = publicToolId,
            Status = success ? "completed" : "failed",
            Summary = $"Git {action} {(success ? "completed" : "failed")}.",
            Evidence = ["state_owner: core", "workspace_root: registered_project", "provider_transport: stdio", $"git_action: {action}"],
            Data = data,
            RequiresApproval = false,
            Error = failed?.Error
        };
        return Results.Json(result, statusCode: success ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    private static CodeToolExecuteResultDto ToResult(
        string publicToolId,
        ToolWireResponseDto response,
        JsonElement data,
        ToolManifestEntryDto descriptor,
        string summary) => new()
        {
            ToolId = publicToolId,
            Status = response.IsSuccess ? "completed" : "failed",
            Summary = summary,
            Evidence = ["state_owner: core", "workspace_root: registered_project", "provider_transport: stdio", $"provider_tool_id: {descriptor.Id}"],
            Data = data,
            RequiresApproval = descriptor.RequiresApproval || descriptor.MutatesWorkspace,
            ApprovalSummary = descriptor.RequiresApproval || descriptor.MutatesWorkspace ? "Use /api/v1/user/tool-actions for governed writes." : null,
            Error = response.Error
        };

    private static bool TryValidateManifest(ToolManifestDto manifest, out (string Code, string Message)? error)
    {
        error = null;
        if (manifest.ProtocolVersion != 2)
        {
            error = ("tool_manifest_protocol_unsupported", "Tool Provider manifest v2 is required.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.ManifestHash)
            || !string.Equals(ToolManifestHasher.Compute(manifest.Tools), manifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            error = ("tool_manifest_hash_mismatch", "Tool Provider manifest identity is invalid.");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in manifest.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Id) || !seen.Add(tool.Id.Trim()))
            {
                error = ("tool_manifest_invalid", "Tool Provider manifest contains duplicate or empty tool ids.");
                return false;
            }
            if (tool.InputSchema.ValueKind is not JsonValueKind.Object
                || string.IsNullOrWhiteSpace(tool.Risk)
                || string.IsNullOrWhiteSpace(tool.RetrySafety))
            {
                error = ("tool_manifest_invalid", $"Tool '{tool.Id}' has incomplete execution policy metadata.");
                return false;
            }
        }
        return true;
    }

    private static bool TryGetManifestEntry(ToolManifestDto manifest, string toolId, out ToolManifestEntryDto? descriptor)
    {
        descriptor = manifest.Tools.FirstOrDefault(tool => string.Equals(tool.Id, toolId.Trim(), StringComparison.OrdinalIgnoreCase));
        return descriptor is not null;
    }

    private static bool HasManifestTool(ToolManifestDto manifest, string toolId) => TryGetManifestEntry(manifest, toolId, out _);

    private static bool IsGitTool(string toolId) => toolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase);

    private static JsonElement InjectRepositoryPath(JsonElement arguments, string root) =>
        WithProperties(arguments, ("repository_path", root));

    private static JsonElement WithProperties(JsonElement? source, params (string Name, object? Value)[] overrides)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source is JsonElement sourceValue && sourceValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in sourceValue.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        foreach (var (name, value) in overrides)
            values[name] = value;
        return JsonSerializer.SerializeToElement(values);
    }

    private static JsonElement AddRoot(JsonElement? value, string root) => WithProperties(value, ("git_root", root));

    private static JsonElement AdaptStatusData(string root, JsonElement? value)
    {
        var data = CopyObject(value);
        data["git_root"] = root;
        if (ReadProperty(value, "repository_root") is { } repositoryRoot)
            data["git_root"] = repositoryRoot;
        return SerializeObject(data);
    }

    private static JsonElement AdaptLogData(JsonElement? value, JsonElement? arguments)
    {
        var data = CopyObject(value);
        data["action"] = "log";
        if (ReadString(arguments, "ref") is { } reference) data["ref"] = reference;
        if (ReadInt(arguments, "limit") is { } limit) data["limit"] = limit;
        return SerializeObject(data);
    }

    private static JsonElement AdaptDiffData(string root, JsonElement? status, JsonElement? diff, string action, string? baseRef, string? headRef)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["git_root"] = root };
        CopyStatusFields(data, status);
        var sections = new List<object?>();
        var allFiles = new List<object?>();
        var diffText = new System.Text.StringBuilder();
        var truncated = false;
        var sectionNode = ReadProperty(diff, "sections");
        if (sectionNode is { ValueKind: JsonValueKind.Array })
        {
            foreach (var section in sectionNode.Value.EnumerateArray())
            {
                var kind = ReadString(section, "kind") ?? "working_tree";
                var sectionFiles = AdaptFiles(ReadProperty(section, "files"), ReadBoolean(section, "truncated") ?? false);
                allFiles.AddRange(sectionFiles);
                var sectionDiff = ReadString(section, "diff") ?? string.Empty;
                if (sectionDiff.Length > 0) diffText.Append(sectionDiff);
                var sectionTruncated = ReadBoolean(section, "truncated") ?? false;
                truncated |= sectionTruncated;
                sections.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = kind,
                    ["kind"] = kind,
                    ["title"] = kind switch { "staged" => "Staged changes", "ref_range" => "Reference range", _ => "Working tree changes" },
                    ["subtitle"] = kind switch { "staged" => "Changes already in the index", "ref_range" => "Changes between the selected refs", _ => "Tracked and untracked workspace changes" },
                    ["base_ref"] = ReadProperty(section, "base_ref"),
                    ["head_ref"] = ReadProperty(section, "head_ref"),
                    ["diff"] = sectionDiff,
                    ["files"] = sectionFiles,
                    ["file_count"] = sectionFiles.Count,
                    ["additions"] = SumFileDelta(sectionFiles, "additions"),
                    ["deletions"] = SumFileDelta(sectionFiles, "deletions"),
                    ["notices"] = Array.Empty<string>(),
                    ["truncated"] = sectionTruncated
                });
            }
        }
        data["sections"] = sections;
        data["files"] = allFiles;
        data["diff"] = diffText.ToString();
        data["truncated"] = truncated || (ReadBoolean(diff, "truncated") ?? false);
        data["action"] = action;
        if (baseRef is not null) data["base_ref"] = baseRef;
        if (headRef is not null) data["head_ref"] = headRef;
        return SerializeObject(data);
    }

    private static JsonElement AdaptPushPlanData(string root, JsonElement? readiness, JsonElement? worktrees, JsonElement? commits, JsonElement? remotes)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["git_root"] = root };
        var status = ReadProperty(readiness, "status");
        CopyStatusFields(data, status);
        data["push_ready"] = ReadBoolean(readiness, "ready") ?? false;
        data["needs_push"] = ReadBoolean(readiness, "needs_push") ?? false;
        data["push_blockers"] = ReadArray(readiness, "blockers");
        data["worktrees"] = ReadArray(worktrees, "worktrees");
        data["recent_commits"] = FormatCommits(commits);
        data["remotes"] = FormatRemotes(remotes);
        data["suggested_commands"] = new[] { "git status --short --branch", "git add <paths>", "git commit -m \"<message>\"" };
        return SerializeObject(data);
    }

    private static void CopyStatusFields(Dictionary<string, object?> data, JsonElement? status)
    {
        foreach (var name in new[] { "branch", "upstream", "ahead", "behind", "detached_head", "has_uncommitted_changes" })
        {
            if (ReadProperty(status, name) is { } value) data[name] = value;
        }
        if (ReadProperty(status, "repository_root") is { } repositoryRoot) data["git_root"] = repositoryRoot;
    }

    private static int SumFileDelta(List<object?> files, string key) =>
        files.Sum(file => (file as Dictionary<string, object?>)?[key] is int value ? value : 0);

    private static List<object?> AdaptFiles(JsonElement? files, bool truncated)
    {
        var result = new List<object?>();
        if (files is not { ValueKind: JsonValueKind.Array }) return result;
        foreach (var file in files.Value.EnumerateArray())
        {
            var status = ReadString(file, "status") ?? string.Empty;
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = ReadString(file, "new_path") ?? ReadString(file, "path") ?? string.Empty,
                ["previous_path"] = ReadProperty(file, "old_path") ?? ReadProperty(file, "previous_path"),
                ["change_type"] = ChangeType(status),
                ["additions"] = ReadInt(file, "additions") ?? 0,
                ["deletions"] = ReadInt(file, "deletions") ?? 0,
                ["binary"] = ReadBoolean(file, "is_binary") ?? ReadBoolean(file, "binary") ?? false,
                ["truncated"] = truncated
            });
        }
        return result;
    }

    private static string ChangeType(string status) => status.ToUpperInvariant() switch
    {
        "A" => "added",
        "D" => "deleted",
        "R" => "renamed",
        "C" => "copied",
        "U" => "conflicted",
        "M" => "modified",
        _ => string.IsNullOrWhiteSpace(status) ? "modified" : status.ToLowerInvariant()
    };

    private static List<string> FormatCommits(JsonElement? value)
    {
        var result = new List<string>();
        var commits = ReadProperty(value, "commits");
        if (commits is not { ValueKind: JsonValueKind.Array }) return result;
        foreach (var commit in commits.Value.EnumerateArray())
        {
            if (commit.ValueKind == JsonValueKind.String)
            {
                result.Add(commit.GetString() ?? string.Empty);
                continue;
            }
            var hash = ReadString(commit, "short_hash") ?? ReadString(commit, "hash") ?? string.Empty;
            var subject = ReadString(commit, "subject") ?? string.Empty;
            result.Add(string.IsNullOrWhiteSpace(subject) ? hash : $"{hash} {subject}");
        }
        return result;
    }

    private static List<string> FormatRemotes(JsonElement? value)
    {
        var result = new List<string>();
        var remotes = ReadProperty(value, "remotes");
        if (remotes is not { ValueKind: JsonValueKind.Array }) return result;
        foreach (var remote in remotes.Value.EnumerateArray())
        {
            var name = ReadString(remote, "name") ?? string.Empty;
            if (ReadString(remote, "fetch_url") is { } fetch) result.Add($"{name} {fetch} (fetch)");
            if (ReadString(remote, "push_url") is { } push) result.Add($"{name} {push} (push)");
        }
        return result;
    }

    private static Dictionary<string, object?> CopyObject(JsonElement? value)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (value is not { ValueKind: JsonValueKind.Object }) return data;
        foreach (var property in value.Value.EnumerateObject()) data[property.Name] = property.Value.Clone();
        return data;
    }

    private static JsonElement SerializeObject(Dictionary<string, object?> value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement? ReadProperty(JsonElement? source, string name)
    {
        if (source is not JsonElement value || value.ValueKind != JsonValueKind.Object) return null;
        return value.TryGetProperty(name, out var property) ? property.Clone() : null;
    }

    private static string? ReadString(JsonElement? source, string name)
    {
        var value = ReadProperty(source, name);
        return value is { ValueKind: JsonValueKind.String } stringValue ? stringValue.GetString() : null;
    }

    private static int? ReadInt(JsonElement? source, string name)
    {
        var value = ReadProperty(source, name);
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var result) ? result : null;
    }

    private static bool? ReadBoolean(JsonElement? source, string name)
    {
        var value = ReadProperty(source, name);
        return value is { ValueKind: JsonValueKind.True } ? true : value is { ValueKind: JsonValueKind.False } ? false : null;
    }

    private static List<JsonElement> ReadArray(JsonElement? source, string name)
    {
        var value = ReadProperty(source, name);
        return value is { ValueKind: JsonValueKind.Array }
            ? value.Value.EnumerateArray().Select(item => item.Clone()).ToList()
            : [];
    }

    private static bool IsObjectOrNull(JsonElement? value) => value is null || value.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null;

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement EmptyArray() => JsonSerializer.SerializeToElement(Array.Empty<object>());

}
