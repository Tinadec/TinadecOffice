using System.Text.Json;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Api.Endpoints;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Configure snake_case JSON serialization for all HTTP responses.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// Shared database abstraction (SQLite default; PostgreSQL optional) before business modules.
builder.Services.AddTinadecPersistence(builder.Configuration, builder.Environment.ContentRootPath);

// Register all TinadecCore modules.
builder.Services.AddTinadecCore();

var app = builder.Build();

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
});

// ============================================================
// GET /api/v1/harness/manifest — returns dual-layer Agent, MAF version, eight module manifest
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
                Layer = "planning",
                Role = "Planning layer: proactive planning and supervision",
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
});

// ============================================================
// GET /api/v1/readiness — MAF assemblies loadable = ready; unconfigured modules use warning
// ============================================================
app.MapGet("/api/v1/readiness", async (
    ITinadecCoreBuilder coreBuilder,
    IDatabaseReadiness databaseReadiness,
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

    var hasModuleWarnings = modules.Any(m => m.RegistrationStatus == ModuleRegistrationStatus.NotConfigured);
    var hasStorageWarning = storageProbe.State != DatabaseReadinessState.Ready;
    var status = hasModuleWarnings || hasStorageWarning ? "warning" : "ready";

    var response = new ReadinessResponseDto
    {
        Status = status,
        FrameworkReady = true,
        FrameworkName = "Microsoft Agent Framework",
        FrameworkVersion = "1.13.0",
        Storage = storage,
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
});

// ============================================================
// Stub endpoints for Gateway proxy and Desktop frontend consumption.
// GET endpoints return 200 with empty collections.
// Write endpoints return 501 Not Implemented.
// ============================================================
app.MapStorageEndpoints();
app.MapStubEndpoints();

app.Run();

/// <summary>
/// Exposed for integration test hosting (WebApplicationFactory).
/// </summary>
public partial class Program;
