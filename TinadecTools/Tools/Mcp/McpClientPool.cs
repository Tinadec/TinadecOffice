using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using TinadecTools.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecTools.Runtime.Sandbox;

namespace TinadecTools.Tools.Mcp;

internal sealed class McpClientPool : IAsyncDisposable
{
    private readonly Dictionary<string, ClientEntry> _clients = new(StringComparer.Ordinal);
    private readonly HashSet<string> _releasedRuns = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    // A lease holds a frozen resource revision between calls. Active call refs separately protect
    // calls already underway when Core cancels/releases a run. A saved revision is never a kill.
    private sealed class ClientEntry
    {
        public ClientEntry(string key, McpServerConfig config, bool governed)
        {
            Key = key;
            Client = new Lazy<Task<McpClient>>(() => CreateAsync(config, governed));
        }
        public string Key { get; }
        public CancellationTokenSource Shutdown { get; } = new();
        public Lazy<Task<McpClient>> Client { get; }
        public HashSet<string> Runs { get; } = new(StringComparer.Ordinal);
        public int ActiveCalls { get; set; }
        public bool Standalone { get; set; }

        private async Task<McpClient> CreateAsync(McpServerConfig resource, bool governed)
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(Shutdown.Token);
            // Each waiter has its own configured deadline. Sharing a handshake must not let the
            // first agent's short deadline cancel another agent's longer one. Only the immutable
            // provider ceiling bounds shared initialization; releasing its last owner cancels it.
            if (governed) startup.CancelAfter(1_800_000);
            return await McpClientPool.CreateAsync(resource, startup.Token).ConfigureAwait(false);
        }
    }

    internal int CachedClientCount { get { lock (_gate) return _clients.Count; } }

    private ClientEntry BeginCall(McpServerConfig config)
    {
        var context = ToolExecutionContext.Current;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (context?.RunId is { } runId && _releasedRuns.Contains(runId))
                throw new InvalidOperationException($"Execution context '{runId}' has already been released.");
            var key = PoolKey(config);
            if (!_clients.TryGetValue(key, out var entry))
                _clients.Add(key, entry = new ClientEntry(key, config, context is not null));
            entry.ActiveCalls++;
            if (context is null) entry.Standalone = true;
            else if (context.RunId is { } leaseId) entry.Runs.Add(leaseId);
            return entry;
        }
    }

    private async ValueTask EndCallAsync(ClientEntry entry)
    {
        bool remove;
        lock (_gate)
        {
            entry.ActiveCalls--;
            // Failed connections are retriable and must not remain cached under a live run lease.
            if (entry.Client.IsValueCreated && entry.Client.Value.IsCompleted && !entry.Client.Value.IsCompletedSuccessfully)
            {
                entry.Runs.Clear();
                entry.Standalone = false;
            }
            remove = RemoveIfUnused(entry);
        }
        if (remove) await DisposeEntryAsync(entry).ConfigureAwait(false);
    }

    private bool RemoveIfUnused(ClientEntry entry)
    {
        if (entry.ActiveCalls != 0 || entry.Standalone || entry.Runs.Count != 0) return false;
        if (!_clients.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry)) return false;
        return _clients.Remove(entry.Key);
    }

    public async ValueTask ReleaseRunAsync(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var retired = new List<ClientEntry>();
        lock (_gate)
        {
            _releasedRuns.Add(runId);
            foreach (var entry in _clients.Values.ToArray())
            {
                entry.Runs.Remove(runId);
                if (RemoveIfUnused(entry)) retired.Add(entry);
            }
        }
        foreach (var entry in retired) await DisposeEntryAsync(entry).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolSummary>> ListToolsAsync(McpServerConfig config, bool includeSchema, CancellationToken cancellationToken = default)
    {
        var timeoutMs = ToolExecutionContext.Current?.Integer("mcp", "timeout_ms", 30_000, 1, 1_800_000);
        var started = Stopwatch.GetTimestamp();
        using var deadline = CallDeadline(cancellationToken, timeoutMs);
        cancellationToken = deadline.Token;
        var entry = BeginCall(config);
        try
        {
            var client = await entry.Client.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            CheckDeadline(deadline, started, timeoutMs);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            CheckDeadline(deadline, started, timeoutMs);
            return tools.Select(tool => ToSummary(tool, includeSchema)).ToArray();
        }
        finally { await EndCallAsync(entry).ConfigureAwait(false); }
    }

    public async Task<JsonElement> InvokeAsync(McpServerConfig config, string toolName, JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        var timeoutMs = ToolExecutionContext.Current?.Integer("mcp", "timeout_ms", 30_000, 1, 1_800_000);
        var started = Stopwatch.GetTimestamp();
        using var deadline = CallDeadline(cancellationToken, timeoutMs);
        cancellationToken = deadline.Token;
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var entry = BeginCall(config);
        try
        {
            var client = await entry.Client.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            CheckDeadline(deadline, started, timeoutMs);
            var dictionary = McpJsonArguments.ToDictionary(arguments);
            var result = await client.CallToolAsync(toolName, dictionary, cancellationToken: cancellationToken).ConfigureAwait(false);
            CheckDeadline(deadline, started, timeoutMs);
            return JsonSerializer.SerializeToElement(result, McpJsonContext.Default.CallToolResult);
        }
        finally { await EndCallAsync(entry).ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        var retired = new List<ClientEntry>();
        lock (_gate)
        {
            _disposed = true;
            foreach (var entry in _clients.Values.ToArray())
            {
                entry.Runs.Clear();
                entry.Standalone = false;
                if (RemoveIfUnused(entry)) retired.Add(entry);
            }
        }
        foreach (var entry in retired) await DisposeEntryAsync(entry).ConfigureAwait(false);
    }

    private static async ValueTask DisposeEntryAsync(ClientEntry entry)
    {
        try
        {
            // A cancelled caller may leave an initialization in flight. Once no owner/call needs
            // it, cancellation reclaims that transport instead of waiting for a full handshake.
            entry.Shutdown.Cancel();
            if (entry.Client.IsValueCreated)
            {
                var client = await entry.Client.Value.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch { /* Best-effort shutdown; failed handshakes have no reusable client. */ }
        finally { entry.Shutdown.Dispose(); }
    }

    private static Task<McpClient> CreateAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var transportName = (config.Transport ?? "").Trim().ToLowerInvariant();
        if (transportName is "http" or "sse") return CreateHttpAsync(config, transportName, cancellationToken);

        if (ToolExecutionContext.Current is not null && (config.ProgramStatus is "not_installed" or "outdated" or "invalid" or "needs_reinstall"
            || Path.GetFileNameWithoutExtension(config.Command).ToLowerInvariant() is "npx" or "uvx"))
            throw new InvalidOperationException("This MCP resource is registered, but its pinned program has not been explicitly installed. Review and approve the program installation first; no dependency was downloaded.");
        IDictionary<string, string?>? environment = SandboxEnvironment.Build(null, null).ToDictionary(x => x.Key, x => (string?)x.Value,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (config.Env is not null) foreach (var entry in config.Env) environment[entry.Key] = entry.Value;
        environment.Remove("TINADEC_HOST_CONTROL_TOKEN");
        if (ToolExecutionContext.Current is { StorageRoot: { } root } context)
        {
            var variables = SandboxEnvironment.Build(null, null).ToDictionary(x => x.Key, x => (string?)x.Value,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (config.Env is not null) foreach (var entry in config.Env) variables[entry.Key] = entry.Value;
            var cache = Path.Combine(root, "cache", "mcp");
            var temporary = Path.Combine(root, "temp", "mcp");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(temporary);
            // Registry credentials may configure the server; they cannot redirect owned caches.
            variables["NPM_CONFIG_CACHE"] = Path.Combine(cache, "npm");
            variables["UV_CACHE_DIR"] = Path.Combine(cache, "uv");
            variables["PIP_CACHE_DIR"] = Path.Combine(cache, "pip");
            variables["NUGET_PACKAGES"] = Path.Combine(cache, "nuget");
            variables["TMPDIR"] = variables["TEMP"] = variables["TMP"] = temporary;
            variables.Remove("TINADEC_HOST_CONTROL_TOKEN");
            environment = variables;
        }
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = string.IsNullOrWhiteSpace(config.Name) ? config.Id : config.Name,
            Command = config.Command,
            Arguments = config.Args.ToArray(),
            WorkingDirectory = config.Cwd,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment
        });

        return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// HTTP and legacy SSE servers have no child process, no managed program and no environment.
    /// Their credentials are header values resolved from Core's secret store into the frozen call.
    /// </summary>
    private static Task<McpClient> CreateHttpAsync(McpServerConfig config, string transportName, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("This MCP resource has no valid http(s) URL.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.Headers is not null)
            foreach (var header in config.Headers)
                if (header.Value is not null) headers[header.Key] = header.Value;
        var options = new HttpClientTransportOptions
        {
            Name = string.IsNullOrWhiteSpace(config.Name) ? config.Id : config.Name,
            Endpoint = endpoint,
            TransportMode = transportName == "sse" ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
            ConnectionTimeout = TimeSpan.FromSeconds(60),
        };
        return McpClient.CreateAsync(new HttpClientTransport(options, NullLoggerFactory.Instance), cancellationToken: cancellationToken);
    }

    private static CancellationTokenSource CallDeadline(CancellationToken cancellationToken, int? timeoutMs)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs is { } milliseconds) deadline.CancelAfter(milliseconds);
        return deadline;
    }

    private static void CheckDeadline(CancellationTokenSource deadline, long started, int? timeoutMs)
    {
        if (timeoutMs is { } milliseconds && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds)
            deadline.Cancel();
        deadline.Token.ThrowIfCancellationRequested();
    }

    // Reusing only the user-facing id silently kept old commands and environments after a save.
    // Old fingerprints remain available while their runs/calls hold leases; saving never kills
    // live clients. Core releases a run through the private control channel when it ends.
    internal static string PoolKey(McpServerConfig config)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("storage_id", ToolExecutionContext.Current?.StorageId);
            writer.WriteString("storage_root", ToolExecutionContext.Current?.StorageRoot);
            writer.WriteString("program_root", config.ProgramRoot);
            writer.WriteString("program_hash", config.ProgramHash);
            writer.WriteString("resource_id", config.ResourceId ?? config.Id);
            writer.WriteNumber("revision", config.Revision);
            writer.WriteString("transport", config.Transport);
            writer.WriteString("url", config.Url);
            writer.WriteString("command", config.Command);
            writer.WriteString("cwd", config.Cwd);
            writer.WriteStartArray("args");
            foreach (var argument in config.Args) writer.WriteStringValue(argument);
            writer.WriteEndArray();
            writer.WriteStartObject("env");
            if (config.Env is not null)
                foreach (var variable in config.Env.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    writer.WriteString(variable.Key, variable.Value);
            writer.WriteEndObject();
            writer.WriteStartObject("headers");
            if (config.Headers is not null)
                foreach (var header in config.Headers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    writer.WriteString(header.Key, header.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static McpToolSummary ToSummary(McpClientTool tool, bool includeSchema)
    {
        return new McpToolSummary
        {
            Id = tool.Name,
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = includeSchema ? tool.JsonSchema.Clone() : null
        };
    }
}
