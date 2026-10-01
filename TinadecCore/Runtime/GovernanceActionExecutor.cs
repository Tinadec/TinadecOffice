using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.Runtime;

/// <summary>
/// The single runtime implementation for report-proposed governance actions.
/// Communication decides who may submit the action; this class decides whether
/// the operation is supported and delegates the state change to its owner.
/// </summary>
internal sealed class OrganizationReportActionExecutor(IGovernanceRunController runs) : IOrganizationReportActionExecutor
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
