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

public sealed record ToolRuntimePolicy(string Provider, bool MutationRequiresApproval, bool SerializeWorkspaceWrites, int DefaultTimeoutSeconds, int MaxToolRounds);

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
        if (!ApplicationModes.TryGetValue(appId, out var app))
            throw new InvalidOperationException($"Application mode '{appId}' is not configured.");

        var selectedAgentMode = string.IsNullOrWhiteSpace(agentMode) ? app.DefaultAgentMode : agentMode.Trim().ToLowerInvariant();
        if (!app.AllowedAgentModes.Contains(selectedAgentMode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Agent mode '{selectedAgentMode}' is unavailable in application mode '{appId}'.");
        if (!app.Bindings.TryGetValue(selectedAgentMode, out var profileId) || !Profiles.TryGetValue(profileId, out var profile))
            throw new InvalidOperationException($"No runtime profile is bound to '{appId}/{selectedAgentMode}'.");
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

        var applications = new Dictionary<string, ApplicationModeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in Table(root, "application_modes"))
        {
            if (value is not TomlTable mode) continue;
            var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (mode.TryGetValue("bindings", out var bindingValue) && bindingValue is TomlTable bindingTable)
                foreach (var (agentMode, profile) in bindingTable) bindings[agentMode] = profile?.ToString() ?? string.Empty;
            applications[id] = new ApplicationModeDefinition(id, Text(mode, "default_agent_mode"), Strings(mode, "allowed_agent_modes"), bindings);
        }

        var profiles = new Dictionary<string, RuntimeProfileDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in Table(root, "profiles"))
        {
            if (value is not TomlTable profile) continue;
            profiles[id] = new RuntimeProfileDefinition(id, Text(profile, "activation_policy"), Strings(profile, "operation_agents"), Strings(profile, "execution_agents"), Boolean(profile, "direct_answer_allowed"));
        }

        var agents = new Dictionary<string, RuntimeAgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in Table(root, "agents"))
        {
            if (value is not TomlTable agent) continue;
            var layer = NormalizeLayer(Text(agent, "layer"));
            var capabilities = NormalizeCapabilities(Strings(agent, "capabilities"));
            agents[id] = new RuntimeAgentDefinition(id, layer, Text(agent, "role"), Text(agent, "lifecycle"), capabilities, Boolean(agent, "direct_user_output"), Text(agent, "context_access"))
            {
                AllowedTools = Strings(agent, "allowed_tools"),
                PromptProfile = Text(agent, "prompt_profile"),
                Triggers = Strings(agent, "triggers"),
                Accepts = Strings(agent, "accepts"),
                Emits = Strings(agent, "emits"),
                Decisions = Strings(agent, "decisions"),
                MemoryWritePolicy = Text(agent, "memory_write_policy")
            };
        }

        Validate(applications, profiles, agents);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new AgentRuntimeConfigurationSnapshot(
            version,
            hash,
            DateTimeOffset.UtcNow,
            new SpawnPolicy(Integer(spawn, "max_depth", 2), Integer(spawn, "max_agents_per_run", 8), Integer(spawn, "max_parallel_workers", 4)),
            new SchedulingPolicy(Integer(scheduling, "max_active_runs_per_session", 2), Integer(scheduling, "worker_retry_limit", 2), Boolean(scheduling, "preserve_partial_results")),
            new SupervisionPolicy(Boolean(supervision, "required_before_final"), Integer(supervision, "max_revision_rounds", 2)),
            new ContextPolicy(Integer(context, "default_token_budget", 8192), Integer(context, "recent_message_limit", 24), Boolean(context, "optimistic_revision")),
            new MemoryPolicy(Boolean(memory, "candidate_only"), Integer(memory, "retrieval_limit", 8), Strings(memory, "allowed_scopes"), Strings(memory, "allowed_kinds")),
            new ToolRuntimePolicy(Text(tools, "provider"), Boolean(tools, "mutation_requires_approval"), Boolean(tools, "serialize_workspace_writes"), Integer(tools, "default_timeout_seconds", 120), Integer(tools, "max_tool_rounds", 4)),
            applications,
            profiles,
            agents);
    }

    public static string NormalizeLayer(string? layer) => string.Equals(layer, "planning", StringComparison.OrdinalIgnoreCase) ? "operation" : layer?.Trim().ToLowerInvariant() ?? string.Empty;

    private static void Validate(
        IReadOnlyDictionary<string, ApplicationModeDefinition> applications,
        IReadOnlyDictionary<string, RuntimeProfileDefinition> profiles,
        IReadOnlyDictionary<string, RuntimeAgentDefinition> agents)
    {
        if (!applications.ContainsKey("conversation") || !applications.ContainsKey("space")) throw new InvalidDataException("Both conversation and space application modes are required.");
        foreach (var app in applications.Values)
        {
            if (!app.AllowedAgentModes.Contains(app.DefaultAgentMode, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"Default agent mode '{app.DefaultAgentMode}' is not allowed by '{app.Id}'.");
            foreach (var mode in app.AllowedAgentModes)
                if (!app.Bindings.TryGetValue(mode, out var profile) || !profiles.ContainsKey(profile)) throw new InvalidDataException($"Application mode '{app.Id}' has no valid profile binding for '{mode}'.");
        }
        if (!agents.TryGetValue("meeting", out var meeting) || meeting.Layer != "operation" || !meeting.DirectUserOutput) throw new InvalidDataException("The operation-layer meeting agent must be the direct user entry.");
        if (agents.Values.Any(a => a.Id != "meeting" && a.DirectUserOutput)) throw new InvalidDataException("Only the meeting agent may set direct_user_output=true.");
        if (agents.Values.Any(a => a.Layer is not ("operation" or "execution"))) throw new InvalidDataException("Agent layers must be operation or execution.");
        // Dual-layer invariant: execution specialists are execution workers, never a third layer.
        // Creation granularity is explicit: temporary/persistent/profile. Legacy agent.spawn is
        // normalized to agent.create_temporary for compatibility.
        var allowedCreation = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent.create_temporary", "agent.create_persistent", "agent.create_profile" };
        foreach (var agent in agents.Values)
        {
            if (agent.Capabilities.Any(c => string.Equals(c, "agent.spawn", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "agent.create_profile", StringComparison.OrdinalIgnoreCase)) && agent.Layer != "operation")
            {
                // task_planner historically carries execution-layer temporary creation; profile creation must remain operation-only.
                if (agent.Capabilities.Any(c => string.Equals(c, "agent.create_profile", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Only operation-layer agents may carry agent.create_profile.");
            }
            if (agent.Capabilities.Any(c => !string.Equals(c, "agent.spawn", StringComparison.OrdinalIgnoreCase) && !allowedCreation.Contains(c) && c.StartsWith("agent.create", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Agent '{agent.Id}' has an unknown agent creation capability.");
        }
        // Profiles must reference known agents and respect layer boundaries.
        foreach (var profile in profiles.Values)
        {
            foreach (var id in profile.OperationAgents)
                if (!agents.ContainsKey(id)) throw new InvalidDataException($"Profile '{profile.Id}' references unknown operation agent '{id}'.");
            foreach (var id in profile.ExecutionAgents)
                if (!agents.ContainsKey(id)) throw new InvalidDataException($"Profile '{profile.Id}' references unknown execution agent '{id}'.");
        }
    }

    private static TomlTable Table(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlTable nested ? nested : throw new InvalidDataException($"Missing TOML table '{key}'.");
    private static string Text(TomlTable table, string key, string fallback = "") => table.TryGetValue(key, out var value) ? value?.ToString() ?? fallback : fallback;
    private static int Integer(TomlTable table, string key, int fallback) => table.TryGetValue(key, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    private static bool Boolean(TomlTable table, string key) => table.TryGetValue(key, out var value) && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    private static string[] Strings(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlArray array ? array.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToArray() : [];
    private static IReadOnlyList<string> NormalizeCapabilities(IReadOnlyList<string> caps)
    {
        var list = new List<string>(caps.Count);
        foreach (var c in caps)
        {
            if (string.Equals(c, "agent.spawn", StringComparison.OrdinalIgnoreCase)) list.Add("agent.create_temporary");
            else list.Add(c);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
