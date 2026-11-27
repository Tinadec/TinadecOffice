using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Manages model provider instances, credentials, routing, capabilities,
/// error normalization, and readiness. Uses IChatClient / ChatClientAgent as entry point.
/// Does not rewrite model HTTP clients.
/// </summary>
public interface IModelProvider
{
    Task<IChatClient?> GetChatClientAsync(
        string? routeId = null,
        CancellationToken cancellationToken = default);

    Task<ModelReadiness> CheckReadinessAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the configured embedding route without exposing provider credentials to callers.</summary>
public interface IEmbeddingProvider
{
    Task<EmbeddingResult> GenerateAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class EmbeddingRequest
{
    public Guid TenantId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public Guid ProjectId { get; init; }
    public IReadOnlyList<string> Inputs { get; init; } = [];
}

public sealed class EmbeddingResult
{
    public bool IsAvailable { get; init; }
    public string? ModelId { get; init; }
    public int Dimension { get; init; }
    public IReadOnlyList<float[]> Vectors { get; init; } = [];
    public string? Detail { get; init; }
}

public sealed class ModelReadiness
{
    public bool IsReady { get; init; }
    public string? StatusMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Provider-neutral token accounting persisted by TinadecCore. Framework-specific
/// usage objects are normalized into this type inside the MAF adapter.
/// </summary>
public sealed record ModelUsage(
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens,
    [property: JsonPropertyName("cached_input_tokens")] long? CachedInputTokens = null,
    [property: JsonPropertyName("reasoning_tokens")] long? ReasoningTokens = null,
    [property: JsonPropertyName("additional_counts")] IReadOnlyDictionary<string, long>? AdditionalCounts = null);

public sealed record ChatResolution
{
    public bool IsAvailable { get; init; }
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelId { get; init; }
    /// <summary>Wire protocol the resolved provider speaks; one of <see cref="ChatProtocols"/>.</summary>
    public string? Protocol { get; init; }
    /// <summary>Local HTTP endpoint of a CLI runtime that serves over HTTP (<c>opencode serve</c>); null for HTTP API providers and for stdio harness sessions.</summary>
    public string? ServerUrl { get; init; }
    /// <summary>Bearer token an HTTP-serving CLI runtime prints on startup, when it issues one. Never used by the stdio ACP channel.</summary>
    public string? Token { get; init; }
    /// <summary>Absolute path to the harness executable; required by every process-backed protocol (<see cref="ChatProtocols.Acp"/>, <see cref="ChatProtocols.HeadlessCli"/>, <see cref="ChatProtocols.Tui"/>) in place of an endpoint URL.</summary>
    public string? BinaryPath { get; init; }
    /// <summary>CLI launch arguments, already materialized from the harness catalog's argv template for the selected channel (e.g. <c>serve --port 4096</c>, <c>--acp</c>).</summary>
    public string? LaunchArgs { get; init; }
    public string? HomePath { get; init; }
    public string? Error { get; init; }
    /// <summary>
    /// Machine-readable reason an unavailable candidate could not be used, so the invocation layer can
    /// report a category the UI already words instead of a bare sentence.
    /// <see cref="ModelHealthPolicy.PoolExhaustedCategory"/> is special: it means <em>every</em> candidate
    /// of the frozen plan is cooling down, which is not the same fact as "this one candidate is broken".
    /// </summary>
    public string? UnavailableCategory { get; init; }
    public Guid? ProviderInstanceId { get; init; }
    public Guid? ProviderVersionId { get; init; }
    public Guid? RouteId { get; init; }
    public Guid? RouteVersionId { get; init; }
    public int CandidatePosition { get; init; }
    public string? StrategySource { get; init; }
    public ModelParameters? Parameters { get; init; }
    /// <summary>Whether this exact provider/model route accepts image parts.</summary>
    public bool SupportsImageInput { get; init; }
    public long MaxImageBytes { get; init; } = ImageInputCapability.DefaultMaxImageBytes;
    public int MaxImagesPerTurn { get; init; } = ImageInputCapability.DefaultMaxImagesPerTurn;

    /// <summary>Optional active run identifier for per-teammate session/process isolation (#36).</summary>
    public string? RunId { get; init; }
    /// <summary>Optional teammate handle within the run (e.g. <c>backend-api</c>).</summary>
    public string? TeammateHandle { get; init; }
    /// <summary>Optional working directory override (e.g. an isolated Git worktree root).</summary>
    public string? WorkingDirectoryOverride { get; init; }
    /// <summary>Optional authorized workspace-relative write scope for the teammate.</summary>
    public IReadOnlyList<string>? WriteScope { get; init; }
    /// <summary>Optional extra environment variables injected into a local CLI/ACP/TUI subprocess.</summary>
    public IReadOnlyDictionary<string, string?>? AdditionalEnvironment { get; init; }
    /// <summary>Optional loopback MCP endpoint URL (<c>http://127.0.0.1:&lt;port&gt;/api/v1/mcp/tinadec</c>) for shared-board access.</summary>
    public string? McpLoopbackEndpoint { get; init; }
    /// <summary>Optional run- and teammate-scoped bearer token issued by <see cref="ITeamBoardMcpBridge"/>.</summary>
    public string? McpScopedBearerToken { get; init; }
    /// <summary>Human-readable runtime badge (e.g. <c>ACP · Antigravity</c>, <c>CLI · Claude Code</c>, <c>API · gpt-5</c>).</summary>
    public string? RuntimeBadge { get; init; }
    /// <summary>Target controller device ID when <see cref="Protocol"/> is <see cref="ChatProtocols.PeerProxy"/>.</summary>
    public string? ProxyControllerDeviceId { get; init; }
    /// <summary>Short-lived single-session token for <see cref="ChatProtocols.PeerProxy"/> (never an upstream API key).</summary>
    public string? ProxySessionToken { get; init; }
}

/// <summary>
/// Canonical chat wire-protocol identifiers carried by <see cref="ChatResolution.Protocol"/>
/// and stored in provider configuration JSON (<c>protocol</c> key). This axis answers <em>which
/// dialect</em> is spoken; it is not <em>how the process is reached</em>
/// (<see cref="AgentChannels"/>) and not <em>local process or HTTP API</em>
/// (<c>connection_kind</c>). Resolve a harness's protocol through
/// <see cref="HarnessCatalog.ResolveProtocol"/>, never from its driver name alone.
/// </summary>
public static class ChatProtocols
{
    /// <summary>OpenAI-compatible <c>/chat/completions</c> protocol (default).</summary>
    public const string OpenAiChat = "openai-chat";

    /// <summary>OpenAI Responses API protocol.</summary>
    public const string OpenAiResponses = "openai-responses";

    /// <summary>Anthropic Messages API protocol (<c>/v1/messages</c>).</summary>
    public const string AnthropicMessages = "anthropic-messages";

    /// <summary>
    /// Agent Client Protocol: a persistent JSON-RPC 2.0 session carried over the harness
    /// subprocess's stdio, one frame per line (NDJSON). Two details the wire format fixes and the
    /// serializer has to honor: field names are <em>camelCase</em> (<c>sessionId</c>,
    /// <c>sessionUpdate</c>, <c>stopReason</c>) and <c>protocolVersion</c> is the <em>integer</em>
    /// 1 — neither matches Core's snake_case API-boundary convention, so ACP needs its own
    /// serialization context rather than a reused one.
    /// <para>
    /// The client declares <c>fs.readTextFile</c>, <c>fs.writeTextFile</c> and <c>terminal</c>
    /// capabilities as false, which is not a restriction: it means Core does not act as the
    /// harness's file or terminal proxy. The harness still reads and writes its session working
    /// directory with its own tools, so the session's <c>cwd</c> is what governs its blast radius.
    /// </para>
    /// </summary>
    public const string Acp = "acp";

    /// <summary>
    /// opencode <c>serve</c> protocol: an HTTP/SSE session surface on <c>http://127.0.0.1:&lt;port&gt;</c>
    /// (POST /session, POST /session/{id}/message, GET /session/{id}/event).
    /// </summary>
    public const string OpencodeServe = "opencode-serve";

    /// <summary>
    /// Headless CLI protocol: the harness is invoked once with the prompt on argv or stdin, emits
    /// its vendor JSON envelope on stdout, and exits.
    /// </summary>
    public const string HeadlessCli = "headless-cli";

    /// <summary>
    /// TUI protocol: a full-screen harness hosted in a real PTY. Core submits one prompt, projects
    /// the settled visible transcript into an assistant answer, then tears down the one-shot terminal.
    /// </summary>
    public const string Tui = "tui";

    /// <summary>
    /// Peer provider proxy protocol: a zero-API-key worker forwards chat/tool inference over an
    /// authenticated mTLS peer tunnel to the controller's model provider via local Gateway loopback.
    /// </summary>
    public const string PeerProxy = "peer_proxy";

    /// <summary>
    /// Normalizes a stored protocol value; blank or unknown values fall back to
    /// <see cref="OpenAiChat"/> so legacy configurations keep working.
    /// </summary>
    public static string Normalize(string? protocol) => protocol?.Trim().ToLowerInvariant() switch
    {
        OpenAiResponses => OpenAiResponses,
        AnthropicMessages => AnthropicMessages,
        Acp => Acp,
        OpencodeServe => OpencodeServe,
        HeadlessCli => HeadlessCli,
        Tui => Tui,
        PeerProxy or "peer-proxy" => PeerProxy,
        _ => OpenAiChat
    };

    /// <summary>
    /// Infers a protocol from a provider driver name when no explicit protocol and no harness
    /// channel are configured. Covers HTTP API drivers <em>only</em>.
    /// <para>
    /// A harness's protocol is a property of a (harness, channel) pair, so it comes from
    /// <see cref="HarnessCatalog.ProtocolFor"/>. The removed rows here mapped <c>claude-cli</c>,
    /// <c>codex-cli</c> and <c>cursor-acp</c> all to <see cref="Acp"/>; none of those binaries
    /// offered an ACP endpoint, and the driver name encoding a channel
    /// (<c>-cli</c>, <c>-acp</c>) was the same category error this narrowing ends.
    /// </para>
    /// <para>
    /// <c>claude</c> below is the Anthropic HTTP API driver and stays distinct from the
    /// <c>claude-code</c> catalog harness id that names the vendor's CLI.
    /// </para>
    /// </summary>
    public static string InferFromDriver(string? driver) => driver?.Trim().ToLowerInvariant() switch
    {
        "anthropic" or "claude" => AnthropicMessages,
        "openai-responses" => OpenAiResponses,
        _ => OpenAiChat
    };

    /// <summary>
    /// True when Core reaches the peer by spawning a process and talking to its stdio, so the
    /// resolution needs a <c>binary_path</c> and has no endpoint URL. <see cref="OpencodeServe"/> is
    /// excluded on purpose: that harness is reached over HTTP, which is why it kept working when the
    /// invented stdio-over-HTTP dialect was removed.
    /// </summary>
    public static bool IsProcessTransport(string? protocol) => Normalize(protocol) switch
    {
        Acp or HeadlessCli or Tui => true,
        _ => false
    };

    /// <summary>
    /// True when the peer chooses which model answers, so Core must not require a model id in the
    /// provider configuration. Covers every local-agent protocol, both transports.
    /// </summary>
    public static bool IsModelChosenByHarness(string? protocol) => Normalize(protocol) switch
    {
        Acp or HeadlessCli or Tui or OpencodeServe or PeerProxy => true,
        _ => false
    };

    /// <summary>
    /// The gap that stops Core from driving a local channel today, or <c>null</c> when this build can
    /// drive it. One owner for the wording, because discovery offers the channel, the chat factory
    /// refuses it, and the model center explains the refusal — three places that read as three
    /// different answers if each invents its own sentence.
    /// <para>
    /// <see cref="HeadlessCli"/> and <see cref="Tui"/> have no entry here because chat clients now
    /// exist. Headless CLI is still not drivable for a harness whose answer frame this build has
    /// never captured — that verdict is
    /// per-harness and lives in <see cref="HarnessCatalog.HasVerifiedHeadlessEnvelope"/>, since the
    /// envelope vocabulary belongs to the vendor, not to the protocol.
    /// </para>
    /// </summary>
    public static string? HarnessClientGap(string? protocol) => Normalize(protocol) switch
    {
        _ => null
    };

    /// <summary>
    /// True when this build can start a provider and carry a turn over that protocol: a harness
    /// protocol with a chat client, or an HTTP API. Discovery answers with it, so a channel that is
    /// configured but not implemented cannot be presented as a button that works.
    /// </summary>
    public static bool IsDrivable(string? protocol)
    {
        var normalized = Normalize(protocol);
        return HarnessClientGap(normalized) is null
            && normalized switch { Acp or OpencodeServe or HeadlessCli or Tui or PeerProxy => true, _ => false };
    }
}

/// <summary>
/// Ephemeral in-memory session/token state for Wave 3 Peer Provider Proxy.
/// Never persists anything to SQLite, SecretStore, TOML, or logs, and partitions controller
/// token tables by host identity key so multi-instance in-process tests stay strictly isolated
/// while <c>user</c> and <c>project</c> storage scopes within one host share state seamlessly.
/// </summary>
public static class PeerProviderProxyHostRegistry
{
    public const int DefaultTokenTtlSeconds = 1800;
    public const int DefaultMaxRequests = 64;

    public sealed record IssuedProxyTokenRecord(
        string Token,
        string ControllerDeviceId,
        string WorkerDeviceId,
        string? DelegationId,
        string? SessionId,
        long ExpiresAtMs,
        int MaxRequests)
    {
        private int _usedRequests;
        private int _revoked;

        public int UsedRequests => Volatile.Read(ref _usedRequests);
        public bool IsRevoked => Volatile.Read(ref _revoked) != 0;

        public bool TryConsume(long nowMs, string? verifiedWorkerDeviceId, out string denyReason)
        {
            if (IsRevoked)
            {
                denyReason = "Proxy session token has been revoked.";
                return false;
            }
            if (nowMs > ExpiresAtMs)
            {
                denyReason = "Proxy session token has expired.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(WorkerDeviceId)
                && !string.IsNullOrWhiteSpace(verifiedWorkerDeviceId)
                && !string.Equals(WorkerDeviceId, verifiedWorkerDeviceId.Trim(), StringComparison.Ordinal))
            {
                denyReason = $"Proxy session token is not bound to worker device '{verifiedWorkerDeviceId}'.";
                return false;
            }
            var next = Interlocked.Increment(ref _usedRequests);
            if (next > MaxRequests)
            {
                denyReason = $"Proxy session token exceeded max_requests ({MaxRequests}).";
                return false;
            }
            denyReason = string.Empty;
            return true;
        }

        public void Revoke() => Interlocked.Exchange(ref _revoked, 1);
    }

    public sealed record WorkerProxyContextEntry(
        Guid SessionId,
        Guid? RunId,
        string? DelegationId,
        string ControllerDeviceId,
        string ProxySessionToken,
        bool ForceProviderProxy,
        long ExpiresAtMs,
        string? GatewayLoopbackUrl = null,
        string? ModelHint = null);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentDictionary<string, IssuedProxyTokenRecord>> TokensByHost = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, WorkerProxyContextEntry> WorkerSessions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, WorkerProxyContextEntry> WorkerRuns = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, WorkerProxyContextEntry> HostDefaultWorkerContext = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> HostGatewayLoopbackUrls = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> HostControllerProxyAllowed = new();

    public static int ResolveHostPartitionKey(object? sharedHostAnchor) =>
        sharedHostAnchor is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(sharedHostAnchor);

    public static IssuedProxyTokenRecord IssueToken(
        int hostKey,
        string controllerDeviceId,
        string workerDeviceId,
        string? delegationId = null,
        string? sessionId = null,
        long? expiresAtMs = null,
        int? maxRequests = null,
        long? nowMs = null)
    {
        var currentNow = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effectiveExpiry = expiresAtMs is > 0 ? expiresAtMs.Value : currentNow + (DefaultTokenTtlSeconds * 1000L);
        var effectiveMax = maxRequests is > 0 ? Math.Min(maxRequests.Value, 4096) : DefaultMaxRequests;
        var randomBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        var token = "ppt_v1_" + Convert.ToHexString(randomBytes).ToLowerInvariant();
        var record = new IssuedProxyTokenRecord(
            token,
            controllerDeviceId?.Trim() ?? string.Empty,
            workerDeviceId?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(delegationId) ? null : delegationId.Trim(),
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
            effectiveExpiry,
            effectiveMax);

        var table = TokensByHost.GetOrAdd(hostKey, _ => new());
        PurgeExpiredTokens(table, currentNow);
        table[token] = record;
        return record;
    }

    public static bool TryValidateAndConsumeToken(
        int hostKey,
        string? rawToken,
        string? verifiedWorkerDeviceId,
        long? nowMs,
        out IssuedProxyTokenRecord? matched,
        out string denyReason)
    {
        matched = null;
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            denyReason = "Missing x-tinadec-peer-proxy-token.";
            return false;
        }
        var trimmed = rawToken.Trim();
        if (!TokensByHost.TryGetValue(hostKey, out var table) || !table.TryGetValue(trimmed, out var record))
        {
            denyReason = "Invalid or unknown proxy_session_token.";
            return false;
        }
        matched = record;
        return record.TryConsume(nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), verifiedWorkerDeviceId, out denyReason);
    }

    public static int RevokeWorkerTokens(int hostKey, string workerDeviceId)
    {
        if (string.IsNullOrWhiteSpace(workerDeviceId) || !TokensByHost.TryGetValue(hostKey, out var table))
            return 0;
        var count = 0;
        foreach (var pair in table)
        {
            if (string.Equals(pair.Value.WorkerDeviceId, workerDeviceId.Trim(), StringComparison.Ordinal))
            {
                pair.Value.Revoke();
                table.TryRemove(pair.Key, out _);
                count++;
            }
        }
        return count;
    }

    public static void SetControllerProxyAllowed(int hostKey, bool allowed) =>
        HostControllerProxyAllowed[hostKey] = allowed;

    public static bool IsControllerProxyAllowed(int hostKey) =>
        !HostControllerProxyAllowed.TryGetValue(hostKey, out var allowed) || allowed;

    public static void SetHostGatewayLoopbackUrl(int hostKey, string? loopbackUrl)
    {
        if (TryNormalizeLoopbackBaseUrl(loopbackUrl, out var normalized))
        {
            HostGatewayLoopbackUrls[hostKey] = normalized;
        }
    }

    public static string? GetHostGatewayLoopbackUrl(int hostKey) =>
        HostGatewayLoopbackUrls.TryGetValue(hostKey, out var url) ? url : null;

    public static void BindWorkerSession(int hostKey, WorkerProxyContextEntry entry)
    {
        if (entry.SessionId != Guid.Empty)
        {
            WorkerSessions[entry.SessionId] = entry;
        }
        if (entry.RunId is { } runId && runId != Guid.Empty)
        {
            WorkerRuns[runId] = entry;
        }
        if (TryNormalizeLoopbackBaseUrl(entry.GatewayLoopbackUrl, out var normalizedGw))
        {
            HostGatewayLoopbackUrls[hostKey] = normalizedGw;
        }
        HostDefaultWorkerContext[hostKey] = entry;
    }

    public static WorkerProxyContextEntry? TryGetWorkerSession(Guid sessionId, int? hostKey = null, long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (sessionId != Guid.Empty && WorkerSessions.TryGetValue(sessionId, out var found))
        {
            if (found.ExpiresAtMs <= 0 || now <= found.ExpiresAtMs)
                return found;
            WorkerSessions.TryRemove(sessionId, out _);
        }
        if (hostKey is { } hk && HostDefaultWorkerContext.TryGetValue(hk, out var fallback))
        {
            if (fallback.ExpiresAtMs <= 0 || now <= fallback.ExpiresAtMs)
                return fallback;
        }
        return null;
    }

    public static WorkerProxyContextEntry? TryGetWorkerRun(Guid runId, long? nowMs = null)
    {
        if (runId == Guid.Empty || !WorkerRuns.TryGetValue(runId, out var found))
            return null;
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (found.ExpiresAtMs <= 0 || now <= found.ExpiresAtMs)
            return found;
        WorkerRuns.TryRemove(runId, out _);
        return null;
    }

    public static void ClearWorkerControllerBindings(int hostKey, string controllerDeviceId)
    {
        if (string.IsNullOrWhiteSpace(controllerDeviceId)) return;
        var target = controllerDeviceId.Trim();
        foreach (var pair in WorkerSessions)
        {
            if (string.Equals(pair.Value.ControllerDeviceId, target, StringComparison.Ordinal))
                WorkerSessions.TryRemove(pair.Key, out _);
        }
        foreach (var pair in WorkerRuns)
        {
            if (string.Equals(pair.Value.ControllerDeviceId, target, StringComparison.Ordinal))
                WorkerRuns.TryRemove(pair.Key, out _);
        }
        if (HostDefaultWorkerContext.TryGetValue(hostKey, out var def)
            && string.Equals(def.ControllerDeviceId, target, StringComparison.Ordinal))
        {
            HostDefaultWorkerContext.TryRemove(hostKey, out _);
        }
    }

    public static bool TryNormalizeLoopbackBaseUrl(string? rawUrl, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (!IsStrictLoopbackHost(uri.Host)) return false;
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port);
        normalized = builder.Uri.ToString().TrimEnd('/');
        return true;
    }

    public static bool IsStrictLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var trimmed = host.Trim().Trim('[', ']');
        return string.Equals(trimmed, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static void PurgeExpiredTokens(
        System.Collections.Concurrent.ConcurrentDictionary<string, IssuedProxyTokenRecord> table,
        long nowMs)
    {
        if (table.Count < 128) return;
        foreach (var pair in table)
        {
            if (pair.Value.IsRevoked || pair.Value.ExpiresAtMs < nowMs)
                table.TryRemove(pair.Key, out _);
        }
    }
}
