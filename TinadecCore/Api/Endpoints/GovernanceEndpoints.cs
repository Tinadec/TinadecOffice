using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.Api.Endpoints;

/// <summary>
/// Northbound governance control-plane endpoints. The handlers are deliberately
/// thin: authorization, scope checks, idempotency, and durable state transitions
/// remain in <see cref="IAuthorizationService"/>.
/// </summary>
public static class GovernanceEndpoints
{
    public static WebApplication MapGovernanceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/governance/permission-requests", async (
            string? status,
            string? run_id,
            string? task_id,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var runId = ParseOptionalGuid(run_id, "run_id");
            var taskId = ParseOptionalGuid(task_id, "task_id");
            var requests = await authorization.ListPermissionRequestsAsync(status, runId, taskId, ct).ConfigureAwait(false);
            return Results.Ok(requests.Select(ToRequestBody).ToArray());
        });

        app.MapGet("/api/v1/governance/permission-requests/{id:guid}", async (
            Guid id,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var resolution = await authorization.GetPermissionRequestAsync(id, ct).ConfigureAwait(false);
            return resolution is null ? Results.NotFound(new { code = "permission_request_not_found" }) : Results.Ok(ToResolutionBody(resolution));
        });

        app.MapPost("/api/v1/governance/permission-requests/{id:guid}/decision", async (
            Guid id,
            PermissionDecisionRequestDto? input,
            IAuthorizationService authorization,
            IUserToolActionService actions,
            ILifecycleManager lifecycle,
            IFullDuplexRunEngine engine,
            CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A decision body is required." });
            var resolution = await authorization.DecidePermissionAsync(new PermissionDecisionCommand(
                id,
                input.Approve,
                input.ApproverAgentInstanceId,
                input.ApprovalDelegationId,
                input.Reason ?? string.Empty), ct).ConfigureAwait(false);

            // A decision is committed before this wake-up. If the process exits
            // between these operations, the persisted executing status is visible
            // to the normal lease/recovery scan and the run can be enqueued again.
            await WakeRunAsync(resolution, lifecycle, engine, ct).ConfigureAwait(false);

            // A user tool action is deliberately outside the run/task graph.
            // Reconcile its durable state after the permission decision so a
            // governance-client decision wakes the same action as the legacy
            // approval projection endpoint, without manufacturing a run.
            var userAction = (await actions.ListAsync(null, ct).ConfigureAwait(false))
                .FirstOrDefault(value => value.PermissionRequestId == id);
            var actionStatus = userAction is null
                ? null
                : (await actions.ResumeAsync(userAction.Id, ct).ConfigureAwait(false)).Status;
            var statusCode = actionStatus is UserToolActionStatuses.SnapshotRequired
                or UserToolActionStatuses.AwaitingDelegate
                or UserToolActionStatuses.AwaitingUser
                or UserToolActionStatuses.AwaitingApproval
                or UserToolActionStatuses.OutcomeUnknown
                || resolution.Request.Status is PermissionRequestStatuses.AwaitingDelegate
                or PermissionRequestStatuses.AwaitingUser
                ? StatusCodes.Status202Accepted
                : StatusCodes.Status200OK;
            return Results.Json(ToResolutionBody(resolution), statusCode: statusCode);
        });

        app.MapPost("/api/v1/governance/policy-bundles", async (
            CreatePolicyBundleRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A policy bundle body is required." });
            var snapshot = await authorization.CreatePolicyBundleAsync(new CreatePolicyBundleCommand(
                input.Slug ?? string.Empty,
                input.DisplayName ?? string.Empty,
                ToRules(input.Rules),
                input.TenantWide), ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/governance/policy-bundles/{snapshot.Id}", snapshot);
        });

        app.MapPost("/api/v1/governance/policy-bundles/{id:guid}/versions", async (
            Guid id,
            PublishPolicyVersionRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A policy version body is required." });
            var snapshot = await authorization.PublishPolicyVersionAsync(new PublishPolicyVersionCommand(
                id, ToRules(input.Rules)), ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/governance/policy-bundles/{id}/versions/{snapshot.Id}", snapshot);
        });

        app.MapPost("/api/v1/governance/policy-bundles/{id:guid}/archive", async (
            Guid id,
            RevokeRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var archived = await authorization.RevokePolicyBundleAsync(id, input?.Reason ?? "Archived by operator.", ct).ConfigureAwait(false);
            return archived ? Results.Ok(new { id, status = "archived" }) : Results.NotFound(new { code = "policy_bundle_not_found" });
        });

        app.MapPost("/api/v1/governance/capability-grants", async (
            GrantCapabilityRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A capability grant body is required." });
            var snapshot = await authorization.GrantCapabilityAsync(new GrantCapabilityCommand(
                input.SubjectPrincipalId,
                input.SubjectAgentInstanceId,
                new CapabilityClaim(input.Capability ?? string.Empty, input.Action ?? string.Empty, input.Resource ?? string.Empty),
                input.RunId,
                input.TaskId,
                input.ExpiresAt,
                input.MaxUses,
                input.Transferable,
                input.ParentGrantId,
                input.Reason ?? string.Empty), ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/governance/capability-grants/{snapshot.Id}", snapshot);
        });

        app.MapPost("/api/v1/governance/capability-grants/{id:guid}/revoke", async (
            Guid id,
            RevokeRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var revoked = await authorization.RevokeGrantAsync(id, input?.Reason ?? "Revoked by operator.", ct).ConfigureAwait(false);
            return revoked ? Results.Ok(new { id, status = "revoked" }) : Results.NotFound(new { code = "capability_grant_not_found" });
        });

        app.MapPost("/api/v1/governance/approval-delegations", async (
            CreateApprovalDelegationRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "An approval delegation body is required." });
            var snapshot = await authorization.CreateApprovalDelegationAsync(new CreateApprovalDelegationCommand(
                input.DelegateAgentVersionId,
                input.DelegateAgentInstanceId,
                ToRules(input.Rules),
                input.MaxRisk ?? "low",
                input.MaxCost,
                input.MaxUses,
                input.ExpiresAt,
                input.RunId,
                input.RequireUserReview), ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/governance/approval-delegations/{snapshot.Id}", snapshot);
        });

        app.MapPost("/api/v1/governance/approval-delegations/{id:guid}/revoke", async (
            Guid id,
            RevokeRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var revoked = await authorization.RevokeDelegationAsync(id, input?.Reason ?? "Revoked by operator.", ct).ConfigureAwait(false);
            return revoked ? Results.Ok(new { id, status = "revoked" }) : Results.NotFound(new { code = "approval_delegation_not_found" });
        });

        app.MapPost("/api/v1/governance/capability-leases/{id:guid}/revoke", async (
            Guid id,
            RevokeRequestDto? input,
            IAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var revoked = await authorization.RevokeLeaseAsync(id, input?.Reason ?? "Revoked by operator.", ct).ConfigureAwait(false);
            return revoked ? Results.Ok(new { id, status = "revoked" }) : Results.NotFound(new { code = "capability_lease_not_found" });
        });

        return app;
    }

    private static IReadOnlyList<CapabilityRule> ToRules(IReadOnlyList<CapabilityRuleDto>? rules) =>
        (rules ?? []).Select(rule => new CapabilityRule(
            rule.Effect ?? string.Empty,
            rule.Capability ?? string.Empty,
            rule.Action ?? string.Empty,
            rule.ResourcePattern ?? string.Empty)).ToArray();

    private static Guid? ParseOptionalGuid(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var id)
            ? id
            : throw new ArgumentException($"{name} must be a valid Guid.", name);
    }

    private static async Task WakeRunAsync(
        PermissionResolution resolution,
        ILifecycleManager lifecycle,
        IFullDuplexRunEngine engine,
        CancellationToken cancellationToken)
    {
        if (resolution.Request.RunId is not { } runId || resolution.Request.Status is
            PermissionRequestStatuses.AwaitingDelegate or PermissionRequestStatuses.AwaitingUser)
            return;

        var run = await lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled) return;
        if (run.Status is RunStatus.AwaitingApproval or RunStatus.AwaitingDelegate or RunStatus.AwaitingUser)
        {
            await lifecycle.SetRunStatusAsync(runId.ToString(), "executing", "Permission decision committed; resuming run.", cancellationToken).ConfigureAwait(false);
            await lifecycle.AppendEventAsync(runId, "governance.permission_decided", new
            {
                permission_request_id = resolution.Request.Id,
                authorization_decision_id = resolution.Decision.Id,
                outcome = resolution.Decision.Outcome,
                reason_code = resolution.Decision.ReasonCode
            }, "Permission decision committed; run resumed.", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private static object ToResolutionBody(PermissionResolution resolution) => new
    {
        request = ToRequestBody(resolution.Request),
        decision = ToDecisionBody(resolution.Decision),
        grant = resolution.Grant is null ? null : ToGrantBody(resolution.Grant),
        lease = resolution.Lease is null ? null : ToLeaseBody(resolution.Lease)
    };

    private static object ToRequestBody(PermissionRequestSnapshot value) => new
    {
        id = value.Id,
        tenant_id = value.TenantId,
        workspace_id = value.WorkspaceId,
        subject_principal_id = value.SubjectPrincipalId,
        subject_agent_instance_id = value.SubjectAgentInstanceId,
        parent_agent_instance_id = value.ParentAgentInstanceId,
        capability = value.Claim.Capability,
        action = value.Claim.Action,
        resource = value.Claim.Resource,
        run_id = value.RunId,
        task_id = value.TaskId,
        risk = value.Risk,
        expected_cost = value.ExpectedCost,
        status = value.Status,
        authorization_decision_id = value.AuthorizationDecisionId,
        capability_grant_id = value.CapabilityGrantId,
        capability_lease_id = value.CapabilityLeaseId,
        expires_at = value.ExpiresAt,
        created_at = value.CreatedAt,
        updated_at = value.UpdatedAt
    };

    private static object ToDecisionBody(AuthorizationDecisionSnapshot value) => new
    {
        id = value.Id,
        tenant_id = value.TenantId,
        workspace_id = value.WorkspaceId,
        subject_principal_id = value.SubjectPrincipalId,
        subject_agent_instance_id = value.SubjectAgentInstanceId,
        capability = value.Claim.Capability,
        action = value.Claim.Action,
        resource = value.Claim.Resource,
        run_id = value.RunId,
        task_id = value.TaskId,
        permission_request_id = value.PermissionRequestId,
        capability_grant_id = value.CapabilityGrantId,
        capability_lease_id = value.CapabilityLeaseId,
        outcome = value.Outcome,
        reason_code = value.ReasonCode,
        reason = value.Reason,
        decision_source = value.DecisionSource,
        policy_snapshot_hash = value.PolicySnapshotHash,
        decided_by_principal_id = value.DecidedByPrincipalId,
        decided_by_agent_instance_id = value.DecidedByAgentInstanceId,
        created_at = value.CreatedAt
    };

    private static object ToGrantBody(CapabilityGrantSnapshot value) => new
    {
        id = value.Id,
        tenant_id = value.TenantId,
        workspace_id = value.WorkspaceId,
        subject_principal_id = value.SubjectPrincipalId,
        subject_agent_instance_id = value.SubjectAgentInstanceId,
        capability = value.Claim.Capability,
        action = value.Claim.Action,
        resource = value.Claim.Resource,
        run_id = value.RunId,
        task_id = value.TaskId,
        parent_grant_id = value.ParentGrantId,
        transferable = value.Transferable,
        status = value.Status,
        max_uses = value.MaxUses,
        use_count = value.UseCount,
        starts_at = value.StartsAt,
        expires_at = value.ExpiresAt,
        revoked_at = value.RevokedAt,
        revoke_reason = value.RevokeReason
    };

    private static object ToLeaseBody(CapabilityLeaseSnapshot value) => new
    {
        id = value.Id,
        tenant_id = value.TenantId,
        workspace_id = value.WorkspaceId,
        subject_principal_id = value.SubjectPrincipalId,
        subject_agent_instance_id = value.SubjectAgentInstanceId,
        capability_grant_id = value.CapabilityGrantId,
        permission_request_id = value.PermissionRequestId,
        capability = value.Claim.Capability,
        action = value.Claim.Action,
        resource = value.Claim.Resource,
        run_id = value.RunId,
        task_id = value.TaskId,
        policy_snapshot_hash = value.PolicySnapshotHash,
        status = value.Status,
        max_uses = value.MaxUses,
        use_count = value.UseCount,
        starts_at = value.StartsAt,
        expires_at = value.ExpiresAt,
        revoked_at = value.RevokedAt,
        revoke_reason = value.RevokeReason
    };
}
