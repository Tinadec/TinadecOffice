using System.Text.Json.Nodes;
using TinadecCore.Storage;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Tracing;

namespace TinadecCore.Services;

public sealed class OrchestratorService
{
    private const int MaxAgentTurns = 8;

    private readonly CoreStore _store;
    private readonly EventHub _events;
    private readonly IAgentWorkflowRuntime _workflowRuntime;
    private readonly IModelInvocationRuntime _modelRuntime;
    private readonly PromptContextService _promptContextService;
    private readonly IToolRegistry _tools;
    private readonly ICapabilityPolicy _capabilityPolicy;
    private readonly IReadOnlyList<IToolInvocationAdapter> _invocationAdapters;

    public OrchestratorService(
        CoreStore store,
        EventHub events,
        IAgentWorkflowRuntime workflowRuntime,
        IModelInvocationRuntime modelRuntime,
        PromptContextService promptContextService,
        IToolRegistry tools,
        ICapabilityPolicy capabilityPolicy,
        IEnumerable<IToolInvocationAdapter> invocationAdapters)
    {
        _store = store;
        _events = events;
        _workflowRuntime = workflowRuntime;
        _modelRuntime = modelRuntime;
        _promptContextService = promptContextService;
        _tools = tools;
        _capabilityPolicy = capabilityPolicy;
        _invocationAdapters = invocationAdapters.ToArray();
    }

    public OrchestrationSnapshotDto CreateRunForMessage(string sessionId, string userMessageId, string userContent)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentTurn);
        activity?
            .SetTag(SpanAttrs.SessionId, sessionId)
            .SetTag(SpanAttrs.UserMessageId, userMessageId);

        var snapshot = _store.CreateOrchestrationRun(sessionId, userMessageId, userContent);
        if (snapshot.Run is null)
        {
            return snapshot;
        }

        Publish("run.started", sessionId, new JsonObject
        {
            ["run_id"] = snapshot.Run.Id,
            ["summary"] = snapshot.Run.Summary,
            ["status"] = snapshot.Run.Status
        }, ["agent.run"]);

        if (snapshot.Graph is not null)
        {
            Publish("task_graph.created", sessionId, new JsonObject
            {
                ["run_id"] = snapshot.Run.Id,
                ["graph_id"] = snapshot.Graph.Id,
                ["node_count"] = snapshot.Nodes.Count
            }, ["task_graph.create"]);
        }

        foreach (var assignment in snapshot.Assignments)
        {
            Publish("task.assigned", sessionId, new JsonObject
            {
                ["run_id"] = assignment.RunId,
                ["task_node_id"] = assignment.TaskNodeId,
                ["agent_id"] = assignment.AgentId,
                ["agent_type"] = assignment.AgentType,
                ["permission_mode"] = assignment.PermissionMode
            }, ["task.assign", "agent.execution"]);
        }

        var workflow = _workflowRuntime.Compile(snapshot);
        Publish("agent.workflow.compiled", sessionId, new JsonObject
        {
            ["run_id"] = workflow.RunId,
            ["runtime"] = workflow.Runtime,
            ["step_count"] = workflow.Steps.Count
        }, ["agent.workflow", "runtime.core-workflow"]);

        foreach (var result in snapshot.StepResults)
        {
            Publish("step.result.created", sessionId, new JsonObject
            {
                ["run_id"] = result.RunId,
                ["task_node_id"] = result.TaskNodeId,
                ["agent_id"] = result.AgentId,
                ["status"] = result.Status
            }, ["step.result"]);
        }

        foreach (var finding in snapshot.SupervisionFindings)
        {
            Publish("supervision.checked", sessionId, new JsonObject
            {
                ["run_id"] = finding.RunId,
                ["severity"] = finding.Severity,
                ["category"] = finding.Category,
                ["status"] = finding.Status
            }, ["supervisor.check"]);
        }

        foreach (var pack in snapshot.ContextPacks)
        {
            Publish("context.pack.created", sessionId, new JsonObject
            {
                ["run_id"] = pack.RunId,
                ["context_pack_id"] = pack.Id,
                ["token_budget"] = pack.TokenBudget,
                ["compression_ratio"] = pack.CompressionRatio
            }, ["context.compact"]);
        }

        return snapshot;
    }

    public async Task<SessionModelOrchestrationResult> CompleteRunWithModelAsync(
        OrchestrationSnapshotDto snapshot,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Run is null)
        {
            return new SessionModelOrchestrationResult(null, null);
        }

        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentInference);
        activity?
            .SetTag(SpanAttrs.RunId, snapshot.Run.Id)
            .SetTag(SpanAttrs.SessionId, snapshot.Run.SessionId)
            .SetTag(SpanAttrs.RoutePurpose, "planner");

        var promptContext = await _promptContextService.BuildForRunAsync(
            snapshot,
            "agent_meeting",
            cancellationToken: cancellationToken);

        // Build OpenAI-compatible tool specs for the model to use
        var toolSpecs = _tools.BuildOpenAiToolSpecs("programming");
        var hasTools = toolSpecs.Count > 0;

        // Conversation history for the agent loop (mutable list)
        var conversation = new List<MessageDto>(_store.ListMessages(snapshot.Run.SessionId));

        ModelInvocationResultDto? lastInvocation = null;

        for (var turn = 0; turn < MaxAgentTurns; turn++)
        {
            var invocation = await _modelRuntime.InvokeAsync(
                snapshot.Run.SessionId,
                "planner",
                conversation,
                cancellationToken,
                promptContext.SystemPrompt,
                hasTools ? toolSpecs : null);

            lastInvocation = invocation;

            activity?
                .SetTag(SpanAttrs.ProviderId, invocation.Context.ProviderInstanceId)
                .SetTag(SpanAttrs.ProviderInstanceId, invocation.Context.ProviderInstanceId)
                .SetTag(SpanAttrs.Model, invocation.Context.EffectiveModel)
                .SetTag(SpanAttrs.Status, invocation.Status)
                .SetTag(SpanAttrs.ErrorCategory, invocation.ErrorCategory?.ToString())
                .SetTag(SpanAttrs.FallbackProviderId, IsFallback(invocation) ? invocation.ErrorProviderId : null);

            PublishModelEvent("model.requested", snapshot.Run.SessionId, snapshot.Run.Id, invocation, promptContext, ["agent.meeting", "model.remote"]);

            if (!string.Equals(invocation.Status, "executed", StringComparison.OrdinalIgnoreCase))
            {
                PublishModelEvent("model.failed", snapshot.Run.SessionId, snapshot.Run.Id, invocation, promptContext, ["agent.meeting", "model.remote", "model.error"]);
                return new SessionModelOrchestrationResult(null, invocation);
            }

            // Check if the model wants to call tools
            var toolCalls = invocation.ToolCalls;
            if (toolCalls is null || toolCalls.Count == 0)
            {
                // No tool calls — final text response
                var reply = invocation.Content;
                if (snapshot.Graph is not null && turn == 0)
                {
                    reply = $"{reply}\n\nTask graph ready: {snapshot.Graph.Title} with {snapshot.Nodes.Count} nodes and {snapshot.Assignments.Count} execution assignments. Mutating actions remain approval-gated.";
                }

                var assistantMessage = _store.AddMessage(snapshot.Run.SessionId, "assistant", reply);
                Publish("message.created", snapshot.Run.SessionId, new JsonObject
                {
                    ["message_id"] = assistantMessage.Id,
                    ["role"] = assistantMessage.Role,
                    ["run_id"] = snapshot.Run.Id,
                    ["route_purpose"] = invocation.Context.Purpose,
                    ["provider_instance_id"] = invocation.Context.ProviderInstanceId,
                    ["model"] = invocation.Context.EffectiveModel,
                    ["agent_turn"] = turn,
                    ["fallback_provider_selected"] = IsFallback(invocation)
                }, ["agent.message", "agent.meeting", "model.remote"]);

                PublishModelEvent("model.completed", snapshot.Run.SessionId, snapshot.Run.Id, invocation, promptContext, ["agent.meeting", "model.remote"]);
                return new SessionModelOrchestrationResult(assistantMessage, invocation);
            }

            // Model wants to call tools — process them
            Publish("model.tool_calls", snapshot.Run.SessionId, new JsonObject
            {
                ["run_id"] = snapshot.Run.Id,
                ["turn"] = turn,
                ["tool_call_count"] = toolCalls.Count,
                ["tool_ids"] = new JsonArray(toolCalls.Select(tc => JsonValue.Create(tc.ToolId)).ToArray())
            }, ["agent.tool_calls", "model.remote"]);

            // Add assistant message with tool_calls to conversation (not persisted, just for model context)
            var toolCallSummary = string.Join("\n", toolCalls.Select(tc => $"[Calling {tc.ToolId}]"));
            conversation.Add(new MessageDto(
                $"asst_tc_{Guid.NewGuid():N}",
                snapshot.Run.SessionId,
                "assistant",
                string.IsNullOrWhiteSpace(invocation.Content) ? toolCallSummary : $"{invocation.Content}\n{toolCallSummary}",
                DateTimeOffset.UtcNow));

            // Execute each tool call
            var pendingApprovals = new List<ToolCallDto>();
            foreach (var toolCall in toolCalls)
            {
                var tool = _tools.Resolve(toolCall.ToolId);
                if (tool is null)
                {
                    // Tool not found — return error as tool result
                    conversation.Add(new MessageDto(
                        $"tool_{Guid.NewGuid():N}",
                        snapshot.Run.SessionId,
                        "tool",
                        $"Error: Tool '{toolCall.ToolId}' is not registered in the tool registry.",
                        DateTimeOffset.UtcNow,
                        toolCall.CallId));
                    continue;
                }

                // Check if approval is needed
                if (tool.RequiresApproval || _capabilityPolicy.Evaluate("approval", tool).Required)
                {
                    // Create an approval record
                    var approval = _store.CreateApproval(new CreateApprovalRequest(
                        snapshot.Run.SessionId,
                        tool.Id,
                        tool.DisplayName,
                        SummarizeToolCall(toolCall),
                        null));
                    Publish("tool.execution.approval_required", snapshot.Run.SessionId, new JsonObject
                    {
                        ["run_id"] = snapshot.Run.Id,
                        ["tool_id"] = tool.Id,
                        ["tool_call_id"] = toolCall.CallId,
                        ["approval_id"] = approval.Id,
                        ["agent_turn"] = turn
                    }, ["tool.execution", "approval.ask"]);

                    pendingApprovals.Add(toolCall);
                    conversation.Add(new MessageDto(
                        $"tool_{Guid.NewGuid():N}",
                        snapshot.Run.SessionId,
                        "tool",
                        $"Tool '{tool.Id}' requires user approval before execution. Approval ID: {approval.Id}. Please wait for user decision.",
                        DateTimeOffset.UtcNow,
                        toolCall.CallId));
                    continue;
                }

                // Execute read-only tool
                var result = await ExecuteToolCallAsync(snapshot.Run, tool, toolCall, cancellationToken);

                // Record step result
                _store.AddStepResult(
                    snapshot.Run.Id,
                    $"agent_turn_{turn}_{tool.Id}",
                    "agent_planner",
                    result.Status,
                    result.Summary,
                    result.Evidence);

                Publish("tool.execution.completed", snapshot.Run.SessionId, new JsonObject
                {
                    ["run_id"] = snapshot.Run.Id,
                    ["tool_id"] = tool.Id,
                    ["tool_call_id"] = toolCall.CallId,
                    ["status"] = result.Status,
                    ["agent_turn"] = turn
                }, ["tool.execution", "step.result"]);

                // Add tool result to conversation
                var resultContent = FormatToolResult(result);
                conversation.Add(new MessageDto(
                    $"tool_{Guid.NewGuid():N}",
                    snapshot.Run.SessionId,
                    "tool",
                    resultContent,
                    DateTimeOffset.UtcNow,
                    toolCall.CallId));
            }

            // If any tool required approval, return early with pending approval info
            if (pendingApprovals.Count > 0)
            {
                var approvalNote = $"I need your approval to execute {pendingApprovals.Count} tool(s): {string.Join(", ", pendingApprovals.Select(tc => tc.ToolId))}. Please review the pending approvals and decide.";
                var approvalMessage = _store.AddMessage(snapshot.Run.SessionId, "assistant", approvalNote);
                Publish("message.created", snapshot.Run.SessionId, new JsonObject
                {
                    ["message_id"] = approvalMessage.Id,
                    ["role"] = approvalMessage.Role,
                    ["run_id"] = snapshot.Run.Id,
                    ["pending_approval_count"] = pendingApprovals.Count
                }, ["agent.message", "approval.ask"]);

                return new SessionModelOrchestrationResult(approvalMessage, lastInvocation);
            }

            // Continue loop — model will see tool results and decide next action
        }

        // Max turns reached
        var fallbackReply = lastInvocation?.Content ?? "Agent loop reached maximum turns without a final response.";
        var fallbackMessage = _store.AddMessage(snapshot.Run.SessionId, "assistant", fallbackReply);
        Publish("message.created", snapshot.Run.SessionId, new JsonObject
        {
            ["message_id"] = fallbackMessage.Id,
            ["role"] = fallbackMessage.Role,
            ["run_id"] = snapshot.Run.Id,
            ["max_turns_reached"] = true
        }, ["agent.message", "agent.loop.limit"]);

        return new SessionModelOrchestrationResult(fallbackMessage, lastInvocation);
    }

    private async Task<CodeToolExecuteResultDto> ExecuteToolCallAsync(
        OrchestrationRunDto run,
        ToolDescriptorDto tool,
        ToolCallDto toolCall,
        CancellationToken cancellationToken)
    {
        var session = _store.ListSessions(null).FirstOrDefault(item => item.Id == run.SessionId);
        var project = session is null ? null : _store.ListProjects().FirstOrDefault(item => item.Id == session.ProjectId);
        var cwd = project?.Path ?? Directory.GetCurrentDirectory();

        var request = new CodeToolExecuteRequest(
            run.SessionId,
            run.Id,
            null,
            null,
            cwd,
            toolCall.Arguments);

        var adapter = _invocationAdapters.FirstOrDefault(item => item.CanInvoke(tool));
        if (adapter is null)
        {
            return new CodeToolExecuteResultDto(
                tool.Id,
                "failed",
                $"No invocation adapter registered for tool source '{tool.Source}'.",
                ["adapter missing", tool.Source],
                new Dictionary<string, object?>(),
                false,
                null);
        }

        try
        {
            return await adapter.InvokeAsync(tool, request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new CodeToolExecuteResultDto(
                tool.Id,
                "failed",
                $"Tool execution failed: {ex.Message}",
                ["execution failed", tool.Id],
                new Dictionary<string, object?>(),
                false,
                null);
        }
    }

    private static string FormatToolResult(CodeToolExecuteResultDto result)
    {
        var status = result.Status;
        var summary = result.Summary;
        var dataKeys = result.Data.Count > 0 ? string.Join(", ", result.Data.Keys.Take(8)) : "none";
        return $"[{status}] {summary}\nData keys: {dataKeys}";
    }

    private static string SummarizeToolCall(ToolCallDto toolCall)
    {
        var argKeys = toolCall.Arguments.Count > 0 ? string.Join(", ", toolCall.Arguments.Keys) : "none";
        return $"{toolCall.ToolId}(args: {argKeys})";
    }

    public async Task DispatchReadOnlyToolsAsync(
        OrchestrationSnapshotDto snapshot,
        string userContent,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Run is null)
        {
            return;
        }

        using var dispatchActivity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentToolDispatch);
        dispatchActivity?
            .SetTag(SpanAttrs.RunId, snapshot.Run.Id)
            .SetTag(SpanAttrs.AutoDispatch, true);

        var workflow = _workflowRuntime.Compile(snapshot);
        var session = _store.ListSessions(null).FirstOrDefault(item => item.Id == snapshot.Run.SessionId);
        var project = session is null
            ? null
            : _store.ListProjects().FirstOrDefault(item => item.Id == session.ProjectId);
        var cwd = project?.Path ?? Directory.GetCurrentDirectory();

        foreach (var step in workflow.Steps)
        {
            foreach (var toolId in step.ToolIds)
            {
                var tool = _tools.Resolve(toolId);
                if (tool is null || !_capabilityPolicy.IsReadOnly(tool))
                {
                    continue;
                }

                using var toolSpan = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentToolExecution);
                toolSpan?
                    .SetTag(SpanAttrs.ToolId, tool.Id)
                    .SetTag(SpanAttrs.TaskNodeId, step.TaskNodeId)
                    .SetTag(SpanAttrs.PermissionMode, "read-only");

                Publish("tool.execution.requested", snapshot.Run.SessionId, new JsonObject
                {
                    ["run_id"] = snapshot.Run.Id,
                    ["tool_id"] = tool.Id,
                    ["task_node_id"] = step.TaskNodeId,
                    ["auto_dispatch"] = true
                }, ["tool.execution", "agent.workflow"]);

                try
                {
                    var adapter = _invocationAdapters.FirstOrDefault(item => item.CanInvoke(tool));
                    if (adapter is null)
                    {
                        throw new InvalidOperationException($"No Core invocation adapter is registered for tool source '{tool.Source}'.");
                    }

                    var result = await adapter.InvokeAsync(
                        tool,
                        new CodeToolExecuteRequest(
                            snapshot.Run.SessionId,
                            snapshot.Run.Id,
                            step.TaskNodeId,
                            null,
                            cwd,
                            BuildReadOnlyArguments(tool.Id, userContent)),
                        cancellationToken);

                    var stepResult = _store.AddStepResult(
                        snapshot.Run.Id,
                        step.TaskNodeId,
                        step.AgentId,
                        result.Status,
                        result.Summary,
                        result.Evidence);

                    Publish(result.Status is "failed" or "blocked" ? "tool.execution.failed" : "tool.execution.completed",
                        snapshot.Run.SessionId,
                        new JsonObject
                        {
                            ["run_id"] = snapshot.Run.Id,
                            ["tool_id"] = tool.Id,
                            ["task_node_id"] = step.TaskNodeId,
                            ["status"] = result.Status,
                            ["step_result_id"] = stepResult.Id
                        },
                        ["tool.execution", "step.result"]);
                }
                catch (Exception ex)
                {
                    var stepResult = _store.AddStepResult(
                        snapshot.Run.Id,
                        step.TaskNodeId,
                        step.AgentId,
                        "failed",
                        $"Read-only tool dispatch failed: {ex.Message}",
                        ["tool dispatch failed", tool.Id]);

                    Publish("tool.execution.failed", snapshot.Run.SessionId, new JsonObject
                    {
                        ["run_id"] = snapshot.Run.Id,
                        ["tool_id"] = tool.Id,
                        ["task_node_id"] = step.TaskNodeId,
                        ["status"] = "failed",
                        ["step_result_id"] = stepResult.Id
                    }, ["tool.execution", "step.result"]);
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> BuildReadOnlyArguments(string toolId, string userContent)
    {
        return toolId switch
        {
            "search_files" => new Dictionary<string, object?>
            {
                ["query"] = string.IsNullOrWhiteSpace(userContent) ? "Tinadec" : userContent,
                ["limit"] = 10
            },
            _ => new Dictionary<string, object?>()
        };
    }

    private void PublishModelEvent(
        string type,
        string sessionId,
        string runId,
        ModelInvocationResultDto invocation,
        PromptContextPreviewDto? promptContext,
        IReadOnlyList<string> capabilities)
    {
        var payload = new JsonObject
        {
            ["run_id"] = runId,
            ["agent_id"] = "agent_meeting",
            ["agent_type"] = "meeting",
            ["status"] = invocation.Status,
            ["route_purpose"] = invocation.Context.Purpose,
            ["provider_instance_id"] = invocation.Context.ProviderInstanceId,
            ["driver"] = invocation.Context.Driver,
            ["connection_kind"] = invocation.Context.ConnectionKind,
            ["model"] = invocation.Context.EffectiveModel,
            ["used_stub_response"] = invocation.UsedStubResponse,
            ["runtime_id"] = invocation.RuntimeId,
            ["error_category"] = invocation.ErrorCategory?.ToString(),
            ["is_retryable"] = invocation.IsRetryable,
            ["fallback_provider_selected"] = IsFallback(invocation)
        };

        if (promptContext is not null)
        {
            payload["prompt_fragment_ids"] = new JsonArray(promptContext.Fragments.Select(fragment => JsonValue.Create(fragment.Id)).ToArray());
            payload["prompt_estimated_tokens"] = promptContext.EstimatedTokens;
            payload["prompt_warning_count"] = promptContext.Warnings.Count;
            payload["prompt_context_pack_ids"] = new JsonArray(promptContext.ContextPackIds.Select(id => JsonValue.Create(id)).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(invocation.ErrorProviderId))
        {
            payload["error_provider_instance_id"] = invocation.ErrorProviderId;
        }

        if (!string.IsNullOrWhiteSpace(invocation.SafeErrorMessage))
        {
            payload["safe_error_message"] = invocation.SafeErrorMessage;
        }

        Publish(type, sessionId, payload, capabilities);
    }

    private static bool IsFallback(ModelInvocationResultDto invocation)
    {
        return !string.IsNullOrWhiteSpace(invocation.ErrorProviderId)
            && !invocation.ErrorProviderId.Equals(invocation.Context.ProviderInstanceId, StringComparison.OrdinalIgnoreCase);
    }

    private void Publish(string type, string sessionId, JsonObject payload, IReadOnlyList<string> capabilities)
    {
        var envelope = _store.AppendNewEvent(type, sessionId, payload, capabilities);
        _events.Publish(envelope);
    }
}

public sealed record SessionModelOrchestrationResult(
    MessageDto? AssistantMessage,
    ModelInvocationResultDto? Invocation);
