using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Execution-layer agent: executes a single planned task and produces a <see cref="StepResult"/>.
/// Returns a failed result (never throws) when the chat route is unavailable, so the
/// orchestrator can record the failure per task and keep the run auditable.
/// </summary>
public sealed class ExecutionAgent
{
    private readonly IChatResolver _chatResolver;
    private readonly ILogger? _logger;
    private readonly Func<ChatResolution, IChatClient>? _chatClientFactory;

    public ExecutionAgent(IChatResolver chatResolver, ILogger? logger = null, Func<ChatResolution, IChatClient>? chatClientFactory = null)
    {
        _chatResolver = chatResolver;
        _logger = logger;
        _chatClientFactory = chatClientFactory;
    }

    public async Task<StepResult> ExecuteAsync(DmaeaRunContext ctx, AgentDefinition agent, PlannedTask task, Guid taskNodeId, CancellationToken ct)
    {
        var resolved = await _chatResolver.ResolveChatAsync(agent.ModelRoutePurpose ?? "chat", ct).ConfigureAwait(false);
        if (!resolved.IsAvailable)
        {
            _logger?.LogWarning("Execution agent {Agent} failed to resolve chat route: {Error}", agent.Name, resolved.Error);
            return new StepResult
            {
                TaskNodeId = taskNodeId,
                AgentId = agent.Id.ToString("N"),
                Status = "failed",
                Summary = resolved.Error ?? "Chat route unavailable.",
                Evidence = []
            };
        }

        var chatClient = _chatClientFactory is not null ? _chatClientFactory(resolved) : PlanningAgent.DefaultChatClient(resolved);
        var instructions = $"你是执行层 agent（{agent.Name}）。执行以下任务：\n标题：{task.Title}\n描述：{task.Description}\n成功标准：{string.Join("; ", task.SuccessCriteria)}\n只输出完成摘要。";
        var agentInstance = new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = agent.Name, ChatOptions = new ChatOptions { Instructions = instructions } });

        var response = await agentInstance.RunAsync(ctx.UserGoal, cancellationToken: ct).ConfigureAwait(false);
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new StepResult
            {
                TaskNodeId = taskNodeId,
                AgentId = agent.Id.ToString("N"),
                Status = "failed",
                Summary = "Execution returned no output.",
                Evidence = []
            };
        }
        return new StepResult
        {
            TaskNodeId = taskNodeId,
            AgentId = agent.Id.ToString("N"),
            Status = "completed",
            Summary = text,
            Evidence = [text]
        };
    }
}
