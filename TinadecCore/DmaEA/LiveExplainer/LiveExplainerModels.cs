using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.DmaEA.LiveExplainer;

/// <summary>
/// Thread-flowing and async-flowing guard that marks every governance-layer narrator / scout
/// invocation with <c>IsObserverRun = true</c>. Stream projections and governance topic hooks
/// ignore everything inside an observer scope so the narrator can never trigger recursive
/// self-observation or contaminate the execution layer.
/// </summary>
public static class ObserverRunScope
{
    private static readonly AsyncLocal<ObserverFrame?> CurrentFrame = new();
    private static readonly ConcurrentDictionary<Guid, byte> ActiveObserverInvocations = new();
    private static long _totalEnteredCount;
    private static long _suppressedRecursiveSignals;

    public static bool IsCurrentObserverRun => CurrentFrame.Value is { IsObserverRun: true };

    public static Guid? CurrentObservedRunId => CurrentFrame.Value?.ObservedRunId;

    public static long TotalObserverInvocations => Interlocked.Read(ref _totalEnteredCount);

    public static long SuppressedRecursiveSignals => Interlocked.Read(ref _suppressedRecursiveSignals);

    public static void RecordSuppressedRecursiveSignal() => Interlocked.Increment(ref _suppressedRecursiveSignals);

    public static bool IsActiveObserverInvocation(Guid id) => ActiveObserverInvocations.ContainsKey(id);

    public static IDisposable Enter(Guid observedRunId, Guid? invocationId = null)
    {
        var previous = CurrentFrame.Value;
        var currentInvocationId = invocationId ?? Guid.NewGuid();
        ActiveObserverInvocations[currentInvocationId] = 1;
        Interlocked.Increment(ref _totalEnteredCount);
        CurrentFrame.Value = new ObserverFrame(IsObserverRun: true, observedRunId, currentInvocationId);
        return new ScopeLease(previous, currentInvocationId);
    }

    private sealed record ObserverFrame(bool IsObserverRun, Guid ObservedRunId, Guid InvocationId);

    private sealed class ScopeLease(ObserverFrame? previous, Guid invocationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            ActiveObserverInvocations.TryRemove(invocationId, out _);
            CurrentFrame.Value = previous;
        }
    }
}

public static class LiveExplainerModes
{
    public const string Off = "off";
    public const string Auto = "auto";
    public const string Always = "always";
    public const string RoutePurpose = "live_explainer";

    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is Off or Auto or Always) return true;
        normalized = Auto;
        return false;
    }

    public static string NormalizeOrDefault(string? raw) =>
        TryNormalize(raw, out var normalized) ? normalized : Auto;
}

/// <summary>
/// Canonical 9 governance and isolation red-line signals unified across Live Digest,
/// Trajectory diagnostics, RunContextDiagnosticsService, and P2 self-evolution.
/// </summary>
public static class GovernanceSignalCodes
{
    public const string ApprovalRejected = UnifiedGovernanceSignalCodes.ApprovalRejected;
    public const string FsNotObserved = UnifiedGovernanceSignalCodes.FsNotObserved;
    public const string FsStaleVersion = UnifiedGovernanceSignalCodes.FsStaleVersion;
    public const string WorktreeGuard = UnifiedGovernanceSignalCodes.WorktreeGuard;
    public const string WriteScopeLocked = UnifiedGovernanceSignalCodes.WriteScopeLocked;
    public const string Timeout = UnifiedGovernanceSignalCodes.Timeout;
    public const string SameArgsRetry = UnifiedGovernanceSignalCodes.SameArgsRetry;
    public const string LoopDetected = UnifiedGovernanceSignalCodes.LoopDetected;
    public const string ContextPressure = UnifiedGovernanceSignalCodes.ContextPressure;

    public static readonly IReadOnlyList<string> Ordered = UnifiedGovernanceSignalCodes.Ordered;

    public static readonly IReadOnlySet<string> All = UnifiedGovernanceSignalCodes.All;

    public static string DefaultSeverity(string code) => UnifiedGovernanceSignalCodes.DefaultSeverity(code);
}

public static class LiveDigestPhases
{
    public const string Hypothesis = "hypothesis";
    public const string Verification = "verification";
    public const string Conclusion = "conclusion";

    public static string Normalize(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Verification => Verification,
        Conclusion => Conclusion,
        _ => Hypothesis
    };
}

public static class LiveDigestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };
}

public sealed record LiveDigestSettingsDto
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = LiveExplainerModes.Auto;

    [JsonPropertyName("route_purpose")]
    public string RoutePurpose { get; init; } = LiveExplainerModes.RoutePurpose;

    [JsonPropertyName("provider_instance_id")]
    public Guid? ProviderInstanceId { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("resolved_provider_instance_id")]
    public Guid? ResolvedProviderInstanceId { get; init; }

    [JsonPropertyName("resolved_model")]
    public string? ResolvedModel { get; init; }

    [JsonPropertyName("resolved_source")]
    public string ResolvedSource { get; init; } = "unconfigured";

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record UpdateLiveDigestSettingsRequestDto
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("provider_instance_id")]
    public Guid? ProviderInstanceId { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("clear_model_binding")]
    public bool ClearModelBinding { get; init; }
}

public sealed record CompactionPointDto
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("before_tokens")]
    public int BeforeTokens { get; init; }

    [JsonPropertyName("after_tokens")]
    public int AfterTokens { get; init; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;
}

public sealed record ContextWaterlineDto
{
    [JsonPropertyName("used_tokens")]
    public int UsedTokens { get; init; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 128_000;

    [JsonPropertyName("usage_percent")]
    public double UsagePercent { get; init; }

    [JsonPropertyName("compaction_count")]
    public int CompactionCount { get; init; }

    [JsonPropertyName("compaction_points")]
    public IReadOnlyList<CompactionPointDto> CompactionPoints { get; init; } = [];
}

public sealed record GovernanceSignalDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "warning";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("subject_id")]
    public string? SubjectId { get; init; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TeammateThoughtDto
{
    [JsonPropertyName("handle")]
    public string Handle { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "working";

    [JsonPropertyName("current_task")]
    public string? CurrentTask { get; init; }

    [JsonPropertyName("thought")]
    public string Thought { get; init; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ChapterToolCallDto
{
    [JsonPropertyName("tool_call_id")]
    public string ToolCallId { get; init; } = string.Empty;

    [JsonPropertyName("tool_name")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "succeeded";

    [JsonPropertyName("compact_summary")]
    public string CompactSummary { get; init; } = string.Empty;
}

public sealed record TouchedFileStatDto
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("added_lines")]
    public int AddedLines { get; init; }

    [JsonPropertyName("removed_lines")]
    public int RemovedLines { get; init; }
}

public sealed record LiveDigestChapterDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = LiveDigestPhases.Hypothesis;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("narrative")]
    public string Narrative { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "l0_structural";

    [JsonPropertyName("raw_thought_excerpt")]
    public string? RawThoughtExcerpt { get; init; }

    [JsonPropertyName("scout_note")]
    public string? ScoutNote { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ChapterToolCallDto> ToolCalls { get; init; } = [];

    [JsonPropertyName("touched_files")]
    public IReadOnlyList<TouchedFileStatDto> TouchedFiles { get; init; } = [];

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SentinelAlertDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "warning";

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("suggested_steer_prompt")]
    public string SuggestedSteerPrompt { get; init; } = string.Empty;

    [JsonPropertyName("teammate_handle")]
    public string? TeammateHandle { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LiveDigestSnapshotDto
{
    [JsonPropertyName("run_id")]
    public Guid RunId { get; init; }

    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = LiveExplainerModes.Auto;

    [JsonPropertyName("explainer_active")]
    public bool ExplainerActive { get; init; }

    [JsonPropertyName("explainer_model")]
    public string? ExplainerModel { get; init; }

    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("headline")]
    public string Headline { get; init; } = string.Empty;

    [JsonPropertyName("current_phase")]
    public string CurrentPhase { get; init; } = LiveDigestPhases.Hypothesis;

    [JsonPropertyName("context_waterline")]
    public ContextWaterlineDto ContextWaterline { get; init; } = new();

    [JsonPropertyName("governance_signals")]
    public IReadOnlyList<GovernanceSignalDto> GovernanceSignals { get; init; } = [];

    [JsonPropertyName("teammate_thoughts")]
    public IReadOnlyList<TeammateThoughtDto> TeammateThoughts { get; init; } = [];

    [JsonPropertyName("chapters")]
    public IReadOnlyList<LiveDigestChapterDto> Chapters { get; init; } = [];

    [JsonPropertyName("touched_files")]
    public IReadOnlyList<TouchedFileStatDto> TouchedFiles { get; init; } = [];

    [JsonPropertyName("sentinel_alerts")]
    public IReadOnlyList<SentinelAlertDto> SentinelAlerts { get; init; } = [];

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LiveDigestStreamFrame
{
    public long Seq { get; init; }
    public string EventKind { get; init; } = "l0_delta";
    public object Payload { get; init; } = new();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Mandatory read-only observer invocation contract. Any model call for L1 Lead Narrator or L2 Scout
/// must carry <see cref="IsObserverRun"/> = <c>true</c>, zero tools, and zero conversation writes.
/// </summary>
public sealed record ObserverModelInvocation
{
    public Guid ObservedRunId { get; init; }
    public string Role { get; init; } = "lead_narrator";
    public bool IsObserverRun { get; init; } = true;
    public string SystemPrompt { get; init; } = string.Empty;
    public string UserPrompt { get; init; } = string.Empty;
    public int MaxOutputTokens { get; init; } = 512;
}

public sealed record ObserverModelResult(bool Succeeded, string? Text, string? ModelId, string? Error = null)
{
    public static ObserverModelResult Unavailable(string? error = null) => new(false, null, null, error ?? "Explainer model unavailable.");
}

public interface ILiveExplainerModelInvoker
{
    Task<ObserverModelResult> InvokeObserverAsync(ObserverModelInvocation invocation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production observer model invoker: resolves the dedicated <c>live_explainer</c> slot,
/// strips all tools and workspace mutation handles, enters <see cref="ObserverRunScope"/>,
/// and never touches <see cref="IConversationStore"/> or the main run's prompt/KV-cache.
/// </summary>
public sealed class DefaultLiveExplainerModelInvoker(IAgentChatClientFactory chatClients) : ILiveExplainerModelInvoker
{
    public async Task<ObserverModelResult> InvokeObserverAsync(ObserverModelInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!invocation.IsObserverRun)
        {
            throw new InvalidOperationException("Iron Law #1 violation: LiveExplainer model invocations must set IsObserverRun = true.");
        }

        using var scope = ObserverRunScope.Enter(invocation.ObservedRunId);
        ChatResolution resolution;
        try
        {
            resolution = await chatClients.ResolveChatAsync(LiveExplainerModes.RoutePurpose, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ObserverModelResult.Unavailable(ex.Message);
        }

        if (!resolution.IsAvailable)
        {
            return ObserverModelResult.Unavailable(resolution.Error);
        }

        // Isolate completely from the main run's working directory, MCP loopback credentials and write scope.
        var isolatedScratch = Path.Combine(Path.GetTempPath(), "tinadec-observer-readonly");
        var isolatedResolution = resolution with
        {
            RunId = null,
            TeammateHandle = "__live_explainer_observer__",
            WorkingDirectoryOverride = isolatedScratch,
            McpLoopbackEndpoint = null,
            McpScopedBearerToken = null,
            WriteScope = [],
            AdditionalEnvironment = null
        };

        try
        {
            using var client = await chatClients.CreateAsync(isolatedResolution, cancellationToken).ConfigureAwait(false);
            var options = new ChatOptions
            {
                Instructions = invocation.SystemPrompt,
                ToolMode = ChatToolMode.None,
                Tools = null,
                AllowMultipleToolCalls = false,
                MaxOutputTokens = Math.Clamp(invocation.MaxOutputTokens, 64, 1024),
                Temperature = 0.2f
            };
            var messages = new List<ChatMessage> { new(ChatRole.User, invocation.UserPrompt) };
            var response = await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            var text = response.Text?.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? ObserverModelResult.Unavailable("Empty observer response.")
                : new ObserverModelResult(true, text, resolution.Model ?? resolution.ModelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ObserverModelResult.Unavailable(ex.Message);
        }
    }
}
