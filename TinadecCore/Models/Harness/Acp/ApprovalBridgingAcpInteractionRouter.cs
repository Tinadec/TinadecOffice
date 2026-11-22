using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Round-2 governed ACP interaction router: bridges <c>session/request_permission</c> into
/// <see cref="IAcpApprovalBridge"/> so workspace/write-scope policies and interactive approval gates
/// govern external CLI/ACP harnesses. When no approval bridge is registered in a minimal host,
/// cleanly delegates to <see cref="RefusingAcpInteractionRouter"/>.
/// </summary>
internal sealed class ApprovalBridgingAcpInteractionRouter : IAcpInteractionRouter
{
    private const int MaxRawParamsChars = 8_192;

    private static readonly IReadOnlyList<string> AllowPriority = ["allow_once", "allow_always"];
    private static readonly IReadOnlyList<string> RejectPriority = ["reject_once", "reject_always"];
    private static readonly string[] SinglePathPropertyNames =
    [
        "path", "file_path", "filePath", "target_path", "targetPath",
        "target_file", "targetFile", "destination", "dest", "source", "src",
        "cwd", "directory", "working_directory", "workingDirectory"
    ];
    private static readonly string[] MultiPathPropertyNames = ["paths", "files", "file_paths", "filePaths"];

    private readonly AcpInteractionContext _context;
    private readonly IAcpApprovalBridge? _approvalBridge;
    private readonly RefusingAcpInteractionRouter _fallback;
    private readonly object _pendingGate = new();
    private readonly List<CancellationTokenSource> _pendingCts = [];
    private int _pendingCount;

    public ApprovalBridgingAcpInteractionRouter(
        AcpInteractionContext context,
        IAcpApprovalBridge? approvalBridge)
    {
        _context = context;
        _approvalBridge = approvalBridge;
        _fallback = new RefusingAcpInteractionRouter(context.SessionId);
    }

    public string SessionId => _context.SessionId;

    public int PendingCount => Volatile.Read(ref _pendingCount);

    public async Task<JsonElement> RouteAsync(
        string method,
        JsonElement @params,
        IAcpTurnObserver observer,
        CancellationToken cancellationToken)
    {
        if (method != "session/request_permission")
        {
            RefusingAcpInteractionRouter.RecordUnsupportedMethod(method);
            throw new AcpMethodNotFoundException(method);
        }

        if (_approvalBridge is null)
        {
            return await _fallback.RouteAsync(method, @params, observer, cancellationToken).ConfigureAwait(false);
        }

        var options = RefusingAcpInteractionRouter.ReadOptions(@params);
        var sessionId = ReadTopLevelString(@params, "sessionId") ?? _context.SessionId;
        var toolCallId = RefusingAcpInteractionRouter.ReadString(@params, "toolCall", "toolCallId");
        var title = RefusingAcpInteractionRouter.ReadString(@params, "toolCall", "title");
        var kind = RefusingAcpInteractionRouter.ReadString(@params, "toolCall", "kind");
        var affectedPaths = ExtractAffectedPaths(@params);
        var availableOptionKinds = options
            .Select(option => option.Kind)
            .Where(optionKind => !string.IsNullOrWhiteSpace(optionKind))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var checkRequest = new AcpPermissionCheckRequest(
            SessionId: sessionId,
            RunId: _context.RunId,
            TeammateHandle: _context.TeammateHandle,
            WorkingDirectory: _context.WorkingDirectory,
            WriteScope: _context.WriteScope,
            ToolCallId: toolCallId,
            Title: title,
            Kind: kind,
            AffectedPaths: affectedPaths)
        {
            AvailableOptionKinds = availableOptionKinds,
            RawParametersJson = SerializeBounded(@params)
        };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_pendingGate)
        {
            _pendingCts.Add(linkedCts);
        }

        // Increment before awaiting the approval bridge so AcpSession's drain window and idle
        // watchdog stay open while parked waiting on a human or delegate decision.
        Interlocked.Increment(ref _pendingCount);
        try
        {
            AcpPermissionDecision decision;
            try
            {
                decision = await _approvalBridge
                    .EvaluateOrWaitForApprovalAsync(checkRequest, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                observer.PermissionRefused(new AcpPermissionRefusal(toolCallId, title, null, "approval_cancelled"));
                return AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("cancelled")));
            }

            if (decision.Approved)
            {
                var allow = SelectAllowOption(options, decision.SelectedOptionKind);
                if (allow is not null)
                {
                    return AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("selected", allow.OptionId)));
                }

                // Fail closed when the peer offered no allow option to select.
                observer.PermissionRefused(new AcpPermissionRefusal(toolCallId, title, null, "no_allow_option"));
                return AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("cancelled")));
            }

            var reason = string.IsNullOrWhiteSpace(decision.Reason) ? "approval_denied" : decision.Reason;
            var reject = SelectRejectOption(options, decision.SelectedOptionKind);
            if (reject is null)
            {
                observer.PermissionRefused(new AcpPermissionRefusal(toolCallId, title, null, reason));
                return AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("cancelled")));
            }

            observer.PermissionRefused(new AcpPermissionRefusal(toolCallId, title, reject.OptionId, reason));
            return AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("selected", reject.OptionId)));
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
            lock (_pendingGate)
            {
                _pendingCts.Remove(linkedCts);
            }
        }
    }

    public void CloseAll()
    {
        CancellationTokenSource[] snapshot;
        lock (_pendingGate)
        {
            if (_pendingCts.Count == 0) return;
            snapshot = [.. _pendingCts];
            _pendingCts.Clear();
        }

        foreach (var cts in snapshot)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already completed and disposed by its RouteAsync call.
            }
        }
    }

    private static AcpPermissionOption? SelectAllowOption(IReadOnlyList<AcpPermissionOption> options, string? preferredKind)
    {
        if (!string.IsNullOrWhiteSpace(preferredKind)
            && preferredKind.StartsWith("allow", StringComparison.OrdinalIgnoreCase))
        {
            var matched = options.FirstOrDefault(option => string.Equals(option.Kind, preferredKind, StringComparison.OrdinalIgnoreCase));
            if (matched is not null) return matched;
        }

        foreach (var kind in AllowPriority)
        {
            var matched = options.FirstOrDefault(option => string.Equals(option.Kind, kind, StringComparison.Ordinal));
            if (matched is not null) return matched;
        }

        return options.FirstOrDefault(option => option.Kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase));
    }

    private static AcpPermissionOption? SelectRejectOption(IReadOnlyList<AcpPermissionOption> options, string? preferredKind)
    {
        if (!string.IsNullOrWhiteSpace(preferredKind)
            && preferredKind.StartsWith("reject", StringComparison.OrdinalIgnoreCase))
        {
            var matched = options.FirstOrDefault(option => string.Equals(option.Kind, preferredKind, StringComparison.OrdinalIgnoreCase));
            if (matched is not null) return matched;
        }

        foreach (var kind in RejectPriority)
        {
            var matched = options.FirstOrDefault(option => string.Equals(option.Kind, kind, StringComparison.Ordinal));
            if (matched is not null) return matched;
        }

        return options.FirstOrDefault(option => option.Kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> ExtractAffectedPaths(JsonElement @params)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddPath(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var trimmed = candidate.Trim();
            if (seen.Add(trimmed)) paths.Add(trimmed);
        }

        void ReadLocations(JsonElement container)
        {
            if (container.ValueKind != JsonValueKind.Object) return;
            if (!container.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array) return;
            foreach (var item in locations.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AddPath(item.GetString());
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    AddPath(ReadTopLevelString(item, "path"));
                    AddPath(ReadTopLevelString(item, "uri"));
                }
            }
        }

        void ReadRawInput(JsonElement container)
        {
            if (container.ValueKind != JsonValueKind.Object) return;
            if (!container.TryGetProperty("rawInput", out var rawInput) || rawInput.ValueKind != JsonValueKind.Object) return;

            foreach (var name in SinglePathPropertyNames)
            {
                AddPath(ReadTopLevelString(rawInput, name));
            }

            foreach (var name in MultiPathPropertyNames)
            {
                if (!rawInput.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String) AddPath(entry.GetString());
                }
            }
        }

        void ReadContentDiffs(JsonElement container)
        {
            if (container.ValueKind != JsonValueKind.Object) return;
            if (!container.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (string.Equals(ReadTopLevelString(block, "type"), "diff", StringComparison.Ordinal))
                {
                    AddPath(ReadTopLevelString(block, "path"));
                }
            }
        }

        if (@params.ValueKind == JsonValueKind.Object)
        {
            ReadLocations(@params);
            if (@params.TryGetProperty("toolCall", out var toolCall) && toolCall.ValueKind == JsonValueKind.Object)
            {
                ReadLocations(toolCall);
                ReadRawInput(toolCall);
                ReadContentDiffs(toolCall);
            }
        }

        return paths;
    }

    private static string? ReadTopLevelString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? SerializeBounded(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        var raw = element.GetRawText();
        return raw.Length <= MaxRawParamsChars ? raw : raw[..MaxRawParamsChars];
    }
}

/// <summary>
/// Supplies <see cref="ApprovalBridgingAcpInteractionRouter"/> for every session, injecting the
/// optional <see cref="IAcpApprovalBridge"/> when available.
/// </summary>
internal sealed class ApprovalBridgingAcpInteractionRouterFactory(IAcpApprovalBridge? approvalBridge = null)
    : IAcpInteractionRouterFactory
{
    public IAcpInteractionRouter Create(string sessionId) =>
        Create(new AcpInteractionContext(sessionId));

    public IAcpInteractionRouter Create(AcpInteractionContext context) =>
        new ApprovalBridgingAcpInteractionRouter(context, approvalBridge);
}
