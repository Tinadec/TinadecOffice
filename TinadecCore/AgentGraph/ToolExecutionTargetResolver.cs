using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentGraph;

/// <summary>
/// Binds the resource ledger to the provider root used by ToolDispatcher. A
/// run-level worktree assignment is usable by its tasks; an environment must
/// explicitly name a local workspace root. URL-only remote environments remain
/// registered for orchestration but are rejected at the local provider boundary.
/// </summary>
internal sealed class ToolExecutionTargetResolver(
    IResourceLeaseService leases,
    IEnvironmentRegistry environments) : IToolExecutionTargetResolver
{
    private static readonly string[] RootKeys = ["workspace_root", "root_path", "path", "cwd", "working_directory"];

    public async Task<ToolExecutionTargetResolution> ResolveAsync(
        Guid sessionId,
        Guid runId,
        Guid taskId,
        string toolId,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        // Core virtual tools do not reach a provider. Worktree management must
        // run from the repository root so create/remove can manage the assigned
        // worktree itself rather than trying to start inside it.
        if (CoreVirtualToolPolicy.IsCoreVirtual(toolId)
            || toolId.Equals("git_worktree_create", StringComparison.OrdinalIgnoreCase)
            || toolId.Equals("git_worktree_remove", StringComparison.OrdinalIgnoreCase))
            return new ToolExecutionTargetResolution(null);

        var active = await leases.ListActiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var worktrees = active.Where(item => item.RunId == runId
                && item.Kind == ResourceLeaseKinds.Worktree
                && item.Purpose == ResourceLeasePurposes.Assignment)
            .ToArray();
        if (worktrees.Length > 1)
        {
            return new ToolExecutionTargetResolution(null,
                "This run holds more than one worktree assignment without a task binding; refusing to guess which provider root this task should use.");
        }

        var environmentLeases = active.Where(item => item.RunId == runId
                && item.Kind == ResourceLeaseKinds.Environment
                && item.Purpose == ResourceLeasePurposes.Assignment
                && (item.TaskId is null || item.TaskId == taskId))
            .ToArray();
        if (environmentLeases.Length > 1)
        {
            return new ToolExecutionTargetResolution(null,
                "This task matches more than one environment assignment; bind one environment to the task before invoking a provider tool.");
        }

        // A worktree is the most specific local execution root. If an environment
        // is also held, require the two facts to identify the same root rather than
        // silently dropping one of the assignments.
        if (worktrees is [{ } worktree])
        {
            var worktreeRoot = ExistingDirectory(worktree.ResourceKey, "worktree", worktree.Id);
            if (environmentLeases is [{ } environment])
            {
                var environmentRoot = await EnvironmentRootAsync(environment, projectRoot, cancellationToken).ConfigureAwait(false);
                if (environmentRoot.Error is not null) return environmentRoot;
                if (!SamePath(worktreeRoot, environmentRoot.Target!.RootPath))
                    return new ToolExecutionTargetResolution(null,
                        "The task's worktree and environment assignments resolve to different execution roots; refusing to combine them implicitly.");
            }
            return new ToolExecutionTargetResolution(new ToolExecutionTarget("worktree", worktree.ResourceKey, worktreeRoot, worktree.Id));
        }

        if (environmentLeases is [{ } onlyEnvironment])
            return await EnvironmentRootAsync(onlyEnvironment, projectRoot, cancellationToken).ConfigureAwait(false);

        return new ToolExecutionTargetResolution(null);
    }

    private async Task<ToolExecutionTargetResolution> EnvironmentRootAsync(
        ResourceLeaseInfo lease,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var key = EnvironmentKey(lease.ResourceKey);
        if (key is null)
            return new ToolExecutionTargetResolution(null, "The environment lease key is malformed; provider execution is refused.");
        var environment = (await environments.ListAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (environment is null)
            return new ToolExecutionTargetResolution(null, $"Environment '{key}' no longer exists; provider execution is refused.");
        if (environment.Kind is not (EnvironmentKinds.Local or EnvironmentKinds.Test or EnvironmentKinds.Terminal))
            return new ToolExecutionTargetResolution(null,
                $"Environment '{key}' is kind '{environment.Kind}', which has no local Tool Provider binding in this host.");

        var rootText = ReadRoot(environment.ConnectionJson);
        if (string.IsNullOrWhiteSpace(rootText))
            return new ToolExecutionTargetResolution(null,
                $"Environment '{key}' has no workspace_root/root_path/cwd in its connection; refusing to fall back to the project root.");
        var root = Path.GetFullPath(Path.IsPathRooted(rootText) ? rootText : Path.Combine(projectRoot, rootText));
        if (!Directory.Exists(root))
            return new ToolExecutionTargetResolution(null, $"Environment '{key}' execution root '{root}' does not exist.");
        return new ToolExecutionTargetResolution(new ToolExecutionTarget("environment", key, root, lease.Id));
    }

    private static string ExistingDirectory(string path, string kind, Guid leaseId)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root)) throw new InvalidOperationException($"Assigned {kind} lease {leaseId:N} points to missing directory '{root}'.");
        return root;
    }

    private static string? EnvironmentKey(string resourceKey)
    {
        var separator = resourceKey.LastIndexOf('#');
        return separator <= 0 ? null : resourceKey[..separator];
    }

    private static string? ReadRoot(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (RootKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    return property.Value.GetString()!.Trim();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
