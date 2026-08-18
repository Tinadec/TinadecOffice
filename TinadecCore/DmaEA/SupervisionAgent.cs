using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
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
/// supervisor cannot approve and the user must decide.
/// </summary>
public sealed record SupervisionVerdict(
    SupervisionDecision Decision,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<int> ReviseTaskIndexes)
{
    public static SupervisionVerdict EscalateVerdict(string reason) => new(SupervisionDecision.Escalate, [reason], []);
    public string DecictionString() => Decision switch
    {
        SupervisionDecision.Pass => "pass",
        SupervisionDecision.Revise => "revise",
        _ => "escalate"
    };
}

/// <summary>
/// Supervision-layer agent: reviews planned tasks against execution evidence and returns
/// pass / revise / escalate. Model unavailability or an unparsable response escalates —
/// it never fabricates a pass.
/// </summary>
public sealed class SupervisionAgent
{
    private const string SupervisionInstructions =
        "你是监督智能体。对照任务列表与执行证据给出质量结论。仅输出 JSON 对象，包含 decision（pass/revise/escalate）、reasons（字符串数组）、revise_task_indexes（decision 为 revise 时需要重做的任务下标数组，其他情况为空数组）。证据不足或存在无法自动处理的风险时选择 escalate。不要输出其他文字。";

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentChatClientFactory _chatClients;
    private readonly ILogger? _logger;

    public SupervisionAgent(IAgentChatClientFactory chatClients, ILogger? logger = null)
    {
        _chatClients = chatClients;
        _logger = logger;
    }

    public async Task<SupervisionVerdict> ReviewAsync(string userGoal, IReadOnlyList<PlannedTask> tasks, IReadOnlyList<StepResult> results, int revisionRound, CancellationToken ct)
    {
        var resolution = await _chatClients.ResolveChatAsync("chat", ct).ConfigureAwait(false);
        if (!resolution.IsAvailable)
        {
            return SupervisionVerdict.EscalateVerdict("Supervision could not run: " + (resolution.Error ?? "chat route unavailable") + ".");
        }

        try
        {
            var chatClient = _chatClients.Create(resolution);
            var taskLines = string.Join("\n", tasks.Select((task, index) => $"{index}. {task.Title} | criteria: {string.Join("; ", task.SuccessCriteria)}"));
            var evidenceLines = string.Join("\n", results.Select(result => $"task {result.TaskNodeId}: [{result.Status}] {result.Summary}"));
            var prompt = $"用户目标:\n{userGoal}\n\n任务列表:\n{taskLines}\n\n执行证据 (第 {revisionRound} 轮修正后):\n{evidenceLines}";
            var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = "supervisor", ChatOptions = new ChatOptions { Instructions = SupervisionInstructions } });
            var response = await agent.RunAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            var verdict = TryParseVerdict(response.Text);
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
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<VerdictBody>(text.Substring(start, end - start + 1), ParseOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Decision)) return null;
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
            return new SupervisionVerdict(decision, parsed.Reasons ?? [], decision == SupervisionDecision.Revise ? indexes : []);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class VerdictBody
    {
        [JsonPropertyName("decision")]
        public string? Decision { get; set; }

        [JsonPropertyName("reasons")]
        public string[]? Reasons { get; set; }

        [JsonPropertyName("revise_task_indexes")]
        public int[]? ReviseTaskIndexes { get; set; }
    }
}
