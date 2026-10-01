using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.Runtime;

/// <summary>
/// The single runtime implementation for report-proposed governance actions.
/// Communication decides who may submit the action; this class decides whether
/// the operation is supported and delegates the state change to its owner.
/// </summary>
internal sealed class OrganizationReportActionExecutor(
    IGovernanceRunController runs,
    IEnvironmentRegistry? environments = null,
    ILifecycleManager? lifecycle = null,
    IResourceLeaseService? leases = null,
    ISessionLocator? sessions = null) : IOrganizationReportActionExecutor
{
    private static readonly HashSet<string> RunActions = ["pause_run", "resume_run", "stop_run"];

    public async Task<OrganizationReportActionResult> ExecuteAsync(
        OrganizationReportActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var verb = request.Verb.Trim().ToLowerInvariant();
        if (verb is "wait" or "ask_user")
        {
            return new OrganizationReportActionResult(
                Completed: false,
                Status: "waiting",
                Code: verb == "wait" ? "follow_up_required" : "user_decision_required",
                Message: verb == "wait"
                    ? "The report remains open: waiting is a coordination decision, so no run state was changed."
                    : "The report remains open: this proposal requires the user to choose the next action.");
        }

        if (!RunActions.Contains(verb))
        {
            if (verb == "assign_environment")
                return await AssignEnvironmentAsync(request, cancellationToken).ConfigureAwait(false);
            if (verb == "separate_worktree")
                return await AssignWorktreeAsync(request, cancellationToken).ConfigureAwait(false);
            return new OrganizationReportActionResult(
                Completed: false,
                Status: "not_executed",
                Code: "action_not_implemented",
                Message: $"The governance action '{verb}' has no runtime executor yet; the report remains open.");
        }

        if (!string.Equals(request.SubjectKind, "run", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(request.SubjectId, out var runId)
            || runId == Guid.Empty)
        {
            return new OrganizationReportActionResult(
                Completed: false,
                Status: "not_executed",
                Code: "run_subject_required",
                Message: $"The action '{verb}' requires subject_kind=run and a valid subject_id.");
        }

        // The report contract stores proposed_args as short human/model text.
        // Run-control actions have no free-form execution arguments: the target
        // is the validated run subject, so supplemental prose is deliberately
        // ignored rather than treated as a second command language.

        var controlAction = verb switch
        {
            "pause_run" => "pause",
            "resume_run" => "resume",
            "stop_run" => "cancel",
            _ => throw new InvalidOperationException($"Unsupported run action '{verb}'.")
        };

        GovernanceRunControlResult result;
        try
        {
            result = await runs.ControlAsync(request.Scope, request.SessionId, runId, controlAction,
                request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        }
        catch (RunAdmissionException ex)
        {
            return new OrganizationReportActionResult(false, "not_executed", ex.Code, ex.Message, runId);
        }
        catch (KeyNotFoundException ex)
        {
            return new OrganizationReportActionResult(false, "not_executed", "run_not_found", ex.Message, runId);
        }

        return result.Accepted
            ? new OrganizationReportActionResult(true, "executed", "run_control_applied",
                $"Run control '{controlAction}' was accepted for run {runId:N}.", runId, result.Status)
            : new OrganizationReportActionResult(false, "not_executed",
                string.IsNullOrWhiteSpace(result.Code) ? "run_control_rejected" : result.Code,
                string.IsNullOrWhiteSpace(result.Message) ? "The run control was rejected; the report remains open." : result.Message,
                runId, result.Status);
    }

    private async Task<OrganizationReportActionResult> AssignWorktreeAsync(
        OrganizationReportActionRequest request,
        CancellationToken cancellationToken)
    {
        if (leases is null || sessions is null)
            return new OrganizationReportActionResult(false, "not_executed", "worktree_registry_unavailable",
                "No resource ledger or session lookup is available; the report remains open.");
        if (!string.Equals(request.SubjectKind, "run", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(request.SubjectId, out var runId)
            || runId == Guid.Empty)
            return new OrganizationReportActionResult(false, "not_executed", "run_subject_required",
                "separate_worktree requires subject_kind=run and a valid subject_id.");

        var arguments = ParseArguments(request.ArgumentsJson);
        var pathText = arguments.GetValueOrDefault("path") ?? arguments.GetValueOrDefault("worktree_path");
        if (string.IsNullOrWhiteSpace(pathText))
            return new OrganizationReportActionResult(false, "not_executed", "worktree_path_required",
                "separate_worktree needs path or worktree_path in proposed_args.", runId);

        var session = await sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        var project = session?.ProjectId is { } projectId
            ? await sessions.FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            : null;
        if (project is null || project.TenantId != request.Scope.TenantId || project.WorkspaceId != request.Scope.WorkspaceId)
            return new OrganizationReportActionResult(false, "not_executed", "project_boundary_unavailable",
                "The session has no project workspace boundary for worktree assignment.", runId);

        var path = Path.GetFullPath(Path.IsPathRooted(pathText) ? pathText : Path.Combine(project.RootPath, pathText));
        if (!Directory.Exists(path)
            || (!File.Exists(Path.Combine(path, ".git")) && !Directory.Exists(Path.Combine(path, ".git"))))
            return new OrganizationReportActionResult(false, "not_executed", "invalid_worktree_path",
                $"'{path}' is not an existing Git worktree directory; the report remains open.", runId);

        var decision = await leases.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Worktree, path, Exclusive: true),
            request.SessionId, runId, null, null,
            arguments.GetValueOrDefault("reason") ?? $"Governance report {request.ReportId:N}",
            ResourceLeasePurposes.Assignment), cancellationToken).ConfigureAwait(false);
        if (!decision.Granted)
            return new OrganizationReportActionResult(false, "not_executed", "worktree_unavailable",
                "The worktree is already assigned: " + string.Join("; ", decision.Conflicts.Select(item => item.Reason)), runId);

        await AppendEventBestEffortAsync(lifecycle, runId, request.ReportId, decision.Lease!, path, cancellationToken).ConfigureAwait(false);
        return new OrganizationReportActionResult(true, "executed", "worktree_assigned",
            $"Worktree '{path}' was assigned to run {runId:N}.", runId, "assigned");
    }

    private static async Task AppendEventBestEffortAsync(ILifecycleManager? lifecycle, Guid runId, Guid reportId,
        ResourceLeaseInfo lease, string path, CancellationToken cancellationToken)
    {
        if (lifecycle is null) return;
        try
        {
            await lifecycle.AppendEventAsync(runId, "governance.worktree_assigned", new
            {
                report_id = reportId, path, lease_id = lease.Id
            }, "A governance report assigned a worktree to the run.", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
    }

    private async Task<OrganizationReportActionResult> AssignEnvironmentAsync(
        OrganizationReportActionRequest request,
        CancellationToken cancellationToken)
    {
        if (environments is null)
            return new OrganizationReportActionResult(false, "not_executed", "environment_registry_unavailable",
                "No environment registry is available in this host; the report remains open.");
        if (!string.Equals(request.SubjectKind, "run", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(request.SubjectId, out var runId)
            || runId == Guid.Empty)
            return new OrganizationReportActionResult(false, "not_executed", "run_subject_required",
                "assign_environment requires subject_kind=run and a valid subject_id.");

        var arguments = ParseArguments(request.ArgumentsJson);
        var key = arguments.GetValueOrDefault("key");
        var kind = arguments.GetValueOrDefault("kind");
        var reason = arguments.GetValueOrDefault("reason") ?? $"Governance report {request.ReportId:N}";
        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(kind))
            return new OrganizationReportActionResult(false, "not_executed", "environment_target_required",
                "assign_environment needs key or kind in proposed_args (JSON or key=value pairs).", runId);

        var result = await environments.AcquireAsync(new EnvironmentAcquireRequest(
            request.SessionId, runId, null, null, key, kind, reason), cancellationToken).ConfigureAwait(false);
        if (!result.Granted)
            return new OrganizationReportActionResult(false, "not_executed", "environment_unavailable",
                result.Error ?? "No environment slot was available; the report remains open.", runId);

        if (lifecycle is not null)
        {
            try
            {
                await lifecycle.AppendEventAsync(runId, "governance.environment_assigned", new
                {
                    report_id = request.ReportId,
                    environment = result.Environment!.Key,
                    kind = result.Environment.Kind,
                    slot = result.Slot,
                    lease_id = result.LeaseId,
                    reason
                }, "A governance report assigned an environment to the run.", "info", cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The durable resource lease is authoritative; a missing/terminal
                // run event must not turn a granted slot into a second attempt.
            }
        }

        return new OrganizationReportActionResult(true, "executed", "environment_assigned",
            $"Environment '{result.Environment!.Key}' slot {result.Slot} was assigned to run {runId:N}.", runId, "assigned");
    }

    private static Dictionary<string, string> ParseArguments(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        result[property.Name.Trim()] = property.Value.GetString()!.Trim();
                return result;
            }
        }
        catch (JsonException)
        {
            // The report contract historically stored short prose. Keep accepting
            // key=value;key=value for existing reports while writes use JSON.
        }
        foreach (var part in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1) continue;
            result[part[..separator].Trim()] = part[(separator + 1)..].Trim();
        }
        return result;
    }
}

/// <summary>
/// Adapter around the full-duplex coordinator. It is also the authorization
/// boundary that prevents a report from controlling a run in another session,
/// tenant or workspace.
/// </summary>
internal sealed class GovernanceRunController(
    IFullDuplexRunCoordinator coordinator,
    ILifecycleManager lifecycle) : IGovernanceRunController
{
    public async Task<GovernanceRunControlResult> ControlAsync(
        TenantContext scope,
        Guid sessionId,
        Guid runId,
        string action,
        string clientControlId,
        CancellationToken cancellationToken = default)
    {
        var state = await lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(state.SessionId, out var actualSession) || actualSession != sessionId
            || !Guid.TryParse(state.TenantId, out var actualTenant) || actualTenant != scope.TenantId
            || !Guid.TryParse(state.WorkspaceId, out var actualWorkspace) || actualWorkspace != scope.WorkspaceId)
        {
            return new GovernanceRunControlResult(false, state.Status.ToString().ToLowerInvariant(), action,
                "run_scope_mismatch", "The target run is outside the report's session or workspace.", runId);
        }

        var result = await coordinator.ControlAsync(runId, new RunControlCommand(action, clientControlId), cancellationToken).ConfigureAwait(false);
        return new GovernanceRunControlResult(result.Accepted, result.Status, result.Action, RunId: result.RunId);
    }
}
