using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

public static class LifecycleHooksEndpoints
{
    public static IEndpointRouteBuilder MapLifecycleHooksEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/lifecycle-hooks", async (
            [FromQuery(Name = "scope")] string? scope,
            [FromQuery(Name = "workspace_root")] string? workspaceRoot,
            HttpResponse response,
            ILifecycleHookService hooks,
            CancellationToken ct) =>
        {
            var doc = await hooks.GetHooksAsync(scope, workspaceRoot, ct).ConfigureAwait(false);
            response.Headers.ETag = $"\"{doc.Revision}\"";
            return Results.Ok(doc);
        })
        .Produces<LifecycleHooksDocumentDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPut("/api/v1/lifecycle-hooks", async (
            [FromQuery(Name = "scope")] string? scope,
            [FromQuery(Name = "workspace_root")] string? workspaceRoot,
            LifecycleHooksWriteRequestDto input,
            HttpRequest request,
            HttpResponse response,
            ILifecycleHookService hooks,
            CancellationToken ct) =>
        {
            var normalizedRequest = new LifecycleHooksWriteRequestDto
            {
                Scope = string.IsNullOrWhiteSpace(input.Scope) ? scope : input.Scope,
                WorkspaceRoot = string.IsNullOrWhiteSpace(input.WorkspaceRoot) ? workspaceRoot : input.WorkspaceRoot,
                Hooks = input.Hooks,
                ExpectedRevision = input.ExpectedRevision,
            };
            var ifMatch = long.TryParse(request.Headers.IfMatch.FirstOrDefault()?.Trim('"', 'W', '/', ' '), out var rev)
                ? rev
                : (long?)null;
            var doc = await hooks.SaveHooksAsync(normalizedRequest, ifMatch, ct).ConfigureAwait(false);
            response.Headers.ETag = $"\"{doc.Revision}\"";
            return Results.Ok(doc);
        })
        .Produces<LifecycleHooksDocumentDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        return app;
    }
}
