using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA.LiveExplainer;
using TinadecCore.Persistence;

namespace TinadecCore.DmaEA;

/// <summary>
/// DmaEA module registrar. Registers the dual-layer agent orchestrator.
/// </summary>
public sealed class DmaEAModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "dma_ea";

    public void Register(ITinadecCoreBuilder builder)
    {
        Maf18RuntimeAdapter.EnsureCompatible();
        builder.Services.AddDbContextFactory<AgentControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<AgentControlDbContext>>();
        builder.Services.AddDbContextFactory<SessionPlanDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<SessionPlanDbContext>>();
        builder.Services.AddSingleton<SessionPlanService>();
        builder.Services.AddSingleton<ITeamTaskBoardPort>(sp => sp.GetRequiredService<SessionPlanService>());
        builder.Services.AddSingleton<AgentRuntimeConfigurationStore>();
        builder.Services.AddSingleton<IConfigurationDocumentValidator, RuntimeConfigurationDocumentValidator>();
        builder.Services.AddSingleton<IAgentRuntimeConfiguration>(sp => sp.GetRequiredService<AgentRuntimeConfigurationStore>());
        // Governance consumes the policy through this port; DmaEA owns the
        // runtime.toml document, so the dependency points one way only.
        builder.Services.AddSingleton<IToolApprovalPolicySource, RuntimeToolApprovalPolicySource>();
        builder.Services.AddSingleton<IAgentRuntimeConfigurationResolver, AgentRuntimeConfigurationResolver>();
        builder.Services.AddSingleton<IRuntimeContextSettings, RuntimeContextSettingsAdapter>();
        // The chat client factory, the harness transports, the binary resolver and the terminal host are
        // registered by the model-interface module: this module consumes them through
        // IAgentChatClientFactory and the other ports, and cannot reference Models to build them here.
        builder.Services.AddSingleton<ITinaChatIntentInterpreter, TinaChatIntentInterpreter>();
        // Standing organization members take their turns through the same port the engine uses, so the
        // communication module resolves the runner lazily and neither module constructs the other.
        builder.Services.AddSingleton<ITinaChatMemberTurnRunner>(sp => new TinaChatMemberTurnRunner(
            sp.GetRequiredService<ILifecycleManager>(),
            sp.GetRequiredService<IAgentChatClientFactory>(),
            sp,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TinaChatMemberTurnRunner>>(),
            sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?.GetValue<int?>("TinadecTinaChat:MemberTurnRounds") ?? 6));
        builder.Services.AddSingleton<IGovernanceTopicSink, GovernanceTopicSink>();
        // B3: coalesces run facts into one planner wake and closes the planning loop when the
        // ledger is empty and coverage is sufficient. Ports are optional so a trimmed host runs.
        builder.Services.AddSingleton(sp => new AutonomousExplorationCoordinator(
            sp.GetRequiredService<IGovernanceTopicSink>(),
            sp.GetService<IPlanLedger>(),
            sp.GetService<IExplorationGraph>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<AutonomousExplorationCoordinator>>()));
        builder.Services.AddHostedService<AutonomousExplorationCoordinatorService>();
        // B4: settlement of terminal worker results into the exploration graph and the plan ledger,
        // and read-only trace search across one run's events, tools and evidence.
        builder.Services.AddSingleton(sp => new WorkerSettlementService(
            sp.GetService<IExplorationGraph>(),
            sp.GetService<IPlanLedger>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<WorkerSettlementService>>()));
        builder.Services.AddSingleton<IRunTraceSearch>(sp => new RunTraceSearchService(
            sp.GetRequiredService<ILifecycleManager>(),
            sp.GetRequiredService<IToolExecutionCoordinator>(),
            sp.GetService<IEvidenceArchive>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RunTraceSearchService>>()));
        builder.Services.AddSingleton<IApprovalGateJudge, ApprovalGateJudge>();
        builder.Services.AddSingleton<AgentInstanceService>();
        builder.Services.AddSingleton<IAgentInstanceService>(sp => sp.GetRequiredService<AgentInstanceService>());
        builder.Services.AddSingleton<AgentEvaluationService>();
        builder.Services.AddSingleton<IAgentToolAuthorization>(sp => sp.GetRequiredService<AgentInstanceService>());
        builder.Services.AddSingleton<IOperationalTriggerEvaluator, OperationalTriggerEvaluator>();
        builder.Services.AddSingleton<TinadecCore.Abstractions.Ports.IOrchestrationDirectiveValidator, Orchestration.OrchestrationDirectiveValidator>();
        builder.Services.AddSingleton<IRunReplayService, RunReplayService>();
        builder.Services.AddSingleton<IRunContextDiagnosticsService>(sp => new RunContextDiagnosticsService(
            sp.GetRequiredService<ILifecycleManager>(),
            sp.GetService<IRunReplayService>(),
            sp.GetService<IToolExecutionCoordinator>(),
            sp.GetService<IRunModelInvocationReader>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RunContextDiagnosticsService>>()));
        // B9: one run's dossier, assembled from the lifecycle row, the replay, the plan ledger and the
        // exploration graph. Every optional source is reported as a note when this host lacks it.
        builder.Services.AddSingleton<IRunDossier>(sp => new RunDossierService(
            sp.GetRequiredService<ILifecycleManager>(),
            sp.GetService<IRunReplayService>(),
            sp.GetService<IPlanLedger>(),
            sp.GetService<IExplorationGraph>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RunDossierService>>(),
            sp.GetService<IRunContextDiagnosticsService>()));
        // Hard insert (todo D2): process-local, like the in-flight tool registry.
        builder.Services.AddSingleton<IRunInterrupts, RunInterruptRegistry>();
        builder.Services.AddSingleton<ITaskLineageLookup, CheckpointTaskLineageLookup>();
        // The live positions of followed runs. One instance per storage scope, shared by the
        // engine (which publishes after a durable append) and the coordinator (which reads
        // instead of polling storage).
        builder.Services.AddSingleton<RunStreamProjection>();
        builder.Services.AddSingleton<FullDuplexRunEngine>();
        builder.Services.AddSingleton<IFullDuplexRunEngine>(sp => sp.GetRequiredService<FullDuplexRunEngine>());
        builder.Services.AddSingleton<ITeamRunBoardActionSink>(sp => sp.GetRequiredService<FullDuplexRunEngine>());
        builder.Services.AddSingleton<ITeammateControlPort>(sp => sp.GetRequiredService<FullDuplexRunEngine>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FullDuplexRunEngine>());
        builder.Services.AddSingleton<SideQuestionService>();
        builder.Services.AddSingleton<LiveExplainerOptions>();
        builder.Services.AddSingleton<LiveExplainerStore>();
        builder.Services.AddSingleton<ILiveExplainerModelInvoker, DefaultLiveExplainerModelInvoker>();
        builder.Services.AddSingleton<LiveExplainerScoutRunner>(sp => new LiveExplainerScoutRunner(
            sp.GetRequiredService<ILiveExplainerModelInvoker>()));
        builder.Services.AddSingleton<LiveExplainerService>(sp => new LiveExplainerService(
            sp.GetRequiredService<ILifecycleManager>(),
            sp.GetRequiredService<LiveExplainerStore>(),
            sp.GetRequiredService<ILiveExplainerModelInvoker>(),
            sp.GetRequiredService<LiveExplainerScoutRunner>(),
            sp.GetService<IToolExecutionCoordinator>(),
            sp.GetService<Microsoft.EntityFrameworkCore.IDbContextFactory<SessionPlanDbContext>>(),
            sp.GetService<RunStreamProjection>(),
            sp.GetService<IGovernanceTopicSink>(),
            sp.GetRequiredService<LiveExplainerOptions>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<LiveExplainerService>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveExplainerService>());
        builder.Services.AddSingleton<IFullDuplexRunCoordinator, FullDuplexRunCoordinator>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.2.0",
            Dependencies = ["abstractions", "persistence", "lifecycle", "models", "memory", "context", "prompts", "loop_guard", "tools"],
            Capabilities = ["dual_layer_orchestration", "task_dispatch", "team_orchestration", "persistent_teammates", "worktree_integration", "collaboration", "scheduling", "result_aggregation", "operational_trigger_chain", "standing_member_turns", "governance_topic_sink", "approval_gate_judge", "autonomous_exploration_coordinator", "worker_settlement", "cross_worker_trace"],
            Language = "C#",
            MafPrimitives = ["agent", "workflow"],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
