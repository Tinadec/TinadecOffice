using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

public enum SupervisionDecision
{
    Pass,
    Revise,
    Escalate
}

/// <summary>
/// Operation-layer supervision verdict returned before the meeting agent's final output.
/// Revise carries the indexes of tasks that must be re-executed; escalate means the
/// supervisor cannot approve and the user must decide. CriterionVerdicts carry the
/// per-criterion satisfaction verdicts that lane gates later read as code facts.
/// </summary>
public sealed record SupervisionVerdict(
    SupervisionDecision Decision,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<int> ReviseTaskIndexes,
    IReadOnlyList<LaneCriterionVerdict>? CriterionVerdicts = null)
{
    public static SupervisionVerdict EscalateVerdict(string reason) => new(SupervisionDecision.Escalate, [reason], []);
    public string DecictionString() => Decision switch
    {
        SupervisionDecision.Pass => "pass",
        SupervisionDecision.Revise => "revise",
        _ => "escalate"
    };
}

/// <summary>A per-criterion verdict naming the task it belongs to.</summary>
public sealed record LaneCriterionVerdict(
    string TaskKey,
    string Criterion,
    bool Satisfied,
    string? Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? EvidenceRefs = null);

/// <summary>
/// Supervision-layer agent: reviews planned tasks against execution evidence and returns
/// pass / revise / escalate. Model unavailability or an unparsable response escalates —
/// it never fabricates a pass.
/// </summary>
public sealed class SupervisionAgent
{
    private const string SupervisionInstructions =
        "你是监督智能体（Supervision Quality Reviewer）。对照任务列表与实际执行证据给出客观质量裁决。"
        + "仅输出一个 JSON 对象（Output JSON object ONLY），包含：decision（pass / revise / escalate）、reasons（字符串数组，清晰说明依据或返工要点）、revise_task_indexes（decision 为 revise 时需要重做/唤醒对应执行者的任务下标数组，其他情况为空数组），以及 criteria_verdicts。"
        + "criteria_verdicts 必须逐条覆盖任务列表中的每一条 success_criteria，每项形如 {\"task_key\":\"...\",\"criterion\":\"原文成功标准\",\"satisfied\":true|false,\"evidence\":\"支持该裁决的具体执行证据\"}；不得省略或改写 criterion，也不得在 evidence 为空时判 satisfied=true。"
        + "【质量与集成门禁 / Verification & Integration Gate】对涉及代码/文件改动的任务，必须核对实际工具/测试验证证据，并确认隔离 worktree 集成（team_integrate / WorktreeIntegratorService）不存在未解决的合并冲突、write_scope 越界或未关闭的评审阻塞项，方可判 pass。"
        + "若证据不足、测试未通过、存在未解决的 worktree/越界冲突，或审阅发现实质缺陷，应选择 revise（通过 revise_task_indexes 精准指出需返工的任务，便于唤醒持久队友定向修复）；若存在无法自动处理的高风险或需人工拍板的分歧，选择 escalate。不要输出其他文字。";

    private const string EvidenceRefsInstructions =
        "【证据引用要求 / Evidence References Required】本轮要求证据引用：每条 satisfied=true 的 criteria_verdicts 必须同时给出 evidence_refs（字符串数组，指向执行证据中的具体来源，如 task:<task_key>、tool:<tool_id>、evidence:<file> 或 worktree:<handle>）；没有 evidence_refs 时不得判 satisfied=true。satisfied=false 可以省略 evidence_refs。";

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentChatClientFactory _chatClients;
    private readonly ILogger? _logger;

    public ModelUsage? LastUsage { get; private set; }

    public SupervisionAgent(IAgentChatClientFactory chatClients, ILogger? logger = null)
    {
        _chatClients = chatClients;
        _logger = logger;
    }

    public async Task<SupervisionVerdict> ReviewAsync(string userGoal, IReadOnlyList<PlannedTask> tasks, IReadOnlyList<StepResult> results, int revisionRound, CancellationToken ct)
        => await ReviewAsync(userGoal, tasks, results, revisionRound, null, ct).ConfigureAwait(false);

    public async Task<SupervisionVerdict> ReviewAsync(
        string userGoal,
        IReadOnlyList<PlannedTask> tasks,
        IReadOnlyList<StepResult> results,
        int revisionRound,
        string? assembledInstructions,
        CancellationToken ct,
        bool requireEvidenceRefs = false)
    {
        var resolution = await _chatClients.ResolveChatAsync("chat", ct).ConfigureAwait(false);
        if (!resolution.IsAvailable)
        {
            return SupervisionVerdict.EscalateVerdict("Supervision could not run: " + (resolution.Error ?? "chat route unavailable") + ".");
        }

        try
        {
            var chatClient = await _chatClients.CreateAsync(resolution, ct).ConfigureAwait(false);
            var taskLines = string.Join("\n", tasks.Select((task, index) =>
                $"{index}. key={task.TaskKey ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture)} | title={task.Title} | criteria: {string.Join("; ", task.SuccessCriteria)}"));
            var evidenceLines = string.Join("\n", results.Select(result =>
                $"task {result.TaskNodeId}: [{result.Status}] {result.Summary}\n"
                + string.Join("\n", result.Evidence
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Take(8)
                    .Select(item => $"  evidence: {item}"))));
            var prompt = $"用户目标:\n{userGoal}\n\n任务列表:\n{taskLines}\n\n执行证据 (第 {revisionRound} 轮修正后):\n{evidenceLines}";
            var instructions = string.IsNullOrWhiteSpace(assembledInstructions)
                ? SupervisionInstructions
                : assembledInstructions.Trim() + "\n\n" + SupervisionInstructions;
            if (requireEvidenceRefs) instructions += "\n\n" + EvidenceRefsInstructions;
            var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
                chatClient,
                "operation.supervisor",
                "supervisor",
                "Reviews execution evidence and emits a non-authoritative quality verdict.",
                new ChatOptions
                {
                    Instructions = instructions
                },
                prompt,
                ct).ConfigureAwait(false);
            LastUsage = turn.Usage;
            // Raw text: the verdict reader walks balanced JSON candidates itself.
            var verdict = TryParseVerdict(turn.RawText);
            if (verdict is null)
            {
                _logger?.LogWarning("Supervision response could not be parsed; escalating instead of guessing.");
                return SupervisionVerdict.EscalateVerdict("Supervision response was unparsable; manual confirmation is required.");
            }
            return verdict;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Supervision review failed; escalating.");
            return SupervisionVerdict.EscalateVerdict("Supervision review failed: " + ex.Message);
        }
    }

    private static SupervisionVerdict? TryParseVerdict(string? text)
    {
        // Reasoning models may surround the verdict object with thinking prose or
        // markdown fences. Try each balanced object candidate until one carries a
        // decision, rather than spanning the first '{' to the last '}'.
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: false))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<VerdictBody>(candidate, ParseOptions);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Decision)) continue;
                var decision = parsed.Decision.Trim().ToLowerInvariant() switch
                {
                    "pass" => SupervisionDecision.Pass,
                    "revise" => SupervisionDecision.Revise,
                    "escalate" => SupervisionDecision.Escalate,
                    _ => SupervisionDecision.Escalate
                };
                var indexes = (parsed.ReviseTaskIndexes ?? [])
                    .Where(index => index >= 0 && index < 100)
                    .Distinct()
                    .ToArray();
                var criterionVerdicts = (parsed.CriterionVerdicts ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.TaskKey) && !string.IsNullOrWhiteSpace(item.Criterion))
                    .Select(item => new LaneCriterionVerdict(
                        item.TaskKey!.Trim(),
                        item.Criterion!.Trim(),
                        item.Satisfied,
                        item.Evidence,
                        NormalizeRefs(item.EvidenceRefs)))
                    .ToList();
                return new SupervisionVerdict(decision, parsed.Reasons ?? [], decision == SupervisionDecision.Revise ? indexes : [],
                    criterionVerdicts.Count > 0 ? criterionVerdicts : null);
            }
            catch (JsonException)
            {
                // Not the verdict object; try the next balanced candidate.
            }
        }
        return null;
    }

    private static IReadOnlyList<string>? NormalizeRefs(string[]? refs)
    {
        var normalized = (refs ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => item.Length <= 512)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
        return normalized.Length > 0 ? normalized : null;
    }

    private sealed class VerdictBody
    {
        [JsonPropertyName("decision")]
        public string? Decision { get; set; }

        [JsonPropertyName("reasons")]
        public string[]? Reasons { get; set; }

        [JsonPropertyName("revise_task_indexes")]
        public int[]? ReviseTaskIndexes { get; set; }

        [JsonPropertyName("criteria_verdicts")]
        public CriterionVerdictBody[]? CriterionVerdicts { get; set; }
    }

    private sealed class CriterionVerdictBody
    {
        [JsonPropertyName("task_key")]
        public string? TaskKey { get; set; }

        [JsonPropertyName("criterion")]
        public string? Criterion { get; set; }

        [JsonPropertyName("satisfied")]
        public bool Satisfied { get; set; }

        [JsonPropertyName("evidence")]
        public string? Evidence { get; set; }

        [JsonPropertyName("evidence_refs")]
        public string[]? EvidenceRefs { get; set; }
    }
}
