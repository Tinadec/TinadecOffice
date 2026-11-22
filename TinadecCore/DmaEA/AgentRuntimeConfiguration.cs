using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.DmaEA;

public sealed record SpawnPolicy(int MaxDepth, int MaxAgentsPerRun, int MaxParallelWorkers);

public sealed record SchedulingPolicy(int MaxActiveRunsPerSession, int WorkerRetryLimit, bool PreservePartialResults);

public sealed record SupervisionPolicy(bool RequiredBeforeFinal, int MaxRevisionRounds);

public sealed record ContextPolicy(int DefaultTokenBudget, int RecentMessageLimit, bool OptimisticRevision)
{
    public const string DefaultStrategy = "managed";
    public const string DefaultRecallFidelity = "balanced";
    public const double DefaultCompactionTriggerRatio = 0.75;
    public const int DefaultOffloadLargeToolPayloadBytes = 16_384;
    public const int MinRecentMessageLimit = 4;
    public const int MaxRecentMessageLimit = 200;

    public static readonly IReadOnlySet<string> AllowedStrategies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "managed", "deep_curator"
    };

    public static readonly IReadOnlySet<string> AllowedRecallFidelities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "summary_only", "balanced", "verbatim_first"
    };

    /// <summary>
    /// Shipped per-task token fuse. Zero disables it; loop detection still stops
    /// repeated calls, consecutive errors and malformed model behaviour.
    /// </summary>
    public const int DefaultTaskTokenBudget = 0;

    /// <summary>
    /// Default run-wide token fuse. A run is many tasks, so this sits well above
    /// any single task's context budget; it exists to stop an unattended run
    /// burning tokens without bound, not to cap how much work a run may do.
    /// Fuse-level initial value, to be calibrated against measured P95 usage.
    /// </summary>
    public const int DefaultRunTokenBudget = 1_048_576;

    /// <summary>
    /// Run-wide token fuse (<c>[context] run_token_budget</c>). Crossing it ends
    /// the active task gracefully instead of failing the run. Zero or less
    /// disables the gate; a TOML without the key keeps the default fuse.
    /// </summary>
    public int RunTokenBudget { get; init; } = DefaultRunTokenBudget;

    /// <summary>
    /// Per-task cumulative token fuse (<c>[context] task_token_budget</c>).
    /// Zero disables this gate. Null is the legacy frozen-config shape: those
    /// runs keep the old effective value (<see cref="DefaultTokenBudget"/>) so
    /// resuming them does not silently remove a safety boundary.
    /// </summary>
    public int? TaskTokenBudget { get; init; }

    /// <summary>3-tier context management preset: <c>direct</c>, <c>managed</c>, or <c>deep_curator</c>.</summary>
    public string Strategy { get; init; } = DefaultStrategy;

    /// <summary>On-demand history recall fidelity: <c>summary_only</c>, <c>balanced</c>, or <c>verbatim_first</c>.</summary>
    public string RecallFidelity { get; init; } = DefaultRecallFidelity;

    /// <summary>Context occupancy ratio (<c>0.40..0.95</c>) that triggers async background compaction.</summary>
    public double CompactionTriggerRatio { get; init; } = DefaultCompactionTriggerRatio;

    /// <summary>Allows <c>deep_curator</c> and explicit recall queries to search sibling project sessions.</summary>
    public bool AllowCrossSessionRecall { get; init; } = true;

    /// <summary>Always-on primitive: folds stale large tool outputs older than the hot window.</summary>
    public bool MicroCompactStaleToolOutputs { get; init; } = true;

    /// <summary>Always-on primitive: byte threshold above which stale tool payloads are offloaded to artifact pointers.</summary>
    public int OffloadLargeToolPayloadBytes { get; init; } = DefaultOffloadLargeToolPayloadBytes;

    public int EffectiveTaskTokenBudget => TaskTokenBudget ?? DefaultTokenBudget;

    public static void Validate(ContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.DefaultTokenBudget <= 0)
        {
            throw new InvalidDataException("default_token_budget must be positive.");
        }
        if (policy.RecentMessageLimit is < MinRecentMessageLimit or > MaxRecentMessageLimit)
        {
            throw new InvalidDataException($"recent_message_limit must be between {MinRecentMessageLimit} and {MaxRecentMessageLimit}.");
        }
        if (policy.RunTokenBudget < 0)
        {
            throw new InvalidDataException("run_token_budget must not be negative (0 disables the run-level gate).");
        }
        if (policy.TaskTokenBudget is < 0)
        {
            throw new InvalidDataException("task_token_budget must not be negative (0 disables the per-task gate).");
        }
        if (string.IsNullOrWhiteSpace(policy.Strategy) || !AllowedStrategies.Contains(policy.Strategy.Trim()))
        {
            throw new InvalidDataException($"Unknown context.strategy '{policy.Strategy}'. Expected one of: direct, managed, deep_curator.");
        }
        if (string.IsNullOrWhiteSpace(policy.RecallFidelity) || !AllowedRecallFidelities.Contains(policy.RecallFidelity.Trim()))
        {
            throw new InvalidDataException($"Unknown context.recall_fidelity '{policy.RecallFidelity}'. Expected one of: summary_only, balanced, verbatim_first.");
        }
        if (double.IsNaN(policy.CompactionTriggerRatio) || policy.CompactionTriggerRatio is < 0.40 or > 0.95)
        {
            throw new InvalidDataException("compaction_trigger_ratio must be between 0.40 and 0.95.");
        }
        if (policy.OffloadLargeToolPayloadBytes < 1024)
        {
            throw new InvalidDataException("offload_large_tool_payload_bytes must be at least 1024.");
        }
    }
}

public sealed record MemoryPolicy(bool CandidateOnly, int RetrievalLimit, IReadOnlyList<string> AllowedScopes, IReadOnlyList<string> AllowedKinds)
{
    public const string DefaultEvolutionMode = "auto_evolve";
    public const int DefaultCoreMarkdownMaxTokens = 1500;
    public const int MinCoreMarkdownMaxTokens = 256;
    public const int MaxCoreMarkdownMaxTokens = 8000;
    public const string DefaultProjectMemoryPath = ".tinadec/memory/project-core.md";

    public static readonly IReadOnlySet<string> AllowedEvolutionModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "auto_evolve", "candidate_only"
    };

    /// <summary>Memory mutation friction mode: <c>auto_evolve</c> (default) or <c>candidate_only</c>.</summary>
    public string EvolutionMode { get; init; } = DefaultEvolutionMode;

    /// <summary>Hard token cap for projected core Markdown files (<c>256..8000</c>).</summary>
    public int CoreMarkdownMaxTokens { get; init; } = DefaultCoreMarkdownMaxTokens;

    /// <summary>Enables bidirectional sync with workspace project Markdown memory.</summary>
    public bool ProjectMarkdownProjection { get; init; } = true;

    /// <summary>Workspace-relative path for objective project memory Markdown.</summary>
    public string ProjectMemoryPath { get; init; } = DefaultProjectMemoryPath;

    /// <summary>When false (default), companions have read-only access to project memory.</summary>
    public bool CompanionCanWriteProjectMemory { get; init; }

    /// <summary>True when either <see cref="CandidateOnly"/> is set or <see cref="EvolutionMode"/> is <c>candidate_only</c>.</summary>
    public bool EffectiveCandidateOnly => CandidateOnly || string.Equals(EvolutionMode, "candidate_only", StringComparison.OrdinalIgnoreCase);

    public static void Validate(MemoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.RetrievalLimit < 0)
        {
            throw new InvalidDataException("memory.retrieval_limit must not be negative.");
        }
        if (string.IsNullOrWhiteSpace(policy.EvolutionMode) || !AllowedEvolutionModes.Contains(policy.EvolutionMode.Trim()))
        {
            throw new InvalidDataException($"Unknown memory.evolution_mode '{policy.EvolutionMode}'. Expected one of: auto_evolve, candidate_only.");
        }
        if (policy.CoreMarkdownMaxTokens is < MinCoreMarkdownMaxTokens or > MaxCoreMarkdownMaxTokens)
        {
            throw new InvalidDataException($"memory.core_markdown_max_tokens must be between {MinCoreMarkdownMaxTokens} and {MaxCoreMarkdownMaxTokens}.");
        }
        if (string.IsNullOrWhiteSpace(policy.ProjectMemoryPath)
            || Path.IsPathRooted(policy.ProjectMemoryPath.Trim())
            || policy.ProjectMemoryPath.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException("memory.project_memory_path must be a non-empty relative path without '..' traversal.");
        }
    }
}

public sealed record ToolRuntimePolicy(
    string Provider,
    bool MutationRequiresApproval,
    bool SerializeWorkspaceWrites,
    int DefaultTimeoutSeconds,
    int MaxToolRounds,
    IReadOnlyDictionary<string, int>? TaskRoundOverrides = null)
{
    public const int MaximumRounds = 32;

    /// <summary>
    /// Hard ceiling for any per-task round override, regardless of what the
    /// baseline or a workspace override declares. Kept equal to the global
    /// ceiling: an override must never be allowed to be narrower than the global
    /// default it replaces, so it only bounds *positive* configuration values.
    /// </summary>
    public const int MaxTaskOverrideRounds = 32;

    /// <summary>
    /// Default absolute per-task tool-call fuse. Deliberately not unlimited: this
    /// Core supports unattended runs (auto-approve / full-access), and unlike an
    /// interactive CLI nobody is at the keyboard to interrupt one. 500 calls is
    /// far beyond any real task, so a normal run never reaches it — tripping it
    /// usually indicates a bug.
    /// </summary>
    public const int DefaultMaxToolCalls = 500;

    /// <summary>
    /// Absolute per-task tool-call fuse (<c>[tools] max_tool_calls</c>). This is
    /// a fuse, not a budget, and it is independent of the round limit: setting
    /// <see cref="MaxToolRounds"/> to a non-positive value (unlimited rounds) does
    /// not remove it. Zero disables this call fuse; negative values are invalid for
    /// the fuse itself. A TOML without the key keeps the default.
    /// </summary>
    public int MaxToolCalls { get; init; } = DefaultMaxToolCalls;

    public IReadOnlyDictionary<string, int> Overrides { get; init; } =
        TaskRoundOverrides is { Count: > 0 }
            ? new Dictionary<string, int>(TaskRoundOverrides, StringComparer.OrdinalIgnoreCase)
            : (IReadOnlyDictionary<string, int>)System.Collections.Frozen.FrozenDictionary<string, int>.Empty;

    public static void Validate(ToolRuntimePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // Any non-positive value means "unlimited rounds". Only positive values
        // are bounded by the Core safety ceiling, matching the documented runtime
        // contract and ResolveTaskRoundLimit's <= 0 semantics.
        if (policy.MaxToolRounds > MaximumRounds)
        {
            throw new InvalidDataException(
                $"A positive max_tool_rounds value must not exceed the Core safety ceiling ({MaximumRounds}); non-positive values mean unlimited.");
        }
        if (policy.MaxToolCalls < 0)
        {
            throw new InvalidDataException("max_tool_calls must not be negative (0 disables the absolute call fuse).");
        }
        foreach (var (key, rounds) in policy.Overrides)
        {
            if (rounds > MaxTaskOverrideRounds)
            {
                throw new InvalidDataException(
                    $"A positive task_round_overrides['{key}'] value must not exceed the per-task ceiling ({MaxTaskOverrideRounds}); non-positive values mean unlimited.");
            }
        }
    }

    /// <summary>
    /// Effective tool-round limit for one task: the category override wins, the
    /// risk class doubles as the category when the planner omitted one, and the
    /// frozen global default applies otherwise. Zero or less means "no round
    /// gate" — convergence is then carried by the loop guard and the token
    /// budgets, with <see cref="MaxToolCalls"/> as the absolute fuse.
    /// </summary>
    public int ResolveTaskRoundLimit(string? category, string risk)
    {
        // Category first, then the risk class as the second key — a non-matching
        // category must not shadow a matching risk override.
        if (CategoryKey(category) is { } byCategory && Overrides.TryGetValue(byCategory, out var byCategoryValue)) return byCategoryValue;
        if (RiskKey(risk) is { } byRisk && Overrides.TryGetValue(byRisk, out var byRiskValue)) return byRiskValue;
        return MaxToolRounds;
    }

    /// <summary>
    /// Where the effective round limit came from, for the operator-facing budget
    /// context (`global_default` / `category_override` / `risk_override`).
    /// </summary>
    public string ResolveTaskRoundLimitSource(string? category, string risk)
    {
        if (CategoryKey(category) is { } byCategory && Overrides.ContainsKey(byCategory)) return "category_override";
        if (RiskKey(risk) is { } byRisk && Overrides.ContainsKey(byRisk)) return "risk_override";
        return "global_default";
    }

    private static string? CategoryKey(string? category) =>
        string.IsNullOrWhiteSpace(category) ? null : category.Trim();

    private static string? RiskKey(string? risk) =>
        string.IsNullOrWhiteSpace(risk) ? null : risk.Trim();
}

/// <summary>
/// Gates the operational-role trigger chain (context compression, skill
/// recommendation, experience curation, git stewardship). Budgets and switches
/// live in the TOML baseline; per-role event bindings ride the frozen roster.
/// </summary>
public sealed record TriggersPolicy(
    bool Enabled,
    int ContextTokenThreshold,
    bool CompressOnTaskClosed,
    bool RecommendOnTaskCreated,
    bool CurateOnRunClosed,
    bool GitStewardOnRunClosed)
{
    public static TriggersPolicy Disabled => new(false, 0, false, false, false, false);

    public static void Validate(TriggersPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.ContextTokenThreshold < 0)
        {
            throw new InvalidDataException("context_token_threshold must not be negative.");
        }
    }
}

/// <summary>
/// Lateral execution lanes (swim lanes) inside one run. The master switch stays
/// off until M2 wires the multi-lane main loop; the ceilings below are frozen
/// into every run configuration so admission can rely on them either way.
/// </summary>
public sealed record OrchestrationPolicy(bool LanesEnabled, int MaxLanesPerRun, int MaxTasksPerLane)
{
    public static OrchestrationPolicy Disabled => new(false, 4, 6);

    public static void Validate(OrchestrationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxLanesPerRun is < 1 or > 16)
        {
            throw new InvalidDataException("max_lanes_per_run must be between 1 and 16.");
        }
        if (policy.MaxTasksPerLane is < 1 or > 64)
        {
            throw new InvalidDataException("max_tasks_per_lane must be between 1 and 64.");
        }
    }
}

public sealed record RuntimeAgentDefinition(
    string Id,
    string Layer,
    string Role,
    string Lifecycle,
    IReadOnlyList<string> Capabilities,
    bool DirectUserOutput,
    string ContextAccess)
{
    /// <summary>
    /// Immutable configuration identity captured at run admission. Formal agents
    /// carry the published relational ids; TOML agents receive deterministic
    /// virtual ids when the frozen run is assembled.
    /// </summary>
    public Guid? AgentDefinitionId { get; init; }

    public Guid? AgentVersionId { get; init; }

    public string VersionContentHash { get; init; } = string.Empty;

    /// <summary>
    /// Immutable tool allow-list declared by the runtime profile.  It is an
    /// optional v1-compatible property; omitted TOML means no autonomous tools.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Resource-path grants frozen from the node's binding envelope (WS-4 resource
    /// envelope). Empty = no workspace authorization for provider tools; the PDP
    /// resource_access boundary enforces read/write levels from these grants.
    /// </summary>
    public IReadOnlyList<FrozenResourceGrant> ResourceGrants { get; init; } = [];

    /// <summary>Optional prompt profile for deterministic prompt assembly.</summary>
    public string PromptProfile { get; init; } = string.Empty;

    /// <summary>Event triggers that activate the agent (logical channels).</summary>
    public IReadOnlyList<string> Triggers { get; init; } = [];

    /// <summary>
    /// The executor ids this agent's node may dispatch to (relationship file
    /// <c>allowed_dispatch_targets</c>). Null = not declared: the tier's roster applies. Frozen with
    /// null suppression so a body admitted before the field existed keeps identical bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedDispatchTargets { get; init; }

    /// <summary>Supervisor decisions when applicable (pass/revise/escalate).</summary>
    public IReadOnlyList<string> Decisions { get; init; } = [];

    /// <summary>Determines memory write policy for experience curators.</summary>
    public string MemoryWritePolicy { get; init; } = string.Empty;

    /// <summary>Immutable formal-agent content captured before run admission.</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>
    /// The agent's published responsibility description ("what it is for / when to use it /
    /// what it cannot do"). It is what the coordinator reads to choose a dispatch target, so it
    /// travels into the frozen roster verbatim. Null-suppressed so bodies frozen before it
    /// existed serialize to identical bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    public string ModelStrategyJson { get; init; } = "{\"kind\":\"inherit\"}";

    public string ModelStrategySource { get; init; } = "agent_version";

    public FrozenModelPlan? ModelPlan { get; init; }

    public bool Enabled { get; init; } = true;

    public int RosterOrder { get; init; }

    public Guid? PromptPipelineId { get; init; }

    public Guid? PromptVersionId { get; init; }

    public string PromptVersionContentHash { get; init; } = string.Empty;

    /// <summary>
    /// The immutable PromptVersion graph body. Keeping it in the frozen run document
    /// prevents resume from observing a later prompt publication.
    /// </summary>
    public string PromptGraphJson { get; init; } = string.Empty;
}

public sealed record AgentRuntimeConfigurationSnapshot(
    long Version,
    string ContentHash,
    DateTimeOffset LoadedAt,
    SpawnPolicy Spawn,
    SchedulingPolicy Scheduling,
    SupervisionPolicy Supervision,
    ContextPolicy Context,
    MemoryPolicy Memory,
    ToolRuntimePolicy Tools)
{
    /// <summary>Operational-role trigger gates; absent TOML keeps the chain disabled.</summary>
    public TriggersPolicy Triggers { get; init; } = TriggersPolicy.Disabled;

    /// <summary>Lane master switch and ceilings; absent TOML keeps lanes off.</summary>
    public OrchestrationPolicy Orchestration { get; init; } = OrchestrationPolicy.Disabled;

    /// <summary>
    /// User-editable approval policy. Null when runtime.toml has no [approval]
    /// section, which keeps the host's compiled options for that scope.
    /// </summary>
    public ToolApprovalPolicy? Approval { get; init; }
}

public sealed record RuntimeConfigurationDiagnostic(string State, string Detail, string SourcePath, DateTimeOffset CheckedAt);

public interface IAgentRuntimeConfiguration
{
    AgentRuntimeConfigurationSnapshot Current { get; }
    RuntimeConfigurationDiagnostic Diagnostic { get; }
}

/// <summary>Loads the annotated TOML baseline and hot-reloads valid snapshots for new runs.</summary>
public sealed class AgentRuntimeConfigurationStore : IAgentRuntimeConfiguration, IDisposable
{
    private readonly ILogger<AgentRuntimeConfigurationStore> _logger;
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _reloadTimer;
    private readonly object _gate = new();
    private long _version;
    private AgentRuntimeConfigurationSnapshot _current;
    private RuntimeConfigurationDiagnostic _diagnostic;

    public AgentRuntimeConfigurationStore(IConfiguration configuration, ILogger<AgentRuntimeConfigurationStore> logger,
        IScopeStorageLocations? locations = null, IScopeConfigurationDocuments? documents = null)
    {
        _logger = logger;
        // Persistence locations also exist in embedded hosts. Only registration
        // of the editing authority makes runtime.toml a required scope document.
        _path = documents is null ? ResolvePath(configuration["TinadecAgent:ProfileConfigPath"])
            : Path.Combine((locations ?? throw new InvalidOperationException("Scope configuration documents require storage locations.")).Config, "runtime.toml");
        _current = LoadSnapshot(_path, Interlocked.Increment(ref _version));
        _diagnostic = new("ready", $"Loaded runtime configuration v{_current.Version} ({_current.ContentHash[..12]}).", _path, DateTimeOffset.UtcNow);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            _reloadTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += ScheduleReload;
            _watcher.Created += ScheduleReload;
            _watcher.Renamed += ScheduleReload;
            _watcher.EnableRaisingEvents = true;
        }
    }

    public AgentRuntimeConfigurationSnapshot Current
    {
        get
        {
            // Admission cannot wait for the watcher debounce: a file edit is effective immediately.
            var current = Volatile.Read(ref _current);
            if (File.Exists(_path))
            {
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_path))).ToLowerInvariant();
                if (hash != current.ContentHash) Reload();
            }
            return Volatile.Read(ref _current);
        }
    }
    public RuntimeConfigurationDiagnostic Diagnostic => Volatile.Read(ref _diagnostic);

    public RuntimeContextSettings ContextSettings => new(
        Current.Context.DefaultTokenBudget,
        Current.Context.RecentMessageLimit,
        Current.Memory.RetrievalLimit);

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
    }

    private void ScheduleReload(object? sender, FileSystemEventArgs args) => _reloadTimer?.Change(250, Timeout.Infinite);

    private void Reload()
    {
        lock (_gate)
        {
            try
            {
                var next = LoadSnapshot(_path, Interlocked.Increment(ref _version));
                Volatile.Write(ref _current, next);
                Volatile.Write(ref _diagnostic, new RuntimeConfigurationDiagnostic("ready", $"Loaded runtime configuration v{next.Version} ({next.ContentHash[..12]}).", _path, DateTimeOffset.UtcNow));
                _logger.TryLogInformation("Loaded agent runtime configuration version {Version} from {Path}", next.Version, _path);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _diagnostic, new RuntimeConfigurationDiagnostic("warning", ex.Message, _path, DateTimeOffset.UtcNow));
                _logger.TryLogWarning(ex, "Agent runtime configuration reload failed; keeping version {Version}", Volatile.Read(ref _current).Version);
            }
        }
    }

    private static string ResolvePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Path.Combine(AppContext.BaseDirectory, "Configuration", "default-agent-runtime.toml");
    }

    internal static AgentRuntimeConfigurationSnapshot LoadSnapshot(string path, long version)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Agent runtime TOML was not found.", path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        return LoadSnapshotText(text, path, version);
    }

    internal static AgentRuntimeConfigurationSnapshot LoadSnapshotText(string text, string path, long version)
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text)
                ?? throw new InvalidDataException("Agent runtime TOML did not contain a root table.");
        }
        catch (TomlException ex)
        {
            throw new InvalidDataException("Agent runtime TOML is invalid: " + ex.Message, ex);
        }
        var schema = Integer(root, "schema_version", 0);
        if (schema != 1) throw new InvalidDataException($"Unsupported agent runtime schema_version '{schema}'.");

        var spawn = Table(root, "spawn");
        var scheduling = Table(root, "scheduling");
        var supervision = Table(root, "supervision");
        var context = Table(root, "context");
        var memory = Table(root, "memory");
        var tools = Table(root, "tools");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var toolPolicy = new ToolRuntimePolicy(Text(tools, "provider"), Boolean(tools, "mutation_requires_approval"), Boolean(tools, "serialize_workspace_writes"), Integer(tools, "default_timeout_seconds", 120), Integer(tools, "max_tool_rounds", 4), ReadTaskRoundOverrides(tools))
        {
            MaxToolCalls = Integer(tools, "max_tool_calls", ToolRuntimePolicy.DefaultMaxToolCalls)
        };
        ToolRuntimePolicy.Validate(toolPolicy);
        // The trigger chain is opt-in: a baseline without a [triggers] table keeps
        // operational roles dormant, matching pre-trigger deployments.
        var triggers = OptionalTable(root, "triggers");
        var triggersPolicy = triggers is null
            ? TriggersPolicy.Disabled
            : new TriggersPolicy(
                Boolean(triggers, "enabled"),
                Integer(triggers, "context_token_threshold", 6000),
                Boolean(triggers, "compress_on_task_closed"),
                Boolean(triggers, "recommend_on_task_created"),
                Boolean(triggers, "curate_on_run_closed"),
                Boolean(triggers, "git_steward_on_run_closed"));
        TriggersPolicy.Validate(triggersPolicy);
        var contextPolicy = new ContextPolicy(
            Integer(context, "default_token_budget", 8192),
            Integer(context, "recent_message_limit", 24),
            Boolean(context, "optimistic_revision"))
        {
            RunTokenBudget = Integer(context, "run_token_budget", ContextPolicy.DefaultRunTokenBudget),
            TaskTokenBudget = Integer(context, "task_token_budget", ContextPolicy.DefaultTaskTokenBudget),
            Strategy = Text(context, "strategy", ContextPolicy.DefaultStrategy).Trim().ToLowerInvariant(),
            RecallFidelity = Text(context, "recall_fidelity", ContextPolicy.DefaultRecallFidelity).Trim().ToLowerInvariant(),
            CompactionTriggerRatio = Double(context, "compaction_trigger_ratio", ContextPolicy.DefaultCompactionTriggerRatio),
            AllowCrossSessionRecall = Boolean(context, "allow_cross_session_recall", true),
            MicroCompactStaleToolOutputs = Boolean(context, "micro_compact_stale_tool_outputs", true),
            OffloadLargeToolPayloadBytes = Integer(context, "offload_large_tool_payload_bytes", ContextPolicy.DefaultOffloadLargeToolPayloadBytes)
        };
        ContextPolicy.Validate(contextPolicy);
        var candidateOnly = Boolean(memory, "candidate_only", false);
        var rawEvolutionMode = OptionalString(memory, "evolution_mode");
        var evolutionMode = string.IsNullOrWhiteSpace(rawEvolutionMode)
            ? (candidateOnly ? "candidate_only" : MemoryPolicy.DefaultEvolutionMode)
            : rawEvolutionMode.Trim().ToLowerInvariant();
        var memoryPolicy = new MemoryPolicy(
            candidateOnly,
            Integer(memory, "retrieval_limit", 8),
            Strings(memory, "allowed_scopes"),
            Strings(memory, "allowed_kinds"))
        {
            EvolutionMode = evolutionMode,
            CoreMarkdownMaxTokens = Integer(memory, "core_markdown_max_tokens", MemoryPolicy.DefaultCoreMarkdownMaxTokens),
            ProjectMarkdownProjection = Boolean(memory, "project_markdown_projection", true),
            ProjectMemoryPath = Text(memory, "project_memory_path", MemoryPolicy.DefaultProjectMemoryPath).Trim(),
            CompanionCanWriteProjectMemory = Boolean(memory, "companion_can_write_project_memory", false)
        };
        MemoryPolicy.Validate(memoryPolicy);
        // The compression threshold and the context budget are one mechanism: if
        // the threshold reaches (or passes) the budget, compaction either never
        // fires or always fires. Fail loud at load time and name BOTH keys instead
        // of letting a run discover the mismatch mid-flight.
        if (triggersPolicy.ContextTokenThreshold >= contextPolicy.DefaultTokenBudget)
        {
            throw new InvalidDataException(
                $"context_token_threshold ({triggersPolicy.ContextTokenThreshold}) must be lower than default_token_budget ({contextPolicy.DefaultTokenBudget}); otherwise context compression never fires (or always fires) and the two settings have drifted apart.");
        }
        // Lanes ride the same opt-in pattern: a baseline without [orchestration]
        // keeps the lateral channel closed while the ceilings still freeze.
        var orchestration = OptionalTable(root, "orchestration");
        var orchestrationPolicy = orchestration is null
            ? OrchestrationPolicy.Disabled
            : new OrchestrationPolicy(
                Boolean(orchestration, "lanes_enabled"),
                Integer(orchestration, "max_lanes_per_run", 4),
                Integer(orchestration, "max_tasks_per_lane", 6));
        OrchestrationPolicy.Validate(orchestrationPolicy);
        // Approval is opt-in like triggers and lanes: a baseline without
        // [approval] keeps the compiled baseline so embedded hosts do not gain
        // an unconfigured auto-approval path. The shipped runtime.toml carries
        // the commented section, which is what a real scope loads.
        var approval = OptionalTable(root, "approval");
        var approvalPolicy = approval is null
            ? null
            : new ToolApprovalPolicy
            {
                Enabled = Boolean(approval, "enabled", ToolApprovalPolicy.Default.Enabled),
                RiskMax = Text(approval, "risk_max", ToolApprovalPolicy.Default.RiskMax),
                MaxPerRun = Integer(approval, "max_per_run", ToolApprovalPolicy.Default.MaxPerRun),
                ReleaseReadOnlyInAskMode = Boolean(approval, "release_read_only_in_ask_mode", ToolApprovalPolicy.Default.ReleaseReadOnlyInAskMode),
                DelegatedRiskMax = Text(approval, "delegated_risk_max", ToolApprovalPolicy.Default.DelegatedRiskMax),
                HumanOnlyTools = Strings(approval, "human_only_tools", ToolApprovalPolicy.Default.HumanOnlyTools),
                DelegationBlockedTools = Strings(approval, "delegation_blocked_tools", ToolApprovalPolicy.Default.DelegationBlockedTools)
            };
        if (approvalPolicy is not null) ToolApprovalPolicy.Validate(approvalPolicy);
        return new AgentRuntimeConfigurationSnapshot(
            version,
            hash,
            DateTimeOffset.UtcNow,
            new SpawnPolicy(Integer(spawn, "max_depth", 2), Integer(spawn, "max_agents_per_run", 16), Integer(spawn, "max_parallel_workers", 4)),
            new SchedulingPolicy(Integer(scheduling, "max_active_runs_per_session", 2), Integer(scheduling, "worker_retry_limit", 2), Boolean(scheduling, "preserve_partial_results")),
            new SupervisionPolicy(Boolean(supervision, "required_before_final"), Integer(supervision, "max_revision_rounds", 2)),
            contextPolicy,
            memoryPolicy,
            toolPolicy)
        {
            Triggers = triggersPolicy,
            Orchestration = orchestrationPolicy,
            Approval = approvalPolicy
        };
    }

    private static string? OptionalString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string text ? text : null;

    public static string NormalizeLayer(string? layer) => string.Equals(layer, "planning", StringComparison.OrdinalIgnoreCase) ? "operation" : layer?.Trim().ToLowerInvariant() ?? string.Empty;

    private static IReadOnlyDictionary<string, int>? ReadTaskRoundOverrides(TomlTable tools)
    {
        if (!tools.TryGetValue("task_round_overrides", out var node) || node is not TomlTable table) return null;
        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in table)
        {
            if (value is long rounds) overrides[key] = checked((int)rounds);
        }
        return overrides.Count == 0 ? null : overrides;
    }

    private static TomlTable Table(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlTable nested ? nested : throw new InvalidDataException($"Missing TOML table '{key}'.");
    private static TomlTable? OptionalTable(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlTable nested ? nested : null;
    private static string Text(TomlTable table, string key, string fallback = "") => table.TryGetValue(key, out var value) ? value?.ToString() ?? fallback : fallback;
    private static int Integer(TomlTable table, string key, int fallback) => table.TryGetValue(key, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    private static double Double(TomlTable table, string key, double fallback) => table.TryGetValue(key, out var value) ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    private static bool Boolean(TomlTable table, string key) => table.TryGetValue(key, out var value) && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    private static bool Boolean(TomlTable table, string key, bool fallback) => table.TryGetValue(key, out var value) ? Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    private static string[] Strings(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlArray array ? array.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToArray() : [];
    private static string[] Strings(TomlTable table, string key, IReadOnlyList<string> fallback) => table.TryGetValue(key, out var value) && value is TomlArray array
        ? array.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
        : fallback.ToArray();
}
