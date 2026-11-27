using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AspNetCore;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Models.Harness;

namespace TinadecCore.AcpServer;

/// <summary>
/// Mode B (<c>--core-url</c>): attaches the ACP Server over HTTP + SSE to a running TinadecCore instance
/// (e.g. TinadecOffice Desktop's live Core), keeping Desktop UI conversation, orchestration graph, and
/// trajectory synchronized in real time.
/// <para>
/// Security invariant: <c>X-Tinadec-Host-Control</c> is attached as an HTTP header only and is NEVER
/// written to logs, exception messages, or diagnostics.
/// </para>
/// </summary>
internal sealed class RemoteHttpAcpCoreBackend : IAcpCoreBackend
{
    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;
    private readonly bool _disposeHttp;
    private readonly string? _hostToken;
    private readonly ConcurrentDictionary<Guid, string?> _sessionStorageIds = new();
    private readonly ConcurrentDictionary<Guid, string> _sessionCwds = new();

    public RemoteHttpAcpCoreBackend(Uri baseUri, string? hostToken = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        _hostToken = string.IsNullOrEmpty(hostToken) ? null : hostToken;

        if (httpClient is not null)
        {
            _http = httpClient;
            if (_http.BaseAddress is null) _http.BaseAddress = NormalizeBaseUri(baseUri);
            _disposeHttp = false;
        }
        else
        {
            var normalized = NormalizeBaseUri(baseUri);
            _http = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            }, disposeHandler: true)
            {
                BaseAddress = normalized,
                Timeout = Timeout.InfiniteTimeSpan
            };
            _disposeHttp = true;
        }
    }

    public async Task<AcpSessionSnapshot> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        var canonicalCwd = WorkspacePathSpelling.Canonical(Path.GetFullPath(cwd));
        var (projectId, storageId) = await FindOrCreateProjectAsync(canonicalCwd, cancellationToken).ConfigureAwait(false);

        using var createReq = CreateRequest(HttpMethod.Post, "/api/v1/sessions", storageId, new
        {
            project_id = projectId.ToString("D"),
            title = $"ACP · {Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalCwd))}",
            origin = "acp"
        });
        using var createResp = await SendWithTimeoutAsync(createReq, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(createResp, "POST /api/v1/sessions", cancellationToken).ConfigureAwait(false);

        var sessionEl = await createResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var sessionId = sessionEl.GetProperty("id").GetGuid();
        var effectiveStorageId = ReadString(sessionEl, "storage_id") ?? storageId;

        _sessionStorageIds[sessionId] = effectiveStorageId;
        _sessionCwds[sessionId] = canonicalCwd;

        return await BuildSessionSnapshotAsync(sessionId, canonicalCwd, effectiveStorageId, sessionEl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcpSessionSnapshot> LoadSessionAsync(Guid sessionId, string cwd, CancellationToken cancellationToken)
    {
        var canonicalCwd = WorkspacePathSpelling.Canonical(Path.GetFullPath(cwd));
        var (sessionEl, storageId) = await FindRemoteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionEl is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        _sessionStorageIds[sessionId] = storageId;
        _sessionCwds[sessionId] = canonicalCwd;

        return await BuildSessionSnapshotAsync(sessionId, canonicalCwd, storageId, sessionEl.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AcpHistoryMessage>> ListHistoryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Get, $"/api/v1/sessions/{sessionId:D}/messages", storageId);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        await EnsureSuccessAsync(resp, $"GET /api/v1/sessions/{sessionId:D}/messages", cancellationToken).ConfigureAwait(false);
        var array = await resp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        var list = new List<AcpHistoryMessage>(array.Length);
        foreach (var item in array)
        {
            var role = ReadString(item, "role") ?? string.Empty;
            if (role is not ("user" or "assistant")) continue;
            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var msgId)
                ? msgId
                : Guid.Empty;
            var content = ReadString(item, "content") ?? string.Empty;
            list.Add(new AcpHistoryMessage(id, role, content));
        }

        return list;
    }

    public async Task<AcpSessionSnapshot> SetModeAsync(Guid sessionId, string modeId, CancellationToken cancellationToken)
    {
        var (currentSessionEl, storageId) = await FindRemoteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (currentSessionEl is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        var existingSpace = ReadSpaceOptions(currentSessionEl.Value);
        var publishedModes = await ListPublishedModesAsync(storageId, cancellationToken).ConfigureAwait(false);

        object patchPayload;
        if (AcpModeCatalog.TryResolveBuiltInSettings(modeId, existingSpace, out var permissionMode, out var spaceOptions, out var clearSpace))
        {
            if (clearSpace)
            {
                patchPayload = new
                {
                    permission_mode = permissionMode,
                    space_options = (object?)null
                };
            }
            else
            {
                patchPayload = new
                {
                    permission_mode = permissionMode,
                    space_options = new
                    {
                        plan_first = spaceOptions!.PlanFirst,
                        spec_enabled = spaceOptions.SpecEnabled,
                        multi_agent = spaceOptions.MultiAgent,
                        workflow_mode_version_id = spaceOptions.WorkflowModeVersionId,
                        bulletin_board = spaceOptions.BulletinBoard,
                        worktree = spaceOptions.Worktree,
                        context_strategy = spaceOptions.ContextStrategy
                    }
                };
            }
        }
        else
        {
            var matched = publishedModes.FirstOrDefault(m => string.Equals(m.Slug, modeId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                throw new ArgumentException($"Unsupported ACP modeId '{modeId}'.");
            }

            patchPayload = new
            {
                mode_version_id = matched.ModeVersionId,
                permission_mode = "default",
                space_options = (object?)null
            };
        }

        using var patchReq = CreateRequest(HttpMethod.Patch, $"/api/v1/sessions/{sessionId:D}", storageId, patchPayload);
        using var patchResp = await SendWithTimeoutAsync(patchReq, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (patchResp.StatusCode == HttpStatusCode.BadRequest)
        {
            var errText = await ReadSafeErrorMessageAsync(patchResp, cancellationToken).ConfigureAwait(false);
            throw new ArgumentException(errText);
        }

        await EnsureSuccessAsync(patchResp, $"PATCH /api/v1/sessions/{sessionId:D}", cancellationToken).ConfigureAwait(false);
        var updatedEl = await patchResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var cwd = _sessionCwds.GetValueOrDefault(sessionId, string.Empty);
        return await BuildSessionSnapshotAsync(sessionId, cwd, storageId, updatedEl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcpSessionSnapshot> SetModelAsync(Guid sessionId, string modelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("modelId is required.");
        }

        var (currentSessionEl, storageId) = await FindRemoteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (currentSessionEl is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        var catalog = await LoadModelCatalogAsync(storageId, cancellationToken).ConfigureAwait(false);
        if (!TryMatchModelSelection(modelId, catalog.Entries, out var providerId, out var modelName))
        {
            throw new ArgumentException($"Unknown or unavailable modelId '{modelId}'.");
        }

        using var patchReq = CreateRequest(HttpMethod.Patch, $"/api/v1/sessions/{sessionId:D}", storageId, new
        {
            meeting_model_override = new
            {
                provider_instance_id = providerId,
                model = modelName
            }
        });
        using var patchResp = await SendWithTimeoutAsync(patchReq, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (patchResp.StatusCode == HttpStatusCode.BadRequest)
        {
            var errText = await ReadSafeErrorMessageAsync(patchResp, cancellationToken).ConfigureAwait(false);
            throw new ArgumentException(errText);
        }

        await EnsureSuccessAsync(patchResp, $"PATCH /api/v1/sessions/{sessionId:D}", cancellationToken).ConfigureAwait(false);
        var updatedEl = await patchResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var cwd = _sessionCwds.GetValueOrDefault(sessionId, string.Empty);
        return await BuildSessionSnapshotAsync(sessionId, cwd, storageId, updatedEl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> StoreAttachmentAsync(
        Guid sessionId,
        string fileName,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var url = $"/api/v1/sessions/{sessionId:D}/attachments?filename={Uri.EscapeDataString(fileName)}&media_type={Uri.EscapeDataString(mediaType)}&origin=composer";
        using var req = CreateRequest(HttpMethod.Post, url, storageId);
        req.Content = new ReadOnlyMemoryContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            throw new ArgumentException(await ReadSafeErrorMessageAsync(resp, cancellationToken).ConfigureAwait(false));
        }

        await EnsureSuccessAsync(resp, "POST /api/v1/sessions/{id}/attachments", cancellationToken).ConfigureAwait(false);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return Guid.Parse(dto.GetProperty("id").GetString()!);
    }

    public async Task<AcpPromptSubmission> SubmitPromptAsync(
        Guid sessionId,
        string content,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["content"] = content,
            ["client_message_id"] = $"acp-{Guid.NewGuid():N}",
            ["dispatch_mode"] = "queued"
        };
        if (attachmentIds.Count > 0)
        {
            payload["attachment_ids"] = attachmentIds.Select(id => id.ToString("D")).ToArray();
        }

        using var req = CreateRequest(HttpMethod.Post, $"/api/v1/sessions/{sessionId:D}/interactions", storageId, payload);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }
        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ArgumentException(await ReadSafeErrorMessageAsync(resp, cancellationToken).ConfigureAwait(false));
        }

        await EnsureSuccessAsync(resp, $"POST /api/v1/sessions/{sessionId:D}/interactions", cancellationToken).ConfigureAwait(false);
        var receipt = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var runId = receipt.GetProperty("run_id").GetGuid();
        var turnId = receipt.TryGetProperty("turn_id", out var tEl) && tEl.ValueKind == JsonValueKind.String && Guid.TryParse(tEl.GetString(), out var parsedTurn)
            ? parsedTurn
            : (Guid?)null;
        var messageId = receipt.TryGetProperty("message_id", out var mEl) && mEl.ValueKind == JsonValueKind.String && Guid.TryParse(mEl.GetString(), out var parsedMsg)
            ? parsedMsg
            : receipt.GetProperty("interaction_id").GetGuid();
        var streamCursor = receipt.TryGetProperty("stream_cursor", out var scEl) && scEl.ValueKind == JsonValueKind.Number && scEl.TryGetInt64(out var parsedCursor)
            ? parsedCursor
            : 0L;

        return new AcpPromptSubmission(runId, turnId, messageId, streamCursor);
    }

    public async IAsyncEnumerable<AcpRunStreamFrame> FollowRunStreamAsync(
        Guid sessionId,
        Guid runId,
        Guid? turnId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var query = $"/api/v1/runs/{runId:D}/stream?after_seq={afterSequence}" + (turnId is { } tid ? $"&turn_id={tid:D}" : string.Empty);
        using var req = CreateRequest(HttpMethod.Get, query, storageId);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, query, cancellationToken).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var sse in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(sse.Data)) continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(sse.Data);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var kind = ReadString(root, "kind") ?? sse.EventName ?? string.Empty;
                if (string.IsNullOrEmpty(kind) || kind == "heartbeat") continue;
                var seq = root.TryGetProperty("seq", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number && seqEl.TryGetInt64(out var s) ? s : 0L;
                var delta = ReadString(root, "delta");
                var finishReason = ReadString(root, "finish_reason");
                var errorCategory = ReadString(root, "error_category");
                var safeErrorMessage = ReadString(root, "safe_error_message");

                yield return new AcpRunStreamFrame(seq, kind, delta, finishReason, errorCategory, safeErrorMessage);
                if (kind is "done" or "error") yield break;
            }
        }
    }

    public async IAsyncEnumerable<AcpBackendEvent> SubscribeSessionEventsAsync(
        Guid sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var url = $"/api/v1/events?session_id={sessionId:D}";
        using var req = CreateRequest(HttpMethod.Get, url, storageId);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, url, cancellationToken).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var sse in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (sse.EventName == "heartbeat" || string.IsNullOrWhiteSpace(sse.Data)) continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(sse.Data);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var eventType = ReadString(root, "event_type") ?? sse.EventName ?? string.Empty;
                if (string.IsNullOrEmpty(eventType) || eventType == "heartbeat") continue;

                var eventId = ReadString(root, "event_id") ?? Guid.NewGuid().ToString("N");
                Guid? eventRunId = ReadString(root, "run_id") is { } runStr && Guid.TryParse(runStr, out var parsedRun) ? parsedRun : null;

                long seq = 0;
                string? summary = null;
                var innerPayload = default(JsonElement);
                if (root.TryGetProperty("payload", out var envelopePayload) && envelopePayload.ValueKind == JsonValueKind.Object)
                {
                    if (envelopePayload.TryGetProperty("sequence", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number && seqEl.TryGetInt64(out var parsedSeq))
                    {
                        seq = parsedSeq;
                    }
                    summary = ReadString(envelopePayload, "summary");
                    if (envelopePayload.TryGetProperty("payload", out var inner))
                    {
                        innerPayload = inner.Clone();
                    }
                }

                yield return new AcpBackendEvent(eventId, eventType, eventRunId, seq, summary, innerPayload);
            }
        }
    }

    public async Task<AcpRunStatusSnapshot?> GetRunStatusAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Get, $"/api/v1/sessions/{sessionId:D}/runs", storageId);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var runs = await resp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        foreach (var item in runs)
        {
            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var id) && id == runId)
            {
                var status = ReadString(item, "status") ?? string.Empty;
                var lastSeq = item.TryGetProperty("latest_event_sequence", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number && seqEl.TryGetInt64(out var s) ? s : 0L;
                var summary = ReadString(item, "summary");
                return new AcpRunStatusSnapshot(runId, status, lastSeq, summary);
            }
        }

        return null;
    }

    public async Task<AcpToolExecutionDetail?> GetToolExecutionDetailAsync(
        Guid sessionId,
        Guid runId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Get, $"/api/v1/sessions/{sessionId:D}/tool-executions?run_id={runId:D}", storageId);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var rows = await resp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idEl.GetString(), out var rowId) || rowId != executionId)
            {
                continue;
            }

            var toolId = ReadString(row, "tool_id") ?? string.Empty;
            var summary = ReadString(row, "arguments_summary") ?? ReadString(row, "summary");
            var resourcePath = ReadString(row, "resource_path");
            var command = ReadString(row, "command");
            var cwd = ReadString(row, "cwd");
            bool? toolSuccess = row.TryGetProperty("tool_success", out var tsEl) && tsEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? tsEl.GetBoolean()
                : null;

            var rawInputObj = BuildSyntheticRawInput(resourcePath, command, cwd, ReadString(row, "query"));
            return new AcpToolExecutionDetail(
                executionId,
                toolId,
                summary,
                resourcePath,
                command,
                cwd,
                rawInputObj,
                RawOutput: null,
                ResultText: ReadString(row, "summary"),
                ToolSuccess: toolSuccess);
        }

        return null;
    }

    public async Task<IReadOnlyList<AcpPendingApproval>> ListPendingApprovalsAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Get, $"/api/v1/approvals?status=pending&run_id={runId:D}", storageId);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var array = await resp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        return array.Select(ParseApprovalElement).Where(x => x is not null).Cast<AcpPendingApproval>().ToArray();
    }

    public async Task<AcpPendingApproval?> GetApprovalAsync(
        Guid sessionId,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Get, $"/api/v1/approvals/{approvalId:D}", storageId);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || !resp.IsSuccessStatusCode) return null;

        var element = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseApprovalElement(element);
    }

    public async Task<AcpApprovalDecisionOutcome> DecideApprovalAsync(
        Guid sessionId,
        Guid approvalId,
        AcpApprovalDecisionInput input,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["decision"] = input.Decision,
            ["scope"] = input.Scope
        };
        if (!string.IsNullOrWhiteSpace(input.Reason))
        {
            body["reason"] = input.Reason;
        }
        if (input.RememberRule is { } rule)
        {
            body["remember_rule"] = new
            {
                kind = rule.Kind,
                pattern = rule.Pattern,
                scope = rule.Scope
            };
        }

        using var req = CreateRequest(HttpMethod.Post, $"/api/v1/approvals/{approvalId:D}/decision", storageId, body);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AcpApprovalDecisionOutcome(
                Applied: true,
                AlreadyDecidedConflict: false,
                InvalidRuleRejected: false,
                Status: ReadString(dto, "status"));
        }

        var errorEl = default(JsonElement);
        try
        {
            errorEl = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }

        var code = ReadString(errorEl, "code");
        var message = ReadString(errorEl, "message") ?? ReadString(errorEl, "detail");

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            // Desktop UI already resolved this approval concurrently — safe coexistence!
            return new AcpApprovalDecisionOutcome(
                Applied: false,
                AlreadyDecidedConflict: true,
                InvalidRuleRejected: false,
                ErrorCode: code,
                ErrorMessage: message);
        }

        if (resp.StatusCode == HttpStatusCode.BadRequest
            && code is "invalid_rule" or "spec_confirmation_scope")
        {
            return new AcpApprovalDecisionOutcome(
                Applied: false,
                AlreadyDecidedConflict: false,
                InvalidRuleRejected: true,
                ErrorCode: code,
                ErrorMessage: message);
        }

        return new AcpApprovalDecisionOutcome(
            Applied: false,
            AlreadyDecidedConflict: false,
            InvalidRuleRejected: false,
            ErrorCode: code,
            ErrorMessage: message);
    }

    public async Task ResumeAwaitingUserSupervisionAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var pending = await ListPendingApprovalsAsync(sessionId, runId, cancellationToken).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            return;
        }

        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Post, $"/api/v1/runs/{runId:D}/control", storageId, new
        {
            action = "resume",
            client_control_id = $"acp-resume-{Guid.NewGuid():N}"
        });
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        _ = resp;
    }

    public async Task CancelRunAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var storageId = await ResolveStorageIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Post, $"/api/v1/runs/{runId:D}/control", storageId, new
        {
            action = "cancel",
            client_control_id = $"acp-cancel-{runId:N}"
        });
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        // 200 OK or 409 RUN_NOT_ACTIVE (already terminal) are both clean outcomes.
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Conflict && resp.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(resp, $"POST /api/v1/runs/{runId:D}/control", cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposeHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<(Guid ProjectId, string? StorageId)> FindOrCreateProjectAsync(string canonicalCwd, CancellationToken ct)
    {
        using (var listReq = CreateRequest(HttpMethod.Get, "/api/v1/projects", storageId: null))
        using (var listResp = await SendWithTimeoutAsync(listReq, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(listResp, "GET /api/v1/projects", ct).ConfigureAwait(false);
            var projects = await listResp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct).ConfigureAwait(false) ?? [];
            foreach (var p in projects)
            {
                var pPath = ReadString(p, "path");
                if (string.IsNullOrWhiteSpace(pPath)) continue;
                try
                {
                    var normalizedPPath = WorkspacePathSpelling.Canonical(Path.GetFullPath(pPath));
                    if (SamePath(normalizedPPath, canonicalCwd)
                        && p.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                        && Guid.TryParse(idEl.GetString(), out var existingId))
                    {
                        return (existingId, ReadString(p, "storage_id"));
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                }
            }
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalCwd));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "Workspace";

        using var createReq = CreateRequest(HttpMethod.Post, "/api/v1/projects", storageId: null, new
        {
            name = folderName,
            path = canonicalCwd
        });
        using var createResp = await SendWithTimeoutAsync(createReq, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(createResp, "POST /api/v1/projects", ct).ConfigureAwait(false);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        return (created.GetProperty("id").GetGuid(), ReadString(created, "storage_id"));
    }

    private async Task<(JsonElement? Session, string? StorageId)> FindRemoteSessionAsync(Guid sessionId, CancellationToken ct)
    {
        if (_sessionStorageIds.TryGetValue(sessionId, out var knownStorage))
        {
            using var scopedReq = CreateRequest(HttpMethod.Get, "/api/v1/sessions", knownStorage);
            using var scopedResp = await SendWithTimeoutAsync(scopedReq, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (scopedResp.IsSuccessStatusCode)
            {
                var scopedSessions = await scopedResp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct).ConfigureAwait(false) ?? [];
                foreach (var item in scopedSessions)
                {
                    if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        && Guid.TryParse(idEl.GetString(), out var id) && id == sessionId)
                    {
                        return (item.Clone(), ReadString(item, "storage_id") ?? knownStorage);
                    }
                }
            }
        }

        using var req = CreateRequest(HttpMethod.Get, "/api/v1/sessions", storageId: null);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, "GET /api/v1/sessions", ct).ConfigureAwait(false);

        var sessions = await resp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        foreach (var item in sessions)
        {
            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                && Guid.TryParse(idEl.GetString(), out var id) && id == sessionId)
            {
                var storageId = ReadString(item, "storage_id");
                _sessionStorageIds[sessionId] = storageId;
                return (item.Clone(), storageId);
            }
        }

        return (null, null);
    }

    private async Task<string?> ResolveStorageIdAsync(Guid sessionId, CancellationToken ct)
    {
        if (_sessionStorageIds.TryGetValue(sessionId, out var cached)) return cached;
        var (_, discovered) = await FindRemoteSessionAsync(sessionId, ct).ConfigureAwait(false);
        return discovered;
    }

    private async Task<AcpSessionSnapshot> BuildSessionSnapshotAsync(
        Guid sessionId,
        string cwd,
        string? storageId,
        JsonElement sessionEl,
        CancellationToken ct)
    {
        var publishedModes = await ListPublishedModesAsync(storageId, ct).ConfigureAwait(false);
        var permissionMode = ReadString(sessionEl, "permission_mode") ?? "default";
        var spaceOptions = ReadSpaceOptions(sessionEl);
        Guid? modeVersionId = sessionEl.TryGetProperty("mode_version_id", out var mvEl) && mvEl.ValueKind == JsonValueKind.String && Guid.TryParse(mvEl.GetString(), out var mv)
            ? mv
            : null;
        Guid? latestDefaultModeVersionId = sessionEl.TryGetProperty("latest_mode_version_id", out var lmEl) && lmEl.ValueKind == JsonValueKind.String && Guid.TryParse(lmEl.GetString(), out var lmv)
            ? lmv
            : null;

        var currentModeId = AcpModeCatalog.ResolveCurrentModeId(permissionMode, spaceOptions, modeVersionId, latestDefaultModeVersionId, publishedModes);
        var availableModes = AcpModeCatalog.BuildAvailableModes(publishedModes);

        var catalog = await LoadModelCatalogAsync(storageId, ct).ConfigureAwait(false);
        var currentModelId = ResolveCurrentModelIdFromSessionElement(sessionEl, catalog);

        return new AcpSessionSnapshot(
            sessionId,
            cwd,
            new AcpSessionModesState(currentModeId, availableModes),
            new AcpSessionModelsState(currentModelId, catalog.Options));
    }

    private async Task<IReadOnlyList<AcpPublishedModeInfo>> ListPublishedModesAsync(string? storageId, CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, "/api/v1/agent-modes", storageId);
        using var resp = await SendWithTimeoutAsync(req, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var modes = await resp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        var list = new List<AcpPublishedModeInfo>();
        foreach (var item in modes)
        {
            if (ReadString(item, "status") != "published" && !item.TryGetProperty("latest_published_mode_version_id", out _)) continue;
            if (!item.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var modeId)) continue;
            if (!item.TryGetProperty("latest_published_mode_version_id", out var verEl) || verEl.ValueKind != JsonValueKind.String || !Guid.TryParse(verEl.GetString(), out var verId)) continue;
            var slug = ReadString(item, "slug") ?? string.Empty;
            var displayName = ReadString(item, "display_name") ?? slug;
            var description = ReadString(item, "description");
            list.Add(new AcpPublishedModeInfo(modeId, verId, slug, displayName, description));
        }

        return list;
    }

    private sealed record RemoteModelEntry(Guid ProviderId, string ProviderName, string? Model, string ModelId);

    private sealed record RemoteModelCatalog(IReadOnlyList<RemoteModelEntry> Entries, IReadOnlyList<AcpModelOption> Options, string DefaultModelId);

    private async Task<RemoteModelCatalog> LoadModelCatalogAsync(string? storageId, CancellationToken ct)
    {
        var entries = new List<RemoteModelEntry>();
        using (var provReq = CreateRequest(HttpMethod.Get, "/api/v1/model-providers", storageId))
        using (var provResp = await SendWithTimeoutAsync(provReq, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
        {
            if (provResp.IsSuccessStatusCode)
            {
                var providers = await provResp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct).ConfigureAwait(false) ?? [];
                foreach (var prov in providers)
                {
                    if (prov.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.False) continue;
                    if (!prov.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var provId)) continue;
                    var displayName = ReadString(prov, "display_name") ?? ReadString(prov, "driver") ?? provId.ToString("D");
                    var models = new List<string>();
                    if (ReadString(prov, "model") is { Length: > 0 } primaryModel) models.Add(primaryModel);
                    if (prov.TryGetProperty("models", out var arrEl) && arrEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in arrEl.EnumerateArray())
                        {
                            if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } mStr)
                                models.Add(mStr);
                        }
                    }

                    var distinct = models.Distinct(StringComparer.Ordinal).ToArray();
                    if (distinct.Length == 0)
                    {
                        var modelId = provId.ToString("D");
                        entries.Add(new RemoteModelEntry(provId, displayName, null, modelId));
                    }
                    else
                    {
                        foreach (var m in distinct)
                        {
                            entries.Add(new RemoteModelEntry(provId, displayName, m, $"{provId:D}/{m}"));
                        }
                    }
                }
            }
        }

        string? defaultModelId = null;
        using (var routeReq = CreateRequest(HttpMethod.Get, "/api/v1/model-routes", storageId))
        using (var routeResp = await SendWithTimeoutAsync(routeReq, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
        {
            if (routeResp.IsSuccessStatusCode)
            {
                var routes = await routeResp.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct).ConfigureAwait(false) ?? [];
                var chatRoute = routes.FirstOrDefault(r => string.Equals(ReadString(r, "purpose"), "chat", StringComparison.OrdinalIgnoreCase));
                if (chatRoute.ValueKind == JsonValueKind.Object
                    && chatRoute.TryGetProperty("candidates", out var candidates)
                    && candidates.ValueKind == JsonValueKind.Array
                    && candidates.GetArrayLength() > 0)
                {
                    var first = candidates[0];
                    if (first.TryGetProperty("provider_instance_id", out var pidEl) && Guid.TryParse(pidEl.GetString(), out var pid))
                    {
                        var model = ReadString(first, "model");
                        defaultModelId = string.IsNullOrWhiteSpace(model) ? $"{pid:D}" : $"{pid:D}/{model}";
                    }
                }
            }
        }

        var options = entries
            .Select(e => new AcpModelOption(
                e.ModelId,
                e.Model is null ? e.ProviderName : $"{e.ProviderName} / {e.Model}",
                e.Model))
            .ToArray();

        defaultModelId ??= options.FirstOrDefault()?.ModelId ?? "default";
        return new RemoteModelCatalog(entries, options, defaultModelId);
    }

    private static string ResolveCurrentModelIdFromSessionElement(JsonElement sessionEl, RemoteModelCatalog catalog)
    {
        if (sessionEl.TryGetProperty("meeting_model_override", out var ov)
            && ov.ValueKind == JsonValueKind.Object
            && ov.TryGetProperty("provider_instance_id", out var pidEl)
            && Guid.TryParse(pidEl.GetString(), out var pid))
        {
            var model = ReadString(ov, "model");
            return string.IsNullOrWhiteSpace(model) ? $"{pid:D}" : $"{pid:D}/{model}";
        }

        return catalog.DefaultModelId;
    }

    private static bool TryMatchModelSelection(
        string requestedModelId,
        IReadOnlyList<RemoteModelEntry> entries,
        out Guid providerId,
        out string? modelName)
    {
        var trimmed = requestedModelId.Trim();
        var exact = entries.FirstOrDefault(e => string.Equals(e.ModelId, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            providerId = exact.ProviderId;
            modelName = exact.Model;
            return true;
        }

        var slash = trimmed.IndexOf('/');
        if (slash > 0 && Guid.TryParse(trimmed[..slash], out var explicitProviderId))
        {
            var explicitModel = trimmed[(slash + 1)..].Trim();
            if (entries.Any(e => e.ProviderId == explicitProviderId) && explicitModel.Length > 0)
            {
                providerId = explicitProviderId;
                modelName = explicitModel;
                return true;
            }
        }

        var byModelName = entries.Where(e => e.Model is not null && string.Equals(e.Model, trimmed, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byModelName.Length == 1)
        {
            providerId = byModelName[0].ProviderId;
            modelName = byModelName[0].Model;
            return true;
        }

        providerId = Guid.Empty;
        modelName = null;
        return false;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl, string? storageId, object? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, relativeUrl);
        if (_hostToken is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Tinadec-Host-Control", _hostToken);
        }
        if (!string.IsNullOrWhiteSpace(storageId) && !string.Equals(storageId, "user", StringComparison.Ordinal))
        {
            req.Headers.TryAddWithoutValidation(StorageScopeHttpExtensions.StorageHeader, storageId);
        }
        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, SnakeJson);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return req;
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await ReadSafeErrorMessageAsync(response, ct).ConfigureAwait(false);
        throw new InvalidOperationException($"Remote Core {operation} failed with HTTP {(int)response.StatusCode}: {detail}");
    }

    private static async Task<string> ReadSafeErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var el = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            return ReadString(el, "message") ?? ReadString(el, "detail") ?? ReadString(el, "title") ?? $"HTTP {(int)response.StatusCode}";
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    private static AcpPendingApproval? ParseApprovalElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id)) return null;
        Guid? sessionId = ReadGuid(el, "session_id");
        Guid? runId = ReadGuid(el, "run_id");
        Guid? executionId = ReadGuid(el, "execution_id");
        return new AcpPendingApproval(
            id,
            sessionId,
            runId,
            executionId,
            ReadString(el, "kind") ?? "tool",
            ReadString(el, "tool_id") ?? string.Empty,
            ReadString(el, "risk") ?? "medium",
            ReadString(el, "status") ?? "pending",
            ReadString(el, "summary") ?? "Tool requires approval.",
            ReadString(el, "arguments"),
            ReadString(el, "command"),
            ReadString(el, "cwd"),
            ReadString(el, "resource_path"));
    }

    private static SpaceRunOptions? ReadSpaceOptions(JsonElement sessionEl)
    {
        if (!sessionEl.TryGetProperty("space_options", out var sp) || sp.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return SpaceRunOptionsJson.Parse(sp, rejectUnknownFields: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonElement? BuildSyntheticRawInput(string? resourcePath, string? command, string? cwd, string? query)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(resourcePath)) dict["path"] = resourcePath;
        if (!string.IsNullOrWhiteSpace(command)) dict["command"] = command;
        if (!string.IsNullOrWhiteSpace(cwd)) dict["cwd"] = cwd;
        if (!string.IsNullOrWhiteSpace(query)) dict["query"] = query;
        return dict.Count == 0 ? null : JsonSerializer.SerializeToElement(dict);
    }

    private static Uri NormalizeBaseUri(Uri uri)
    {
        var text = uri.ToString();
        return text.EndsWith('/') ? uri : new Uri(text + "/");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static Guid? ReadGuid(JsonElement el, string name) =>
        ReadString(el, name) is { } text && Guid.TryParse(text, out var parsed) ? parsed : null;
}
