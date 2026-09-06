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
                Reason = $"模型探针无法执行：{resolution.Error ?? "chat 路由不可用"}",
                Action = "在模型中心完成 chat 路由与 provider 配置（录入 API key、启用 provider）后重试",
                CheckedAt = now,
                Data = new { base_url_override = GetOverrideBaseUrl() }
            });
        }

        var protocol = ChatProtocols.Normalize(resolution.Protocol);
        if (protocol is ChatProtocols.Acp or ChatProtocols.OpencodeServe)
        {
            return Store(BuildFingerprint(resolution, resolution.BaseUrl ?? string.Empty), now, new ReadinessItem
            {
                Id = Id,
                Status = "unavailable",
                Reason = $"CLI 协议 {protocol} 通过本地 agent 进程通信，服务端 HTTP 探针不适用。",
                Action = "使用对应 CLI runtime 的会话验证连通性（发起一次真实会话）。",
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
                Reason = $"模型端点可用（{stopwatch.ElapsedMilliseconds}ms）。",
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
                Reason = $"模型端点在 {ProbeTimeout.TotalSeconds:0}s 内未响应（TimeoutException/超时取消）。端点 {effectiveBaseUrl}。",
                Action = $"检查网络连通性与 base_url（当前 {effectiveBaseUrl}）；fixture 模式确认 scripts/model-fixture.mjs 已启动。",
                CheckedAt = now,
                Data = new { model_id = resolution.ModelId, base_url = effectiveBaseUrl, duration_ms = stopwatch.ElapsedMilliseconds }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            var summary = DescribeException(ex);
            item = new ReadinessItem
            {
                Id = Id,
                Status = "degraded",
                Reason = $"模型探针调用失败：{summary}",
                Action = SuggestAction(ex, effectiveBaseUrl),
                CheckedAt = now,
                Data = new { model_id = resolution.ModelId, base_url = effectiveBaseUrl, exception_type = ex.GetType().Name }
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

    private static string BuildFingerprint(ChatResolution resolution, string baseUrl) =>
        $"{resolution.ProviderInstanceId}:{resolution.ProviderVersionId}:{resolution.Model}:{baseUrl}";

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

    private static string SuggestAction(Exception ex, string baseUrl)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("401") || message.Contains("Unauthorized") || message.Contains("invalid_api_key"))
            return "API key 被拒绝（HTTP 401）：在模型中心为 provider 重新录入有效 API key 后重试。";
        if (message.Contains("403") || message.Contains("Forbidden"))
            return "端点拒绝访问（HTTP 403）：确认账号权限与 key 所属项目。";
        if (message.Contains("404") || message.Contains("Not Found"))
            return $"端点返回 404：检查 base_url 是否正确（当前 {baseUrl}），OpenAI 兼容端点通常以 /v1 结尾。";
        if (message.Contains("429") || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "端点限流（HTTP 429）：稍后重试或更换额度可用的 key。";
        if (message.Contains("refused") || message.Contains("connect failed", StringComparison.OrdinalIgnoreCase))
            return $"无法连接 {baseUrl}：确认端点进程已启动（fixture 模式先启动 scripts/model-fixture.mjs）。";
        return $"排查端点 {baseUrl} 的可用性后重试；也可查看 Core 日志获取完整异常。";
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? value
        : value.Length <= maxLength ? value
        : value[..maxLength] + "…";
}
