using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Endpoints;

/// <summary>
/// DmaEA runtime endpoints: full-duplex SSE invoke-stream, run control, run-scoped
/// projections, agent lineage, context versions, and TOML-driven mode catalogs.
/// The coordinator appends the user message (Desktop never double-writes) and keeps
/// running after a subscriber disconnects. No fake success: without a chat route the
/// invoke-stream returns an error chunk and the run records a run.failed event.
/// </summary>
public static class DmaeaEndpoints
{
    public static WebApplication MapDmaeaEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/sessions/{sessionId}/invoke-stream", async (HttpContext context, string sessionId, InvokeStreamRequest request, IFullDuplexRunCoordinator coordinator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("{\"code\":\"INVALID_SESSION_ID\"}", ct);
                return;
            }
            if (request is null || string.IsNullOrWhiteSpace(request.Content))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("{\"code\":\"INVALID_MESSAGE\"}", ct);
                return;
            }

            try
            {
                var admission = await coordinator.SubmitAsync(new FullDuplexInvocation(sessionGuid, request.Content, request.ClientMessageId, request.ApplicationMode, request.AgentMode, request.PermissionMode, request.TargetRunId, request.ExpectedContextRevision), ct);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                await foreach (var chunk in coordinator.FollowAsync(admission.RunId, admission.TurnId, 0, CancellationToken.None))
                {
                    await WriteChunkAsync(context, chunk.Seq, new
                    {
                        run_id = chunk.RunId,
                        session_id = sessionId,
                        turn_id = chunk.TurnId,
                        message_id = chunk.MessageId,
                        seq = chunk.Seq,
                        purpose = "dual_layer",
                        kind = chunk.Kind,
                        delta = chunk.Delta,
                        usage = chunk.Usage,
                        finish_reason = chunk.FinishReason,
                        error_category = chunk.ErrorCategory,
                        safe_error_message = chunk.SafeErrorMessage
                    }, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (RunAdmissionException ex)
            {
                context.Response.StatusCode = ex.Code is "CONTEXT_REVISION_CONFLICT" or "ACTIVE_RUN_LIMIT" or "IDEMPOTENCY_KEY_REUSE" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message }, ct);
            }
            catch (KeyNotFoundException)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { code = "NOT_FOUND", message = "Session was not found." }, ct);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The subscriber disconnected. The coordinator continues independently.
            }
        });

        app.MapGet("/api/v1/runs/{runId}/stream", async (HttpContext context, string runId, Guid? turn_id, long? after_seq, IFullDuplexRunCoordinator coordinator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." }, ct);
                return;
            }

            var cursor = after_seq ?? 0;
            if (context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
                && long.TryParse(lastEventId.ToString(), out var headerCursor)) cursor = Math.Max(cursor, headerCursor);
            if (cursor < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { code = "INVALID_STREAM_CURSOR" }, ct);
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            try
            {
                await foreach (var chunk in coordinator.FollowAsync(runGuid, turn_id, cursor, CancellationToken.None))
                {
                    await WriteChunkAsync(context, chunk.Seq, new
                    {
                        run_id = chunk.RunId,
                        turn_id = chunk.TurnId,
                        message_id = chunk.MessageId,
                        seq = chunk.Seq,
                        purpose = "dual_layer",
                        kind = chunk.Kind,
                        delta = chunk.Delta,
                        usage = chunk.Usage,
                        finish_reason = chunk.FinishReason,
                        error_category = chunk.ErrorCategory,
                        safe_error_message = chunk.SafeErrorMessage
                    }, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (KeyNotFoundException)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { code = "NOT_FOUND", message = "Run was not found." }, ct);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });

        app.MapGet("/api/v1/sessions/{sessionId}/orchestration", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            var session = await sessions.FindAsync(sessionGuid, ct);
            if (session is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            var runs = await lifecycle.ListRunsAsync(sessionGuid, ct);
            var run = runs.FirstOrDefault();
            if (run is null) return Results.Json(new { run = (object?)null, graph = (object?)null, nodes = Array.Empty<object>(), assignments = Array.Empty<object>(), step_results = Array.Empty<object>(), context_packs = Array.Empty<object>(), supervision_findings = Array.Empty<object>() }, options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var nodes = events.Where(e => e.EventType == "task.assigned" || e.EventType == "step.result.created")
                .Select(e => new
                {
                    id = PayloadString(e.Payload, "task_node_id"),
                    graph_id = PayloadString(e.Payload, "graph_id"),
                    run_id = run.Id.ToString(),
                    session_id = sessionId,
                    title = PayloadString(e.Payload, "title") ?? "",
                    description = PayloadString(e.Payload, "description") ?? "",
                    status = e.EventType == "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : "assigned",
                    priority = 1,
                    risk = "medium",
                    success_criteria = Array.Empty<string>(),
                    dependencies = Array.Empty<string>(),
                    required_capabilities = Array.Empty<string>(),
                    created_at = e.Timestamp,
                    updated_at = e.Timestamp
                })
                .ToList();
            var stepResults = events.Where(e => e.EventType == "step.result.created")
                .Select(e => new
                {
                    id = PayloadString(e.Payload, "task_node_id"),
                    run_id = run.Id.ToString(),
                    task_node_id = PayloadString(e.Payload, "task_node_id"),
                    agent_id = PayloadString(e.Payload, "agent_id") ?? "",
                    status = PayloadString(e.Payload, "status") ?? "completed",
                    summary = PayloadString(e.Payload, "summary") ?? "",
                    evidence = PayloadArray(e.Payload, "evidence"),
                    created_at = e.Timestamp
                })
                .ToList();
            return Results.Json(new
            {
                run = new
                {
                    id = run.Id.ToString(),
                    session_id = sessionId,
                    user_message_id = run.TriggerMessageId.ToString(),
                    status = run.Status,
                    summary = run.Summary ?? "",
                    created_at = run.CreatedAt,
                    updated_at = run.UpdatedAt
                },
                graph = (object?)null,
                nodes,
                assignments = Array.Empty<object>(),
                step_results = stepResults,
                context_packs = Array.Empty<object>(),
                supervision_findings = Array.Empty<object>()
            }, options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
            });
        });

        app.MapGet("/api/v1/sessions/{sessionId}/tool-executions", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (await sessions.FindAsync(sessionGuid, ct) is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var items = events.Where(e => e.EventType == "step.result.created" || e.EventType == "task.assigned").Select(e => new
            {
                id = PayloadString(e.Payload, "task_node_id"),
                run_id = PayloadString(e.Payload, "run_id") ?? "",
                session_id = sessionId,
                tool_id = PayloadString(e.Payload, "tool_id") ?? "",
                tool_display_name = "",
                source = "dmaea",
                provider_layer = "execution",
                risk = "medium",
                requires_approval = false,
                status = e.EventType == "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : "pending",
                approval_id = (string?)null,
                step_result_id = PayloadString(e.Payload, "task_node_id"),
                summary = PayloadString(e.Payload, "summary") ?? "",
                evidence = PayloadArray(e.Payload, "evidence"),
                requested_at = e.Timestamp,
                updated_at = e.Timestamp,
                duration_ms = 0L,
                requested_seq = e.Payload.TryGetValue("sequence", out var seq) ? (long)(seq is JsonElement je && je.ValueKind == JsonValueKind.Number ? je.GetInt64() : 0) : 0L,
                updated_seq = 0L,
                event_types = new[] { e.EventType },
                checkpoint_summary = ""
            }).ToList();
            return Results.Ok(items);
        });

        app.MapGet("/api/v1/sessions/{sessionId}/task-nodes", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (await sessions.FindAsync(sessionGuid, ct) is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var nodes = events.Where(e => e.EventType == "task.assigned" || e.EventType == "step.result.created").Select(e => new
            {
                id = PayloadString(e.Payload, "task_node_id"),
                graph_id = PayloadString(e.Payload, "graph_id") ?? "",
                run_id = PayloadString(e.Payload, "run_id") ?? "",
                session_id = sessionId,
                title = PayloadString(e.Payload, "title") ?? "",
                description = PayloadString(e.Payload, "description") ?? "",
                status = e.EventType == "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : "assigned",
                priority = 1,
                risk = "medium",
                success_criteria = Array.Empty<string>(),
                dependencies = Array.Empty<string>(),
                required_capabilities = Array.Empty<string>(),
                created_at = e.Timestamp,
                updated_at = e.Timestamp
            }).ToList();
            return Results.Ok(nodes);
        });

        app.MapPost("/api/v1/runs/{runId}/control", async (string runId, RunControlRequest? request, IFullDuplexRunCoordinator coordinator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            if (request is null || string.IsNullOrWhiteSpace(request.Action)) return Results.BadRequest(new { code = "INVALID_RUN_CONTROL", message = "Action is required." });
            try
            {
                var result = await coordinator.ControlAsync(runGuid, new RunControlCommand(request.Action, request.ClientControlId, request.ExpectedContextRevision), ct);
                return Results.Ok(new { run_id = result.RunId, status = result.Status, action = result.Action, accepted = result.Accepted });
            }
            catch (RunAdmissionException ex)
            {
                return ex.Code switch
                {
                    "RUN_NOT_ACTIVE" => Results.Conflict(new { code = ex.Code, message = ex.Message }),
                    _ => Results.BadRequest(new { code = ex.Code, message = ex.Message })
                };
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "NOT_FOUND", message = ex.Message });
            }
        });

        app.MapGet("/api/v1/runs/{runId}/orchestration", async (string runId, StorageLifecycleService lifecycle, IAgentInstanceService instances, ILifecycleManager manager, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            var run = await lifecycle.FindRunAsync(runGuid, ct);
            if (run is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Run was not found." });
            var frozen = await manager.GetFrozenRunConfigurationAsync(runGuid.ToString(), ct);
            FrozenRunConfigurationV1? parsedFrozen = null;
            if (frozen is not null)
            {
                try { parsedFrozen = System.Text.Json.JsonSerializer.Deserialize<FrozenRunConfigurationV1>(frozen.Content); } catch { }
            }
            var events = (await lifecycle.ReplayEventsAsync(run.SessionId, 0, ct)).Where(e => e.RunId == runGuid.ToString()).ToList();
            var nodes = events.Where(e => e.EventType is "task.dispatched" or "task.assigned" or "worker.completed" or "worker.failed" or "step.result.created").Select(e => new
            {
                id = PayloadString(e.Payload, "task_id") ?? PayloadString(e.Payload, "task_node_id"),
                graph_id = PayloadString(e.Payload, "graph_id") ?? "",
                run_id = runGuid.ToString(),
                session_id = run.SessionId.ToString(),
                title = PayloadString(e.Payload, "title") ?? "",
                description = PayloadString(e.Payload, "description") ?? "",
                status = e.EventType is "worker.completed" or "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : e.EventType is "worker.failed" ? "failed" : "assigned",
                priority = 1,
                risk = PayloadString(e.Payload, "risk") ?? "medium",
                success_criteria = Array.Empty<string>(),
                dependencies = PayloadArray(e.Payload, "dependencies"),
                required_capabilities = Array.Empty<string>(),
                created_at = e.Timestamp,
                updated_at = e.Timestamp
            }).ToList();
            var stepResults = events.Where(e => e.EventType is "worker.completed" or "worker.failed" or "step.result.created").Select(e => new
            {
                id = PayloadString(e.Payload, "task_id") ?? PayloadString(e.Payload, "task_node_id"),
                run_id = runGuid.ToString(),
                task_node_id = PayloadString(e.Payload, "task_id") ?? PayloadString(e.Payload, "task_node_id"),
                agent_id = PayloadString(e.Payload, "agent_instance_id") ?? PayloadString(e.Payload, "agent_id") ?? "",
                status = PayloadString(e.Payload, "status") ?? (e.EventType == "worker.failed" ? "failed" : "completed"),
                summary = PayloadString(e.Payload, "summary") ?? PayloadString(e.Payload, "decision") ?? "",
                evidence = PayloadArray(e.Payload, "evidence"),
                created_at = e.Timestamp
            }).ToList();
            var supervision = events.Where(e => e.EventType is "supervision.requested" or "supervision.completed").Select(e => new
            {
                id = e.EventId,
                run_id = runGuid.ToString(),
                decision = PayloadString(e.Payload, "decision"),
                revision_round = PayloadInt(e.Payload, "revision_round") ?? 0,
                reason = PayloadString(e.Payload, "reason"),
                created_at = e.Timestamp
            }).ToList();
            var agentInstances = (await instances.ListByRunAsync(runGuid, ct)).Select(a => new
            {
                id = a.Id,
                run_id = a.RunId,
                parent_instance_id = a.ParentInstanceId,
                task_id = a.TaskId,
                layer = a.Layer,
                role = a.Role,
                generation_depth = a.GenerationDepth,
                generated = a.Generated,
                status = a.Status,
                capabilities = a.Capabilities,
                allowed_tools = a.AllowedTools,
                created_at = a.CreatedAt,
                released_at = (DateTimeOffset?)null
            }).ToList();
            return Results.Json(new
            {
                run = new
                {
                    id = run.Id.ToString(),
                    session_id = run.SessionId.ToString(),
                    user_message_id = run.TriggerMessageId.ToString(),
                    turn_id = run.TurnId?.ToString(),
                    status = run.Status,
                    application_mode = run.ApplicationMode,
                    agent_mode = run.AgentMode,
                    runtime_profile_id = run.RuntimeProfileId,
                    mode_id = parsedFrozen?.ApplicationMode ?? run.ApplicationMode,
                    agent_profile_id = parsedFrozen?.RuntimeProfileId ?? run.RuntimeProfileId,
                    config_version = run.ConfigurationVersion,
                    config_hash = run.FrozenConfigurationHash ?? run.ConfigurationHash,
                    context_revision = run.ContextRevision,
                    summary = run.Summary ?? "",
                    created_at = run.CreatedAt,
                    updated_at = run.UpdatedAt
                },
                frozen = parsedFrozen is null ? null : new
                {
                    schema_version = parsedFrozen.SchemaVersion,
                    baseline_hash = parsedFrozen.BaselineHash,
                    baseline_version = parsedFrozen.BaselineVersion,
                    application_mode = parsedFrozen.ApplicationMode,
                    agent_mode = parsedFrozen.AgentMode,
                    runtime_profile_id = parsedFrozen.RuntimeProfileId,
                    mode_id = parsedFrozen.ApplicationMode,
                    agent_profile_id = parsedFrozen.RuntimeProfileId,
                    permission_mode = parsedFrozen.PermissionMode,
                    config_version = parsedFrozen.BaselineVersion,
                    config_hash = parsedFrozen.ContentHash,
                    tool_manifest_hash = parsedFrozen.ToolManifestHash,
                    tool_manifest_protocol_version = parsedFrozen.ToolManifestProtocolVersion,
                    bindings = parsedFrozen.Bindings
                },
                graph = (object?)null,
                nodes,
                assignments = Array.Empty<object>(),
                step_results = stepResults,
                agent_instances = agentInstances,
                supervision_findings = supervision,
                context_packs = events.Where(e => e.EventType == "context.packed").Select(e => new
                {
                    id = e.EventId,
                    run_id = runGuid.ToString(),
                    evidence_count = PayloadInt(e.Payload, "evidence_count") ?? 0,
                    estimated_tokens = PayloadInt(e.Payload, "estimated_tokens") ?? 0,
                    token_budget = PayloadInt(e.Payload, "token_budget") ?? 0,
                    sources = PayloadArray(e.Payload, "sources"),
                    created_at = e.Timestamp
                }).ToList()
            }, Options);
        });

        app.MapGet("/api/v1/runs/{runId}/agent-lineage", async (string runId, IAgentInstanceService instances, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            var rows = await instances.ListByRunAsync(runGuid, ct);
            return Results.Ok(rows.Select(a => new
            {
                id = a.Id,
                run_id = a.RunId,
                parent_instance_id = a.ParentInstanceId,
                task_id = a.TaskId,
                layer = a.Layer,
                role = a.Role,
                generation_depth = a.GenerationDepth,
                generated = a.Generated,
                status = a.Status,
                capabilities = a.Capabilities,
                allowed_tools = a.AllowedTools,
                allowed_resources = a.AllowedResources,
                budget_tokens = a.BudgetTokens,
                created_at = a.CreatedAt,
                updated_at = a.UpdatedAt
            }));
        });

        app.MapGet("/api/v1/sessions/{sessionId}/context-versions", async (string sessionId, string? run_id, string? runId, int? limit, IConversationStore conversations, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (await sessions.FindAsync(sessionGuid, ct) is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            Guid? runFilter = null;
            var selected = runId ?? run_id;
            if (selected is not null)
            {
                if (!Guid.TryParse(selected, out var parsed)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
                runFilter = parsed;
            }
            var versions = await conversations.ListContextVersionsAsync(sessionGuid, runFilter, limit ?? 50, ct);
            return Results.Ok(versions.Select(v => new
            {
                id = v.Id,
                session_id = v.SessionId,
                run_id = v.RunId,
                revision = v.Revision,
                kind = v.Kind,
                status = v.Status,
                base_revision = v.BaseRevision,
                created_at = v.CreatedAt
            }));
        });

        app.MapGet("/api/v1/application-modes", (IAgentRuntimeConfiguration configuration) =>
            Results.Ok(configuration.Current.ApplicationModes.Values.Select(m => new
            {
                id = m.Id,
                default_agent_mode = m.DefaultAgentMode,
                allowed_agent_modes = m.AllowedAgentModes
            })));

        app.MapGet("/api/v1/agent-modes", (string? application_mode, IAgentRuntimeConfiguration configuration) =>
        {
            var snapshot = configuration.Current;
            var appId = AgentRuntimeConfigurationSnapshot.NormalizeApplicationMode(application_mode);
            if (!snapshot.ApplicationModes.TryGetValue(appId, out var mode))
                return Results.BadRequest(new { code = "UNKNOWN_APPLICATION_MODE", message = $"Application mode '{appId}' is not configured." });
            return Results.Ok(mode.AllowedAgentModes.Select(id => new
            {
                id,
                display_name = id switch
                {
                    "plan" => "Plan",
                    "spec" => "Spec",
                    "ask" => "Ask",
                    "vibe" => "Vibe",
                    "auto" => "Auto",
                    "agent" => "Agent",
                    _ => id
                },
                summary = $"Agent mode '{id}' in application mode '{appId}'",
                application_mode = appId,
                is_default = string.Equals(id, mode.DefaultAgentMode, StringComparison.OrdinalIgnoreCase),
                max_parallel_executors = snapshot.Spawn.MaxParallelWorkers,
                worktree_isolation = false,
                approval_required = true,
                budget_policy = "bounded",
                runtime_profile_id = mode.Bindings.TryGetValue(id, out var profileId) ? profileId : null,
                operation_agents = mode.Bindings.TryGetValue(id, out var pid) && snapshot.Profiles.TryGetValue(pid, out var profile) ? profile.OperationAgents : (IReadOnlyList<string>)Array.Empty<string>(),
                execution_agents = mode.Bindings.TryGetValue(id, out var pid2) && snapshot.Profiles.TryGetValue(pid2, out var profile2) ? profile2.ExecutionAgents : (IReadOnlyList<string>)Array.Empty<string>(),
                activation_policy = mode.Bindings.TryGetValue(id, out var pid3) && snapshot.Profiles.TryGetValue(pid3, out var profile3) ? profile3.ActivationPolicy : null
            }));
        });

        // Read-only catalog of built-in runtime agents (dual-layer composition). Remains thin: policy stays in Core.
        app.MapGet("/api/v1/agents/catalog", (IAgentRuntimeConfiguration configuration) =>
        {
            var snapshot = configuration.Current;
            return Results.Ok(snapshot.Agents.Values.Select(a => new
            {
                id = a.Id,
                layer = a.Layer,
                role = a.Role,
                lifecycle = a.Lifecycle,
                prompt_profile = a.PromptProfile,
                capabilities = a.Capabilities,
                allowed_tools = a.AllowedTools,
                context_access = a.ContextAccess,
                direct_user_output = a.DirectUserOutput,
                triggers = a.Triggers,
                accepts = a.Accepts,
                emits = a.Emits,
                decisions = a.Decisions,
                memory_write_policy = a.MemoryWritePolicy
            }));
        });

        app.MapPost("/api/v1/runs/{runId}/agents/spawn", async (string runId, HttpRequest request, IAgentInstanceService instances, ILifecycleManager lifecycle, IAgentRuntimeConfiguration configuration, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            var run = await lifecycle.FindAsync(runGuid, ct).ConfigureAwait(false);
            if (run is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Run was not found." });
            if (run.Status is "completed" or "failed" or "cancelled") return Results.Conflict(new { code = "RUN_TERMINAL", message = "Run is already terminal." });
            JsonElement body;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct); } catch { return Results.BadRequest(new { code = "INVALID_PAYLOAD", message = "Body must be valid JSON." }); }
            if (!body.TryGetProperty("parent_instance_id", out var pid) || !Guid.TryParse(pid.GetString(), out var parentId)) return Results.BadRequest(new { code = "INVALID_PARENT", message = "parent_instance_id is required." });
            if (!body.TryGetProperty("goal", out var goal) || string.IsNullOrWhiteSpace(goal.GetString())) return Results.BadRequest(new { code = "INVALID_GOAL", message = "goal is required." });
            var intentRaw = body.TryGetProperty("intent", out var intentEl) ? intentEl.GetString()?.Trim().ToLowerInvariant() : "temporary";
            var intent = intentRaw switch { "persistent_candidate" or "persistent" => AgentCreationIntent.PersistentCandidate, "persistent_profile" or "profile" => AgentCreationIntent.PersistentProfile, _ => AgentCreationIntent.Temporary };
            var role = body.TryGetProperty("role", out var roleEl) ? roleEl.GetString() ?? "worker" : "worker";
            var tools = body.TryGetProperty("allowed_tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array ? toolsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray() : Array.Empty<string>();
            var resources = body.TryGetProperty("allowed_resources", out var resEl) && resEl.ValueKind == JsonValueKind.Array ? resEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray() : Array.Empty<string>();
            var success = body.TryGetProperty("success_criteria", out var scEl) && scEl.ValueKind == JsonValueKind.Array ? scEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray() : Array.Empty<string>();
            var selectors = body.TryGetProperty("context_selectors", out var csEl) && csEl.ValueKind == JsonValueKind.Array ? csEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray() : Array.Empty<string>();
            var modelPurpose = body.TryGetProperty("model_route_purpose", out var mpEl) ? mpEl.GetString() : null;
            var budget = body.TryGetProperty("budget_tokens", out var bEl) && bEl.TryGetInt32(out var bv) ? bv : 0;
            try
            {
                var limits = new AgentSpawnLimits(configuration.Current.Spawn.MaxDepth, configuration.Current.Spawn.MaxAgentsPerRun, configuration.Current.Spawn.MaxParallelWorkers);
                var created = await instances.SpawnAsync(new AgentSpawnRequest(parentId, goal.GetString()!, success, selectors, modelPurpose, tools, resources, budget, null, role, limits, intent), ct);
                return Results.Created($"/api/v1/runs/{runId}/agent-lineage/{created.Id}", new { id = created.Id, run_id = created.RunId, intent = intentRaw, layer = created.Layer, role = created.Role, generated = created.Generated, status = created.Status });
            }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { code = "FORBIDDEN_SPAWN", message = ex.Message }, statusCode: 403); }
            catch (InvalidOperationException ex) { return Results.Json(new { code = "SPAWN_LIMIT", message = ex.Message }, statusCode: 409); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { code = "NOT_FOUND", message = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_SPAWN", message = ex.Message }); }
        });

        return app;
    }

    private static async Task WriteChunkAsync(HttpContext context, long sequence, object chunk, CancellationToken ct)
    {
        await context.Response.WriteAsync($"id: {sequence}\ndata: {JsonSerializer.Serialize(chunk)}\n\n", ct);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static JsonElement PayloadRoot(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("payload", out var inner) && inner is JsonElement { ValueKind: JsonValueKind.Object } obj) return obj;
        return default;
    }

    private static string? PayloadString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static IReadOnlyList<string> PayloadArray(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        return [];
    }

    private static int? PayloadInt(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var value))
            return value;
        return null;
    }
}
