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

    /// <summary>
    /// Output ceiling applied whenever a caller does not set one. Leaving it unset let
    /// the PROVIDER's default decide, which silently truncated structured output: the
    /// task planner's JSON array was cut off mid-object at ~180 tokens, so it could
    /// never parse — and along declared edges a parse failure fails the whole run, which
    /// is why the chain never got past planning. A complete task array, a full meeting
    /// answer, and one worker turn all need more room than a chat-completions default
    /// gives. Deliberately generous: it is a ceiling, not a target.
    /// </summary>
    internal const int DefaultMaxOutputTokens = 4096;

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

        // Governance agents MAY now receive tools: the operation layer is no longer barred
        // from holding a tool surface of its own, because a mode can arm its conversation
        // identity to edit the workspace directly (the solo/master-slave shape).
        //
        // What stays barred is a tool MAF could INVOKE BY ITSELF. The guard is not about
        // who the agent is; it is about keeping Core the only thing that executes side
        // effects — an invokable tool would let MAF call it straight through, bypassing the
        // authorization, approval, audit and checkpoint path. Executors already pass
        // declarations (AIFunctionFactory.CreateDeclaration with a null implementation),
        // which MAF cannot call: the model's call comes back to the engine and Core
        // dispatches it. So the line is drawn on invokability, not on layer.
        var invokable = chatOptions.Tools?
            .OfType<AIFunction>()
            .Where(tool => tool.UnderlyingMethod is not null)
            .ToArray() ?? [];
        if (invokable.Length != 0)
        {
            throw new InvalidOperationException(
                "MAF governance agents may only receive declarative tools "
                + $"(AIFunctionFactory.CreateDeclaration with no implementation); received invokable tool(s) "
                + $"{string.Join(", ", invokable.Select(tool => tool.Name))}. "
                + "TinadecCore owns authorization and dispatch, so MAF must never be able to invoke a tool itself.");
        }

        chatOptions.MaxOutputTokens ??= DefaultMaxOutputTokens;

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
    /// Token accounting for the budget gates. Providers are not required to report
    /// a total, and <see cref="ModelUsage.TotalTokens"/> stays null for calls that
    /// omit it, so fall back to input + output. Cached and reasoning tokens are
    /// separate dimensions: they are already inside the total when a provider
    /// reports one, and adding them on top would double-count.
    /// </summary>
    internal static long BudgetTokens(ModelUsage? usage) =>
        usage is null
            ? 0
            : usage.TotalTokens ?? (usage.InputTokens.GetValueOrDefault() + usage.OutputTokens.GetValueOrDefault());

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
