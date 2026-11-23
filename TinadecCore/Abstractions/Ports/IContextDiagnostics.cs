using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Caller execution boundary passed when gathering live server-side context sources or run diagnostics.
/// </summary>
public sealed record ContextAuditExecutionScope(
    Guid TenantId,
    Guid WorkspaceId,
    Guid? SessionId = null,
    Guid? ProjectId = null,
    string? AuthorizedWorkspaceRoot = null);

/// <summary>
/// Options controlling the 64-character rolling-hash Winnowing overlap auditor.
/// </summary>
public sealed record WinnowingAuditOptions(
    int KGramSize = 64,
    int WindowSize = 16,
    int MaxExcerptLength = 180,
    int MaxMatchesPerPair = 12);

/// <summary>
/// Optional cross-module catalog reader implemented by the Runtime composition layer so
/// <c>TinadecCore.Context</c> can audit active AgentDefinitions, AgentModes, PromptPipelines,
/// and Tool manifests without directly referencing sibling business modules.
/// </summary>
public interface IContextAuditCatalogReader
{
    Task<IReadOnlyList<ContextAuditSourceInputDto>> GatherCatalogSourcesAsync(
        ContextOverlapAuditRequestDto request,
        ContextAuditExecutionScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional cross-module reader for persisted model invocations of a single run so
/// <c>TinadecCore.DmaEA</c> never directly references <c>TinadecCore.Lifecycle</c>.
/// </summary>
public interface IRunModelInvocationReader
{
    Task<IReadOnlyList<ModelInvocationDto>> ListForRunAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid runId,
        int limit = 500,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 64-character rolling-hash Winnowing context overlap auditor (P1 Track 2).
/// </summary>
public interface IContextOverlapAuditor
{
    /// <summary>
    /// Audits an explicit in-memory list of context sources using dual-prime Rabin-Karp rolling-hash
    /// Winnowing (<c>k=64, w=16</c> by default) with 1-based line ranges and CJK-aware token estimates.
    /// </summary>
    ContextOverlapAuditDto AuditSources(
        IReadOnlyList<ContextAuditSourceInputDto> sources,
        WinnowingAuditOptions? options = null);

    /// <summary>
    /// Gathers real workspace instructions (<c>AGENTS.md</c>, <c>.apm/instructions</c>), active prompt
    /// pipelines/fragments, agent system prompts, enabled skills bodies, and tool/MCP schemas within
    /// the authorized scope (or frozen run snapshot when <c>run_id</c> is provided), plus any
    /// <c>extra_sources</c>, and runs the Winnowing overlap audit.
    /// </summary>
    Task<ContextOverlapAuditDto> AuditAsync(
        ContextOverlapAuditRequestDto request,
        ContextAuditExecutionScope? scope = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input records for deterministic in-memory evaluation in <see cref="IRunContextDiagnosticsService"/>.
/// </summary>
public sealed record DiagnosticContextBudgetShare(string Source, int Tokens);

public sealed record DiagnosticContextPackSnapshot(
    string Id,
    string RunId,
    string? LaneKey,
    int EvidenceCount,
    int EstimatedTokens,
    int TokenBudget,
    IReadOnlyList<string> Sources,
    IReadOnlyList<DiagnosticContextBudgetShare> SourceTokens,
    IReadOnlyList<DiagnosticContextBudgetShare> DroppedSources,
    DateTimeOffset CreatedAt);

public sealed record DiagnosticToolExecutionItem(
    string Id,
    string RunId,
    string? SessionId,
    string ToolId,
    string? ToolDisplayName,
    string Status,
    bool? ToolSuccess,
    string? ErrorCategory,
    string? Summary,
    string? CheckpointSummary,
    string? ArgumentsSummary,
    string? ResourcePath,
    string? Command,
    string? Cwd,
    string? Query,
    string? TaskId,
    string? AgentInstanceId,
    long DurationMs,
    long RequestedSeq,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> Evidence);

public sealed record DiagnosticSupervisionFinding(
    string Id,
    string? Category,
    string? Summary,
    string? Recommendation,
    DateTimeOffset CreatedAt);

public sealed record RunContextDiagnosticsBuildInput(
    string RunId,
    string? SessionId,
    string Status,
    Guid? ModeVersionId = null,
    IReadOnlyList<DiagnosticContextPackSnapshot>? ContextPacks = null,
    IReadOnlyList<ModelInvocationDto>? ModelInvocations = null,
    IReadOnlyList<DiagnosticToolExecutionItem>? ToolExecutions = null,
    IReadOnlyList<DiagnosticSupervisionFinding>? SupervisionFindings = null,
    IReadOnlyList<string>? LifecycleSignals = null,
    int? ContextWindowTokens = null,
    string? SelectedPackId = null);

/// <summary>
/// Authoritative P1 Run Context Diagnostics engine: 5% window occupancy &amp; Context Rot Detector,
/// ContextPack source classification &amp; compaction reconciliation, unified 9 governance signals,
/// error code frames, file diff line deltas, and executive findings.
/// </summary>
public interface IRunContextDiagnosticsService
{
    RunContextDiagnosticsDto BuildDiagnostics(RunContextDiagnosticsBuildInput input);

    Task<RunContextDiagnosticsDto?> DiagnoseRunAsync(
        Guid runId,
        Guid? tenantId = null,
        Guid? workspaceId = null,
        int? contextWindowTokens = null,
        string? selectedPackId = null,
        CancellationToken cancellationToken = default);
}
