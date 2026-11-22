using System.Security.Cryptography;
using System.Text;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Context;

/// <summary>
/// Implements the 3-tier context management strategy (<c>direct</c>, <c>managed</c>,
/// <c>deep_curator</c>) and always-on efficiency primitives: KV-cache friendly append-only
/// hot window ordering + automatic stale tool output micro-compaction &amp; payload offloading.
/// </summary>
public sealed class ContextStrategyService : IContextStrategyService
{
    private const int StaleToolOutputFoldChars = 360;
    private const int PreviewChars = 200;

    public ContextStrategyProfile ResolveProfile(string? strategyOverride = null, ContextMemoryPolicyDto? policy = null)
        => ResolveProfile(strategyOverride, policy, compactionTriggerRatioOverride: null, recallPolicyOverride: null);

    public ContextStrategyProfile ResolveProfile(
        string? strategyOverride,
        ContextMemoryPolicyDto? policy,
        double? compactionTriggerRatioOverride,
        string? recallPolicyOverride)
    {
        var raw = string.IsNullOrWhiteSpace(strategyOverride)
            ? policy?.Strategy ?? "managed"
            : strategyOverride;
        var strategy = raw.Trim().ToLowerInvariant() switch
        {
            "direct" => "direct",
            "deep_curator" => "deep_curator",
            _ => "managed"
        };

        var rawRatio = compactionTriggerRatioOverride is { } ratioOverride && !double.IsNaN(ratioOverride)
            ? ratioOverride
            : policy?.CompactionTriggerRatio ?? 0.75;
        var triggerRatio = Math.Clamp(rawRatio, 0.40, 0.95);

        var normalizedRecallOverride = string.IsNullOrWhiteSpace(recallPolicyOverride)
            ? null
            : recallPolicyOverride.Trim().ToLowerInvariant();

        var fidelityRaw = normalizedRecallOverride is "summary_only" or "balanced" or "verbatim_first"
            ? normalizedRecallOverride
            : policy?.RecallFidelity ?? "balanced";
        var fidelity = fidelityRaw.Trim().ToLowerInvariant() switch
        {
            "summary_only" => "summary_only",
            "verbatim_first" => "verbatim_first",
            _ => "balanced"
        };

        var allowCrossSession = policy?.AllowCrossSessionRecall ?? true;
        var defaultHistoryRecall = strategy != "direct";
        var defaultCrossSession = strategy == "deep_curator" && allowCrossSession;
        var (historyRecallEnabled, crossSessionRecallEnabled) = normalizedRecallOverride switch
        {
            "disabled" or "none" or "off" => (false, false),
            "session_only" or "local" => (true, false),
            "cross_session" or "cross_session_enabled" => (true, true),
            _ => (defaultHistoryRecall, defaultCrossSession)
        };

        return strategy switch
        {
            "direct" => new ContextStrategyProfile(
                Strategy: "direct",
                AlwaysOnKvCacheAppendWindow: true,
                AlwaysOnMicroCompaction: true,
                AsyncCompactionEnabled: false,
                HistoryRecallEnabled: historyRecallEnabled,
                CrossSessionRecallEnabled: crossSessionRecallEnabled,
                ExplorationDigestEnabled: false,
                ActiveMemoryEvolutionEnabled: false,
                CompactionTriggerRatio: triggerRatio,
                RecallFidelity: fidelity),
            "deep_curator" => new ContextStrategyProfile(
                Strategy: "deep_curator",
                AlwaysOnKvCacheAppendWindow: true,
                AlwaysOnMicroCompaction: true,
                AsyncCompactionEnabled: true,
                HistoryRecallEnabled: historyRecallEnabled,
                CrossSessionRecallEnabled: crossSessionRecallEnabled,
                ExplorationDigestEnabled: true,
                ActiveMemoryEvolutionEnabled: true,
                CompactionTriggerRatio: triggerRatio,
                RecallFidelity: fidelity),
            _ => new ContextStrategyProfile(
                Strategy: "managed",
                AlwaysOnKvCacheAppendWindow: true,
                AlwaysOnMicroCompaction: true,
                AsyncCompactionEnabled: true,
                HistoryRecallEnabled: historyRecallEnabled,
                CrossSessionRecallEnabled: crossSessionRecallEnabled,
                ExplorationDigestEnabled: false,
                ActiveMemoryEvolutionEnabled: true,
                CompactionTriggerRatio: triggerRatio,
                RecallFidelity: fidelity)
        };
    }

    public bool ShouldTriggerAsyncCompaction(
        int estimatedTokens,
        int tokenBudget,
        string? strategyOverride = null,
        double? triggerRatioOverride = null)
    {
        var profile = ResolveProfile(strategyOverride);
        if (!profile.AsyncCompactionEnabled || tokenBudget <= 0)
        {
            return false;
        }

        var ratio = Math.Clamp(triggerRatioOverride ?? profile.CompactionTriggerRatio, 0.40, 0.95);
        return estimatedTokens >= (int)Math.Ceiling(tokenBudget * ratio);
    }

    public ContextMicroCompactionResult MicroCompactMessages(
        IReadOnlyList<ContextWindowMessage> messages,
        int hotTailCount = 6,
        int offloadThresholdBytes = 16_384,
        Func<ContextWindowMessage, string>? offloadArtifact = null)
    {
        if (messages.Count == 0)
        {
            return new ContextMicroCompactionResult([], 0, 0, 0, []);
        }

        var effectiveHotTail = Math.Max(1, hotTailCount);
        var effectiveOffloadBytes = Math.Max(1024, offloadThresholdBytes);
        var hotStartIndex = Math.Max(0, messages.Count - effectiveHotTail);

        var output = new List<ContextWindowMessage>(messages.Count);
        var pointers = new List<string>();
        var foldedCount = 0;
        var offloadedCount = 0;
        var savedBytes = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var isBeforeHotTail = i < hotStartIndex;
            var isBeforeLast = i < messages.Count - 1;
            var content = message.Content ?? string.Empty;
            var byteCount = Encoding.UTF8.GetByteCount(content);
            var isToolMessage = string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                || LooksLikeToolOutput(content);

            if (isBeforeLast && isToolMessage && byteCount >= effectiveOffloadBytes)
            {
                var pointer = offloadArtifact is not null
                    ? offloadArtifact(message)
                    : DeterministicArtifactPointer(content);
                var preview = SingleLinePreview(content, PreviewChars);
                var replacement = $"[offloaded tool payload ({byteCount} bytes) -> {pointer}; preview: {preview}]";
                savedBytes += Math.Max(0, byteCount - Encoding.UTF8.GetByteCount(replacement));
                offloadedCount++;
                pointers.Add(pointer);
                output.Add(message with { Content = replacement });
                continue;
            }

            if (isBeforeHotTail && isToolMessage && content.Length > StaleToolOutputFoldChars)
            {
                var preview = SingleLinePreview(content, PreviewChars);
                var replacement = $"[micro-compacted stale tool output ({content.Length} chars): {preview}]";
                savedBytes += Math.Max(0, byteCount - Encoding.UTF8.GetByteCount(replacement));
                foldedCount++;
                output.Add(message with { Content = replacement });
                continue;
            }

            output.Add(message);
        }

        return new ContextMicroCompactionResult(output, foldedCount, offloadedCount, savedBytes, pointers);
    }

    private static bool LooksLikeToolOutput(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.AsSpan().TrimStart();
        return trimmed.StartsWith("[tool_result", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Tool output:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("{\"tool_call_id\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string DeterministicArtifactPointer(string content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return $"artifact://tool-output/{hash[..16]}";
    }

    private static string SingleLinePreview(string content, int maxChars)
    {
        var single = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= maxChars ? single : string.Concat(single.AsSpan(0, maxChars), "…");
    }
}
