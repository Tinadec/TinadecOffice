using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Api.Endpoints;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Global snake_case JSON + validation ProblemDetails (RFC 9457 shape).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var detail = string.Join("; ", context.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        var problem = new ProblemDetails
        {
            Type = "https://tinadec.dev/errors/invalid_request",
            Title = "invalid_request",
            Detail = string.IsNullOrWhiteSpace(detail) ? "Request validation failed." : detail,
            Status = StatusCodes.Status400BadRequest,
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["code"] = "invalid_request";
        problem.Extensions["trace_id"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    };
});

// Shared database abstraction (SQLite default; PostgreSQL optional) before business modules.
builder.Services.AddTinadecPersistence(builder.Configuration, builder.Environment.ContentRootPath);

// Register all TinadecCore modules.
builder.Services.AddTinadecCore();
builder.Services.AddScoped<ControlPlaneService>();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["trace_id"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddOpenApi();

var app = builder.Build();

// RFC 9457 + snake_case ProblemDetails with trace_id/code extension.
// Must be registered before endpoint mapping so it wraps all handlers.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var exception = feature?.Error;
        // Map exception types to required code set:
        // invalid_request, context_conflict, model_not_configured, run_not_found, forbidden, conflict
        var (status, code, detail) = exception switch
        {
            TinadecCore.DmaEA.RunAdmissionException rae when rae.Code == "CONTEXT_REVISION_CONFLICT" => (StatusCodes.Status409Conflict, "context_conflict", rae.Message),
            TinadecCore.DmaEA.RunAdmissionException rae => (StatusCodes.Status409Conflict, "conflict", rae.Message),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "forbidden", exception.Message),
            ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request", exception.Message),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "run_not_found", exception.Message),
            InvalidOperationException ioe when ioe.Message.Contains("model", StringComparison.OrdinalIgnoreCase) || ioe.Message.Contains("Provider", StringComparison.OrdinalIgnoreCase) => (StatusCodes.Status400BadRequest, "model_not_configured", ioe.Message),
            InvalidOperationException => (StatusCodes.Status409Conflict, "conflict", exception.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.")
        };
        var problem = new ProblemDetails
        {
            Type = $"https://tinadec.dev/errors/{code}",
            Title = code,
            Detail = detail,
            Status = status,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["trace_id"] = context.TraceIdentifier;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    });
});
app.UseStatusCodePages();

// Core-internal OpenAPI is the source of truth; Gateway generates its own external OpenAPI.
app.MapOpenApi("/openapi/core.json").WithSummary("TinadecCore internal OpenAPI");

// SQLite migrates at local startup. PostgreSQL only does so when explicitly configured.
using (var scope = app.Services.CreateScope())
{
    var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value;
    var connection = scope.ServiceProvider.GetRequiredService<IDatabaseConnectionInfo>();
    if (storageOptions.Enabled && connection.IsConfigured)
    {
        await scope.ServiceProvider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
        await scope.ServiceProvider.GetRequiredService<TinadecCore.Lifecycle.StorageLifecycleService>().ReconcileAsync();
    }
}

// Idempotent dev bootstrap: chat route + planner/executor agents when missing.
using (var seedScope = app.Services.CreateScope())
{
    await TinadecCore.Runtime.DevSeed.SeedIfMissingAsync(seedScope.ServiceProvider, CancellationToken.None);
}

// ============================================================
// GET /api/v1/health — legacy-compatible {name, status, version, time}
// ============================================================
app.MapGet("/api/v1/health", () =>
{
    return Results.Ok(new HealthResponseDto
    {
        Name = "tinadec-core",
        Status = "ok",
        Version = "0.1.0",
        Time = DateTimeOffset.UtcNow
    });
}).WithSummary("Health probe").WithDescription("Legacy-compatible health probe.");

// ============================================================
// GET /api/v1/harness/manifest — returns dual-layer Agent, MAF version, and Core module manifest
// ============================================================
app.MapGet("/api/v1/harness/manifest", (ITinadecCoreBuilder coreBuilder) =>
{
    var modules = coreBuilder.GetRegisteredModules();

    var manifest = new HarnessManifestDto
    {
        Runtime = "tinadec-core-maf-0.1.0",
        OwnershipModel = "core-authoritative",
        ToolRegistry = new ToolRegistrySummaryDto
        {
            DeclaredToolCount = 0,
            CanonicalToolCount = 0,
            DuplicateToolIdCount = 0,
            DuplicateToolIds = [],
            SourcePrecedence = ["builtin", "extension", "mcp", "acp"],
            SelectionPolicy = "first-source-wins"
        },
        AgentLayers =
        [
            new AgentLayerManifestDto
            {
                Layer = "operation",
                Role = "Operation layer: intent understanding, coordination, supervision",
                AgentCount = 0,
                EnabledAgentCount = 0,
                MaxParallelExecutors = 1,
                WorktreeIsolation = false,
                ApprovalRequired = false,
                AgentTypes = [],
                ToolIds = []
            },
            new AgentLayerManifestDto
            {
                Layer = "execution",
                Role = "Execution layer: passive task execution",
                AgentCount = 0,
                EnabledAgentCount = 0,
                MaxParallelExecutors = 4,
                WorktreeIsolation = false,
                ApprovalRequired = false,
                AgentTypes = [],
                ToolIds = []
            }
        ],
        ToolProviders = [],
        ToolRisks = [],
        Tools = [],
        DesignNotes =
        [
            "Core is the sole state authority: sessions, runs, tasks, approvals, events, traces.",
            "Gateway is a thin proxy; Desktop is presentation-only.",
            "Tool-layer capabilities are Core-governed; all mutations go through approval gates.",
            "MAF is the technical foundation; DmaEA is the Tinadec dual-layer multi-agent framework built on top."
        ],
        Framework = new FrameworkInfoDto(),
        Modules = modules.Select(m => m.ToDto()).ToList()
    };

    return Results.Ok(manifest);
}).WithSummary("Harness manifest");

// ============================================================
// GET /api/v1/readiness — MAF assemblies loadable = ready; unconfigured modules use warning
// ============================================================
app.MapGet("/api/v1/readiness", async (
    ITinadecCoreBuilder coreBuilder,
    IDatabaseReadiness databaseReadiness,
    TinadecCore.DmaEA.IAgentRuntimeConfiguration agentRuntime,
    CancellationToken cancellationToken) =>
{
    var modules = coreBuilder.GetRegisteredModules();
    var storageProbe = await databaseReadiness.ProbeAsync(cancellationToken).ConfigureAwait(false);
    var storage = new ReadinessStorageDto
    {
        Provider = storageProbe.Provider,
        State = storageProbe.StateName,
        Detail = storageProbe.Detail
    };
    var runtimeDiagnostic = agentRuntime.Diagnostic;

    var hasModuleWarnings = modules.Any(m => m.RegistrationStatus == ModuleRegistrationStatus.NotConfigured);
    var hasStorageWarning = storageProbe.State != DatabaseReadinessState.Ready;
    var hasRuntimeWarning = runtimeDiagnostic.State != "ready";
    var status = hasModuleWarnings || hasStorageWarning || hasRuntimeWarning ? "warning" : "ready";

    var response = new ReadinessResponseDto
    {
        Status = status,
        FrameworkReady = true,
        FrameworkName = "Microsoft Agent Framework",
        FrameworkVersion = "1.18.0",
        Storage = storage,
        AgentRuntime = new ReadinessAgentRuntimeDto
        {
            State = runtimeDiagnostic.State,
            Detail = runtimeDiagnostic.Detail,
            SourcePath = runtimeDiagnostic.SourcePath,
            CheckedAt = runtimeDiagnostic.CheckedAt
        },
        Modules = modules.Select(m => new ReadinessModuleDto
        {
            ModuleId = m.ModuleId,
            ModuleState = m.RegistrationStatus switch
            {
                ModuleRegistrationStatus.Registered => "registered",
                ModuleRegistrationStatus.NotConfigured => "not_configured",
                ModuleRegistrationStatus.Disabled => "disabled",
                _ => "unknown"
            },
            Detail = m.RegistrationStatus == ModuleRegistrationStatus.NotConfigured
                ? $"Module '{m.ModuleId}' is registered but not configured with real providers."
                : null
        }).ToList()
    };

    return Results.Ok(response);
}).WithSummary("Readiness probe");

// ============================================================
// Stub endpoints for Gateway proxy and Desktop frontend consumption.
// GET endpoints return 200 with empty collections.
// Write endpoints return 501 Not Implemented.
// ============================================================
app.MapStorageEndpoints();
app.MapDmaeaEndpoints();
app.MapAgentConfigurationEndpoints();
app.MapInteractionsEndpoints();
app.MapControlPlaneEndpoints();
app.MapGovernanceEndpoints();
app.MapMemoryReviewEndpoints();
app.MapEvolutionEndpoints();
app.MapWorkspaceSnapshotEndpoints();
app.MapUserToolActionEndpoints();
app.MapStubEndpoints();



app.Run();

/// <summary>
/// Exposed for integration test hosting (WebApplicationFactory).
/// </summary>
public partial class Program;
