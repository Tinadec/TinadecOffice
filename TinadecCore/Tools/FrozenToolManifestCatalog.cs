using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Tools;

/// <summary>
/// Bridges DmaEA's persisted agent grants with the run-frozen manifest.  This is
/// intentionally a declaration catalog rather than a process registry: callers
/// cannot discover a later tool merely because it exists in a live child process.
/// </summary>
public sealed class FrozenToolManifestCatalog : IFrozenToolManifestCatalog
{
    private readonly ILifecycleManager _lifecycle;
    private readonly IAgentToolAuthorization? _agents;

    public FrozenToolManifestCatalog(ILifecycleManager lifecycle, IServiceProvider services)
    {
        _lifecycle = lifecycle;
        _agents = services.GetService(typeof(IAgentToolAuthorization)) as IAgentToolAuthorization;
    }

    public async Task<IReadOnlyList<FrozenToolManifestEntry>> ListAuthorizedAsync(
        Guid runId,
        Guid taskId,
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || taskId == Guid.Empty || agentInstanceId == Guid.Empty)
            throw new ArgumentException("run, task, and agent ids are required.");
        if (_agents is null)
            throw new InvalidOperationException("No agent authorization provider is registered.");

        var authorization = await _agents.GetAuthorizationAsync(runId, taskId, agentInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Agent instance is not authorized for this run/task.");
        if (authorization.RunId != runId || authorization.TaskId != taskId || authorization.AgentInstanceId != agentInstanceId)
            throw new UnauthorizedAccessException("Agent invocation scope does not match the requested run/task.");

        var run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
            throw new InvalidOperationException("Tool declarations are unavailable for a terminal run.");
        var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runId.ToString(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Frozen run configuration body is unavailable.");
        if (!string.Equals(frozen.ContentHash, run.FrozenConfigurationHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run frozen configuration hash does not match its durable binding.");

        var manifest = ToolInvocationScopeResolver.ReadFrozenToolManifest(frozen.Content);
        if (manifest.ProtocolVersion != 2 || string.IsNullOrWhiteSpace(manifest.ManifestHash))
            throw new InvalidOperationException("The run does not contain a valid frozen TinadecTools v2 manifest.");

        var allowed = authorization.AllowedTools;
        if (allowed.Count == 0) return [];
        var all = allowed.Any(value => string.Equals(value, "*", StringComparison.OrdinalIgnoreCase));
        return manifest.Tools
            .Where(tool => all || allowed.Any(value => string.Equals(value, tool.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(tool => tool with { InputSchema = tool.InputSchema.Clone(), ConfirmationFields = tool.ConfirmationFields.ToArray() })
            .ToArray();
    }
}
