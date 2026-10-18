using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

public static class CorrectionEndpoints
{
    public static IEndpointRouteBuilder MapCorrectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/{runId:guid}/correction", GetCorrection)
            .Produces<CorrectionTraceDto>(StatusCodes.Status200OK);
        app.MapPost("/api/v1/runs/{runId:guid}/correction/questions/{questionId}/answer", AnswerQuestion);
        app.MapPost("/api/v1/runs/{runId:guid}/correction/feedback", SubmitFeedback);
        return app;
    }

    private static async Task<IResult> GetCorrection(
        Guid runId,
        ILifecycleManager lifecycle,
        CancellationToken cancellationToken)
    {
        var result = await LoadAsync(runId, lifecycle, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null) return result.Error;
        return Results.Ok(result.Checkpoint!.Correction!.ToDto());
    }

    private static async Task<IResult> AnswerQuestion(
        Guid runId,
        string questionId,
        HttpRequest request,
        ILifecycleManager lifecycle,
        IFullDuplexRunEngine engine,
        CancellationToken cancellationToken)
    {
        CorrectionAnswerRequestDto? body;
        try { body = await request.ReadFromJsonAsync<CorrectionAnswerRequestDto>(cancellationToken: cancellationToken); }
        catch (JsonException) { return BadRequest("invalid_request", "The answer body is invalid JSON."); }
        if (body is null || string.IsNullOrWhiteSpace(body.Answer))
            return BadRequest("invalid_request", "answer is required.");
        if (!ValidRequestId(body.ClientRequestId))
            return BadRequest("invalid_request_id", "client_request_id is required and must be at most 128 characters.");

        var requestId = body.ClientRequestId!.Trim();
        var answer = Bound(body.Answer.Trim(), 8000);
        var idempotencyKey = $"correction-answer:{runId}:{questionId}:{requestId}";
        var loaded = await LoadAsync(runId, lifecycle, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return loaded.Error;

        var trace = loaded.Checkpoint!.Correction!;
        var question = trace.OpenQuestions.FirstOrDefault(item => string.Equals(item.Id, questionId, StringComparison.Ordinal));
        if (question is null)
            return Conflict("correction_question_not_open", "The correction question is not open.");
        if (question.AnsweredAt is not null)
        {
            if (string.Equals(question.AnswerRequestId, requestId, StringComparison.Ordinal)
                && string.Equals(question.Answer, answer, StringComparison.Ordinal))
            {
                return Results.Accepted($"/api/v1/runs/{runId}/correction",
                    new { run_id = runId, question_id = questionId, status = "resumed", trace = trace.ToDto() });
            }
            return Conflict("correction_question_not_open", "The correction question is not open.");
        }
        if (loaded.Run!.Status != RunStatus.AwaitingUser)
            return Conflict("correction_not_waiting", "The correction is not waiting for a user answer.");

        question.Answer = answer;
        question.AnswerRequestId = requestId;
        question.AnsweredAt = DateTimeOffset.UtcNow;
        trace.Revision++;
        trace.UpdatedAt = DateTimeOffset.UtcNow;
        loaded.Checkpoint.Phase = "executing";
        var saved = await lifecycle.SaveRunCheckpointAsync(runId.ToString(), new RunCheckpointWrite(
            loaded.Checkpoint.CheckpointRevision,
            "correction-answer",
            JsonSerializer.Serialize(loaded.Checkpoint, FrozenRunConfigurationV1.JsonOptions),
            IdempotencyKey: idempotencyKey), cancellationToken).ConfigureAwait(false);
        loaded.Checkpoint.CheckpointRevision = saved.Revision;
        await lifecycle.AppendEventAsync(runId, "correction.question.answered",
            new { run_id = runId, question_id = questionId, answer = question.Answer, trace = trace.ToDto() },
            "Correction question answered.",
            cancellationToken: cancellationToken,
            idempotencyKey: idempotencyKey).ConfigureAwait(false);
        await lifecycle.SetRunStatusAsync(runId.ToString(), "executing", "Correction answer accepted.", cancellationToken).ConfigureAwait(false);
        await engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
        return Results.Accepted($"/api/v1/runs/{runId}/correction", new { run_id = runId, question_id = questionId, status = "resumed", trace = trace.ToDto() });
    }

    private static async Task<IResult> SubmitFeedback(
        Guid runId,
        HttpRequest request,
        ILifecycleManager lifecycle,
        IFullDuplexRunEngine engine,
        IFullDuplexRunCoordinator coordinator,
        IWorkspaceSnapshotService snapshots,
        CancellationToken cancellationToken)
    {
        CorrectionFeedbackRequestDto? body;
        try { body = await request.ReadFromJsonAsync<CorrectionFeedbackRequestDto>(cancellationToken: cancellationToken); }
        catch (JsonException) { return BadRequest("invalid_request", "The feedback body is invalid JSON."); }
        var kind = body?.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("reproduced" or "resolved"))
            return BadRequest("invalid_request", "kind must be reproduced or resolved.");
        if (!ValidRequestId(body?.ClientRequestId))
            return BadRequest("invalid_request_id", "client_request_id is required and must be at most 128 characters.");

        var requestId = body!.ClientRequestId!.Trim();
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : Bound(body.Note.Trim(), 2000);
        var idempotencyKey = $"correction-feedback:{runId}:{requestId}";
        var loaded = await LoadAsync(runId, lifecycle, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return loaded.Error;
        var sourceRun = loaded.Run!;
        var policy = loaded.Configuration!.RuntimePolicy!;
        var trace = loaded.Checkpoint!.Correction!;
        var replay = string.Equals(trace.LastFeedbackRequestId, requestId, StringComparison.Ordinal);
        if (replay && (
            !string.Equals(trace.LastFeedbackKind, kind, StringComparison.Ordinal)
            || !string.Equals(trace.LastFeedbackNote, note, StringComparison.Ordinal)))
        {
            return Conflict("idempotency_key_reused", "This client_request_id was already used with different feedback.");
        }

        if (kind == "resolved")
        {
            if (replay)
                return Results.Ok(new { run_id = runId, status = "resolved", trace = trace.ToDto() });
            if (!CorrectionRuntimePolicy.Has(policy, CorrectionRuntimePolicy.CompletionContractFeature)
                || loaded.Checkpoint!.Correction!.Completion.Requirement != "user")
                return Conflict("correction_user_authority_required", "This correction can only be resolved by its required machine verification.");
            trace.LastFeedbackRequestId = requestId;
            trace.LastFeedbackKind = kind;
            trace.LastFeedbackNote = note;
            CorrectionTracePolicy.MarkUserResolved(trace, note);
            loaded.Checkpoint.Phase = sourceRun.Status == RunStatus.Completed ? "completed" : "finalizing";
            var saved = await SaveAsync(runId, lifecycle, loaded.Checkpoint, "correction-user-resolved", cancellationToken, idempotencyKey).ConfigureAwait(false);
            await lifecycle.AppendEventAsync(runId, "correction.feedback.received",
                new { run_id = runId, kind, note, trace = trace.ToDto() },
                "User confirmed the correction.",
                cancellationToken: cancellationToken,
                idempotencyKey: idempotencyKey).ConfigureAwait(false);
            if (sourceRun.Status == RunStatus.AwaitingUser)
            {
                await lifecycle.SetRunStatusAsync(runId.ToString(), "executing", "User confirmed the correction.", cancellationToken).ConfigureAwait(false);
                await engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            return Results.Ok(new { run_id = runId, status = "resolved", revision = saved.Revision, trace = trace.ToDto() });
        }

        if (!CorrectionRuntimePolicy.Has(policy, CorrectionRuntimePolicy.UserReproductionReopensFeature))
            return Conflict("correction_reopen_not_enabled", "This mode does not enable user reproduction reopening.");
        if (replay && trace.ReopenedRunId is { } existingRunId)
        {
            return Results.Accepted($"/api/v1/runs/{existingRunId}/correction",
                new
                {
                    run_id = existingRunId,
                    turn_id = trace.ReopenedTurnId,
                    episode_id = trace.EpisodeId,
                    status = "reopened",
                });
        }
        if (!replay)
        {
            if (sourceRun.Status != RunStatus.Completed)
                return Conflict("correction_not_completed", "Only a completed correction can be reopened by user reproduction.");
            trace.LastFeedbackRequestId = requestId;
            trace.LastFeedbackKind = kind;
            trace.LastFeedbackNote = note;
            CorrectionTracePolicy.Reopen(trace, note ?? string.Empty);
            if (CorrectionRuntimePolicy.Has(policy, CorrectionRuntimePolicy.LocalHunkRevertFeature))
            {
                var rollback = await TryRollbackAsync(trace, snapshots, cancellationToken).ConfigureAwait(false);
                if (rollback is not null) trace.Reason = rollback;
            }
            var saved = await SaveAsync(runId, lifecycle, loaded.Checkpoint, "correction-reopened", cancellationToken, idempotencyKey).ConfigureAwait(false);
            loaded.Checkpoint.CheckpointRevision = saved.Revision;
        }
        await lifecycle.AppendEventAsync(runId, "correction.feedback.received",
            new { run_id = runId, kind, note, trace = trace.ToDto() },
            "User reported that the symptom still reproduces.",
            cancellationToken: cancellationToken,
            idempotencyKey: idempotencyKey).ConfigureAwait(false);
        var reopenedGoal = note ?? "原症状仍复现，请重新调查。";
        var submitted = await coordinator.SubmitAsync(new FullDuplexInvocation(
            Guid.Parse(sourceRun.SessionId),
            reopenedGoal,
            idempotencyKey,
            sourceRun.PermissionMode,
            TargetRunId: null,
            ExpectedContextRevision: null,
            ModeVersionId: loaded.Configuration.ModeVersionId,
            ParentRunId: runId,
            RunKind: "correction_reopen",
            SessionSettingsCaptured: true), cancellationToken).ConfigureAwait(false);
        trace.ReopenedRunId = submitted.RunId;
        trace.ReopenedTurnId = submitted.TurnId;
        trace.Revision++;
        trace.UpdatedAt = DateTimeOffset.UtcNow;
        var linked = await SaveAsync(runId, lifecycle, loaded.Checkpoint, "correction-reopened-linked", cancellationToken,
            $"{idempotencyKey}:reopened").ConfigureAwait(false);
        loaded.Checkpoint.CheckpointRevision = linked.Revision;
        await lifecycle.AppendEventAsync(runId, "correction.episode.reopened",
            new { run_id = runId, reopened_run_id = submitted.RunId, episode_id = trace.EpisodeId },
            "Correction episode reopened.",
            cancellationToken: cancellationToken,
            idempotencyKey: $"correction-reopened:{runId}:{submitted.RunId}").ConfigureAwait(false);
        return Results.Accepted($"/api/v1/runs/{submitted.RunId}/correction",
            new { run_id = submitted.RunId, turn_id = submitted.TurnId, episode_id = loaded.Checkpoint.Correction!.EpisodeId, status = "reopened" });
    }

    private static async Task<(RunState? Run, FullDuplexCheckpointV1? Checkpoint, FrozenRunConfigurationV1? Configuration, IResult? Error)>
        LoadAsync(Guid runId, ILifecycleManager lifecycle, CancellationToken cancellationToken)
    {
        try
        {
            var run = await lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            var stored = await lifecycle.GetFrozenRunConfigurationAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            var current = await lifecycle.GetCurrentRunCheckpointAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (stored is null || current is null) return (null, null, null, NotFound());
            var configuration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(stored.Content, FrozenRunConfigurationV1.JsonOptions);
            var checkpoint = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(current.Content, FrozenRunConfigurationV1.JsonOptions);
            if (configuration?.RuntimePolicy is null || checkpoint?.Correction is null)
                return (run, checkpoint, configuration, NotFound("correction_not_found", "This run has no correction trace."));
            return (run, checkpoint, configuration, null);
        }
        catch (KeyNotFoundException)
        {
            return (null, null, null, NotFound());
        }
    }

    private static async Task<RunCheckpoint> SaveAsync(
        Guid runId,
        ILifecycleManager lifecycle,
        FullDuplexCheckpointV1 checkpoint,
        string purpose,
        CancellationToken cancellationToken,
        string? idempotencyKey = null) =>
        await lifecycle.SaveRunCheckpointAsync(runId.ToString(), new RunCheckpointWrite(
            checkpoint.CheckpointRevision,
            purpose,
            JsonSerializer.Serialize(checkpoint, FrozenRunConfigurationV1.JsonOptions),
            IdempotencyKey: idempotencyKey), cancellationToken).ConfigureAwait(false);

    private static IResult NotFound(string code = "not_found", string message = "Run not found.") =>
        Results.NotFound(new { code, message });

    private static IResult Conflict(string code, string message) =>
        Results.Conflict(new { code, message });

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });

    private static bool ValidRequestId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 128;

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];

    private static async Task<string?> TryRollbackAsync(
        CorrectionTraceState trace,
        IWorkspaceSnapshotService snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var intervention in trace.Interventions
                     .Where(item => item.WorkspaceSnapshotId is not null && item.Path is not null && item.RevertedAt is null)
                     .TakeLast(8)
                     .Reverse())
        {
            try
            {
                var changes = await snapshots.ListFileChangesAsync(intervention.WorkspaceSnapshotId!.Value, cancellationToken).ConfigureAwait(false);
                var change = changes.FirstOrDefault(item =>
                    string.Equals(item.Path, intervention.Path, StringComparison.OrdinalIgnoreCase)
                    || (intervention.Path ?? string.Empty).EndsWith("/" + item.Path, StringComparison.OrdinalIgnoreCase)
                    || (intervention.Path ?? string.Empty).EndsWith("\\" + item.Path, StringComparison.OrdinalIgnoreCase));
                if (change is null || !change.Restorable || change.AfterSha256 is null) continue;
                await snapshots.RestoreFileAsync(
                    intervention.WorkspaceSnapshotId.Value,
                    new WorkspaceFileRestoreRequest(change.Path, change.AfterSha256),
                    cancellationToken).ConfigureAwait(false);
                intervention.RevertedAt = DateTimeOffset.UtcNow;
                intervention.Revertible = false;
                trace.Completion.VerifiedArtifacts.Clear();
                trace.Evidence.Add(new CorrectionEvidenceState
                {
                    Id = $"ev:rollback:{intervention.Id}",
                    Source = "host",
                    Kind = "intervention_reverted",
                    Strength = "counter",
                    Summary = $"Reverted the latest attributable intervention at {change.Path}.",
                    Path = change.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                trace.Revision++;
                return null;
            }
            catch (Exception)
            {
                return "The latest intervention could not be reverted safely; the new investigation will not guess at a file location.";
            }
        }
        return null;
    }

}
