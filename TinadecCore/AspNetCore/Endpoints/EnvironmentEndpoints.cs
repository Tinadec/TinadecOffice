using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// The workspace's environment registry (todo E1): what the environment steward can hand out, and who
/// holds each slot now. Registering and disabling is the user's; taking and giving back is the agents'
/// (<c>environment_acquire</c> / <c>environment_release</c>), recorded in the resource ledger.
/// </summary>
public static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/environments").WithTags("Environments");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (EnvironmentRegistryException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode, title: ex.Code, detail: ex.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["trace_id"] = context.HttpContext.TraceIdentifier });
            }
        });

        group.MapGet("", async (IEnvironmentRegistry registry, CancellationToken ct) =>
            Results.Ok((await registry.ListAsync(ct)).Select(ToDto).ToArray()))
            .Produces<EnvironmentDto[]>();

        group.MapPost("", async (EnvironmentRegisterRequest request, IEnvironmentRegistry registry, CancellationToken ct) =>
        {
            var created = await registry.RegisterAsync(new EnvironmentRegistration(request.Key, request.Kind, request.DisplayName,
                request.Description, ConnectionText(request.Connection), request.Capacity ?? 1), ct);
            return Results.Created($"/api/v1/environments/{created.Id}", ToDto(created));
        }).Produces<EnvironmentDto>(StatusCodes.Status201Created);

        group.MapPatch("/{environmentId:guid}", async (Guid environmentId, EnvironmentUpdateRequest request, IEnvironmentRegistry registry, CancellationToken ct) =>
            Results.Ok(ToDto(await registry.UpdateAsync(environmentId, new EnvironmentUpdate(request.DisplayName, request.Description,
                ConnectionText(request.Connection), request.Capacity, request.Status), ct))))
            .Produces<EnvironmentDto>();

        return app;
    }

    private static string? ConnectionText(JsonElement? connection) =>
        connection is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? value.GetRawText() : null;

    private static EnvironmentDto ToDto(EnvironmentView view)
    {
        using var connection = JsonDocument.Parse(view.ConnectionJson);
        return new EnvironmentDto(view.Id, view.Key, view.Kind, view.DisplayName, view.Description, connection.RootElement.Clone(),
            view.Capacity, view.FreeSlots, view.Status,
            view.Holders.Select(holder => new EnvironmentHolderDto(holder.Slot, holder.LeaseId, holder.SessionId, holder.RunId, holder.TaskId, holder.Reason)).ToArray(),
            view.UpdatedAt);
    }
}
