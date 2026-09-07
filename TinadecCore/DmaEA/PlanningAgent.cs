using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Execution coordinator: parses the user goal into a list of executable subtasks.
/// Uses the real chat provider resolved via <see cref="IChatResolver"/>. If the model
/// output cannot be parsed as a task array, degrades to a single task covering the goal.
/// </summary>
public sealed class PlanningAgent
{
    private const string PlanningInstructions =
        "你是执行层任务规划智能体。将用户目标分解为可执行的有向无环任务列表。每个任务的 required_capabilities 和 required_tools 必须由上方冻结的专业 worker roster 中至少一个成员完整覆盖；没有成员可覆盖时，不得虚构能力或工具。仅输出 JSON 数组，每个元素必须包含 task_key（稳定、唯一、仅小写字母数字和短横线）、title、description、success_criteria、dependencies（task_key 数组）、required_capabilities、required_tools、priority、risk 字段。不要输出其他文字。";

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentChatClientFactory _chatClients;
    private readonly ILogger? _logger;

    public ModelUsage? LastUsage { get; private set; }

    public PlanningAgent(IAgentChatClientFactory chatClients, ILogger? logger = null)
    {
        _chatClients = chatClients;
        _logger = logger;
    }

    public async Task<PlannedTask[]> PlanAsync(DmaeaRunContext ctx, IReadOnlyList<AgentDefinition> agents, CancellationToken ct)
        => await PlanAsync(ctx, agents, null, ct).ConfigureAwait(false);

    public async Task<PlannedTask[]> PlanAsync(
        DmaeaRunContext ctx,
        IReadOnlyList<AgentDefinition> agents,
        string? assembledInstructions,
        CancellationToken ct)
    {
        var resolved = await _chatClients.ResolveChatAsync("chat", ct).ConfigureAwait(false);
        if (!resolved.IsAvailable) throw new InvalidOperationException(resolved.Error);

        var chatClient = await _chatClients.CreateAsync(resolved, ct).ConfigureAwait(false);
        var rosterInstructions = BuildRosterInstructions(agents);
        var options = new ChatOptions
        {
            Instructions = string.IsNullOrWhiteSpace(assembledInstructions)
                ? rosterInstructions + "\n\n" + PlanningInstructions
                : assembledInstructions.Trim() + "\n\n" + rosterInstructions + "\n\n" + PlanningInstructions
        };
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            chatClient,
            "operation.task_planner",
            "task_planner",
            "Creates the execution task graph without performing side effects.",
            options);

        var response = await agent.RunAsync(ctx.UserGoal, cancellationToken: ct).ConfigureAwait(false);
        LastUsage = Maf18RuntimeAdapter.NormalizeUsage(response.Usage);
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

    private static string BuildRosterInstructions(IReadOnlyList<AgentDefinition> agents)
    {
        var roster = agents
            .Where(agent => agent.Enabled)
            .Select(agent => new
            {
                slug = agent.Name,
                role = agent.AgentType,
                capabilities = agent.Capabilities.Order(StringComparer.Ordinal).ToArray(),
                allowed_tools = agent.AllowedTools.Order(StringComparer.Ordinal).ToArray()
            })
            .ToArray();
        return "Frozen specialist roster (authoritative for this run):\n"
            + JsonSerializer.Serialize(roster);
    }

    /// <summary>Default real chat client factory: OpenAI-compatible endpoint from the resolution.</summary>
    internal static IChatClient DefaultChatClient(ChatResolution resolved)
        => AgentChatClientFactory.CreateOpenAiChatClient(resolved);

    private static PlannedTask[] TryParseTasks(string? text)
    {
        // Reasoning models wrap the task array in <think> blocks, prose, and markdown
        // fences. ExtractJsonCandidates strips reasoning and returns each balanced
        // top-level array, so we keep the first candidate that actually deserializes
        // instead of naively spanning the first '[' to the last ']'.
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: true))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<PlannedTask[]>(candidate, ParseOptions);
                if (parsed is { Length: > 0 }) return parsed;
            }
            catch (JsonException)
            {
                // Not the payload array; try the next balanced candidate.
            }
        }
        return [];
    }
}
