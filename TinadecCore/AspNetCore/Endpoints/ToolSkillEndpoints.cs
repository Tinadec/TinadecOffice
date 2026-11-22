using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

public static class ToolSkillEndpoints
{
    public static IEndpointRouteBuilder MapToolSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tools/skills").WithTags("Tool Skills");
        group.MapGet("", async (Guid? project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try { return Results.Ok(await skills.ListAsync(project_id, includeBuiltins: true, ct)); } catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillListDto>();
        group.MapGet("/diagnostics", async (Guid? project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try
            {
                var list = await skills.ListAsync(project_id, includeBuiltins: true, ct);
                return Results.Ok(new
                {
                    source = list.Source,
                    skills = list.Skills.Select(x => new
                    {
                        x.ResourceId, x.Name, x.SourceKind, x.Scope, x.Rank, x.Enabled, x.Valid,
                        x.ShadowedBy, x.ReadOnly, x.ReasonCode, x.Path,
                    }).ToArray(),
                    diagnostics = list.Diagnostics,
                });
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        });
        group.MapGet("/{resourceId:guid}", async (Guid resourceId, Guid? project_id, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try
            {
                var resource = await skills.GetAsync(resourceId, project_id, ct);
                if (resource is null) return Results.NotFound();
                response.Headers.ETag = $"\"{resource.Revision}\"";
                return Results.Ok(resource);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{resourceId:guid}/copy-to-project", async (Guid resourceId, Guid project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try { return Results.Ok(await skills.RequestCopyToProjectAsync(resourceId, project_id, ct)); }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapGet("/{resourceId:guid}/files", async (Guid resourceId, Guid? project_id, string path, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try
            {
                var file = await skills.GetFileAsync(resourceId, project_id, path, ct);
                return file is null ? Results.NotFound() : Results.Ok(file);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillFileDto>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/import", async (ToolSkillWriteDto input, HttpRequest request, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (!Revision(request, out var revision)) return Results.Problem("If-Match with revision zero is required for a new resource.", statusCode: 428);
            if (revision != 0) return Results.Problem("A new resource requires revision zero.", statusCode: 412);
            try
            {
                var resource = await skills.RequestSaveAsync(null, input.ProjectId, input, 0, ct);
                response.Headers.ETag = $"\"{resource.Revision}\"";
                return Results.Ok(resource);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        group.MapPut("/{resourceId:guid}", async (Guid resourceId, Guid? project_id, ToolSkillWriteDto input, HttpRequest request, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (!Revision(request, out var revision)) return Results.Problem("If-Match with the loaded revision is required.", statusCode: 428);
            try
            {
                var resource = await skills.RequestSaveAsync(resourceId, project_id, input, revision, ct);
                response.Headers.ETag = $"\"{resource.Revision}\"";
                return Results.Ok(resource);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(412).ProducesProblem(428);
        group.MapDelete("/{resourceId:guid}", async (Guid resourceId, Guid? project_id, HttpRequest request, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (!Revision(request, out var revision)) return Results.Problem("If-Match with the loaded revision is required.", statusCode: 428);
            try { return Results.Ok(await skills.RequestDeleteAsync(resourceId, project_id, revision, ct)); } catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(412).ProducesProblem(428);

        group.MapPost("/install", async (ToolSkillInstallRequestDto input, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try { return Results.Ok(await skills.InstallAsync(input, ct)); }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillInstallResultDto>().ProducesProblem(400).ProducesProblem(409);

        group.MapPatch("/{resourceId:guid}/enabled", async (Guid resourceId, ToolSkillEnabledRequestDto input, HttpRequest request, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (input.Enabled is not { } enabled)
                return Results.Problem("enabled (true/false) is required.", statusCode: 400);
            long? expected = request.Headers.IfMatch.Count > 0 && Revision(request, out var rev) ? rev : null;
            if (request.Headers.IfMatch.Count > 0 && expected is null)
                return Results.Problem("If-Match must contain a valid non-negative integer.", statusCode: 400);
            try
            {
                var updated = await skills.SetEnabledAsync(resourceId, enabled, input.ProjectId, expected, ct);
                response.Headers.ETag = $"\"{updated.Revision}\"";
                return Results.Ok(updated);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412);

        group.MapPost("/reload", async (Guid? project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try { return Results.Ok(await skills.ReloadAsync(project_id, ct)); }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillListDto>();

        app.MapGet("/api/v1/tools/external-cli/discover", async (
            Guid? project_id,
            string? ecosystems,
            IExternalCliDiscoveryService discovery,
            CancellationToken ct) =>
        {
            var ecoList = string.IsNullOrWhiteSpace(ecosystems)
                ? null
                : ecosystems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Ok(await discovery.DiscoverAsync(project_id, ecoList, ct));
        }).WithTags("Tool Skills").Produces<ExternalCliDiscoveryDto>();
        return app;
    }
    private static bool Revision(HttpRequest request, out long revision) => long.TryParse(request.Headers.IfMatch.ToString().Trim('"'), out revision) && revision >= 0;
    private static IResult Problem(ToolSettingsException ex) => Results.Problem(ex.Message, statusCode: ex.StatusCode, extensions: new Dictionary<string, object?> { ["error_code"] = ex.Code });
}

