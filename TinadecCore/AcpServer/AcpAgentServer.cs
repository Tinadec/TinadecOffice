using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AspNetCore.Endpoints;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AcpServer;

/// <summary>
/// Full ACP v1 Agent Server speaking JSON-RPC 2.0 NDJSON over an <see cref="IAcpTransport"/>.
/// Reuses <see cref="JsonRpcConnection"/> (with concurrent server request dispatch enabled) and
/// <see cref="AcpWire"/> so wire framing, correlation, and casing stay unified across client and server.
/// </summary>
internal sealed class AcpAgentServer
{
    private static readonly TimeSpan PermissionRequestTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan TrailingEventDrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly string AgentVersion =
        typeof(AcpAgentServer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    private readonly IAcpCoreBackend _backend;
    private readonly JsonRpcConnection _connection;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<Guid, ActivePromptTurn> _activeTurns = new();
    private volatile bool _initialized;

    public AcpAgentServer(
        IAcpTransport transport,
        IAcpCoreBackend backend,
        ILogger? logger = null,
        Action<string>? onDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _logger = logger;
        _connection = new JsonRpcConnection(
            transport,
            onDiagnostic: onDiagnostic,
            logger: logger,
            dispatchRequestsConcurrently: true);

        _connection.OnRequest = HandleRequestAsync;
        _connection.OnMethodNotification = HandleNotification;
        _connection.OnInputClosed = OnPeerInputClosed;
    }

    public Task RunAsync(CancellationToken cancellationToken) =>
        _connection.PumpAsync(cancellationToken);

    public async Task CancelAllActiveTurnsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (sessionId, turn) in _activeTurns.ToArray())
        {
            await CancelActiveTurnAsync(sessionId, turn, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnPeerInputClosed()
    {
        // When stdin hits EOF while a session/prompt turn is running, cancel active runs cleanly
        // so DrainInFlightRequestsAsync finishes promptly with zero orphan background work.
        if (_activeTurns.IsEmpty) return;
        _ = Task.Run(() => CancelAllActiveTurnsAsync(CancellationToken.None));
    }

    private void HandleNotification(string method, JsonElement @params)
    {
        if (!string.Equals(method, "session/cancel", StringComparison.Ordinal))
        {
            return;
        }

        if (!TryReadSessionId(@params, out var sessionId))
        {
            return;
        }

        if (_activeTurns.TryGetValue(sessionId, out var turn))
        {
            _ = Task.Run(() => CancelActiveTurnAsync(sessionId, turn, CancellationToken.None));
        }
    }

    private async Task CancelActiveTurnAsync(Guid sessionId, ActivePromptTurn turn, CancellationToken ct)
    {
        turn.MarkCancelled();
        if (turn.RunId is { } runId)
        {
            try
            {
                await _backend.CancelRunAsync(sessionId, runId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Best-effort run cancellation on session {SessionId} failed.", sessionId);
            }
        }
    }

    private async Task<JsonElement> HandleRequestAsync(string method, JsonElement @params, CancellationToken cancellationToken)
    {
        return method switch
        {
            "initialize" => HandleInitialize(@params),
            "authenticate" => AcpWire.EmptyObject,
            "session/new" => await HandleSessionNewAsync(@params, cancellationToken).ConfigureAwait(false),
            "session/load" => await HandleSessionLoadAsync(@params, cancellationToken).ConfigureAwait(false),
            "session/prompt" => await HandleSessionPromptAsync(@params, cancellationToken).ConfigureAwait(false),
            "session/set_mode" => await HandleSetModeAsync(@params, cancellationToken).ConfigureAwait(false),
            "session/set_model" => await HandleSetModelAsync(@params, cancellationToken).ConfigureAwait(false),
            _ => throw new AcpMethodNotFoundException(method)
        };
    }

    private JsonElement HandleInitialize(JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("protocolVersion", out var versionEl)
            || versionEl.ValueKind != JsonValueKind.Number
            || !versionEl.TryGetInt32(out var protocolVersion))
        {
            throw new AcpInvalidParamsException("initialize requires integer parameter 'protocolVersion'.");
        }

        if (protocolVersion != AcpWire.ProtocolVersion)
        {
            throw new AcpInvalidParamsException($"Unsupported ACP protocolVersion {protocolVersion}; only version {AcpWire.ProtocolVersion} is supported.");
        }

        _initialized = true;
        var result = new AcpServerInitializeResult(
            AcpWire.ProtocolVersion,
            new AcpAgentCapabilities(
                LoadSession: true,
                PromptCapabilities: new AcpPromptCapabilities(Image: true),
                SessionCapabilities: new AcpSessionCapabilities(Fork: false)),
            new AcpAgentInfo("tinadec", "TinadecOffice", AgentVersion));

        return AcpWire.ToParams(result);
    }

    private async Task<JsonElement> HandleSessionNewAsync(JsonElement @params, CancellationToken ct)
    {
        EnsureInitialized();
        var cwd = ValidateCwd(@params);
        var snapshot = await _backend.CreateSessionAsync(cwd, ct).ConfigureAwait(false);
        return ToSessionResultElement(snapshot);
    }

    private async Task<JsonElement> HandleSessionLoadAsync(JsonElement @params, CancellationToken ct)
    {
        EnsureInitialized();
        var cwd = ValidateCwd(@params);
        var sessionId = RequireSessionId(@params);

        AcpSessionSnapshot snapshot;
        IReadOnlyList<AcpHistoryMessage> history;
        try
        {
            snapshot = await _backend.LoadSessionAsync(sessionId, cwd, ct).ConfigureAwait(false);
            history = await _backend.ListHistoryAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            throw new AcpInvalidParamsException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new AcpInvalidParamsException(ex.Message);
        }

        // ACP v1 rule: replay historical messages in chronological order via session/update
        // notifications and flush them BEFORE returning the session/load response.
        var sessionIdStr = sessionId.ToString("D");
        foreach (var message in history)
        {
            var updateKind = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                ? "user_message_chunk"
                : "agent_message_chunk";

            await SendSessionUpdateAsync(
                sessionIdStr,
                AcpWire.ToParams(new AcpMessageChunkUpdatePayload(
                    updateKind,
                    new AcpTextContentPayload("text", message.Content))),
                ct).ConfigureAwait(false);
        }

        return ToSessionResultElement(snapshot);
    }

    private async Task<JsonElement> HandleSetModeAsync(JsonElement @params, CancellationToken ct)
    {
        EnsureInitialized();
        var sessionId = RequireSessionId(@params);
        var modeId = RequireNonEmptyString(@params, "modeId");

        AcpSessionSnapshot updated;
        try
        {
            updated = await _backend.SetModeAsync(sessionId, modeId, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            throw new AcpInvalidParamsException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new AcpInvalidParamsException(ex.Message);
        }

        await SendSessionUpdateAsync(
            sessionId.ToString("D"),
            AcpWire.ToParams(new AcpCurrentModeUpdatePayload("current_mode_update", updated.Modes.CurrentModeId)),
            ct).ConfigureAwait(false);

        return AcpWire.EmptyObject;
    }

    private async Task<JsonElement> HandleSetModelAsync(JsonElement @params, CancellationToken ct)
    {
        EnsureInitialized();
        var sessionId = RequireSessionId(@params);
        var modelId = RequireNonEmptyString(@params, "modelId");

        try
        {
            _ = await _backend.SetModelAsync(sessionId, modelId, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            throw new AcpInvalidParamsException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new AcpInvalidParamsException(ex.Message);
        }

        return AcpWire.EmptyObject;
    }

    private async Task<JsonElement> HandleSessionPromptAsync(JsonElement @params, CancellationToken ct)
    {
        EnsureInitialized();
        var sessionId = RequireSessionId(@params);
        if (!@params.TryGetProperty("prompt", out var promptEl) || promptEl.ValueKind != JsonValueKind.Array || promptEl.GetArrayLength() == 0)
        {
            throw new AcpInvalidParamsException("session/prompt requires a non-empty 'prompt' content block array.");
        }

        var (promptText, attachmentIds) = await ParsePromptBlocksAsync(sessionId, promptEl, ct).ConfigureAwait(false);

        var activeTurn = new ActivePromptTurn(ct);
        if (!_activeTurns.TryAdd(sessionId, activeTurn))
        {
            activeTurn.Dispose();
            throw new AcpInvalidParamsException($"Session '{sessionId:D}' already has a prompt turn in flight.");
        }

        try
        {
            // Open the live session event subscription BEFORE submitting the interaction so zero early
            // events (thoughts, quick tool calls, plans, approvals) can slip past before attach.
            var eventChannel = Channel.CreateUnbounded<AcpBackendEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            using var eventReaderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var eventPumpTask = PumpBackendEventsToChannelAsync(sessionId, eventChannel.Writer, eventReaderCts.Token);

            AcpPromptSubmission submission;
            try
            {
                submission = await _backend.SubmitPromptAsync(sessionId, promptText, attachmentIds, activeTurn.TurnToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (activeTurn.CancelRequested)
            {
                eventReaderCts.Cancel();
                return AcpWire.ToParams(new AcpPromptResult("cancelled"));
            }
            catch (KeyNotFoundException ex)
            {
                eventReaderCts.Cancel();
                throw new AcpInvalidParamsException(ex.Message);
            }
            catch (ArgumentException ex)
            {
                eventReaderCts.Cancel();
                throw new AcpInvalidParamsException(ex.Message);
            }

            activeTurn.RunId = submission.RunId;
            if (activeTurn.CancelRequested)
            {
                await _backend.CancelRunAsync(sessionId, submission.RunId, CancellationToken.None).ConfigureAwait(false);
            }

            var sessionIdStr = sessionId.ToString("D");
            var turnState = new PromptStreamCoordinator(this, sessionId, sessionIdStr, submission.RunId, activeTurn);

            // Process live session events (thoughts, tool calls, plans, approval gates) concurrently
            // alongside the durable answer RunStream.
            var eventConsumerTask = turnState.ConsumeEventsLoopAsync(eventChannel.Reader, eventReaderCts.Token);
            var approvalPollerTask = turnState.PollApprovalsAndSupervisionLoopAsync(eventReaderCts.Token);

            AcpRunStreamFrame? terminalFrame = null;
            try
            {
                await foreach (var frame in _backend.FollowRunStreamAsync(
                    sessionId,
                    submission.RunId,
                    submission.TurnId,
                    afterSequence: 0,
                    activeTurn.TurnToken).ConfigureAwait(false))
                {
                    if (frame.Kind == "answer.delta" && !string.IsNullOrEmpty(frame.Delta))
                    {
                        turnState.MarkAnswerChunkSent();
                        await SendSessionUpdateAsync(
                            sessionIdStr,
                            AcpWire.ToParams(new AcpMessageChunkUpdatePayload(
                                "agent_message_chunk",
                                new AcpTextContentPayload("text", frame.Delta))),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (frame.Kind is "done" or "error")
                    {
                        terminalFrame = frame;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (activeTurn.CancelRequested && !ct.IsCancellationRequested)
            {
                await _backend.CancelRunAsync(sessionId, submission.RunId, CancellationToken.None).ConfigureAwait(false);
                terminalFrame = new AcpRunStreamFrame(0, "done", FinishReason: "cancelled");
            }

            var wasCancelled = activeTurn.CancelRequested
                || string.Equals(terminalFrame?.FinishReason, "cancelled", StringComparison.OrdinalIgnoreCase);

            // Deterministic trailing-event flush BEFORE sending session/prompt result:
            // wait until the event consumer has processed through run.LastEventSequence so no trailing
            // tool_call_update, plan, or thought chunk arrives after stopReason!
            if (!wasCancelled)
            {
                await turnState.WaitForTrailingEventsAsync(TrailingEventDrainTimeout, ct).ConfigureAwait(false);
            }

            eventReaderCts.Cancel();
            try
            {
                await Task.WhenAll(eventPumpTask, eventConsumerTask, approvalPollerTask)
                    .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Bounded shutdown of event/poller background tasks.
            }

            if (wasCancelled)
            {
                return AcpWire.ToParams(new AcpPromptResult("cancelled"));
            }

            if (terminalFrame?.Kind == "error")
            {
                var stopFromError = MapErrorCategoryToStopReason(terminalFrame.ErrorCategory);
                if (stopFromError is not null)
                {
                    return AcpWire.ToParams(new AcpPromptResult(stopFromError));
                }

                throw new AcpRpcErrorException(
                    JsonRpcConnection.InternalError,
                    terminalFrame.SafeErrorMessage ?? "The TinadecCore run failed.");
            }

            // Fallback deduplication guard: if an assistant reply was persisted without streaming
            // answer.delta frames, emit that assistant message once before completing the turn.
            if (!turnState.AnyAnswerChunkSent)
            {
                var messages = await _backend.ListHistoryAsync(sessionId, ct).ConfigureAwait(false);
                var latestAssistant = messages.LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
                if (latestAssistant is not null && !string.IsNullOrEmpty(latestAssistant.Content))
                {
                    await SendSessionUpdateAsync(
                        sessionIdStr,
                        AcpWire.ToParams(new AcpMessageChunkUpdatePayload(
                            "agent_message_chunk",
                            new AcpTextContentPayload("text", latestAssistant.Content))),
                        ct).ConfigureAwait(false);
                }
            }

            var stopReason = MapFinishReasonToStopReason(terminalFrame?.FinishReason);
            return AcpWire.ToParams(new AcpPromptResult(stopReason));
        }
        finally
        {
            _activeTurns.TryRemove(sessionId, out _);
            activeTurn.Dispose();
        }
    }

    private async Task<(string Content, IReadOnlyList<Guid> AttachmentIds)> ParsePromptBlocksAsync(
        Guid sessionId,
        JsonElement promptArray,
        CancellationToken ct)
    {
        var textParts = new List<string>();
        var attachmentIds = new List<Guid>();
        var imageIndex = 0;

        foreach (var block in promptArray.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                throw new AcpInvalidParamsException("Every prompt item must be a JSON object.");
            }

            var type = ReadOptionalString(block, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new AcpInvalidParamsException("Every prompt content block requires a 'type' string.");
            }

            switch (type)
            {
                case "text":
                {
                    if (!block.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                    {
                        throw new AcpInvalidParamsException("Prompt 'text' block requires a string 'text' property.");
                    }

                    var text = textEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text)) textParts.Add(text);
                    break;
                }

                case "resource":
                {
                    var formatted = FormatEmbeddedResourceBlock(block);
                    textParts.Add(formatted);
                    break;
                }

                case "resource_link":
                {
                    var uri = ReadOptionalString(block, "uri");
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        throw new AcpInvalidParamsException("Prompt 'resource_link' block requires a non-empty 'uri'.");
                    }

                    var name = ReadOptionalString(block, "name");
                    var description = ReadOptionalString(block, "description");
                    var label = string.IsNullOrWhiteSpace(name) ? uri : $"{name} ({uri})";
                    textParts.Add(string.IsNullOrWhiteSpace(description)
                        ? $"[Resource Link: {label}]"
                        : $"[Resource Link: {label} — {description}]");
                    break;
                }

                case "image":
                {
                    var mimeType = ReadOptionalString(block, "mimeType")?.Trim();
                    var base64Data = ReadOptionalString(block, "data")?.Trim();
                    if (string.IsNullOrWhiteSpace(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AcpInvalidParamsException("Prompt 'image' block requires an 'image/*' mimeType.");
                    }

                    if (string.IsNullOrWhiteSpace(base64Data))
                    {
                        throw new AcpInvalidParamsException("Prompt 'image' block requires non-empty Base64 'data'.");
                    }

                    byte[] decoded;
                    try
                    {
                        decoded = Convert.FromBase64String(base64Data);
                    }
                    catch (FormatException)
                    {
                        throw new AcpInvalidParamsException("Prompt 'image' block contains malformed Base64 'data'.");
                    }

                    if (decoded.Length == 0 || decoded.Length > AttachmentEndpoints.MaxAttachmentBytes)
                    {
                        throw new AcpInvalidParamsException($"Prompt 'image' byte length must be between 1 and {AttachmentEndpoints.MaxAttachmentBytes}.");
                    }

                    imageIndex++;
                    var ext = ImageExtensionForMime(mimeType);
                    var fileName = $"acp-image-{imageIndex}.{ext}";
                    Guid attachmentId;
                    try
                    {
                        attachmentId = await _backend.StoreAttachmentAsync(sessionId, fileName, mimeType, decoded, ct).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        throw new AcpInvalidParamsException(ex.Message);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new AcpInvalidParamsException(ex.Message);
                    }

                    attachmentIds.Add(attachmentId);
                    break;
                }

                default:
                    throw new AcpInvalidParamsException($"Unsupported prompt content block type '{type}'.");
            }
        }

        var combinedText = string.Join("\n\n", textParts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrWhiteSpace(combinedText) && attachmentIds.Count == 0)
        {
            throw new AcpInvalidParamsException("session/prompt content blocks carried neither text nor attachments.");
        }

        // Core's interaction admission requires non-empty text to start an agent run (an empty-text
        // attachment post only appends to transcript without starting a run).
        if (string.IsNullOrWhiteSpace(combinedText) && attachmentIds.Count > 0)
        {
            combinedText = "[Analyze the attached image(s).]";
        }

        return (combinedText, attachmentIds);
    }

    private static string FormatEmbeddedResourceBlock(JsonElement block)
    {
        var container = block.TryGetProperty("resource", out var resObj) && resObj.ValueKind == JsonValueKind.Object
            ? resObj
            : block;

        var uri = ReadOptionalString(container, "uri");
        var text = ReadOptionalString(container, "text");
        if (string.IsNullOrWhiteSpace(uri) && string.IsNullOrWhiteSpace(text))
        {
            throw new AcpInvalidParamsException("Prompt 'resource' block requires 'resource.uri' or 'resource.text'.");
        }

        if (!string.IsNullOrWhiteSpace(uri) && !string.IsNullOrWhiteSpace(text))
        {
            return $"[Resource: {uri}]\n{text}";
        }

        return !string.IsNullOrWhiteSpace(text) ? text! : $"[Resource: {uri}]";
    }

    private async Task PumpBackendEventsToChannelAsync(
        Guid sessionId,
        ChannelWriter<AcpBackendEvent> writer,
        CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _backend.SubscribeSessionEventsAsync(sessionId, ct).ConfigureAwait(false))
            {
                writer.TryWrite(ev);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ACP session event stream ended for session {SessionId}.", sessionId);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private Task SendSessionUpdateAsync(string sessionId, JsonElement updatePayload, CancellationToken ct) =>
        _connection.NotifyAsync(
            "session/update",
            AcpWire.ToParams(new AcpSessionUpdateParams(sessionId, updatePayload)),
            ct);

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new AcpInvalidParamsException("ACP connection must call 'initialize' before session methods.");
        }
    }

    private static string ValidateCwd(JsonElement @params)
    {
        var cwd = RequireNonEmptyString(@params, "cwd");
        if (!Path.IsPathFullyQualified(cwd))
        {
            throw new AcpInvalidParamsException($"'cwd' must be an absolute path (received '{cwd}').");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AcpInvalidParamsException($"'cwd' is not a valid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new AcpInvalidParamsException($"'cwd' directory does not exist: '{fullPath}'.");
        }

        return WorkspacePathSpelling.Canonical(fullPath);
    }

    private static Guid RequireSessionId(JsonElement @params)
    {
        if (!TryReadSessionId(@params, out var sessionId))
        {
            throw new AcpInvalidParamsException("A valid GUID 'sessionId' is required.");
        }

        return sessionId;
    }

    private static bool TryReadSessionId(JsonElement @params, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (@params.ValueKind != JsonValueKind.Object) return false;
        if (!@params.TryGetProperty("sessionId", out var el) || el.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(el.GetString(), out sessionId) && sessionId != Guid.Empty;
    }

    private static string RequireNonEmptyString(JsonElement @params, string propertyName)
    {
        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty(propertyName, out var prop)
            || prop.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(prop.GetString()))
        {
            throw new AcpInvalidParamsException($"Parameter '{propertyName}' must be a non-empty string.");
        }

        return prop.GetString()!.Trim();
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static JsonElement ToSessionResultElement(AcpSessionSnapshot snapshot) =>
        AcpWire.ToParams(new AcpServerSessionResult(
            snapshot.SessionId.ToString("D"),
            snapshot.Modes,
            snapshot.Models));

    private static string ImageExtensionForMime(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/bmp" => "bmp",
        _ => "img"
    };

    private static string? MapErrorCategoryToStopReason(string? errorCategory)
    {
        if (string.IsNullOrWhiteSpace(errorCategory)) return null;
        return errorCategory.Trim().ToLowerInvariant() switch
        {
            "run_token_budget_exhausted" or "tool_round_limit" or "max_tokens" => "max_tokens",
            "refusal" or "content_filter" or "policy_refusal" => "refusal",
            "cancelled" => "cancelled",
            _ => null
        };
    }

    private static string MapFinishReasonToStopReason(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason)) return "end_turn";
        return finishReason.Trim().ToLowerInvariant() switch
        {
            "cancelled" => "cancelled",
            "max_tokens" or "length" or "run_token_budget_exhausted" => "max_tokens",
            "refusal" or "content_filter" => "refusal",
            _ => "end_turn"
        };
    }

    private sealed class ActivePromptTurn(CancellationToken requestToken) : IDisposable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        private int _cancelRequested;

        public Guid? RunId { get; set; }

        public bool CancelRequested => Volatile.Read(ref _cancelRequested) != 0;

        public CancellationToken TurnToken => _cts.Token;

        public void MarkCancelled()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) == 0)
            {
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    private sealed class PromptStreamCoordinator(
        AcpAgentServer server,
        Guid sessionId,
        string sessionIdStr,
        Guid runId,
        ActivePromptTurn activeTurn)
    {
        private readonly ConcurrentDictionary<string, byte> _emittedToolCalls = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _settledToolCalls = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _activeApprovalRaces = new();
        private readonly ConcurrentDictionary<Guid, byte> _handledApprovals = new();
        private readonly ConcurrentDictionary<string, (string Title, string Status, int Order)> _fallbackTaskPlan = new(StringComparer.OrdinalIgnoreCase);
        private long _highestObservedSequence;
        private int _anyAnswerChunkSent;
        private int _taskPlanOrder;
        private bool _hasExplicitSessionPlan;
        private int _idleParkedPolls;

        public bool AnyAnswerChunkSent => Volatile.Read(ref _anyAnswerChunkSent) != 0;

        public void MarkAnswerChunkSent() => Interlocked.Exchange(ref _anyAnswerChunkSent, 1);

        public async Task ConsumeEventsLoopAsync(ChannelReader<AcpBackendEvent> reader, CancellationToken ct)
        {
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (ev.RunId is { } evRunId && evRunId != runId) continue;
                    if (ev.Sequence > 0)
                    {
                        InterlockedMax(ref _highestObservedSequence, ev.Sequence);
                    }

                    await HandleBackendEventAsync(ev, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        public async Task PollApprovalsAndSupervisionLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !activeTurn.CancelRequested)
                {
                    var pending = await server._backend.ListPendingApprovalsAsync(sessionId, runId, ct).ConfigureAwait(false);
                    foreach (var item in pending)
                    {
                        if (_handledApprovals.TryAdd(item.ApprovalId, 0))
                        {
                            _ = Task.Run(() => BridgeApprovalAsync(item.ApprovalId, ct), CancellationToken.None);
                        }
                    }

                    if (pending.Count == 0 && _activeApprovalRaces.IsEmpty)
                    {
                        var status = await server._backend.GetRunStatusAsync(sessionId, runId, ct).ConfigureAwait(false);
                        if (status is not null
                            && status.IsLeaseHeld != true
                            && status.Status.Trim().ToLowerInvariant() is "awaiting_user" or "awaiting_approval" or "awaiting_delegate")
                        {
                            if (Interlocked.Increment(ref _idleParkedPolls) >= 5)
                            {
                                Interlocked.Exchange(ref _idleParkedPolls, 0);
                                var recheck = await server._backend.ListPendingApprovalsAsync(sessionId, runId, ct).ConfigureAwait(false);
                                if (recheck.Count == 0 && _activeApprovalRaces.IsEmpty)
                                {
                                    await server._backend.ResumeAwaitingUserSupervisionAsync(sessionId, runId, ct).ConfigureAwait(false);
                                }
                            }
                        }
                        else
                        {
                            Interlocked.Exchange(ref _idleParkedPolls, 0);
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _idleParkedPolls, 0);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(120), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                server._logger?.LogDebug(ex, "Approval/supervision poll loop ended for run {RunId}.", runId);
            }
        }

        public async Task WaitForTrailingEventsAsync(TimeSpan timeout, CancellationToken ct)
        {
            long targetSequence = 0;
            try
            {
                var status = await server._backend.GetRunStatusAsync(sessionId, runId, ct).ConfigureAwait(false);
                if (status is not null) targetSequence = status.LastEventSequence;
            }
            catch
            {
                return;
            }

            if (targetSequence <= 0) return;

            var deadline = DateTime.UtcNow + timeout;
            while (Interlocked.Read(ref _highestObservedSequence) < targetSequence && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }

        private async Task HandleBackendEventAsync(AcpBackendEvent ev, CancellationToken ct)
        {
            switch (ev.EventType)
            {
                case "model.output.delta":
                {
                    var channel = ReadOptionalString(ev.Payload, "channel");
                    var delta = ReadOptionalString(ev.Payload, "delta");
                    if (string.Equals(channel, "reasoning", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(delta))
                    {
                        await server.SendSessionUpdateAsync(
                            sessionIdStr,
                            AcpWire.ToParams(new AcpMessageChunkUpdatePayload(
                                "agent_thought_chunk",
                                new AcpTextContentPayload("text", delta))),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    break;
                }

                case "tool.execution.requested":
                {
                    var executionIdStr = ReadOptionalString(ev.Payload, "execution_id");
                    if (string.IsNullOrWhiteSpace(executionIdStr) || !_emittedToolCalls.TryAdd(executionIdStr, 0)) break;

                    var toolId = ReadOptionalString(ev.Payload, "tool_id") ?? "tool";
                    var requiresApproval = ev.Payload.ValueKind == JsonValueKind.Object
                        && ev.Payload.TryGetProperty("requires_approval", out var reqEl)
                        && reqEl.ValueKind == JsonValueKind.True;
                    var summary = ReadOptionalString(ev.Payload, "arguments_summary") ?? ev.Summary ?? toolId;
                    var resourcePath = ReadOptionalString(ev.Payload, "resource_path");
                    var command = ReadOptionalString(ev.Payload, "command");
                    var cwd = ReadOptionalString(ev.Payload, "cwd");
                    var query = ReadOptionalString(ev.Payload, "query");

                    AcpToolExecutionDetail? detail = null;
                    if (Guid.TryParse(executionIdStr, out var execGuid))
                    {
                        detail = await server._backend.GetToolExecutionDetailAsync(sessionId, runId, execGuid, ct).ConfigureAwait(false);
                    }

                    var rawInput = detail?.RawInput ?? RemoteHttpAcpCoreBackend.BuildSyntheticRawInput(resourcePath, command, cwd, query);
                    var locations = BuildLocations(resourcePath ?? detail?.ResourcePath, rawInput);
                    var kind = AcpToolKindMapper.Normalize(toolId, summary);

                    await server.SendSessionUpdateAsync(
                        sessionIdStr,
                        AcpWire.ToParams(new AcpToolCallUpdatePayload(
                            "tool_call",
                            executionIdStr,
                            Title: summary,
                            Kind: kind,
                            Status: requiresApproval ? "pending" : "in_progress",
                            Locations: locations,
                            RawInput: rawInput)),
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                case "tool.execution.completed":
                case "tool.execution.failed":
                case "tool.execution.outcome_unknown":
                case "worker.tool_failed":
                {
                    var executionIdStr = ReadOptionalString(ev.Payload, "execution_id");
                    if (string.IsNullOrWhiteSpace(executionIdStr) || !_settledToolCalls.TryAdd(executionIdStr, 0)) break;

                    var toolSuccess = ev.EventType == "tool.execution.completed"
                        && (!ev.Payload.TryGetProperty("tool_success", out var tsEl) || tsEl.ValueKind != JsonValueKind.False);
                    var status = toolSuccess ? "completed" : "failed";

                    AcpToolExecutionDetail? detail = null;
                    if (Guid.TryParse(executionIdStr, out var execGuid))
                    {
                        detail = await server._backend.GetToolExecutionDetailAsync(sessionId, runId, execGuid, ct).ConfigureAwait(false);
                    }

                    var text = detail?.ResultText
                        ?? ReadOptionalString(ev.Payload, "message")
                        ?? ev.Summary
                        ?? status;

                    if (!_emittedToolCalls.TryAdd(executionIdStr, 0))
                    {
                        // Normal order: tool_call already emitted.
                    }
                    else
                    {
                        var toolId = ReadOptionalString(ev.Payload, "tool_id") ?? detail?.ToolId ?? "tool";
                        await server.SendSessionUpdateAsync(
                            sessionIdStr,
                            AcpWire.ToParams(new AcpToolCallUpdatePayload(
                                "tool_call",
                                executionIdStr,
                                Title: detail?.Summary ?? toolId,
                                Kind: AcpToolKindMapper.Normalize(toolId),
                                Status: "in_progress",
                                Locations: BuildLocations(detail?.ResourcePath, detail?.RawInput),
                                RawInput: detail?.RawInput)),
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    await server.SendSessionUpdateAsync(
                        sessionIdStr,
                        AcpWire.ToParams(new AcpToolCallUpdatePayload(
                            "tool_call_update",
                            executionIdStr,
                            Status: status,
                            RawOutput: detail?.RawOutput,
                            Content: string.IsNullOrWhiteSpace(text)
                                ? null
                                : [new AcpToolCallContentItem("content", new AcpTextContentPayload("text", text))])),
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                case "session.plan.updated":
                {
                    var entries = ExtractSessionPlanEntries(ev.Payload);
                    if (entries.Count > 0)
                    {
                        _hasExplicitSessionPlan = true;
                        await server.SendSessionUpdateAsync(
                            sessionIdStr,
                            AcpWire.ToParams(new AcpPlanUpdatePayload("plan", entries)),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    break;
                }

                case "task.dispatched":
                case "task.assigned":
                case "worker.completed":
                case "worker.failed":
                case "step.result.created":
                {
                    if (_hasExplicitSessionPlan) break;
                    var taskId = ReadOptionalString(ev.Payload, "task_id") ?? ReadOptionalString(ev.Payload, "task_node_id");
                    if (string.IsNullOrWhiteSpace(taskId)) break;

                    var title = ReadOptionalString(ev.Payload, "title")
                        ?? (_fallbackTaskPlan.TryGetValue(taskId, out var prev) ? prev.Title : ev.Summary ?? taskId);
                    var mappedStatus = ev.EventType switch
                    {
                        "worker.completed" or "step.result.created" => "completed",
                        "worker.failed" => "completed",
                        _ => "in_progress"
                    };
                    var order = _fallbackTaskPlan.TryGetValue(taskId, out var existing)
                        ? existing.Order
                        : Interlocked.Increment(ref _taskPlanOrder);
                    _fallbackTaskPlan[taskId] = (title, mappedStatus, order);

                    var planEntries = _fallbackTaskPlan.Values
                        .OrderBy(x => x.Order)
                        .Select(x => new AcpPlanEntryPayload(x.Title, "medium", x.Status))
                        .ToArray();
                    if (planEntries.Length > 0)
                    {
                        await server.SendSessionUpdateAsync(
                            sessionIdStr,
                            AcpWire.ToParams(new AcpPlanUpdatePayload("plan", planEntries)),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    break;
                }

                case "approval.requested":
                {
                    var approvalIdStr = ReadOptionalString(ev.Payload, "approval_id")
                        ?? ReadOptionalString(ev.Payload, "permission_request_id");
                    if (Guid.TryParse(approvalIdStr, out var approvalId) && _handledApprovals.TryAdd(approvalId, 0))
                    {
                        _ = Task.Run(() => BridgeApprovalAsync(approvalId, ct), CancellationToken.None);
                    }
                    break;
                }

                case "approval.decided":
                case "governance.permission_decided":
                {
                    var resolvedStr = ReadOptionalString(ev.Payload, "approval_id")
                        ?? ReadOptionalString(ev.Payload, "permission_request_id");
                    if (Guid.TryParse(resolvedStr, out var resolvedId)
                        && _activeApprovalRaces.TryGetValue(resolvedId, out var raceTcs))
                    {
                        raceTcs.TrySetResult(true);
                    }
                    break;
                }
            }
        }

        private async Task BridgeApprovalAsync(Guid approvalId, CancellationToken ct)
        {
            var externalResolved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeApprovalRaces[approvalId] = externalResolved;

            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(activeTurn.TurnToken, ct);
            using var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
            Task? externalPollTask = null;

            try
            {
                // Re-verify live pending list first: command-prefix allow_always rules and run-scoped
                // pre-authorizations mint the approval synchronously in Core BEFORE emitting approval.requested!
                AcpPendingApproval? approval = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var pendingList = await server._backend.ListPendingApprovalsAsync(sessionId, runId, ct).ConfigureAwait(false);
                    approval = pendingList.FirstOrDefault(p => p.ApprovalId == approvalId);
                    if (approval is not null)
                    {
                        var awaitingParkEvidence = string.Equals(approval.Kind, "permission", StringComparison.OrdinalIgnoreCase)
                            && string.IsNullOrWhiteSpace(approval.Command)
                            && string.IsNullOrWhiteSpace(approval.ResourcePath);
                        if (!awaitingParkEvidence || attempt == 4)
                        {
                            break;
                        }
                    }

                    if (attempt < 4)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
                    }
                }

                if (approval is null || externalResolved.Task.IsCompleted)
                {
                    return;
                }

                externalPollTask = WatchExternalApprovalResolutionAsync(approvalId, externalResolved, watcherCts.Token);
                using var externalReg = externalResolved.Task.ContinueWith(
                    _ =>
                    {
                        try { raceCts.Cancel(); } catch (ObjectDisposedException) { }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var toolCallId = approval.ExecutionId?.ToString("D") ?? approval.ApprovalId.ToString("D");
                var kind = AcpToolKindMapper.Normalize(approval.ToolId, approval.Summary);
                JsonElement? rawInput = null;
                if (!string.IsNullOrWhiteSpace(approval.ArgumentsJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(approval.ArgumentsJson);
                        rawInput = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                    }
                }
                rawInput ??= RemoteHttpAcpCoreBackend.BuildSyntheticRawInput(approval.ResourcePath, approval.Command, approval.Cwd, null);
                var locations = BuildLocations(approval.ResourcePath, rawInput);

                var permissionParams = AcpWire.ToParams(new AcpRequestPermissionParams(
                    sessionIdStr,
                    AcpWire.ToParams(new AcpPermissionToolCallPayload(
                        toolCallId,
                        string.IsNullOrWhiteSpace(approval.Summary) ? approval.ToolId : approval.Summary,
                        kind,
                        "pending",
                        locations,
                        rawInput)),
                    [
                        new AcpPermissionOption("allow_once", "Allow once", "allow_once"),
                        new AcpPermissionOption("allow_always", "Always allow", "allow_always"),
                        new AcpPermissionOption("reject_once", "Reject", "reject_once")
                    ]));

                JsonElement clientResponse;
                try
                {
                    clientResponse = await server._connection
                        .RequestAsync("session/request_permission", permissionParams, PermissionRequestTimeout, raceCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (externalResolved.Task.IsCompleted)
                {
                    // Mode B coexistence: Desktop UI already decided this approval! Unblock cleanly
                    // without sending a second conflicting decision.
                    return;
                }
                catch (OperationCanceledException) when (activeTurn.CancelRequested || ct.IsCancellationRequested)
                {
                    // Turn cancelled via session/cancel or shutdown.
                    return;
                }
                catch (Exception ex)
                {
                    server._logger?.LogWarning(ex, "session/request_permission failed for approval {ApprovalId}; failing closed.", approvalId);
                    await SubmitDecisionSafelyAsync(approval, externalResolved, new AcpApprovalDecisionInput("rejected", "once", "ACP client permission request failed.")).ConfigureAwait(false);
                    return;
                }

                if (externalResolved.Task.IsCompleted)
                {
                    return;
                }

                var decisionInput = MapClientPermissionOutcome(approval, rawInput, clientResponse);
                await SubmitDecisionSafelyAsync(approval, externalResolved, decisionInput).ConfigureAwait(false);
            }
            finally
            {
                _activeApprovalRaces.TryRemove(approvalId, out _);
                try { watcherCts.Cancel(); } catch (ObjectDisposedException) { }
                if (externalPollTask is not null)
                {
                    try { await externalPollTask.ConfigureAwait(false); } catch { }
                }
            }
        }

        private async Task WatchExternalApprovalResolutionAsync(
            Guid approvalId,
            TaskCompletionSource<bool> externalResolved,
            CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !externalResolved.Task.IsCompleted)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
                    var pending = await server._backend.ListPendingApprovalsAsync(sessionId, runId, ct).ConfigureAwait(false);
                    if (!pending.Any(p => p.ApprovalId == approvalId))
                    {
                        externalResolved.TrySetResult(true);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        private async Task SubmitDecisionSafelyAsync(
            AcpPendingApproval approval,
            TaskCompletionSource<bool> externalResolved,
            AcpApprovalDecisionInput input)
        {
            // Wait until FullDuplexRunEngine has finished persisting its awaiting checkpoint ("tool-prepared" /
            // "tool-awaiting") AND released its run lease before committing DecideApproval; otherwise an instant
            // response can re-enqueue the run while the engine still holds the lease prior to checkpoint save.
            var parkDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            AcpRunStatusSnapshot? settledSnap = null;
            while (DateTime.UtcNow < parkDeadline && !externalResolved.Task.IsCompleted && !activeTurn.CancelRequested)
            {
                var runSnap = await server._backend.GetRunStatusAsync(sessionId, runId, CancellationToken.None).ConfigureAwait(false);
                var st = runSnap?.Status?.Trim().ToLowerInvariant();
                if (st is "completed" or "failed" or "cancelled")
                {
                    settledSnap = runSnap;
                    break;
                }

                if (st is "awaiting_approval" or "awaiting_user" or "awaiting_delegate" && runSnap?.IsLeaseHeld != true)
                {
                    settledSnap = runSnap;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None).ConfigureAwait(false);
            }

            if (settledSnap is { IsLeaseHeld: null } && !externalResolved.Task.IsCompleted && !activeTurn.CancelRequested)
            {
                // Mode B HTTP does not project LeaseOwner over REST; allow the remote engine's finally block to
                // finish saving checkpoint + releasing the run lease before posting /approvals/{id}/decision.
                await Task.Delay(TimeSpan.FromMilliseconds(150), CancellationToken.None).ConfigureAwait(false);
            }

            if (externalResolved.Task.IsCompleted)
            {
                return;
            }

            var stillPending = await server._backend.ListPendingApprovalsAsync(sessionId, runId, CancellationToken.None).ConfigureAwait(false);
            var freshApproval = stillPending.FirstOrDefault(p => p.ApprovalId == approval.ApprovalId);
            if (freshApproval is null)
            {
                return;
            }

            if (string.Equals(input.Decision, "approved", StringComparison.OrdinalIgnoreCase)
                && string.Equals(input.Scope, "run", StringComparison.OrdinalIgnoreCase)
                && input.RememberRule is null
                && CommandPrefixRules.IsRuleTool(freshApproval.ToolId))
            {
                JsonElement? freshRawInput = null;
                if (!string.IsNullOrWhiteSpace(freshApproval.ArgumentsJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(freshApproval.ArgumentsJson);
                        freshRawInput = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                    }
                }

                input = BuildAllowAlwaysInput(freshApproval, freshRawInput);
            }

            var outcome = await server._backend.DecideApprovalAsync(sessionId, freshApproval.ApprovalId, input, CancellationToken.None).ConfigureAwait(false);
            if (outcome.Applied || outcome.AlreadyDecidedConflict)
            {
                return;
            }

            if (outcome.InvalidRuleRejected && string.Equals(input.Decision, "approved", StringComparison.OrdinalIgnoreCase))
            {
                // Safe fallback when remember_rule or run-scope is ineligible (e.g. complex shell pipeline or spec_propose).
                var fallbackScope = CoreVirtualToolPolicy.IsSpecPropose(approval.ToolId) ? "once" : "run";
                var retry = await server._backend.DecideApprovalAsync(
                    sessionId,
                    approval.ApprovalId,
                    new AcpApprovalDecisionInput("approved", fallbackScope),
                    CancellationToken.None).ConfigureAwait(false);
                if (!retry.Applied && !retry.AlreadyDecidedConflict && fallbackScope != "once")
                {
                    await server._backend.DecideApprovalAsync(
                        sessionId,
                        approval.ApprovalId,
                        new AcpApprovalDecisionInput("approved", "once"),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private AcpApprovalDecisionInput MapClientPermissionOutcome(
            AcpPendingApproval approval,
            JsonElement? rawInput,
            JsonElement response)
        {
            string? outcomeKind = null;
            string? optionId = null;

            if (response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("outcome", out var outcomeEl)
                && outcomeEl.ValueKind == JsonValueKind.Object)
            {
                outcomeKind = ReadOptionalString(outcomeEl, "outcome");
                optionId = ReadOptionalString(outcomeEl, "optionId");
            }

            if (!string.Equals(outcomeKind, "selected", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(optionId))
            {
                return new AcpApprovalDecisionInput("rejected", "once", "Permission cancelled by ACP client.");
            }

            return optionId.Trim() switch
            {
                "allow_once" => new AcpApprovalDecisionInput("approved", "once"),
                "allow_always" => BuildAllowAlwaysInput(approval, rawInput),
                "reject_once" => new AcpApprovalDecisionInput("rejected", "once", "Rejected by user in ACP client."),
                _ => new AcpApprovalDecisionInput("rejected", "once", $"Unrecognized ACP permission option '{optionId}'; failing closed.")
            };
        }

        private AcpApprovalDecisionInput BuildAllowAlwaysInput(AcpPendingApproval approval, JsonElement? rawInput)
        {
            if (CoreVirtualToolPolicy.IsSpecPropose(approval.ToolId))
            {
                return new AcpApprovalDecisionInput("approved", "once");
            }

            ApprovalRememberRuleInputDto? rememberRule = null;
            if (CommandPrefixRules.IsRuleTool(approval.ToolId))
            {
                var command = approval.Command;
                if (string.IsNullOrWhiteSpace(command) && rawInput is { ValueKind: JsonValueKind.Object } inputObj)
                {
                    command = CommandPrefixRules.CommandOf(approval.ToolId, inputObj);
                }

                if (!string.IsNullOrWhiteSpace(command) && CommandPrefixRules.IsSimple(command))
                {
                    var words = CommandPrefixRules.Words(command);
                    if (words.Length > 0)
                    {
                        var prefix = words[0];
                        if (!prefix.Contains('*', StringComparison.Ordinal)
                            && CommandPrefixRules.PrefixError(prefix) is null
                            && (approval.SessionId ?? sessionId) != Guid.Empty)
                        {
                            rememberRule = new ApprovalRememberRuleInputDto
                            {
                                Kind = ApprovalRuleKinds.CommandPrefix,
                                Pattern = prefix,
                                Scope = "session"
                            };
                        }
                    }
                }
            }

            return new AcpApprovalDecisionInput("approved", "run", RememberRule: rememberRule);
        }

        private static IReadOnlyList<AcpToolLocation>? BuildLocations(string? primaryPath, JsonElement? rawInput)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<AcpToolLocation>();

            void Add(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                var trimmed = p.Trim();
                if (seen.Add(trimmed)) list.Add(new AcpToolLocation(trimmed));
            }

            Add(primaryPath);
            if (rawInput is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (var key in new[] { "path", "filepath", "file_path", "filePath", "target_path", "source_path" })
                {
                    Add(ReadOptionalString(obj, key));
                }
            }

            return list.Count == 0 ? null : list;
        }

        private static IReadOnlyList<AcpPlanEntryPayload> ExtractSessionPlanEntries(JsonElement payload)
        {
            var entries = new List<AcpPlanEntryPayload>();
            if (payload.ValueKind != JsonValueKind.Object) return entries;
            var planEl = payload.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object ? p : payload;
            var phasesEl = planEl.TryGetProperty("phases", out var ph) && ph.ValueKind == JsonValueKind.Array
                ? ph
                : planEl.TryGetProperty("Phases", out var phPascal) && phPascal.ValueKind == JsonValueKind.Array
                    ? phPascal
                    : default;

            if (phasesEl.ValueKind != JsonValueKind.Array) return entries;

            foreach (var phase in phasesEl.EnumerateArray())
            {
                if (phase.ValueKind != JsonValueKind.Object) continue;
                var stepsEl = phase.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array
                    ? st
                    : phase.TryGetProperty("Steps", out var stPascal) && stPascal.ValueKind == JsonValueKind.Array
                        ? stPascal
                        : default;
                if (stepsEl.ValueKind != JsonValueKind.Array) continue;

                foreach (var step in stepsEl.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object) continue;
                    var title = ReadOptionalString(step, "title") ?? ReadOptionalString(step, "Title");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var rawStatus = (ReadOptionalString(step, "status") ?? ReadOptionalString(step, "Status") ?? "pending").Trim().ToLowerInvariant();
                    var acpStatus = rawStatus switch
                    {
                        "completed" or "skipped" or "done" => "completed",
                        "in_progress" or "running" or "active" => "in_progress",
                        _ => "pending"
                    };
                    entries.Add(new AcpPlanEntryPayload(title, "medium", acpStatus));
                }
            }

            return entries;
        }

        private static void InterlockedMax(ref long location, long candidate)
        {
            long current = Interlocked.Read(ref location);
            while (candidate > current)
            {
                var prev = Interlocked.CompareExchange(ref location, candidate, current);
                if (prev == current) break;
                current = prev;
            }
        }
    }
}
