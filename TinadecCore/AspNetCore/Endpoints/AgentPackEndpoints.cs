using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

public static class AgentPackEndpoints
{
    public static IEndpointRouteBuilder MapAgentPackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/agent-packs", async (IAgentPackService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken).ConfigureAwait(false)))
            .WithTags("AgentPacks")
            .WithSummary("List installed agent packs")
            .Produces<AgentPackInstallationView[]>(StatusCodes.Status200OK);

        app.MapGet("/api/v1/agent-packs/{packId}", async (
            string packId,
            IAgentPackService service,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            var detail = await service.GetAsync(packId, cancellationToken).ConfigureAwait(false);
            if (detail is null) throw new AgentPackDomainException(404, "agent_pack_not_found", "Agent pack not found", $"Agent pack '{packId}' is not installed in the current workspace.");
            response.Headers.ETag = $"\"{detail.Revision}\"";
            return Results.Ok(detail);
        }).WithTags("AgentPacks")
            .WithSummary("Get an installed agent pack")
            .Produces<AgentPackInstallationDetail>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .WithAgentPackOpenApi(
                etagStatuses: [StatusCodes.Status200OK],
                problemCodes: new Dictionary<int, string>
                {
                    [StatusCodes.Status404NotFound] = "agent_pack_not_found"
                });

        app.MapPost("/api/v1/agent-packs/install-preview", async (
            AgentPackEnvelopeDto envelope,
            IAgentPackService service,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            var preview = await service.PreviewAsync(envelope, cancellationToken).ConfigureAwait(false);
            response.Headers.ETag = $"\"{preview.Revision}\"";
            return Results.Ok(preview);
        }).WithTags("AgentPacks")
            .WithSummary("Preview an agent pack installation or upgrade")
            .Accepts<AgentPackEnvelopeDto>("application/json")
            .Produces<AgentPackPreviewView>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .WithAgentPackOpenApi(
                etagStatuses: [StatusCodes.Status200OK],
                problemCodes: new Dictionary<int, string>
                {
                    [StatusCodes.Status400BadRequest] = "invalid_agent_pack_manifest",
                    [StatusCodes.Status403Forbidden] = "agent_pack_management_forbidden",
                    [StatusCodes.Status409Conflict] = "agent_pack_owner_conflict, agent_pack_version_hash_conflict",
                    [StatusCodes.Status422UnprocessableEntity] = "agent_pack_incompatible, invalid_agent_pack_manifest"
                });

        app.MapPut("/api/v1/agent-packs/{packId}", async (
            string packId,
            AgentPackApplyRequestDto request,
            HttpRequest httpRequest,
            IAgentPackService service,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty;
            var expectedRevision = ParseIfMatch(httpRequest.Headers.IfMatch.FirstOrDefault());
            var result = await service.ApplyAsync(packId, request, expectedRevision, idempotencyKey, cancellationToken).ConfigureAwait(false);
            response.Headers.ETag = $"\"{result.Revision}\"";
            return Results.Json(result, statusCode: result.Status == "installed" ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithTags("AgentPacks")
            .WithSummary("Install or upgrade an agent pack")
            .Accepts<AgentPackApplyRequestDto>("application/json")
            .Produces<AgentPackApplyView>(StatusCodes.Status200OK)
            .Produces<AgentPackApplyView>(StatusCodes.Status201Created)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .WithAgentPackOpenApi(
                etagStatuses: [StatusCodes.Status200OK, StatusCodes.Status201Created],
                includeApplyHeaders: true,
                problemCodes: new Dictionary<int, string>
                {
                    [StatusCodes.Status400BadRequest] = "invalid_agent_pack_manifest",
                    [StatusCodes.Status403Forbidden] = "agent_pack_management_forbidden",
                    [StatusCodes.Status409Conflict] = "agent_pack_owner_conflict, agent_pack_version_hash_conflict, agent_pack_resource_conflict",
                    [StatusCodes.Status412PreconditionFailed] = "agent_pack_preview_stale, agent_pack_revision_conflict",
                    [StatusCodes.Status422UnprocessableEntity] = "agent_pack_incompatible, invalid_agent_pack_manifest"
                });

        return app;
    }

    private static long? ParseIfMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().TrimStart('W', '/').Trim('"');
        return long.TryParse(normalized, out var revision)
            ? revision
            : throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid If-Match", "If-Match must contain the numeric agent pack revision.");
    }

    private static RouteHandlerBuilder WithAgentPackOpenApi(
        this RouteHandlerBuilder builder,
        IReadOnlyList<int>? etagStatuses = null,
        IReadOnlyDictionary<int, string>? problemCodes = null,
        bool includeApplyHeaders = false)
    {
        return builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (includeApplyHeaders)
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Unique key for this preview application. Replays with the same key and request converge to the stored receipt.",
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        MinLength = 1,
                        MaxLength = 256
                    }
                });
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "If-Match",
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "Quoted numeric installation revision from the preview or pack ETag. Required for upgrades; omit for a first installation.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
            }

            if (operation.Responses is not { } responses)
                return Task.CompletedTask;

            foreach (var status in etagStatuses ?? [])
            {
                if (responses.TryGetValue(status.ToString(CultureInfo.InvariantCulture), out var response)
                    && response is OpenApiResponse mutableResponse)
                {
                    mutableResponse.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
                    mutableResponse.Headers["ETag"] = new OpenApiHeader
                    {
                        Description = "Quoted numeric agent pack installation revision for conditional updates.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                }
            }

            foreach (var (status, codes) in problemCodes ?? new Dictionary<int, string>())
            {
                if (responses.TryGetValue(status.ToString(CultureInfo.InvariantCulture), out var response)
                    && response is OpenApiResponse mutableResponse)
                {
                    mutableResponse.Description = $"RFC 9457 problem details. Possible code values: {codes}.";
                }
            }

            return Task.CompletedTask;
        });
    }
}
