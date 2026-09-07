using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Execution-layer model adapter. It deliberately exposes declaration-only tools
/// and returns model function calls to the durable run engine instead of invoking
/// them through MAF's automatic function-invocation pipeline.
/// </summary>
public sealed class ExecutionAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentChatClientFactory _chatClients;
    private readonly ILogger? _logger;

    public ExecutionAgent(IAgentChatClientFactory chatClients, ILogger? logger = null)
    {
        _chatClients = chatClients;
        _logger = logger;
    }

    /// <summary>Compatibility entry point used by the legacy request-bound runtime.</summary>
    public async Task<StepResult> ExecuteAsync(DmaeaRunContext ctx, AgentDefinition agent, PlannedTask task, Guid taskNodeId, CancellationToken ct)
    {
        var turn = await GetNextTurnAsync(ctx, agent, task, [], [], null, 24, ct).ConfigureAwait(false);
        if (!turn.IsAvailable)
        {
            return Failed(taskNodeId, agent, turn.Error ?? "Chat route unavailable.");
        }
        if (turn.Calls.Count != 0)
        {
            return Failed(taskNodeId, agent, "The legacy execution runtime does not support tool calls.");
        }
        return string.IsNullOrWhiteSpace(turn.Text)
            ? Failed(taskNodeId, agent, "Execution returned no output.")
            : Completed(taskNodeId, agent, turn.Text);
    }

    /// <summary>
    /// Sends exactly one worker model turn. <paramref name="history"/> is a
    /// checkpoint-safe DTO transcript rebuilt into M.E.AI message content here.
    /// No tool function has an implementation delegate, so the caller remains the
    /// sole authority for checkpointing, policy, approval, and dispatch.
    /// </summary>
    public async Task<WorkerModelTurn> GetNextTurnAsync(
        DmaeaRunContext ctx,
        AgentDefinition agent,
        PlannedTask task,
        IReadOnlyList<WorkerToolTurn> history,
        IReadOnlyList<WorkerToolDescriptor> tools,
        string? assembledInstructions,
        int maxHistoryMessages,
        CancellationToken ct)
    {
        var resolved = await _chatClients.ResolveChatAsync(agent.ModelRoutePurpose ?? "chat", ct).ConfigureAwait(false);
        if (!resolved.IsAvailable)
        {
            _logger?.LogWarning("Execution agent {Agent} failed to resolve chat route: {Error}", agent.Name, resolved.Error);
            return WorkerModelTurn.Unavailable(resolved.Error ?? "Chat route unavailable.");
        }

        var taskInstructions = $"你是执行层 agent（{agent.Name}）。执行以下任务：\n标题：{task.Title}\n描述：{task.Description}\n成功标准：{string.Join("; ", task.SuccessCriteria)}\n只输出完成摘要。";
        var instructions = string.IsNullOrWhiteSpace(assembledInstructions)
            ? taskInstructions
            : assembledInstructions.Trim() + "\n\n" + taskInstructions;
        var options = new ChatOptions
        {
            Instructions = instructions,
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = true
        };
        if (tools.Count != 0)
        {
            options.Tools = tools
                .Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(tool.ToolId, tool.Description, tool.InputSchema, null))
                .ToList();
        }

        var conversation = await BuildConversationAsync(ctx.UserGoal, history, maxHistoryMessages, ct).ConfigureAwait(false);
        var response = await (await _chatClients.CreateAsync(resolved, ct).ConfigureAwait(false))
            .GetResponseAsync(conversation, options, ct)
            .ConfigureAwait(false);
        var messages = response.Messages.Count != 0
            ? response.Messages.ToList()
            : [new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty)];
        var calls = new List<WorkerToolCall>();
        foreach (var call in messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
        {
            // Informational calls deliberately have no side effect.
            if (call.InformationalOnly) continue;
            if (call.Exception is not null)
            {
                return WorkerModelTurn.Invalid("The model returned an invalid tool call mapping.");
            }
            if (string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name))
            {
                return WorkerModelTurn.Invalid("The model returned a tool call without an id or name.");
            }
            calls.Add(new WorkerToolCall(call.CallId, call.Name, SerializeArguments(call.Arguments)));
        }

        var duplicate = calls.GroupBy(call => call.CallId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
        {
            return WorkerModelTurn.Invalid($"The model reused tool call id '{duplicate.Key}' in one response.");
        }

        // Reasoning models leak <think> blocks and orphan tags into content; strip them
        // so the worker's stored answer/evidence (and the AssistantText echoed back
        // into multi-turn history) never carries thinking markup. Tool calls are
        // captured separately above, so stripping text cannot lose a function call.
        var text = ModelOutputText.AnswerText(response.Text);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = messages
                .Select(message => ModelOutputText.AnswerText(message.Text))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }
        return new WorkerModelTurn(true, text, calls, null, Maf18RuntimeAdapter.NormalizeUsage(response.Usage));
    }

    private static async Task<IReadOnlyList<ChatMessage>> BuildConversationAsync(
        string goal,
        IReadOnlyList<WorkerToolTurn> history,
        int maxHistoryMessages,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>();
        foreach (var turn in history)
        {
            var assistantContents = new List<AIContent>();
            if (!string.IsNullOrWhiteSpace(turn.AssistantText)) assistantContents.Add(new TextContent(turn.AssistantText));
            assistantContents.Add(new FunctionCallContent(turn.CallId, turn.ToolId, ParseArguments(turn.ArgumentsJson)));
            messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));
            if (!string.IsNullOrWhiteSpace(turn.ResultJson))
            {
                messages.Add(new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent(turn.CallId, ParseJsonValue(turn.ResultJson))
                ]));
            }
        }
        return await Maf18RuntimeAdapter.CompactWorkerConversationAsync(
            new ChatMessage(ChatRole.User, goal),
            messages,
            maxHistoryMessages,
            cancellationToken).ConfigureAwait(false);
    }

    private static IDictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();
            return document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object? ParseJsonValue(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments) => JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), JsonOptions);

    private static StepResult Completed(Guid taskNodeId, AgentDefinition agent, string text) => new()
    {
        TaskNodeId = taskNodeId,
        AgentId = agent.Id.ToString("N"),
        Status = "completed",
        Summary = text,
        Evidence = [text]
    };

    private static StepResult Failed(Guid taskNodeId, AgentDefinition agent, string message) => new()
    {
        TaskNodeId = taskNodeId,
        AgentId = agent.Id.ToString("N"),
        Status = "failed",
        Summary = message,
        Evidence = []
    };
}

/// <summary>Manifest-backed declaration supplied to one worker model turn.</summary>
public sealed record WorkerToolDescriptor(string ToolId, string Description, JsonElement InputSchema);

/// <summary>A model-produced function request in a checkpoint-safe form.</summary>
public sealed record WorkerToolCall(string CallId, string ToolId, string ArgumentsJson);

/// <summary>One raw worker model response, before any tool side effect is started.</summary>
public sealed record WorkerModelTurn(
    bool IsAvailable,
    string Text,
    IReadOnlyList<WorkerToolCall> Calls,
    string? Error,
    ModelUsage? Usage = null)
{
    public static WorkerModelTurn Unavailable(string error) => new(false, string.Empty, [], error);
    public static WorkerModelTurn Invalid(string error) => new(true, string.Empty, [], error);
}

/// <summary>
/// Durable representation of an assistant tool-call plus its eventual Core-owned
/// execution/approval/result. Framework content is rebuilt only in memory.
/// </summary>
public sealed class WorkerToolTurn
{
    public string CallId { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public string? AssistantText { get; set; }
    public string? ExecutionId { get; set; }
    public string? ApprovalId { get; set; }
    public string? DispatchStatus { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorCategory { get; set; }
}
