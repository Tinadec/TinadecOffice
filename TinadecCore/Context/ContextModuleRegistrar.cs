using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Context;

/// <summary>
/// Context module registrar. Registers context provider with token budget strategy.
/// </summary>
public sealed class ContextModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "context";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<IContextProvider, ContextProvider>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies"],
            Capabilities = ["context_pack", "evidence_gathering", "token_budget"],
            Language = "C#",
            MafPrimitives = ["context"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Builds a bounded, provenance-bearing context pack. The provider intentionally reads only
/// current-session history and reviewed memory through Core ports; candidate memory and prompt
/// bodies never become event payloads here.
/// </summary>
internal sealed class ContextProvider : IContextProvider
{
    private readonly IConversationStore _conversations;
    private readonly IMemoryStore _memory;
    private readonly IRuntimeContextSettings _settings;

    public ContextProvider(IConversationStore conversations, IMemoryStore memory, IRuntimeContextSettings settings)
    {
        _conversations = conversations;
        _memory = memory;
        _settings = settings;
    }

    public async Task<ContextPack> BuildContextAsync(
        ContextBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.SessionId, out var parsedSessionId))
        {
            throw new ArgumentException("Session id must be a valid Guid.", nameof(request));
        }

        var settings = _settings.Current;
        // A run passes the values captured at admission. The live settings remain a
        // backwards-compatible fallback for callers outside the durable engine.
        var tokenBudget = Math.Clamp(request.TokenBudget ?? settings.DefaultTokenBudget, 512, 262144);
        var recentMessageLimit = Math.Clamp(request.RecentMessageLimit ?? settings.RecentMessageLimit, 1, 256);
        var reviewedMemoryLimit = Math.Clamp(request.ReviewedMemoryLimit ?? settings.ReviewedMemoryLimit, 0, 64);
        var messages = await _conversations.ListMessagesAsync(parsedSessionId, recentMessageLimit, cancellationToken).ConfigureAwait(false);
        var contextRevision = await _conversations.GetContextRevisionAsync(parsedSessionId, cancellationToken).ConfigureAwait(false);
        var latestUserMessage = messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        var memory = reviewedMemoryLimit == 0 || string.IsNullOrWhiteSpace(latestUserMessage?.Content)
            ? Array.Empty<MemoryEntry>()
            : await _memory.RetrieveAsync(request.SessionId, latestUserMessage.Content, reviewedMemoryLimit, cancellationToken).ConfigureAwait(false);

        var candidates = new List<ContextEvidence>();
        var structuredSessionState = $"session_id={request.SessionId}\ncontext_revision={contextRevision}\nmessage_count={messages.Count}";
        candidates.Add(new ContextEvidence
        {
            Source = "structured_session_state",
            Content = structuredSessionState,
            EstimatedTokens = EstimateTokens(structuredSessionState),
            Metadata = new Dictionary<string, string>
            {
                ["context_revision"] = contextRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["message_count"] = messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        });

        if (!string.IsNullOrWhiteSpace(request.TaskContext))
        {
            candidates.Add(new ContextEvidence
            {
                Source = "task_context",
                Content = request.TaskContext.Trim(),
                EstimatedTokens = EstimateTokens(request.TaskContext),
                Metadata = new Dictionary<string, string>()
            });
        }

        if (messages.Count != 0)
        {
            var history = string.Join('\n', messages.Select(message => $"{message.Role}: {message.Content}"));
            candidates.Add(new ContextEvidence
            {
                Source = "session_history",
                Content = history,
                EstimatedTokens = EstimateTokens(history),
                Metadata = new Dictionary<string, string>
                {
                    ["message_count"] = messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["first_sequence"] = messages[0].Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["last_sequence"] = messages[^1].Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            });
        }

        foreach (var item in memory)
        {
            candidates.Add(new ContextEvidence
            {
                Source = "reviewed_memory",
                Content = item.Content,
                EstimatedTokens = EstimateTokens(item.Content),
                Metadata = new Dictionary<string, string>(item.Provenance, StringComparer.Ordinal)
                {
                    ["memory_id"] = item.Id,
                    ["score"] = item.Score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                }
            });
        }

        var budgeted = new List<ContextEvidence>();
        var used = 0;
        foreach (var item in candidates)
        {
            if (item.EstimatedTokens > tokenBudget - used)
            {
                continue;
            }

            budgeted.Add(item);
            used += item.EstimatedTokens;
        }

        return new ContextPack
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            TokenBudget = tokenBudget,
            EstimatedTokens = used,
            Evidence = budgeted,
            Metadata = new Dictionary<string, string>
            {
                ["application_mode"] = request.ApplicationMode,
                ["agent_mode"] = request.AgentMode,
                ["runtime_profile_id"] = request.RuntimeProfileId,
                ["agent_id"] = request.AgentId,
                ["context_revision"] = contextRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
    }

    private static int EstimateTokens(string text) => Math.Max(1, (text.Length + 3) / 4);
}
