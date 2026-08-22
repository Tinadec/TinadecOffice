using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// The only place where DmaEA relies on MAF 1.18-specific agent behavior.
/// Tinadec-owned contracts, checkpoints, permissions, and tool execution stay
/// outside this adapter.
/// </summary>
internal static class Maf18RuntimeAdapter
{
    internal const int RequiredMajorVersion = 1;
    internal const int RequiredMinorVersion = 18;
    internal const string TelemetrySourceName = "TinadecCore.Maf";

    internal static IReadOnlyDictionary<string, Version> FrameworkVersions => new Dictionary<string, Version>(StringComparer.Ordinal)
    {
        ["Microsoft.Agents.AI.Abstractions"] = VersionOf(typeof(AIAgent)),
        ["Microsoft.Agents.AI"] = VersionOf(typeof(ChatClientAgent)),
        ["Microsoft.Agents.AI.Workflows"] = VersionOf(typeof(Workflow)),
        ["Microsoft.Agents.AI.OpenAI"] = VersionOf(typeof(OpenAI.Chat.OpenAIChatClientExtensions))
    };

    internal static void EnsureCompatible()
    {
        foreach (var (package, version) in FrameworkVersions)
        {
            if (version.Major != RequiredMajorVersion || version.Minor != RequiredMinorVersion)
            {
                throw new InvalidOperationException(
                    $"TinadecCore requires {package} {RequiredMajorVersion}.{RequiredMinorVersion}.x, but loaded {version}.");
            }
        }
    }

    /// <summary>
    /// Creates an operation-layer model agent with a stable identity and non-sensitive
    /// OpenTelemetry. Governance agents never receive tools: durable Core policy must
    /// authorize and dispatch every side effect.
    /// </summary>
    internal static OpenTelemetryAgent CreateGovernanceAgent(
        IChatClient chatClient,
        string id,
        string name,
        string description,
        ChatOptions chatOptions)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(chatOptions);

        if (chatOptions.Tools is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "MAF governance agents cannot receive tools; TinadecCore owns authorization and dispatch.");
        }

        EnsureCompatible();
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = id,
            Name = name,
            Description = description,
            ChatOptions = chatOptions,
            // MAF 1.18 can invoke tool calls concurrently. Tinadec keeps this disabled
            // because its durable dispatcher serializes policy and approval boundaries.
            AllowConcurrentInvocation = false,
            DisableApprovalResponseBinding = false
        });
        return new OpenTelemetryAgent(agent, TelemetrySourceName)
        {
            // Prompts, arguments, results, and user content are never exported by default.
            EnableSensitiveData = false
        };
    }

    internal static ModelUsage? NormalizeUsage(UsageDetails? usage)
    {
        if (usage is null) return null;
        var additional = usage.AdditionalCounts is { Count: > 0 }
            ? usage.AdditionalCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            : null;
        return new ModelUsage(
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount,
            usage.CachedInputTokenCount,
            usage.ReasoningTokenCount,
            additional);
    }

    internal static ModelUsage? AddUsage(ModelUsage? current, ModelUsage? next)
    {
        if (current is null) return next;
        if (next is null) return current;

        Dictionary<string, long>? additional = null;
        if (current.AdditionalCounts is { Count: > 0 } || next.AdditionalCounts is { Count: > 0 })
        {
            additional = new Dictionary<string, long>(StringComparer.Ordinal);
            AddCounts(additional, current.AdditionalCounts);
            AddCounts(additional, next.AdditionalCounts);
        }

        return new ModelUsage(
            Add(current.InputTokens, next.InputTokens),
            Add(current.OutputTokens, next.OutputTokens),
            Add(current.TotalTokens, next.TotalTokens),
            Add(current.CachedInputTokens, next.CachedInputTokens),
            Add(current.ReasoningTokens, next.ReasoningTokens),
            additional);
    }

    internal static string? SerializeUsage(ModelUsage? usage) =>
        usage is null ? null : JsonSerializer.Serialize(usage);

    /// <summary>
    /// Applies MAF's atomic tool-call grouping before a worker model turn. The
    /// user's goal is kept outside compaction; old tool groups may be summarized
    /// or removed, while a function call and its result can never be split.
    /// </summary>
    internal static async Task<IReadOnlyList<ChatMessage>> CompactWorkerConversationAsync(
        ChatMessage goal,
        IReadOnlyList<ChatMessage> history,
        int maxHistoryMessages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0) return [goal];

        var limit = Math.Max(2, maxHistoryMessages - 1);
#pragma warning disable MAAI001 // Experimental MAF compaction stays behind this internal adapter.
        var strategy = new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(
                CompactionTriggers.MessagesExceed(limit),
                minimumPreservedGroups: 2,
                target: index => index.IncludedMessageCount <= limit),
            new TruncationCompactionStrategy(
                CompactionTriggers.MessagesExceed(limit),
                minimumPreservedGroups: 2,
                target: index => index.IncludedMessageCount <= limit));
        var compacted = await CompactionProvider.CompactAsync(strategy, history, cancellationToken: cancellationToken).ConfigureAwait(false);
#pragma warning restore MAAI001
        return [goal, .. compacted];
    }

    private static long? Add(long? left, long? right) =>
        left is null && right is null ? null : left.GetValueOrDefault() + right.GetValueOrDefault();

    private static void AddCounts(Dictionary<string, long> destination, IReadOnlyDictionary<string, long>? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source)
        {
            destination[key] = destination.GetValueOrDefault(key) + value;
        }
    }

    private static Version VersionOf(Type type) =>
        type.Assembly.GetName().Version
        ?? throw new InvalidOperationException($"The {type.Assembly.GetName().Name} assembly has no version.");
}
