using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.DmaEA;

internal sealed class CorrectionTraceState
{
    public Guid EpisodeId { get; set; }
    public Guid RunId { get; set; }
    public Guid? ParentRunId { get; set; }
    public int Revision { get; set; }
    public string Phase { get; set; } = "OBSERVE";
    public string Status { get; set; } = "investigating";
    public string Goal { get; set; } = string.Empty;
    public string Symptom { get; set; } = string.Empty;
    public string DoneWhen { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string RootCause { get; set; } = "UNKNOWN";
    public string? Recheck { get; set; }
    public string Confidence { get; set; } = "low";
    public string? Intervention { get; set; }
    public string? Prediction { get; set; }
    public string? Verdict { get; set; }
    public string? Fix { get; set; }
    public string? Verify { get; set; }
    public string? LastFeedbackRequestId { get; set; }
    public string? LastFeedbackKind { get; set; }
    public string? LastFeedbackNote { get; set; }
    public Guid? ReopenedRunId { get; set; }
    public Guid? ReopenedTurnId { get; set; }
    public List<CorrectionHypothesisState> Hypotheses { get; set; } = [];
    public List<CorrectionEvidenceState> Evidence { get; set; } = [];
    public List<CorrectionInterventionState> Interventions { get; set; } = [];
    public CorrectionCompletionState Completion { get; set; } = new();
    public List<CorrectionQuestionState> OpenQuestions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CorrectionTraceDto ToDto() => new()
    {
        EpisodeId = EpisodeId,
        RunId = RunId,
        ParentRunId = ParentRunId,
        Revision = Revision,
        Phase = Phase,
        Status = Status,
        Goal = Goal,
        Symptom = Symptom,
        DoneWhen = DoneWhen,
        Summary = Summary,
        Reason = Reason,
        RootCause = RootCause,
        Recheck = Recheck,
        Confidence = Confidence,
        Intervention = Intervention,
        Prediction = Prediction,
        Verdict = Verdict,
        Fix = Fix,
        Verify = Verify,
        Hypotheses = Hypotheses.Select(item => item.ToDto()).ToList(),
        Evidence = Evidence.Select(item => item.ToDto()).ToList(),
        Interventions = Interventions.Select(item => item.ToDto()).ToList(),
        Completion = Completion.ToDto(),
        OpenQuestions = OpenQuestions.Select(item => item.ToDto()).ToList(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    public string PromptContext()
    {
        var lines = new List<string>
        {
            $"[Correction] phase={Phase}; status={Status}; goal={Goal}; symptom={Symptom}; done_when={DoneWhen}",
            $"root_cause={RootCause}; completion={Completion.Requirement}/{Completion.Status}; unresolved={Completion.UnresolvedReason ?? "none"}",
        };
        if (Hypotheses.Count > 0)
            lines.Add("hypotheses: " + string.Join(" | ", Hypotheses.Take(8).Select(item => $"{item.Id}:{item.Statement} ({item.Status})")));
        if (Evidence.Count > 0)
            lines.Add("evidence: " + string.Join(" | ", Evidence.TakeLast(8).Select(item => $"{item.Kind}:{item.Summary}")));
        var pending = OpenQuestions.Where(question => question.Blocking && question.AnsweredAt is null).ToArray();
        if (pending.Length > 0)
            lines.Add("open_questions: " + string.Join(" | ", pending.Select(item => $"{item.Id}:{item.Question}")));
        return string.Join("\n", lines);
    }
}

internal sealed class CorrectionHypothesisState
{
    public string Id { get; set; } = string.Empty;
    public string Statement { get; set; } = string.Empty;
    public string Status { get; set; } = "testing";
    public string? KillCriterion { get; set; }
    public List<string> EvidenceIds { get; set; } = [];
    public string Confidence { get; set; } = "low";

    public CorrectionHypothesisDto ToDto() => new()
    {
        Id = Id,
        Statement = Statement,
        Status = Status,
        KillCriterion = KillCriterion,
        EvidenceIds = EvidenceIds.ToList(),
        Confidence = Confidence,
    };
}

internal sealed class CorrectionEvidenceState
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = "tool";
    public string Kind { get; set; } = "observation";
    public string Strength { get; set; } = "inference";
    public string Summary { get; set; } = string.Empty;
    public string? ExecutionId { get; set; }
    public string? ToolId { get; set; }
    public string? Path { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CorrectionEvidenceDto ToDto() => new()
    {
        Id = Id,
        Source = Source,
        Kind = Kind,
        Strength = Strength,
        Summary = Summary,
        ExecutionId = ExecutionId,
        ToolId = ToolId,
        Path = Path,
        CreatedAt = CreatedAt,
    };
}

internal sealed class CorrectionInterventionState
{
    public string Id { get; set; } = string.Empty;
    public string? ExecutionId { get; set; }
    public string? ToolId { get; set; }
    public Guid? WorkspaceSnapshotId { get; set; }
    public string? Path { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool Revertible { get; set; }
    public string? BeforeText { get; set; }
    public string? AfterText { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }

    public CorrectionInterventionDto ToDto() => new()
    {
        Id = Id,
        ExecutionId = ExecutionId,
        ToolId = ToolId,
        Path = Path,
        Summary = Summary,
        Revertible = Revertible,
        RevertedAt = RevertedAt,
    };
}

internal sealed class CorrectionCompletionState
{
    public string Requirement { get; set; } = "diagnostics";
    public string Authority { get; set; } = "machine";
    public string Status { get; set; } = "blocked";
    public List<string> ModifiedArtifacts { get; set; } = [];
    public List<string> VerifiedArtifacts { get; set; } = [];
    public CorrectionVerificationDto? StrongestCheck { get; set; }
    public CorrectionReproductionState? Repro { get; set; }
    public string? UnresolvedReason { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    public CorrectionCompletionDto ToDto() => new()
    {
        Requirement = Requirement,
        Authority = Authority,
        Status = Status,
        ModifiedArtifacts = ModifiedArtifacts.ToList(),
        VerifiedArtifacts = VerifiedArtifacts.ToList(),
        StrongestCheck = StrongestCheck,
        Repro = Repro?.ToDto(),
        UnresolvedReason = UnresolvedReason,
        VerifiedAt = VerifiedAt,
    };
}

internal sealed class CorrectionReproductionState
{
    public string Command { get; set; } = string.Empty;
    public string? FailedBeforeCommand { get; set; }
    public string? PassedAfterCommand { get; set; }
    public string? FailedBeforeExecutionId { get; set; }
    public string? PassedAfterExecutionId { get; set; }

    public CorrectionReproductionDto ToDto() => new()
    {
        Command = Command,
        FailedBeforeCommand = FailedBeforeCommand,
        PassedAfterCommand = PassedAfterCommand,
        FailedBeforeExecutionId = FailedBeforeExecutionId,
        PassedAfterExecutionId = PassedAfterExecutionId,
    };
}

internal sealed class CorrectionQuestionState
{
    public string Id { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public bool Blocking { get; set; }
    public List<string> Options { get; set; } = [];
    public string? Answer { get; set; }
    public string? AnswerRequestId { get; set; }
    public DateTimeOffset AskedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnsweredAt { get; set; }

    public CorrectionQuestionDto ToDto() => new()
    {
        Id = Id,
        Question = Question,
        Blocking = Blocking,
        Options = Options.ToList(),
        Answer = Answer,
        AskedAt = AskedAt,
        AnsweredAt = AnsweredAt,
    };
}

internal static class CorrectionTracePolicy
{
    public const int MaxHypotheses = 32;
    public const int MaxEvidence = 128;
    public const int MaxInterventions = 32;
    public const int MaxQuestions = 32;

    public static CorrectionTraceState Create(RunState run, string goal, Guid? parentRunId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new CorrectionTraceState
        {
            EpisodeId = Guid.NewGuid(),
            RunId = Guid.TryParse(run.RunId, out var runId) ? runId : Guid.Empty,
            ParentRunId = parentRunId,
            Revision = 0,
            Goal = Bound(goal, 4000),
            Symptom = Bound(goal, 2000),
            DoneWhen = string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            Completion = new CorrectionCompletionState
            {
                Requirement = "diagnostics",
                Authority = "machine",
                Status = "blocked",
                UnresolvedReason = "The correction has not gathered verification evidence yet.",
            },
        };
    }

    public static CorrectionTraceState Clone(CorrectionTraceState source, RunState run) =>
        JsonSerializer.Deserialize<CorrectionTraceState>(
            JsonSerializer.Serialize(source, CorrectionJson),
            CorrectionJson) is { } clone
            ? new CorrectionTraceState
            {
                EpisodeId = clone.EpisodeId,
                RunId = Guid.TryParse(run.RunId, out var runId) ? runId : clone.RunId,
                ParentRunId = clone.RunId,
                Revision = clone.Revision + 1,
                Phase = "BACKTRACK",
                Status = "reopened",
                Goal = clone.Goal,
                Symptom = clone.Symptom,
                DoneWhen = clone.DoneWhen,
                Summary = clone.Summary,
                Reason = clone.Reason,
                RootCause = clone.RootCause,
                Recheck = clone.Recheck,
                Confidence = clone.Confidence,
                Intervention = clone.Intervention,
                Prediction = clone.Prediction,
                Verdict = clone.Verdict,
                Fix = clone.Fix,
                Verify = clone.Verify,
                LastFeedbackRequestId = null,
                LastFeedbackKind = null,
                LastFeedbackNote = null,
                ReopenedRunId = null,
                ReopenedTurnId = null,
                Hypotheses = clone.Hypotheses,
                Evidence = clone.Evidence,
                Interventions = clone.Interventions,
                Completion = clone.Completion,
                OpenQuestions = clone.OpenQuestions,
                CreatedAt = clone.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
            : throw new InvalidDataException("The correction state could not be cloned.");

    public static CorrectionTraceState Merge(CorrectionTraceState left, CorrectionTraceState right)
    {
        if (left.EpisodeId != right.EpisodeId)
            throw new InvalidDataException("Parallel correction traces belong to different episodes.");

        var rightWins = right.UpdatedAt > left.UpdatedAt
            || right.UpdatedAt == left.UpdatedAt && right.Revision >= left.Revision;
        var winner = rightWins ? right : left;
        var other = rightWins ? left : right;
        var merged = JsonSerializer.Deserialize<CorrectionTraceState>(
            JsonSerializer.Serialize(winner, CorrectionJson),
            CorrectionJson)
            ?? throw new InvalidDataException("The correction state could not be merged.");

        merged.Evidence = left.Evidence.Concat(right.Evidence)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.CreatedAt).First())
            .OrderBy(item => item.CreatedAt)
            .TakeLast(MaxEvidence)
            .ToList();
        merged.Interventions = left.Interventions.Concat(right.Interventions)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.RevertedAt is not null).First())
            .TakeLast(MaxInterventions)
            .ToList();
        merged.Hypotheses = MergeHypotheses(winner, other, merged.Evidence);
        merged.OpenQuestions = MergeQuestions(winner, other);
        merged.Revision = Math.Max(left.Revision, right.Revision) + 1;
        merged.UpdatedAt = DateTimeOffset.UtcNow;
        return merged;
    }

    private static readonly JsonSerializerOptions CorrectionJson = new(JsonSerializerDefaults.Web);

    public static string? ApplyProposal(
        CorrectionTraceState trace,
        JsonElement arguments,
        CorrectionRuntimePolicyDto? policy = null)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return "update_correction_trace requires an object.";
        if (policy is not null
            && arguments.TryGetProperty("open_questions", out var questionProposal)
            && questionProposal.ValueKind == JsonValueKind.Array
            && questionProposal.GetArrayLength() > 0
            && !CorrectionRuntimePolicy.Has(policy, CorrectionRuntimePolicy.ClarificationLedgerFeature))
        {
            return "This correction mode does not enable the clarification ledger.";
        }

        var phase = String(arguments, "phase")?.ToUpperInvariant();
        if (phase is not (null or "OBSERVE" or "INTERVENE" or "CONVERGE" or "BACKTRACK"))
            return "phase must be OBSERVE, INTERVENE, CONVERGE, or BACKTRACK.";
        if (phase is not null) trace.Phase = phase;

        if (String(arguments, "summary") is { } summary) trace.Summary = Bound(summary, 500);
        if (String(arguments, "reason") is { } reason) trace.Reason = Bound(reason, 2000);
        if (String(arguments, "goal") is { } goal) trace.Goal = Bound(goal, 4000);
        if (String(arguments, "symptom") is { } symptom) trace.Symptom = Bound(symptom, 2000);
        if (String(arguments, "done_when") is { } doneWhen) trace.DoneWhen = Bound(doneWhen, 2000);
        if (String(arguments, "root_cause") is { } rootCause) trace.RootCause = Bound(rootCause, 4000);
        if (String(arguments, "recheck") is { } recheck) trace.Recheck = Bound(recheck, 2000);
        if (String(arguments, "intervention") is { } intervention) trace.Intervention = Bound(intervention, 2000);
        if (String(arguments, "prediction") is { } prediction) trace.Prediction = Bound(prediction, 2000);
        if (String(arguments, "verdict") is { } verdict) trace.Verdict = Bound(verdict, 2000);
        if (String(arguments, "fix") is { } fix) trace.Fix = Bound(fix, 2000);
        if (String(arguments, "verify") is { } verify) trace.Verify = Bound(verify, 2000);
        if (String(arguments, "confidence") is { } confidence
            && confidence.ToLowerInvariant() is "low" or "medium" or "high")
            trace.Confidence = confidence.ToLowerInvariant();

        if (arguments.TryGetProperty("hypotheses", out var hypotheses) && hypotheses.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in hypotheses.EnumerateArray().Take(MaxHypotheses))
            {
                var id = String(item, "id");
                var statement = String(item, "statement");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(statement)) continue;
                var existing = trace.Hypotheses.FirstOrDefault(hypothesis => hypothesis.Id == id);
                if (existing is null)
                {
                    existing = new CorrectionHypothesisState { Id = Bound(id, 32) };
                    trace.Hypotheses.Add(existing);
                }
                existing.Statement = Bound(statement, 2000);
                existing.Status = String(item, "status")?.ToLowerInvariant() is "confirmed" or "rejected" ? String(item, "status")!.ToLowerInvariant() : "testing";
                existing.KillCriterion = String(item, "kill_criterion") is { } kill ? Bound(kill, 1000) : existing.KillCriterion;
                existing.EvidenceIds = StringArray(item, "evidence_ids")
                    .Where(evidenceId => trace.Evidence.Any(evidence => evidence.Id == evidenceId))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                existing.Confidence = existing.EvidenceIds.Count > 0 ? "medium" : "low";
            }
            trace.Hypotheses = trace.Hypotheses.Take(MaxHypotheses).ToList();
        }

        if (arguments.TryGetProperty("open_questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            var existingBlocking = trace.OpenQuestions
                .FirstOrDefault(question => question.Blocking && question.AnsweredAt is null)
                ?.Id;
            var requestedBlocking = questions.EnumerateArray().Take(MaxQuestions)
                .Where(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("blocking", out var blocking)
                    && blocking.ValueKind == JsonValueKind.True)
                .Select(item => String(item, "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requestedBlocking.Length > 1
                || (existingBlocking is not null && requestedBlocking.Any(id => id != existingBlocking)))
            {
                return "Only one blocking correction question can be open at a time.";
            }
            foreach (var item in questions.EnumerateArray().Take(MaxQuestions))
            {
                var id = String(item, "id");
                var question = String(item, "question");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(question)) continue;
                var existing = trace.OpenQuestions.FirstOrDefault(row => row.Id == id);
                if (existing is null)
                {
                    existing = new CorrectionQuestionState { Id = Bound(id, 32) };
                    trace.OpenQuestions.Add(existing);
                }
                existing.Question = Bound(question, 2000);
                existing.Blocking = item.TryGetProperty("blocking", out var blocking) && blocking.ValueKind == JsonValueKind.True;
                existing.Options = StringArray(item, "options").Select(value => Bound(value, 200)).ToList();
            }
            trace.OpenQuestions = trace.OpenQuestions.Take(MaxQuestions).ToList();
        }

        trace.Revision++;
        trace.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }

    public static void RecordToolFact(
        CorrectionTraceState trace,
        string toolId,
        string argumentsJson,
        ToolDispatchResultDto result,
        bool mutatesWorkspace,
        string? taskHandle,
        Guid? workspaceSnapshotId = null)
    {
        var executionId = result.ExecutionId;
        if (string.IsNullOrWhiteSpace(executionId)) return;
        var path = ReadPath(argumentsJson);
        var exitCode = ReadExitCode(result.Result);
        var embeddedSuccess = result.Result is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("success", out var successValue)
            ? successValue.ToString()
            : null;
        var succeeded = result.Status == "completed"
            && !string.Equals(embeddedSuccess, "false", StringComparison.OrdinalIgnoreCase)
            && (exitCode is null or 0);
        var command = ReadString(argumentsJson, "command");
        var isTest = IsTestCommand(command, toolId);
        var kind = mutatesWorkspace ? "mutation" : isTest ? (succeeded ? "verification_passed" : "verification_failed") : succeeded ? "observation" : "failure";
        var strength = kind switch
        {
            "verification_passed" => "strong",
            "verification_failed" or "failure" => "counter",
            "mutation" => "observation",
            _ => "observation",
        };
        AddEvidence(trace, new CorrectionEvidenceState
        {
            Id = $"ev:{executionId}",
            Source = "tool",
            Kind = kind,
            Strength = strength,
            Summary = Bound($"{toolId}{(string.IsNullOrWhiteSpace(path) ? string.Empty : $" · {path}")}{(exitCode is null ? string.Empty : $" · exit {exitCode}")}", 800),
            ExecutionId = executionId,
            ToolId = toolId,
            Path = path,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        if (mutatesWorkspace && succeeded)
        {
            if (!string.IsNullOrWhiteSpace(path)) AddArtifact(trace.Completion.ModifiedArtifacts, path);
            trace.Completion.VerifiedArtifacts.RemoveAll(item => path is null || SamePath(item, path));
            if (trace.Interventions.Count < MaxInterventions)
            {
                trace.Interventions.Add(new CorrectionInterventionState
                {
                    Id = $"int:{executionId}",
                    ExecutionId = executionId,
                    ToolId = toolId,
                    WorkspaceSnapshotId = workspaceSnapshotId,
                    Path = path,
                    Summary = Bound($"{toolId} applied", 500),
                    Revertible = false,
                });
            }
        }

        if (isTest)
        {
            trace.Completion.Repro ??= new CorrectionReproductionState();
            if (string.IsNullOrWhiteSpace(trace.Completion.Repro.Command) && !string.IsNullOrWhiteSpace(command))
                trace.Completion.Repro.Command = NormalizeCommand(command);
            if (!succeeded && trace.Completion.ModifiedArtifacts.Count == 0)
            {
                trace.Completion.Repro.FailedBeforeExecutionId ??= executionId;
                trace.Completion.Repro.FailedBeforeCommand ??= NormalizeCommand(command);
            }
            if (succeeded && trace.Completion.ModifiedArtifacts.Count > 0)
            {
                trace.Completion.Repro.PassedAfterExecutionId ??= executionId;
                trace.Completion.Repro.PassedAfterCommand ??= NormalizeCommand(command);
                trace.Completion.StrongestCheck = new CorrectionVerificationDto
                {
                    ExecutionId = executionId,
                    ToolId = toolId,
                    Command = command,
                    ExitCode = exitCode,
                    Succeeded = true,
                    Summary = "Focused verification passed after the intervention.",
                };
                if (!string.IsNullOrWhiteSpace(path)) AddArtifact(trace.Completion.VerifiedArtifacts, path);
                foreach (var artifact in trace.Completion.ModifiedArtifacts) AddArtifact(trace.Completion.VerifiedArtifacts, artifact);
            }
            else if (!succeeded)
            {
                trace.Completion.StrongestCheck = new CorrectionVerificationDto
                {
                    ExecutionId = executionId,
                    ToolId = toolId,
                    Command = command,
                    ExitCode = exitCode,
                    Succeeded = false,
                    Summary = "Focused verification failed; the correction is not complete.",
                };
            }
        }

        if (succeeded && !mutatesWorkspace && !isTest && exitCode is 0 && path is not null)
            AddArtifact(trace.Completion.VerifiedArtifacts, path);
        trace.Status = trace.Completion.Status == "resolved" ? "resolved" : "investigating";
        trace.Revision++;
        trace.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static string? EvaluateCompletion(
        CorrectionTraceState trace,
        CorrectionRuntimePolicyDto policy,
        string goal,
        bool hasMutation)
    {
        var completion = trace.Completion;
        completion.ModifiedArtifacts = completion.ModifiedArtifacts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        completion.VerifiedArtifacts = completion.VerifiedArtifacts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        completion.Requirement = ClassifyRequirement(goal, hasMutation, completion);
        completion.Authority = completion.Requirement == "user" ? "user" : "machine";
        completion.Repro ??= new CorrectionReproductionState();
        var hasFailBefore = !string.IsNullOrWhiteSpace(completion.Repro.FailedBeforeExecutionId);
        var hasPassAfter = !string.IsNullOrWhiteSpace(completion.Repro.PassedAfterExecutionId);
        var allArtifactsVerified = completion.ModifiedArtifacts.Count == 0
            || completion.ModifiedArtifacts.All(artifact => completion.VerifiedArtifacts.Any(verified => SamePath(verified, artifact)));

        if (completion.Requirement == "user")
        {
            completion.Status = completion.Status == "resolved" ? "resolved" : "blocked";
            completion.UnresolvedReason = completion.Status == "resolved" ? null : "Waiting for the user to confirm that the reported symptom is gone.";
            return completion.UnresolvedReason;
        }

        if (completion.Requirement == "test" && CorrectionRuntimePolicy.Has(policy, CorrectionRuntimePolicy.FailingReproRequiredFeature))
        {
            if (!hasFailBefore || !hasPassAfter
                || !string.Equals(NormalizeCommand(completion.Repro.FailedBeforeCommand), NormalizeCommand(completion.Repro.PassedAfterCommand), StringComparison.Ordinal))
            {
                completion.Status = "blocked";
                completion.UnresolvedReason = "The focused repro has not been observed failing before and passing after the correction.";
                return completion.UnresolvedReason;
            }
        }

        if (completion.Requirement == "diagnostics" && !allArtifactsVerified)
        {
            completion.Status = "blocked";
            completion.UnresolvedReason = "One or more modified artifacts have no successful verification.";
            return completion.UnresolvedReason;
        }

        if (completion.Requirement == "evidence" && trace.Evidence.Count == 0)
        {
            completion.Status = "blocked";
            completion.UnresolvedReason = "No host-recorded evidence supports the conclusion.";
            return completion.UnresolvedReason;
        }

        if (trace.OpenQuestions.Any(question => question.Blocking && question.AnsweredAt is null))
        {
            completion.Status = "blocked";
            completion.UnresolvedReason = "A blocking correction question is still unanswered.";
            return completion.UnresolvedReason;
        }

        var statusChanged = !string.Equals(trace.Status, "ready", StringComparison.Ordinal);
        var phaseChanged = !string.Equals(trace.Phase, "CONVERGE", StringComparison.Ordinal);
        completion.Status = "ready";
        completion.UnresolvedReason = null;
        completion.VerifiedAt = DateTimeOffset.UtcNow;
        trace.Status = "ready";
        trace.Phase = "CONVERGE";
        if (statusChanged || phaseChanged)
        {
            trace.Revision++;
            trace.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return null;
    }

    public static void MarkUserResolved(CorrectionTraceState trace, string? note = null)
    {
        trace.Completion.Status = "resolved";
        trace.Completion.Authority = "user";
        trace.Completion.VerifiedAt = DateTimeOffset.UtcNow;
        trace.Completion.UnresolvedReason = null;
        if (!string.IsNullOrWhiteSpace(note)) trace.Reason = Bound(note, 2000);
        trace.Status = "resolved";
        trace.Phase = "CONVERGE";
        trace.Revision++;
        trace.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void Reopen(CorrectionTraceState trace, string note)
    {
        trace.Phase = "BACKTRACK";
        trace.Status = "reopened";
        trace.Reason = string.IsNullOrWhiteSpace(note) ? "The user reported that the symptom still reproduces." : Bound(note, 2000);
        trace.Completion.Status = "reopened";
        trace.Completion.UnresolvedReason = "The user reported that the symptom still reproduces.";
        trace.Completion.VerifiedAt = null;
        trace.Completion.VerifiedArtifacts = [];
        trace.Revision++;
        trace.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static List<CorrectionHypothesisState> MergeHypotheses(
        CorrectionTraceState winner,
        CorrectionTraceState other,
        IReadOnlyList<CorrectionEvidenceState> evidence)
    {
        var evidenceIds = evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return winner.Hypotheses.Concat(other.Hypotheses)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(MaxHypotheses)
            .Select(item => new CorrectionHypothesisState
            {
                Id = item.Id,
                Statement = item.Statement,
                Status = item.Status,
                KillCriterion = item.KillCriterion,
                EvidenceIds = item.EvidenceIds.Where(evidenceIds.Contains).Distinct(StringComparer.Ordinal).ToList(),
                Confidence = item.Confidence,
            })
            .ToList();
    }

    private static List<CorrectionQuestionState> MergeQuestions(
        CorrectionTraceState winner,
        CorrectionTraceState other)
    {
        var questions = winner.OpenQuestions.Concat(other.OpenQuestions)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.AnsweredAt is not null)
                .ThenByDescending(item => item.AnsweredAt)
                .First())
            .Take(MaxQuestions)
            .ToList();
        var blocking = questions
            .Where(item => item.Blocking && item.AnsweredAt is null)
            .OrderByDescending(item => winner.OpenQuestions.Any(candidate => candidate.Id == item.Id))
            .ThenByDescending(item => item.AskedAt)
            .FirstOrDefault();
        foreach (var question in questions.Where(item => item.Blocking && item.AnsweredAt is null && item.Id != blocking?.Id))
            question.Blocking = false;
        return questions;
    }

    private static void AddEvidence(CorrectionTraceState trace, CorrectionEvidenceState evidence)
    {
        if (trace.Evidence.Any(item => item.Id == evidence.Id)) return;
        trace.Evidence.Add(evidence);
        if (trace.Evidence.Count > MaxEvidence)
            trace.Evidence = trace.Evidence.TakeLast(MaxEvidence).ToList();
    }

    private static void AddArtifact(List<string> artifacts, string path)
    {
        if (!artifacts.Any(item => SamePath(item, path))) artifacts.Add(path);
    }

    private static string ClassifyRequirement(string goal, bool hasMutation, CorrectionCompletionState completion)
    {
        var text = goal.ToLowerInvariant();
        if (new[] { "视觉", "ui", "窗口", "卡顿", "加载", "浏览器", "页面", "visual", "window", "lag", "slow" }.Any(word => text.Contains(word, StringComparison.Ordinal)))
            return "user";
        if (new[] { "test", "spec", "测试", "回归", "并发", "状态", "持久化", "协议", "repro", "复现" }.Any(word => text.Contains(word, StringComparison.Ordinal)))
            return "test";
        if (hasMutation) return "diagnostics";
        return completion.ModifiedArtifacts.Count > 0 ? "diagnostics" : "evidence";
    }

    private static bool IsTestCommand(string? command, string toolId)
    {
        var text = $"{command} {toolId}".ToLowerInvariant();
        return new[] { "test", "pytest", "vitest", "jest", "go test", "cargo test", "gradle test", "mvn test", "repro" }
            .Any(word => text.Contains(word, StringComparison.Ordinal));
    }

    private static string? ReadPath(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return ReadString(document.RootElement, "path")
                ?? ReadString(document.RootElement, "file_path")
                ?? ReadString(document.RootElement, "filepath")
                ?? ReadString(document.RootElement, "filePath")
                ?? ReadString(document.RootElement, "resource_path");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadExitCode(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value) return null;
        return TryInt(value, "exit_code") ?? TryInt(value, "exitCode");
    }

    private static int? TryInt(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.TryGetInt32(out var result) ? result : null;

    private static string? ReadString(string json, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadString(document.RootElement, key);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static string? String(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()?.Trim()
            : null;

    private static List<string> StringArray(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Array
            ? item.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.String).Select(row => row.GetString()!.Trim()).Where(row => row.Length > 0).ToList()
            : [];

    private static readonly Regex ExitCodeCaptureSuffix = new(
        """(?is)(?:\s*;\s*|\s+&&\s+)(?:echo|printf|write-output)\s+(?:"EXIT_CODE=\$\?"|'EXIT_CODE=\$\?'|EXIT_CODE=\$\?|\$LASTEXITCODE|"%ERRORLEVEL%")\s*$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StderrRedirectSuffix = new(
        """(?is)\s+2>&1\s*$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string NormalizeCommand(string? command)
    {
        var normalized = string.Join(' ', (command ?? string.Empty).Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalized = ExitCodeCaptureSuffix.Replace(normalized, string.Empty).Trim();
        return StderrRedirectSuffix.Replace(normalized, string.Empty).Trim();
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string Bound(string value, int max) =>
        value.Length <= max ? value : value[..max];

}
