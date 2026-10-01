using System.Text.Json;
using System.Text.Json.Serialization;
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
        // 选人按职责，由规划者显式写 assignee：2026-09-27 的派发采样里，让引擎按 required_tools
        // 反推执行者（最小权限 = 持有这些工具的最窄成员）把 12 个任务错派了 4 个（取证给了评审、
        // 改代码给了写文档），显式点名则 11/12 正确。
        + "每个任务必须用 assignee 指定执行者：从上方「可派发执行者」名册里按职责选择，原样填写其 id。判断依据是该执行者的职责与边界是否匹配这项任务，而不是它碰巧持有哪些工具：只读取证、检索、审阅给只读执行者；改文件、跑命令、提交给持有相应写权限的执行者。一项任务只派给一个执行者；需要两种职责就拆成两个有依赖关系的任务。"
        + "required_tools 可选、不用于选人：填写后该执行者在这项任务里只能使用这些工具（逐字取自名册里的工具名），留空则拥有它的全部工具——拿不准就留空；不得写名册之外的工具或能力。"
        + "会改文件的任务可以写 write_scope（字符串数组：它要改的工作区内文件或目录）：任务开始时这些路径会为它独占预留，别的运行正在改同一处时任务会直接失败并说明谁占着，从而避免两个智能体改同一个地方；只读任务不要写。"
        + "success_criteria 必须是可外部验证的判据（可观察的文件、命令输出或状态），不要写「完成即可」这类自指描述；"
        + "title 用一句话说明做什么，不要复述用户原话。"
        + "success_criteria、dependencies、required_capabilities、required_tools 四个字段必须是字符串数组（例如 \"success_criteria\": [\"判据\"]），绝不能写成单个字符串；priority 必须是整数。"
        + "若目标不需要执行任何子任务（问候、闲聊、纯提问），直接输出 []，不要用散文回答。"
        + "JSON 字符串里写 Windows 路径必须转义反斜杠（C:\\\\dir）或改用正斜杠（C:/dir）。"
        + "description 要自足：执行者看不到本次对话，目标、涉及的绝对路径和必要上下文都要写进去。"
        + "仅输出 JSON 数组，每个元素必须包含 task_key（稳定、唯一、仅小写字母数字和短横线）、assignee、title、description、success_criteria、dependencies（task_key 数组）、required_capabilities、required_tools、priority、risk 字段。不要输出其他文字。";

    /// <summary>Bound on the diagnostic head, so one runaway answer cannot bloat a run.</summary>
    private const int MaxResponseHeadLength = 2000;

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new LenientStringArrayConverter() }
    };

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

    /// <summary>
    /// JSON-level reason the last answer failed to parse (the first
    /// <see cref="JsonException"/> message, or "no balanced JSON array candidate
    /// found…" when the answer carried no parseable array at all). Empty after a
    /// successful parse. "Not a task array" alone is undiagnosable and a blind
    /// retry re-commits the same mistake, so this detail travels with the retry
    /// hint and the run's plan_diagnostic event.
    /// </summary>
    public string LastParseErrorDetail { get; private set; } = string.Empty;

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
        var tasks = TryParseTasks(response.Text, out var parsed, out var parseError);
        LastParseErrorDetail = parseError;
        LastPlanWasParsed = parsed;
        if (!parsed)
        {
            _logger?.LogWarning(
                "Planning response could not be parsed as a task array (plan_parse_failed); falling back to a single goal-task. "
                + "Declared-edge tiers refuse to dispatch this fallback (graph plan guard). Parse error: {ParseError} Response head: {ResponseHead}",
                parseError,
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

    /// <summary>
    /// The roster as the planner reads it: one line per executor — id, responsibility, tools.
    /// The responsibility text is what the choice is made by (the "name : description" roster
    /// form); a bare slug + tool list made the model route by tool overlap.
    /// </summary>
    internal static string BuildRosterInstructions(IReadOnlyList<AgentDefinition> agents)
    {
        var lines = agents
            .Where(agent => agent.Enabled)
            .Select(agent =>
            {
                var duty = string.IsNullOrWhiteSpace(agent.Description) ? agent.AgentType : agent.Description.Trim();
                var tools = agent.AllowedTools.Order(StringComparer.Ordinal).ToArray();
                var capabilities = agent.Capabilities.Order(StringComparer.Ordinal).ToArray();
                return $"- {agent.Name}: {duty} | 工具: {(tools.Length == 0 ? "无" : string.Join(", ", tools))}"
                    + (capabilities.Length == 0 ? string.Empty : $" | 能力: {string.Join(", ", capabilities)}");
            })
            .ToArray();
        return lines.Length == 0
            ? "可派发执行者（assignee 取值）：本次运行没有可派发的执行者。"
            : "可派发执行者（assignee 取值；id: 职责 | 工具，本次运行冻结、权威）：\n" + string.Join("\n", lines);
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
    private static PlannedTask[] TryParseTasks(string? text, out bool parsed, out string parseError)
    {
        parsed = false;
        parseError = string.Empty;
        // Reasoning models wrap the task array in <think> blocks, prose, and markdown
        // fences. ExtractJsonCandidates strips reasoning and returns each balanced
        // top-level array, so we keep the first candidate that actually deserializes
        // instead of naively spanning the first '[' to the last ']'.
        var candidates = ModelOutputText.ExtractJsonCandidates(text, array: true);
        if (candidates.Count == 0)
        {
            parseError = "no balanced JSON array candidate found in the response";
            return [];
        }
        string? firstError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var tasks = JsonSerializer.Deserialize<PlannedTask[]>(candidate, ParseOptions);
                if (tasks is null) continue;
                parsed = true;
                return tasks;
            }
            catch (JsonException ex)
            {
                // Keep the first detail: it names the exact field/shape the model got
                // wrong, which is what the retry hint and plan_diagnostic need.
                firstError ??= ex.Message;
            }
        }
        parseError = firstError ?? string.Empty;
        return [];
    }

    /// <summary>
    /// Models write array-typed plan fields as a single scalar string ("success_criteria":
    /// "判据" instead of ["判据"]) far more often than they write broken JSON — that was
    /// the 2026-09-17 plan_parse_failed cluster (runs feb146e4/05a89fc3, qwen3.8-27b).
    /// This converter accepts the scalar form and repairs it instead of throwing the
    /// whole array away: string → one-element array, null → empty, array elements keep
    /// non-empty strings and drop null/empty/non-string entries. Shapes that carry no
    /// recoverable meaning (numbers, nested containers) still throw.
    /// </summary>
    private sealed class LenientStringArrayConverter : JsonConverter<string[]>
    {
        public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return [];
            if (reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString();
                return string.IsNullOrWhiteSpace(value) ? [] : [value];
            }
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"expected a string array or a string, got {reader.TokenType}");
            var values = new List<string>();
            var depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray && depth == 0) return [.. values];
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartArray or JsonTokenType.StartObject:
                        // A nested container can never become a task criterion; skip it whole.
                        depth++;
                        break;
                    case JsonTokenType.EndArray or JsonTokenType.EndObject:
                        depth--;
                        break;
                    case JsonTokenType.String when depth == 0 && !string.IsNullOrWhiteSpace(reader.GetString()):
                        values.Add(reader.GetString()!);
                        break;
                }
            }
            throw new JsonException("unterminated string array");
        }

        public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value) writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
    }
}
