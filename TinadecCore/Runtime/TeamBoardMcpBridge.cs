using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Tools;

namespace TinadecCore.Runtime;

/// <summary>
/// Issues cryptographically random per-teammate scoped MCP bearer tokens and executes ONLY the
/// run-scoped shared-board &amp; read-only recall tools (<c>team_message</c>, <c>team_plan</c>,
/// <c>recall_evidence</c>, <c>recall_conversation_history</c>, <c>graph_view</c>), strictly rejecting
/// all <c>tinadec_*</c> self-configuration calls (#36).
/// </summary>
public sealed class TeamBoardMcpBridge : ITeamBoardMcpBridge
{
    private static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromHours(4);
    private static readonly TimeSpan MinLeaseTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLeaseTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, ScopedTeamMcpLease> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Func<TeamBoardToolCallContext, CancellationToken, Task<TeamBoardToolCallResult>>> _runHandlers = new();

    public TeamBoardMcpBridge(IServiceProvider services) => _services = services;

    public ScopedTeamMcpLease IssueToken(
        Guid tenantId,
        Guid? workspaceId,
        Guid sessionId,
        Guid runId,
        string teammateHandle,
        IReadOnlyList<string>? allowedTools = null,
        TimeSpan? ttl = null,
        Guid? projectId = null,
        Guid principalId = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("sessionId is required.", nameof(sessionId));
        if (runId == Guid.Empty) throw new ArgumentException("runId is required.", nameof(runId));
        ArgumentException.ThrowIfNullOrWhiteSpace(teammateHandle);

        PruneExpiredLeases();

        var effectiveTools = NormalizeAllowedTools(allowedTools);
        var duration = ttl is { } rawTtl
            ? TimeSpan.FromMilliseconds(Math.Clamp(rawTtl.TotalMilliseconds, MinLeaseTtl.TotalMilliseconds, MaxLeaseTtl.TotalMilliseconds))
            : DefaultLeaseTtl;

        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var token = "tmb_" + Base64UrlEncode(randomBytes);
        var expiresAt = DateTimeOffset.UtcNow.Add(duration);

        var lease = new ScopedTeamMcpLease(
            Token: token,
            TenantId: tenantId,
            WorkspaceId: workspaceId,
            SessionId: sessionId,
            RunId: runId,
            TeammateHandle: teammateHandle.Trim(),
            AllowedTools: effectiveTools,
            ExpiresAt: expiresAt)
        {
            ProjectId = projectId,
            PrincipalId = principalId,
        };

        _leases[token] = lease;
        return lease;
    }

    public int RevokeRunTokens(Guid runId)
    {
        var removed = 0;
        foreach (var kvp in _leases)
        {
            if (kvp.Value.RunId == runId && _leases.TryRemove(kvp.Key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    public ScopedTeamMcpLease? ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = StripBearerPrefix(token);
        if (string.IsNullOrEmpty(normalized))
            return null;

        if (!_leases.TryGetValue(normalized, out var lease))
            return null;

        var expectedBytes = Encoding.UTF8.GetBytes(lease.Token);
        var actualBytes = Encoding.UTF8.GetBytes(normalized);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            return null;

        if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _leases.TryRemove(normalized, out _);
            return null;
        }

        return lease;
    }

    public IReadOnlyList<ToolManifestEntryDto> ListScopedTools(ScopedTeamMcpLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var entries = new List<ToolManifestEntryDto>(lease.AllowedTools.Count);

        foreach (var toolId in lease.AllowedTools)
        {
            if (CoreVirtualToolPolicy.IsTinadecConfig(toolId))
                continue;

            var descriptor = ResolveScopedToolManifestEntry(toolId);
            if (descriptor is not null)
                entries.Add(descriptor);
        }

        return entries;
    }

    public async Task<ToolWireResponseDto> ExecuteScopedToolAsync(
        ScopedTeamMcpLease lease,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var callId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (string.IsNullOrWhiteSpace(toolName))
            return Fail(callId, "Tool name is required.");

        var normalizedTool = toolName.Trim();
        if (CoreVirtualToolPolicy.IsTinadecConfig(normalizedTool))
        {
            return Fail(callId, $"Tool '{normalizedTool}' is forbidden: scoped teammate MCP tokens cannot invoke tinadec_* self-configuration tools.");
        }

        if (!ITeamBoardMcpBridge.DefaultAllowedTools.Contains(normalizedTool, StringComparer.OrdinalIgnoreCase)
            || !lease.AllowedTools.Contains(normalizedTool, StringComparer.OrdinalIgnoreCase))
        {
            return Fail(callId, $"Tool '{normalizedTool}' is not authorized in scoped MCP lease for teammate '{lease.TeammateHandle}'.");
        }

        if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _leases.TryRemove(lease.Token, out _);
            return Fail(callId, "Scoped teammate MCP token has expired.");
        }

        var args = arguments.ValueKind == JsonValueKind.Object ? arguments : EmptyObject();

        if (CoreVirtualToolPolicy.IsTeamMessage(normalizedTool)
            || CoreVirtualToolPolicy.IsTeamPlan(normalizedTool)
            || TeamTaskBoardProtocol.IsTeamTasks(normalizedTool))
        {
            return await ExecuteBoardToolAsync(lease, callId, normalizedTool.ToLowerInvariant(), args, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsRecallEvidence(normalizedTool))
        {
            return await ExecuteRecallEvidenceAsync(lease, callId, args, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsRecallHistory(normalizedTool))
        {
            return await ExecuteRecallHistoryAsync(lease, callId, args, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsGraphView(normalizedTool))
        {
            return await ExecuteGraphViewAsync(lease, callId, args, cancellationToken).ConfigureAwait(false);
        }

        return Fail(callId, $"Unsupported scoped tool '{normalizedTool}'.");
    }

    public IDisposable RegisterRunBoardHandler(
        Guid runId,
        Func<TeamBoardToolCallContext, CancellationToken, Task<TeamBoardToolCallResult>> handler)
    {
        if (runId == Guid.Empty) throw new ArgumentException("runId is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(handler);

        _runHandlers[runId] = handler;
        return new HandlerRegistration(_runHandlers, runId, handler);
    }

    private async Task<ToolWireResponseDto> ExecuteBoardToolAsync(
        ScopedTeamMcpLease lease,
        long callId,
        string canonicalTool,
        JsonElement args,
        CancellationToken ct)
    {
        var context = new TeamBoardToolCallContext(
            TenantId: lease.TenantId,
            WorkspaceId: lease.WorkspaceId,
            SessionId: lease.SessionId,
            RunId: lease.RunId,
            TeammateHandle: lease.TeammateHandle,
            ToolName: canonicalTool,
            Arguments: args);

        if (_runHandlers.TryGetValue(lease.RunId, out var handler))
        {
            var handled = await handler(context, ct).ConfigureAwait(false);
            return handled.Success
                ? new ToolWireResponseDto
                {
                    CallId = callId,
                    IsSuccess = true,
                    Result = handled.Result ?? JsonSerializer.SerializeToElement(new { ok = true, tool = canonicalTool, teammate = lease.TeammateHandle }, SnakeJson),
                }
                : Fail(callId, handled.Error ?? $"Board tool '{canonicalTool}' failed.");
        }

        if (_services.GetService<ITeamRunBoardActionSink>() is { } sink)
        {
            var sinkResult = await sink.ExecuteBoardActionAsync(context, ct).ConfigureAwait(false);
            return sinkResult.Success
                ? new ToolWireResponseDto
                {
                    CallId = callId,
                    IsSuccess = true,
                    Result = sinkResult.Result ?? JsonSerializer.SerializeToElement(new { ok = true, tool = canonicalTool, teammate = lease.TeammateHandle }, SnakeJson),
                }
                : Fail(callId, sinkResult.Error ?? $"Board tool '{canonicalTool}' failed.");
        }

        // Durable fallback when no in-memory handler is registered yet: record on lifecycle/evidence and wake the run!
        return await RecordDurableBoardFallbackAsync(lease, callId, canonicalTool, args, ct).ConfigureAwait(false);
    }

    private async Task<ToolWireResponseDto> RecordDurableBoardFallbackAsync(
        ScopedTeamMcpLease lease,
        long callId,
        string canonicalTool,
        JsonElement args,
        CancellationToken ct)
    {
        var workspaceId = ResolveWorkspaceId(lease);
        if (CoreVirtualToolPolicy.IsTeamMessage(canonicalTool))
        {
            var target = args.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;
            var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() : null;
            var kind = args.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(k.GetString())
                ? k.GetString()!.Trim()
                : "update";
            var wake = !args.TryGetProperty("wake", out var w) || w.ValueKind != JsonValueKind.False;

            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(content))
                return Fail(callId, "team_message requires non-empty 'target' and 'content'.");

            if (_services.GetService<IEvidenceArchive>() is { } archive)
            {
                await archive.AppendAsync(new EvidenceEntry(
                    lease.TenantId,
                    workspaceId,
                    lease.SessionId,
                    lease.RunId,
                    TaskId: null,
                    Kind: EvidenceKinds.MemberTurn,
                    Title: $"[{kind}] {lease.TeammateHandle} -> {target}",
                    Author: lease.TeammateHandle,
                    Content: content,
                    SourceKey: $"mcp-board-msg:{lease.RunId:N}:{lease.TeammateHandle}:{callId}"), ct).ConfigureAwait(false);
            }

            if (_services.GetService<ILifecycleManager>() is { } lifecycle)
            {
                await lifecycle.AppendEventAsync(
                    lease.RunId,
                    "team.message",
                    new
                    {
                        from = lease.TeammateHandle,
                        target,
                        kind,
                        content,
                        wake,
                        source = "mcp",
                    },
                    $"[{lease.TeammateHandle} -> {target}] ({kind}): {(content.Length > 160 ? content[..160] + "…" : content)}",
                    toolId: CoreVirtualToolPolicy.TeamMessageToolId,
                    cancellationToken: ct).ConfigureAwait(false);
            }

            if (wake && _services.GetService<IFullDuplexRunEngine>() is { } engine)
            {
                await engine.EnqueueAsync(lease.RunId, ct).ConfigureAwait(false);
            }

            return new ToolWireResponseDto
            {
                CallId = callId,
                IsSuccess = true,
                Result = JsonSerializer.SerializeToElement(new
                {
                    ok = true,
                    run_id = lease.RunId,
                    from = lease.TeammateHandle,
                    target,
                    kind,
                    woke_target = wake,
                }, SnakeJson),
            };
        }

        if (TeamTaskBoardProtocol.IsTeamTasks(canonicalTool)
            || (CoreVirtualToolPolicy.IsTeamPlan(canonicalTool) && IsTaskBoardOperation(args)))
        {
            return await ExecuteDurableTaskBoardFallbackAsync(lease, callId, args, ct).ConfigureAwait(false);
        }

        // team_plan fallback
        if (!args.TryGetProperty("steps", out var stepsProp) || stepsProp.ValueKind != JsonValueKind.Array)
            return Fail(callId, "team_plan requires a 'steps' array.");

        var goal = args.TryGetProperty("goal", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()?.Trim() : null;
        var operation = args.TryGetProperty("operation", out var op) && op.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(op.GetString())
            ? op.GetString()!.Trim()
            : "replace";

        if (_services.GetService<ILifecycleManager>() is { } planLifecycle)
        {
            await planLifecycle.AppendEventAsync(
                lease.RunId,
                "plan.updated",
                new
                {
                    author = lease.TeammateHandle,
                    operation,
                    goal,
                    steps = stepsProp.Clone(),
                    source = "mcp",
                },
                $"Plan updated by '{lease.TeammateHandle}' ({operation}, {stepsProp.GetArrayLength()} step(s)).",
                toolId: CoreVirtualToolPolicy.TeamPlanToolId,
                cancellationToken: ct).ConfigureAwait(false);
        }

        if (_services.GetService<IFullDuplexRunEngine>() is { } planEngine)
        {
            await planEngine.EnqueueAsync(lease.RunId, ct).ConfigureAwait(false);
        }

        return new ToolWireResponseDto
        {
            CallId = callId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                run_id = lease.RunId,
                author = lease.TeammateHandle,
                operation,
                step_count = stepsProp.GetArrayLength(),
            }, SnakeJson),
        };
    }

    private async Task<ToolWireResponseDto> ExecuteRecallEvidenceAsync(
        ScopedTeamMcpLease lease,
        long callId,
        JsonElement args,
        CancellationToken ct)
    {
        if (_services.GetService<IEvidenceArchive>() is not { } archive)
            return Fail(callId, "The evidence archive is not available in this host.");

        var parsed = EvidenceRecallArguments.Parse(args);
        if (parsed.Error is { } error)
            return Fail(callId, error);

        var workspaceId = ResolveWorkspaceId(lease);
        var result = await archive.RecallAsync(new EvidenceRecallQuery(
            lease.TenantId,
            workspaceId,
            lease.SessionId,
            parsed.Query!,
            parsed.Kinds,
            parsed.RunId,
            parsed.Limit), ct).ConfigureAwait(false);

        return new ToolWireResponseDto
        {
            CallId = callId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(EvidenceRecallArguments.ForModel(result), SnakeJson),
        };
    }

    private async Task<ToolWireResponseDto> ExecuteRecallHistoryAsync(
        ScopedTeamMcpLease lease,
        long callId,
        JsonElement args,
        CancellationToken ct)
    {
        if (_services.GetService<IContextMemoryService>() is not { } contextMemory)
            return Fail(callId, "Context memory service is unavailable in this host.");

        // Enforce session isolation unless cross_session is explicitly requested within the lease's project.
        var sanitizedArgs = args;
        if (args.TryGetProperty("session_id", out var sidProp)
            && sidProp.ValueKind == JsonValueKind.String
            && Guid.TryParse(sidProp.GetString(), out var requestedSession)
            && requestedSession != lease.SessionId)
        {
            return Fail(callId, "Scoped teammate MCP tokens may only recall history from their bound session (or pass cross_session=true if project policy allows).");
        }

        var scope = new ToolInvocationScope(
            TenantId: lease.TenantId,
            WorkspaceId: ResolveWorkspaceId(lease),
            PrincipalId: lease.PrincipalId,
            ProjectId: CoreVirtualToolPolicy.ToWireSentinel(lease.ProjectId),
            SessionId: lease.SessionId,
            RunId: lease.RunId,
            TaskId: Guid.Empty,
            AgentInstanceId: Guid.Empty,
            WorkspaceRoot: string.Empty,
            AllowedTools: lease.AllowedTools,
            AllowedResources: [],
            FrozenConfigurationHash: string.Empty,
            DefaultTimeoutSeconds: 30,
            WorkerRetryLimit: 0,
            SerializeWorkspaceWrites: false);

        var wire = new ToolWireRequestDto
        {
            ToolId = CoreVirtualToolPolicy.RecallHistoryToolId,
            SessionId = lease.SessionId.ToString("N"),
            ToolCallId = callId,
            Approved = true,
            Params = sanitizedArgs,
        };

        return await CoreRecallHistoryTool.ExecuteAsync(contextMemory, scope, wire, ct).ConfigureAwait(false);
    }

    private async Task<ToolWireResponseDto> ExecuteGraphViewAsync(
        ScopedTeamMcpLease lease,
        long callId,
        JsonElement args,
        CancellationToken ct)
    {
        if (_services.GetService<ISessionTopology>() is not { } topology)
            return Fail(callId, "Session topology service is unavailable in this host.");

        Guid? runFilter = null;
        if (args.TryGetProperty("run_id", out var rProp) && rProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rProp.GetString()))
        {
            if (!Guid.TryParse(rProp.GetString(), out var parsedRun))
                return Fail(callId, $"Invalid run_id '{rProp.GetString()}'.");
            runFilter = parsedRun;
        }

        var includeFinished = !args.TryGetProperty("include_finished", out var incProp) || incProp.ValueKind != JsonValueKind.False;
        var query = new SessionTopologyQuery(IncludeFinishedRuns: includeFinished, RunId: runFilter).Clamped();
        var view = await topology.GetAsync(lease.SessionId, query, ct).ConfigureAwait(false);
        if (view is null)
            return Fail(callId, "Session topology could not be resolved for this scoped lease.");

        return new ToolWireResponseDto
        {
            CallId = callId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(view, SnakeJson),
        };
    }

    private Guid ResolveWorkspaceId(ScopedTeamMcpLease lease)
    {
        if (lease.WorkspaceId is { } ws && ws != Guid.Empty)
            return ws;
        try
        {
            return _services.GetService<ITenantContextAccessor>()?.Current.WorkspaceId ?? Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private static IReadOnlyList<string> NormalizeAllowedTools(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
            return ITeamBoardMcpBridge.DefaultAllowedTools;

        var filtered = requested
            .Where(t => !string.IsNullOrWhiteSpace(t)
                && !CoreVirtualToolPolicy.IsTinadecConfig(t)
                && ITeamBoardMcpBridge.DefaultAllowedTools.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(t => ITeamBoardMcpBridge.DefaultAllowedTools.First(d => string.Equals(d, t.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return filtered.Length == 0 ? ITeamBoardMcpBridge.DefaultAllowedTools : filtered;
    }

    private static bool IsTaskBoardOperation(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        var op = args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : args.TryGetProperty("operation", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString()
                : null;
        return op?.Trim().ToLowerInvariant() is "list" or "claim" or "complete" or "release";
    }

    private async Task<ToolWireResponseDto> ExecuteDurableTaskBoardFallbackAsync(
        ScopedTeamMcpLease lease,
        long callId,
        JsonElement args,
        CancellationToken ct)
    {
        var board = _services.GetService<ITeamTaskBoardPort>() ?? _services.GetService<SessionPlanService>();
        if (board is null)
            return Fail(callId, "Team task board service is not available in this host.");

        var action = (args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : args.TryGetProperty("operation", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString()
                : null)?.Trim().ToLowerInvariant();
        var stepId = args.TryGetProperty("step_id", out var sid) && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()?.Trim()
            : null;
        long? expectedRev = args.TryGetProperty("expected_revision", out var revEl) && revEl.TryGetInt64(out var parsedRev)
            ? parsedRev
            : null;

        switch (action)
        {
            case "list":
            {
                var onlyReady = !args.TryGetProperty("only_ready", out var rEl) || rEl.ValueKind != JsonValueKind.False;
                var listed = await board.ListTasksAsync(lease.SessionId, onlyReady, ct).ConfigureAwait(false);
                return new ToolWireResponseDto
                {
                    CallId = callId,
                    IsSuccess = true,
                    Result = JsonSerializer.SerializeToElement(listed, SnakeJson),
                };
            }
            case "claim":
            {
                var claimed = await board.ClaimTaskAsync(lease.SessionId, stepId, lease.TeammateHandle, expectedRev, lease.RunId, ct).ConfigureAwait(false);
                return new ToolWireResponseDto
                {
                    CallId = callId,
                    IsSuccess = true,
                    Result = JsonSerializer.SerializeToElement(claimed, SnakeJson),
                };
            }
            case "complete":
            {
                if (string.IsNullOrWhiteSpace(stepId))
                    return Fail(callId, "team_tasks action='complete' requires a non-empty 'step_id'.");
                var summary = args.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.String ? sumEl.GetString() : null;
                var evidence = args.TryGetProperty("evidence", out var evEl) && evEl.ValueKind == JsonValueKind.Array
                    ? evEl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                    : null;
                var completed = await board.CompleteTaskAsync(lease.SessionId, stepId, lease.TeammateHandle, summary, evidence, lease.RunId, ct).ConfigureAwait(false);
                if (completed.Completed && completed.UnlockedReadyTasks.Count > 0 && _services.GetService<IFullDuplexRunEngine>() is { } wakeEngine)
                {
                    await wakeEngine.EnqueueAsync(lease.RunId, ct).ConfigureAwait(false);
                }
                return new ToolWireResponseDto
                {
                    CallId = callId,
                    IsSuccess = true,
                    Result = JsonSerializer.SerializeToElement(completed, SnakeJson),
                };
            }
            case "release":
            {
                if (string.IsNullOrWhiteSpace(stepId))
                    return Fail(callId, "team_tasks action='release' requires a non-empty 'step_id'.");
                var reason = args.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.String
                    ? sumEl.GetString()
                    : args.TryGetProperty("reason", out var rsEl) && rsEl.ValueKind == JsonValueKind.String
                        ? rsEl.GetString()
                        : null;
                var markBlocked = args.TryGetProperty("mark_blocked", out var mbEl) && mbEl.ValueKind == JsonValueKind.True;
                var released = await board.ReleaseTaskAsync(lease.SessionId, stepId, lease.TeammateHandle, reason, markBlocked, lease.RunId, ct).ConfigureAwait(false);
                return new ToolWireResponseDto
                {
                    CallId = callId,
                    IsSuccess = true,
                    Result = JsonSerializer.SerializeToElement(released, SnakeJson),
                };
            }
            default:
                return Fail(callId, $"Unsupported team_tasks action '{action}'. Use list, claim, complete, or release.");
        }
    }

    private static ToolManifestEntryDto? ResolveScopedToolManifestEntry(string toolId)
    {
        if (TeamTaskBoardProtocol.IsTeamTasks(toolId))
            return TeamTaskBoardProtocol.ManifestEntry();

        if (CoreVirtualToolPolicy.IsTeamTool(toolId))
            return TeamToolCatalog.ManifestEntry(toolId);

        if (CoreVirtualToolPolicy.IsRecallHistory(toolId))
            return CoreRecallHistoryTool.ManifestEntry();

        if (OrganizationToolCatalog.Find(toolId) is { } orgEntry)
        {
            return new ToolManifestEntryDto
            {
                Id = orgEntry.Id,
                Description = orgEntry.Description,
                RequiresApproval = false,
                InputSchema = JsonDocument.Parse(orgEntry.SchemaJson).RootElement.Clone(),
                Risk = "low",
                MutatesWorkspace = false,
                RetrySafety = "safe",
                ConfirmationFields = [],
            };
        }

        return null;
    }

    private void PruneExpiredLeases()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _leases)
        {
            if (kvp.Value.ExpiresAt <= now)
                _leases.TryRemove(kvp.Key, out _);
        }
    }

    internal static string StripBearerPrefix(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? trimmed["Bearer ".Length..].Trim()
            : trimmed;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    private static ToolWireResponseDto Fail(long callId, string error) =>
        new() { CallId = callId, IsSuccess = false, Error = error };

    private sealed class HandlerRegistration(
        ConcurrentDictionary<Guid, Func<TeamBoardToolCallContext, CancellationToken, Task<TeamBoardToolCallResult>>> map,
        Guid runId,
        Func<TeamBoardToolCallContext, CancellationToken, Task<TeamBoardToolCallResult>> expected) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                map.TryRemove(new KeyValuePair<Guid, Func<TeamBoardToolCallContext, CancellationToken, Task<TeamBoardToolCallResult>>>(runId, expected));
            }
        }
    }
}
