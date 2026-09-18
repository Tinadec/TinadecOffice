using System.Runtime.CompilerServices;
using System.Text.Json;
using TinadecCore.Abstractions;
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
    string? PermissionMode,
    Guid? TargetRunId,
    long? ExpectedContextRevision,
    SessionModelOverride? MeetingModelOverride = null,
    Guid? ModeVersionId = null);

public sealed record RunSubmission(
    Guid RunId,
    Guid TurnId,
    Guid MessageId,
    long ContextRevision,
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
    string? SafeErrorMessage = null,
    DateTimeOffset OccurredAt = default);

public sealed class RunAdmissionException : InvalidOperationException
{
    public RunAdmissionException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

internal sealed class FullDuplexRunCoordinator : IFullDuplexRunCoordinator
{
    private static readonly TimeSpan FollowPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly IConversationStore _conversations;
    private readonly ILifecycleManager _lifecycle;
    private readonly IAgentRuntimeConfigurationResolver _configurationResolver;
    private readonly IToolManifestSnapshotResolver _toolManifestResolver;
    private readonly IFullDuplexRunEngine _engine;
    private readonly IReadOnlyList<IRunInFlightToolCancellation> _runToolCancellations;

    public FullDuplexRunCoordinator(
        IConversationStore conversations,
        ILifecycleManager lifecycle,
        IAgentRuntimeConfigurationResolver configurationResolver,
        IToolManifestSnapshotResolver toolManifestResolver,
        IFullDuplexRunEngine engine,
        IEnumerable<IRunInFlightToolCancellation> runToolCancellations)
    {
        _conversations = conversations;
        _lifecycle = lifecycle;
        _configurationResolver = configurationResolver;
        _toolManifestResolver = toolManifestResolver;
        _engine = engine;
        _runToolCancellations = runToolCancellations.ToArray();
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

        var configuration = await ResolveConfigurationAsync(invocation, cancellationToken).ConfigureAwait(false);

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
                priorRun.RuntimeProfileId, Existing: true);
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
                existingRun.RuntimeProfileId, Existing: true);
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
            configuration.RuntimeProfileId, Existing: false);
    }

    private async Task<FrozenRunConfigurationV1> FreezeToolManifestAsync(
        Guid sessionId,
        FrozenRunConfigurationV1 configuration,
        CancellationToken cancellationToken)
    {
        // The run-frozen manifest is the run's TOOL CEILING. This list is the request's
        // hint; the AUTHORITATIVE set is the mode version's effective-tool union, which
        // ToolManifestSnapshotResolver reads back through IFormalModeResolver and prefers.
        //
        // That union spans ALL nodes regardless of layer, and it must: a solo_dispatch
        // mode arms its conversation identity with tools, and the master's surface is
        // exactly what the master's instance resolves its catalog from. Do NOT "fix" this
        // by excluding operation agents downstream — doing so silently leaves the solo
        // master holding a tool_scope it can never see.
        //
        // The security property the old comment here described still holds, just at a
        // different level: a declaration cannot widen what a WORKER reaches, because each
        // instance's declaration surface is its own grant ∩ this manifest
        // (IFrozenToolManifestCatalog.ListAuthorizedAsync). The ceiling being a union does
        // not hand one agent another's tools.
        //
        // Spawnable worker templates (graph tiers) contribute their tool scope too — a
        // free-form director mode has an empty execution roster, so without this the
        // manifest would authorize nothing for its spawned workers.
        var definitions = configuration.ExecutionAgents.ToArray();
        var spawnable = configuration.Graph?.SpawnableTemplates ?? [];
        var allowedToolIds = definitions
            .SelectMany(agent => agent.AllowedTools)
            .Concat(spawnable.SelectMany(template => template.ToolCeiling))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowAll = allowedToolIds.Any(value => string.Equals(value, "*", StringComparison.Ordinal));

        try
        {
            var snapshot = await _toolManifestResolver.ResolveAsync(
                new ToolManifestSnapshotRequest(
                    sessionId,
                    allowedToolIds,
                    allowAll,
                    SpawnableToolIds: spawnable.SelectMany(template => template.ToolCeiling).ToList(),
                    ModeVersionId: configuration.ModeVersionId), cancellationToken).ConfigureAwait(false);
            if (snapshot.ProtocolVersion != 2 || string.IsNullOrWhiteSpace(snapshot.ManifestHash))
            {
                throw new ToolManifestSnapshotException(
                    "TOOL_MANIFEST_PROTOCOL_UNSUPPORTED",
                    "TinadecTools manifest v2 is required for autonomous runs.");
            }

            // Finish the spawnable tool ceilings against the frozen manifest: an
            // explicit tool id the live manifest does not carry is a misconfiguration
            // and fails closed at admission; a wildcard ceiling stays as-is (it is
            // bounded by the frozen manifest at dispatch time).
            var manifestIds = snapshot.AuthorizedTools.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            FrozenSpawnableTemplate[] frozenSpawnable;
            if (spawnable.Count == 0)
            {
                frozenSpawnable = [];
            }
            else
            {
                frozenSpawnable = new FrozenSpawnableTemplate[spawnable.Count];
                for (var index = 0; index < spawnable.Count; index++)
                {
                    var template = spawnable[index];
                    if (template.ToolCeiling.Contains("*", StringComparer.Ordinal))
                    {
                        frozenSpawnable[index] = template;
                        continue;
                    }
                    var ceiling = template.ToolCeiling.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var unauthorized = ceiling.Where(id => !manifestIds.Contains(id)).ToArray();
                    if (unauthorized.Length > 0)
                        throw new RunAdmissionException("spawnable_template_tools_unauthorized",
                            $"Spawnable template '{template.Slug}' declares tools the frozen manifest does not authorize: {string.Join(", ", unauthorized)}.");
                    frozenSpawnable[index] = template with { ToolCeiling = ceiling };
                }
            }

            return configuration with
            {
                ToolManifestHash = snapshot.ManifestHash,
                ToolManifestProtocolVersion = snapshot.ProtocolVersion,
                ToolManifest = snapshot.AuthorizedTools,
                Graph = configuration.Graph is null ? null : configuration.Graph with { SpawnableTemplates = frozenSpawnable }
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
        var terminalRepairAttempts = 0;
        var lastEmitAt = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var chunks = await _lifecycle.ReplayRunStreamAsync(runId.ToString(), turnId, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in chunks)
            {
                cursor = Math.Max(cursor, chunk.Sequence);
                lastEmitAt = DateTimeOffset.UtcNow;
                yield return ToStreamChunk(chunk);
                if (chunk.Kind is "done" or "error") yield break;
            }

            var state = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (IsTerminal(state.Status))
            {
                // The stream write and terminal run update use independent durable
                // transactions. Actively repair the idempotent terminal tail before
                // giving up; a terminal status must never make the current SSE reader
                // disappear without done/error.
                terminalObservedAt ??= DateTimeOffset.UtcNow;
                if (terminalRepairAttempts < 3)
                {
                    terminalRepairAttempts++;
                    try
                    {
                        await _engine.ReconcileTerminalRunAsync(runId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // The hosted repair loop will retry the durable tail. Keep the
                        // live reader open and, after a bounded grace period, emit an
                        // authoritative in-memory terminal fallback instead of EOF.
                    }
                    continue;
                }
                if (DateTimeOffset.UtcNow - terminalObservedAt >= TimeSpan.FromSeconds(2))
                {
                    yield return state.Status switch
                    {
                        RunStatus.Cancelled => new RunStreamChunk(
                            runId, turnId ?? Guid.Empty, null, cursor, "done",
                            FinishReason: "cancelled", OccurredAt: DateTimeOffset.UtcNow),
                        RunStatus.Failed => new RunStreamChunk(
                            runId, turnId ?? Guid.Empty, null, cursor, "error",
                            ErrorCategory: state.TerminalErrorCategory ?? RunErrorTaxonomy.Runtime,
                            SafeErrorMessage: state.Summary ?? "The run failed before producing a final response.",
                            OccurredAt: DateTimeOffset.UtcNow),
                        _ => new RunStreamChunk(
                            runId, turnId ?? Guid.Empty, null, cursor, "done",
                            FinishReason: "completed", OccurredAt: DateTimeOffset.UtcNow)
                    };
                    yield break;
                }
            }
            else
            {
                terminalObservedAt = null;
                terminalRepairAttempts = 0;
            }

            if (DateTimeOffset.UtcNow - lastEmitAt >= HeartbeatInterval)
            {
                // Idle keep-alive: the endpoint renders this as an SSE comment so
                // intermediaries see traffic and clients never advance their cursor.
                lastEmitAt = DateTimeOffset.UtcNow;
                yield return new RunStreamChunk(runId, turnId ?? Guid.Empty, null, cursor, "heartbeat",
                    OccurredAt: DateTimeOffset.UtcNow);
            }

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
            if (action == "cancel" && state.Status == RunStatus.Cancelled)
            {
                CancelInFlightTools(runId);
                await _engine.FinalizeCancelledRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
            }
            return new RunControlResult(runId, state.Status.ToString().ToLowerInvariant(), action, Accepted: true);
        }
        if (action == "cancel" && state.Status == RunStatus.Cancelled)
        {
            // A prior caller may have committed the terminal status and then lost
            // its response before the event/turn/stream finalization completed.
            // Cancellation finalization is idempotent, so retries repair the tail.
            CancelInFlightTools(runId);
            await _engine.FinalizeCancelledRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
            return new RunControlResult(runId, "cancelled", action, Accepted: true);
        }
        if (IsTerminal(state.Status))
        {
            throw new RunAdmissionException("RUN_NOT_ACTIVE", "The run is already terminal.");
        }
        if (state.CompletedAt is not null)
        {
            throw new RunAdmissionException("RUN_NOT_ACTIVE", "The run has already claimed successful completion and is durably finalizing it.");
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
        try
        {
            await _lifecycle.SetRunStatusAsync(runId.ToString(), status, $"Run {action} requested.", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            var winner = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (action == "cancel" && winner.Status == RunStatus.Cancelled)
            {
                CancelInFlightTools(runId);
                await _engine.FinalizeCancelledRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
                return new RunControlResult(runId, "cancelled", action, Accepted: true);
            }
            if (IsTerminal(winner.Status))
            {
                throw new RunAdmissionException("RUN_NOT_ACTIVE", $"The run already ended as '{winner.Status.ToString().ToLowerInvariant()}'.");
            }
            if (winner.CompletedAt is not null)
            {
                throw new RunAdmissionException("RUN_NOT_ACTIVE", "The run has already claimed successful completion and is durably finalizing it.");
            }
            throw;
        }
        // From this point onward the state transition is durable. A client closing
        // the HTTP request must not interrupt the terminal event/stream/turn tail;
        // startup recovery repairs the same idempotent closure after a host crash.
        var durableToken = action == "cancel" ? CancellationToken.None : cancellationToken;
        if (action == "cancel")
        {
            // The terminal status is already durable. Signal process-local tool
            // calls before writing the terminal stream/turn tail so a provider
            // cannot keep running through the cancellation-finalization window.
            CancelInFlightTools(runId);
            // Terminal closure is the required product behavior; optional control
            // telemetry must never stand between the committed cancellation and its
            // done(cancelled) frame.
            await _engine.FinalizeCancelledRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
            return new RunControlResult(runId, status, action, Accepted: true);
        }
        await _lifecycle.AppendEventAsync(runId, action == "cancel" ? "task.cancelled" : $"run.{action}d", new
        {
            run_id = runId,
            action,
            client_control_id = command.ClientControlId
        }, $"Run {action}.", cancellationToken: durableToken,
            idempotencyKey: action == "cancel"
                ? $"run:{runId}:event:task.cancelled"
                : string.IsNullOrWhiteSpace(command.ClientControlId)
                    ? null
                    : $"run:{runId}:control:{command.ClientControlId.Trim()}:event").ConfigureAwait(false);

        var streamTurnId = command.TurnId ?? (Guid.TryParse(state.TurnId, out var parsedTurnId) ? parsedTurnId : null);
        if (streamTurnId is { } turnId)
        {
            await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
                turnId,
                "control",
                FinishReason: action,
                IdempotencyKey: string.IsNullOrWhiteSpace(command.ClientControlId)
                    ? null
                    : $"run:{runId}:control:{command.ClientControlId.Trim()}"), durableToken).ConfigureAwait(false);
        }

        if (action == "resume")
        {
            await _engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        return new RunControlResult(runId, status, action, Accepted: true);
    }

    private void CancelInFlightTools(Guid runId)
    {
        foreach (var cancellation in _runToolCancellations)
        {
            try
            {
                cancellation.CancelForRun(runId);
            }
            catch
            {
                // The durable run status is already authoritative. Tool-process
                // cancellation is best-effort here; TerminalSessionControl and
                // execution recovery retain their own idempotent repair paths.
            }
        }
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
        if (string.IsNullOrWhiteSpace(invocation.PermissionMode)) return;
        var requested = await ResolveConfigurationAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (!ModeMatches(requested, run))
        {
            throw new RunAdmissionException("IDEMPOTENCY_KEY_REUSE", "The client message id was already used with a different request mode.");
        }
    }

    private Task<FrozenRunConfigurationV1> ResolveConfigurationAsync(
        FullDuplexInvocation invocation,
        CancellationToken cancellationToken) =>
        invocation.ModeVersionId is { } modeVersionId
            ? _configurationResolver.ResolveForModeAsync(
                invocation.SessionId,
                modeVersionId,
                invocation.PermissionMode,
                invocation.MeetingModelOverride,
                cancellationToken)
            : _configurationResolver.ResolveAsync(
                invocation.SessionId,
                invocation.PermissionMode,
                invocation.MeetingModelOverride,
                cancellationToken);

    private static bool ModeMatches(FrozenRunConfigurationV1 configuration, RunState run) =>
        string.Equals(configuration.PermissionMode, run.PermissionMode, StringComparison.OrdinalIgnoreCase)
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
            chunk.FinishReason, chunk.ErrorCategory, chunk.SafeErrorMessage, chunk.CreatedAt);
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
