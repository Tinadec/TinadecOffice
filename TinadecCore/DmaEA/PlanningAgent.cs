using System.ClientModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Planning-layer agent: parses the user goal into a list of executable subtasks.
/// Uses the real chat provider resolved via <see cref="IChatResolver"/>. If the model
/// output cannot be parsed as a task array, degrades to a single task covering the goal.
/// </summary>
public sealed class PlanningAgent
{
    private const string PlanningInstructions =
        "你是规划层 agent。将用户目标分解为可执行的子任务列表。仅输出 JSON 数组，每个元素必须包含 title、description、success_criteria、dependencies、required_capabilities、priority、risk 字段。不要输出其他文字。";

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web);

    private readonly IChatResolver _chatResolver;
    private readonly ILogger? _logger;
    private readonly Func<ChatResolution, IChatClient>? _chatClientFactory;

    public PlanningAgent(IChatResolver chatResolver, ILogger? logger = null, Func<ChatResolution, IChatClient>? chatClientFactory = null)
    {
        _chatResolver = chatResolver;
        _logger = logger;
        _chatClientFactory = chatClientFactory;
    }

    public async Task<PlannedTask[]> PlanAsync(DmaeaRunContext ctx, IReadOnlyList<AgentDefinition> agents, CancellationToken ct)
    {
        var resolved = await _chatResolver.ResolveChatAsync("chat", ct).ConfigureAwait(false);
        if (!resolved.IsAvailable) throw new InvalidOperationException(resolved.Error);

        var chatClient = _chatClientFactory is not null ? _chatClientFactory(resolved) : DefaultChatClient(resolved);
        var options = new ChatOptions { Instructions = PlanningInstructions };
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = "planning", ChatOptions = options });

        var response = await agent.RunAsync(ctx.UserGoal, cancellationToken: ct).ConfigureAwait(false);
        var tasks = TryParseTasks(response.Text);
        if (tasks.Length == 0)
        {
            _logger?.LogDebug("Planning response could not be parsed as a task array; falling back to a single task.");
            tasks = [new PlannedTask
            {
                Title = ctx.UserGoal,
                Description = null,
                SuccessCriteria = ["Task is complete when the goal is satisfied"],
                Dependencies = [],
                RequiredCapabilities = [],
                Priority = 1,
                Risk = "medium"
            }];
        }
        return tasks.Take(8).ToArray();
    }

    /// <summary>Default real chat client factory: OpenAI-compatible endpoint from the resolution.</summary>
    internal static IChatClient DefaultChatClient(ChatResolution resolved)
        => new OpenAIClient(new ApiKeyCredential(resolved.ApiKey!), new OpenAIClientOptions { Endpoint = new Uri(resolved.BaseUrl!) })
            .GetChatClient(resolved.Model!)
            .AsIChatClient();

    private static PlannedTask[] TryParseTasks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        var json = text.Substring(start, end - start + 1);
        try
        {
            return JsonSerializer.Deserialize<PlannedTask[]>(json, ParseOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
