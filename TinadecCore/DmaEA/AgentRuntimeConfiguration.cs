using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.DmaEA;

public sealed record SpawnPolicy(int MaxDepth, int MaxAgentsPerRun, int MaxParallelWorkers);

public sealed record SchedulingPolicy(int MaxActiveRunsPerSession, int WorkerRetryLimit, bool PreservePartialResults);

public sealed record SupervisionPolicy(bool RequiredBeforeFinal, int MaxRevisionRounds);

public sealed record ContextPolicy(int DefaultTokenBudget, int RecentMessageLimit, bool OptimisticRevision);

public sealed record MemoryPolicy(bool CandidateOnly, int RetrievalLimit, IReadOnlyList<string> AllowedScopes, IReadOnlyList<string> AllowedKinds);

public sealed record ToolRuntimePolicy(string Provider, bool MutationRequiresApproval, bool SerializeWorkspaceWrites, int DefaultTimeoutSeconds, int MaxToolRounds)
{
    public const int MaximumRounds = 32;

    public static void Validate(ToolRuntimePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxToolRounds is < 0 or > MaximumRounds)
        {
            throw new InvalidDataException(
                $"max_tool_rounds must be between 0 and the Core safety ceiling ({MaximumRounds}).");
        }
    }
}

public sealed record ApplicationModeDefinition(
    string Id,
    string DefaultAgentMode,
    IReadOnlyList<string> AllowedAgentModes,
    IReadOnlyDictionary<string, string> Bindings);

public sealed record RuntimeProfileDefinition(
    string Id,
    string ActivationPolicy,
    IReadOnlyList<string> OperationAgents,
    IReadOnlyList<string> ExecutionAgents,
    bool DirectAnswerAllowed);

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

    /// <summary>Optional prompt profile for deterministic prompt assembly.</summary>
    public string PromptProfile { get; init; } = string.Empty;

    /// <summary>Event triggers that activate the agent (logical channels).</summary>
    public IReadOnlyList<string> Triggers { get; init; } = [];

    /// <summary>Accepted logical messages (validated against the dual-layer bus).</summary>
    public IReadOnlyList<string> Accepts { get; init; } = [];

    /// <summary>Emitted logical messages.</summary>
    public IReadOnlyList<string> Emits { get; init; } = [];

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
    ToolRuntimePolicy Tools,
    IReadOnlyDictionary<string, ApplicationModeDefinition> ApplicationModes,
    IReadOnlyDictionary<string, RuntimeProfileDefinition> Profiles,
    IReadOnlyDictionary<string, RuntimeAgentDefinition> Agents)
{
    public (string ApplicationMode, string AgentMode, RuntimeProfileDefinition Profile) Resolve(string? applicationMode, string? agentMode)
    {
        var appId = NormalizeApplicationMode(applicationMode);
        var allowed = appId switch
        {
            "conversation" => new[] { "plan", "spec", "ask", "vibe", "auto", "agent" },
            "space" => new[] { "agent" },
            _ => throw new InvalidOperationException($"Application mode '{appId}' is not configured.")
        };
        var selectedAgentMode = string.IsNullOrWhiteSpace(agentMode)
            ? appId == "conversation" ? "auto" : "agent"
            : agentMode.Trim().ToLowerInvariant();
        if (!allowed.Contains(selectedAgentMode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Agent mode '{selectedAgentMode}' is unavailable in application mode '{appId}'.");
        var profileId = appId == "space" ? "space.full_duplex" : $"conversation.{selectedAgentMode}";
        var activation = profileId switch
        {
            "conversation.plan" => "plan_only",
            "conversation.spec" => "specification",
            "conversation.agent" => "execute",
            "space.full_duplex" => "full_duplex",
            _ => "intent_adaptive"
        };
        var profile = new RuntimeProfileDefinition(profileId, activation, [], [], selectedAgentMode is "ask" or "vibe" or "auto");
        return (appId, selectedAgentMode, profile);
    }

    public static string NormalizeApplicationMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "im" => "conversation",
        "hub" => "space",
        var mode => mode
    };
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
                _logger.LogInformation("Loaded agent runtime configuration version {Version} from {Path}", next.Version, _path);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _diagnostic, new RuntimeConfigurationDiagnostic("warning", ex.Message, _path, DateTimeOffset.UtcNow));
                _logger.LogWarning(ex, "Agent runtime configuration reload failed; keeping version {Version}", Current.Version);
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
        var toolPolicy = new ToolRuntimePolicy(Text(tools, "provider"), Boolean(tools, "mutation_requires_approval"), Boolean(tools, "serialize_workspace_writes"), Integer(tools, "default_timeout_seconds", 120), Integer(tools, "max_tool_rounds", 4));
        ToolRuntimePolicy.Validate(toolPolicy);
        return new AgentRuntimeConfigurationSnapshot(
            version,
            hash,
            DateTimeOffset.UtcNow,
            new SpawnPolicy(Integer(spawn, "max_depth", 2), Integer(spawn, "max_agents_per_run", 8), Integer(spawn, "max_parallel_workers", 4)),
            new SchedulingPolicy(Integer(scheduling, "max_active_runs_per_session", 2), Integer(scheduling, "worker_retry_limit", 2), Boolean(scheduling, "preserve_partial_results")),
            new SupervisionPolicy(Boolean(supervision, "required_before_final"), Integer(supervision, "max_revision_rounds", 2)),
            new ContextPolicy(Integer(context, "default_token_budget", 8192), Integer(context, "recent_message_limit", 24), Boolean(context, "optimistic_revision")),
            new MemoryPolicy(Boolean(memory, "candidate_only"), Integer(memory, "retrieval_limit", 8), Strings(memory, "allowed_scopes"), Strings(memory, "allowed_kinds")),
            toolPolicy,
            new Dictionary<string, ApplicationModeDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RuntimeProfileDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RuntimeAgentDefinition>(StringComparer.OrdinalIgnoreCase));
    }

    public static string NormalizeLayer(string? layer) => string.Equals(layer, "planning", StringComparison.OrdinalIgnoreCase) ? "operation" : layer?.Trim().ToLowerInvariant() ?? string.Empty;

    private static TomlTable Table(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlTable nested ? nested : throw new InvalidDataException($"Missing TOML table '{key}'.");
    private static string Text(TomlTable table, string key, string fallback = "") => table.TryGetValue(key, out var value) ? value?.ToString() ?? fallback : fallback;
    private static int Integer(TomlTable table, string key, int fallback) => table.TryGetValue(key, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    private static bool Boolean(TomlTable table, string key) => table.TryGetValue(key, out var value) && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    private static string[] Strings(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlArray array ? array.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToArray() : [];
}
