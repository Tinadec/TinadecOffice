using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.DmaEA;

/// <summary>
/// DmaEA module registrar. Registers the dual-layer agent orchestrator.
/// </summary>
public sealed class DmaEAModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "dma_ea";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<AgentControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<AgentControlDbContext>>();
        builder.Services.AddSingleton<IAgentOrchestrator, DualLayerAgentOrchestrator>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence", "lifecycle", "models", "memory", "context", "prompts", "loop_guard"],
            Capabilities = ["dual_layer_orchestration", "task_dispatch", "collaboration", "scheduling", "result_aggregation"],
            Language = "C#",
            MafPrimitives = ["agent", "workflow"],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}

/// <summary>
/// Dual-layer agent orchestrator.
/// Planning layer: proactive planning and supervision.
/// Execution layer: passive task execution.
/// Persists runs, task snapshots, and audit events through the lifecycle port;
/// messages are appended by the HTTP endpoint (orchestrator only reads the session).
/// </summary>
internal sealed class DualLayerAgentOrchestrator : IAgentOrchestrator
{
    private readonly ILifecycleManager _lifecycle;
    private readonly ISessionLocator _sessions;
    private readonly IDbContextFactory<AgentControlDbContext> _agents;
    private readonly IContentStore _content;
    private readonly IChatResolver _chatResolver;
    private readonly ITenantContextAccessor _tenant;
    private readonly ILogger<DualLayerAgentOrchestrator> _logger;

    public DualLayerAgentOrchestrator(
        ILifecycleManager lifecycle,
        ISessionLocator sessions,
        IDbContextFactory<AgentControlDbContext> agents,
        IContentStore content,
        IChatResolver chatResolver,
        ITenantContextAccessor tenant,
        ILogger<DualLayerAgentOrchestrator> logger)
    {
        _lifecycle = lifecycle;
        _sessions = sessions;
        _agents = agents;
        _content = content;
        _chatResolver = chatResolver;
        _tenant = tenant;
        _logger = logger;
    }

    public async Task<OrchestrationResult> OrchestrateAsync(string sessionId, string userGoal, string? triggerMessageId = null, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid)) throw new ArgumentException("Session id must be a valid Guid.", nameof(sessionId));
        var session = await _sessions.FindAsync(sessionGuid, cancellationToken).ConfigureAwait(false);
        if (session is null) throw new KeyNotFoundException("Session was not found.");
        var runId = await _lifecycle.StartRunAsync(sessionId, triggerMessageId, cancellationToken).ConfigureAwait(false);
        var runGuid = Guid.Parse(runId);
        await _lifecycle.AppendEventAsync(runGuid, "run.started", new { run_id = runGuid, session_id = sessionGuid }, "Run started.", cancellationToken: cancellationToken).ConfigureAwait(false);
        var ctx = new DmaeaRunContext
        {
            SessionId = sessionGuid,
            TenantId = session.TenantId,
            WorkspaceId = session.WorkspaceId,
            UserGoal = userGoal,
            TriggerMessageId = Guid.TryParse(triggerMessageId, out var mid) ? mid : Guid.Empty,
            RunId = runGuid
        };
        var nodeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        try
        {
            var agents = await LoadAgentsAsync(cancellationToken).ConfigureAwait(false);
            var planning = agents.FirstOrDefault(a => a.Layer == "planning" && a.Enabled);
            var executionAgents = agents.Where(a => a.Layer == "execution" && a.Enabled).ToList();
            PlannedTask[] tasks;
            if (planning is null)
            {
                _logger.LogInformation("No planning agent configured; running as a single task.");
                tasks = [new PlannedTask
                {
                    Title = userGoal,
                    Description = null,
                    SuccessCriteria = ["Task is complete when the goal is satisfied"],
                    Dependencies = [],
                    RequiredCapabilities = [],
                    Priority = 1,
                    Risk = "medium"
                }];
            }
            else
            {
                var planner = new PlanningAgent(_chatResolver, _logger);
                tasks = await planner.PlanAsync(ctx, agents, cancellationToken).ConfigureAwait(false);
            }
            var graphId = Guid.NewGuid();
            await _lifecycle.AppendEventAsync(runGuid, "task_graph.created", new { graph_id = graphId, run_id = runGuid, task_count = tasks.Length }, $"{tasks.Length} task(s) planned.", cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                var nodeId = Guid.NewGuid();
                nodeIds[task.Title] = nodeId;
                await _lifecycle.UpdateTaskSnapshotAsync(runGuid, new
                {
                    id = nodeId, graph_id = graphId, run_id = runGuid, session_id = sessionGuid,
                    title = task.Title, description = task.Description, status = "pending", priority = task.Priority,
                    risk = task.Risk, success_criteria = task.SuccessCriteria, dependencies = task.Dependencies,
                    required_capabilities = task.RequiredCapabilities,
                    created_at = DateTimeOffset.UtcNow, updated_at = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.AppendEventAsync(runGuid, "task.assigned", new { task_node_id = nodeId, run_id = runGuid, graph_id = graphId, title = task.Title, description = task.Description }, $"Task assigned: {task.Title}", taskId: nodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            foreach (var task in tasks)
            {
                var nodeId = nodeIds[task.Title];
                var agent = SelectExecutionAgent(executionAgents, task);
                if (agent is null)
                {
                    await _lifecycle.AppendEventAsync(runGuid, "step.result.created", new { task_node_id = nodeId, run_id = runGuid, status = "failed", summary = "No execution agent available.", evidence = Array.Empty<string>() }, "No execution agent available.", taskId: nodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var executor = new ExecutionAgent(_chatResolver, _logger);
                var result = await executor.ExecuteAsync(ctx, agent, task, nodeId, cancellationToken).ConfigureAwait(false);
                await _lifecycle.UpdateTaskSnapshotAsync(runGuid, new
                {
                    id = nodeId, graph_id = graphId, run_id = runGuid, session_id = sessionGuid,
                    title = task.Title, description = task.Description, status = result.Status, priority = task.Priority,
                    risk = task.Risk, success_criteria = task.SuccessCriteria, dependencies = task.Dependencies,
                    required_capabilities = task.RequiredCapabilities,
                    created_at = DateTimeOffset.UtcNow, updated_at = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.AppendEventAsync(runGuid, "step.result.created", new { task_node_id = nodeId, run_id = runGuid, agent_id = result.AgentId, status = result.Status, summary = result.Summary, evidence = result.Evidence }, result.Summary, taskId: nodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            await _lifecycle.CompleteRunAsync(runId, cancellationToken).ConfigureAwait(false);
            return new OrchestrationResult { RunId = runId, Success = true, Summary = "Dual-layer orchestration completed.", Warnings = [] };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId} failed: {Message}", runGuid, ex.Message);
            await _lifecycle.AppendEventAsync(runGuid, "run.failed", new { run_id = runGuid, error = ex.Message }, "Run failed: " + ex.Message, severity: "error", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new OrchestrationResult { RunId = runId, Success = false, Summary = ex.Message, Warnings = ["dma_ea run failed: " + ex.Message] };
        }
    }

    private async Task<IReadOnlyList<AgentDefinition>> LoadAgentsAsync(CancellationToken ct)
    {
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Agents.AsNoTracking().Where(a => a.TenantId == _tenant.Current.TenantId && a.WorkspaceId == _tenant.Current.WorkspaceId && a.DeletedAt == null).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<AgentDefinition>();
        foreach (var row in rows)
        {
            var version = await db.Versions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == row.CurrentVersionId, ct).ConfigureAwait(false);
            if (version is null) continue;
            var body = JsonSerializer.Deserialize<JsonElement>(await ReadContentAsync(_content, version.ContentReference, ct).ConfigureAwait(false));
            result.Add(new AgentDefinition
            {
                Id = row.Id,
                Name = row.Name,
                Layer = row.Layer,
                AgentType = row.AgentType,
                ModelRoutePurpose = body.TryGetProperty("model_route_purpose", out var r) ? r.GetString() : null,
                SystemPrompt = body.TryGetProperty("system_prompt", out var p) ? p.GetString() : null,
                AllowedTools = body.TryGetProperty("allowed_tools", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray() : [],
                Enabled = row.Enabled
            });
        }
        return result;
    }

    private static AgentDefinition? SelectExecutionAgent(IReadOnlyList<AgentDefinition> agents, PlannedTask task)
    {
        if (agents.Count == 0) return null;
        var matching = agents.Where(a => task.RequiredCapabilities.Length == 0 || task.RequiredCapabilities.All(rc => a.AllowedTools.Contains(rc, StringComparer.OrdinalIgnoreCase))).ToList();
        if (matching.Count > 0) return matching[0];
        return agents[0];
    }

    private static async Task<string> ReadContentAsync(IContentStore content, string reference, CancellationToken ct)
    {
        await using var stream = await content.OpenReadAsync(new ContentReference(reference, "", 0, "application/json"), ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
