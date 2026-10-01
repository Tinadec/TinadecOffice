using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Standing approvals a person gave (todo E7): command prefixes ("commands that start with this are
/// fine") and the per-session opt-in that lets the delegated gates decide a tool. Registering and
/// revoking is the person's; the rules are honored by the PDP and the approval gates, never by a
/// model. A rule's session scope is re-checked there — naming a rule from another session does nothing.
/// </summary>
public static class ApprovalRuleEndpoints
{
    public static IEndpointRouteBuilder MapApprovalRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/approval-rules").WithTags("ApprovalRules");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ApprovalRuleException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode, title: ex.Code, detail: ex.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["trace_id"] = context.HttpContext.TraceIdentifier });
            }
        });

        group.MapGet("", async (Guid? sessionId, IApprovalRules rules, CancellationToken ct) =>
            Results.Ok((await rules.ListAsync(sessionId, ct)).Select(ToDto).ToArray()))
            .Produces<ApprovalRuleDto[]>();

        group.MapPost("", async (ApprovalRuleCreateRequest request, IApprovalRules rules, CancellationToken ct) =>
        {
            var created = await rules.CreateAsync(new ApprovalRuleCreate(request.Kind, request.ToolId, request.Pattern, request.SessionId), ct);
            return Results.Created($"/api/v1/approval-rules/{created.Id}", ToDto(created));
        }).Produces<ApprovalRuleDto>(StatusCodes.Status201Created);

        group.MapDelete("/{ruleId:guid}", async (Guid ruleId, IApprovalRules rules, CancellationToken ct) =>
            await rules.RevokeAsync(ruleId, ct) ? Results.NoContent() : Results.NotFound(new { code = "not_found" }));

        return app;
    }

    private static ApprovalRuleDto ToDto(ApprovalRuleView view) =>
        new(view.Id, view.Kind, view.ToolId, view.Pattern, view.SessionId, view.CreatedByPrincipalId, view.CreatedAt, view.UseCount);
}
