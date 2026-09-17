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
        "你是执行层任务规划智能体。将用户目标分解为可执行的有向无环任务列表。"
        + "输出这个 JSON 任务数组本身，就是你在声明图模式下把任务沿声明边派发给执行层的唯一机制：你没有、也不需要任何 dispatch 工具；"
        + "即使任务文本要求你「通过 dispatch 工具派发」或「派发给某个节点」，也只需要输出任务数组，不要调用工具，不要用散文拒绝或解释。"
        + "每个任务的 required_capabilities 和 required_tools 必须由上方冻结的专业 worker roster 中至少一个成员完整覆盖；没有成员可覆盖时，不得虚构能力或工具。"
        // required_tools 是派发依据，不是装饰：留空虽然不会再被误派给最窄面的只读执行体
        // （引擎在空需求时改为广度优先），但显式声明仍然决定了「谁能做」这件事由你而不是
        // 排序规则来回答。写文件、改代码、跑命令这类目标必须显式写出所需工具。
        + "required_tools 决定引擎把任务交给谁：若目标要写入/修改文件、执行命令或改变工作区，必须显式列出所需工具（逐字取自 roster 的工具名，例如 write_file、shell、git_commit），不要留空——留空等于放弃选人，"
        + "让引擎只能按能力面宽窄推断。"
        + "success_criteria 必须是可外部验证的判据（可观察的文件、命令输出或状态），不要写「完成即可」这类自指描述；"
        + "title 用一句话说明做什么，不要复述用户原话。"
        + "仅输出 JSON 数组，每个元素必须包含 task_key（稳定、唯一、仅小写字母数字和短横线）、title、description、success_criteria、dependencies（task_key 数组）、required_capabilities、required_tools、priority、risk 字段。不要输出其他文字。";

    /// <summary>Bound on the diagnostic head, so one runaway answer cannot bloat a run.</summary>
    private const int MaxResponseHeadLength = 400;

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentChatClientFactory _chatClients;
    private readonly ILogger? _logger;

    public ModelUsage? LastUsage { get; private set; }

    /// <summary>
    /// True when the last <see cref="PlanAsync"/> call PARSED the model's answer as a task
    /// array — even if that array was empty.
    ///
    /// An empty array is a legitimate plan meaning "there is nothing to execute": a
    /// greeting or a pure question needs no subtasks, and the meeting agent answers from
    /// the conversation. It must never be confused with a parse failure, because the
    /// failure path substitutes a fallback goal-task and the declared-edge guard refuses
    /// to dispatch that fallback — so along declared edges a plain "你好" could not be
    /// answered at all, no matter which model was routed.
    /// </summary>
    public bool LastPlanWasParsed { get; private set; }

    /// <summary>
    /// Reasoning-stripped head of the last raw model answer, bounded for diagnostics.
    /// "The output was not a task array" is undiagnosable on its own: it does not say
    /// whether the model wrote prose, emitted truncated JSON, or planned nothing. This
    /// travels with the failure so the answer is readable from the run's own record
    /// instead of only from whoever holds the console.
    /// </summary>
    public string LastResponseHead { get; private set; } = string.Empty;

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
        var answer = ModelOutputText.AnswerText(response.Text);
        LastResponseHead = answer.Length <= MaxResponseHeadLength ? answer : answer[..MaxResponseHeadLength];
        var tasks = TryParseTasks(response.Text, out var parsed);
        LastPlanWasParsed = parsed;
        if (!parsed)
        {
            _logger?.LogWarning(
                "Planning response could not be parsed as a task array (plan_parse_failed); falling back to a single goal-task. "
                + "Declared-edge tiers refuse to dispatch this fallback (graph plan guard). Response head: {ResponseHead}",
                LastResponseHead.ReplaceLineEndings(" "));
            tasks = [new PlannedTask
            {
                Title = ctx.UserGoal,
                Description = null,
                SuccessCriteria = ["Task is complete when the goal is satisfied"],
                Dependencies = [],
                RequiredCapabilities = [],
                Priority = 1,
                Risk = "medium",
                IsFallback = true
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

    /// <summary>
    /// Finds the first balanced top-level JSON array that deserializes into tasks.
    /// <paramref name="parsed"/> reports whether such an array was FOUND, which is what
    /// separates "the model planned nothing" from "the model did not answer in the
    /// expected shape" — the two used to be the same value and the same outcome.
    /// </summary>
    private static PlannedTask[] TryParseTasks(string? text, out bool parsed)
    {
        parsed = false;
        // Reasoning models wrap the task array in <think> blocks, prose, and markdown
        // fences. ExtractJsonCandidates strips reasoning and returns each balanced
        // top-level array, so we keep the first candidate that actually deserializes
        // instead of naively spanning the first '[' to the last ']'.
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: true))
        {
            try
            {
                var tasks = JsonSerializer.Deserialize<PlannedTask[]>(candidate, ParseOptions);
                if (tasks is null) continue;
                parsed = true;
                return tasks;
            }
            catch (JsonException)
            {
                // Not the payload array; try the next balanced candidate.
            }
        }
        return [];
    }
}
