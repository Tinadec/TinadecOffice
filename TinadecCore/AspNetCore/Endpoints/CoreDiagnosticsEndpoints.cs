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
        // GET /api/v1/readiness — unified readiness receipt (plan §5.3 item 1).
        // status: ready | degraded | blocked; items carry per-check
        // ready|degraded|blocked|unavailable plus reason/action. Gateway appends
        // its own items; Core never emits them.
        // ============================================================
        app.MapGet("/api/v1/readiness", async (TinadecCore.Runtime.ReadinessService readiness, CancellationToken cancellationToken) =>
        {
            var receipt = await readiness.GetReceiptAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(receipt);
        }).WithSummary("Readiness receipt").WithDescription(
            "Unified readiness receipt: overall ready|degraded|blocked plus fixed items " +
            "(database, core_storage, agent_pack, default_mode, model_provider, model_secret, " +
            "model_route, model_probe, tool_provider, manifest_hash), each with reason/action.");

        // ============================================================
        // POST /api/v1/model-probe — run the model connectivity probe
        // (1 output token, 10s timeout, 60s result cache). ?force=true bypasses
        // and replaces the cached result. Returns the model_probe readiness item.
        // ============================================================
        app.MapPost("/api/v1/model-probe", async (TinadecCore.Runtime.ModelProbeService probe, bool? force, CancellationToken cancellationToken) =>
        {
            var item = await probe.ProbeAsync(force == true, cancellationToken).ConfigureAwait(false);
            return Results.Ok(item);
        }).WithSummary("Model connectivity probe").WithDescription(
            "Resolves the chat route and sends one minimal completion (max_tokens=1, 10s timeout) " +
            "through the production chat client construction path. Honors TINADEC_MODEL_BASE_URL_OVERRIDE.");

        return app;
    }
}
