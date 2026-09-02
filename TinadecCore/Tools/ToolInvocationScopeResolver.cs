using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Tools;

/// <summary>
/// Resolves the execution workspace from Core-owned records. The request contains
/// only identity references; it never carries a path that can override project
/// ownership or tenant isolation.
/// </summary>
public sealed class ToolInvocationScopeResolver : IToolInvocationScopeResolver
{
    private readonly ILifecycleManager _lifecycle;
    private readonly ISessionLocator _sessions;
    private readonly IToolProvider _provider;
    private readonly ITenantContextAccessor _tenant;
    private readonly IAgentToolAuthorization? _agents;

    public ToolInvocationScopeResolver(
        ILifecycleManager lifecycle,
        ISessionLocator sessions,
        IToolProvider provider,
        ITenantContextAccessor tenant,
        IServiceProvider services)
    {
        _lifecycle = lifecycle;
        _sessions = sessions;
        _provider = provider;
        _tenant = tenant;
        _agents = services.GetService(typeof(IAgentToolAuthorization)) as IAgentToolAuthorization;
    }

    public async Task<ToolInvocationScope> ResolveAsync(
        ToolInvocationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.RunId == Guid.Empty || request.TaskId == Guid.Empty || request.AgentInstanceId == Guid.Empty)
            throw new ArgumentException("run, task, and agent ids are required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ToolId)) throw new ArgumentException("tool id is required.", nameof(request));

        var run = await _lifecycle.GetRunStateAsync(request.RunId.ToString(), cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(run.SessionId, out var sessionId)
            || !Guid.TryParse(run.TenantId, out var tenantId)
            || !Guid.TryParse(run.WorkspaceId, out var workspaceId)
            || !string.Equals(run.RunId, request.RunId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException("Run scope was not found.");
        if (run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
            throw new InvalidOperationException("Tool execution is not allowed for a terminal run.");
        if (string.IsNullOrWhiteSpace(run.FrozenConfigurationHash))
            throw new InvalidOperationException("Tool execution requires a frozen run configuration.");

        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        if (session.TenantId != tenantId || session.WorkspaceId != workspaceId)
            throw new UnauthorizedAccessException("Session does not belong to the run tenant/workspace.");
        var project = await _sessions.FindProjectAsync(session.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project.TenantId != tenantId || project.WorkspaceId != workspaceId)
            throw new UnauthorizedAccessException("Project does not belong to the run tenant/workspace.");
        var root = Path.GetFullPath(project.RootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The project workspace root no longer exists.");

        if (_agents is null)
            throw new InvalidOperationException("No agent authorization provider is registered.");
        var authorization = await _agents.AuthorizeAsync(request.RunId, request.TaskId, request.AgentInstanceId, request.ToolId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Agent instance is not authorized for this run/task/tool.");
        if (authorization.TenantId != tenantId || authorization.WorkspaceId != workspaceId
            || authorization.SessionId != sessionId || authorization.RunId != request.RunId
            || authorization.TaskId != request.TaskId || authorization.AgentInstanceId != request.AgentInstanceId)
            throw new UnauthorizedAccessException("Agent invocation scope does not match the run/session.");
        if (!IsToolAllowed(authorization.AllowedTools, request.ToolId))
            throw new UnauthorizedAccessException($"Agent instance is not allowed to invoke '{request.ToolId}'.");
        if (!IsResourceAllowed(authorization.AllowedResources, root))
            throw new UnauthorizedAccessException("Agent instance is not allowed to access the project workspace.");

        var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(request.RunId.ToString(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Frozen run configuration body is unavailable.");
        if (!string.Equals(frozen.ContentHash, run.FrozenConfigurationHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run frozen configuration hash does not match its durable binding.");
        var frozenManifest = ReadFrozenToolManifest(frozen.Content);
        if (frozenManifest.ProtocolVersion != 2 || string.IsNullOrWhiteSpace(frozenManifest.ManifestHash))
        {
            throw new InvalidOperationException("The run does not contain a valid frozen TinadecTools v2 manifest.");
        }

        var liveManifest = await _provider.GetManifestAsync(root, cancellationToken).ConfigureAwait(false);
        if (liveManifest.ProtocolVersion != 2
            || !string.Equals(liveManifest.ManifestHash, frozenManifest.ManifestHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ToolManifestHasher.Compute(liveManifest.Tools), frozenManifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The TinadecTools manifest changed after run admission.");
        }

        var liveEntry = liveManifest.Tools.FirstOrDefault(item => string.Equals(item.Id, request.ToolId, StringComparison.OrdinalIgnoreCase));
        var frozenEntry = frozenManifest.Tools.FirstOrDefault(item => string.Equals(item.Id, request.ToolId, StringComparison.OrdinalIgnoreCase));
        if (liveEntry is null || frozenEntry is null || !ToolManifestHasher.Equivalent(liveEntry, frozenEntry))
        {
            throw new UnauthorizedAccessException($"Tool '{request.ToolId}' is not in the frozen authorized manifest.");
        }

        var policy = ReadFrozenToolPolicy(frozen.Content);

        return new ToolInvocationScope(
            tenantId,
            workspaceId,
            _tenant.Current.PrincipalId,
            project.ProjectId,
            sessionId,
            request.RunId,
            request.TaskId,
            request.AgentInstanceId,
            root,
            authorization.AllowedTools,
            authorization.AllowedResources,
            frozen.ContentHash,
            policy.DefaultTimeoutSeconds,
            policy.WorkerRetryLimit,
            policy.SerializeWorkspaceWrites,
            frozenManifest.Tools,
            frozenManifest.ManifestHash,
            policy.PermissionMode);
    }

    private static bool IsToolAllowed(IReadOnlyList<string> allowedTools, string toolId) =>
        allowedTools.Count == 0
            ? false
            : allowedTools.Any(value => string.Equals(value, "*", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, toolId, StringComparison.OrdinalIgnoreCase));

    private static bool IsResourceAllowed(IReadOnlyList<string> resources, string root)
    {
        if (resources.Count == 0) return true;
        foreach (var resource in resources)
        {
            if (string.Equals(resource, "workspace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(resource, "project", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var candidate = Path.GetFullPath(resource);
                if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception) when (resource.Length > 0)
            {
                // A malformed resource is simply not an authorization grant.
            }
        }
        return false;
    }

    internal static FrozenToolManifestBinding ReadFrozenToolManifest(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var protocol = ReadRootInt(root, "toolManifestProtocolVersion", "tool_manifest_protocol_version");
            var hash = ReadRootText(root, "toolManifestHash", "tool_manifest_hash");
            if (!TryGetProperty(root, out var toolsNode, "toolManifest", "tool_manifest")
                || toolsNode.ValueKind != JsonValueKind.Array)
            {
                return new FrozenToolManifestBinding(protocol, hash, []);
            }

            var entries = new List<FrozenToolManifestEntry>();
            foreach (var node in toolsNode.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Frozen tool manifest contains an invalid entry.");
                var id = ReadRootText(node, "id");
                var description = ReadRootText(node, "description") ?? string.Empty;
                var risk = ReadRootText(node, "risk") ?? string.Empty;
                var retrySafety = ReadRootText(node, "retrySafety", "retry_safety") ?? string.Empty;
                var requiresApproval = ReadRootBoolean(node, "requiresApproval", "requires_approval");
                var mutatesWorkspace = ReadRootBoolean(node, "mutatesWorkspace", "mutates_workspace");
                if (string.IsNullOrWhiteSpace(id)
                    || !TryGetProperty(node, out var schema, "inputSchema", "input_schema")
                    || schema.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Frozen tool manifest contains an incomplete entry.");
                }

                var confirmations = new List<string>();
                if (TryGetProperty(node, out var fields, "confirmationFields", "confirmation_fields")
                    && fields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in fields.EnumerateArray())
                    {
                        if (field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()))
                            confirmations.Add(field.GetString()!.Trim());
                    }
                }

                entries.Add(new FrozenToolManifestEntry(
                    id.Trim(),
                    description,
                    schema.Clone(),
                    risk,
                    mutatesWorkspace,
                    requiresApproval,
                    retrySafety,
                    confirmations));
            }

            return new FrozenToolManifestBinding(protocol, hash, entries);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Frozen run configuration is not valid JSON.", ex);
        }
    }

    private static bool TryGetProperty(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value)) return true;
        }

        value = default;
        return false;
    }

    private static string? ReadRootText(JsonElement root, params string[] names)
    {
        return TryGetProperty(root, out var value, names) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int ReadRootInt(JsonElement root, params string[] names)
    {
        return TryGetProperty(root, out var value, names) && value.TryGetInt32(out var parsed) ? parsed : 0;
    }

    private static bool ReadRootBoolean(JsonElement root, params string[] names)
    {
        return TryGetProperty(root, out var value, names)
            && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            && value.GetBoolean();
    }

    private static FrozenToolPolicy ReadFrozenToolPolicy(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            // Current frozen bodies use JsonSerializerDefaults.Web (camelCase),
            // while imported or older frozen bodies can retain TOML snake_case.
            // Deny and unknown modes must not execute a tool at all; passing
            // this gate never grants an approval by itself.
            var permissionMode = ReadString(document.RootElement, "permissionMode", "permission_mode");
            EnsurePermissionModeExecutable(permissionMode);
            var timeout = ReadInt(document.RootElement, "tools", "defaultTimeoutSeconds", "default_timeout_seconds", 120, 1, 1800);
            var retries = ReadInt(document.RootElement, "scheduling", "workerRetryLimit", "worker_retry_limit", 0, 0, 10);
            var serialize = ReadBoolean(document.RootElement, "tools", "serializeWorkspaceWrites", "serialize_workspace_writes", true);
            return new FrozenToolPolicy(permissionMode, timeout, retries, serialize);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Frozen run configuration is not valid JSON.", ex);
        }
    }

    internal static void EnsurePermissionModeExecutable(string? permissionMode)
    {
        var normalized = permissionMode?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized)
            && normalized is not ("ask" or "default" or "auto-approve" or "full-access"))
        {
            throw new UnauthorizedAccessException("Frozen run permission mode does not permit tool execution.");
        }
    }

    private static string? ReadString(JsonElement root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }

    private static int ReadInt(JsonElement root, string section, string property, string legacyProperty, int fallback, int minimum, int maximum)
    {
        if (root.TryGetProperty(section, out var node) && node.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { property, legacyProperty })
            {
                if (node.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed))
                    return Math.Clamp(parsed, minimum, maximum);
            }
        }
        return fallback;
    }

    private static bool ReadBoolean(JsonElement root, string section, string property, string legacyProperty, bool fallback)
    {
        if (root.TryGetProperty(section, out var node) && node.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { property, legacyProperty })
            {
                if (node.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return value.GetBoolean();
            }
        }
        return fallback;
    }

    private sealed record FrozenToolPolicy(string? PermissionMode, int DefaultTimeoutSeconds, int WorkerRetryLimit, bool SerializeWorkspaceWrites);

    internal sealed record FrozenToolManifestBinding(
        int ProtocolVersion,
        string? ManifestHash,
        IReadOnlyList<FrozenToolManifestEntry> Tools);
}
