using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Abstractions.Ports;

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
        string? permissionMode,
        SessionModelOverride? meetingModelOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one explicitly selected mode version without consulting a later
    /// mutable session binding. Used by durable queued interactions.
    /// </summary>
    Task<FrozenRunConfigurationV1> ResolveForModeAsync(
        Guid sessionId,
        Guid modeVersionId,
        string? permissionMode,
        SessionModelOverride? meetingModelOverride = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Version one is deliberately explicit rather than retaining a pointer to the
/// mutable TOML document. Secrets are represented only by external version ids.
/// Identity is the mode version: `ModeVersionId`/`RuntimeProfileId` carry the
/// frozen relational mode (`mode:{modeId}:{version}`), and the legacy
/// application-mode/agent-mode string pair is gone (schema v3).
/// </summary>
public sealed record FrozenRunConfigurationV1(
    string SchemaVersion,
    string BaselineHash,
    long BaselineVersion,
    Guid ModeVersionId,
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

    /// <summary>
    /// Policy versions captured at admission.  An empty list is meaningful when
    /// <see cref="PolicySnapshotHash"/> is populated: it means the run admitted
    /// with no active policy bundles, rather than asking Governance to reload the
    /// current policy set later.
    /// </summary>
    public string PolicySnapshotHash { get; init; } = "";
    public IReadOnlyList<FrozenPolicyBundle> PolicyBundles { get; init; } = [];

    /// <summary>
    /// Operational-role trigger gates frozen at admission. Runs admitted before
    /// the trigger chain existed deserialize with the disabled default.
    /// </summary>
    public TriggersPolicy Triggers { get; init; } = TriggersPolicy.Disabled;

    /// <summary>
    /// Lane master switch and ceilings frozen at admission. Runs admitted before
    /// the orchestration section existed deserialize with lanes disabled, so an
    /// old checkpoint resumes with the pre-lane single-lane semantics.
    /// </summary>
    public OrchestrationPolicy Orchestration { get; init; } = OrchestrationPolicy.Disabled;

    /// <summary>
    /// Declared mode graph frozen at admission (DmaEA graph orchestration).
    /// Schema v2 freezes a Graph section for EVERY mode: free_form (no declared
    /// edges) is itself a tier and its single-director shape is enforced by the
    /// engine. The tier is derived ONLY from frozen inputs at admission; recovery
    /// never re-derives it. (v1 freezes omitted this section for edge-less modes;
    /// the v1→v2 clean break supersedes those bodies — recovery fail-closes with
    /// run_schema_superseded instead of double-reading.)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FrozenGraph? Graph { get; init; }

    /// <summary>
    /// The workspace this run is bound to, frozen at admission (root absolute
    /// path, extra read-only roots, git facts, top-level listing, path contract).
    /// Null means "this run has no workspace": projectless free-conversation
    /// sessions, and every body frozen before the section existed. Recovery treats
    /// a missing section as no workspace rather than failing the schema gate, and
    /// it never re-resolves the binding — the model prompt and the tool boundary
    /// both read this section as the single authority for "where am I".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FrozenWorkspaceBinding? Workspace { get; init; }

    /// <summary>Optional trusted communication binding. Absent on ordinary runs; never caller-supplied HTTP data.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TinaChatInputBinding? TinaChatInput { get; init; }

    /// <summary>
    /// The frozen-body schema this Core writes and reads. v2 introduced the
    /// always-present Graph section (free_form tier on disk) and spawnable
    /// templates; v3 replaced the application-mode/agent-mode string pair with
    /// the frozen mode version identity and dropped the dead workspace-profile
    /// override section. Older bodies are superseded and fail closed on resume.
    /// </summary>
    public const string CurrentSchemaVersion = "frozen-run-configuration/v3";

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

/// <summary>Enforcement tiers for the declared-graph orchestration. The tier decides which dispatch/spawn authority the engine consults; it is derived only at admission.</summary>
public static class FrozenGraphTiers
{
    /// <summary>Declared edges walked; conversation identity holds no dispatchable-worker spawn authority (or holds only agent.create_persistent). Spawn intent is denied (graph_tier_spawn_denied).</summary>
    public const string Deterministic = "deterministic";

    /// <summary>Declared edges walked; conversation identity holds agent.create_temporary (or the agent.spawn alias) and may additionally spawn workers from the frozen spawnable-template whitelist.</summary>
    public const string SelfDispatch = "self_dispatch";

    /// <summary>No declared dispatch edges — single-director free-form orchestration; the director spawns workers from the frozen spawnable-template set and edges are prompt material only.</summary>
    public const string FreeForm = "free_form";

    /// <summary>
    /// The conversation identity holds a TOOL SURFACE OF ITS OWN: the master does the
    /// work itself (read/write/exec in one continuous tool loop, the classic
    /// single-agent CLI shape) while keeping full spawn authority to hand work to
    /// sub-agents.
    ///
    /// Outranks every other branch. "The master executes" is a statement about WHO
    /// works, not about topology, so it holds whether or not the mode declares
    /// dispatch edges — a solo mode may keep a declared graph (dispatch along it) or
    /// none (dispatch freely). Derived from the frozen operation roster's declared
    /// tool_scope, never from a bespoke marker field, so no pack has to opt in
    /// explicitly and no digest can drift silently.
    /// </summary>
    public const string SoloDispatch = "solo_dispatch";
}

/// <summary>
/// The frozen declared graph of the session's published mode. Edges are a
/// communication topology, not a DAG — mutual dispatch/result pairs are legal.
/// </summary>
public sealed record FrozenGraph(
    string Tier,
    string? ConversationTemplateSlug,
    string? ConversationNodeKey,
    IReadOnlyList<FrozenGraphNode> Nodes,
    IReadOnlyList<DeclaredGraphEdge> Edges)
{
    /// <summary>
    /// Worker templates the conversation identity may spawn outside the declared
    /// roster (self_dispatch whitelist; free_form director's buildable set),
    /// hash-pinned at admission with their tool ceiling already intersected
    /// against the frozen tool manifest.
    /// </summary>
    public IReadOnlyList<FrozenSpawnableTemplate> SpawnableTemplates { get; init; } = [];
}

public sealed record FrozenGraphNode(string NodeKey, string AgentSlug, string Layer, bool IsConversation)
{
    /// <summary>Workspace-relative path grants frozen from the node's binding envelope resources (empty = deny path-targeting tools; WS-4 resource envelope).</summary>
    public IReadOnlyList<FrozenResourceGrant> ResourceGrants { get; init; } = [];
}

/// <summary>A worker template the conversation identity may spawn, with its frozen tool ceiling.</summary>
public sealed record FrozenSpawnableTemplate(
    string Slug,
    Guid AgentDefinitionId,
    Guid AgentVersionId,
    string VersionHash,
    string Role,
    IReadOnlyList<string> ToolCeiling,
    IReadOnlyList<string> Capabilities)
{
    /// <summary>Workspace-relative path grants frozen from the template's binding envelope resources (empty = no workspace authorization).</summary>
    public IReadOnlyList<FrozenResourceGrant> ResourceGrants { get; init; } = [];
}

internal sealed class AgentRuntimeConfigurationResolver : IAgentRuntimeConfigurationResolver
{
    private readonly IAgentRuntimeConfiguration _baseline;
    private readonly IFormalModeResolver _formal;
    private readonly IAgentModelResolver _models;
    private readonly ISessionLocator _sessions;
    private readonly IPolicySnapshotProvider? _policySnapshots;

    public AgentRuntimeConfigurationResolver(
        IAgentRuntimeConfiguration baseline,
        IFormalModeResolver formal,
        IAgentModelResolver models,
        ISessionLocator sessions,
        IPolicySnapshotProvider? policySnapshots = null)
    {
        _baseline = baseline;
        _formal = formal;
        _models = models;
        _sessions = sessions;
        _policySnapshots = policySnapshots;
    }

    public Task<FrozenRunConfigurationV1> ResolveAsync(
        Guid sessionId,
        string? permissionMode,
        SessionModelOverride? meetingModelOverride = null,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(sessionId, modeVersionIdOverride: null, permissionMode, meetingModelOverride, cancellationToken);

    public Task<FrozenRunConfigurationV1> ResolveForModeAsync(
        Guid sessionId,
        Guid modeVersionId,
        string? permissionMode,
        SessionModelOverride? meetingModelOverride = null,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(sessionId, modeVersionId, permissionMode, meetingModelOverride, cancellationToken);

    private async Task<FrozenRunConfigurationV1> ResolveCoreAsync(
        Guid sessionId,
        Guid? modeVersionIdOverride,
        string? permissionMode,
        SessionModelOverride? meetingModelOverride,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        var modeVersionId = modeVersionIdOverride ?? session.ModeVersionId;
        if (modeVersionId is null)
            throw new RunAdmissionException("agent_mode_not_configured", "A published default Agent Mode must be configured before creating a run.");
        // The workspace is resolved exactly once, here, from Core-owned records and
        // a cheap filesystem probe. Everything downstream (prompt, tool boundary,
        // resource claims) reads the frozen section instead of re-resolving it.
        var workspace = await WorkspaceBindingFactory.TryCreateAsync(_sessions, session, cancellationToken).ConfigureAwait(false);
        var snapshot = _baseline.Current;
        var policySnapshot = _policySnapshots is null
            ? null
            : await _policySnapshots.CaptureAsync(session.TenantId, session.WorkspaceId, cancellationToken).ConfigureAwait(false);

        // Policy budgets remain in the runtime baseline. Agent identity and topology
        // are always frozen from the session's published relational ModeVersion.
        var bindings = new List<RunConfigurationBinding>
        {
            // The TOML baseline itself has no relational version. A deterministic id
            // makes it visible to lifecycle audit without inventing a mutable record.
            new("agent_runtime_baseline", DeterministicGuid(snapshot.ContentHash), DeterministicGuid(snapshot.ContentHash + ":" + snapshot.Version), snapshot.ContentHash)
        };
        var relational = await _formal.ResolveRosterForModeAsync(sessionId, modeVersionId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Agent mode version '{modeVersionId}' could not be resolved.");
        var operation = relational.Operation.Select(ToRuntimeAgentDefinition).ToArray();
        var execution = relational.Execution.Select(ToRuntimeAgentDefinition).ToArray();
        operation = (await FreezeModelPlansAsync(operation, sessionId, modeVersionId.Value, session.ConversationTemplateSlug, meetingModelOverride, cancellationToken).ConfigureAwait(false)).ToArray();
        execution = (await FreezeModelPlansAsync(execution, sessionId, modeVersionId.Value, session.ConversationTemplateSlug, meetingModelOverride, cancellationToken).ConfigureAwait(false)).ToArray();

        // Workspace baseline: a bound workspace gives every agent that holds a
        // provider tool face the whole-root read level, so read-only work never
        // fails for "no workspace authorization". An envelope that declared
        // grants keeps them verbatim (narrowing only), and write stays
        // envelope-declared plus approval-gated.
        operation = operation.Select(agent => agent with
        {
            ResourceGrants = WorkspaceGrantDefaults.Resolve(workspace, agent.ResourceGrants, agent.AllowedTools)
        }).ToArray();
        execution = execution.Select(agent => agent with
        {
            ResourceGrants = WorkspaceGrantDefaults.Resolve(workspace, agent.ResourceGrants, agent.AllowedTools)
        }).ToArray();

        // Declared graph freeze (DmaEA graph orchestration): every mode freezes a
        // Graph section under schema v2 — free_form is a tier on disk, not the
        // absence of one. The tier is derived ONLY here from frozen inputs;
        // recovery never re-derives it (configuration.Graph is the single
        // authority). Spawnable tool ceilings are finished in the coordinator's
        // manifest freeze (the execution ceiling is not resolvable here yet).
        var grantsBySlug = operation.Concat(execution)
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ResourceGrants, StringComparer.OrdinalIgnoreCase);
        var graph = new FrozenGraph(
            DeriveGraphTier(operation, relational.HasDeclaredEdges, relational.ConversationTemplateSlug),
            relational.ConversationTemplateSlug,
            relational.ConversationNodeKey,
            relational.GraphNodes.Select(node => new FrozenGraphNode(
                node.NodeKey,
                node.AgentSlug,
                node.Layer,
                string.Equals(node.NodeKey, relational.ConversationNodeKey, StringComparison.Ordinal))
            {
                ResourceGrants = grantsBySlug.TryGetValue(node.AgentSlug, out var grants) ? grants : []
            }).ToArray(),
            relational.Edges)
        {
            SpawnableTemplates = relational.SpawnableTemplates.Select(template => new FrozenSpawnableTemplate(
                template.Slug,
                template.AgentDefinitionId,
                template.AgentVersionId,
                template.VersionHash,
                template.Role,
                template.ToolScope,
                template.Capabilities)
            {
                ResourceGrants = WorkspaceGrantDefaults.Resolve(workspace, template.ResourceGrants, template.ToolScope)
            }).ToArray()
        };

        // Gate 3 (run freeze): conversation identity lock + operation deny floor,
        // fail-closed at admission. Sessions frozen before ConversationIdentity
        // existed keep the legacy literal-meeting semantics (null identity).
        RunFreezeGate.Validate(
            session.ConversationNodeKey is null
                ? null
                : new RunFreezeGate.ConversationIdentity(session.ConversationNodeKey, session.ConversationTemplateSlug ?? string.Empty),
            operation,
            execution,
            graph,
            snapshot.Orchestration.LanesEnabled);

        var runtimeProfileId = relational.RuntimeProfileId;
        bindings.Add(new RunConfigurationBinding("agent_mode_version", relational.AgentModeId, relational.ModeVersionId, relational.TopologyHash ?? ""));
        foreach (var agent in relational.Operation.Concat(relational.Execution))
        {
            if (agent.AgentDefinitionId is { } definitionId && agent.AgentVersionId is { } versionId)
            {
                bindings.Add(new RunConfigurationBinding("agent_version", definitionId, versionId, agent.VersionContentHash));
            }
            if (agent.PromptPipelineId is { } pipelineId && agent.PromptVersionId is { } promptVersionId)
            {
                if (!bindings.Any(binding => binding.ConfigurationKind == "prompt_version" && binding.ConfigurationVersionId == promptVersionId))
                {
                    bindings.Add(new RunConfigurationBinding("prompt_version", pipelineId, promptVersionId, agent.PromptVersionContentHash));
                }
            }
        }

        var frozen = new FrozenRunConfigurationV1(
            FrozenRunConfigurationV1.CurrentSchemaVersion,
            snapshot.ContentHash,
            snapshot.Version,
            relational.ModeVersionId,
            runtimeProfileId,
            NormalizePermissionMode(permissionMode),
            snapshot.Spawn,
            snapshot.Scheduling,
            snapshot.Supervision,
            snapshot.Context,
            snapshot.Memory,
            snapshot.Tools,
            operation,
            execution,
            bindings)
        {
            PolicySnapshotHash = policySnapshot?.SnapshotHash ?? "",
            PolicyBundles = policySnapshot?.Bundles ?? [],
            Triggers = snapshot.Triggers,
            Orchestration = snapshot.Orchestration,
            Graph = graph,
            Workspace = workspace
        };
        return frozen;
    }

    /// <summary>
    /// Four-branch tier derivation, decided only from frozen inputs, first match wins:
    /// the conversation identity declares a tool surface → solo_dispatch (the master
    /// executes; topology-independent); no declared dispatch edges → free_form (the
    /// director builds its own workers); declared edges + a conversation identity
    /// holding a dispatchable-worker spawn authority (agent.create_temporary, or the
    /// agent.spawn alias) → self_dispatch; declared edges without spawn authority
    /// (including create_persistent-only, which mints candidates not dispatchable
    /// workers) → deterministic.
    /// </summary>
    internal static string DeriveGraphTier(IReadOnlyList<RuntimeAgentDefinition> operation, bool hasDeclaredEdges, string? conversationSlug)
    {
        var conversation = conversationSlug is { } slug
            ? operation.FirstOrDefault(agent => string.Equals(agent.Id, slug, StringComparison.OrdinalIgnoreCase))
            : null;
        // First branch: the conversation identity holds tools. Placed first because it
        // answers a different question than the two that follow — who EXECUTES, not how
        // dispatch is shaped. The three modes that predate it declare an empty meeting
        // tool_scope and therefore land on their original branches unchanged.
        if (conversation is { AllowedTools.Count: > 0 }) return FrozenGraphTiers.SoloDispatch;
        if (!hasDeclaredEdges) return FrozenGraphTiers.FreeForm;
        var capabilities = conversation?.Capabilities ?? [];
        return capabilities.Any(capability =>
            string.Equals(capability, ThreeNamespaceMap.SpawnTemporaryCapability, StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability, ThreeNamespaceMap.SpawnAliasCapability, StringComparison.OrdinalIgnoreCase))
            ? FrozenGraphTiers.SelfDispatch
            : FrozenGraphTiers.Deterministic;
    }

    private async Task<IReadOnlyList<RuntimeAgentDefinition>> FreezeModelPlansAsync(
        IReadOnlyList<RuntimeAgentDefinition> definitions,
        Guid sessionId,
        Guid modeVersionId,
        string? conversationTemplateSlug,
        SessionModelOverride? meetingModelOverride,
        CancellationToken cancellationToken)
    {
        var result = new List<RuntimeAgentDefinition>(definitions.Count);
        foreach (var definition in definitions)
        {
            var definitionId = definition.AgentDefinitionId ?? throw new InvalidDataException($"Agent '{definition.Id}' has no definition id.");
            var versionId = definition.AgentVersionId ?? throw new InvalidDataException($"Agent '{definition.Id}' has no version id.");
            var plan = await _models.FreezeAsync(new AgentModelFreezeRequest(
                sessionId, modeVersionId, definitionId, versionId, definition.Id,
                definition.ModelStrategyJson, definition.ModelStrategySource,
                IsConversationRoot(definition, conversationTemplateSlug),
                meetingModelOverride), cancellationToken).ConfigureAwait(false);
            result.Add(definition with { ModelPlan = plan });
        }
        return result;
    }

    // The conversation root is the frozen conversation identity holder; sessions
    // created before identity existed fall back to the literal meeting slug.
    private static bool IsConversationRoot(RuntimeAgentDefinition definition, string? conversationTemplateSlug) =>
        definition.Layer == "operation"
        && (conversationTemplateSlug is { } slug
            ? string.Equals(definition.Id, slug, StringComparison.OrdinalIgnoreCase)
            : string.Equals(definition.Id, "meeting", StringComparison.OrdinalIgnoreCase));

    private static RuntimeAgentDefinition ToRuntimeAgentDefinition(RuntimeAgentRosterEntry e) => new(
        e.Id,
        e.Layer,
        e.Role,
        e.Lifecycle,
        e.Capabilities,
        e.DirectUserOutput,
        e.ContextAccess)
    {
        AgentDefinitionId = e.AgentDefinitionId,
        AgentVersionId = e.AgentVersionId,
        VersionContentHash = e.VersionContentHash,
        AllowedTools = e.AllowedTools,
        ResourceGrants = e.ResourceGrants,
        PromptProfile = e.PromptProfile,
        SystemPrompt = e.SystemPrompt,
        ModelStrategyJson = e.ModelStrategyJson,
        ModelStrategySource = e.ModelStrategySource,
        Enabled = e.Enabled,
        RosterOrder = e.RosterOrder,
        PromptPipelineId = e.PromptPipelineId,
        PromptVersionId = e.PromptVersionId,
        PromptVersionContentHash = e.PromptVersionContentHash,
        PromptGraphJson = e.PromptGraphJson
    };

    // Unattended permission modes pass through admission verbatim instead of
    // being folded away: a frozen body must record the mode the run was
    // actually admitted under. Executability stays fail-closed in
    // ToolInvocationScopeResolver, and every executable grant is still minted
    // through the approval coordinator's binding-checked path.
    internal static string NormalizePermissionMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "deny" => "deny",
        "auto-approve" => "auto-approve",
        "full-access" => "full-access",
        "default" or "ask" or null or "" => "ask",
        _ => "ask"
    };

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
