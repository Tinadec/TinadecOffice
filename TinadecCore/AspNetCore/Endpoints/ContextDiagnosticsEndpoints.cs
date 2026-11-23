using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// HTTP surface for P1 Context Diagnostics &amp; 64-char Winnowing Overlap Audit:
/// <list type="bullet">
/// <item><c>GET /api/v1/runs/{runId}/context-diagnostics</c> — 5% window occupancy buckets, Context Rot Detector, ContextPack compaction reconciliation, unified governance signals, and executive findings.</item>
/// <item><c>POST /api/v1/context/audit-overlap</c> — 64-character rolling-hash Winnowing cross-source overlap audit over live workspace instructions, prompt pipelines/fragments, skills, tool schemas, or frozen run snapshots.</item>
/// </list>
/// </summary>
public static class ContextDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapContextDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/{runId}/context-diagnostics", async (
            string runId,
            int? context_window_tokens,
            string? selected_pack_id,
            ITenantContextAccessor tenant,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out var runGuid) || runGuid == Guid.Empty)
            {
                return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid Guid." });
            }

            var diagnostics = services.GetService<IRunContextDiagnosticsService>()
                ?? new RunContextDiagnosticsService(
                    services.GetRequiredService<ILifecycleManager>(),
                    services.GetService<IRunReplayService>(),
                    services.GetService<IToolExecutionCoordinator>(),
                    services.GetService<IRunModelInvocationReader>()
                        ?? services.GetService<ITinadecConfigToolGateway>() as IRunModelInvocationReader);

            var scope = tenant.Current;
            var result = await diagnostics.DiagnoseRunAsync(
                runGuid,
                scope.TenantId,
                scope.WorkspaceId,
                context_window_tokens,
                selected_pack_id,
                ct).ConfigureAwait(false);

            return result is null
                ? Results.NotFound(new { code = "NOT_FOUND", message = "Run was not found." })
                : Results.Ok(result);
        })
        .Produces<RunContextDiagnosticsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/context/audit-overlap", async (
            HttpRequest httpRequest,
            ITenantContextAccessor tenant,
            IConfiguration configuration,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            ContextOverlapAuditRequestDto? request = null;
            if (httpRequest.ContentLength is not 0 && httpRequest.HasJsonContentType())
            {
                try
                {
                    request = await httpRequest.ReadFromJsonAsync<ContextOverlapAuditRequestDto>(cancellationToken: ct).ConfigureAwait(false);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    return Results.BadRequest(new { code = "INVALID_AUDIT_REQUEST", message = $"Invalid JSON request body: {ex.Message}" });
                }
            }

            request ??= new ContextOverlapAuditRequestDto();

            var auditor = services.GetService<IContextOverlapAuditor>()
                ?? new ContextOverlapAuditor(
                    services.GetService<ILifecycleManager>(),
                    services.GetService<ISessionLocator>(),
                    services.GetService<IPromptFragmentService>(),
                    services.GetService<IToolSkillResourceService>(),
                    services.GetService<IToolRegistry>(),
                    services.GetService<IToolProvider>(),
                    services.GetService<IMcpServerService>(),
                    services.GetService<IContextAuditCatalogReader>()
                        ?? services.GetService<ITinadecConfigToolGateway>() as IContextAuditCatalogReader);

            var current = tenant.Current;
            var defaultWorkspaceRoot = configuration["TinadecTools:DefaultWorkspaceRoot"];
            var executionScope = new ContextAuditExecutionScope(
                current.TenantId,
                current.WorkspaceId,
                ProjectId: request.ProjectId,
                AuthorizedWorkspaceRoot: defaultWorkspaceRoot);

            try
            {
                var audit = await auditor.AuditAsync(request, executionScope, ct).ConfigureAwait(false);
                return Results.Ok(audit);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "NOT_FOUND", message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "INVALID_AUDIT_REQUEST", message = ex.Message });
            }
        })
        .Accepts<ContextOverlapAuditRequestDto>("application/json")
        .Produces<ContextOverlapAuditDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
