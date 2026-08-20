using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.DmaEA;

/// <summary>
/// Core-owned runtime agent lifecycle service. It deliberately is not a tool: callers
/// receive a constrained child instance only after lineage and least-privilege checks.
/// </summary>
public interface IAgentInstanceService
{
    Task<RuntimeAgentInstance> CreateRootAsync(RuntimeAgentSeed seed, CancellationToken cancellationToken = default);
    Task<RuntimeAgentInstance> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeAgentInstance>> ListByRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task ReleaseRunInstancesAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<AgentCandidateRecord> CreateCandidateAsync(AgentCandidateProposal proposal, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentCandidateRecord>> ListCandidatesAsync(string? status = null, CancellationToken cancellationToken = default);
    Task<AgentCandidateRecord> DecideCandidateAsync(Guid candidateId, string decision, string? reason, CancellationToken cancellationToken = default);
}

public sealed record RuntimeAgentSeed(
    Guid SessionId,
    Guid RunId,
    string Id,
    string Layer,
    string Role,
    string ModelRoutePurpose,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedResources,
    int BudgetTokens = 0,
    Guid? ProfileId = null,
    Guid? TaskId = null);

public enum AgentCreationIntent
{
    Temporary = 0,
    PersistentCandidate = 1,
    PersistentProfile = 2
}

public sealed record AgentSpawnRequest(
    Guid ParentInstanceId,
    string Goal,
    IReadOnlyList<string> SuccessCriteria,
    IReadOnlyList<string> ContextSelectors,
    string? ModelRoutePurpose,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedResources,
    int BudgetTokens,
    Guid? TaskId = null,
    string Role = "worker",
    AgentSpawnLimits? Limits = null,
    AgentCreationIntent Intent = AgentCreationIntent.Temporary);

/// <summary>
/// Run-frozen limits for generated agents. The durable engine supplies these values
/// so a hot-reloaded configuration cannot alter an admitted run.
/// </summary>
public sealed record AgentSpawnLimits(
    int MaxDepth,
    int MaxAgentsPerRun,
    int MaxParallelWorkers);

public sealed record AgentCandidateProposal(
    Guid SourceRunId,
    Guid SourceInstanceId,
    string Name,
    string Layer,
    string AgentType,
    double ConfidenceScore,
    object Proposal,
    Guid? ProjectId = null);

public sealed record RuntimeAgentInstance(
    Guid Id,
    Guid RunId,
    Guid? ParentInstanceId,
    Guid? TaskId,
    string Layer,
    string Role,
    int GenerationDepth,
    bool Generated,
    string Status,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedResources,
    int BudgetTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed class AgentInstanceService : IAgentInstanceService, IAgentToolAuthorization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<AgentControlDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly IAgentRuntimeConfiguration _configuration;

    public AgentInstanceService(
        IDbContextFactory<AgentControlDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant,
        IAgentRuntimeConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
        _configuration = configuration;
    }

    public async Task<RuntimeAgentInstance> CreateRootAsync(RuntimeAgentSeed seed, CancellationToken cancellationToken = default)
    {
        if (seed.Layer is not ("operation" or "execution")) throw new ArgumentException("Root instance layer must be operation or execution.", nameof(seed));
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        var definition = new AgentInstanceDefinition(
            seed.Id, seed.Layer, seed.Role, seed.ModelRoutePurpose, Normalize(seed.Capabilities), Normalize(seed.AllowedTools),
            Normalize(seed.AllowedResources), Math.Max(0, seed.BudgetTokens), DirectUserOutput: seed.Id == "meeting", FormalMemoryWrite: false,
            Goal: null, SuccessCriteria: [], ContextSelectors: []);
        var stored = await PutDefinitionAsync(scope.TenantId, scope.WorkspaceId, definition, cancellationToken).ConfigureAwait(false);
        var row = new AgentInstanceRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, SessionId = seed.SessionId, RunId = seed.RunId,
            TaskNodeId = seed.TaskId, ProfileId = seed.ProfileId, Layer = seed.Layer, Role = seed.Role, GenerationDepth = 0,
            Generated = false, Status = "running", DefinitionReference = stored.Value, DefinitionHash = stored.Sha256, DefinitionLength = stored.Length,
            CreatedAt = now, UpdatedAt = now
        };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Instances.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToRuntime(row, definition);
    }

    public async Task<RuntimeAgentInstance> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Goal)) throw new ArgumentException("Spawn goal is required.", nameof(request));
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var parent = await db.Instances.SingleOrDefaultAsync(x => x.Id == request.ParentInstanceId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Parent agent instance was not found.");
        if (parent.Status is not ("created" or "running")) throw new InvalidOperationException("Parent agent instance is not active.");
        var parentDefinition = await ReadDefinitionAsync(parent, cancellationToken).ConfigureAwait(false);
        var intent = request.Intent;
        var requiredCapability = intent switch
        {
            AgentCreationIntent.PersistentProfile => "agent.create_profile",
            AgentCreationIntent.PersistentCandidate => "agent.create_persistent",
            _ => "agent.create_temporary"
        };
        if (!HasCapability(parentDefinition.Capabilities, requiredCapability))
            throw new UnauthorizedAccessException($"Parent instance is not allowed to perform '{requiredCapability}'.");
        // Legacy callers have no frozen run configuration yet. Full-duplex callers
        // always pass the values frozen at admission.
        var limits = request.Limits ?? new AgentSpawnLimits(
            _configuration.Current.Spawn.MaxDepth,
            _configuration.Current.Spawn.MaxAgentsPerRun,
            _configuration.Current.Spawn.MaxParallelWorkers);
        if (parent.GenerationDepth >= limits.MaxDepth)
            throw new InvalidOperationException("Agent spawn depth limit has been reached.");
        var isPersistent = intent is AgentCreationIntent.PersistentCandidate or AgentCreationIntent.PersistentProfile;
        if (intent == AgentCreationIntent.PersistentProfile && parent.Layer != "operation")
            throw new UnauthorizedAccessException("Only operation-layer agents may create persistent profiles.");
        if (!isPersistent && parent.Layer != "execution")
            throw new UnauthorizedAccessException("Only execution coordinators may create generated worker instances.");
        if (isPersistent && parent.Generated)
            throw new UnauthorizedAccessException("Generated workers cannot directly create persistent agents or profiles.");
        if (intent == AgentCreationIntent.PersistentCandidate && !HasCapability(parentDefinition.Capabilities, "agent.create_persistent") && !HasCapability(parentDefinition.Capabilities, "agent.candidate"))
            throw new UnauthorizedAccessException("Parent instance is not allowed to create persistent candidates.");
        var generatedCount = await db.Instances.CountAsync(x => x.RunId == parent.RunId && x.Generated, cancellationToken).ConfigureAwait(false);
        if (generatedCount >= limits.MaxAgentsPerRun)
            throw new InvalidOperationException("Agent spawn budget for this run has been reached.");
        var tools = Normalize(request.AllowedTools);
        var resources = Normalize(request.AllowedResources);
        if (!IsSubset(tools, parentDefinition.AllowedTools) || !IsSubset(resources, parentDefinition.AllowedResources))
            throw new UnauthorizedAccessException("Child agent permissions cannot exceed its parent instance.");
        if (!string.IsNullOrWhiteSpace(request.ModelRoutePurpose) && !string.Equals(request.ModelRoutePurpose, parentDefinition.ModelRoutePurpose, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Child agent cannot select a different model route from its parent.");
        var activeChildren = await db.Instances.CountAsync(x => x.RunId == parent.RunId && x.Generated && (x.Status == "created" || x.Status == "running"), cancellationToken).ConfigureAwait(false);
        if (activeChildren >= limits.MaxParallelWorkers)
            throw new InvalidOperationException("Concurrent generated-worker limit has been reached.");

        if (intent is AgentCreationIntent.PersistentCandidate or AgentCreationIntent.PersistentProfile)
        {
            var candidate = await CreateCandidateAsync(new AgentCandidateProposal(
                parent.RunId,
                parent.Id,
                string.IsNullOrWhiteSpace(request.Role) ? "generated.worker" : request.Role.Trim(),
                intent == AgentCreationIntent.PersistentProfile ? parent.Layer : "execution",
                string.IsNullOrWhiteSpace(request.Role) ? "worker" : request.Role.Trim(),
                0.5,
                new { goal = request.Goal.Trim(), successCriteria = Normalize(request.SuccessCriteria), contextSelectors = Normalize(request.ContextSelectors), allowedTools = tools, allowedResources = resources, intent = intent.ToString().ToLowerInvariant() }), cancellationToken).ConfigureAwait(false);
            if (intent == AgentCreationIntent.PersistentProfile)
            {
                candidate = await DecideCandidateAsync(candidate.Id, "promoted", "Persistent profile requested by authorized parent.", cancellationToken).ConfigureAwait(false);
            }
            var tempDefinition = new AgentInstanceDefinition(
                candidate.Name, candidate.Layer, candidate.AgentType, parentDefinition.ModelRoutePurpose, [], tools, resources, Math.Clamp(request.BudgetTokens, 0, parentDefinition.BudgetTokens),
                DirectUserOutput: false, FormalMemoryWrite: false, request.Goal.Trim(), Normalize(request.SuccessCriteria), Normalize(request.ContextSelectors));
            var tempStored = await PutDefinitionAsync(scope.TenantId, scope.WorkspaceId, tempDefinition, cancellationToken).ConfigureAwait(false);
            var tempRow = new AgentInstanceRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, SessionId = parent.SessionId, RunId = parent.RunId,
                TaskNodeId = request.TaskId ?? parent.TaskNodeId, ParentInstanceId = parent.Id, CreatedByProfileId = parent.ProfileId,
                Layer = candidate.Layer, Role = tempDefinition.Role, GenerationDepth = parent.GenerationDepth + 1, Generated = false, Status = "created",
                DefinitionReference = tempStored.Value, DefinitionHash = tempStored.Sha256, DefinitionLength = tempStored.Length, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Instances.Add(tempRow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToRuntime(tempRow, tempDefinition);
        }

        var definition = new AgentInstanceDefinition(
            "generated.worker", "execution", string.IsNullOrWhiteSpace(request.Role) ? "worker" : request.Role.Trim(),
            parentDefinition.ModelRoutePurpose, [], tools, resources, Math.Clamp(request.BudgetTokens, 0, parentDefinition.BudgetTokens),
            DirectUserOutput: false, FormalMemoryWrite: false, request.Goal.Trim(), Normalize(request.SuccessCriteria), Normalize(request.ContextSelectors));
        var stored = await PutDefinitionAsync(scope.TenantId, scope.WorkspaceId, definition, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var row = new AgentInstanceRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, SessionId = parent.SessionId, RunId = parent.RunId,
            TaskNodeId = request.TaskId ?? parent.TaskNodeId, ParentInstanceId = parent.Id, CreatedByProfileId = parent.ProfileId,
            Layer = "execution", Role = definition.Role, GenerationDepth = parent.GenerationDepth + 1, Generated = true, Status = "created",
            DefinitionReference = stored.Value, DefinitionHash = stored.Sha256, DefinitionLength = stored.Length, CreatedAt = now, UpdatedAt = now
        };
        db.Instances.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToRuntime(row, definition);
    }

    public async Task<IReadOnlyList<RuntimeAgentInstance>> ListByRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Instances.AsNoTracking()
            .Where(x => x.RunId == runId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        var result = new List<RuntimeAgentInstance>(rows.Count);
        foreach (var row in rows) result.Add(ToRuntime(row, await ReadDefinitionAsync(row, cancellationToken).ConfigureAwait(false)));
        return result;
    }

    public async Task<AgentToolAuthorization?> AuthorizeAsync(
        Guid runId,
        Guid taskId,
        Guid agentInstanceId,
        string toolId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || taskId == Guid.Empty || agentInstanceId == Guid.Empty || string.IsNullOrWhiteSpace(toolId))
            return null;
        var authorization = await GetAuthorizationAsync(runId, taskId, agentInstanceId, cancellationToken).ConfigureAwait(false);
        if (authorization is null
            || !authorization.AllowedTools.Any(value => string.Equals(value, "*", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, toolId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return authorization;
    }

    public async Task<AgentToolAuthorization?> GetAuthorizationAsync(
        Guid runId,
        Guid taskId,
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || taskId == Guid.Empty || agentInstanceId == Guid.Empty)
            return null;
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Instances.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == agentInstanceId
            && item.RunId == runId
            && item.TenantId == scope.TenantId
            && item.WorkspaceId == scope.WorkspaceId
            && item.ReleasedAt == null
            && (item.TaskNodeId == null || item.TaskNodeId == taskId), cancellationToken).ConfigureAwait(false);
        if (row is null || row.Status is not ("created" or "running")) return null;
        var definition = await ReadDefinitionAsync(row, cancellationToken).ConfigureAwait(false);
        return new AgentToolAuthorization(scope.TenantId, scope.WorkspaceId, row.SessionId, row.RunId,
            taskId, row.Id, definition.AllowedTools, definition.AllowedResources);
    }

    public async Task ReleaseRunInstancesAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Instances.Where(x => x.RunId == runId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.ReleasedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows) { row.Status = "released"; row.ReleasedAt = now; row.UpdatedAt = now; }
        if (rows.Count != 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentCandidateRecord> CreateCandidateAsync(AgentCandidateProposal proposal, CancellationToken cancellationToken = default)
    {
        if (proposal.Layer is not ("operation" or "execution")) throw new ArgumentException("Candidate layer must be operation or execution.", nameof(proposal));
        var scope = _tenant.Current;
        var source = await FindInstanceAsync(proposal.SourceInstanceId, scope, cancellationToken).ConfigureAwait(false);
        if (!source.Generated && source.Role != "experience_curator") throw new UnauthorizedAccessException("Only generated workers or evolution agents can propose candidates.");
        var stored = await PutJsonAsync(scope.TenantId, scope.WorkspaceId, "agent-candidate", proposal.Proposal, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var row = new AgentCandidateRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = proposal.ProjectId, SourceRunId = proposal.SourceRunId,
            SourceInstanceId = proposal.SourceInstanceId, GeneratedByInstanceId = proposal.SourceInstanceId, Name = proposal.Name.Trim(), Layer = proposal.Layer,
            AgentType = proposal.AgentType.Trim(), Status = "proposed", ConfidenceScore = Math.Clamp(proposal.ConfidenceScore, 0, 1),
            ProposalReference = stored.Value, ProposalHash = stored.Sha256, ProposalLength = stored.Length, CreatedByPrincipalId = scope.PrincipalId, CreatedAt = now, UpdatedAt = now
        };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Candidates.Add(row); await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task<IReadOnlyList<AgentCandidateRecord>> ListCandidatesAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Candidates.AsNoTracking().Where(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(item => item.Status == status.Trim().ToLowerInvariant());
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return rows;
    }

    public async Task<AgentCandidateRecord> DecideCandidateAsync(Guid candidateId, string decision, string? reason, CancellationToken cancellationToken = default)
    {
        if (decision is not ("promoted" or "rejected")) throw new ArgumentException("Candidate decision must be promoted or rejected.", nameof(decision));
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await db.Candidates.SingleOrDefaultAsync(x => x.Id == candidateId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Agent candidate was not found.");
        if (candidate.Status != "proposed") throw new InvalidOperationException("Candidate has already been decided.");
        candidate.Status = decision; candidate.DecisionReason = reason; candidate.DecidedByPrincipalId = scope.PrincipalId; candidate.UpdatedAt = DateTimeOffset.UtcNow;
        if (decision == "promoted")
        {
            var profileBody = await ReadCandidateProfileAsync(candidate, cancellationToken).ConfigureAwait(false);
            var stored = await PutJsonAsync(scope.TenantId, scope.WorkspaceId, "agent-profile", profileBody, cancellationToken).ConfigureAwait(false);
            var profile = new AgentProfileRecord
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                ProjectId = candidate.ProjectId,
                Scope = candidate.ProjectId is null ? "workspace" : "project",
                Name = candidate.Name,
                Layer = candidate.Layer,
                AgentType = candidate.AgentType,
                Enabled = true,
                IsBuiltIn = false,
                Revision = 1,
                CreatedByPrincipalId = scope.PrincipalId,
                UpdatedByPrincipalId = scope.PrincipalId,
                CreatedAt = candidate.UpdatedAt,
                UpdatedAt = candidate.UpdatedAt
            };
            var version = new AgentProfileVersionRecord
            {
                Id = Guid.NewGuid(),
                AgentId = profile.Id,
                Version = 1,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedByPrincipalId = scope.PrincipalId,
                CreatedAt = candidate.UpdatedAt
            };
            profile.CurrentVersionId = version.Id;
            candidate.PromotedAgentId = profile.Id;
            db.Agents.Add(profile);
            db.Versions.Add(version);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    private async Task<AgentInstanceRecord> FindInstanceAsync(Guid id, TenantContext scope, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Instances.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Agent instance was not found.");
    }

    private async Task<ContentReference> PutDefinitionAsync(Guid tenantId, Guid workspaceId, AgentInstanceDefinition definition, CancellationToken cancellationToken) =>
        await PutJsonAsync(tenantId, workspaceId, "agent-instance", definition, cancellationToken).ConfigureAwait(false);

    private async Task<ContentReference> PutJsonAsync(Guid tenantId, Guid workspaceId, string kind, object value, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions)));
        return await _content.PutAsync(new ContentWriteRequest(tenantId, workspaceId, kind, "application/json", stream), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentInstanceDefinition> ReadDefinitionAsync(AgentInstanceRecord row, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(row.DefinitionReference, row.DefinitionHash, row.DefinitionLength, "application/json"), cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<AgentInstanceDefinition>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Agent instance definition is invalid.");
    }

    private async Task<object> ReadCandidateProfileAsync(AgentCandidateRecord candidate, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(candidate.ProposalReference, candidate.ProposalHash, candidate.ProposalLength, "application/json"), cancellationToken).ConfigureAwait(false);
        var proposal = await JsonSerializer.DeserializeAsync<JsonElement>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (proposal.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Agent candidate proposal is invalid.");
        }

        if (proposal.TryGetProperty("direct_user_output", out var directUserOutput) && directUserOutput.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("Promoted agent candidates cannot grant direct_user_output.");
        }
        if (proposal.TryGetProperty("formal_memory_write", out var formalMemoryWrite) && formalMemoryWrite.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("Promoted agent candidates cannot grant formal_memory_write.");
        }

        return new
        {
            name = candidate.Name,
            layer = candidate.Layer,
            agent_type = candidate.AgentType,
            description = proposal.TryGetProperty("description", out var description) ? description.GetString() : null,
            model_route_purpose = proposal.TryGetProperty("model_route_purpose", out var route) ? route.GetString() : "chat",
            allowed_tools = proposal.TryGetProperty("allowed_tools", out var tools) ? tools : JsonSerializer.SerializeToElement(Array.Empty<string>()),
            capabilities = proposal.TryGetProperty("capabilities", out var capabilities) ? capabilities : JsonSerializer.SerializeToElement(Array.Empty<string>()),
            system_prompt = proposal.TryGetProperty("system_prompt", out var prompt) ? prompt.GetString() : null,
            direct_user_output = false,
            formal_memory_write = false,
            source_candidate_id = candidate.Id
        };
    }

    private static RuntimeAgentInstance ToRuntime(AgentInstanceRecord row, AgentInstanceDefinition definition) =>
        new(row.Id, row.RunId, row.ParentInstanceId, row.TaskNodeId, row.Layer, row.Role, row.GenerationDepth, row.Generated, row.Status,
            definition.Capabilities, definition.AllowedTools, definition.AllowedResources, definition.BudgetTokens, row.CreatedAt, row.UpdatedAt);
    private static bool HasCapability(IEnumerable<string> caps, string required) =>
        caps.Any(c => string.Equals(c, required, StringComparison.OrdinalIgnoreCase) || string.Equals(c, "agent.spawn", StringComparison.OrdinalIgnoreCase) && required == "agent.create_temporary");
    private static string[] Normalize(IEnumerable<string> values) => values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsSubset(IEnumerable<string> child, IEnumerable<string> parent)
    {
        var parentSet = parent.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (parentSet.Contains("*")) return true;
        return child.All(parentSet.Contains);
    }

    private sealed record AgentInstanceDefinition(
        string Id,
        string Layer,
        string Role,
        string ModelRoutePurpose,
        IReadOnlyList<string> Capabilities,
        IReadOnlyList<string> AllowedTools,
        IReadOnlyList<string> AllowedResources,
        int BudgetTokens,
        bool DirectUserOutput,
        bool FormalMemoryWrite,
        string? Goal,
        IReadOnlyList<string> SuccessCriteria,
        IReadOnlyList<string> ContextSelectors);
}
