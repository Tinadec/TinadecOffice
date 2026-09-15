using System.Security.Cryptography;
using System.Text;
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

    public static void Validate(ContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.DefaultTokenBudget <= 0)
        {
            throw new InvalidDataException("default_token_budget must be positive.");
        }
        if (policy.RecentMessageLimit <= 0)
        {
            throw new InvalidDataException("recent_message_limit must be positive.");
        }
        if (policy.RunTokenBudget < 0)
        {
            throw new InvalidDataException("run_token_budget must not be negative (0 disables the run-level gate).");
        }
    }
}

public sealed record MemoryPolicy(bool CandidateOnly, int RetrievalLimit, IReadOnlyList<string> AllowedScopes, IReadOnlyList<string> AllowedKinds);

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
    /// <see cref="MaxToolRounds"/> to 0 (unlimited rounds) does not remove it.
    /// Zero or less disables the check; a TOML without the key keeps the default.
    /// </summary>
    public int MaxToolCalls { get; init; } = DefaultMaxToolCalls;

    public IReadOnlyDictionary<string, int> Overrides { get; init; } =
        TaskRoundOverrides is { Count: > 0 }
            ? new Dictionary<string, int>(TaskRoundOverrides, StringComparer.OrdinalIgnoreCase)
            : (IReadOnlyDictionary<string, int>)System.Collections.Frozen.FrozenDictionary<string, int>.Empty;

    public static void Validate(ToolRuntimePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // 0 (or less) is a legal "unlimited rounds" declaration; negative is not
        // a second spelling of it.
        if (policy.MaxToolRounds is < 0 or > MaximumRounds)
        {
            throw new InvalidDataException(
                $"max_tool_rounds must be 0 (unlimited) or a positive value up to the Core safety ceiling ({MaximumRounds}).");
        }
        if (policy.MaxToolCalls < 0)
        {
            throw new InvalidDataException("max_tool_calls must not be negative (0 disables the absolute call fuse).");
        }
        foreach (var (key, rounds) in policy.Overrides)
        {
            if (rounds is < 0 or > MaxTaskOverrideRounds)
            {
                throw new InvalidDataException(
                    $"task_round_overrides['{key}'] must be 0 (unlimited) or a positive value up to the per-task ceiling ({MaxTaskOverrideRounds}).");
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

    /// <summary>Supervisor decisions when applicable (pass/revise/escalate).</summary>
    public IReadOnlyList<string> Decisions { get; init; } = [];

    /// <summary>Determines memory write policy for experience curators.</summary>
    public string MemoryWritePolicy { get; init; } = string.Empty;

    /// <summary>Immutable formal-agent content captured before run admission.</summary>
    public string SystemPrompt { get; init; } = string.Empty;

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

    public AgentRuntimeConfigurationStore(IConfiguration configuration, ILogger<AgentRuntimeConfigurationStore> logger)
    {
        _logger = logger;
        _path = ResolvePath(configuration["TinadecAgent:ProfileConfigPath"]);
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

    public AgentRuntimeConfigurationSnapshot Current => Volatile.Read(ref _current);
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
                _logger.TryLogWarning(ex, "Agent runtime configuration reload failed; keeping version {Version}", Current.Version);
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
            RunTokenBudget = Integer(context, "run_token_budget", ContextPolicy.DefaultRunTokenBudget)
        };
        ContextPolicy.Validate(contextPolicy);
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
        return new AgentRuntimeConfigurationSnapshot(
            version,
            hash,
            DateTimeOffset.UtcNow,
            new SpawnPolicy(Integer(spawn, "max_depth", 2), Integer(spawn, "max_agents_per_run", 16), Integer(spawn, "max_parallel_workers", 4)),
            new SchedulingPolicy(Integer(scheduling, "max_active_runs_per_session", 2), Integer(scheduling, "worker_retry_limit", 2), Boolean(scheduling, "preserve_partial_results")),
            new SupervisionPolicy(Boolean(supervision, "required_before_final"), Integer(supervision, "max_revision_rounds", 2)),
            contextPolicy,
            new MemoryPolicy(Boolean(memory, "candidate_only"), Integer(memory, "retrieval_limit", 8), Strings(memory, "allowed_scopes"), Strings(memory, "allowed_kinds")),
            toolPolicy)
        {
            Triggers = triggersPolicy,
            Orchestration = orchestrationPolicy
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
    private static bool Boolean(TomlTable table, string key) => table.TryGetValue(key, out var value) && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    private static string[] Strings(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlArray array ? array.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToArray() : [];
}
