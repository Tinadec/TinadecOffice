using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;

namespace TinadecCore.AspNetCore.Endpoints;

public static class ToolSettingsEndpoints
{
    public static IEndpointRouteBuilder MapToolSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tools/settings/schema", (IToolSettingsStore store, IConfiguration configuration) => Results.Ok(new ToolSettingsSchemaDto
        {
            Schema = store.GetSchema(), Defaults = ToolSettingsSchema.Defaults,
            HostSettings = new() { ExecutablePath = configuration["TinadecTools:ExecutablePath"], StartupTimeoutSeconds = configuration["TinadecTools:StartupTimeoutSeconds"], DefaultTimeoutSeconds = configuration["TinadecTools:DefaultTimeoutSeconds"], DefaultWorkspaceRoot = configuration["TinadecTools:DefaultWorkspaceRoot"] }
        })).Produces<ToolSettingsSchemaDto>();
        app.MapGet("/api/v1/tools/settings/defaults", async (HttpResponse response, IToolSettingsStore store, CancellationToken ct) => WithEtag(response, await store.GetAsync(null, ct))).Produces<ToolSettingsDocumentDto>();
        app.MapPut("/api/v1/tools/settings/defaults", async (Guid? project_id, ToolSettingsWriteDto input, HttpRequest request, HttpResponse response, IToolSettingsStore store, CancellationToken ct) => WithEtag(response, await store.SaveAsync(null, input.Settings, Revision(request), ct, project_id))).Produces<ToolSettingsDocumentDto>().ProducesProblem(428).ProducesProblem(412).ProducesProblem(400);
        app.MapGet("/api/v1/tools/settings/agents/{agentId:guid}", async (Guid agentId, HttpResponse response, IToolSettingsStore store, IDbContextFactory<AgentConfigurationDbContext> factory, ITenantContextAccessor tenant, CancellationToken ct) =>
        {
            await RequireAgentAsync(agentId, factory, tenant, ct);
            return WithEtag(response, await store.GetAsync(agentId, ct));
        }).Produces<ToolSettingsDocumentDto>().ProducesProblem(404);
        app.MapPut("/api/v1/tools/settings/agents/{agentId:guid}", async (Guid agentId, Guid? project_id, ToolSettingsWriteDto input, HttpRequest request, HttpResponse response, IToolSettingsStore store, IDbContextFactory<AgentConfigurationDbContext> factory, ITenantContextAccessor tenant, CancellationToken ct) =>
        {
            await RequireAgentAsync(agentId, factory, tenant, ct);
            return WithEtag(response, await store.SaveAsync(agentId, input.Settings, Revision(request), ct, project_id));
        }).Produces<ToolSettingsDocumentDto>().ProducesProblem(428).ProducesProblem(412).ProducesProblem(400).ProducesProblem(404);
        app.MapDelete("/api/v1/tools/settings/agents/{agentId:guid}", async (Guid agentId, HttpRequest request, HttpResponse response, IToolSettingsStore store, IDbContextFactory<AgentConfigurationDbContext> factory, ITenantContextAccessor tenant, CancellationToken ct) =>
        {
            await RequireAgentAsync(agentId, factory, tenant, ct);
            return WithEtag(response, await store.ResetAsync(agentId, Revision(request), ct));
        }).Produces<ToolSettingsDocumentDto>().ProducesProblem(428).ProducesProblem(412).ProducesProblem(404);
        app.MapGet("/api/v1/tools/settings/effective", async (Guid? agent_id, Guid? project_id, IToolConfigurationResolver resolver, IToolProvider provider, IConfiguration configuration, ISessionLocator sessions, IDbContextFactory<AgentConfigurationDbContext> factory, ITenantContextAccessor tenant, CancellationToken ct) =>
        {
            if (agent_id is { } id) await RequireAgentAsync(id, factory, tenant, ct);
            var project = project_id is { } projectId ? await sessions.FindProjectAsync(projectId, ct) : null;
            if (project_id is not null && (project is null || project.TenantId != tenant.Current.TenantId || project.WorkspaceId != tenant.Current.WorkspaceId)) throw new ToolSettingsException("project_not_found", "Project not found.", 404);
            // This returns frozen secret references rather than credential values.
            var context = await resolver.ResolveForInspectionAsync(project_id, agent_id, ct);
            var root = project?.RootPath ?? configuration["TinadecTools:DefaultWorkspaceRoot"] ?? Directory.GetCurrentDirectory();
            ToolManifestDto manifest;
            var diagnostics = context.ResourceDiagnostics.ToList();
            try { manifest = await provider.GetManifestAsync(root, ct); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                manifest = new() { Tools = [] };
                diagnostics.Add(new() { Kind = "host", Status = "unavailable", Reason = ex.Message });
            }
            IReadOnlyList<string> granted = ["*"];
            if (agent_id is { } aid)
            {
                await using var db = await factory.CreateDbContextAsync(ct); var actor = tenant.Current;
                var definition = await db.AgentDefinitions.AsNoTracking().SingleAsync(x => x.Id == aid && x.TenantId == actor.TenantId && x.WorkspaceId == actor.WorkspaceId, ct);
                using var toolsDoc = JsonDocument.Parse(definition.ToolScopeJson ?? "[]");
                var scopes = toolsDoc.RootElement;
                if (scopes.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "allowed_tools", "tool_enabled", "tools" }) if (scopes.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array) { scopes = list; break; }
                granted = scopes.ValueKind == JsonValueKind.Array ? scopes.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
            }
            var allowed = manifest.Tools.Where(t => (granted.Contains("*") || granted.Contains(t.Id, StringComparer.OrdinalIgnoreCase)) && ToolSettingsSchema.IsEnabled(context.Settings,t.Id)).Select(t => t.Id).ToArray();
            return Results.Ok(new ToolExecutionContextDto { AgentDefinitionId = context.AgentDefinitionId, SettingsHash = context.SettingsHash, Settings = context.Settings, McpServers = context.McpServers, ReadRoots = context.ReadRoots, SkillResources = context.SkillResources, McpImportError = context.McpImportError, AllowedToolIds = allowed, ResourceDiagnostics = diagnostics });
        }).Produces<ToolExecutionContextDto>().ProducesProblem(404);
        app.MapGet("/api/v1/tools/settings/capabilities", async (Guid? agent_id, Guid? project_id, IToolConfigurationResolver resolver, IToolProvider provider, IConfiguration configuration, ISessionLocator sessions, IDbContextFactory<AgentConfigurationDbContext> factory, ITenantContextAccessor tenant, CancellationToken ct) =>
        {
            if (agent_id is { } agent) await RequireAgentAsync(agent, factory, tenant, ct);
            var context = await resolver.ResolveForInspectionAsync(project_id, agent_id, ct);
            var project = project_id is { } id ? await sessions.FindProjectAsync(id, ct) : null;
            if (project_id is not null && (project is null || project.TenantId != tenant.Current.TenantId || project.WorkspaceId != tenant.Current.WorkspaceId)) throw new ToolSettingsException("project_not_found", "Project not found.", 404);
            var root = project?.RootPath ?? configuration["TinadecTools:DefaultWorkspaceRoot"] ?? Directory.GetCurrentDirectory();
            try
            {
                var response = await provider.CallAsync(root, new() { ToolId = "#capabilities", SessionId = "capabilities", Approved = false, Params = JsonSerializer.SerializeToElement(new { }), ExecutionContext = await resolver.MaterializeForCallAsync(context, [], ct, toolId: "#capabilities") }, TimeSpan.FromSeconds(15), ct);
                return Results.Ok(new ToolCapabilitiesDto { Status = response.IsSuccess ? "available" : "unavailable", Reason = response.Error, Capabilities = response.Result, SettingsHash = context.SettingsHash });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            { return Results.Ok(new ToolCapabilitiesDto { Reason = ex.Message, SettingsHash = context.SettingsHash }); }
        }).Produces<ToolCapabilitiesDto>().ProducesProblem(404);
        app.MapGet("/api/v1/tools/mcp/servers", (Guid? project_id, IMcpResourceRegistry registry, CancellationToken ct) => registry.ListAsync(project_id, ct)).Produces<IReadOnlyList<McpResourceDto>>();
        app.MapPost("/api/v1/tools/mcp/servers", async (McpResourceWriteDto input, HttpRequest request, HttpResponse response, IMcpResourceRegistry registry, CancellationToken ct) =>
        {
            if (Revision(request) != 0) throw new ToolSettingsException("revision_conflict", "A new MCP resource requires revision zero.", 412);
            var result = await registry.SaveAsync(null, input, 0, ct); response.Headers.ETag = $"\"{result.Revision}\""; return Results.Ok(result);
        }).Produces<McpResourceDto>().ProducesProblem(400).ProducesProblem(428).ProducesProblem(412);
        app.MapPut("/api/v1/tools/mcp/servers/{resourceId:guid}", async (Guid resourceId, McpResourceWriteDto input, HttpRequest request, HttpResponse response, IMcpResourceRegistry registry, CancellationToken ct) =>
        {
            var result = await registry.SaveAsync(resourceId, input, Revision(request), ct); response.Headers.ETag = $"\"{result.Revision}\""; return Results.Ok(result);
        }).Produces<McpResourceDto>().ProducesProblem(428).ProducesProblem(412).ProducesProblem(400).ProducesProblem(404);
        app.MapDelete("/api/v1/tools/mcp/servers/{resourceId:guid}", async (Guid resourceId, HttpRequest request, IMcpResourceRegistry registry, CancellationToken ct) =>
        {
            await registry.DeleteAsync(resourceId, Revision(request), ct); return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent).ProducesProblem(428).ProducesProblem(412).ProducesProblem(404);
        app.MapPost("/api/v1/tools/mcp/servers/import-json", async (McpImportJsonRequestDto input, IMcpServerService mcpService, CancellationToken ct) =>
            Results.Ok(await mcpService.ImportJsonAsync(input, ct))).Produces<McpImportJsonResultDto>().ProducesProblem(400);
        app.MapPatch("/api/v1/tools/mcp/servers/{resourceId:guid}/enabled", async (Guid resourceId, McpServerEnabledRequestDto input, HttpRequest request, HttpResponse response, IMcpServerService mcpService, CancellationToken ct) =>
        {
            if (input.Enabled is not { } enabled) throw new ToolSettingsException("invalid_request", "enabled (true/false) is required.");
            var rawIfMatch = request.Headers.IfMatch.FirstOrDefault();
            long? expected = rawIfMatch is null ? null : Revision(request);
            var updated = await mcpService.SetEnabledAsync(resourceId.ToString("N"), enabled, input.ProjectId, expected, ct);
            response.Headers.ETag = $"\"{updated.Revision}\"";
            return Results.Ok(updated);
        }).Produces<McpResourceDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(412);
           app.MapPost("/api/v1/tools/mcp/servers/{resourceId:guid}/install-preview", async (Guid resourceId, Guid? project_id, string? action, IManagedMcpProgramService programs, CancellationToken ct) =>
            Results.Ok(await programs.PreviewAsync(resourceId, project_id, action ?? "install", ct))).Produces<ManagedMcpProgramPreviewDto>().ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/tools/mcp/servers/{resourceId:guid}/install", async (Guid resourceId, Guid? project_id, string? action, IManagedMcpProgramService programs, CancellationToken ct) =>
        {
            var preview = await programs.PreviewAsync(resourceId, project_id, action ?? "install", ct);
            return Results.Ok(await programs.RequestApplyAsync(preview, ct));
        }).Produces<UserToolActionResult>().ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/tools/mcp/servers/{resourceId:guid}/test", async (Guid resourceId, IMcpServerService mcpService, CancellationToken ct) =>
            Results.Ok(await mcpService.TestAsync(resourceId.ToString("N"), null, ct))).Produces<McpServerTestResultDto>().ProducesProblem(404);
        return app;
    }
    private static IResult WithEtag(HttpResponse response, ToolSettingsDocumentDto dto) { response.Headers.ETag = $"\"{dto.Revision}\""; return Results.Ok(dto); }
    private static long Revision(HttpRequest request)
    {
        var raw = request.Headers.IfMatch.FirstOrDefault();
        if (raw is null) throw new ToolSettingsException("precondition_required", "If-Match is required. Reload the current configuration before saving.", 428);
        if (!long.TryParse(raw.Trim('"'), out var revision) || revision < 0) throw new ToolSettingsException("invalid_precondition", "If-Match must contain a quoted revision.");
        return revision;
    }
    private static async Task RequireAgentAsync(Guid id, IDbContextFactory<AgentConfigurationDbContext> factory, ITenantContextAccessor tenant, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var actor = tenant.Current;
        if (!await db.AgentDefinitions.AnyAsync(x => x.Id == id && x.TenantId == actor.TenantId && x.WorkspaceId == actor.WorkspaceId && x.ArchivedAt == null, ct)) throw new ToolSettingsException("agent_not_found", "Agent definition not found.", 404);
    }
}
