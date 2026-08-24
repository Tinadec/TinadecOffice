using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Api.Endpoints;

public static class UserToolActionEndpoints
{
    public static WebApplication MapUserToolActionEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UserToolActionEndpoints));
        app.MapGet("/api/v1/user/tool-actions", async (string? status, IUserToolActionService actions, CancellationToken ct) =>
        {
            var results = await actions.ListAsync(status, ct).ConfigureAwait(false);
            return Results.Ok(results.Select(value => ToDto(value, logger)).ToArray());
        }).Produces<UserToolActionDto[]>(StatusCodes.Status200OK);

        app.MapPost("/api/v1/user/tool-actions", async (UserToolActionCreateRequestDto? input, IUserToolActionService actions, CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A user tool action body is required." });
            var parameters = input.Params is { } element ? element.GetRawText() : "null";
            var result = await actions.CreateAsync(new UserToolActionRequest(input.ProjectId, input.ToolId, parameters, input.IdempotencyKey), ct).ConfigureAwait(false);
            return Results.Json(ToDto(result, logger), statusCode: IsWaiting(result.Status) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces<UserToolActionDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        app.MapGet("/api/v1/user/tool-actions/{id:guid}", async (Guid id, IUserToolActionService actions, CancellationToken ct) =>
        {
            var result = await actions.GetAsync(id, ct).ConfigureAwait(false);
            return result is null ? Results.NotFound(new { code = "user_tool_action_not_found" }) : Results.Ok(ToDto(result, logger));
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/user/tool-actions/{id:guid}/resume", async (Guid id, IUserToolActionService actions, CancellationToken ct) =>
        {
            var result = await actions.ResumeAsync(id, ct).ConfigureAwait(false);
            return Results.Json(ToDto(result, logger), statusCode: IsWaiting(result.Status) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces<UserToolActionDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/user/tool-actions/{id:guid}/snapshot-override", async (Guid id, UserToolActionSnapshotOverrideRequestDto? input, IUserToolActionService actions, CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A snapshot override reason is required." });
            var result = await actions.OverrideSnapshotAsync(id, input.Reason, ct).ConfigureAwait(false);
            return Results.Json(ToDto(result, logger), statusCode: IsWaiting(result.Status) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces<UserToolActionDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/user/tool-actions/{id:guid}/recovery-decision", async (Guid id, UserToolActionRecoveryDecisionRequestDto? input, IUserToolActionService actions, CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A recovery decision body is required." });
            var result = await actions.DecideRecoveryAsync(id, input.Decision, input.Reason, ct).ConfigureAwait(false);
            return Results.Ok(ToDto(result, logger));
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        return app;
    }

    private static bool IsWaiting(string status) => status is UserToolActionStatuses.SnapshotRequired or UserToolActionStatuses.AwaitingDelegate or UserToolActionStatuses.AwaitingUser or UserToolActionStatuses.AwaitingApproval or UserToolActionStatuses.OutcomeUnknown;

    internal static UserToolActionDto ToDto(UserToolActionResult value, ILogger? logger = null)
    {
        JsonElement? result = null;
        if (!string.IsNullOrWhiteSpace(value.ResultJson))
        {
            try { result = JsonDocument.Parse(value.ResultJson).RootElement.Clone(); }
            catch (JsonException ex)
            {
                // Surface corrupt durable results instead of silently pretending
                // the action has no outcome.
                logger?.LogWarning(ex, "User tool action {ActionId} has unparseable ResultJson; returning null result", value.Id);
            }
        }
        return new UserToolActionDto
        {
            Id = value.Id, AuditReference = value.AuditReference, TenantId = value.TenantId, WorkspaceId = value.WorkspaceId, ProjectId = value.ProjectId,
            PrincipalId = value.PrincipalId, ToolId = value.ToolId, Status = value.Status, Risk = value.Risk,
            MutatesWorkspace = value.MutatesWorkspace, RequiresApproval = value.RequiresApproval,
            PermissionRequestId = value.PermissionRequestId, AuthorizationDecisionId = value.AuthorizationDecisionId,
            ActionApprovalId = value.ActionApprovalId, SnapshotId = value.SnapshotId, SnapshotHash = value.SnapshotHash,
            SnapshotOverride = value.SnapshotOverride, SnapshotOverrideReason = value.SnapshotOverrideReason,
            NonReversible = value.NonReversible, CompensationGuidance = value.CompensationGuidance,
            RecoveryDecision = value.RecoveryDecision, RecoveryReason = value.RecoveryReason, RecoveredAt = value.RecoveredAt,
            Result = result, ErrorCategory = value.ErrorCategory, Message = value.Message,
            CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt, CompletedAt = value.CompletedAt
        };
    }
}
