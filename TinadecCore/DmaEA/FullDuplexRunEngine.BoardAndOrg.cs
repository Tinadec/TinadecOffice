using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    private const int MaxPoolSiblings = 16;
    private const int TaskWaitFieldLimit = 2000;
    private const int MaxBoardMessagesInPrompt = 20;
    private const int MaxTeamBoardHistory = 200;

    private static readonly Regex MentionHandleRegex = new(@"(?<![A-Za-z0-9_.-])@([A-Za-z0-9][A-Za-z0-9_-]{0,47})", RegexOptions.Compiled);

    private sealed record LiveMcpBoardPost(
        string Sender,
        string Target,
        string Kind,
        string Content,
        bool Wake,
        DateTimeOffset CreatedAt);

    private sealed record TaskWaitOutcome(FullDuplexCheckpointV1 Checkpoint, bool Parked);

    public Task<TeamBoardToolCallResult> ExecuteBoardActionAsync(
        TeamBoardToolCallContext context,
        CancellationToken cancellationToken = default) =>
        HandleLiveMcpBoardCallAsync(context, cancellationToken);

    private async Task<TeamBoardToolCallResult> HandleLiveMcpBoardCallAsync(
        TeamBoardToolCallContext context,
        CancellationToken cancellationToken)
    {
        if (CoreVirtualToolPolicy.IsTeamMessage(context.ToolName))
        {
            var args = context.Arguments;
            var target = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("target", out var targetEl)
                && targetEl.ValueKind == JsonValueKind.String
                ? targetEl.GetString()?.Trim().TrimStart('@')
                : null;
            var content = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String
                ? contentEl.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(content))
            {
                return new TeamBoardToolCallResult(false, Error: "team_message requires non-empty 'target' and 'content'.");
            }

            var kind = args.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(kindEl.GetString())
                ? kindEl.GetString()!.Trim().ToLowerInvariant()
                : "update";
            var wake = !args.TryGetProperty("wake", out var wakeEl) || wakeEl.ValueKind != JsonValueKind.False;
            var boundedContent = BoundText(content, 4000);

            // Fast-path: if the run's actor hub is currently live, deliver straight to the authoritative board & target mailboxes in sub-millisecond time!
            if (_runActorHubs.TryGetValue(context.RunId, out var hub) && hub.LiveCheckpoint is not null)
            {
                long deliveredSeq = 0;
                var wokenHandles = new List<string>();
                await using (await EnterCheckpointGateAsync(context.RunId, cancellationToken).ConfigureAwait(false))
                {
                    if (hub.LiveCheckpoint is { } liveCp)
                    {
                        var entry = AppendBoardMessage(liveCp, context.TeammateHandle, target, kind, boundedContent, wake);
                        deliveredSeq = entry.Seq;
                        DeliverBoardMessageToLiveMailboxes(liveCp, hub, entry);
                        if (wake)
                        {
                            var coordinatorTaskId = liveCp.Tasks.FirstOrDefault(t => t.IsCoordinatorTask)?.TaskId;
                            var senderTm = liveCp.Teammates.FirstOrDefault(t =>
                                string.Equals(t.Handle, context.TeammateHandle, StringComparison.OrdinalIgnoreCase));
                            foreach (var wakeTarget in ResolveBoardWakeTargets(liveCp, context.TeammateHandle, target, boundedContent))
                            {
                                if (WakeTeammateFromBoard(liveCp, coordinatorTaskId, wakeTarget, entry.Seq, boundedContent, senderTm?.RelayDepth))
                                {
                                    wokenHandles.Add(wakeTarget);
                                }
                            }
                        }
                        await SaveCheckpointAsync(liveCp, liveCp.CheckpointRevision, "mcp-board-live", cancellationToken).ConfigureAwait(false);
                        hub.SignalWake();
                    }
                }

                if (deliveredSeq > 0)
                {
                    await AppendEventAsync(context.RunId, "team.board_message",
                        $"Board message #{deliveredSeq} from @{context.TeammateHandle} to @{target} ({kind}).",
                        new
                        {
                            run_id = context.RunId,
                            seq = deliveredSeq,
                            sender = context.TeammateHandle,
                            target,
                            kind,
                            content = BoundText(boundedContent, 800),
                            wake,
                            woken_handles = wokenHandles,
                            source = "mcp_loopback"
                        }, cancellationToken).ConfigureAwait(false);

                    var livePayload = JsonSerializer.SerializeToElement(new
                    {
                        ok = true,
                        seq = deliveredSeq,
                        sender = context.TeammateHandle,
                        target,
                        kind,
                        wake,
                        woken_handles = wokenHandles
                    }, JsonOptions);
                    return new TeamBoardToolCallResult(true, livePayload);
                }
            }

            var queue = _liveMcpBoardPosts.GetOrAdd(context.RunId, _ => new ConcurrentQueue<LiveMcpBoardPost>());
            queue.Enqueue(new LiveMcpBoardPost(context.TeammateHandle, target, kind, boundedContent, wake, DateTimeOffset.UtcNow));
            await EnqueueAsync(context.RunId, cancellationToken).ConfigureAwait(false);

            var payload = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                sender = context.TeammateHandle,
                target,
                kind,
                wake
            }, JsonOptions);
            return new TeamBoardToolCallResult(true, payload);
        }

        if (TeamTaskBoardProtocol.IsTeamTasks(context.ToolName)
            || (CoreVirtualToolPolicy.IsTeamPlan(context.ToolName) && IsTaskBoardOperationElement(context.Arguments)))
        {
            var (opOk, opError, opPayload) = await ExecuteTaskBoardOperationCoreAsync(
                context.SessionId,
                context.RunId,
                context.TeammateHandle,
                context.Arguments,
                cancellationToken).ConfigureAwait(false);
            return opOk
                ? new TeamBoardToolCallResult(true, opPayload)
                : new TeamBoardToolCallResult(false, Error: opError);
        }

        if (CoreVirtualToolPolicy.IsTeamPlan(context.ToolName))
        {
            var args = context.Arguments;
            var (error, goal, operation, parsedSteps) = ParseTeamPlanArguments(args, context.TeammateHandle);
            if (error is not null)
            {
                return new TeamBoardToolCallResult(false, Error: error);
            }

            var updatedPlan = await ApplyTeamPlanToSessionPlanAsync(
                context.SessionId,
                context.RunId,
                goal,
                operation,
                parsedSteps,
                cancellationToken).ConfigureAwait(false);

            var summaryNote = $"[plan:{operation}] @{context.TeammateHandle} updated {parsedSteps.Count} step(s)"
                + (string.IsNullOrWhiteSpace(goal) ? "." : $" (goal: {BoundText(goal, 160)}).");
            var queue = _liveMcpBoardPosts.GetOrAdd(context.RunId, _ => new ConcurrentQueue<LiveMcpBoardPost>());
            queue.Enqueue(new LiveMcpBoardPost(context.TeammateHandle, "coordinator", "update", summaryNote, Wake: false, DateTimeOffset.UtcNow));
            await EnqueueAsync(context.RunId, cancellationToken).ConfigureAwait(false);

            var payload = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                operation,
                steps = parsedSteps.Count,
                revision = updatedPlan?.Revision
            }, JsonOptions);
            return new TeamBoardToolCallResult(true, payload);
        }

        return new TeamBoardToolCallResult(false, Error: $"Unsupported team board action '{context.ToolName}'.");
    }

    private static void DeliverBoardMessageToLiveMailboxes(
        FullDuplexCheckpointV1 checkpoint,
        RunTeammateActorHub hub,
        DurableTeamBoardMessage entry)
    {
        var recipients = new HashSet<string>(ResolveBoardWakeTargets(checkpoint, entry.Sender, entry.Target, entry.Content), StringComparer.OrdinalIgnoreCase);
        if (string.Equals(entry.Target, "coordinator", StringComparison.OrdinalIgnoreCase))
        {
            recipients.Add("coordinator");
        }
        foreach (var recipient in recipients)
        {
            if (string.Equals(recipient, entry.Sender, StringComparison.OrdinalIgnoreCase))
                continue;
            hub.GetMailbox(recipient).Enqueue(new LiveMailboxMessage(
                entry.Seq,
                entry.Sender,
                entry.Kind,
                entry.Content,
                Urgent: entry.Kind == "steer",
                entry.CreatedAt));
        }
    }

    private async Task<bool> DrainLiveMcpBoardPostsAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        _ = configuration;
        if (!_liveMcpBoardPosts.TryGetValue(checkpoint.RunId, out var queue) || queue.IsEmpty)
            return false;

        var runId = Guid.Parse(run.RunId);
        var hub = GetOrCreateActorHub(runId);
        var coordinatorTaskId = checkpoint.Tasks.FirstOrDefault(t => t.IsCoordinatorTask)?.TaskId;
        var drained = false;
        while (queue.TryDequeue(out var post))
        {
            var entry = AppendBoardMessage(checkpoint, post.Sender, post.Target, post.Kind, post.Content, post.Wake);
            DeliverBoardMessageToLiveMailboxes(checkpoint, hub, entry);
            drained = true;

            var wokenHandles = new List<string>();
            if (post.Wake)
            {
                var senderTm = checkpoint.Teammates.FirstOrDefault(t =>
                    string.Equals(t.Handle, post.Sender, StringComparison.OrdinalIgnoreCase));
                foreach (var targetHandle in ResolveBoardWakeTargets(checkpoint, post.Sender, post.Target, post.Content))
                {
                    if (WakeTeammateFromBoard(checkpoint, coordinatorTaskId, targetHandle, entry.Seq, post.Content, senderTm?.RelayDepth))
                    {
                        wokenHandles.Add(targetHandle);
                    }
                }
            }

            await AppendEventAsync(runId, "team.board_message",
                $"Board message #{entry.Seq} from @{entry.Sender} to @{entry.Target} ({entry.Kind}).",
                new
                {
                    run_id = runId,
                    seq = entry.Seq,
                    sender = entry.Sender,
                    target = entry.Target,
                    kind = entry.Kind,
                    content = BoundText(entry.Content, 800),
                    wake = entry.Wake,
                    woken_handles = wokenHandles,
                    source = "mcp_loopback"
                }, cancellationToken).ConfigureAwait(false);
        }

        return drained;
    }

    private async Task<FullDuplexCheckpointV1> HandleTeamSpawnAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode callerTask,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsTeamSpawn(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_spawn is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (!TryParseJsonObject(turn.ArgumentsJson, out var args))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_spawn requires a JSON object with 'agent_slug' and 'prompt'.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var agentSlug = (StringProperty(args, "agent_slug") ?? StringProperty(args, "agent"))?.Trim();
        var prompt = (StringProperty(args, "prompt") ?? StringProperty(args, "description"))?.Trim();
        if (string.IsNullOrWhiteSpace(agentSlug) || string.IsNullOrWhiteSpace(prompt))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_spawn requires non-empty 'agent_slug' and 'prompt'.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var runId = Guid.Parse(run.RunId);
        await using var checkpointGate = await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false);

        var callerSlug = callerTask.WorkerAgentSlug ?? configuration.Graph?.ConversationTemplateSlug ?? "meeting";
        var callerDepth = callerTask.DispatchedByTaskId is null ? 0 : 1;
        var allowedRoster = DispatchRosterForWorker(configuration, callerSlug, callerDepth);
        var matchedTarget = allowedRoster.FirstOrDefault(t => string.Equals(t.Id, agentSlug, StringComparison.OrdinalIgnoreCase));
        if (matchedTarget is null)
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                $"team_spawn cannot dispatch to '{agentSlug}'. {DescribeDispatchChoices(allowedRoster)}",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }
        agentSlug = matchedTarget.Id;

        var defaultMode = string.Equals(configuration.Graph?.Tier, FrozenGraphTiers.SoloDispatch, StringComparison.Ordinal)
            ? "ephemeral"
            : "teammate";
        var rawMode = StringProperty(args, "mode")?.ToLowerInvariant();
        var mode = rawMode is "teammate" or "ephemeral" ? rawMode : defaultMode;

        var rawHandle = StringProperty(args, "handle");
        var handle = string.IsNullOrWhiteSpace(rawHandle)
            ? NextTeammateHandle(checkpoint.Teammates, agentSlug)
            : SanitizeTeammateHandle(rawHandle);
        if (string.Equals(handle, "coordinator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(handle, "all", StringComparison.OrdinalIgnoreCase))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                $"Handle '{handle}' is reserved; choose a descriptive specialist handle such as 'backend-api' or 'ui-audit'.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var requestWorktree = args.TryGetProperty("worktree", out var wtEl) && wtEl.ValueKind == JsonValueKind.True;
        if (requestWorktree && configuration.Workspace is not { IsGitRepository: true })
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_spawn requested worktree=true, but the current workspace is not a Git repository.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }
        var writeScope = NormalizeWriteScope(ReadStringArray(args, "write_scope"));

        Guid? providerOverride = null;
        string? channelOverride = null;
        string? modelOverride = null;
        if (args.TryGetProperty("runtime_override", out var rtEl) && rtEl.ValueKind == JsonValueKind.Object)
        {
            if (StringProperty(rtEl, "provider_instance_id") is { } pidStr && Guid.TryParse(pidStr, out var pid))
                providerOverride = pid;
            channelOverride = StringProperty(rtEl, "channel");
            modelOverride = StringProperty(rtEl, "model_id");
        }

        var existingTeammate = checkpoint.Teammates.FirstOrDefault(t =>
            string.Equals(t.Handle, handle, StringComparison.OrdinalIgnoreCase));
        if (existingTeammate is not null)
        {
            if (string.Equals(existingTeammate.Status, "working", StringComparison.OrdinalIgnoreCase))
            {
                return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                    $"Teammate '@{existingTeammate.Handle}' is currently working. Call team_wait for it to settle or send context via team_message.",
                    checkpoint, cancellationToken).ConfigureAwait(false);
            }
            if (string.Equals(existingTeammate.Status, "waiting", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingTeammate.Mode, "teammate", StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingTeammate.AgentSlug, agentSlug, StringComparison.OrdinalIgnoreCase))
            {
                if (writeScope is { Count: > 0 }) existingTeammate.WriteScope = writeScope;
                if (providerOverride.HasValue) existingTeammate.ProviderInstanceId = providerOverride;
                if (!string.IsNullOrWhiteSpace(channelOverride)) existingTeammate.ChannelOverride = channelOverride;
                if (!string.IsNullOrWhiteSpace(modelOverride)) existingTeammate.ModelId = modelOverride;

                var wakeMsg = AppendBoardMessage(checkpoint, callerTask.TeammateHandle ?? callerTask.WorkerHandle ?? "coordinator",
                    existingTeammate.Handle, "handoff", prompt, wake: true);
                if (!WakeTeammateFromBoard(checkpoint, callerTask.TaskId, existingTeammate.Handle, wakeMsg.Seq, prompt))
                {
                    return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.ToolLoopDetected,
                        $"Teammate '@{existingTeammate.Handle}' reached MaxTeammateRelayDepth ({MaxTeammateRelayDepth}) and cannot be woken again.",
                        checkpoint, cancellationToken).ConfigureAwait(false);
                }

                turn.ResultJson = JsonSerializer.Serialize(new
                {
                    ok = true,
                    reused_existing = true,
                    handle = existingTeammate.Handle,
                    agent_slug = existingTeammate.AgentSlug,
                    mode = existingTeammate.Mode,
                    status = existingTeammate.Status,
                    task_id = existingTeammate.CurrentTaskId,
                    worktree_path = existingTeammate.WorktreePath,
                    branch_name = existingTeammate.BranchName
                });
                turn.DispatchStatus = ToolDispatchStatus.Completed;
                turn.ToolSuccess = true;
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-spawn-woken", cancellationToken).ConfigureAwait(false);
                GetOrCreateActorHub(runId).SignalWake();
                return checkpoint;
            }

            handle = NextTeammateHandle(checkpoint.Teammates, handle);
        }

        var taskId = Guid.NewGuid();
        var titleSummary = FirstSentenceOrBound(prompt, 120);
        var taskKeyBase = NormalizeTaskKey(handle, titleSummary);
        var taskKey = checkpoint.Tasks.Any(n => string.Equals(n.TaskKey, taskKeyBase, StringComparison.OrdinalIgnoreCase))
            ? $"{taskKeyBase}-{checkpoint.Tasks.Count + 1}"
            : taskKeyBase;

        var teammateState = new DurableTeammateState
        {
            Handle = handle,
            AgentSlug = agentSlug,
            Mode = mode,
            Status = "working",
            WriteScope = writeScope,
            ProviderInstanceId = providerOverride,
            ChannelOverride = channelOverride,
            ModelId = modelOverride,
            RelayDepth = 0,
            CurrentTaskId = taskId,
            LastDeliveredBoardSeq = checkpoint.NextTeamBoardSeq,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        checkpoint.Teammates.Add(teammateState);

        var spawnedNode = new DurableTaskNode
        {
            TaskId = taskId,
            TaskKey = taskKey,
            Title = $"@{handle}: {titleSummary}",
            Description = prompt,
            DispatchId = taskId,
            DispatchedByTaskId = callerTask.TaskId,
            RequestedAgent = agentSlug,
            TeammateHandle = handle,
            TeammateMode = mode,
            WorkerHandle = handle,
            Worktree = requestWorktree,
            WriteScope = writeScope,
            ProviderInstanceOverride = providerOverride,
            ChannelOverride = channelOverride,
            ModelIdOverride = modelOverride,
            SuccessCriteria = [],
            Dependencies = [],
            RequiredCapabilities = [],
            RequiredTools = [],
            Priority = 2,
            Risk = "medium",
            Status = "pending"
        };
        checkpoint.Tasks.Add(spawnedNode);

        var senderLabel = callerTask.TeammateHandle ?? callerTask.WorkerHandle ?? "coordinator";
        var briefBoardMsg = AppendBoardMessage(checkpoint, senderLabel, handle, "handoff", BoundText(prompt, 1200), wake: false);
        teammateState.LastWakeSeq = briefBoardMsg.Seq;
        teammateState.LastDeliveredBoardSeq = briefBoardMsg.Seq;

        await EnrolTeammateAsync(run, configuration, checkpoint, teammateState, callerTask.WorkerAgentId, cancellationToken).ConfigureAwait(false);

        turn.ResultJson = JsonSerializer.Serialize(new
        {
            ok = true,
            handle,
            agent_slug = agentSlug,
            mode,
            status = "working",
            task_id = taskId,
            dispatch_id = taskId,
            worktree = requestWorktree,
            write_scope = writeScope
        });
        turn.DispatchStatus = ToolDispatchStatus.Completed;
        turn.ToolSuccess = true;
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-spawned", cancellationToken).ConfigureAwait(false);
        GetOrCreateActorHub(runId).SignalWake();

        await AppendEventAsync(runId, "team.teammate_spawned",
            $"Spawned {mode} @{handle} ({agentSlug}).",
            new
            {
                run_id = runId,
                task_id = taskId,
                dispatched_by_task_id = callerTask.TaskId,
                handle,
                agent_slug = agentSlug,
                mode,
                worktree = requestWorktree,
                write_scope = writeScope,
                provider_instance_id = providerOverride,
                channel = channelOverride,
                model_id = modelOverride
            }, cancellationToken, taskId, idempotencyKey: $"team-spawn:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);

        return checkpoint;
    }

    private async Task<FullDuplexCheckpointV1> HandleTeamMessageAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode callerTask,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        _ = configuration;
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsTeamMessage(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_message is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (!TryParseJsonObject(turn.ArgumentsJson, out var args))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_message requires a JSON object with 'target' and 'content'.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var target = StringProperty(args, "target")?.TrimStart('@');
        var content = StringProperty(args, "content");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(content))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_message requires non-empty 'target' and 'content'.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var kind = StringProperty(args, "kind")?.ToLowerInvariant();
        if (kind is not ("update" or "review" or "handoff" or "question"))
            kind = "update";
        var wake = !args.TryGetProperty("wake", out var wakeEl) || wakeEl.ValueKind != JsonValueKind.False;

        var runId = Guid.Parse(run.RunId);
        var hub = GetOrCreateActorHub(runId);
        var sender = callerTask.IsCoordinatorTask
            ? "coordinator"
            : callerTask.TeammateHandle ?? callerTask.WorkerHandle ?? callerTask.TaskKey;
        var boundedContent = BoundText(content, 4000);
        DurableTeamBoardMessage entry;
        var wokenHandles = new List<string>();

        await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            entry = AppendBoardMessage(checkpoint, sender, target, kind, boundedContent, wake);
            DeliverBoardMessageToLiveMailboxes(checkpoint, hub, entry);

            if (wake)
            {
                var senderTm = !callerTask.IsCoordinatorTask
                    ? checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, sender, StringComparison.OrdinalIgnoreCase))
                    : null;
                foreach (var wakeTarget in ResolveBoardWakeTargets(checkpoint, sender, target, boundedContent))
                {
                    if (WakeTeammateFromBoard(checkpoint, callerTask.TaskId, wakeTarget, entry.Seq, boundedContent, senderTm?.RelayDepth))
                    {
                        wokenHandles.Add(wakeTarget);
                    }
                }
            }

            turn.ResultJson = JsonSerializer.Serialize(new
            {
                ok = true,
                seq = entry.Seq,
                sender,
                target,
                kind,
                woken_handles = wokenHandles
            });
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-message", cancellationToken).ConfigureAwait(false);
            hub.SignalWake();
        }

        await AppendEventAsync(runId, "team.board_message",
            $"Board message #{entry.Seq} from @{sender} to @{target} ({kind}).",
            new
            {
                run_id = runId,
                task_id = callerTask.TaskId,
                seq = entry.Seq,
                sender,
                target,
                kind,
                content = BoundText(boundedContent, 800),
                wake,
                woken_handles = wokenHandles
            }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-msg:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);

        return checkpoint;
    }

    private async Task<TaskWaitOutcome> HandleTeamWaitAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode callerTask,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsTeamWait(tool.ToolId)))
        {
            return new(await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_wait is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false), false);
        }

        var runId = Guid.Parse(run.RunId);
        await using var checkpointGate = await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false);

        if (await ApplyPendingTaskDispatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false))
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-dispatched", cancellationToken).ConfigureAwait(false);
        }

        JsonElement args = default;
        if (!string.IsNullOrWhiteSpace(turn.ArgumentsJson) && !TryParseJsonObject(turn.ArgumentsJson, out args))
        {
            return new(await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_wait requires a JSON object.", checkpoint, cancellationToken).ConfigureAwait(false), false);
        }

        var requestedHandles = args.ValueKind == JsonValueKind.Object
            ? ReadStringArray(args, "handles").Select(h => h.Trim().TrimStart('@')).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var timeoutSeconds = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("timeout_seconds", out var tsEl)
            && tsEl.TryGetInt32(out var parsedTs)
            ? Math.Clamp(parsedTs, 1, 600)
            : 120;
        var until = args.ValueKind == JsonValueKind.Object
            ? StringProperty(args, "until")?.ToLowerInvariant()
            : null;
        if (until is not ("settled" or "message" or "any"))
            until = "any";

        var unknownHandles = requestedHandles
            .Where(h => !checkpoint.Teammates.Any(t => string.Equals(t.Handle, h, StringComparison.OrdinalIgnoreCase))
                && !checkpoint.Tasks.Any(t => string.Equals(t.WorkerHandle, h, StringComparison.OrdinalIgnoreCase) || string.Equals(t.TaskKey, h, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (unknownHandles.Length > 0)
        {
            var known = checkpoint.Teammates.Select(t => t.Handle).ToArray();
            var hint = known.Length == 0
                ? "No teammates have been spawned in this run yet."
                : $"Spawned teammates: {string.Join(", ", known)}.";
            return new(await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                $"team_wait references unknown handle(s): {string.Join(", ", unknownHandles)}. {hint}",
                checkpoint, cancellationToken).ConfigureAwait(false), false);
        }

        var effectiveHandles = requestedHandles.Length > 0
            ? requestedHandles
            : checkpoint.Teammates.Select(t => t.Handle).ToArray();

        var isResumingWait = callerTask.TeamWaitDeadline is not null;
        if (!isResumingWait)
        {
            var anyTargetWorking = AnyTargetTeammateWorking(checkpoint, callerTask.TaskId, effectiveHandles);
            var hasOtherRunnableTasks = checkpoint.Tasks.Any(other =>
                other.TaskId != callerTask.TaskId && !IsTerminalTaskStatus(other.Status));
            var hasActiveExternalLeases = _runTeammateMcpLeases.Keys.Any(k => k.RunId == checkpoint.RunId);

            var shouldPark = until switch
            {
                "settled" => anyTargetWorking,
                "message" => anyTargetWorking || hasOtherRunnableTasks || hasActiveExternalLeases,
                _ => anyTargetWorking || hasOtherRunnableTasks // "any"
            };

            if (shouldPark)
            {
                callerTask.AwaitingTeammateHandles = effectiveHandles.ToList();
                callerTask.AwaitingBoardSeq = checkpoint.NextTeamBoardSeq;
                callerTask.TeamWaitUntil = until;
                callerTask.TeamWaitDeadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
                callerTask.Status = "waiting";
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-waiting", cancellationToken).ConfigureAwait(false);
                await AppendEventAsync(Guid.Parse(run.RunId), "team.wait_started",
                    $"'{callerTask.TaskKey}' is waiting on [{(effectiveHandles.Length == 0 ? "all" : string.Join(", ", effectiveHandles))}] (until={until}).",
                    new
                    {
                        run_id = run.RunId,
                        task_id = callerTask.TaskId,
                        handles = effectiveHandles,
                        until,
                        timeout_seconds = timeoutSeconds
                    }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-wait-started:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);
                return new(checkpoint, true);
            }
        }

        var sinceSeq = callerTask.AwaitingBoardSeq;
        var timedOut = isResumingWait
            && callerTask.TeamWaitDeadline is { } deadline
            && DateTimeOffset.UtcNow >= deadline
            && !IsTeamWaitConditionMetWithoutTimeout(callerTask, checkpoint);

        foreach (var node in checkpoint.Tasks.Where(n =>
                     n.TaskId != callerTask.TaskId
                     && IsTerminalTaskStatus(n.Status)
                     && !callerTask.ReportedTaskIds.Contains(n.TaskId)))
        {
            if (effectiveHandles.Length == 0
                || effectiveHandles.Contains(node.TeammateHandle ?? node.WorkerHandle ?? node.TaskKey, StringComparer.OrdinalIgnoreCase))
            {
                callerTask.ReportedTaskIds.Add(node.TaskId);
            }
        }

        callerTask.AwaitingTeammateHandles = null;
        callerTask.TeamWaitUntil = null;
        callerTask.TeamWaitDeadline = null;
        callerTask.AwaitingBoardSeq = 0;

        turn.ResultJson = BuildTeamWaitSnapshotJson(checkpoint, effectiveHandles, sinceSeq, timedOut);
        turn.DispatchStatus = ToolDispatchStatus.Completed;
        turn.ToolSuccess = true;
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-wait-resolved", cancellationToken).ConfigureAwait(false);

        await AppendEventAsync(Guid.Parse(run.RunId), "team.wait_resolved",
            $"team_wait resolved for '{callerTask.TaskKey}' (timed_out={timedOut.ToString().ToLowerInvariant()}).",
            new
            {
                run_id = run.RunId,
                task_id = callerTask.TaskId,
                handles = effectiveHandles,
                timed_out = timedOut,
                resumed = isResumingWait
            }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-wait-resolved:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);

        return new(checkpoint, false);
    }

    private async Task<FullDuplexCheckpointV1> HandleTeamPlanAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode callerTask,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsTeamPlan(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_plan is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (!TryParseJsonObject(turn.ArgumentsJson, out var args))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_plan requires a JSON object with 'steps'.", checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var callerHandle = callerTask.IsCoordinatorTask
            ? "coordinator"
            : callerTask.TeammateHandle ?? callerTask.WorkerHandle ?? callerTask.TaskKey;

        if (IsTaskBoardOperationElement(args))
        {
            return await HandleTeamTasksCoreTurnAsync(run, checkpoint, callerTask, turn, callerHandle, args, cancellationToken).ConfigureAwait(false);
        }

        var (error, goal, operation, parsedSteps) = ParseTeamPlanArguments(args, callerHandle);
        if (error is not null)
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                error, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var runId = Guid.Parse(run.RunId);
        var updatedSessionPlan = await ApplyTeamPlanToSessionPlanAsync(
            checkpoint.SessionId,
            runId,
            goal ?? checkpoint.UserGoal,
            operation,
            parsedSteps,
            cancellationToken).ConfigureAwait(false);

        await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            callerTask.Plan = MergeTaskPlanSteps(callerTask.Plan, operation, parsedSteps);
            var completedCount = callerTask.Plan.Count(s => s.Status == "completed");
            turn.ResultJson = JsonSerializer.Serialize(new
            {
                ok = true,
                operation,
                steps = callerTask.Plan.Count,
                completed = completedCount,
                session_plan_revision = updatedSessionPlan?.Revision
            });
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-plan-updated", cancellationToken).ConfigureAwait(false);
        }

        await AppendEventAsync(runId, "plan.updated",
            $"'{callerHandle}' updated the team plan ({callerTask.Plan.Count} step(s), operation={operation}).",
            new
            {
                run_id = run.RunId,
                task_id = callerTask.TaskId,
                task_key = callerTask.TaskKey,
                handle = callerHandle,
                operation,
                goal,
                steps = callerTask.Plan.Select(s => new { step = s.Step, status = s.Status }).ToArray()
            }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-plan:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);

        return checkpoint;
    }

    private async Task<FullDuplexCheckpointV1> HandleTeamTasksAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode callerTask,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => TeamTaskBoardProtocol.IsTeamTasks(tool.ToolId) || CoreVirtualToolPolicy.IsTeamPlan(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_tasks is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (!TryParseJsonObject(turn.ArgumentsJson, out var args))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_tasks requires a JSON object with 'action' ('list', 'claim', 'complete', or 'release').",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var callerHandle = callerTask.IsCoordinatorTask
            ? "coordinator"
            : callerTask.TeammateHandle ?? callerTask.WorkerHandle ?? callerTask.TaskKey;
        return await HandleTeamTasksCoreTurnAsync(run, checkpoint, callerTask, turn, callerHandle, args, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FullDuplexCheckpointV1> HandleTeamTasksCoreTurnAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode callerTask,
        WorkerToolTurn turn,
        string callerHandle,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var (ok, error, payload) = await ExecuteTaskBoardOperationCoreAsync(
            checkpoint.SessionId,
            runId,
            callerHandle,
            args,
            cancellationToken,
            checkpoint,
            callerTask).ConfigureAwait(false);

        if (!ok)
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                error ?? "Task board operation failed.", checkpoint, cancellationToken).ConfigureAwait(false);
        }

        await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            turn.ResultJson = payload?.GetRawText() ?? "{\"ok\":true}";
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-tasks", cancellationToken).ConfigureAwait(false);
        }
        return checkpoint;
    }

    private static bool IsTaskBoardOperationElement(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        var op = StringProperty(args, "action") ?? StringProperty(args, "operation");
        return op?.Trim().ToLowerInvariant() is "list" or "claim" or "complete" or "release";
    }

    private async Task<(bool Ok, string? Error, JsonElement? Payload)> ExecuteTaskBoardOperationCoreAsync(
        Guid sessionId,
        Guid runId,
        string callerHandle,
        JsonElement args,
        CancellationToken cancellationToken,
        FullDuplexCheckpointV1? liveCheckpoint = null,
        DurableTaskNode? callerTask = null)
    {
        if (_sessionPlan is null)
            return (false, "Session plan task board service is unavailable in this host.", null);

        var action = (StringProperty(args, "action") ?? StringProperty(args, "operation"))?.Trim().ToLowerInvariant();
        var stepId = StringProperty(args, "step_id") ?? StringProperty(args, "id");
        long? expectedRevision = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("expected_revision", out var revEl)
            && revEl.TryGetInt64(out var parsedRev)
            ? parsedRev
            : null;

        switch (action)
        {
            case "list":
            {
                var onlyReady = !args.TryGetProperty("only_ready", out var rEl) || rEl.ValueKind != JsonValueKind.False;
                var listed = await _sessionPlan.ListTasksAsync(sessionId, onlyReady, cancellationToken).ConfigureAwait(false);
                return (true, null, JsonSerializer.SerializeToElement(listed, JsonOptions));
            }
            case "claim":
            {
                var claimed = await _sessionPlan.ClaimTaskAsync(sessionId, stepId, callerHandle, expectedRevision, runId, cancellationToken).ConfigureAwait(false);
                if (claimed.Claimed && !string.IsNullOrWhiteSpace(claimed.StepId))
                {
                    await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                    {
                        var cp = liveCheckpoint ?? (_runActorHubs.TryGetValue(runId, out var h) ? h.LiveCheckpoint : null);
                        var tm = cp?.Teammates.FirstOrDefault(t => string.Equals(t.Handle, callerHandle, StringComparison.OrdinalIgnoreCase));
                        if (tm is not null)
                        {
                            tm.ClaimedStepId = claimed.StepId;
                            tm.UpdatedAt = DateTimeOffset.UtcNow;
                        }
                        if (cp is not null)
                        {
                            AppendBoardMessage(cp, callerHandle, "coordinator", "update",
                                $"[task-claimed] @{callerHandle} claimed step '{claimed.StepId}' ({claimed.Task?.Title ?? claimed.StepId}).",
                                wake: false);
                        }
                    }

                    await AppendEventAsync(runId, "team.task_claimed",
                        $"@{callerHandle} claimed task '{claimed.StepId}'.",
                        new
                        {
                            run_id = runId,
                            handle = callerHandle,
                            step_id = claimed.StepId,
                            title = claimed.Task?.Title,
                            revision = claimed.Revision,
                            autonomous = false
                        }, cancellationToken, callerTask?.TaskId).ConfigureAwait(false);
                }
                return (true, null, JsonSerializer.SerializeToElement(claimed, JsonOptions));
            }
            case "complete":
            {
                if (string.IsNullOrWhiteSpace(stepId))
                    return (false, "team_tasks action='complete' requires a non-empty 'step_id'.", null);
                var summary = StringProperty(args, "summary") ?? StringProperty(args, "evidence_summary");
                var evidence = ReadStringArray(args, "evidence");
                var completed = await _sessionPlan.CompleteTaskAsync(sessionId, stepId, callerHandle, summary, evidence, runId, cancellationToken).ConfigureAwait(false);
                if (completed.Completed)
                {
                    await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                    {
                        var cp = liveCheckpoint ?? (_runActorHubs.TryGetValue(runId, out var h) ? h.LiveCheckpoint : null);
                        if (cp is not null)
                        {
                            var tm = cp.Teammates.FirstOrDefault(t => string.Equals(t.Handle, callerHandle, StringComparison.OrdinalIgnoreCase));
                            if (tm is not null && string.Equals(tm.ClaimedStepId, completed.StepId, StringComparison.OrdinalIgnoreCase))
                            {
                                tm.ClaimedStepId = null;
                            }
                            await NotifyUnlockedTasksAndWakeIdleTeammatesAsync(runId, cp, callerHandle, completed, cancellationToken).ConfigureAwait(false);
                            await SaveCheckpointAsync(cp, cp.CheckpointRevision, "team-task-completed", cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                return (true, null, JsonSerializer.SerializeToElement(completed, JsonOptions));
            }
            case "release":
            {
                if (string.IsNullOrWhiteSpace(stepId))
                    return (false, "team_tasks action='release' requires a non-empty 'step_id'.", null);
                var reason = StringProperty(args, "reason") ?? StringProperty(args, "summary");
                var markBlocked = args.TryGetProperty("mark_blocked", out var mbEl) && mbEl.ValueKind == JsonValueKind.True;
                var released = await _sessionPlan.ReleaseTaskAsync(sessionId, stepId, callerHandle, reason, markBlocked, runId, cancellationToken).ConfigureAwait(false);
                if (released.Released)
                {
                    await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                    {
                        var cp = liveCheckpoint ?? (_runActorHubs.TryGetValue(runId, out var h) ? h.LiveCheckpoint : null);
                        if (cp is not null)
                        {
                            var tm = cp.Teammates.FirstOrDefault(t => string.Equals(t.Handle, callerHandle, StringComparison.OrdinalIgnoreCase));
                            if (tm is not null && string.Equals(tm.ClaimedStepId, released.StepId, StringComparison.OrdinalIgnoreCase))
                            {
                                tm.ClaimedStepId = null;
                            }
                            AppendBoardMessage(cp, callerHandle, "coordinator", "update",
                                $"[task-released] @{callerHandle} released step '{released.StepId}' ({released.Status}).",
                                wake: false);
                        }
                    }
                    await AppendEventAsync(runId, "team.task_released",
                        $"@{callerHandle} released task '{released.StepId}' ({released.Status}).",
                        new
                        {
                            run_id = runId,
                            handle = callerHandle,
                            step_id = released.StepId,
                            status = released.Status,
                            reason,
                            revision = released.Revision
                        }, cancellationToken, callerTask?.TaskId).ConfigureAwait(false);
                }
                return (true, null, JsonSerializer.SerializeToElement(released, JsonOptions));
            }
            default:
                return (false, $"Unsupported task board action '{action}'. Use list, claim, complete, or release.", null);
        }
    }

    private sealed record ParsedTeamPlanStep(
        string Id,
        string? Title,
        string? OwnerHandle,
        string? Status,
        string? Evidence,
        IReadOnlyList<string> DependsOn);

    private static (string? Error, string? Goal, string Operation, List<ParsedTeamPlanStep> Steps) ParseTeamPlanArguments(
        JsonElement args,
        string defaultOwner)
    {
        _ = defaultOwner;
        var steps = new List<ParsedTeamPlanStep>();
        var goal = StringProperty(args, "goal");
        var operation = StringProperty(args, "operation")?.ToLowerInvariant();
        if (operation is not ("replace" or "update_step"))
            operation = "replace";

        if (!args.TryGetProperty("steps", out var array) || array.ValueKind != JsonValueKind.Array)
            return ("team_plan requires a 'steps' array.", goal, operation, steps);

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
                return ($"Step {index} in team_plan must be an object.", goal, operation, steps);
            var id = StringProperty(item, "id") ?? $"step-{index}";
            var title = StringProperty(item, "title") ?? StringProperty(item, "step");
            if (operation == "replace" && string.IsNullOrWhiteSpace(title))
                return ($"Step '{id}' requires a non-empty 'title' when operation='replace'.", goal, operation, steps);

            var status = StringProperty(item, "status")?.ToLowerInvariant();
            if (status is not null && status is not ("pending" or "in_progress" or "completed" or "blocked" or "skipped"))
                return ($"Step '{id}' has invalid status '{status}'; use pending, in_progress, completed, blocked, or skipped.", goal, operation, steps);

            var explicitOwner = StringProperty(item, "owner_handle") ?? StringProperty(item, "owner");
            string? normalizedOwner = null;
            if (!string.IsNullOrWhiteSpace(explicitOwner))
            {
                var cleaned = explicitOwner.Trim().TrimStart('@');
                if (!string.Equals(cleaned, "unassigned", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(cleaned, "any", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedOwner = BoundText(cleaned, 80);
                }
            }

            var dependsOn = ReadStringArray(item, "depends_on");
            var evidence = StringProperty(item, "evidence");
            steps.Add(new ParsedTeamPlanStep(
                BoundText(id, 80),
                title is null ? null : BoundText(title, 300),
                normalizedOwner,
                status ?? (operation == "replace" ? "pending" : null),
                evidence is null ? null : BoundText(evidence, 600),
                dependsOn));
        }

        if (steps.Count == 0)
            return ("team_plan 'steps' array cannot be empty.", goal, operation, steps);
        if (steps.Count > CoreVirtualToolPolicy.PlanUpdateMaxSteps)
            return ($"team_plan exceeds the {CoreVirtualToolPolicy.PlanUpdateMaxSteps}-step limit.", goal, operation, steps);

        return (null, goal, operation, steps);
    }

    private static List<TaskPlanStep> MergeTaskPlanSteps(
        List<TaskPlanStep>? existing,
        string operation,
        IReadOnlyList<ParsedTeamPlanStep> incoming)
    {
        static string NormalizeTaskStepStatus(string? s) => s switch
        {
            "completed" or "skipped" => "completed",
            "in_progress" => "in_progress",
            _ => "pending"
        };

        if (operation == "replace" || existing is not { Count: > 0 })
        {
            return incoming.Select(s =>
            {
                var label = string.IsNullOrWhiteSpace(s.OwnerHandle)
                    ? s.Title ?? s.Id
                    : $"[{s.OwnerHandle}] {s.Title ?? s.Id}";
                return new TaskPlanStep(BoundText(label, 300), NormalizeTaskStepStatus(s.Status));
            }).ToList();
        }

        var result = existing.ToList();
        foreach (var patch in incoming)
        {
            var nextStatus = NormalizeTaskStepStatus(patch.Status);
            var matchIdx = result.FindIndex(e =>
                (!string.IsNullOrWhiteSpace(patch.Title) && e.Step.Contains(patch.Title, StringComparison.OrdinalIgnoreCase))
                || e.Step.Contains(patch.Id, StringComparison.OrdinalIgnoreCase));
            if (matchIdx >= 0)
            {
                result[matchIdx] = result[matchIdx] with { Status = nextStatus };
            }
            else if (!string.IsNullOrWhiteSpace(patch.Title))
            {
                var label = string.IsNullOrWhiteSpace(patch.OwnerHandle)
                    ? patch.Title
                    : $"[{patch.OwnerHandle}] {patch.Title}";
                result.Add(new TaskPlanStep(BoundText(label, 300), nextStatus));
            }
        }
        return result;
    }

    private async Task<SessionPlanDto?> ApplyTeamPlanToSessionPlanAsync(
        Guid sessionId,
        Guid runId,
        string? goal,
        string operation,
        IReadOnlyList<ParsedTeamPlanStep> steps,
        CancellationToken cancellationToken)
    {
        if (_sessionPlan is null) return null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (operation == "replace")
            {
                var phaseSteps = steps.Select((s, idx) => new SessionPlanStepDto
                {
                    Id = s.Id,
                    Title = s.Title ?? s.Id,
                    Status = s.Status ?? SessionPlanStatuses.StepPending,
                    DependsOn = s.DependsOn,
                    AssignedAgentSlug = s.OwnerHandle,
                    Summary = s.Evidence,
                    Artifacts = string.IsNullOrWhiteSpace(s.Evidence)
                        ? []
                        : [new SessionPlanArtifactDto { Kind = "evidence", Label = BoundText(s.Evidence, 160) }],
                    UpdatedAt = now
                }).ToArray();

                return await _sessionPlan.PutAsync(
                    sessionId,
                    new SessionPlanPutRequestDto
                    {
                        Title = string.IsNullOrWhiteSpace(goal) ? "Team Plan" : BoundText(goal, 120),
                        Goal = goal,
                        Status = SessionPlanStatuses.Active,
                        Phases =
                        [
                            new SessionPlanPhaseDto
                            {
                                Id = "phase-team",
                                Title = "Team Execution",
                                Order = 1,
                                Steps = phaseSteps
                            }
                        ]
                    },
                    runId: runId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await _sessionPlan.PatchAsync(
                sessionId,
                new SessionPlanPatchRequestDto
                {
                    Goal = goal,
                    StepUpdates = steps.Select(s => new SessionPlanStepPatchDto
                    {
                        Id = s.Id,
                        Title = s.Title,
                        Status = s.Status,
                        DependsOn = s.DependsOn.Count > 0 ? s.DependsOn : null,
                        AssignedAgentSlug = s.OwnerHandle,
                        Summary = s.Evidence,
                        Artifacts = string.IsNullOrWhiteSpace(s.Evidence)
                            ? null
                            : [new SessionPlanArtifactDto { Kind = "evidence", Label = BoundText(s.Evidence, 160) }]
                    }).ToArray()
                },
                runId: runId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "SessionPlan sync skipped for team_plan in session {SessionId}.", sessionId);
            return null;
        }
    }

    internal static DurableTeamBoardMessage AppendBoardMessage(
        FullDuplexCheckpointV1 checkpoint,
        string sender,
        string target,
        string kind,
        string content,
        bool wake)
    {
        var seq = ++checkpoint.NextTeamBoardSeq;
        var entry = new DurableTeamBoardMessage
        {
            Seq = seq,
            Sender = string.IsNullOrWhiteSpace(sender) ? "coordinator" : sender.Trim().TrimStart('@'),
            Target = string.IsNullOrWhiteSpace(target) ? "all" : target.Trim().TrimStart('@'),
            Kind = string.IsNullOrWhiteSpace(kind) ? "update" : kind.Trim().ToLowerInvariant(),
            Content = content,
            Wake = wake,
            CreatedAt = DateTimeOffset.UtcNow
        };
        checkpoint.TeamBoard.Add(entry);
        if (checkpoint.TeamBoard.Count > MaxTeamBoardHistory)
        {
            checkpoint.TeamBoard.RemoveRange(0, checkpoint.TeamBoard.Count - MaxTeamBoardHistory);
        }
        return entry;
    }

    private static IReadOnlyList<string> ResolveBoardWakeTargets(
        FullDuplexCheckpointV1 checkpoint,
        string sender,
        string target,
        string content)
    {
        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var tm in checkpoint.Teammates)
            {
                if (!string.Equals(tm.Handle, sender, StringComparison.OrdinalIgnoreCase))
                    handles.Add(tm.Handle);
            }
        }
        else if (!string.Equals(target, "coordinator", StringComparison.OrdinalIgnoreCase))
        {
            handles.Add(target.TrimStart('@'));
        }

        foreach (Match m in MentionHandleRegex.Matches(content))
        {
            var mentioned = m.Groups[1].Value;
            if (!string.Equals(mentioned, "coordinator", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mentioned, "all", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mentioned, sender, StringComparison.OrdinalIgnoreCase))
            {
                handles.Add(mentioned);
            }
        }

        return handles.ToArray();
    }

    internal static string FormatBoardContextForAgent(
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        bool isNonMcpHarness)
    {
        if (checkpoint.Teammates.Count == 0 && checkpoint.TeamBoard.Count == 0 && !isNonMcpHarness)
            return string.Empty;

        var selfHandle = task.IsCoordinatorTask
            ? "coordinator"
            : task.TeammateHandle ?? task.WorkerHandle ?? task.TaskKey;
        var sb = new StringBuilder();
        sb.AppendLine("\n\n## Shared Run Board & Teammates");

        if (checkpoint.Teammates.Count > 0)
        {
            sb.AppendLine("Active/settled teammates in this run:");
            foreach (var tm in checkpoint.Teammates)
            {
                var badge = string.IsNullOrWhiteSpace(tm.RuntimeBadge) ? tm.AgentSlug : $"{tm.AgentSlug} [{tm.RuntimeBadge}]";
                var wt = string.IsNullOrWhiteSpace(tm.WorktreePath) ? string.Empty : $" | worktree: {tm.WorktreePath}";
                var scope = tm.WriteScope is { Count: > 0 } ? $" | write_scope: {string.Join(", ", tm.WriteScope)}" : string.Empty;
                sb.AppendLine($"- @{tm.Handle} ({badge}) — status={tm.Status}, mode={tm.Mode}{wt}{scope}");
            }
        }

        var relevantMessages = checkpoint.TeamBoard
            .Where(m => task.IsCoordinatorTask
                || string.Equals(m.Target, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Target, selfHandle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Sender, selfHandle, StringComparison.OrdinalIgnoreCase)
                || m.Content.Contains($"@{selfHandle}", StringComparison.OrdinalIgnoreCase))
            .TakeLast(MaxBoardMessagesInPrompt)
            .ToArray();

        if (relevantMessages.Length > 0)
        {
            sb.AppendLine("Recent shared board messages:");
            foreach (var msg in relevantMessages)
            {
                sb.AppendLine($"- [#{msg.Seq} @{msg.Sender} -> @{msg.Target} ({msg.Kind})]: {BoundText(msg.Content, 500)}");
            }
        }

        if (isNonMcpHarness && !task.IsCoordinatorTask)
        {
            sb.AppendLine("Board bridge note (CLI/TUI channel): to post a message on the shared board when finishing this turn, include a line `BOARD_MESSAGE: @coordinator | <concise update>`.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildTeamWaitSnapshotJson(
        FullDuplexCheckpointV1 checkpoint,
        IReadOnlyList<string> targetHandles,
        long sinceBoardSeq,
        bool timedOut)
    {
        var filterSet = targetHandles.Count > 0
            ? new HashSet<string>(targetHandles, StringComparer.OrdinalIgnoreCase)
            : null;
        var teammates = checkpoint.Teammates
            .Where(t => filterSet is null || filterSet.Contains(t.Handle))
            .Select(t => new
            {
                handle = t.Handle,
                agent_slug = t.AgentSlug,
                mode = t.Mode,
                status = t.Status,
                runtime_badge = t.RuntimeBadge,
                worktree_path = t.WorktreePath,
                branch_name = t.BranchName,
                write_scope = t.WriteScope,
                relay_depth = t.RelayDepth,
                last_outcome = t.LastOutcomeSummary,
                updated_at = t.UpdatedAt
            })
            .ToArray();

        var recentMessages = checkpoint.TeamBoard
            .Where(m => sinceBoardSeq > 0 ? m.Seq > sinceBoardSeq : true)
            .TakeLast(24)
            .Select(m => new
            {
                seq = m.Seq,
                sender = m.Sender,
                target = m.Target,
                kind = m.Kind,
                content = BoundText(m.Content, 1000),
                created_at = m.CreatedAt
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            timed_out = timedOut,
            latest_board_seq = checkpoint.NextTeamBoardSeq,
            teammates,
            messages = recentMessages
        });
    }

    private static bool AnyTargetTeammateWorking(
        FullDuplexCheckpointV1 checkpoint,
        Guid waiterTaskId,
        IReadOnlyList<string> handles)
    {
        var filter = handles.Count > 0 ? new HashSet<string>(handles, StringComparer.OrdinalIgnoreCase) : null;
        foreach (var tm in checkpoint.Teammates)
        {
            if (filter is not null && !filter.Contains(tm.Handle)) continue;
            if (tm.Status is "working" or "awaiting_approval")
                return true;
        }
        foreach (var node in checkpoint.Tasks)
        {
            if (node.TaskId == waiterTaskId || IsTerminalTaskStatus(node.Status)) continue;
            var nodeHandle = node.TeammateHandle ?? node.WorkerHandle ?? node.TaskKey;
            if (filter is null || filter.Contains(nodeHandle))
                return true;
        }
        return false;
    }

    private static bool IsTeamWaitConditionMetWithoutTimeout(DurableTaskNode waiter, FullDuplexCheckpointV1 checkpoint)
    {
        var handles = waiter.AwaitingTeammateHandles ?? [];
        var filter = handles.Count > 0 ? new HashSet<string>(handles, StringComparer.OrdinalIgnoreCase) : null;
        var until = waiter.TeamWaitUntil ?? "any";
        var allSettled = !AnyTargetTeammateWorking(checkpoint, waiter.TaskId, handles);
        var anyTargetNewlySettled = checkpoint.Tasks.Any(n =>
            n.TaskId != waiter.TaskId
            && IsTerminalTaskStatus(n.Status)
            && !waiter.ReportedTaskIds.Contains(n.TaskId)
            && (filter is null || filter.Contains(n.TeammateHandle ?? n.WorkerHandle ?? n.TaskKey)));
        var hasNewMessage = checkpoint.TeamBoard.Any(m =>
            m.Seq > waiter.AwaitingBoardSeq
            && !string.Equals(m.Sender, waiter.IsCoordinatorTask ? "coordinator" : waiter.TeammateHandle ?? waiter.WorkerHandle, StringComparison.OrdinalIgnoreCase));

        return until switch
        {
            "settled" => allSettled,
            "message" => hasNewMessage || allSettled,
            _ => allSettled || anyTargetNewlySettled
        };
    }

    private static string SanitizeTeammateHandle(string raw)
    {
        var buffer = new List<char>(raw.Length);
        foreach (var ch in raw.Trim().TrimStart('@').ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_') buffer.Add(ch);
            else if (buffer.Count > 0 && buffer[^1] != '-') buffer.Add('-');
        }
        while (buffer.Count > 0 && buffer[^1] == '-') buffer.RemoveAt(buffer.Count - 1);
        var cleaned = buffer.Count == 0 ? "teammate" : new string(buffer.ToArray());
        return cleaned.Length <= 36 ? cleaned : cleaned[..36];
    }

    private static string NextTeammateHandle(IReadOnlyList<DurableTeammateState> existing, string prefix)
    {
        var cleanPrefix = SanitizeTeammateHandle(prefix.Replace('.', '-'));
        var n = 1;
        while (existing.Any(t => string.Equals(t.Handle, $"{cleanPrefix}-{n}", StringComparison.OrdinalIgnoreCase)))
        {
            n++;
        }
        return $"{cleanPrefix}-{n}";
    }

    private static string FirstSentenceOrBound(string prompt, int maxChars)
    {
        var firstLine = prompt.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? prompt;
        return BoundText(firstLine, maxChars);
    }

    // Legacy task_dispatch / task_wait / plan_update / plan_read / update_correction_trace

    private async Task<bool> ApplyPendingTaskDispatchesAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var pending = await _lifecycle.ListPendingRunDirectivesAsync(checkpoint.RunId, cancellationToken).ConfigureAwait(false);
        var dispatches = pending
            .Where(item => string.Equals(item.Kind, "task_dispatch", StringComparison.Ordinal))
            .ToList();
        if (dispatches.Count == 0) return false;

        var runId = Guid.Parse(run.RunId);
        var added = 0;
        foreach (var directive in dispatches)
        {
            string? agent = null;
            string? followUpOf = null;
            Guid? dispatchId = null;
            Guid? dispatchedBy = null;
            string? title = null;
            string? description = null;
            var worktree = false;
            string[] criteria = [];
            string[] tools = [];
            string[] capabilities = [];
            string[] writeScope = [];
            try
            {
                using var document = JsonDocument.Parse(directive.PayloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("agent", out var agentElement) && agentElement.ValueKind == JsonValueKind.String)
                        agent = agentElement.GetString()?.Trim();
                    if (root.TryGetProperty("follow_up_of", out var followElement) && followElement.ValueKind == JsonValueKind.String)
                        followUpOf = followElement.GetString()?.Trim();
                    if (root.TryGetProperty("dispatch_id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        && Guid.TryParse(idElement.GetString(), out var parsedId))
                        dispatchId = parsedId;
                    if (root.TryGetProperty("task_id", out var byElement) && byElement.ValueKind == JsonValueKind.String
                        && Guid.TryParse(byElement.GetString(), out var parsedBy))
                        dispatchedBy = parsedBy;
                    if (root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                        title = titleElement.GetString()?.Trim();
                    if (root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
                        description = descriptionElement.GetString()?.Trim();
                    if (root.TryGetProperty("worktree", out var wtElement) && wtElement.ValueKind == JsonValueKind.True)
                        worktree = true;
                    criteria = ReadStringArray(root, "success_criteria");
                    tools = ReadStringArray(root, "required_tools");
                    capabilities = ReadStringArray(root, "required_capabilities");
                    writeScope = ReadStringArray(root, "write_scope");
                }
            }
            catch (JsonException)
            {
            }

            var status = "consumed";
            if (string.IsNullOrWhiteSpace(title))
            {
                status = "rejected";
                await AppendEventAsync(runId, "task.dispatch_rejected",
                    "A queued sub-task carried no title and was rejected.",
                    new { directive_id = directive.Id, code = "dispatch_payload_unreadable" }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var key = NormalizeTaskKey(null, title);
                if (checkpoint.Tasks.Any(item => string.Equals(item.TaskKey, key, StringComparison.OrdinalIgnoreCase)))
                {
                    key = $"{key}-{checkpoint.Tasks.Count(node => node.TaskKey.StartsWith(key, StringComparison.OrdinalIgnoreCase)) + 1}";
                }
                checkpoint.DirectiveCursor++;
                var prior = string.IsNullOrWhiteSpace(followUpOf) ? null : FindDispatchedTask(checkpoint.Tasks, dispatchedBy, followUpOf);
                checkpoint.Tasks.Add(new DurableTaskNode
                {
                    TaskId = dispatchId is { } id && checkpoint.Tasks.All(item => item.TaskId != id) ? id : Guid.NewGuid(),
                    TaskKey = key,
                    Title = title,
                    Description = prior is null ? description : FollowUpBrief(prior, description),
                    DispatchId = dispatchId,
                    DispatchedByTaskId = dispatchedBy,
                    WriteScope = NormalizeWriteScope(writeScope),
                    Worktree = worktree,
                    TaskWorktreePath = prior?.TaskWorktreePath,
                    TaskWorktreeBranch = prior is null ? null : ResolveOwnWorktreeBranch(prior),
                    SuccessCriteria = criteria.Length > 0 ? [.. criteria] : ["The sub-task's stated goal is met."],
                    Dependencies = [],
                    RequiredCapabilities = [.. capabilities],
                    RequiredTools = [.. tools],
                    RequestedAgent = string.IsNullOrWhiteSpace(agent) ? null : agent,
                    Priority = 2,
                    Risk = "medium",
                    Status = "pending"
                });
                added++;
                await AppendEventAsync(runId, "task.dispatched",
                    $"Sub-task '{title}' was accepted into the run's task graph.",
                    new
                    {
                        directive_id = directive.Id,
                        dispatch_id = dispatchId,
                        dispatched_by = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == dispatchedBy)?.WorkerHandle,
                        task_key = key,
                        title,
                        agent,
                        follow_up_of = prior?.WorkerHandle ?? prior?.TaskKey,
                        worktree,
                        required_tools = tools,
                        required_capabilities = capabilities
                    }, cancellationToken).ConfigureAwait(false);
            }
            await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], status, cancellationToken).ConfigureAwait(false);
        }
        return added > 0;
    }

    private async Task<TaskWaitOutcome> HandleTaskWaitAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsTaskWait(tool.ToolId)))
        {
            return new(await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                "task_wait is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false), false);
        }
        var runId = Guid.Parse(run.RunId);
        await using var checkpointGate = await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false);

        if (await ApplyPendingTaskDispatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false))
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-dispatched", cancellationToken).ConfigureAwait(false);
        }

        var requested = TryParseJsonObject(turn.ArgumentsJson, out var parameters)
            ? ReadStringArray(parameters, "dispatch_ids")
            : [];
        var targets = ResolveWaitTargets(checkpoint.Tasks, task, requested, out var unknown);
        if (unknown.Count > 0)
        {
            var own = checkpoint.Tasks.Where(item => item.DispatchedByTaskId == task.TaskId)
                .Select(item => $"{item.DispatchId} ({item.WorkerHandle ?? item.RequestedAgent ?? item.TaskKey}: {item.Title})")
                .ToArray();
            var choices = own.Length == 0 ? "You have not dispatched any sub-task in this task." : "Your sub-tasks: " + string.Join("; ", own) + ".";
            return new(await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                $"task_wait does not know {string.Join(", ", unknown.Select(id => $"'{id}'"))}. {choices}",
                checkpoint, cancellationToken).ConfigureAwait(false), false);
        }

        if (targets.All(item => IsTerminalTaskStatus(item.Status)))
        {
            turn.ResultJson = BuildTaskWaitResult(targets);
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            foreach (var item in targets.Where(item => !task.ReportedTaskIds.Contains(item.TaskId)))
                task.ReportedTaskIds.Add(item.TaskId);
            var resumed = task.AwaitingTaskIds.Count > 0;
            task.AwaitingTaskIds = [];
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-wait-resolved", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(Guid.Parse(run.RunId), "task.wait_resolved",
                $"{targets.Count} sub-task result(s) returned to '{task.TaskKey}'.",
                new
                {
                    run_id = run.RunId,
                    task_id = task.TaskId,
                    task_key = task.TaskKey,
                    handle = task.WorkerHandle,
                    call_id = turn.CallId,
                    resumed,
                    awaited = targets.Select(item => new { task_key = item.TaskKey, handle = item.WorkerHandle, status = item.ResultStatus ?? item.Status }).ToArray()
                }, cancellationToken, task.TaskId, idempotencyKey: $"task-wait-resolved:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
            return new(checkpoint, false);
        }

        task.AwaitingTaskIds = targets.Select(item => item.TaskId).ToList();
        task.Status = "waiting";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-waiting", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "task.wait_started",
            $"'{task.TaskKey}' is waiting for {targets.Count} sub-task(s).",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                handle = task.WorkerHandle,
                call_id = turn.CallId,
                awaited = targets.Select(item => new { task_key = item.TaskKey, handle = item.WorkerHandle, title = item.Title, agent = item.RequestedAgent, status = item.Status }).ToArray()
            }, cancellationToken, task.TaskId, idempotencyKey: $"task-wait-started:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
        return new(checkpoint, true);
    }

    private async Task<FullDuplexCheckpointV1> HandlePlanUpdateAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsPlanUpdate(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                "plan_update is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }
        var error = TryParsePlan(turn.ArgumentsJson, out var steps, out var explanation);
        if (error is not null)
        {
            return await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                error, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        task.Plan = steps;
        var sessionPlan = await TrySyncTaskPlanToSessionPlanAsync(
            checkpoint.SessionId,
            checkpoint.RunId,
            task,
            steps,
            explanation,
            cancellationToken).ConfigureAwait(false);

        turn.ResultJson = JsonSerializer.Serialize(new
        {
            ok = true,
            steps = steps.Count,
            completed = steps.Count(step => step.Status == "completed"),
            session_plan_revision = sessionPlan?.Revision
        });
        turn.DispatchStatus = ToolDispatchStatus.Completed;
        turn.ToolSuccess = true;
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "plan-updated", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "plan.updated",
            $"'{task.TaskKey}' updated its plan ({steps.Count} step(s)).",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                handle = task.WorkerHandle,
                explanation,
                steps = steps.Select(step => new { step = step.Step, status = step.Status }).ToArray()
            }, cancellationToken, task.TaskId, idempotencyKey: $"plan-updated:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
        return checkpoint;
    }

    private async Task<FullDuplexCheckpointV1> HandlePlanReadAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsPlanRead(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                "plan_read is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var sessionPlan = await TryGetSessionPlanAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        turn.ResultJson = JsonSerializer.Serialize(new
        {
            ok = true,
            task_plan = task.Plan ?? [],
            session_plan = sessionPlan
        }, JsonOptions);
        turn.DispatchStatus = ToolDispatchStatus.Completed;
        turn.ToolSuccess = true;
        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "plan-read", cancellationToken).ConfigureAwait(false);
    }

    private async Task<(FullDuplexCheckpointV1 Checkpoint, bool Parked)> HandleCorrectionTraceAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        WorkerToolTurn turn,
        CancellationToken cancellationToken)
    {
        var task = checkpoint.Tasks.FirstOrDefault(item => item.ToolTurns.Contains(turn));
        if (task is null) return (checkpoint, false);
        if (checkpoint.Correction is null)
            return (await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                "This run has no correction trace.", checkpoint, cancellationToken).ConfigureAwait(false), false);
        if (!TryParseJsonObject(turn.ArgumentsJson, out var arguments))
        {
            return (await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                "update_correction_trace needs a JSON object.", checkpoint, cancellationToken).ConfigureAwait(false), false);
        }

        var error = CorrectionTracePolicy.ApplyProposal(checkpoint.Correction, arguments, configuration.RuntimePolicy);
        if (error is not null)
        {
            return (await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                error, checkpoint, cancellationToken).ConfigureAwait(false), false);
        }

        turn.ResultJson = JsonSerializer.Serialize(new
        {
            ok = true,
            episode_id = checkpoint.Correction.EpisodeId,
            revision = checkpoint.Correction.Revision,
            phase = checkpoint.Correction.Phase,
            trace = checkpoint.Correction.ToDto(),
        });
        turn.DispatchStatus = ToolDispatchStatus.Completed;
        turn.ToolSuccess = true;
        var pendingQuestion = checkpoint.Correction.OpenQuestions.LastOrDefault(question => question.Blocking && question.AnsweredAt is null);
        if (pendingQuestion is not null)
        {
            checkpoint.Phase = "awaiting_user";
            await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Correction requires a user answer before it can continue.", cancellationToken).ConfigureAwait(false);
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "correction-trace", cancellationToken).ConfigureAwait(false);
        await AppendCorrectionTraceEventAsync(run, checkpoint.Correction, cancellationToken).ConfigureAwait(false);
        if (pendingQuestion is not null)
        {
            await AppendEventAsync(Guid.Parse(run.RunId), "correction.question.requested",
                "Correction requested a user answer.",
                new { run_id = run.RunId, episode_id = checkpoint.Correction.EpisodeId, question = pendingQuestion.ToDto(), trace_revision = checkpoint.Correction.Revision },
                cancellationToken, idempotencyKey: $"correction-question:{checkpoint.Correction.EpisodeId}:{pendingQuestion.Id}").ConfigureAwait(false);
            return (checkpoint, true);
        }
        return (checkpoint, false);
    }

    private async Task AppendCorrectionTraceEventAsync(
        RunState run,
        CorrectionTraceState trace,
        CancellationToken cancellationToken)
    {
        await AppendEventAsync(Guid.Parse(run.RunId), "correction.trace.updated",
            "Correction trace updated.",
            new { run_id = run.RunId, episode_id = trace.EpisodeId, revision = trace.Revision, trace = trace.ToDto() },
            cancellationToken, idempotencyKey: $"correction-trace:{run.RunId}:{trace.EpisodeId}:{trace.Revision}").ConfigureAwait(false);
    }

    internal static string? TryParsePlan(string? argumentsJson, out List<TaskPlanStep> steps, out string? explanation)
    {
        steps = [];
        explanation = null;
        const string shape = "Send {\"steps\": [{\"step\": \"...\", \"status\": \"pending\" | \"in_progress\" | \"completed\"}]}.";
        if (string.IsNullOrWhiteSpace(argumentsJson)
            || !TryParseJsonObject(argumentsJson, out var parameters)
            || !parameters.TryGetProperty("steps", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return "plan_update needs a steps array. " + shape;
        }
        if (parameters.TryGetProperty("explanation", out var why) && why.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(why.GetString()))
        {
            explanation = BoundText(why.GetString()!.Trim(), 500);
        }
        foreach (var item in array.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("step", out var stepValue)
                && stepValue.ValueKind == JsonValueKind.String ? stepValue.GetString()?.Trim() : null;
            var status = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("status", out var statusValue)
                && statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString()?.Trim().ToLowerInvariant() : null;
            if (string.IsNullOrEmpty(text))
                return $"Step {steps.Count + 1} has no step text. " + shape;
            if (status is not ("pending" or "in_progress" or "completed"))
                return $"Step {steps.Count + 1} ('{BoundText(text, 80)}') has status '{status}'; use pending, in_progress or completed.";
            steps.Add(new TaskPlanStep(BoundText(text, 300), status));
        }
        if (steps.Count == 0)
            return "The plan is empty. " + shape;
        if (steps.Count > CoreVirtualToolPolicy.PlanUpdateMaxSteps)
            return $"The plan has {steps.Count} steps; keep it to {CoreVirtualToolPolicy.PlanUpdateMaxSteps} or fewer by merging steps.";
        if (steps.Count(step => step.Status == "in_progress") > 1)
            return "More than one step is in_progress; keep exactly the step you are working on in_progress.";
        return null;
    }

    internal static bool IsTerminalTaskStatus(string status) => status is "completed" or "failed" or "blocked";

    internal static bool IsDispatchable(DurableTaskNode task, IReadOnlyList<DurableTaskNode> tasks) =>
        task.Status is "pending" or "ready" || IsWaitSatisfied(task, tasks);

    internal static bool IsDispatchable(DurableTaskNode task, FullDuplexCheckpointV1 checkpoint) =>
        task.Status is "pending" or "ready" || IsWaitSatisfied(task, checkpoint);

    internal static bool IsWaitSatisfied(DurableTaskNode task, IReadOnlyList<DurableTaskNode> tasks) =>
        task.Status == "waiting"
        && task.TeamWaitDeadline is null
        && task.AwaitingTaskIds.All(id => tasks.FirstOrDefault(other => other.TaskId == id) is not { } awaited
            || IsTerminalTaskStatus(awaited.Status));

    internal static bool IsWaitSatisfied(DurableTaskNode task, FullDuplexCheckpointV1 checkpoint)
    {
        if (task.Status != "waiting") return false;
        if (task.TeamWaitDeadline is { } deadline)
        {
            if (DateTimeOffset.UtcNow >= deadline) return true;
            return IsTeamWaitConditionMetWithoutTimeout(task, checkpoint);
        }
        return task.AwaitingTaskIds.All(id => checkpoint.Tasks.FirstOrDefault(other => other.TaskId == id) is not { } awaited
            || IsTerminalTaskStatus(awaited.Status));
    }

    internal static List<DurableTaskNode> ResolveWaitTargets(
        IReadOnlyList<DurableTaskNode> tasks,
        DurableTaskNode waiter,
        IReadOnlyList<string> requested,
        out List<string> unknown)
    {
        unknown = [];
        var own = tasks.Where(item => item.DispatchedByTaskId == waiter.TaskId).ToList();
        if (requested.Count == 0)
            return own.Where(item => !waiter.ReportedTaskIds.Contains(item.TaskId)).ToList();
        var targets = new List<DurableTaskNode>();
        foreach (var reference in requested)
        {
            var match = FindDispatchedTask(own, waiter.TaskId, reference);
            if (match is null) unknown.Add(reference);
            else if (!targets.Contains(match)) targets.Add(match);
        }
        return targets;
    }

    internal static DurableTaskNode? FindDispatchedTask(IReadOnlyList<DurableTaskNode> tasks, Guid? dispatcher, string reference)
    {
        var value = reference.Trim();
        var candidates = tasks.Where(item => dispatcher is null || item.DispatchedByTaskId == dispatcher).ToList();
        return candidates.FirstOrDefault(item => Guid.TryParse(value, out var id) && item.DispatchId == id)
            ?? candidates.FirstOrDefault(item => string.Equals(item.TeammateHandle, value, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(item => string.Equals(item.WorkerHandle, value, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(item => string.Equals(item.TaskKey, value, StringComparison.OrdinalIgnoreCase));
    }

    internal static string BuildTaskWaitResult(IReadOnlyList<DurableTaskNode> targets) =>
        JsonSerializer.Serialize(new
        {
            tasks = targets.Select(item => new
            {
                dispatch_id = item.DispatchId,
                handle = item.TeammateHandle ?? item.WorkerHandle,
                agent = item.WorkerAgentSlug ?? item.RequestedAgent,
                title = item.Title,
                status = item.ResultStatus ?? item.Status,
                worktree_path = item.TaskWorktreePath,
                branch = ResolveOwnWorktreeBranch(item),
                dispatched_by_task_id = item.DispatchedByTaskId,
                summary = BoundText(item.ResultSummary ?? string.Empty, TaskWaitFieldLimit),
                criteria = item.Evidence
                    .Where(entry => entry.StartsWith("criterion_evidence:", StringComparison.Ordinal)
                        || entry.StartsWith("missing_criterion_evidence:", StringComparison.Ordinal))
                    .Select(entry => entry.StartsWith("criterion_evidence:", StringComparison.Ordinal)
                        ? SplitCriterion(entry["criterion_evidence:".Length..])
                        : new CriterionResult(BoundText(entry["missing_criterion_evidence:".Length..], 400), false, null))
                    .ToArray()
            }).ToArray()
        });

    private sealed record CriterionResult(
        [property: JsonPropertyName("criterion")] string Criterion,
        [property: JsonPropertyName("met")] bool Met,
        [property: JsonPropertyName("evidence")] string? Evidence);

    private static CriterionResult SplitCriterion(string body)
    {
        var split = body.IndexOf("||", StringComparison.Ordinal);
        return split < 0
            ? new CriterionResult(BoundText(body, 400), true, null)
            : new CriterionResult(BoundText(body[..split], 400), true, BoundText(body[(split + 2)..], 400));
    }

    internal static string FollowUpBrief(DurableTaskNode prior, string? instruction)
    {
        var builder = new StringBuilder()
            .Append("This continues an earlier sub-task: ").Append(prior.Title)
            .Append(" (").Append(prior.TeammateHandle ?? prior.WorkerHandle ?? prior.WorkerAgentSlug ?? prior.TaskKey).Append(", ")
            .Append(prior.ResultStatus ?? prior.Status).Append(").\nIts result: ")
            .Append(BoundText(prior.ResultSummary ?? "no result reported yet", TaskWaitFieldLimit));
        foreach (var entry in prior.Evidence.Where(entry => entry.StartsWith("criterion_evidence:", StringComparison.Ordinal)))
            builder.Append("\n- ").Append(BoundText(entry["criterion_evidence:".Length..].Replace("||", " — "), 400));
        builder.Append("\n\nNew instruction: ").Append(string.IsNullOrWhiteSpace(instruction) ? "(see title)" : instruction.Trim());
        return builder.ToString();
    }

    // Automatic TinaChat / Session Organization Enrollment & Governance

    private async Task JoinConversationPoolAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        Guid identityInstanceId,
        CancellationToken cancellationToken)
    {
        if (checkpoint.ConversationIdentityInstanceId is not null) return;
        var pool = new List<Guid> { identityInstanceId };
        try
        {
            var siblings = await _lifecycle.ListActiveRunsAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
            foreach (var sibling in siblings.Where(item => item.RunId != run.RunId).Take(MaxPoolSiblings))
            {
                var stored = await _lifecycle.GetCurrentRunCheckpointAsync(sibling.RunId, cancellationToken).ConfigureAwait(false);
                if (stored is null) continue;
                var other = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(stored.Content, JsonOptions);
                if (other?.ConversationIdentityInstanceId is { } instance && !pool.Contains(instance)) pool.Add(instance);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not read the sibling runs of {RunId} for its conversation pool.", run.RunId);
        }
        checkpoint.ConversationIdentityInstanceId = identityInstanceId;
        checkpoint.MainInstanceId = identityInstanceId;
        checkpoint.InstancePoolIds = pool;
        if (pool.Count > 1)
            await AppendEventAsync(checkpoint.RunId, "conversation.pool_joined",
                "This run's conversation identity runs alongside other instances of it in the session and shares their context.",
                new { run_id = checkpoint.RunId, instance_id = identityInstanceId, pool }, cancellationToken,
                idempotencyKey: $"run:{checkpoint.RunId}:pool").ConfigureAwait(false);
    }

    private async Task EnrolConversationAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        Guid authorId,
        CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || OrganizationScopeOf(run) is not { } scope) return;
        try
        {
            var conversation = configuration.OperationAgents.FirstOrDefault(agent => agent.DirectUserOutput)
                ?? configuration.OperationAgents.FirstOrDefault();
            await organization.EnrolAsync(new OrganizationMemberEnrolment(
                scope,
                OrganizationRoles.Conversation,
                conversation?.Id ?? "conversation",
                conversation?.Id ?? "conversation",
                Guid.Parse(run.RunId),
                authorId,
                AgentDefinitionId: conversation?.AgentDefinitionId,
                Description: conversation?.Description), cancellationToken).ConfigureAwait(false);
            foreach (var standing in GovernanceSubscribers.Standing(configuration))
            {
                await organization.EnrolAsync(new OrganizationMemberEnrolment(
                    scope,
                    OrganizationRoles.Governance,
                    standing.Id,
                    standing.Id,
                    Guid.Parse(run.RunId),
                    AgentDefinitionId: standing.AgentDefinitionId,
                    Description: standing.Description), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not enrol run {RunId} in its session's organization.", run.RunId);
        }
    }

    private async Task EnrolTeammateAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTeammateState teammate,
        Guid? dispatcherInstanceId,
        CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || OrganizationScopeOf(run) is not { } scope) return;
        try
        {
            var definition = configuration.ExecutionAgents
                .FirstOrDefault(a => string.Equals(a.Id, teammate.AgentSlug, StringComparison.OrdinalIgnoreCase));
            await organization.EnrolAsync(new OrganizationMemberEnrolment(
                scope,
                OrganizationRoles.Executor,
                teammate.AgentSlug,
                teammate.Handle,
                Guid.Parse(run.RunId),
                teammate.AgentInstanceId,
                teammate.CurrentTaskId,
                dispatcherInstanceId ?? checkpoint.PlannerAgentId,
                definition?.AgentDefinitionId,
                definition?.Description), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not pre-enrol teammate {Handle} in organization for run {RunId}.", teammate.Handle, run.RunId);
        }
    }

    private async Task EnrolWorkerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || OrganizationScopeOf(run) is not { } scope) return;
        try
        {
            var slug = string.IsNullOrWhiteSpace(task.WorkerAgentSlug) ? worker.Role : task.WorkerAgentSlug!;
            var definition = configuration.OperationAgents.Concat(configuration.ExecutionAgents)
                .FirstOrDefault(agent => string.Equals(agent.Id, slug, StringComparison.OrdinalIgnoreCase));
            var isConversation = string.Equals(worker.Layer, "operation", StringComparison.Ordinal)
                && (definition?.DirectUserOutput ?? true);
            var dispatcher = task.DispatchedByTaskId is { } dispatchedBy
                ? checkpoint.Tasks.FirstOrDefault(item => item.TaskId == dispatchedBy)?.WorkerAgentId
                : checkpoint.PlannerAgentId;
            var displayHandle = task.TeammateHandle ?? (string.IsNullOrWhiteSpace(task.WorkerHandle) ? slug : task.WorkerHandle);
            await organization.EnrolAsync(isConversation
                ? new OrganizationMemberEnrolment(scope, OrganizationRoles.Conversation, slug, slug,
                    Guid.Parse(run.RunId), worker.Id, task.TaskId, AgentDefinitionId: worker.AgentDefinitionId)
                : new OrganizationMemberEnrolment(scope, OrganizationRoles.Executor, slug, displayHandle,
                    Guid.Parse(run.RunId), worker.Id, task.TaskId, dispatcher ?? checkpoint.PlannerAgentId,
                    worker.AgentDefinitionId, definition?.Description), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not enrol task {TaskKey} of run {RunId} in its session's organization.", task.TaskKey, run.RunId);
        }
    }

    private async Task RaiseStandingAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        string topic,
        string summary,
        string? subjectKind,
        string? subjectId,
        CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || OrganizationScopeOf(run) is not { } scope) return;
        var subscribers = GovernanceSubscribers.For(configuration, topic);
        if (subscribers.Count == 0) return;
        foreach (var agent in subscribers)
        {
            try
            {
                await organization.NotifyAsync(
                    new OrganizationNotice(scope, Guid.Parse(run.RunId), topic, agent.Id, summary, subjectKind, subjectId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.TryLogWarning(ex, "Could not tell standing member {Agent} about {Topic} in run {RunId}.", agent.Id, topic, run.RunId);
            }
        }
    }

    private async Task<bool> ObserveAutonomousEventAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        string topic,
        string summary)
    {
        if (_autonomousPlanning is not { } planning || OrganizationScopeOf(run) is not { } scope) return false;
        try
        {
            return await planning.ObserveAsync(
                new AutonomousPlanningRun(scope.TenantId, scope.WorkspaceId, scope.SessionId, Guid.Parse(run.RunId), configuration.RuntimePolicy),
                topic,
                summary,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "Could not report {Topic} of run {RunId} to the autonomous planner.", topic, run.RunId);
            return false;
        }
    }

    private async Task SetMemberOfflineAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (Organization is not { } organization) return;
        try
        {
            await organization.SetInstanceOfflineAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not mark instance {InstanceId} offline.", instanceId);
        }
    }

    private async Task SetRunMembersOfflineAsync(Guid runId, CancellationToken cancellationToken)
    {
        foreach (var key in _runTeammateMcpLeases.Keys.Where(k => k.RunId == runId).ToArray())
        {
            _runTeammateMcpLeases.TryRemove(key, out _);
        }
        if (Organization is not { } organization) return;
        try
        {
            await organization.SetRunOfflineAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not mark the members of run {RunId} offline.", runId);
        }
    }

    private async Task<Guid?> ArchiveEvidenceAsync(
        RunState run,
        Guid? taskId,
        string kind,
        string title,
        string? author,
        string content,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content) || OrganizationScopeOf(run) is not { } scope) return null;
        if (_services.GetService(typeof(IEvidenceArchive)) is not IEvidenceArchive archive) return null;
        try
        {
            return await archive.AppendAsync(new EvidenceEntry(
                scope.TenantId, scope.WorkspaceId, scope.SessionId, Guid.Parse(run.RunId), taskId,
                kind, title, author, content, sourceKey), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not archive {Kind} evidence of run {RunId}.", kind, run.RunId);
            return null;
        }
    }

    internal static string TaskEvidenceText(DurableTaskNode task, bool citeEvidenceRefs = false)
    {
        var text = new StringBuilder();
        text.Append("Status: ").AppendLine(task.Status);
        text.Append("Task: ").Append(task.TaskKey).Append(" — ").AppendLine(task.Title);
        if (!string.IsNullOrWhiteSpace(task.Description)) text.Append("Brief: ").AppendLine(task.Description);
        var worker = task.TeammateHandle ?? task.WorkerHandle ?? task.WorkerAgentSlug;
        if (!string.IsNullOrWhiteSpace(worker)) text.Append("Worker: ").AppendLine(worker);
        if (task.WriteScope is { Count: > 0 } scope) text.Append("Write scope: ").AppendLine(string.Join(", ", scope));
        text.AppendLine("Result:").AppendLine(string.IsNullOrWhiteSpace(task.ResultSummary) ? "(no result text)" : task.ResultSummary);
        if (task.Evidence.Count > 0)
        {
            text.AppendLine("Evidence:");
            foreach (var item in task.Evidence) text.Append("- ").AppendLine(item);
        }
        if (task.CriteriaVerdicts.Count > 0)
        {
            text.AppendLine("Criteria:");
            foreach (var verdict in task.CriteriaVerdicts)
            {
                text.Append(verdict.Satisfied ? "- ✓ " : "- ✗ ").Append(verdict.Criterion);
                if (citeEvidenceRefs)
                {
                    text.Append(" — refs: ")
                        .AppendLine(verdict.EvidenceRefs is { Count: > 0 } refs ? string.Join(", ", refs) : "(none)");
                }
                else
                {
                    text.AppendLine(string.IsNullOrWhiteSpace(verdict.Evidence) ? string.Empty : " — " + verdict.Evidence);
                }
            }
        }
        return text.ToString();
    }

    private static OrganizationScope? OrganizationScopeOf(RunState run) =>
        Guid.TryParse(run.TenantId, out var tenantId) && Guid.TryParse(run.WorkspaceId, out var workspaceId)
        && Guid.TryParse(run.InitiatedByPrincipalId, out var principalId) && Guid.TryParse(run.SessionId, out var sessionId)
            ? new OrganizationScope(tenantId, workspaceId, principalId, sessionId)
            : null;

    private static string DefaultSummary(OperationalTriggerPoint point, FullDuplexCheckpointV1 checkpoint) => point switch
    {
        OperationalTriggerPoint.TaskGraphCreated => $"The task graph was (re)planned: {checkpoint.Tasks.Count} task(s): {string.Join(", ", checkpoint.Tasks.Take(12).Select(task => task.TaskKey))}.",
        OperationalTriggerPoint.RunFinalized => "The run finished and its answer was delivered.",
        _ => point.ToString()
    };

    // Best-effort SessionPlanService wrappers (safe in background hosted service without HTTP TenantContext)

    private async Task<SessionPlanDto?> TryGetSessionPlanAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_sessionPlan is null) return null;
        try
        {
            return await _sessionPlan.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "Could not read session plan for session {SessionId}.", sessionId);
            return null;
        }
    }

    private async Task TrySyncPlannedTasksToSessionPlanAsync(
        Guid sessionId,
        Guid runId,
        IReadOnlyList<DurableTaskNode> tasks,
        string? goalSummary,
        CancellationToken cancellationToken)
    {
        if (_sessionPlan is null || tasks.Count == 0) return;
        try
        {
            await _sessionPlan.SyncFromPlannedTasksAsync(sessionId, runId, tasks, goalSummary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "Could not sync planned tasks to session plan for run {RunId}.", runId);
        }
    }

    private async Task TrySyncTaskTransitionToSessionPlanAsync(
        Guid sessionId,
        Guid runId,
        DurableTaskNode task,
        string? summary,
        IReadOnlyList<string>? evidence,
        string? worktreePath,
        string? worktreeBranch,
        CancellationToken cancellationToken)
    {
        if (_sessionPlan is null) return;
        try
        {
            await _sessionPlan.SyncTaskTransitionAsync(
                sessionId, runId, task, summary, evidence, worktreePath, worktreeBranch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "Could not sync task transition to session plan for task {TaskId}.", task.TaskId);
        }
    }

    private async Task<SessionPlanDto?> TrySyncTaskPlanToSessionPlanAsync(
        Guid sessionId,
        Guid runId,
        DurableTaskNode task,
        IReadOnlyList<TaskPlanStep> steps,
        string? explanation,
        CancellationToken cancellationToken)
    {
        if (_sessionPlan is null) return null;
        try
        {
            return await _sessionPlan.SyncFromTaskPlanToolAsync(
                sessionId, runId, task, steps, explanation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "Could not sync plan_update steps to session plan for task {TaskId}.", task.TaskId);
            return null;
        }
    }
}
