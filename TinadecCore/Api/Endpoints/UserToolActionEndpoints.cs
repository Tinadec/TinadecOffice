using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Api.Endpoints;

public static class UserToolActionEndpoints
{
    public static WebApplication MapUserToolActionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/user/tool-actions", async (string? status, IUserToolActionService actions, CancellationToken ct) =>
        {
            var results = await actions.ListAsync(status, ct).ConfigureAwait(false);
            return Results.Ok(results.Select(ToDto).ToArray());
        }).Produces<UserToolActionDto[]>(StatusCodes.Status200OK);

        app.MapPost("/api/v1/user/tool-actions", async (UserToolActionCreateRequestDto? input, IUserToolActionService actions, CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A user tool action body is required." });
            var parameters = input.Params is { } element ? element.GetRawText() : "null";
            var result = await actions.CreateAsync(new UserToolActionRequest(input.ProjectId, input.ToolId, parameters, input.IdempotencyKey), ct).ConfigureAwait(false);
            return Results.Json(ToDto(result), statusCode: IsWaiting(result.Status) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces<UserToolActionDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        app.MapGet("/api/v1/user/tool-actions/{id:guid}", async (Guid id, IUserToolActionService actions, CancellationToken ct) =>
        {
            var result = await actions.GetAsync(id, ct).ConfigureAwait(false);
            return result is null ? Results.NotFound(new { code = "user_tool_action_not_found" }) : Results.Ok(ToDto(result));
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/user/tool-actions/{id:guid}/resume", async (Guid id, IUserToolActionService actions, CancellationToken ct) =>
        {
            var result = await actions.ResumeAsync(id, ct).ConfigureAwait(false);
            return Results.Json(ToDto(result), statusCode: IsWaiting(result.Status) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces<UserToolActionDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/user/tool-actions/{id:guid}/snapshot-override", async (Guid id, UserToolActionSnapshotOverrideRequestDto? input, IUserToolActionService actions, CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A snapshot override reason is required." });
            var result = await actions.OverrideSnapshotAsync(id, input.Reason, ct).ConfigureAwait(false);
            return Results.Json(ToDto(result), statusCode: IsWaiting(result.Status) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).Produces<UserToolActionDto>(StatusCodes.Status200OK)
            .Produces<UserToolActionDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        return app;
    }

    private static bool IsWaiting(string status) => status is UserToolActionStatuses.SnapshotRequired or UserToolActionStatuses.AwaitingDelegate or UserToolActionStatuses.AwaitingUser or UserToolActionStatuses.AwaitingApproval or UserToolActionStatuses.OutcomeUnknown;

    internal static UserToolActionDto ToDto(UserToolActionResult value)
    {
        JsonElement? result = null;
        if (!string.IsNullOrWhiteSpace(value.ResultJson))
        {
            try { result = JsonDocument.Parse(value.ResultJson).RootElement.Clone(); } catch (JsonException) { }
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
            Result = result, ErrorCategory = value.ErrorCategory, Message = value.Message,
            CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt, CompletedAt = value.CompletedAt
        };
    }
}
