using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.DmaEA;

/// <summary>
/// Wave 3 cross-device Multi-Agent orchestration primitives (<c>peer_list_nodes</c>,
/// <c>peer_delegate_task</c>, <c>peer_read_task</c>, <c>peer_wait_tasks</c>, <c>peer_cancel_task</c>).
///
/// Architectural boundary invariant: <c>TinadecCore.DmaEA</c> never references <c>TinadecCore.Peers</c>;
/// all cross-device coordination flows exclusively through <see cref="IPeerDelegationCoordinator"/>.
/// </summary>
public static class PeerDelegationTools
{
    public const string ListNodesToolId = "peer_list_nodes";
    public const string DelegateTaskToolId = "peer_delegate_task";
    public const string ReadTaskToolId = "peer_read_task";
    public const string WaitTasksToolId = "peer_wait_tasks";
    public const string CancelTaskToolId = "peer_cancel_task";

    public static readonly IReadOnlyList<string> ToolIds =
    [
        ListNodesToolId,
        DelegateTaskToolId,
        ReadTaskToolId,
        WaitTasksToolId,
        CancelTaskToolId
    ];

    private static readonly HashSet<string> ToolIdSet = new(ToolIds, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static bool IsPeerDelegationTool(string? toolId) =>
        !string.IsNullOrWhiteSpace(toolId) && ToolIdSet.Contains(toolId.Trim());

    private const string ListNodesSchemaJson =
        "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    private const string DelegateTaskSchemaJson =
        "{\"type\":\"object\",\"properties\":{"
        + "\"remote_device_id\":{\"type\":\"string\",\"description\":\"Target paired peer device_id or remote_device_id from peer_list_nodes.\"},"
        + "\"remote_project_id\":{\"type\":\"string\",\"description\":\"Authorized workspace project_id on the target peer device.\"},"
        + "\"goal\":{\"type\":\"string\",\"description\":\"Clear, self-contained session-level task brief and acceptance criteria for the remote worker agent.\"},"
        + "\"branch_or_commit\":{\"type\":\"string\",\"description\":\"Optional Git branch or commit ref for the remote worker to inspect or verify.\"},"
        + "\"write_scope\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Optional list of relative path prefixes authorized for modification on the remote workspace.\"},"
        + "\"use_provider_proxy\":{\"type\":\"boolean\",\"description\":\"When true, issues an ephemeral single-session proxy_session_token so a zero-key worker can invoke the controller's LLM provider without exposing API keys.\"},"
        + "\"callback_mode\":{\"type\":\"string\",\"enum\":[\"poll\",\"stream\"],\"description\":\"Optional progress reporting mode (default 'poll').\"},"
        + "\"teammate_handle\":{\"type\":\"string\",\"description\":\"Optional teammate handle projected in the controller's orchestration swimlane.\"}"
        + "},\"required\":[\"remote_device_id\",\"remote_project_id\",\"goal\"],\"additionalProperties\":false}";

    private const string ReadTaskSchemaJson =
        "{\"type\":\"object\",\"properties\":{"
        + "\"delegation_id\":{\"type\":\"string\",\"description\":\"Delegation identifier returned by peer_delegate_task.\"}"
        + "},\"required\":[\"delegation_id\"],\"additionalProperties\":false}";

    private const string WaitTasksSchemaJson =
        "{\"type\":\"object\",\"properties\":{"
        + "\"delegation_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"One or more delegation_ids to await. When omitted, awaits all active delegations of the current run.\"},"
        + "\"timeout_seconds\":{\"type\":\"integer\",\"description\":\"Maximum wait window in seconds (1..600, default 30). Returns early immediately if any remote task enters awaiting_approval.\"}"
        + "},\"additionalProperties\":false}";

    private const string CancelTaskSchemaJson =
        "{\"type\":\"object\",\"properties\":{"
        + "\"delegation_id\":{\"type\":\"string\",\"description\":\"Delegation identifier to cancel on the remote peer.\"},"
        + "\"reason\":{\"type\":\"string\",\"description\":\"Optional cancellation reason recorded in the delegation summary.\"}"
        + "},\"required\":[\"delegation_id\"],\"additionalProperties\":false}";

    public static IReadOnlyList<ToolManifestEntryDto> ManifestEntries() =>
    [
        CreateEntry(
            ListNodesToolId,
            "List online ('ready') paired peer devices authorized for cross-device agent delegation, including platform ('darwin'/'linux'/'win32') and accessible workspaces.",
            ListNodesSchemaJson),
        CreateEntry(
            DelegateTaskToolId,
            "Delegate a session-level sub-task to an online peer device workspace (enforces MAX_DELEGATION_HOP_DEPTH = 2 and optional zero-key provider proxy).",
            DelegateTaskSchemaJson),
        CreateEntry(
            ReadTaskToolId,
            "Read live status, summary, trajectory events, and any blocking awaiting_approval cards for a delegated remote peer task.",
            ReadTaskSchemaJson),
        CreateEntry(
            WaitTasksToolId,
            "Non-blocking async wait until delegated peer task(s) reach a terminal state OR wake early upon awaiting_approval so human/controller approval can proceed.",
            WaitTasksSchemaJson),
        CreateEntry(
            CancelTaskToolId,
            "Cancel an active remote peer task through the peer proxy and mark its peer_delegation_links status as 'cancelled'.",
            CancelTaskSchemaJson)
    ];

    private static ToolManifestEntryDto CreateEntry(string id, string description, string schemaJson) => new()
    {
        Id = id,
        Description = description,
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(schemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = []
    };

    internal sealed record PeerToolExecutionOutcome(
        bool Success,
        string ResultJson,
        string? ErrorCode = null,
        string? ErrorMessage = null,
        PeerDelegationLinkDto? DelegatedLink = null,
        PeerDelegationTaskStatusDto? ReadOrCancelledTask = null,
        PeerWaitTasksResultDto? WaitResult = null);

    internal static async Task<PeerToolExecutionOutcome> ExecuteAsync(
        IPeerDelegationCoordinator coordinator,
        string toolId,
        JsonElement args,
        Guid parentRunId,
        Guid parentSessionId,
        int currentHopDepth,
        CancellationToken cancellationToken)
    {
        var normalizedTool = toolId.Trim().ToLowerInvariant();
        try
        {
            switch (normalizedTool)
            {
                case ListNodesToolId:
                {
                    var listed = await coordinator.ListNodesAsync(cancellationToken).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        count = listed.Nodes.Count,
                        nodes = listed.Nodes
                    }, JsonOptions);
                    return new PeerToolExecutionOutcome(true, json);
                }

                case DelegateTaskToolId:
                {
                    var remoteDeviceId = ReadString(args, "remote_device_id");
                    var remoteProjectId = ReadString(args, "remote_project_id") ?? ReadString(args, "project_id");
                    var goal = ReadString(args, "goal");
                    if (string.IsNullOrWhiteSpace(remoteDeviceId)
                        || string.IsNullOrWhiteSpace(remoteProjectId)
                        || string.IsNullOrWhiteSpace(goal))
                    {
                        return Failure("invalid_arguments", "peer_delegate_task requires non-empty 'remote_device_id', 'remote_project_id', and 'goal'.");
                    }

                    var link = await coordinator.DelegateTaskAsync(new PeerDelegateTaskCommand
                    {
                        ParentRunId = parentRunId,
                        ParentSessionId = parentSessionId,
                        CurrentHopDepth = currentHopDepth,
                        RemoteDeviceId = remoteDeviceId,
                        RemoteProjectId = remoteProjectId,
                        Goal = goal,
                        BranchOrCommit = ReadString(args, "branch_or_commit"),
                        WriteScope = ReadStringList(args, "write_scope"),
                        UseProviderProxy = ReadBool(args, "use_provider_proxy"),
                        CallbackMode = ReadString(args, "callback_mode"),
                        TeammateHandle = ReadString(args, "teammate_handle")
                    }, cancellationToken).ConfigureAwait(false);

                    var json = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        delegation = link
                    }, JsonOptions);
                    return new PeerToolExecutionOutcome(true, json, DelegatedLink: link);
                }

                case ReadTaskToolId:
                {
                    var delegationId = ReadString(args, "delegation_id");
                    if (string.IsNullOrWhiteSpace(delegationId))
                    {
                        return Failure("invalid_arguments", "peer_read_task requires non-empty 'delegation_id'.");
                    }

                    var taskStatus = await coordinator.ReadTaskAsync(delegationId, cancellationToken).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        task = taskStatus
                    }, JsonOptions);
                    return new PeerToolExecutionOutcome(true, json, ReadOrCancelledTask: taskStatus);
                }

                case WaitTasksToolId:
                {
                    var ids = ReadStringList(args, "delegation_ids");
                    var timeoutSec = ReadInt(args, "timeout_seconds") ?? 30;
                    var waited = await coordinator.WaitTasksAsync(new PeerWaitTasksCommand
                    {
                        ParentRunId = parentRunId,
                        DelegationIds = ids,
                        TimeoutSeconds = timeoutSec
                    }, cancellationToken).ConfigureAwait(false);

                    var json = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        completed = waited.Completed,
                        timed_out = waited.TimedOut,
                        awaiting_approval = waited.AwaitingApproval,
                        woken_reason = waited.WokenReason,
                        tasks = waited.Tasks
                    }, JsonOptions);
                    return new PeerToolExecutionOutcome(true, json, WaitResult: waited);
                }

                case CancelTaskToolId:
                {
                    var delegationId = ReadString(args, "delegation_id");
                    if (string.IsNullOrWhiteSpace(delegationId))
                    {
                        return Failure("invalid_arguments", "peer_cancel_task requires non-empty 'delegation_id'.");
                    }

                    var cancelled = await coordinator.CancelTaskAsync(
                        delegationId,
                        ReadString(args, "reason"),
                        cancellationToken).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        task = cancelled
                    }, JsonOptions);
                    return new PeerToolExecutionOutcome(true, json, ReadOrCancelledTask: cancelled);
                }

                default:
                    return Failure("unknown_tool", $"Unsupported peer delegation tool '{toolId}'.");
            }
        }
        catch (PeerException ex)
        {
            return Failure(ex.Code, ex.Message, ex.StatusCode);
        }
        catch (KeyNotFoundException ex)
        {
            return Failure("not_found", ex.Message, 404);
        }
    }

    private static PeerToolExecutionOutcome Failure(string code, string message, int? statusCode = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            ok = false,
            code,
            status = statusCode,
            message
        }, JsonOptions);
        return new PeerToolExecutionOutcome(false, json, code, message);
    }

    private static string? ReadString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        return args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()?.Trim()
            : null;
    }

    private static bool ReadBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return false;
        return el.ValueKind == JsonValueKind.True;
    }

    private static int? ReadInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var val) ? val : null;
    }

    private static IReadOnlyList<string>? ReadStringList(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var el)
            || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(x => x.GetString()!.Trim())
            .ToArray();
    }
}
