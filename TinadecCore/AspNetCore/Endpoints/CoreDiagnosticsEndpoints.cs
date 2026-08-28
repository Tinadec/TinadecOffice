using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Persistence;

namespace TinadecCore.AspNetCore.Endpoints;

public static class CoreDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapCoreDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
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

        return app;
    }
}
