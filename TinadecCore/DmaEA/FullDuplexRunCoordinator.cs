using System.Runtime.CompilerServices;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Admission, durable-stream follow, and control facade for a full-duplex run.
/// Model work belongs exclusively to <see cref="FullDuplexRunEngine"/>.
/// </summary>
public interface IFullDuplexRunCoordinator
{
    Task<RunSubmission> SubmitAsync(FullDuplexInvocation invocation, CancellationToken cancellationToken = default);
    IAsyncEnumerable<RunStreamChunk> FollowAsync(
        Guid runId,
        Guid? turnId = null,
        long afterSequence = 0,
        CancellationToken cancellationToken = default);
    Task<RunControlResult> ControlAsync(
        Guid runId,
        RunControlCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record FullDuplexInvocation(
    Guid SessionId,
    string Content,
    string? ClientMessageId,
    string? ApplicationMode,
    string? AgentMode,
    string? PermissionMode,
    Guid? TargetRunId,
    long? ExpectedContextRevision);

public sealed record RunSubmission(
    Guid RunId,
    Guid TurnId,
    Guid MessageId,
    long ContextRevision,
    string ApplicationMode,
    string AgentMode,
    string RuntimeProfileId,
    bool Existing);

public sealed record RunControlCommand(
    string Action,
    string? ClientControlId = null,
    long? ExpectedContextRevision = null,
    Guid? TurnId = null);

public sealed record RunControlResult(Guid RunId, string Status, string Action, bool Accepted);

public sealed record RunStreamChunk(
    Guid RunId,
    Guid TurnId,
    Guid? MessageId,
    long Seq,
    string Kind,
    string? Delta = null,
    object? Usage = null,
    string? FinishReason = null,
    string? ErrorCategory = null,
    string? SafeErrorMessage = null);

public sealed class RunAdmissionException : InvalidOperationException
{
    public RunAdmissionException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

internal sealed class FullDuplexRunCoordinator : IFullDuplexRunCoordinator
{
    private static readonly TimeSpan FollowPollInterval = TimeSpan.FromMilliseconds(150);

    private readonly IConversationStore _conversations;
    private readonly ILifecycleManager _lifecycle;
    private readonly IAgentRuntimeConfigurationResolver _configurationResolver;
    private readonly IToolManifestSnapshotResolver _toolManifestResolver;
    private readonly IFullDuplexRunEngine _engine;

    public FullDuplexRunCoordinator(
        IConversationStore conversations,
        ILifecycleManager lifecycle,
        IAgentRuntimeConfigurationResolver configurationResolver,
        IToolManifestSnapshotResolver toolManifestResolver,
        IFullDuplexRunEngine engine)
    {
        _conversations = conversations;
        _lifecycle = lifecycle;
        _configurationResolver = configurationResolver;
        _toolManifestResolver = toolManifestResolver;
        _engine = engine;
    }

    public async Task<RunSubmission> SubmitAsync(FullDuplexInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invocation.Content))
        {
            throw new RunAdmissionException("INVALID_MESSAGE", "Message content is required.");
        }

        // A repeated client message must return the original run. Do this before
        // resolving mutable configuration so a hot reload cannot change an idempotent retry.
        if (!string.IsNullOrWhiteSpace(invocation.ClientMessageId))
        {
            var existingMessage = await _conversations.FindMessageByClientMessageIdAsync(
                invocation.SessionId, invocation.ClientMessageId, cancellationToken).ConfigureAwait(false);
            if (existingMessage is not null)
            {
                if (!string.Equals(existingMessage.Content, invocation.Content, StringComparison.Ordinal))
                {
                    throw new RunAdmissionException("IDEMPOTENCY_KEY_REUSE", "The client message id was already used with different content.");
                }

                var existingTurn = await _conversations.FindTurnByUserMessageAsync(existingMessage.Id, cancellationToken).ConfigureAwait(false);
                if (existingTurn?.RunId is { } existingRunId)
                {
                    var existingRun = await _lifecycle.GetRunStateAsync(existingRunId.ToString(), cancellationToken).ConfigureAwait(false);
                    await VerifyRequestedModeAsync(invocation, existingRun, cancellationToken).ConfigureAwait(false);
                    return new RunSubmission(
                        existingRunId,
                        existingTurn.Id,
                        existingMessage.Id,
                        existingTurn.BaseContextRevision,
                        existingRun.ApplicationMode,
                        existingRun.AgentMode,
                        existingRun.RuntimeProfileId,
                        Existing: true);
                }
            }
        }

        var beforeRevision = await _conversations.GetContextRevisionAsync(invocation.SessionId, cancellationToken).ConfigureAwait(false);
        if (invocation.ExpectedContextRevision is { } expectedRevision && expectedRevision != beforeRevision)
        {
            throw new RunAdmissionException(
                "CONTEXT_REVISION_CONFLICT",
                $"Expected context revision {expectedRevision}, but the current revision is {beforeRevision}.");
        }

        var turnKind = ClassifyTurn(invocation.Content, invocation.TargetRunId);
        if (turnKind is "control" or "supplement" or "goal_adjustment")
        {
            if (invocation.TargetRunId is not { } targetRunId)
            {
                throw new RunAdmissionException("TARGET_RUN_REQUIRED", "This meeting interaction requires target_run_id.");
            }
            return await SubmitTargetRunInteractionAsync(
                invocation, targetRunId, turnKind, beforeRevision, cancellationToken).ConfigureAwait(false);
        }

        RunState? statusTarget = null;
        if (turnKind == "status_query")
        {
            if (invocation.TargetRunId is not { } targetRunId)
            {
                throw new RunAdmissionException("TARGET_RUN_REQUIRED", "A status query requires target_run_id.");
            }
            statusTarget = await GetTargetRunAsync(invocation.SessionId, targetRunId, allowTerminal: true, cancellationToken).ConfigureAwait(false);
        }

        var configuration = await _configurationResolver.ResolveAsync(
            invocation.SessionId,
            invocation.ApplicationMode,
            invocation.AgentMode,
            invocation.PermissionMode,
            cancellationToken).ConfigureAwait(false);

        var active = await _lifecycle.CountActiveRunsAsync(invocation.SessionId.ToString(), cancellationToken).ConfigureAwait(false);
        if (turnKind is not ("status_query" or "clarification") && active >= configuration.Scheduling.MaxActiveRunsPerSession)
        {
            throw new RunAdmissionException(
                "ACTIVE_RUN_LIMIT",
                $"This session already has {active} active runs; its limit is {configuration.Scheduling.MaxActiveRunsPerSession}.");
        }

        var userMessage = await _conversations.AppendMessageAsync(
            invocation.SessionId,
            "user",
            invocation.Content.Trim(),
            clientMessageId: invocation.ClientMessageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // A competing host can win the message insert. Reuse its attached run rather
        // than creating another turn or a second execution.
        var priorTurn = await _conversations.FindTurnByUserMessageAsync(userMessage.Id, cancellationToken).ConfigureAwait(false);
        if (priorTurn?.RunId is { } priorRunId)
        {
            var priorRun = await _lifecycle.GetRunStateAsync(priorRunId.ToString(), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(userMessage.Content, invocation.Content.Trim(), StringComparison.Ordinal)
                || !ModeMatches(configuration, priorRun))
            {
                throw new RunAdmissionException("IDEMPOTENCY_KEY_REUSE", "The client message id was already used with a different request mode.");
            }
            return new RunSubmission(priorRunId, priorTurn.Id, userMessage.Id, priorTurn.BaseContextRevision,
                priorRun.ApplicationMode, priorRun.AgentMode, priorRun.RuntimeProfileId, Existing: true);
        }

        var turn = await _conversations.CreateTurnAsync(
            invocation.SessionId,
            userMessage.Id,
            turnKind,
            beforeRevision,
            cancellationToken).ConfigureAwait(false);

        var started = await _lifecycle.StartOrGetRunAsync(new RunStartRequest(
            invocation.SessionId.ToString(),
            userMessage.Id.ToString(),
            turn.Id.ToString(),
            beforeRevision,
            configuration.BaselineVersion,
            configuration.ContentHash,
            configuration.ApplicationMode,
            configuration.AgentMode,
            configuration.PermissionMode,
            configuration.RuntimeProfileId), cancellationToken).ConfigureAwait(false);
        var runId = Guid.Parse(started.RunId);
        await _conversations.AttachRunAsync(turn.Id, runId, cancellationToken).ConfigureAwait(false);

        if (started.Existing)
        {
            var existingRun = await _lifecycle.GetRunStateAsync(started.RunId, cancellationToken).ConfigureAwait(false);
            if (!ModeMatches(configuration, existingRun))
            {
                throw new RunAdmissionException("IDEMPOTENCY_KEY_REUSE", "The client message id was already used with a different request mode.");
            }
            return new RunSubmission(runId, turn.Id, userMessage.Id, turn.BaseContextRevision,
                existingRun.ApplicationMode, existingRun.AgentMode, existingRun.RuntimeProfileId, Existing: true);
        }

        // Resolve the process manifest only after StartOrGetRun has established
        // that this host won the idempotent admission race. A replaying caller
        // therefore returns the original run without consulting a newer process
        // manifest or hot-reloaded authorization set.
        if (turnKind == "new_task")
        {
            configuration = await FreezeToolManifestAsync(invocation.SessionId, configuration, cancellationToken).ConfigureAwait(false);
        }
        await _lifecycle.FreezeRunConfigurationAsync(runId.ToString(), configuration.ToLifecycleWrite(), cancellationToken).ConfigureAwait(false);
        if (turnKind is "status_query" or "clarification")
        {
            await CreateInteractionCheckpointAsync(runId, invocation.SessionId, turn.Id, userMessage, beforeRevision,
                turnKind, statusTarget?.RunId is { } target ? Guid.Parse(target) : invocation.TargetRunId, cancellationToken).ConfigureAwait(false);
        }
        await _lifecycle.AppendEventAsync(runId, "task.accepted", new
        {
            run_id = runId,
            turn_id = turn.Id,
            context_revision = beforeRevision,
            configuration_hash = configuration.ContentHash,
            runtime_profile_id = configuration.RuntimeProfileId
        }, "Full-duplex run admitted.", cancellationToken: cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            turn.Id,
            "ack",
            userMessage.Id,
            IdempotencyKey: $"run:{runId}:turn:{turn.Id}:ack"), cancellationToken).ConfigureAwait(false);
        await _engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);

        return new RunSubmission(runId, turn.Id, userMessage.Id, beforeRevision,
            configuration.ApplicationMode, configuration.AgentMode, configuration.RuntimeProfileId, Existing: false);
    }

    private async Task<FrozenRunConfigurationV1> FreezeToolManifestAsync(
        Guid sessionId,
        FrozenRunConfigurationV1 configuration,
        CancellationToken cancellationToken)
    {
        var definitions = configuration.OperationAgents.Concat(configuration.ExecutionAgents).ToArray();
        var allowedToolIds = definitions
            .SelectMany(agent => agent.AllowedTools)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowAll = allowedToolIds.Any(value => string.Equals(value, "*", StringComparison.Ordinal));

        try
        {
            var snapshot = await _toolManifestResolver.ResolveAsync(
                new ToolManifestSnapshotRequest(sessionId, allowedToolIds, allowAll), cancellationToken).ConfigureAwait(false);
            if (snapshot.ProtocolVersion != 2 || string.IsNullOrWhiteSpace(snapshot.ManifestHash))
            {
                throw new ToolManifestSnapshotException(
                    "TOOL_MANIFEST_PROTOCOL_UNSUPPORTED",
                    "TinadecTools manifest v2 is required for autonomous runs.");
            }

            return configuration with
            {
                ToolManifestHash = snapshot.ManifestHash,
                ToolManifestProtocolVersion = snapshot.ProtocolVersion,
                ToolManifest = snapshot.AuthorizedTools
            };
        }
        catch (ToolManifestSnapshotException ex)
        {
            throw new RunAdmissionException(ex.Code, ex.Message);
        }
    }

    private async Task<RunSubmission> SubmitTargetRunInteractionAsync(
        FullDuplexInvocation invocation,
        Guid targetRunId,
        string turnKind,
        long baseContextRevision,
        CancellationToken cancellationToken)
    {
        var target = await GetTargetRunAsync(invocation.SessionId, targetRunId, allowTerminal: false, cancellationToken).ConfigureAwait(false);
        var userMessage = await _conversations.AppendMessageAsync(
            invocation.SessionId,
            "user",
            invocation.Content.Trim(),
            clientMessageId: invocation.ClientMessageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var turn = await _conversations.CreateTurnAsync(
            invocation.SessionId,
            userMessage.Id,
            turnKind,
            baseContextRevision,
            cancellationToken).ConfigureAwait(false);
        await _conversations.AttachRunAsync(turn.Id, targetRunId, cancellationToken).ConfigureAwait(false);

        if (turnKind == "control")
        {
            var action = ParseControlAction(invocation.Content);
            var result = await ControlAsync(targetRunId, new RunControlCommand(
                action,
                invocation.ClientMessageId ?? $"meeting:{turn.Id:N}",
                invocation.ExpectedContextRevision,
                turn.Id), cancellationToken).ConfigureAwait(false);
            await _conversations.CompleteTurnAsync(turn.Id, targetRunId, null, baseContextRevision, result.Status, cancellationToken).ConfigureAwait(false);
            await CompleteInteractionStreamAsync(targetRunId, turn.Id, userMessage.Id, "control_applied", cancellationToken).ConfigureAwait(false);
            return ToTargetSubmission(target, turn, userMessage, baseContextRevision);
        }

        // Appending the interaction message advances the session revision. The
        // request's expected revision was checked before the append in SubmitAsync;
        // bind the durable patch to the post-message revision so it does not reject
        // its own evidence as stale. A concurrent change between these operations
        // still produces an explicit stale patch rather than silently merging it.
        var patchBaseRevision = await _conversations.GetContextRevisionAsync(invocation.SessionId, cancellationToken).ConfigureAwait(false);
        var patch = await _conversations.ApplyContextPatchAsync(new ContextPatchRequest(
            invocation.SessionId,
            patchBaseRevision,
            invocation.Content.Trim(),
            turnKind == "goal_adjustment" ? "Meeting goal adjustment" : "Meeting supplement",
            targetRunId,
            Kind: turnKind), cancellationToken).ConfigureAwait(false);
        var applied = patch.Status == "applied";
        if (applied && patch.AppliedRevision is { } appliedRevision)
        {
            await _lifecycle.AdvanceRunContextRevisionAsync(targetRunId.ToString(), appliedRevision, cancellationToken).ConfigureAwait(false);
            await _lifecycle.AppendEventAsync(targetRunId, "context.patch.accepted", new
            {
                patch_id = patch.PatchId,
                kind = turnKind,
                requested_context_revision = baseContextRevision,
                base_context_revision = patchBaseRevision,
                context_revision = appliedRevision,
                turn_id = turn.Id
            }, "Meeting context patch accepted.", cancellationToken: cancellationToken).ConfigureAwait(false);
            // A supervision escalation is a user-review gate. Supplying a
            // correction is an explicit decision to resume and replan, so wake
            // awaiting_user runs before enqueueing them.
            if (target.Status == RunStatus.AwaitingUser)
            {
                await _lifecycle.SetRunStatusAsync(targetRunId.ToString(), "executing", "User correction accepted; resuming after supervision review.", cancellationToken).ConfigureAwait(false);
            }
            if (target.Status != RunStatus.Paused)
            {
                await _engine.EnqueueAsync(targetRunId, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _lifecycle.AppendEventAsync(targetRunId, "context.patch.stale", new
            {
                patch_id = patch.PatchId,
                kind = turnKind,
                requested_context_revision = baseContextRevision,
                base_context_revision = patchBaseRevision,
                current_context_revision = patch.CurrentRevision,
                turn_id = turn.Id
            }, "Meeting context patch was retained as stale evidence.", "warning", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var resultRevision = patch.AppliedRevision ?? patch.CurrentRevision;
        await _conversations.CompleteTurnAsync(turn.Id, targetRunId, null, resultRevision,
            applied ? "completed" : "stale", cancellationToken).ConfigureAwait(false);
        // ack → steering/context_conflict → done so FollowAsync yields ack first and still delivers the extension before terminal
        await _lifecycle.AppendRunStreamAsync(targetRunId.ToString(), new DurableRunStreamAppend(turn.Id, "ack", userMessage.Id, IdempotencyKey: $"run:{targetRunId}:turn:{turn.Id}:interaction:ack"), cancellationToken).ConfigureAwait(false);
        if (applied) { try { await _lifecycle.AppendRunStreamAsync(targetRunId.ToString(), new DurableRunStreamAppend(turn.Id, "steering", null, IdempotencyKey: $"run:{targetRunId}:steering:{turn.Id}"), cancellationToken).ConfigureAwait(false); } catch { } }
        else { try { await _lifecycle.AppendRunStreamAsync(targetRunId.ToString(), new DurableRunStreamAppend(turn.Id, "context_conflict", null, IdempotencyKey: $"run:{targetRunId}:conflict:{turn.Id}"), cancellationToken).ConfigureAwait(false); } catch { } }
        await _lifecycle.AppendRunStreamAsync(targetRunId.ToString(), new DurableRunStreamAppend(turn.Id, "done", FinishReason: applied ? "context_applied" : "stale_context", IdempotencyKey: $"run:{targetRunId}:turn:{turn.Id}:interaction:done"), cancellationToken).ConfigureAwait(false);
        return ToTargetSubmission(target, turn, userMessage, resultRevision);
    }

    private async Task<RunState> GetTargetRunAsync(
        Guid sessionId,
        Guid targetRunId,
        bool allowTerminal,
        CancellationToken cancellationToken)
    {
        var target = await _lifecycle.GetRunStateAsync(targetRunId.ToString(), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(target.SessionId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new RunAdmissionException("TARGET_RUN_NOT_FOUND", "The target run does not belong to this session.");
        }
        if (!allowTerminal && IsTerminal(target.Status))
        {
            throw new RunAdmissionException("RUN_NOT_ACTIVE", "The target run is already terminal.");
        }
        return target;
    }

    private static RunSubmission ToTargetSubmission(
        RunState target,
        ConversationTurn turn,
        ConversationMessage message,
        long contextRevision) => new(
            Guid.Parse(target.RunId),
            turn.Id,
            message.Id,
            contextRevision,
            target.ApplicationMode,
            target.AgentMode,
            target.RuntimeProfileId,
            Existing: false);

    private async Task CompleteInteractionStreamAsync(
        Guid runId,
        Guid turnId,
        Guid messageId,
        string finishReason,
        CancellationToken cancellationToken)
    {
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            turnId,
            "ack",
            messageId,
            IdempotencyKey: $"run:{runId}:turn:{turnId}:interaction:ack"), cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            turnId,
            "done",
            FinishReason: finishReason,
            IdempotencyKey: $"run:{runId}:turn:{turnId}:interaction:done"), cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateInteractionCheckpointAsync(
        Guid runId,
        Guid sessionId,
        Guid turnId,
        ConversationMessage trigger,
        long contextRevision,
        string turnKind,
        Guid? targetRunId,
        CancellationToken cancellationToken)
    {
        var checkpoint = new FullDuplexCheckpointV1
        {
            RunId = runId,
            SessionId = sessionId,
            TurnId = turnId,
            TriggerMessageId = trigger.Id,
            UserGoal = trigger.Content,
            Phase = "responding",
            InteractionKind = turnKind,
            TargetRunId = targetRunId,
            ContextRevision = contextRevision
        };
        await _lifecycle.SaveRunCheckpointAsync(runId.ToString(), new RunCheckpointWrite(
            0,
            checkpoint.Phase,
            JsonSerializer.Serialize(checkpoint),
            IdempotencyKey: $"run:{runId}:interaction:{turnKind}:checkpoint"), cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RunStreamChunk> FollowAsync(
        Guid runId,
        Guid? turnId = null,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));

        var cursor = afterSequence;
        DateTimeOffset? terminalObservedAt = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var chunks = await _lifecycle.ReplayRunStreamAsync(runId.ToString(), turnId, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in chunks)
            {
                cursor = Math.Max(cursor, chunk.Sequence);
                yield return ToStreamChunk(chunk);
                if (chunk.Kind is "done" or "error") yield break;
            }

            var state = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (IsTerminal(state.Status))
            {
                // The stream write and terminal run update use independent durable
                // transactions. Keep polling briefly so a reader never loses a
                // terminal delta/done pair in that commit window. A legacy terminal
                // run with no journal still closes eventually.
                terminalObservedAt ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - terminalObservedAt >= TimeSpan.FromSeconds(2)) yield break;
            }
            else terminalObservedAt = null;

            await Task.Delay(FollowPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RunControlResult> ControlAsync(
        Guid runId,
        RunControlCommand command,
        CancellationToken cancellationToken = default)
    {
        var action = command.Action?.Trim().ToLowerInvariant();
        if (action is not ("pause" or "resume" or "cancel"))
        {
            throw new RunAdmissionException("INVALID_RUN_CONTROL", "Action must be pause, resume, or cancel.");
        }

        var state = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (await HasRecordedControlAsync(state, runId, action, command.ClientControlId, cancellationToken).ConfigureAwait(false))
        {
            return new RunControlResult(runId, state.Status.ToString().ToLowerInvariant(), action, Accepted: true);
        }
        if (IsTerminal(state.Status))
        {
            throw new RunAdmissionException("RUN_NOT_ACTIVE", "The run is already terminal.");
        }

        if (command.ExpectedContextRevision is { } expected)
        {
            var current = await _conversations.GetContextRevisionAsync(Guid.Parse(state.SessionId), cancellationToken).ConfigureAwait(false);
            if (expected != current)
            {
                throw new RunAdmissionException("CONTEXT_REVISION_CONFLICT", $"Expected context revision {expected}, but the current revision is {current}.");
            }
        }

        var status = action switch
        {
            "pause" => "paused",
            "resume" => "executing",
            _ => "cancelled"
        };
        await _lifecycle.SetRunStatusAsync(runId.ToString(), status, $"Run {action} requested.", cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendEventAsync(runId, action == "cancel" ? "task.cancelled" : $"run.{action}d", new
        {
            run_id = runId,
            action,
            client_control_id = command.ClientControlId
        }, $"Run {action}.", cancellationToken: cancellationToken).ConfigureAwait(false);

        var streamTurnId = command.TurnId ?? (Guid.TryParse(state.TurnId, out var parsedTurnId) ? parsedTurnId : null);
        if (streamTurnId is { } turnId)
        {
            await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
                turnId,
                "control",
                FinishReason: action,
                IdempotencyKey: string.IsNullOrWhiteSpace(command.ClientControlId)
                    ? null
                    : $"run:{runId}:control:{command.ClientControlId.Trim()}"), cancellationToken).ConfigureAwait(false);
        }

        if (action == "resume") await _engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
        return new RunControlResult(runId, status, action, Accepted: true);
    }

    private async Task<bool> HasRecordedControlAsync(
        RunState state,
        Guid runId,
        string action,
        string? clientControlId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientControlId) || !Guid.TryParse(state.SessionId, out var sessionId)) return false;
        var events = await _lifecycle.ReplayEventsAsync(sessionId, 0, cancellationToken).ConfigureAwait(false);
        foreach (var item in events.Where(item => string.Equals(item.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            if (!item.Payload.TryGetValue("payload", out var body) || body is not JsonElement { ValueKind: JsonValueKind.Object } payload) continue;
            if (!payload.TryGetProperty("action", out var actionValue) || !string.Equals(actionValue.GetString(), action, StringComparison.OrdinalIgnoreCase)) continue;
            if (!payload.TryGetProperty("client_control_id", out var clientValue) || !string.Equals(clientValue.GetString(), clientControlId.Trim(), StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    private async Task VerifyRequestedModeAsync(FullDuplexInvocation invocation, RunState run, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invocation.ApplicationMode)
            && string.IsNullOrWhiteSpace(invocation.AgentMode)
            && string.IsNullOrWhiteSpace(invocation.PermissionMode)) return;
        var requested = await _configurationResolver.ResolveAsync(
            invocation.SessionId,
            invocation.ApplicationMode,
            invocation.AgentMode,
            invocation.PermissionMode,
            cancellationToken).ConfigureAwait(false);
        if (!ModeMatches(requested, run))
        {
            throw new RunAdmissionException("IDEMPOTENCY_KEY_REUSE", "The client message id was already used with a different request mode.");
        }
    }

    private static bool ModeMatches(FrozenRunConfigurationV1 configuration, RunState run) =>
        string.Equals(configuration.ApplicationMode, run.ApplicationMode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(configuration.AgentMode, run.AgentMode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(configuration.PermissionMode, run.PermissionMode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(configuration.RuntimeProfileId, run.RuntimeProfileId, StringComparison.OrdinalIgnoreCase);

    private static RunStreamChunk ToStreamChunk(DurableRunStreamChunk chunk)
    {
        object? usage = null;
        if (!string.IsNullOrWhiteSpace(chunk.UsageJson))
        {
            try { usage = JsonSerializer.Deserialize<JsonElement>(chunk.UsageJson); }
            catch (JsonException) { }
        }
        return new RunStreamChunk(Guid.TryParse(chunk.RunId, out var runId) ? runId : Guid.Empty,
            chunk.TurnId, chunk.MessageId, chunk.Sequence, chunk.Kind, chunk.Delta, usage,
            chunk.FinishReason, chunk.ErrorCategory, chunk.SafeErrorMessage);
    }

    private static bool IsTerminal(RunStatus status) => status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;

    private static string ClassifyTurn(string content, Guid? targetRunId)
    {
        var normalized = content.Trim();
        if (normalized.StartsWith("/pause", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/resume", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return "control";
        if (targetRunId is null) return "new_task";
        if (normalized.Contains("status", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("状态", StringComparison.Ordinal)
            || normalized.Contains("进度", StringComparison.Ordinal)) return "status_query";
        if (normalized.Contains("补充", StringComparison.Ordinal) || normalized.Contains("追加", StringComparison.Ordinal) || normalized.Contains("supplement", StringComparison.OrdinalIgnoreCase)) return "supplement";
        if (normalized.Contains("调整目标", StringComparison.Ordinal) || normalized.Contains("改为", StringComparison.Ordinal)
            || normalized.Contains("change goal", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("goal adjustment", StringComparison.OrdinalIgnoreCase)) return "goal_adjustment";
        return "clarification";
    }

    private static string ParseControlAction(string content)
    {
        var normalized = content.Trim();
        if (normalized.StartsWith("/pause", StringComparison.OrdinalIgnoreCase)) return "pause";
        if (normalized.StartsWith("/resume", StringComparison.OrdinalIgnoreCase)) return "resume";
        if (normalized.StartsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return "cancel";
        throw new RunAdmissionException("INVALID_RUN_CONTROL", "Control commands must begin with /pause, /resume, or /cancel.");
    }
}
