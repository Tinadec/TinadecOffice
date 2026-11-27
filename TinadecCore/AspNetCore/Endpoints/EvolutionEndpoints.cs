using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Agent and Context evolution surface: human-reviewed candidate &amp; policy proposal flows.
/// Agent proposals promote sanitized immutable agent versions behind evaluation gates;
/// Context evolution proposals (P2 Track 3) queue prompt/skill deduplication and
/// multi-run context policy tuning proposals for human review before writing mode
/// versions or audit-commented <c>runtime.toml</c> updates.
/// </summary>
public static class EvolutionEndpoints
{
    private static readonly JsonSerializerOptions SnakeCaseWebOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static IEndpointRouteBuilder MapEvolutionEndpoints(this IEndpointRouteBuilder app)
    {
        // Ensure TinadecCore.Context module initializer registers ContextPolicyProposalService factory.
        _ = typeof(ContextPolicyProposalService);

        app.MapGet("/api/v1/agent-evolution/proposals", async (string? status, IAgentInstanceService instances, CancellationToken ct) =>
        {
            try
            {
                var candidates = await instances.ListCandidatesAsync(status, ct);
                return Results.Ok(candidates.Select(ToProposal));
            }
            catch (ReviewFilterValueException ex) { return MemoryReviewEndpoints.ReviewFilterFailure(ex); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_STATUS", message = ex.Message }); }
        });

        app.MapPost("/api/v1/agent-evolution/generate", async (HttpRequest request, IAgentInstanceService instances, CancellationToken ct) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<GenerateProposalRequest>(request.Body, cancellationToken: ct);
                if (body is null) return Results.BadRequest(new { code = "INVALID_PAYLOAD", message = "Request body is empty." });
                var candidate = await instances.CreateCandidateAsync(new AgentCandidateProposal(
                    body.SourceRunId,
                    body.SourceInstanceId,
                    body.Name,
                    body.Layer,
                    body.AgentType,
                    body.Confidence,
                    body.Proposal,
                    body.ProjectId), ct);
                return Results.Created($"/api/v1/agent-evolution/proposals/{candidate.Id}", ToProposal(candidate));
            }
            catch (JsonException) { return Results.BadRequest(new { code = "INVALID_PAYLOAD", message = "Request body is not valid JSON." }); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Source agent instance was not found." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_PROPOSAL", message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { code = "FORBIDDEN_PROPOSAL", message = ex.Message }, statusCode: 403); }
        });

        // Promotion is a human review decision: the supplied evaluation run is
        // re-checked against the current suite, proposal and control version,
        // then the sanitized proposal is published as an immutable agent version.
        // Canary and activation remain future pipeline stages.
        app.MapPost("/api/v1/agent-evolution/proposals/{candidateId}/promote", async (Guid candidateId, PromoteAgentCandidateRequest? request, IAgentInstanceService instances, IAgentConfigurationService configurations, AgentEvaluationService evaluations, CancellationToken ct) =>
            await AgentCandidatePromotion.PromoteAsync(instances, configurations, evaluations, candidateId, request?.Reason, request?.EvalRunId, ct));

        // Evaluation evidence for human review: the proposal, the replayed
        // source run (task outcomes, supervision rounds, milestones), and the
        // paired evaluation runs recorded against this candidate.
        app.MapGet("/api/v1/agent-evolution/proposals/{candidateId}/evaluation", async (Guid candidateId, IAgentInstanceService instances, IRunReplayService replay, AgentEvaluationService evaluations, CancellationToken ct) =>
        {
            try
            {
                var (candidate, proposal) = await instances.GetCandidateWithProposalAsync(candidateId, ct);
                var sourceReplay = await replay.BuildReplayAsync(candidate.SourceRunId.ToString(), ct);
                var evalRuns = await evaluations.ListRunsAsync(candidateId, ct);
                return Results.Ok(new
                {
                    candidate_id = candidate.Id,
                    name = candidate.Name,
                    layer = candidate.Layer,
                    agent_type = candidate.AgentType,
                    status = candidate.Status,
                    confidence = candidate.ConfidenceScore,
                    proposal,
                    eval_run_id = candidate.EvalRunId,
                    eval_runs = evalRuns,
                    source_run_replay = sourceReplay is null ? null : new
                    {
                        run_id = sourceReplay.RunId,
                        status = sourceReplay.Status,
                        tasks = sourceReplay.Tasks.Select(task => new
                        {
                            task_key = task.TaskKey,
                            lane_key = task.LaneKey,
                            status = task.Status,
                            summary = task.Summary,
                            evidence = task.Evidence
                        }),
                        lanes = sourceReplay.Lanes.Select(lane => new
                        {
                            lane_key = lane.LaneKey,
                            status = lane.Status,
                            escalated = lane.Escalated,
                            task_keys = lane.TaskKeys,
                            waits = lane.Waits.Select(w => new
                            {
                                waiting_task = w.WaitingTaskKey,
                                lane = w.Lane,
                                predicate = w.Predicate
                            })
                        }),
                        supervision_rounds = sourceReplay.SupervisionRounds.Select(round => new
                        {
                            revision_round = round.RevisionRound,
                            decision = round.Decision,
                            reasons = round.Reasons
                        }),
                        milestones = sourceReplay.Milestones.Select(milestone => new
                        {
                            event_type = milestone.EventType,
                            timestamp = milestone.Timestamp
                        })
                    }
                });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { code = "NOT_FOUND", candidate_id = candidateId, message = "Agent candidate was not found." });
            }
        });

        app.MapPost("/api/v1/agent-evolution/proposals/{candidateId}/reject", async (Guid candidateId, ReviewDecisionRequest? request, IAgentInstanceService instances, CancellationToken ct) =>
            await DecideAsync(instances, candidateId, "rejected", request, ct));

        // ── P2 Context Self-Evolution Proposals (human review queue + evaluation/evidence hooks) ──

        app.MapGet("/api/v1/context-evolution/proposals", async (
            string? status,
            string? kind,
            string? target_scope,
            Guid? project_id,
            Guid? mode_id,
            int? limit,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var service = ResolveProposalService(httpContext.RequestServices);
                var list = await service.ListProposalsAsync(new ContextEvolutionProposalQuery(
                    Status: status,
                    Kind: kind,
                    TargetScope: target_scope,
                    ProjectId: project_id,
                    ModeId: mode_id,
                    Limit: limit ?? 50), ct).ConfigureAwait(false);
                return Results.Ok(list);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "INVALID_FILTER", message = ex.Message });
            }
        })
        .Produces<ContextEvolutionProposalListDto>()
        .ProducesProblem(400);

        app.MapGet("/api/v1/context-evolution/proposals/{proposalId:guid}", async (
            Guid proposalId,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var service = ResolveProposalService(httpContext.RequestServices);
            var proposal = await service.GetProposalAsync(proposalId, ct).ConfigureAwait(false);
            return proposal is null
                ? Results.NotFound(new { code = "NOT_FOUND", proposal_id = proposalId, message = "Context evolution proposal was not found." })
                : Results.Ok(proposal);
        })
        .Produces<ContextEvolutionProposalDto>()
        .ProducesProblem(404);

        app.MapGet("/api/v1/context-evolution/proposals/{proposalId:guid}/evaluation", async (
            Guid proposalId,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var services = httpContext.RequestServices;
            var proposalService = ResolveProposalService(services);
            var proposal = await proposalService.GetProposalAsync(proposalId, ct).ConfigureAwait(false);
            if (proposal is null)
            {
                return Results.NotFound(new { code = "NOT_FOUND", proposal_id = proposalId, message = "Context evolution proposal was not found." });
            }

            IReadOnlyList<EvalRunSummaryResponse> evalRuns = [];
            if (proposal.LinkedAgentCandidateId is { } candidateId && candidateId != Guid.Empty
                && services.GetService<AgentEvaluationService>() is { } evaluations)
            {
                try
                {
                    evalRuns = await evaluations.ListRunsAsync(candidateId, ct).ConfigureAwait(false);
                }
                catch (KeyNotFoundException)
                {
                    evalRuns = [];
                }
            }

            var diagnosticsSnapshots = new List<object>();
            if (services.GetService<IRunContextDiagnosticsService>() is { } diagnosticsService)
            {
                foreach (var rawRunId in proposal.EvidenceRunIds.Take(5))
                {
                    if (!Guid.TryParse(rawRunId, out var evidRunGuid)) continue;
                    var diag = await diagnosticsService.DiagnoseRunAsync(evidRunGuid, cancellationToken: ct).ConfigureAwait(false);
                    if (diag is null) continue;
                    diagnosticsSnapshots.Add(new
                    {
                        run_id = diag.RunId,
                        status = diag.Status,
                        peak_occupancy_pct = diag.WindowBuckets.PeakOccupancyPct,
                        degradation_detected = diag.WindowBuckets.DegradationDetected,
                        degradation_reason_codes = diag.WindowBuckets.DegradationReasonCodes,
                        critical_dropped_sources = diag.Compaction.CriticalDroppedSources,
                        total_dropped_tokens = diag.Compaction.TotalDroppedTokensAcrossPacks,
                        active_signals = diag.Governance.ActiveSignals,
                        max_same_args_retry_streak = diag.Governance.MaxSameArgsRetryStreak
                    });
                }
            }

            return Results.Ok(new
            {
                proposal_id = proposal.Id,
                proposal,
                eval_runs = evalRuns,
                evidence_diagnostics = diagnosticsSnapshots
            });
        })
        .ProducesProblem(404);

        app.MapPost("/api/v1/context-evolution/proposals/{proposalId:guid}/apply", async (
            Guid proposalId,
            HttpRequest httpRequest,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            ApplyContextEvolutionProposalRequest? body = null;
            if (httpRequest.ContentLength is > 0 || httpRequest.HasJsonContentType())
            {
                try
                {
                    body = await JsonSerializer.DeserializeAsync<ApplyContextEvolutionProposalRequest>(
                        httpRequest.Body,
                        SnakeCaseWebOptions,
                        ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { code = "INVALID_PAYLOAD", message = "Request body is not valid JSON." });
                }
            }

            return await ApplyContextProposalAsync(proposalId, body, httpContext, ct).ConfigureAwait(false);
        })
        .Produces<ContextEvolutionApplyResultDto>()
        .ProducesProblem(400)
        .ProducesProblem(404)
        .ProducesProblem(409)
        .ProducesProblem(422);

        app.MapPost("/api/v1/context-evolution/proposals/{proposalId:guid}/reject", async (
            Guid proposalId,
            HttpRequest httpRequest,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            RejectContextEvolutionProposalRequest? body = null;
            if (httpRequest.ContentLength is > 0 || httpRequest.HasJsonContentType())
            {
                try
                {
                    body = await JsonSerializer.DeserializeAsync<RejectContextEvolutionProposalRequest>(
                        httpRequest.Body,
                        SnakeCaseWebOptions,
                        ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { code = "INVALID_PAYLOAD", message = "Request body is not valid JSON." });
                }
            }

            try
            {
                var service = ResolveProposalService(httpContext.RequestServices);
                var rejected = await service.RejectProposalAsync(proposalId, body?.Reason, ct).ConfigureAwait(false);
                return Results.Ok(rejected);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { code = "NOT_FOUND", proposal_id = proposalId, message = "Context evolution proposal was not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { code = "ALREADY_DECIDED", proposal_id = proposalId, message = ex.Message });
            }
        })
        .Produces<ContextEvolutionProposalDto>()
        .ProducesProblem(400)
        .ProducesProblem(404)
        .ProducesProblem(409);

        return app;
    }

    private static IContextPolicyProposalService ResolveProposalService(IServiceProvider services) =>
        ContextPolicyProposalServiceRegistry.Resolve(services)
        ?? ContextPolicyProposalService.CreateForScope(services).Service;

    private static async Task<IResult> ApplyContextProposalAsync(
        Guid proposalId,
        ApplyContextEvolutionProposalRequest? request,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var services = httpContext.RequestServices;
        var proposalService = ResolveProposalService(services);

        var proposal = await proposalService.GetProposalAsync(proposalId, ct).ConfigureAwait(false);
        if (proposal is null)
        {
            return Results.NotFound(new { code = "NOT_FOUND", proposal_id = proposalId, message = "Context evolution proposal was not found." });
        }
        if (!string.Equals(proposal.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { code = "ALREADY_DECIDED", proposal_id = proposalId, message = $"Proposal has already been decided ({proposal.Status})." });
        }

        try
        {
            // 1. Optional / candidate-linked AgentEvaluationService regression gate check
            var effectiveEvalRunId = request?.EvalRunId ?? proposal.EvalRunId;
            string? verifiedGateStatus = null;
            Guid? promotedAgentId = null;

            if (request?.RequireEvalGate == true && (effectiveEvalRunId is null || effectiveEvalRunId == Guid.Empty))
            {
                return Results.Json(new
                {
                    code = "EVAL_REQUIRED",
                    proposal_id = proposalId,
                    message = "Applying this proposal requires an evaluation run that passed the gate."
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (effectiveEvalRunId is { } evalRunId && evalRunId != Guid.Empty)
            {
                var evaluations = services.GetService<AgentEvaluationService>();
                if (evaluations is null)
                {
                    return Results.Json(new
                    {
                        code = "EVAL_UNAVAILABLE",
                        proposal_id = proposalId,
                        message = "AgentEvaluationService is not registered in this host."
                    }, statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                if (proposal.LinkedAgentCandidateId is { } linkedCandidateId && linkedCandidateId != Guid.Empty
                    && services.GetService<IAgentInstanceService>() is { } instances
                    && services.GetService<IAgentConfigurationService>() is { } configurations)
                {
                    var (candidate, candidatePayload) = await instances.GetCandidateWithProposalAsync(linkedCandidateId, ct).ConfigureAwait(false);
                    var sanitized = CandidateProposalSanitizer.Sanitize(candidate, candidatePayload);
                    var gateDecision = await evaluations.AssertPromotableAsync(candidate, sanitized, evalRunId, ct).ConfigureAwait(false);
                    verifiedGateStatus = gateDecision.Gate.Status;

                    if (string.Equals(proposal.Kind, ContextEvolutionProposalKinds.PromptDedupProposal, StringComparison.OrdinalIgnoreCase))
                    {
                        var draft = JsonSerializer.SerializeToElement(await configurations.CreateDraftAsync("agent", sanitized.Payload, ct).ConfigureAwait(false));
                        var agentId = draft.GetProperty("id").GetGuid();
                        var revision = draft.GetProperty("revision").GetInt64();
                        await configurations.PublishAsync(agentId, revision, ct).ConfigureAwait(false);
                        await instances.DecideCandidateAsync(linkedCandidateId, "promoted", request?.Reason, ct, agentId, evalRunId).ConfigureAwait(false);
                        promotedAgentId = agentId;
                    }
                }
                else
                {
                    var evalDetail = await evaluations.GetRunAsync(evalRunId, ct).ConfigureAwait(false);
                    if (evalDetail is null)
                    {
                        return Results.Json(new
                        {
                            code = "EVAL_RUN_INVALID",
                            proposal_id = proposalId,
                            message = "Evaluation run was not found."
                        }, statusCode: StatusCodes.Status422UnprocessableEntity);
                    }
                    if (!string.Equals(evalDetail.Run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Conflict(new
                        {
                            code = "EVAL_RUN_INCOMPLETE",
                            proposal_id = proposalId,
                            message = "Evaluation run has not completed."
                        });
                    }
                    if (!string.Equals(evalDetail.Run.GateStatus, "passed", StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Json(new
                        {
                            code = "EVAL_GATE_FAILED",
                            proposal_id = proposalId,
                            message = $"Evaluation gate status is '{evalDetail.Run.GateStatus ?? "failed"}'."
                        }, statusCode: StatusCodes.Status422UnprocessableEntity);
                    }
                    verifiedGateStatus = evalDetail.Run.GateStatus;
                }
            }

            // 2. Mode-scoped context_policy_proposal -> publish updated mode version with P0 context_strategy / context_advanced
            if (string.Equals(proposal.Kind, ContextEvolutionProposalKinds.ContextPolicyProposal, StringComparison.OrdinalIgnoreCase)
                && string.Equals(proposal.TargetScope, ContextEvolutionTargetScopes.Mode, StringComparison.OrdinalIgnoreCase))
            {
                if (proposal.PolicyPatch is null)
                {
                    return Results.BadRequest(new { code = "INVALID_PROPOSAL", proposal_id = proposalId, message = "Mode proposal has no policy_patch." });
                }

                RuntimeTomlCommentedWriter.ValidatePatch(proposal.PolicyPatch);
                var topology = services.GetService<IAgentTopologyService>();
                if (topology is null)
                {
                    return Results.Conflict(new { code = "MODE_TOPOLOGY_UNAVAILABLE", proposal_id = proposalId, message = "IAgentTopologyService is not available in this host." });
                }

                Guid? targetModeId = proposal.TargetModeId;
                if ((targetModeId is null || targetModeId == Guid.Empty) && !string.IsNullOrWhiteSpace(proposal.TargetModeSlug))
                {
                    targetModeId = await topology.ResolveIdBySlugOrGuidAsync(proposal.TargetModeSlug, ct).ConfigureAwait(false);
                }
                if (targetModeId is null || targetModeId == Guid.Empty)
                {
                    return Results.NotFound(new { code = "MODE_NOT_FOUND", proposal_id = proposalId, message = "Target mode could not be resolved." });
                }

                var modeLoaded = await topology.GetAsync(targetModeId.Value, ct).ConfigureAwait(false);
                if (!modeLoaded.Ok || modeLoaded.Mode is not { ValueKind: JsonValueKind.Object } modeJson)
                {
                    return Results.NotFound(new { code = "MODE_NOT_FOUND", proposal_id = proposalId, message = modeLoaded.Message ?? "Target mode was not found." });
                }

                var mergedDefaultOptions = MergeModeDefaultOptionsWithPatch(modeJson, proposal.PolicyPatch);
                var upsertPayload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["default_options"] = mergedDefaultOptions
                });

                var workingModeId = targetModeId.Value;
                var upserted = await topology.UpsertDraftAsync(workingModeId, upsertPayload, modeLoaded.Revision ?? -1, ct).ConfigureAwait(false);
                if (!upserted.Ok && string.Equals(upserted.Code, AgentTopologyCodes.BuiltInReadOnly, StringComparison.Ordinal))
                {
                    var cloned = await topology.CloneDraftAsync(workingModeId, JsonSerializer.SerializeToElement(new { }), ct).ConfigureAwait(false);
                    if (!cloned.Ok || cloned.ModeId is not { } clonedId)
                    {
                        return Results.Conflict(new { code = cloned.Code ?? "MODE_CLONE_FAILED", proposal_id = proposalId, message = cloned.Message ?? "Could not clone managed mode." });
                    }
                    workingModeId = clonedId;
                    upserted = await topology.UpsertDraftAsync(workingModeId, upsertPayload, cloned.Revision ?? -1, ct).ConfigureAwait(false);
                }

                if (!upserted.Ok)
                {
                    return Results.BadRequest(new { code = upserted.Code ?? "MODE_UPDATE_FAILED", proposal_id = proposalId, message = upserted.Message ?? "Could not update mode default_options." });
                }

                var published = await topology.PublishAsync(workingModeId, upserted.Revision ?? -1, ct).ConfigureAwait(false);
                if (!published.Ok)
                {
                    return Results.BadRequest(new { code = published.Code ?? "MODE_PUBLISH_FAILED", proposal_id = proposalId, message = published.Message ?? "Could not publish updated mode version." });
                }

                var updatedProposal = await proposalService.MarkAppliedAsync(
                    proposalId,
                    new ContextEvolutionAppliedReceipt(
                        Reason: request?.Reason ?? "Applied mode context policy proposal and published new mode version.",
                        AppliedModeId: workingModeId,
                        AppliedModeVersion: published.Version,
                        EvalRunId: effectiveEvalRunId,
                        EvalGateStatus: verifiedGateStatus),
                    ct).ConfigureAwait(false);

                return Results.Ok(new ContextEvolutionApplyResultDto
                {
                    Proposal = updatedProposal,
                    Applied = true,
                    Summary = $"Published mode version v{published.Version} ({workingModeId:D}) with updated P0 context policy.",
                    AppliedModeId = workingModeId,
                    AppliedModeVersion = published.Version,
                    EvalRunId = effectiveEvalRunId
                });
            }

            // 3. Global-scoped context_policy_proposal -> write runtime.toml with explicit audit comments
            if (string.Equals(proposal.Kind, ContextEvolutionProposalKinds.ContextPolicyProposal, StringComparison.OrdinalIgnoreCase))
            {
                var ifMatch = httpContext.Request.Headers.IfMatch.ToString();
                var effectiveReq = request is null && !string.IsNullOrWhiteSpace(ifMatch)
                    ? new ApplyContextEvolutionProposalRequest { ExpectedContentHash = ifMatch.Trim('"') }
                    : request is not null && string.IsNullOrWhiteSpace(request.ExpectedContentHash) && !string.IsNullOrWhiteSpace(ifMatch)
                        ? new ApplyContextEvolutionProposalRequest
                        {
                            Reason = request.Reason,
                            EvalRunId = request.EvalRunId,
                            RequireEvalGate = request.RequireEvalGate,
                            ExpectedContentHash = ifMatch.Trim('"')
                        }
                        : request;

                var appliedGlobal = await proposalService.ApplyGlobalPolicyProposalAsync(
                    proposalId,
                    effectiveReq,
                    verifiedGateStatus,
                    ct).ConfigureAwait(false);
                return Results.Ok(appliedGlobal);
            }

            // 4. Prompt / Skill deduplication proposal -> record approved remediation & optional promoted agent candidate
            var markedDedup = await proposalService.MarkAppliedAsync(
                proposalId,
                new ContextEvolutionAppliedReceipt(
                    Reason: request?.Reason ?? "Approved prompt/skill deduplication proposal.",
                    PromotedAgentId: promotedAgentId,
                    EvalRunId: effectiveEvalRunId,
                    EvalGateStatus: verifiedGateStatus),
                ct).ConfigureAwait(false);

            return Results.Ok(new ContextEvolutionApplyResultDto
            {
                Proposal = markedDedup,
                Applied = true,
                Summary = promotedAgentId.HasValue
                    ? $"Approved deduplication proposal ({markedDedup.RemediationCode}) and published sanitized agent version {promotedAgentId.Value:D}."
                    : $"Approved deduplication proposal ({markedDedup.RemediationCode}) for {markedDedup.DedupEvidence?.LineRangeLabel ?? "selected sources"}.",
                PromotedAgentId = promotedAgentId,
                EvalRunId = effectiveEvalRunId
            });
        }
        catch (ConfigurationDocumentException ex)
        {
            return ex.Code == "configuration_conflict"
                ? Results.Conflict(new { code = "POLICY_CONFLICT", proposal_id = proposalId, message = ex.Message, diagnostics = ex.Diagnostics })
                : Results.BadRequest(new { code = "INVALID_POLICY", proposal_id = proposalId, message = ex.Message, diagnostics = ex.Diagnostics });
        }
        catch (EvalRunInvalidException ex)
        {
            return Results.Json(new { code = "EVAL_RUN_INVALID", proposal_id = proposalId, message = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (EvalRunIncompleteException ex)
        {
            return Results.Conflict(new { code = "EVAL_RUN_INCOMPLETE", proposal_id = proposalId, message = ex.Message });
        }
        catch (EvalStaleException ex)
        {
            return Results.Conflict(new { code = "EVAL_STALE", proposal_id = proposalId, message = ex.Message });
        }
        catch (EvalGateFailedException ex)
        {
            return Results.Json(new
            {
                code = "EVAL_GATE_FAILED",
                proposal_id = proposalId,
                message = ex.Message,
                failures = ex.Failures.Select(f => new { code = f.Code, case_id = f.CaseId, detail = f.Detail })
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { code = "NOT_FOUND", proposal_id = proposalId, message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { code = "INVALID_PROPOSAL", proposal_id = proposalId, message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { code = "INVALID_PROPOSAL", proposal_id = proposalId, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { code = "CONFLICT", proposal_id = proposalId, message = ex.Message });
        }
    }

    private static Dictionary<string, object?> MergeModeDefaultOptionsWithPatch(
        JsonElement modeJson,
        ContextPolicyPatchDto patch)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plan_first"] = false,
            ["spec_enabled"] = false,
            ["multi_agent"] = false,
            ["bulletin_board"] = false,
            ["worktree"] = false,
            ["workflow_mode_version_id"] = null
        };

        var advanced = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (modeJson.TryGetProperty("default_options", out var existingOpts) && existingOpts.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in existingOpts.EnumerateObject())
            {
                if (string.Equals(prop.Name, "context_advanced", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var advProp in prop.Value.EnumerateObject())
                        {
                            advanced[advProp.Name] = JsonElementToPrimitive(advProp.Value);
                        }
                    }
                }
                else
                {
                    merged[prop.Name] = JsonElementToPrimitive(prop.Value);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(patch.ContextStrategy))
        {
            merged["context_strategy"] = patch.ContextStrategy.Trim().ToLowerInvariant();
        }
        if (patch.CompactionTriggerRatio is { } ratio)
        {
            advanced["compaction_trigger_ratio"] = Math.Round(ratio, 4);
        }
        if (patch.RecentMessageLimit is { } recentLimit)
        {
            advanced["recent_message_limit"] = recentLimit;
        }
        if (!string.IsNullOrWhiteSpace(patch.RecallFidelity))
        {
            advanced["recall_fidelity"] = patch.RecallFidelity.Trim().ToLowerInvariant();
        }
        if (patch.AllowCrossSessionRecall is { } allowCross)
        {
            advanced["allow_cross_session_recall"] = allowCross;
        }
        if (!string.IsNullOrWhiteSpace(patch.EvolutionMode))
        {
            advanced["evolution_mode"] = patch.EvolutionMode.Trim().ToLowerInvariant();
        }
        if (patch.CoreMarkdownMaxTokens is { } maxTokens)
        {
            advanced["core_markdown_max_tokens"] = maxTokens;
        }

        if (advanced.Count > 0)
        {
            merged["context_advanced"] = advanced;
        }

        return merged;
    }

    private static object? JsonElementToPrimitive(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.Clone()
    };

    private static async Task<IResult> DecideAsync(IAgentInstanceService instances, Guid candidateId, string decision, ReviewDecisionRequest? request, CancellationToken ct)
    {
        try
        {
            var candidate = await instances.DecideCandidateAsync(candidateId, decision, request?.Reason, ct);
            return Results.Ok(ToProposal(candidate));
        }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Agent proposal was not found." }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_DECISION", message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { code = "ALREADY_DECIDED", message = ex.Message }); }
    }

    private static object ToProposal(AgentCandidateRecord candidate) => new
    {
        id = candidate.Id,
        source_run_id = candidate.SourceRunId,
        source_instance_id = candidate.SourceInstanceId,
        generated_by_instance_id = candidate.GeneratedByInstanceId,
        name = candidate.Name,
        layer = candidate.Layer,
        agent_type = candidate.AgentType,
        status = candidate.Status,
        confidence = candidate.ConfidenceScore,
        promoted_agent_id = candidate.PromotedAgentId,
        eval_run_id = candidate.EvalRunId,
        decision_reason = candidate.DecisionReason,
        created_at = candidate.CreatedAt,
        updated_at = candidate.UpdatedAt
    };
}
