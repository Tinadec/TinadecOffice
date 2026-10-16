using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.Runtime;

/// <summary>
/// Server-side model connectivity probe (plan §5.3 item 2). Resolves the configured
/// <c>chat</c> route, then issues one minimal real completion (1 output token, 10s
/// timeout) through the same client construction path used by real runs
/// (<see cref="IAgentChatClientFactory.CreateAsync"/>, which mirrors
/// <c>AgentChatClientFactory.CreateBrandedOpenAiClient</c> for OpenAI-compatible
/// providers). Results are cached in memory for 60 seconds keyed by the resolved
/// provider/model/endpoint fingerprint so readiness polling does not hammer the
/// provider endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Environment variable <c>TINADEC_MODEL_BASE_URL_OVERRIDE</c> replaces the stored
/// provider <c>base_url</c> at client-construction time only (the database row keeps
/// its value). Local fixture E2E runs depend on this to point Core at
/// <c>http://127.0.0.1:48735/v1</c> instead of the real provider endpoint.
/// </para>
/// <para>
/// Secrets never appear in results: only endpoint/model metadata and exception-type
/// summaries are returned.
/// </para>
/// </remarks>
public sealed class ModelProbeService
{
    /// <summary>Overrides the provider base_url for every probe and model client that goes through readiness probes.</summary>
    public const string BaseUrlOverrideEnvVar = "TINADEC_MODEL_BASE_URL_OVERRIDE";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IChatResolver _resolver;
    private readonly IAgentChatClientFactory _factory;
    private readonly ILogger<ModelProbeService> _logger;
    private readonly object _lock = new();
    private (string Fingerprint, DateTimeOffset ExpiresAt, ReadinessItem Item)? _cache;

    public ModelProbeService(IChatResolver resolver, IAgentChatClientFactory factory, ILogger<ModelProbeService> logger)
    {
        _resolver = resolver;
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cached probe item when it is still fresh, otherwise null.
    /// Readiness treats null as <c>unavailable</c> (probe not executed yet) without
    /// degrading the overall receipt.
    /// </summary>
    public ReadinessItem? Peek()
    {
        lock (_lock)
        {
            if (_cache is { } entry && entry.ExpiresAt > DateTimeOffset.UtcNow) return entry.Item;
            return null;
        }
    }

    /// <summary>
    /// Runs the connectivity probe. With <paramref name="force"/> the cached result is
    /// discarded and a real request is sent; otherwise a fresh cached result wins.
    /// </summary>
    public async Task<ReadinessItem> ProbeAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var resolution = await _resolver.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable)
        {
            return Store("unavailable", now, new ReadinessItem
            {
                Id = Id,
                Status = "blocked",
                ReasonCode = "probe_route_unavailable",
                Reason = $"The model probe cannot run: {resolution.Error ?? "the chat route is unavailable"}",
                Action = "Finish the chat route and provider setup in the model center (enter the API key, enable the provider), then retry.",
                CheckedAt = now,
                Data = new { base_url_override = GetOverrideBaseUrl(), error = resolution.Error }
            });
        }

        var protocol = ChatProtocols.Normalize(resolution.Protocol);
        if (ChatProtocols.IsModelChosenByHarness(protocol))
        {
            return Store(BuildFingerprint(resolution, resolution.BaseUrl ?? string.Empty), now, new ReadinessItem
            {
                Id = Id,
                Status = "unavailable",
                ReasonCode = "probe_not_applicable",
                Reason = $"Local agent protocol {protocol} lets the harness choose the model; the server-side HTTP probe does not apply.",
                Action = "Verify connectivity with a real conversation through that CLI runtime.",
                CheckedAt = now,
                Data = new { model_id = resolution.ModelId, protocol }
            });
        }

        var overrideBaseUrl = GetOverrideBaseUrl();
        var effectiveBaseUrl = overrideBaseUrl ?? resolution.BaseUrl
            ?? throw new InvalidOperationException("Chat resolution has no base_url.");
        var fingerprint = BuildFingerprint(resolution, effectiveBaseUrl);

        if (!force)
        {
            lock (_lock)
            {
                if (_cache is { } entry && entry.ExpiresAt > now && entry.Fingerprint == fingerprint)
                {
                    _logger.LogDebug("Model probe served from cache (fingerprint {Fingerprint}).", fingerprint);
                    return entry.Item;
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        ReadinessItem item;
        try
        {
            var probeResolution = overrideBaseUrl is null
                ? resolution
                : WithBaseUrl(resolution, overrideBaseUrl);
            var client = await _factory.CreateAsync(probeResolution, cancellationToken).ConfigureAwait(false);
            var options = new ChatOptions { MaxOutputTokens = 1 };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);
            var response = await client
                .GetResponseAsync("Connectivity probe. Reply with the single word: ok.", options, timeoutCts.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();
            item = new ReadinessItem
            {
                Id = Id,
                Status = "ready",
                ReasonCode = "probe_ready",
                Reason = $"The model endpoint answered ({stopwatch.ElapsedMilliseconds} ms).",
                CheckedAt = now,
                Data = new
                {
                    model_id = resolution.ModelId,
                    model = resolution.Model,
                    base_url = effectiveBaseUrl,
                    base_url_overridden = overrideBaseUrl is not null,
                    protocol,
                    duration_ms = stopwatch.ElapsedMilliseconds,
                    response_excerpt = Truncate(response.Text, 40)
                }
            };
            _logger.LogInformation("Model probe succeeded for {ModelId} via {BaseUrl} in {ElapsedMs}ms.",
                resolution.ModelId, effectiveBaseUrl, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            item = new ReadinessItem
            {
                Id = Id,
                Status = "degraded",
                ReasonCode = "probe_timeout",
                Reason = $"The model endpoint did not answer within {ProbeTimeout.TotalSeconds:0} s (timeout). Endpoint {effectiveBaseUrl}.",
                Action = $"Check network connectivity and base_url (currently {effectiveBaseUrl}); in fixture mode make sure scripts/model-fixture.mjs is running.",
                CheckedAt = now,
                Data = new { model_id = resolution.ModelId, base_url = effectiveBaseUrl, duration_ms = stopwatch.ElapsedMilliseconds, timeout_seconds = (int)ProbeTimeout.TotalSeconds }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            var summary = DescribeException(ex);
            var (code, action) = SuggestAction(ex, effectiveBaseUrl);
            item = new ReadinessItem
            {
                Id = Id,
                Status = "degraded",
                ReasonCode = code,
                Reason = $"The model probe call failed: {summary}",
                Action = action,
                CheckedAt = now,
                Data = new { model_id = resolution.ModelId, base_url = effectiveBaseUrl, exception_type = ex.GetType().Name, error = summary }
            };
            _logger.LogWarning(ex, "Model probe failed for {ModelId} via {BaseUrl}.", resolution.ModelId, effectiveBaseUrl);
        }

        return Store(fingerprint, now, item);
    }

    public const string Id = "model_probe";

    private ReadinessItem Store(string fingerprint, DateTimeOffset checkedAt, ReadinessItem item)
    {
        lock (_lock)
        {
            _cache = (fingerprint, checkedAt.Add(CacheTtl), item);
        }
        return item;
    }

    /// <summary>
    /// Provider identity plus the protocol. The protocol belongs here because the probe's answer is a
    /// statement about a dialect, not about a row: flipping a provider from <c>openai-chat</c> to
    /// <c>acp</c> keeps every id and the model name, and without this key readiness would keep serving
    /// the HTTP probe's "ready" for the rest of the cache window against a provider that is now a
    /// stdio session.
    /// </summary>
    private static string BuildFingerprint(ChatResolution resolution, string baseUrl) =>
        $"{resolution.ProviderInstanceId}:{resolution.ProviderVersionId}:{resolution.Model}:{baseUrl}:{ChatProtocols.Normalize(resolution.Protocol)}";

    private static string? GetOverrideBaseUrl()
    {
        var value = Environment.GetEnvironmentVariable(BaseUrlOverrideEnvVar);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Rebuilds the resolution with the overridden base_url; all other fields pass through.</summary>
    private static ChatResolution WithBaseUrl(ChatResolution value, string baseUrl) => new()
    {
        IsAvailable = value.IsAvailable,
        BaseUrl = baseUrl,
        Model = value.Model,
        ApiKey = value.ApiKey,
        ModelId = value.ModelId,
        Protocol = value.Protocol,
        ServerUrl = value.ServerUrl,
        Token = value.Token,
        BinaryPath = value.BinaryPath,
        LaunchArgs = value.LaunchArgs,
        HomePath = value.HomePath,
        Error = value.Error,
        ProviderInstanceId = value.ProviderInstanceId,
        ProviderVersionId = value.ProviderVersionId,
        RouteId = value.RouteId,
        RouteVersionId = value.RouteVersionId,
        CandidatePosition = value.CandidatePosition,
        StrategySource = value.StrategySource
    };

    /// <summary>Exception-type summary without any credential material.</summary>
    private static string DescribeException(Exception ex)
    {
        var message = ex.Message?.ReplaceLineEndings(" ") ?? string.Empty;
        return $"{ex.GetType().Name}: {Truncate(message, 240)}";
    }

    /// <summary>The failure's reason code (clients word it) and an English diagnostic action.</summary>
    private static (string Code, string Action) SuggestAction(Exception ex, string baseUrl)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("401") || message.Contains("Unauthorized") || message.Contains("invalid_api_key"))
            return ("probe_unauthorized", "The API key was rejected (HTTP 401): enter a valid API key for the provider in the model center and retry.");
        if (message.Contains("403") || message.Contains("Forbidden"))
            return ("probe_forbidden", "The endpoint refused access (HTTP 403): check the account permissions and the project the key belongs to.");
        if (message.Contains("404") || message.Contains("Not Found"))
            return ("probe_not_found", $"The endpoint returned 404: check that base_url is right (currently {baseUrl}); OpenAI-compatible endpoints usually end with /v1.");
        if (message.Contains("429") || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return ("probe_rate_limited", "The endpoint is rate limited (HTTP 429): retry later or use a key with quota left.");
        if (message.Contains("refused") || message.Contains("connect failed", StringComparison.OrdinalIgnoreCase))
            return ("probe_connection_refused", $"Cannot connect to {baseUrl}: make sure the endpoint process is running (in fixture mode start scripts/model-fixture.mjs first).");
        return ("probe_failed", $"Check that endpoint {baseUrl} is available and retry; the Core log has the full exception.");
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? value
        : value.Length <= maxLength ? value
        : value[..maxLength] + "…";
}
