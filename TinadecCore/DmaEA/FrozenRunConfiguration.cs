using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.DmaEA;

/// <summary>
/// Resolves the configuration for a new run exactly once. The returned value is a
/// self-contained document: execution code must never consult the hot-reload store
/// after admission.
/// </summary>
public interface IAgentRuntimeConfigurationResolver
{
    Task<FrozenRunConfigurationV1> ResolveAsync(
        Guid sessionId,
        string? applicationMode,
        string? agentMode,
        string? permissionMode,
        CancellationToken cancellationToken = default);
}

public sealed record FrozenRuntimeOverride(
    Guid Id,
    int Version,
    string ContentHash,
    DateTimeOffset CreatedAt);

/// <summary>
/// Version one is deliberately explicit rather than retaining a pointer to the
/// mutable TOML document. Secrets are represented only by external version ids.
/// </summary>
public sealed record FrozenRunConfigurationV1(
    string SchemaVersion,
    string BaselineHash,
    long BaselineVersion,
    string ApplicationMode,
    string AgentMode,
    string RuntimeProfileId,
    string PermissionMode,
    SpawnPolicy Spawn,
    SchedulingPolicy Scheduling,
    SupervisionPolicy Supervision,
    ContextPolicy Context,
    MemoryPolicy Memory,
    ToolRuntimePolicy Tools,
    IReadOnlyList<RuntimeAgentDefinition> OperationAgents,
    IReadOnlyList<RuntimeAgentDefinition> ExecutionAgents,
    FrozenRuntimeOverride? WorkspaceOverride,
    IReadOnlyList<RunConfigurationBinding> Bindings,
    string ToolManifestHash = "")
{
    /// <summary>
    /// The v2 tool declarations authorized at admission.  This is a content
    /// snapshot, not a live registry reference; workers must derive declarations
    /// from it and may never expand a wildcard against a later manifest.
    /// </summary>
    public IReadOnlyList<FrozenToolManifestEntry> ToolManifest { get; init; } = [];

    /// <summary>Protocol version of <see cref="ToolManifest"/> (zero for legacy bodies).</summary>
    public int ToolManifestProtocolVersion { get; init; }

    public string ToCanonicalJson() => JsonSerializer.Serialize(this, JsonOptions);

    [JsonIgnore]
    public string ContentHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson()))).ToLowerInvariant();

    public FrozenRunConfigurationWrite ToLifecycleWrite() => new(
        SchemaVersion,
        ToCanonicalJson(),
        Bindings);

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}

internal sealed class AgentRuntimeConfigurationResolver : IAgentRuntimeConfigurationResolver
{
    private readonly IAgentRuntimeConfiguration _baseline;
    private readonly IDbContextFactory<AgentControlDbContext> _agents;
    private readonly IContentStore _content;
    private readonly ISessionLocator _sessions;

    public AgentRuntimeConfigurationResolver(
        IAgentRuntimeConfiguration baseline,
        IDbContextFactory<AgentControlDbContext> agents,
        IContentStore content,
        ISessionLocator sessions)
    {
        _baseline = baseline;
        _agents = agents;
        _content = content;
        _sessions = sessions;
    }

    public async Task<FrozenRunConfigurationV1> ResolveAsync(
        Guid sessionId,
        string? applicationMode,
        string? agentMode,
        string? permissionMode,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        var snapshot = _baseline.Current;
        var (app, mode, profile) = snapshot.Resolve(applicationMode, agentMode);

        var overrideRow = await LoadLatestOverrideAsync(session, profile.Id, cancellationToken).ConfigureAwait(false);
        var effective = overrideRow is null
            ? new EffectivePolicy(snapshot.Spawn, snapshot.Scheduling, snapshot.Supervision, snapshot.Context, snapshot.Memory, snapshot.Tools, profile)
            : await ApplyOverrideAsync(snapshot, profile, overrideRow, cancellationToken).ConfigureAwait(false);

        var operation = ResolveAgents(snapshot, effective.Profile.OperationAgents, "operation");
        var execution = ResolveAgents(snapshot, effective.Profile.ExecutionAgents, "execution");
        var bindings = new List<RunConfigurationBinding>
        {
            // The TOML baseline itself has no relational version. A deterministic id
            // makes it visible to lifecycle audit without inventing a mutable record.
            new("agent_runtime_baseline", DeterministicGuid(snapshot.ContentHash), DeterministicGuid(snapshot.ContentHash + ":" + snapshot.Version), snapshot.ContentHash)
        };
        FrozenRuntimeOverride? frozenOverride = null;
        if (overrideRow is not null)
        {
            bindings.Add(new RunConfigurationBinding("workspace_runtime_override", overrideRow.Id, DeterministicGuid(overrideRow.Id + ":" + overrideRow.Version), overrideRow.ContentHash));
            frozenOverride = new FrozenRuntimeOverride(overrideRow.Id, overrideRow.Version, overrideRow.ContentHash, overrideRow.CreatedAt);
        }

        return new FrozenRunConfigurationV1(
            "frozen-run-configuration/v1",
            snapshot.ContentHash,
            snapshot.Version,
            app,
            mode,
            effective.Profile.Id,
            NormalizePermissionMode(permissionMode),
            effective.Spawn,
            effective.Scheduling,
            effective.Supervision,
            effective.Context,
            effective.Memory,
            effective.Tools,
            operation,
            execution,
            frozenOverride,
            bindings);
    }

    private async Task<RuntimeProfileOverrideRecord?> LoadLatestOverrideAsync(
        SessionReference session,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var db = await _agents.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ProfileOverrides.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId
                && item.WorkspaceId == session.WorkspaceId
                && item.ProfileId == profileId
                && item.Enabled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderByDescending(item => item.Version).ThenByDescending(item => item.CreatedAt).FirstOrDefault();
    }

    private async Task<EffectivePolicy> ApplyOverrideAsync(
        AgentRuntimeConfigurationSnapshot snapshot,
        RuntimeProfileDefinition profile,
        RuntimeProfileOverrideRecord row,
        CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(
            new ContentReference(row.ContentReference, row.ContentHash, row.ContentLength, "application/json"), cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Runtime profile override must be a JSON object.");
        }

        var root = document.RootElement;
        var profileOverride = ReadProfile(root, profile);
        return new EffectivePolicy(
            ReadSpawn(root, snapshot.Spawn),
            ReadScheduling(root, snapshot.Scheduling),
            ReadSupervision(root, snapshot.Supervision),
            ReadContext(root, snapshot.Context),
            ReadMemory(root, snapshot.Memory),
            ReadTools(root, snapshot.Tools),
            profileOverride);
    }

    private static IReadOnlyList<RuntimeAgentDefinition> ResolveAgents(
        AgentRuntimeConfigurationSnapshot snapshot,
        IReadOnlyList<string> ids,
        string layer)
    {
        var result = new List<RuntimeAgentDefinition>(ids.Count);
        foreach (var id in ids)
        {
            if (!snapshot.Agents.TryGetValue(id, out var agent))
            {
                throw new InvalidDataException($"Runtime profile references unknown agent '{id}'.");
            }
            if (!string.Equals(agent.Layer, layer, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Runtime profile agent '{id}' is not in the {layer} layer.");
            }
            result.Add(agent);
        }
        return result;
    }

    private static SpawnPolicy ReadSpawn(JsonElement root, SpawnPolicy fallback) => new(
        Positive(root, "spawn", "max_depth", fallback.MaxDepth, 0, 16),
        Positive(root, "spawn", "max_agents_per_run", fallback.MaxAgentsPerRun, 1, 128),
        Positive(root, "spawn", "max_parallel_workers", fallback.MaxParallelWorkers, 1, 32));

    private static SchedulingPolicy ReadScheduling(JsonElement root, SchedulingPolicy fallback) => new(
        Positive(root, "scheduling", "max_active_runs_per_session", fallback.MaxActiveRunsPerSession, 1, 32),
        Positive(root, "scheduling", "worker_retry_limit", fallback.WorkerRetryLimit, 0, 10),
        Boolean(root, "scheduling", "preserve_partial_results", fallback.PreservePartialResults));

    private static SupervisionPolicy ReadSupervision(JsonElement root, SupervisionPolicy fallback) => new(
        Boolean(root, "supervision", "required_before_final", fallback.RequiredBeforeFinal),
        Positive(root, "supervision", "max_revision_rounds", fallback.MaxRevisionRounds, 0, 10));

    private static ContextPolicy ReadContext(JsonElement root, ContextPolicy fallback) => new(
        Positive(root, "context", "default_token_budget", fallback.DefaultTokenBudget, 512, 262144),
        Positive(root, "context", "recent_message_limit", fallback.RecentMessageLimit, 1, 256),
        Boolean(root, "context", "optimistic_revision", fallback.OptimisticRevision));

    private static MemoryPolicy ReadMemory(JsonElement root, MemoryPolicy fallback) => new(
        Boolean(root, "memory", "candidate_only", fallback.CandidateOnly),
        Positive(root, "memory", "retrieval_limit", fallback.RetrievalLimit, 0, 64),
        Strings(root, "memory", "allowed_scopes", fallback.AllowedScopes),
        Strings(root, "memory", "allowed_kinds", fallback.AllowedKinds));

    private static ToolRuntimePolicy ReadTools(JsonElement root, ToolRuntimePolicy fallback) => new(
        Text(root, "tools", "provider", fallback.Provider),
        Boolean(root, "tools", "mutation_requires_approval", fallback.MutationRequiresApproval),
        Boolean(root, "tools", "serialize_workspace_writes", fallback.SerializeWorkspaceWrites),
        Positive(root, "tools", "default_timeout_seconds", fallback.DefaultTimeoutSeconds, 1, 1800),
        Positive(root, "tools", "max_tool_rounds", fallback.MaxToolRounds, 0, 32));

    private static RuntimeProfileDefinition ReadProfile(JsonElement root, RuntimeProfileDefinition fallback)
    {
        if (!root.TryGetProperty("profile", out var node) || node.ValueKind != JsonValueKind.Object) return fallback;
        return new RuntimeProfileDefinition(
            fallback.Id,
            Text(node, null, "activation_policy", fallback.ActivationPolicy),
            Strings(node, null, "operation_agents", fallback.OperationAgents),
            Strings(node, null, "execution_agents", fallback.ExecutionAgents),
            Boolean(node, null, "direct_answer_allowed", fallback.DirectAnswerAllowed));
    }

    private static int Positive(JsonElement root, string? section, string property, int fallback, int minimum, int maximum)
    {
        var node = Section(root, section);
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private static bool Boolean(JsonElement root, string? section, string property, bool fallback)
    {
        var node = Section(root, section);
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    private static string Text(JsonElement root, string? section, string property, string fallback)
    {
        var node = Section(root, section);
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : fallback;
    }

    private static IReadOnlyList<string> Strings(JsonElement root, string? section, string property, IReadOnlyList<string> fallback)
    {
        var node = Section(root, section);
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) return fallback;
        var values = value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim()).Where(item => item.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? fallback : values;
    }

    private static JsonElement Section(JsonElement root, string? section) => section is null ? root : root.TryGetProperty(section, out var value) ? value : default;

    private static string NormalizePermissionMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "deny" => "deny",
        "default" or "ask" or null or "" => "ask",
        // The first vertical slice deliberately has no approval bypass.
        "auto-approve" or "full-access" => "ask",
        _ => "ask"
    };

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record EffectivePolicy(
        SpawnPolicy Spawn,
        SchedulingPolicy Scheduling,
        SupervisionPolicy Supervision,
        ContextPolicy Context,
        MemoryPolicy Memory,
        ToolRuntimePolicy Tools,
        RuntimeProfileDefinition Profile);
}
