using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Runtime;
using TinadecCore.Tools;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// DmaEA runtime endpoints: full-duplex SSE invoke-stream, run control, run-scoped
/// projections, agent lineage, context versions, and TOML-driven mode catalogs.
/// The coordinator appends the user message (Desktop never double-writes) and keeps
/// running after a subscriber disconnects. No fake success: without a chat route the
/// invoke-stream returns an error chunk and the run records a run.failed event.
/// </summary>
public static class DmaeaEndpoints
{
    public static IEndpointRouteBuilder MapDmaeaEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /sessions/{sessionId}/invoke-stream was retired (plan §4.3 item 4):
        // the durable admission path is POST /sessions/{id}/interactions followed by
        // GET /runs/{runId}/stream; no Desktop consumer remains on the legacy wire.

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
                    if (chunk.Kind is "heartbeat")
                    {
                        // Idle keep-alive as an SSE comment: never advances the
                        // client cursor and every parser ignores it by spec.
                        await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        continue;
                    }
                    await WriteChunkAsync(context, chunk.Seq, chunk.Kind, chunk.OccurredAt == default
                        ? DateTimeOffset.UtcNow
                        : chunk.OccurredAt, new
                    {
                        run_id = chunk.RunId,
                        turn_id = chunk.TurnId,
                        message_id = chunk.MessageId,
                        seq = chunk.Seq,
                        purpose = "dual_layer",
                        kind = chunk.Kind,
                        occurred_at = (chunk.OccurredAt == default ? DateTimeOffset.UtcNow : chunk.OccurredAt).ToString("o"),
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

        app.MapGet("/api/v1/sessions/{sessionId}/orchestration", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            var session = await sessions.FindAsync(sessionGuid, ct);
            if (session is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });

            // Declared graph (additive-only): the published mode-version snapshot is
            // the base layer, even before any run exists — what the UI draws is what
            // the engine walks. Observed flows come from the durable task graph.
            var declaredGraph = (object?)null;
            if (session.ModeVersionId is { } modeVersionId)
            {
                await using var cfg = await cfgFactory.CreateDbContextAsync(ct);
                var snapshotJson = await cfg.ModeVersions.AsNoTracking()
                    .Where(x => x.Id == modeVersionId && x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId)
                    .Select(x => x.SnapshotJson)
                    .FirstOrDefaultAsync(ct);
                declaredGraph = OrchestrationGraphProjection.FromSnapshot(snapshotJson);
            }

            var runs = await lifecycle.ListRunsAsync(sessionGuid, ct);
            var run = runs.FirstOrDefault();
            if (run is null) return Results.Json(new { run = (object?)null, graph = declaredGraph, nodes = Array.Empty<object>(), lanes = Array.Empty<object>(), flows = Array.Empty<object>(), assignments = Array.Empty<object>(), step_results = Array.Empty<object>(), context_packs = Array.Empty<object>(), supervision_findings = Array.Empty<object>() }, options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var checkpointRow = await lifecycle.GetCurrentRunCheckpointAsync(run.Id, ct);
            FullDuplexCheckpointV1? checkpoint = null;
            if (checkpointRow is not null)
            {
                try { checkpoint = System.Text.Json.JsonSerializer.Deserialize<FullDuplexCheckpointV1>(checkpointRow.Content, CheckpointJsonOptions); } catch { }
            }
            static string LaneOfTaskSession(DurableTaskNode task) =>
                string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey.Trim();
            var taskLaneById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (checkpoint is not null)
            {
                foreach (var task in checkpoint.Tasks)
                {
                    taskLaneById.TryAdd(task.TaskKey, LaneOfTaskSession(task));
                    taskLaneById.TryAdd(task.TaskId.ToString(), LaneOfTaskSession(task));
                }
            }
            var lanes = checkpoint is null
                ? new List<object>()
                : (checkpoint.Lanes.Count > 0
                    ? checkpoint.Lanes.Select(l => (object)new
                    {
                        lane_key = l.LaneKey,
                        status = l.Status,
                        escalated = l.Escalated,
                        task_keys = checkpoint.Tasks.Where(t => LaneOfTaskSession(t) == l.LaneKey).Select(t => t.TaskKey).ToList(),
                        waits = checkpoint.Tasks.Where(t => LaneOfTaskSession(t) == l.LaneKey)
                            .SelectMany(t => t.Waits.Select(w => new
                            {
                                waiting_task = t.TaskKey,
                                lane = w.LaneKey,
                                predicate = w.Predicate,
                                required_criteria = w.RequiredCriteria,
                                facts_hash = w.ObservedFactsHash
                            })).ToList()
                    }).ToList()
                    : new List<object>
                    {
                        new
                        {
                            lane_key = "main",
                            status = checkpoint.Phase,
                            escalated = false,
                            task_keys = checkpoint.Tasks.Select(t => t.TaskKey).ToList(),
                            waits = Array.Empty<object>()
                        }
                    });
            var nodes = events.Where(e => e.EventType is "task.assigned" or "step.result.created" or "worker.completed" or "worker.blocked" or "worker.failed")
                .Select(e =>
                {
                    var nodeId = PayloadString(e.Payload, "task_id") ?? PayloadString(e.Payload, "task_node_id") ?? "";
                    return new
                    {
                        id = nodeId,
                        graph_id = PayloadString(e.Payload, "graph_id"),
                        run_id = run.Id.ToString(),
                        session_id = sessionId,
                        title = PayloadString(e.Payload, "title") ?? "",
                        description = PayloadString(e.Payload, "description") ?? "",
                        status = e.EventType switch
                        {
                            "worker.failed" => "failed",
                            "worker.blocked" => "blocked",
                            "worker.completed" or "step.result.created" => PayloadString(e.Payload, "status") ?? "completed",
                            _ => "assigned"
                        },
                        lane_key = taskLaneById.TryGetValue(nodeId, out var nodeLane) ? nodeLane : "main",
                        priority = 1,
                        risk = "medium",
                        success_criteria = Array.Empty<string>(),
                        dependencies = Array.Empty<string>(),
                        required_capabilities = Array.Empty<string>(),
                        created_at = e.Timestamp,
                        updated_at = e.Timestamp
                    };
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
                graph = declaredGraph,
                nodes,
                lanes,
                flows = OrchestrationGraphProjection.FlowsFromCheckpoint(checkpoint, session.ConversationTemplateSlug),
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
            var items = BuildToolExecutionTimeline(events, sessionId);
            return Results.Ok(items);
        });

        app.MapGet("/api/v1/sessions/{sessionId}/task-nodes", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (await sessions.FindAsync(sessionGuid, ct) is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var nodes = events.Where(e => e.EventType is "task.assigned" or "step.result.created" or "worker.completed" or "worker.blocked" or "worker.failed").Select(e => new
            {
                id = PayloadString(e.Payload, "task_id") ?? PayloadString(e.Payload, "task_node_id"),
                graph_id = PayloadString(e.Payload, "graph_id") ?? "",
                run_id = PayloadString(e.Payload, "run_id") ?? "",
                session_id = sessionId,
                title = PayloadString(e.Payload, "title") ?? "",
                description = PayloadString(e.Payload, "description") ?? "",
                status = e.EventType switch
                {
                    "worker.failed" => "failed",
                    "worker.blocked" => "blocked",
                    "worker.completed" or "step.result.created" => PayloadString(e.Payload, "status") ?? "completed",
                    _ => "assigned"
                },
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

        app.MapPost("/api/v1/runs/{runId}/control", async (string runId, RunControlRequest? request, IFullDuplexRunCoordinator coordinator, ITerminalSessionControl terminalSessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            if (request is null || string.IsNullOrWhiteSpace(request.Action)) return Results.BadRequest(new { code = "INVALID_RUN_CONTROL", message = "Action is required." });
            try
            {
                var result = await coordinator.ControlAsync(runGuid, new RunControlCommand(request.Action, request.ClientControlId, request.ExpectedContextRevision), ct);
                // A cancelled run must not leave long-lived terminal processes behind.
                var killedSessions = 0;
                if (string.Equals(request.Action, "cancel", StringComparison.OrdinalIgnoreCase) && result.Accepted)
                {
                    killedSessions = await terminalSessions.KillForRunAsync(runGuid, ct).ConfigureAwait(false);
                }
                return Results.Ok(new { run_id = result.RunId, status = result.Status, action = result.Action, accepted = result.Accepted, killed_terminal_sessions = killedSessions });
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

        app.MapGet("/api/v1/runs/{runId}/orchestration", async (string runId, StorageLifecycleService lifecycle, IAgentInstanceService instances, ILifecycleManager manager, IDbContextFactory<AgentConfigurationDbContext> agentConfigFactory, ProjectSessionStore sessionStore, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            var run = await lifecycle.FindRunAsync(runGuid, ct);
            if (run is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Run was not found." });

            // Declared graph + observed flows (additive-only): the session's
            // published mode-version snapshot is the base layer; flows come from
            // the durable task graph, the same source /replay rebuilds from. The
            // tier comes from the run's frozen graph section (schema v2 freezes
            // one for every mode).
            var frozen = await manager.GetFrozenRunConfigurationAsync(runGuid.ToString(), ct);
            FrozenRunConfigurationV1? parsedFrozen = null;
            if (frozen is not null)
            {
                try { parsedFrozen = System.Text.Json.JsonSerializer.Deserialize<FrozenRunConfigurationV1>(frozen.Content, CheckpointJsonOptions); } catch { }
            }

            var declaredGraph = (object?)null;
            string? conversationTemplateSlug = null;
            if (await sessionStore.FindAsync(run.SessionId, ct) is { } orchestrationSession)
            {
                conversationTemplateSlug = orchestrationSession.ConversationTemplateSlug;
                if (orchestrationSession.ModeVersionId is { } modeVersionId)
                {
                    await using var graphCfg = await agentConfigFactory.CreateDbContextAsync(ct);
                    var snapshotJson = await graphCfg.ModeVersions.AsNoTracking()
                        .Where(x => x.Id == modeVersionId && x.TenantId == orchestrationSession.TenantId && x.WorkspaceId == orchestrationSession.WorkspaceId)
                        .Select(x => x.SnapshotJson)
                        .FirstOrDefaultAsync(ct);
                    declaredGraph = OrchestrationGraphProjection.FromSnapshot(snapshotJson, parsedFrozen?.Graph?.Tier);
                }
            }
            var checkpointRow = await lifecycle.GetCurrentRunCheckpointAsync(runGuid, ct);
            FullDuplexCheckpointV1? checkpoint = null;
            if (checkpointRow is not null)
            {
                try { checkpoint = System.Text.Json.JsonSerializer.Deserialize<FullDuplexCheckpointV1>(checkpointRow.Content, CheckpointJsonOptions); } catch { }
            }
            static string LaneOfTask(DurableTaskNode task) =>
                string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey.Trim();
            var taskLaneById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (checkpoint is not null)
            {
                foreach (var task in checkpoint.Tasks)
                {
                    taskLaneById.TryAdd(task.TaskKey, LaneOfTask(task));
                    taskLaneById.TryAdd(task.TaskId.ToString(), LaneOfTask(task));
                }
            }
            var events = (await lifecycle.ReplayEventsAsync(run.SessionId, 0, ct)).Where(e => e.RunId == runGuid.ToString()).ToList();
            // Agent display names for the run's instances (plan 配置体验改造 B)：
            // 前端不再显示 role/slug/GUID 兜底。
            var agentInstanceRows = await instances.ListByRunAsync(runGuid, ct);
            Guid[] instanceDefinitionIds = agentInstanceRows.Select(a => a.AgentDefinitionId).Distinct().ToArray();
            Dictionary<Guid, string> agentDisplayNames;
            if (instanceDefinitionIds.Length > 0)
            {
                await using var agentCfg = await agentConfigFactory.CreateDbContextAsync(ct);
                var nameRows = await agentCfg.AgentDefinitions.AsNoTracking()
                    .Where(x => instanceDefinitionIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.DisplayName })
                    .ToListAsync(ct);
                agentDisplayNames = nameRows.ToDictionary(x => x.Id, x => x.DisplayName);
            }
            else agentDisplayNames = new Dictionary<Guid, string>();
            string DisplayNameFor(RuntimeAgentInstance a) =>
                agentDisplayNames.TryGetValue(a.AgentDefinitionId, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : a.Role;
            var instanceNameById = agentInstanceRows.ToDictionary(a => a.Id, DisplayNameFor);
            var nodeAgentNames = agentInstanceRows.Where(a => a.TaskId is not null)
                .GroupBy(a => a.TaskId!.Value)
                .ToDictionary(g => g.Key, g => DisplayNameFor(g.First()));
            var nodes = events.Where(e => e.EventType is "task.dispatched" or "task.assigned" or "worker.completed" or "worker.blocked" or "worker.failed" or "step.result.created").Select(e =>
            {
                var nodeId = PayloadString(e.Payload, "task_id") ?? PayloadString(e.Payload, "task_node_id") ?? "";
                var nodeAgentName = Guid.TryParse(nodeId, out var nodeGuid) && nodeAgentNames.TryGetValue(nodeGuid, out var dn) ? dn : null;
                return new
                {
                    id = nodeId,
                    graph_id = PayloadString(e.Payload, "graph_id") ?? "",
                    run_id = runGuid.ToString(),
                    session_id = run.SessionId.ToString(),
                    title = PayloadString(e.Payload, "title") ?? "",
                    description = PayloadString(e.Payload, "description") ?? "",
                    status = e.EventType switch
                    {
                        "worker.failed" => "failed",
                        "worker.blocked" => "blocked",
                        "worker.completed" or "step.result.created" => PayloadString(e.Payload, "status") ?? "completed",
                        _ => "assigned"
                    },
                    lane_key = taskLaneById.TryGetValue(nodeId, out var nodeLane) ? nodeLane : "main",
                    agent_display_name = nodeAgentName,
                    priority = 1,
                    risk = PayloadString(e.Payload, "risk") ?? "medium",
                    success_criteria = Array.Empty<string>(),
                    dependencies = PayloadArray(e.Payload, "dependencies"),
                    required_capabilities = Array.Empty<string>(),
                    created_at = e.Timestamp,
                    updated_at = e.Timestamp
                };
            }).ToList();
            var lanes = checkpoint is null
                ? new List<object>()
                : (checkpoint.Lanes.Count > 0
                    ? checkpoint.Lanes.Select(l => (object)new
                    {
                        lane_key = l.LaneKey,
                        status = l.Status,
                        escalated = l.Escalated,
                        task_keys = checkpoint.Tasks.Where(t => LaneOfTask(t) == l.LaneKey).Select(t => t.TaskKey).ToList(),
                        waits = checkpoint.Tasks.Where(t => LaneOfTask(t) == l.LaneKey)
                            .SelectMany(t => t.Waits.Select(w => new
                            {
                                waiting_task = t.TaskKey,
                                lane = w.LaneKey,
                                predicate = w.Predicate,
                                required_criteria = w.RequiredCriteria,
                                facts_hash = w.ObservedFactsHash
                            })).ToList()
                    }).ToList()
                    : new List<object>
                    {
                        new
                        {
                            lane_key = "main",
                            status = checkpoint.Phase,
                            escalated = false,
                            task_keys = checkpoint.Tasks.Select(t => t.TaskKey).ToList(),
                            waits = Array.Empty<object>()
                        }
                    });
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
            var assignments = agentInstanceRows.Where(a => a.TaskId is not null).Select(a => new
            {
                task_node_id = a.TaskId!.Value,
                agent_instance_id = a.Id,
                agent_display_name = DisplayNameFor(a),
                role = a.Role,
                status = a.Status
            }).ToList();
            var agentInstancesProjection = agentInstanceRows.Select(a => new
            {
                id = a.Id,
                run_id = a.RunId,
                parent_instance_id = a.ParentInstanceId,
                task_id = a.TaskId,
                layer = a.Layer,
                role = a.Role,
                agent_display_name = DisplayNameFor(a),
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
                    runtime_profile_id = run.RuntimeProfileId,
                    mode_version_id = parsedFrozen?.ModeVersionId,
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
                    runtime_profile_id = parsedFrozen.RuntimeProfileId,
                    mode_version_id = parsedFrozen.ModeVersionId,
                    permission_mode = parsedFrozen.PermissionMode,
                    config_version = parsedFrozen.BaselineVersion,
                    config_hash = parsedFrozen.ContentHash,
                    tool_manifest_hash = parsedFrozen.ToolManifestHash,
                    tool_manifest_protocol_version = parsedFrozen.ToolManifestProtocolVersion,
                    bindings = parsedFrozen.Bindings
                },
                graph = declaredGraph,
                nodes,
                lanes,
                flows = OrchestrationGraphProjection.FlowsFromCheckpoint(checkpoint, conversationTemplateSlug),
                assignments,
                step_results = stepResults,
                agent_instances = agentInstancesProjection,
                supervision_findings = supervision,
                context_packs = events.Where(e => e.EventType == "context.packed").Select(e => new
                {
                    id = e.EventId,
                    run_id = runGuid.ToString(),
                    lane_key = PayloadString(e.Payload, "lane_key"),
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
                agent_definition_id = a.AgentDefinitionId,
                agent_version_id = a.AgentVersionId,
                agent_version_content_hash = a.AgentVersionContentHash,
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

        app.MapPost("/api/v1/runs/{runId}/agents/spawn", async (string runId, HttpRequest request, IAgentInstanceService instances, ILifecycleManager lifecycle, IAgentRuntimeConfiguration configuration, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            RunState run;
            try { run = await lifecycle.GetRunStateAsync(runGuid.ToString(), ct).ConfigureAwait(false); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Run was not found." }); }
            if (run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled) return Results.Conflict(new { code = "RUN_TERMINAL", message = "Run is already terminal." });
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
            catch (AgentPromotionDisabledException ex) { return Results.Conflict(new { code = "candidate_pipeline_required", message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { code = "FORBIDDEN_SPAWN", message = ex.Message }, statusCode: 403); }
            catch (InvalidOperationException ex) { return Results.Json(new { code = "SPAWN_LIMIT", message = ex.Message }, statusCode: 409); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { code = "NOT_FOUND", message = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_SPAWN", message = ex.Message }); }
        });

        return app;
    }

    private static async Task WriteChunkAsync(HttpContext context, long sequence, string kind, DateTimeOffset occurredAt, object chunk, CancellationToken ct)
    {
        // Envelope contract (plan §4.3 item 2): the SSE event: line carries the
        // kind, occurred_at is the durable journal timestamp, and id === seq.
        await context.Response.WriteAsync($"id: {sequence}\nevent: {kind}\ndata: {JsonSerializer.Serialize(chunk)}\n\n", ct);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Checkpoint and frozen-configuration bodies are persisted by the engine
    /// with web (camelCase) options, while the coordinator's admission-time
    /// checkpoint write uses bare defaults; web deserialization is
    /// case-insensitive and reads both shapes. A bare (case-sensitive) read
    /// silently dropped every camelCase property, which emptied the
    /// lanes/task_keys projection.
    /// </summary>
    private static readonly JsonSerializerOptions CheckpointJsonOptions = new(JsonSerializerDefaults.Web);

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

    private static bool? PayloadBool(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return v.GetBoolean();
        return null;
    }

    private static IReadOnlyList<object> BuildToolExecutionTimeline(
        IReadOnlyList<TinadecCore.Contracts.Events.EventEnvelope> events,
        string sessionId)
    {
        var relevant = events.Where(e => e.EventType is "step.result.created" or "task.assigned"
                or "tool.execution.requested" or "tool.execution.completed"
                or "tool.execution.failed" or "tool.execution.outcome_unknown"
                or "approval.requested" or "approval.decided" or "governance.permission_decided")
            .ToList();

        // A policy-level permission decision may only carry permission_request_id.
        // approval.requested is the durable bridge from that request to the concrete
        // tool execution, so build the correlation once before folding the timeline.
        var executionByPermission = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in relevant.Where(e => e.EventType == "approval.requested"))
        {
            var executionId = PayloadString(e.Payload, "execution_id");
            var permissionId = PayloadString(e.Payload, "permission_request_id");
            if (!string.IsNullOrWhiteSpace(executionId) && !string.IsNullOrWhiteSpace(permissionId))
                executionByPermission[permissionId] = executionId;
        }

        var toolRows = new Dictionary<string, ToolTimelineAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in relevant.Where(e => e.EventType is not ("step.result.created" or "task.assigned")))
        {
            var executionId = PayloadString(e.Payload, "execution_id");
            if (string.IsNullOrWhiteSpace(executionId) && e.EventType == "governance.permission_decided")
            {
                var permissionId = PayloadString(e.Payload, "permission_request_id");
                if (!string.IsNullOrWhiteSpace(permissionId)) executionByPermission.TryGetValue(permissionId, out executionId);
            }
            if (string.IsNullOrWhiteSpace(executionId)) continue;

            if (!toolRows.TryGetValue(executionId, out var row))
            {
                row = new ToolTimelineAccumulator
                {
                    Id = executionId,
                    RunId = e.RunId ?? string.Empty,
                    RequestedAt = e.Timestamp,
                    UpdatedAt = e.Timestamp,
                    RequestedSeq = EventSequence(e.Payload),
                    UpdatedSeq = EventSequence(e.Payload)
                };
                toolRows.Add(executionId, row);
            }

            row.RunId = string.IsNullOrWhiteSpace(e.RunId) ? row.RunId : e.RunId;
            row.ToolId = PayloadString(e.Payload, "tool_id") ?? row.ToolId;
            row.Risk = PayloadString(e.Payload, "risk") ?? row.Risk;
            row.RequiresApproval = row.RequiresApproval
                || PayloadBool(e.Payload, "requires_approval") == true
                || e.EventType is "approval.requested" or "approval.decided" or "governance.permission_decided";

            var approvalId = PayloadString(e.Payload, "approval_id");
            if (e.EventType == "approval.requested" && !string.IsNullOrWhiteSpace(approvalId)) row.ApprovalId = approvalId;
            else if (string.IsNullOrWhiteSpace(row.ApprovalId))
                row.ApprovalId = approvalId ?? PayloadString(e.Payload, "permission_request_id");

            var summary = EventSummary(e.Payload);
            if (!string.IsNullOrWhiteSpace(summary)) row.Summary = summary;
            var toolSuccess = PayloadBool(e.Payload, "tool_success");
            if (toolSuccess is not null) row.ToolSuccess = toolSuccess;

            row.Status = e.EventType switch
            {
                "tool.execution.requested" => "requested",
                "approval.requested" => "waiting_approval",
                "approval.decided" => string.Equals(PayloadString(e.Payload, "decision"), "approved", StringComparison.OrdinalIgnoreCase)
                    ? "running"
                    : "failed",
                "governance.permission_decided" => string.Equals(PayloadString(e.Payload, "outcome"), "allowed", StringComparison.OrdinalIgnoreCase)
                    ? "running"
                    : "failed",
                "tool.execution.completed" => "completed",
                "tool.execution.failed" => "failed",
                "tool.execution.outcome_unknown" => "outcome_unknown",
                _ => row.Status
            };

            if (!row.EventTypes.Contains(e.EventType, StringComparer.Ordinal)) row.EventTypes.Add(e.EventType);
            var sequence = EventSequence(e.Payload);
            if (sequence > 0 && (row.RequestedSeq <= 0 || sequence < row.RequestedSeq)) row.RequestedSeq = sequence;
            if (sequence > row.UpdatedSeq) row.UpdatedSeq = sequence;
            if (e.Timestamp < row.RequestedAt) row.RequestedAt = e.Timestamp;
            if (e.Timestamp > row.UpdatedAt) row.UpdatedAt = e.Timestamp;
        }

        var projected = new List<(long Sequence, object Item)>();
        foreach (var e in relevant.Where(e => e.EventType is "step.result.created" or "task.assigned"))
        {
            var sequence = EventSequence(e.Payload);
            projected.Add((sequence, new
            {
                id = PayloadString(e.Payload, "task_node_id"),
                run_id = PayloadString(e.Payload, "run_id") ?? string.Empty,
                session_id = sessionId,
                tool_id = PayloadString(e.Payload, "tool_id") ?? string.Empty,
                tool_display_name = string.Empty,
                source = "dmaea",
                provider_layer = "execution",
                risk = "medium",
                requires_approval = false,
                status = e.EventType == "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : "pending",
                approval_id = (string?)null,
                step_result_id = PayloadString(e.Payload, "task_node_id"),
                summary = PayloadString(e.Payload, "summary") ?? string.Empty,
                evidence = PayloadArray(e.Payload, "evidence"),
                requested_at = e.Timestamp,
                updated_at = e.Timestamp,
                duration_ms = 0L,
                requested_seq = sequence,
                updated_seq = sequence,
                event_types = new[] { e.EventType },
                checkpoint_summary = string.Empty,
                tool_success = (bool?)null
            }));
        }

        foreach (var row in toolRows.Values)
        {
            projected.Add((row.RequestedSeq, new
            {
                id = row.Id,
                run_id = row.RunId,
                session_id = sessionId,
                tool_id = row.ToolId,
                tool_display_name = string.Empty,
                source = "dmaea",
                provider_layer = "execution",
                risk = row.Risk,
                requires_approval = row.RequiresApproval,
                status = row.Status,
                approval_id = row.ApprovalId,
                step_result_id = (string?)null,
                summary = row.Summary,
                evidence = Array.Empty<string>(),
                requested_at = row.RequestedAt,
                updated_at = row.UpdatedAt,
                duration_ms = Math.Max(0L, (long)(row.UpdatedAt - row.RequestedAt).TotalMilliseconds),
                requested_seq = row.RequestedSeq,
                updated_seq = row.UpdatedSeq,
                event_types = row.EventTypes.ToArray(),
                checkpoint_summary = string.Empty,
                tool_success = row.ToolSuccess
            }));
        }

        return projected.OrderBy(item => item.Sequence).Select(item => item.Item).ToList();
    }

    private static long EventSequence(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("sequence", out var value))
        {
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is JsonElement { ValueKind: JsonValueKind.Number } json && json.TryGetInt64(out var parsed)) return parsed;
        }
        return 0L;
    }

    private static string EventSummary(IReadOnlyDictionary<string, object?> payload) =>
        payload.TryGetValue("summary", out var value)
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
                _ => string.Empty
            }
            : string.Empty;

    private sealed class ToolTimelineAccumulator
    {
        public string Id { get; init; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string ToolId { get; set; } = string.Empty;
        public string Risk { get; set; } = "medium";
        public bool RequiresApproval { get; set; }
        public string Status { get; set; } = "requested";
        public string? ApprovalId { get; set; }
        public string Summary { get; set; } = string.Empty;
        public bool? ToolSuccess { get; set; }
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public long RequestedSeq { get; set; }
        public long UpdatedSeq { get; set; }
        public List<string> EventTypes { get; } = [];
    }
}
