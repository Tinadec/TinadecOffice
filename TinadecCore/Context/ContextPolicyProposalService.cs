using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Context;

internal static class ContextEvolutionModuleInitializer
{
#pragma warning disable CA2255 // Intentional cross-module factory hook so DmaEA and AspNetCore share scope singletons without direct module coupling.
    [ModuleInitializer]
    internal static void Initialize()
    {
        ContextPolicyProposalServiceRegistry.RegisterFactory(ContextPolicyProposalService.CreateForScope);
    }
#pragma warning restore CA2255
}

/// <summary>
/// Manages P2 context self-evolution proposals (multi-run degradation aggregation,
/// prompt/skill Winnowing deduplication proposals, bounded curator evidence formatting,
/// and human-approved application to <c>runtime.toml</c> via <see cref="RuntimeTomlCommentedWriter"/>).
/// </summary>
public sealed class ContextPolicyProposalService : IContextPolicyProposalService
{
    public const int DefaultAggregationWindowSize = 5;
    public const int DefaultDegradedRunThreshold = 3;
    public const int DefaultOverlapWasteTokenThreshold = 200;
    private const int MaxStoredObservations = 50;
    private const int MaxStoredProposals = 200;

    private static readonly JsonSerializerOptions LedgerJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> CriticalSourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "workspace_instructions",
        "project_instructions",
        "mode_pipeline",
        "reviewed_memory",
        "memory",
        "system_prompt",
        "project_memory"
    };

    private readonly ITenantContextAccessor? _tenant;
    private readonly IScopeStorageLocations? _locations;
    private readonly IScopeConfigurationDocuments? _scopeDocuments;
    private readonly IContextMemoryService? _contextMemory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ScopeEvolutionLedger> _memoryLedgers = new(StringComparer.Ordinal);

    public ContextPolicyProposalService(
        ITenantContextAccessor? tenant = null,
        IScopeStorageLocations? locations = null,
        IScopeConfigurationDocuments? scopeDocuments = null,
        IContextMemoryService? contextMemory = null)
    {
        _tenant = tenant;
        _locations = locations;
        _scopeDocuments = scopeDocuments;
        _contextMemory = contextMemory;
    }

    public static (object ScopeKey, IContextPolicyProposalService Service) CreateForScope(IServiceProvider services)
    {
        var locations = services.GetService(typeof(IScopeStorageLocations)) as IScopeStorageLocations;
        var lifecycle = services.GetService(typeof(ILifecycleManager));
        var contextProvider = services.GetService(typeof(IContextProvider));
        var tenant = services.GetService(typeof(ITenantContextAccessor)) as ITenantContextAccessor;
        var scopeDocs = services.GetService(typeof(IScopeConfigurationDocuments)) as IScopeConfigurationDocuments;
        var contextMemory = services.GetService(typeof(IContextMemoryService)) as IContextMemoryService;

        var key = (object?)locations ?? lifecycle ?? contextProvider ?? services;
        return (key, new ContextPolicyProposalService(tenant, locations, scopeDocs, contextMemory));
    }

    public static bool IsCriticalContextSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var trimmed = source.Trim();
        if (CriticalSourceNames.Contains(trimmed)) return true;
        return trimmed.Contains("workspace_instructions", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("mode_pipeline", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("reviewed_memory", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("project_instructions", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ContextEvolutionProposalListDto> ListProposalsAsync(
        ContextEvolutionProposalQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ContextEvolutionProposalQuery();
        string? normalizedStatus = null;
        if (!string.IsNullOrWhiteSpace(query.Status)
            && !string.Equals(query.Status.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!ContextEvolutionProposalStatuses.TryNormalize(query.Status, out var parsedStatus))
            {
                throw new ArgumentException($"Invalid proposal status '{query.Status}'. Expected: pending, applied, rejected.");
            }
            normalizedStatus = parsedStatus;
        }

        string? normalizedKind = null;
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            if (!ContextEvolutionProposalKinds.IsValid(query.Kind))
            {
                throw new ArgumentException($"Invalid proposal kind '{query.Kind}'. Expected: context_policy_proposal, prompt_dedup_proposal.");
            }
            normalizedKind = ContextEvolutionProposalKinds.Normalize(query.Kind);
        }

        string? normalizedScope = null;
        if (!string.IsNullOrWhiteSpace(query.TargetScope))
        {
            if (!ContextEvolutionTargetScopes.IsValid(query.TargetScope))
            {
                throw new ArgumentException($"Invalid target_scope '{query.TargetScope}'. Expected: global, mode, workspace.");
            }
            normalizedScope = ContextEvolutionTargetScopes.Normalize(query.TargetScope);
        }

        var limit = Math.Clamp(query.Limit <= 0 ? 50 : query.Limit, 1, 200);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<ContextEvolutionProposalDto> allProposals;
        try
        {
            var ledger = await LoadLedgerUnsafeAsync(cancellationToken).ConfigureAwait(false);
            allProposals = ledger.Proposals.ToList();
        }
        finally
        {
            _gate.Release();
        }

        var pendingCount = allProposals.Count(p => string.Equals(p.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase));

        IEnumerable<ContextEvolutionProposalDto> filtered = allProposals;
        if (normalizedStatus is not null)
        {
            filtered = filtered.Where(p => string.Equals(p.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase));
        }
        if (normalizedKind is not null)
        {
            filtered = filtered.Where(p => string.Equals(p.Kind, normalizedKind, StringComparison.OrdinalIgnoreCase));
        }
        if (normalizedScope is not null)
        {
            filtered = filtered.Where(p => string.Equals(p.TargetScope, normalizedScope, StringComparison.OrdinalIgnoreCase));
        }
        if (query.ProjectId is { } projectId && projectId != Guid.Empty)
        {
            filtered = filtered.Where(p => p.ProjectId == projectId || p.ProjectId is null);
        }
        if (query.ModeId is { } modeId && modeId != Guid.Empty)
        {
            filtered = filtered.Where(p => p.TargetModeId == modeId);
        }

        var matched = filtered
            .OrderByDescending(p => string.Equals(p.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.UpdatedAt)
            .ToList();

        var autoTune = await ReadCurrentAutoTuneSwitchAsync(cancellationToken).ConfigureAwait(false);

        return new ContextEvolutionProposalListDto
        {
            Items = matched.Take(limit).ToList(),
            TotalCount = matched.Count,
            PendingCount = pendingCount,
            AutoTuneLowRiskContextPolicy = autoTune
        };
    }

    public async Task<ContextEvolutionProposalDto?> GetProposalAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default)
    {
        if (proposalId == Guid.Empty) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LoadLedgerUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return ledger.Proposals.FirstOrDefault(p => p.Id == proposalId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextEvolutionProposalDto> UpsertProposalAsync(
        ContextEvolutionProposalDto proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.PolicyPatch is not null)
        {
            RuntimeTomlCommentedWriter.ValidatePatch(proposal.PolicyPatch);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LoadLedgerUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var existingIdx = ledger.Proposals.FindIndex(p => p.Id == proposal.Id);
            if (existingIdx >= 0)
            {
                ledger.Proposals[existingIdx] = proposal;
            }
            else
            {
                ledger.Proposals.Insert(0, proposal);
                TrimProposals(ledger);
            }
            await SaveLedgerUnsafeAsync(ledger, cancellationToken).ConfigureAwait(false);
            return proposal;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextEvolutionProposalDto>> RecordRunAndGenerateProposalsAsync(
        RunContextEvolutionObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.RunId))
        {
            throw new ArgumentException("Observation RunId is required.", nameof(observation));
        }

        var createdOrUpdated = new List<ContextEvolutionProposalDto>();
        Guid? autoApplyProposalId = null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LoadLedgerUnsafeAsync(cancellationToken).ConfigureAwait(false);

            // 1. Record / refresh observation (idempotent per RunId)
            var existingObsIndex = ledger.Observations.FindIndex(o =>
                string.Equals(o.RunId, observation.RunId, StringComparison.OrdinalIgnoreCase));
            if (existingObsIndex >= 0)
            {
                ledger.Observations[existingObsIndex] = observation;
            }
            else
            {
                ledger.Observations.Add(observation);
                if (ledger.Observations.Count > MaxStoredObservations)
                {
                    ledger.Observations.RemoveRange(0, ledger.Observations.Count - MaxStoredObservations);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var parsedRunGuid = Guid.TryParse(observation.RunId, out var rg) ? rg : (Guid?)null;

            // 2. Prompt / Skill / Instruction deduplication proposals (waste >= 200 tokens + concrete remediation_code)
            foreach (var pair in observation.OverlapPairs
                .Where(p => p.EstimatedWastedTokens >= DefaultOverlapWasteTokenThreshold && !string.IsNullOrWhiteSpace(p.RemediationCode))
                .OrderByDescending(p => p.EstimatedWastedTokens)
                .Take(5))
            {
                var dedupProposal = UpsertDedupProposalUnsafe(ledger, observation, pair, parsedRunGuid, now);
                createdOrUpdated.Add(dedupProposal);
            }

            // 3. Multi-run Context Policy tuning proposals (last 5 runs in same mode or global scope)
            var hasModeScope = !observation.ForceGlobalPolicyScope
                && (observation.ModeId.HasValue || !string.IsNullOrWhiteSpace(observation.ModeSlug));

            if (hasModeScope)
            {
                var modeWindow = SelectModeWindow(ledger.Observations, observation.ModeId, observation.ModeSlug, DefaultAggregationWindowSize);
                if (TryBuildPolicyProposalFromWindow(
                    modeWindow,
                    observation,
                    ContextEvolutionTargetScopes.Mode,
                    parsedRunGuid,
                    now,
                    out var modeProposal))
                {
                    var stored = UpsertPendingPolicyProposalUnsafe(ledger, modeProposal!);
                    createdOrUpdated.Add(stored);
                }

                // If cross-mode workspace runs are also systematically degrading (>= 4 of last 5 across >= 2 modes), surface a global proposal too
                var workspaceWindow = ledger.Observations.TakeLast(DefaultAggregationWindowSize).ToList();
                var distinctModes = workspaceWindow
                    .Select(w => w.ModeId?.ToString("N") ?? w.ModeSlug ?? "global")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (distinctModes >= 2 && workspaceWindow.Count(w => w.DegradationDetected) >= 4)
                {
                    if (TryBuildPolicyProposalFromWindow(
                        workspaceWindow,
                        observation,
                        ContextEvolutionTargetScopes.Global,
                        parsedRunGuid,
                        now,
                        out var globalCrossModeProposal))
                    {
                        var storedGlobal = UpsertPendingPolicyProposalUnsafe(ledger, globalCrossModeProposal!);
                        createdOrUpdated.Add(storedGlobal);
                    }
                }
            }
            else
            {
                var globalWindow = ledger.Observations
                    .Where(o => o.ForceGlobalPolicyScope || (o.ModeId is null && string.IsNullOrWhiteSpace(o.ModeSlug)))
                    .TakeLast(DefaultAggregationWindowSize)
                    .ToList();
                if (globalWindow.Count < DefaultDegradedRunThreshold)
                {
                    globalWindow = ledger.Observations.TakeLast(DefaultAggregationWindowSize).ToList();
                }

                if (TryBuildPolicyProposalFromWindow(
                    globalWindow,
                    observation,
                    ContextEvolutionTargetScopes.Global,
                    parsedRunGuid,
                    now,
                    out var globalProposal))
                {
                    var stored = UpsertPendingPolicyProposalUnsafe(ledger, globalProposal!);
                    createdOrUpdated.Add(stored);
                    if (IsLowRiskGlobalAutoTunable(stored))
                    {
                        autoApplyProposalId = stored.Id;
                    }
                }
            }

            TrimProposals(ledger);
            await SaveLedgerUnsafeAsync(ledger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        // Conservative default: auto_tune_low_risk_context_policy is false by default.
        // Only if the operator explicitly enabled auto_tune_low_risk_context_policy = true in runtime.toml
        // will a low-risk global parameter adjustment apply automatically (with full TOML audit comments).
        if (autoApplyProposalId is { } candidateAutoId
            && await ReadCurrentAutoTuneSwitchAsync(cancellationToken).ConfigureAwait(false))
        {
            var applied = await ApplyGlobalPolicyProposalAsync(
                candidateAutoId,
                new ApplyContextEvolutionProposalRequest
                {
                    Reason = "Auto-applied via explicit auto_tune_low_risk_context_policy = true switch."
                },
                evalGateStatus: null,
                cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < createdOrUpdated.Count; i++)
            {
                if (createdOrUpdated[i].Id == applied.Proposal.Id)
                {
                    createdOrUpdated[i] = applied.Proposal;
                }
            }
        }

        return createdOrUpdated;
    }

    public async Task<ContextEvolutionApplyResultDto> ApplyGlobalPolicyProposalAsync(
        Guid proposalId,
        ApplyContextEvolutionProposalRequest? request = null,
        string? evalGateStatus = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LoadLedgerUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = ledger.Proposals.FindIndex(p => p.Id == proposalId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Context evolution proposal '{proposalId}' was not found.");
            }

            var proposal = ledger.Proposals[index];
            if (!string.Equals(proposal.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Proposal '{proposalId}' has already been decided ({proposal.Status}).");
            }
            if (proposal.PolicyPatch is null)
            {
                throw new ArgumentException($"Proposal '{proposalId}' does not contain a context policy patch.");
            }

            RuntimeTomlCommentedWriter.ValidatePatch(proposal.PolicyPatch);
            var now = DateTimeOffset.UtcNow;

            string updatedToml;
            string? savedPath = null;
            string? savedHash = null;

            if (HasWritableScopeRuntimeDocument())
            {
                var currentDoc = await _scopeDocuments!.ReadAsync("runtime", cancellationToken).ConfigureAwait(false);
                var expectedHash = string.IsNullOrWhiteSpace(request?.ExpectedContentHash)
                    ? currentDoc.ContentHash
                    : request!.ExpectedContentHash!.Trim().Trim('"');
                var baseText = string.IsNullOrWhiteSpace(currentDoc.Text)
                    ? ReadBundledBaselineToml()
                    : currentDoc.Text;

                updatedToml = RuntimeTomlCommentedWriter.ApplyContextPolicyProposal(baseText, proposal, now);
                var saved = await _scopeDocuments.SaveIfMatchAsync("runtime", updatedToml, expectedHash, cancellationToken).ConfigureAwait(false);
                savedPath = saved.Path;
                savedHash = saved.ContentHash;
            }
            else if (_locations is not null)
            {
                Directory.CreateDirectory(_locations.Config);
                var runtimeTomlPath = Path.Combine(_locations.Config, "runtime.toml");
                var existingText = File.Exists(runtimeTomlPath)
                    ? await File.ReadAllTextAsync(runtimeTomlPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false)
                    : ReadBundledBaselineToml();

                if (!string.IsNullOrWhiteSpace(request?.ExpectedContentHash) && File.Exists(runtimeTomlPath))
                {
                    var actualHash = ComputeSha256Hex(existingText);
                    if (!string.Equals(actualHash, request!.ExpectedContentHash!.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("runtime.toml changed since it was read. Reload before applying.");
                    }
                }

                updatedToml = RuntimeTomlCommentedWriter.ApplyContextPolicyProposal(existingText, proposal, now);
                await WriteFileAtomicallyAsync(runtimeTomlPath, updatedToml, cancellationToken).ConfigureAwait(false);
                savedPath = runtimeTomlPath;
                savedHash = ComputeSha256Hex(updatedToml);

                await TrySyncInMemoryContextPolicyAsync(proposal.PolicyPatch, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var baseText = ReadBundledBaselineToml();
                updatedToml = RuntimeTomlCommentedWriter.ApplyContextPolicyProposal(baseText, proposal, now);
                savedHash = ComputeSha256Hex(updatedToml);
                await TrySyncInMemoryContextPolicyAsync(proposal.PolicyPatch, cancellationToken).ConfigureAwait(false);
            }

            var updatedProposal = CloneProposalWithStatus(
                proposal,
                ContextEvolutionProposalStatuses.Applied,
                now,
                new ContextEvolutionAppliedReceipt(
                    Reason: request?.Reason,
                    AppliedTomlPath: savedPath,
                    AppliedTomlContentHash: savedHash,
                    UpdatedTomlPreview: BuildTomlExcerpt(updatedToml, proposal.Id),
                    EvalRunId: request?.EvalRunId ?? proposal.EvalRunId,
                    EvalGateStatus: evalGateStatus ?? proposal.EvalGateStatus));

            ledger.Proposals[index] = updatedProposal;
            await SaveLedgerUnsafeAsync(ledger, cancellationToken).ConfigureAwait(false);

            return new ContextEvolutionApplyResultDto
            {
                Proposal = updatedProposal,
                Applied = true,
                Summary = $"Applied global context policy proposal '{updatedProposal.Title}' to runtime.toml with audit comments.",
                UpdatedTomlPreview = BuildTomlExcerpt(updatedToml, proposal.Id),
                EvalRunId = updatedProposal.EvalRunId
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextEvolutionProposalDto> MarkAppliedAsync(
        Guid proposalId,
        ContextEvolutionAppliedReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LoadLedgerUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = ledger.Proposals.FindIndex(p => p.Id == proposalId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Context evolution proposal '{proposalId}' was not found.");
            }
            var current = ledger.Proposals[index];
            if (!string.Equals(current.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Proposal '{proposalId}' has already been decided ({current.Status}).");
            }

            var updated = CloneProposalWithStatus(current, ContextEvolutionProposalStatuses.Applied, DateTimeOffset.UtcNow, receipt);
            ledger.Proposals[index] = updated;
            await SaveLedgerUnsafeAsync(ledger, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextEvolutionProposalDto> RejectProposalAsync(
        Guid proposalId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LoadLedgerUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = ledger.Proposals.FindIndex(p => p.Id == proposalId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Context evolution proposal '{proposalId}' was not found.");
            }
            var current = ledger.Proposals[index];
            if (!string.Equals(current.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Proposal '{proposalId}' has already been decided ({current.Status}).");
            }

            var now = DateTimeOffset.UtcNow;
            var rejected = CloneProposalWithStatus(
                current,
                ContextEvolutionProposalStatuses.Rejected,
                now,
                new ContextEvolutionAppliedReceipt(Reason: string.IsNullOrWhiteSpace(reason) ? "Rejected by human reviewer." : reason.Trim()));

            ledger.Proposals[index] = rejected;
            await SaveLedgerUnsafeAsync(ledger, cancellationToken).ConfigureAwait(false);
            return rejected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string BuildBoundedCuratorEvidence(RunContextEvolutionObservation observation, int maxChars = 2200)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var cap = Math.Clamp(maxChars, 400, 6000);
        var sb = new StringBuilder(Math.Min(cap, 2048));

        sb.Append(CultureInfo.InvariantCulture,
            $"- Peak window occupancy: {observation.PeakOccupancyPct:0.0}% | High-occupancy degradation: {(observation.DegradationDetected ? "YES" : "no")}");
        if (observation.DegradationReasons.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", observation.DegradationReasons.Take(4))).Append(')');
        }
        sb.Append('\n');

        sb.Append(CultureInfo.InvariantCulture,
            $"- Occupancy error/latency split: <70% err={observation.LowOccupancyErrorRate * 100.0:0.0}% ({observation.LowOccupancyAvgLatencyMs:0}ms) vs >=70% err={observation.HighOccupancyErrorRate * 100.0:0.0}% ({observation.HighOccupancyAvgLatencyMs:0}ms)\n");

        var droppedHighValue = observation.DroppedSources.Where(IsCriticalContextSource).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var otherDropped = observation.DroppedSources.Where(s => !IsCriticalContextSource(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (droppedHighValue.Count > 0 || observation.DroppedTokens > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- Budget-dropped sources ({observation.DroppedTokens} tokens): critical=[{(droppedHighValue.Count > 0 ? string.Join(", ", droppedHighValue) : "none")}]");
            if (otherDropped.Count > 0)
            {
                sb.Append(", other=[").Append(string.Join(", ", otherDropped.Take(5))).Append(']');
            }
            sb.Append('\n');
        }
        else
        {
            sb.Append("- Budget-dropped sources: none\n");
        }

        if (observation.RedlineCounts.Count > 0 || observation.MaxSameArgsRetryStreak > 0)
        {
            var signals = observation.RedlineCounts
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .Take(6)
                .Select(kv => $"{kv.Key}={kv.Value}");
            sb.Append("- Governance redline signals: ")
                .Append(string.Join(", ", signals));
            if (observation.MaxSameArgsRetryStreak > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" (max SAME_ARGS_RETRY streak={observation.MaxSameArgsRetryStreak})");
            }
            sb.Append('\n');
        }
        else
        {
            sb.Append("- Governance redline signals: none\n");
        }

        var topOverlaps = observation.OverlapPairs
            .OrderByDescending(p => p.EstimatedWastedTokens)
            .Take(3)
            .ToList();
        if (topOverlaps.Count > 0)
        {
            sb.Append("- Top cross-source prompt/skill overlaps:\n");
            foreach (var pair in topOverlaps)
            {
                var lineRange = FormatLineRangeLabel(pair.SourceAStartLine, pair.SourceAEndLine, pair.SourceBStartLine, pair.SourceBEndLine);
                var snippet = BoundSingleLine(pair.Excerpt, 120);
                sb.Append(CultureInfo.InvariantCulture,
                    $"  * {pair.SourceALabel} <-> {pair.SourceBLabel} [{lineRange}] ~{pair.EstimatedWastedTokens} wasted tokens (remediation={pair.RemediationCode}): \"{snippet}\"\n");
            }
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length <= cap) return text;
        const string suffix = "\n... [context evidence truncated]";
        return text[..Math.Max(0, cap - suffix.Length)].TrimEnd() + suffix;
    }

    private static List<RunContextEvolutionObservation> SelectModeWindow(
        List<RunContextEvolutionObservation> observations,
        Guid? modeId,
        string? modeSlug,
        int windowSize)
    {
        return observations
            .Where(o =>
                (modeId.HasValue && o.ModeId == modeId)
                || (!modeId.HasValue && !string.IsNullOrWhiteSpace(modeSlug) && string.Equals(o.ModeSlug, modeSlug, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(windowSize)
            .ToList();
    }

    private static bool TryBuildPolicyProposalFromWindow(
        IReadOnlyList<RunContextEvolutionObservation> window,
        RunContextEvolutionObservation latest,
        string targetScope,
        Guid? sourceRunGuid,
        DateTimeOffset now,
        out ContextEvolutionProposalDto? proposal)
    {
        proposal = null;
        if (window.Count < DefaultDegradedRunThreshold) return false;

        var degradedRuns = window.Where(r => r.DegradationDetected).ToList();
        var criticalDropRuns = window.Where(r => r.DroppedSources.Any(IsCriticalContextSource)).ToList();

        if (degradedRuns.Count < DefaultDegradedRunThreshold && criticalDropRuns.Count < DefaultDegradedRunThreshold)
        {
            return false;
        }

        var evidenceRuns = window
            .Where(r => r.DegradationDetected || r.DroppedSources.Any(IsCriticalContextSource))
            .Select(r => r.RunId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var curStrategy = string.IsNullOrWhiteSpace(latest.EffectiveStrategy) ? "managed" : latest.EffectiveStrategy.Trim().ToLowerInvariant();
        var curRatio = latest.EffectiveCompactionTriggerRatio is >= 0.40 and <= 0.95 ? latest.EffectiveCompactionTriggerRatio : 0.75;
        var curLimit = latest.EffectiveRecentMessageLimit is >= 4 and <= 200 ? latest.EffectiveRecentMessageLimit : 128;
        var curFidelity = string.IsNullOrWhiteSpace(latest.EffectiveRecallFidelity) ? "balanced" : latest.EffectiveRecallFidelity.Trim().ToLowerInvariant();

        string? nextStrategy = null;
        double? nextRatio = null;
        int? nextLimit = null;
        string? nextFidelity = null;

        if (curStrategy == "direct")
        {
            nextStrategy = "managed";
            nextRatio = Math.Min(curRatio, 0.65);
        }
        else if (degradedRuns.Count >= DefaultDegradedRunThreshold)
        {
            if (curRatio > 0.45)
            {
                nextRatio = Math.Round(Math.Clamp(curRatio - 0.10, 0.45, 0.85), 2);
            }
            if (curLimit > 48)
            {
                nextLimit = Math.Max(16, (int)(Math.Round(curLimit * 0.75 / 4.0) * 4));
            }
            if (nextRatio is null && nextLimit is null && curFidelity == "verbatim_first")
            {
                nextFidelity = "balanced";
            }
        }
        else
        {
            // Repeated critical dropped sources (workspace_instructions / mode_pipeline / reviewed_memory)
            if (curRatio > 0.45)
            {
                nextRatio = Math.Round(Math.Clamp(curRatio - 0.10, 0.45, 0.85), 2);
            }
            if (curLimit > 24)
            {
                nextLimit = Math.Max(16, (int)(Math.Round(curLimit * 0.65 / 4.0) * 4));
            }
        }

        if (nextStrategy is null && nextRatio is null && nextLimit is null && nextFidelity is null)
        {
            return false;
        }

        var beforeValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var afterValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var changeParts = new List<string>();

        if (nextStrategy is not null)
        {
            beforeValues["context_strategy"] = curStrategy;
            afterValues["context_strategy"] = nextStrategy;
            changeParts.Add($"strategy {curStrategy} -> {nextStrategy}");
        }
        if (nextRatio is { } nr)
        {
            beforeValues["compaction_trigger_ratio"] = curRatio.ToString("0.##", CultureInfo.InvariantCulture);
            afterValues["compaction_trigger_ratio"] = nr.ToString("0.##", CultureInfo.InvariantCulture);
            changeParts.Add(string.Create(CultureInfo.InvariantCulture, $"compaction_trigger_ratio {curRatio:0.##} -> {nr:0.##}"));
        }
        if (nextLimit is { } nl)
        {
            beforeValues["recent_message_limit"] = curLimit.ToString(CultureInfo.InvariantCulture);
            afterValues["recent_message_limit"] = nl.ToString(CultureInfo.InvariantCulture);
            changeParts.Add(string.Create(CultureInfo.InvariantCulture, $"recent_message_limit {curLimit} -> {nl}"));
        }
        if (nextFidelity is not null)
        {
            beforeValues["recall_fidelity"] = curFidelity;
            afterValues["recall_fidelity"] = nextFidelity;
            changeParts.Add($"recall_fidelity {curFidelity} -> {nextFidelity}");
        }

        var avgPeak = window.Average(w => w.PeakOccupancyPct);
        var avgHighErr = window.Average(w => w.HighOccupancyErrorRate);
        var avgLowErr = window.Average(w => w.LowOccupancyErrorRate);
        var avgHighLat = window.Average(w => w.HighOccupancyAvgLatencyMs);
        var avgLowLat = window.Average(w => w.LowOccupancyAvgLatencyMs);
        var droppedSources = window
            .SelectMany(w => w.DroppedSources)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IsCriticalContextSource)
            .Take(8)
            .ToList();

        var degradationRate = Math.Round((double)degradedRuns.Count / window.Count, 4);
        var scopeLabel = targetScope == ContextEvolutionTargetScopes.Mode
            ? $"mode '{latest.ModeName ?? latest.ModeSlug ?? latest.ModeId?.ToString("D") ?? "selected"}'"
            : "global runtime.toml";

        var triggerReason = degradedRuns.Count >= DefaultDegradedRunThreshold
            ? string.Create(CultureInfo.InvariantCulture, $"{degradedRuns.Count}/{window.Count} recent runs exhibited high-occupancy degradation (avg peak {avgPeak:0.0}%)")
            : string.Create(CultureInfo.InvariantCulture, $"{criticalDropRuns.Count}/{window.Count} recent runs dropped critical context sources ({string.Join(", ", droppedSources.Where(IsCriticalContextSource))})");

        var expectedBenefit = degradedRuns.Count >= DefaultDegradedRunThreshold
            ? "Triggers background structural compaction before the >=70% context-rot zone, lowering tool retry streaks and tail latency while keeping hot-window cache hits."
            : "Frees cold conversation-tail tokens earlier so workspace_instructions, mode_pipeline, and reviewed_memory stay retained inside the token budget.";

        var proposalId = Guid.NewGuid();
        var title = targetScope == ContextEvolutionTargetScopes.Mode
            ? $"Tune {latest.ModeName ?? latest.ModeSlug ?? "mode"} context policy ({string.Join(", ", changeParts)})"
            : $"Tune global context policy ({string.Join(", ", changeParts)})";

        proposal = new ContextEvolutionProposalDto
        {
            Id = proposalId,
            Kind = ContextEvolutionProposalKinds.ContextPolicyProposal,
            Status = ContextEvolutionProposalStatuses.Pending,
            TargetScope = targetScope,
            Title = title,
            Summary = $"{triggerReason}; proposes {string.Join(", ", changeParts)} on {scopeLabel}.",
            Rationale = $"{triggerReason}. Evidence runs: [{string.Join(", ", evidenceRuns)}].",
            Confidence = Math.Clamp(0.70 + 0.05 * Math.Max(degradedRuns.Count, criticalDropRuns.Count), 0.70, 0.95),
            RiskLevel = nextStrategy is not null ? "medium" : "low",
            RequiresHumanApproval = true,
            SourceRunId = sourceRunGuid,
            EvidenceRunIds = evidenceRuns,
            ProjectId = latest.ProjectId,
            TargetModeId = targetScope == ContextEvolutionTargetScopes.Mode ? latest.ModeId : null,
            TargetModeSlug = targetScope == ContextEvolutionTargetScopes.Mode ? latest.ModeSlug : null,
            TargetModeName = targetScope == ContextEvolutionTargetScopes.Mode ? latest.ModeName : null,
            PolicyPatch = new ContextPolicyPatchDto
            {
                ContextStrategy = nextStrategy,
                CompactionTriggerRatio = nextRatio,
                RecentMessageLimit = nextLimit,
                RecallFidelity = nextFidelity
            },
            MetricsComparison = new ContextEvolutionMetricsComparisonDto
            {
                WindowRunCount = window.Count,
                DegradedRunCount = degradedRuns.Count,
                DegradationRate = degradationRate,
                DroppedCriticalRunCount = criticalDropRuns.Count,
                AvgPeakOccupancyPct = Math.Round(avgPeak, 2),
                HighOccupancyErrorRate = Math.Round(avgHighErr, 4),
                LowOccupancyErrorRate = Math.Round(avgLowErr, 4),
                HighOccupancyAvgLatencyMs = Math.Round(avgHighLat, 1),
                LowOccupancyAvgLatencyMs = Math.Round(avgLowLat, 1),
                DroppedSources = droppedSources,
                BeforeValues = beforeValues,
                AfterValues = afterValues,
                ExpectedBenefit = expectedBenefit
            },
            ComposerPrompt = $"Inspect context policy proposal {proposalId:D} ({string.Join(", ", changeParts)}) backed by runs [{string.Join(", ", evidenceRuns)}] using tinadec_inspect run_diagnostics and explain trade-offs before applying.",
            CreatedAt = now,
            UpdatedAt = now
        };
        return true;
    }

    private static ContextEvolutionProposalDto UpsertPendingPolicyProposalUnsafe(
        ScopeEvolutionLedger ledger,
        ContextEvolutionProposalDto candidate)
    {
        var existingIndex = ledger.Proposals.FindIndex(p =>
            string.Equals(p.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Kind, ContextEvolutionProposalKinds.ContextPolicyProposal, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.TargetScope, candidate.TargetScope, StringComparison.OrdinalIgnoreCase)
            && p.TargetModeId == candidate.TargetModeId
            && string.Equals(p.TargetModeSlug ?? string.Empty, candidate.TargetModeSlug ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        if (existingIndex < 0)
        {
            ledger.Proposals.Insert(0, candidate);
            return candidate;
        }

        var existing = ledger.Proposals[existingIndex];
        var mergedRuns = existing.EvidenceRunIds
            .Concat(candidate.EvidenceRunIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(10)
            .ToList();

        var refreshed = new ContextEvolutionProposalDto
        {
            Id = existing.Id,
            Kind = existing.Kind,
            Status = ContextEvolutionProposalStatuses.Pending,
            TargetScope = existing.TargetScope,
            Title = candidate.Title,
            Summary = candidate.Summary,
            Rationale = candidate.Rationale,
            Confidence = Math.Max(existing.Confidence, candidate.Confidence),
            RiskLevel = candidate.RiskLevel,
            RequiresHumanApproval = true,
            SourceRunId = candidate.SourceRunId ?? existing.SourceRunId,
            EvidenceRunIds = mergedRuns,
            ProjectId = candidate.ProjectId ?? existing.ProjectId,
            TargetModeId = existing.TargetModeId,
            TargetModeSlug = existing.TargetModeSlug ?? candidate.TargetModeSlug,
            TargetModeName = candidate.TargetModeName ?? existing.TargetModeName,
            PolicyPatch = candidate.PolicyPatch,
            MetricsComparison = candidate.MetricsComparison,
            LinkedAgentCandidateId = candidate.LinkedAgentCandidateId ?? existing.LinkedAgentCandidateId,
            ComposerPrompt = $"Inspect context policy proposal {existing.Id:D} backed by runs [{string.Join(", ", mergedRuns)}] using tinadec_inspect run_diagnostics and explain trade-offs before applying.",
            CreatedAt = existing.CreatedAt,
            UpdatedAt = candidate.UpdatedAt
        };
        ledger.Proposals[existingIndex] = refreshed;
        return refreshed;
    }

    private static ContextEvolutionProposalDto UpsertDedupProposalUnsafe(
        ScopeEvolutionLedger ledger,
        RunContextEvolutionObservation observation,
        ContextOverlapPairEvidenceInput pair,
        Guid? sourceRunGuid,
        DateTimeOffset now)
    {
        var lineLabel = FormatLineRangeLabel(pair.SourceAStartLine, pair.SourceAEndLine, pair.SourceBStartLine, pair.SourceBEndLine);
        var remediationCode = pair.RemediationCode.Trim();
        var suggested = !string.IsNullOrWhiteSpace(pair.SuggestedRemediation)
            ? pair.SuggestedRemediation!.Trim()
            : DescribeRemediation(remediationCode, pair.SourceALabel, pair.SourceBLabel, lineLabel);

        var existingIndex = ledger.Proposals.FindIndex(p =>
            string.Equals(p.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Kind, ContextEvolutionProposalKinds.PromptDedupProposal, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.RemediationCode, remediationCode, StringComparison.OrdinalIgnoreCase)
            && p.DedupEvidence is not null
            && PairMatches(p.DedupEvidence, pair));

        var evidenceDto = new ContextDedupProposalEvidenceDto
        {
            SourceAId = pair.SourceAId,
            SourceALabel = pair.SourceALabel,
            SourceAKind = pair.SourceAKind,
            SourceAPath = pair.SourceAPath,
            SourceAStartLine = Math.Max(1, pair.SourceAStartLine),
            SourceAEndLine = Math.Max(Math.Max(1, pair.SourceAStartLine), pair.SourceAEndLine),
            SourceBId = pair.SourceBId,
            SourceBLabel = pair.SourceBLabel,
            SourceBKind = pair.SourceBKind,
            SourceBPath = pair.SourceBPath,
            SourceBStartLine = Math.Max(1, pair.SourceBStartLine),
            SourceBEndLine = Math.Max(Math.Max(1, pair.SourceBStartLine), pair.SourceBEndLine),
            LineRangeLabel = lineLabel,
            OverlapChars = pair.OverlapChars,
            EstimatedWastedTokens = pair.EstimatedWastedTokens,
            RemediationCode = remediationCode,
            Excerpt = BoundText(pair.Excerpt, 360),
            SuggestedRemediation = suggested
        };

        if (existingIndex >= 0)
        {
            var existing = ledger.Proposals[existingIndex];
            var mergedRuns = existing.EvidenceRunIds
                .Append(observation.RunId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .TakeLast(10)
                .ToList();

            var updated = new ContextEvolutionProposalDto
            {
                Id = existing.Id,
                Kind = ContextEvolutionProposalKinds.PromptDedupProposal,
                Status = ContextEvolutionProposalStatuses.Pending,
                TargetScope = existing.TargetScope,
                Title = $"Deduplicate {pair.SourceALabel} <-> {pair.SourceBLabel} ({lineLabel})",
                Summary = $"Saves ~{pair.EstimatedWastedTokens} tokens/turn via {remediationCode} across {lineLabel}.",
                Rationale = suggested,
                Confidence = Math.Clamp(0.75 + Math.Min(0.20, pair.EstimatedWastedTokens / 2500.0), 0.75, 0.95),
                RiskLevel = "medium",
                RequiresHumanApproval = true,
                SourceRunId = sourceRunGuid ?? existing.SourceRunId,
                EvidenceRunIds = mergedRuns,
                ProjectId = observation.ProjectId ?? existing.ProjectId,
                TargetModeId = observation.ModeId ?? existing.TargetModeId,
                TargetModeSlug = observation.ModeSlug ?? existing.TargetModeSlug,
                TargetModeName = observation.ModeName ?? existing.TargetModeName,
                RemediationCode = remediationCode,
                DedupEvidence = evidenceDto,
                LinkedAgentCandidateId = observation.LinkedAgentCandidateId ?? existing.LinkedAgentCandidateId,
                ComposerPrompt = $"Audit duplicated prompt/skill block ({remediationCode}) between {pair.SourceALabel} and {pair.SourceBLabel} at {lineLabel} (~{pair.EstimatedWastedTokens} wasted tokens) using tinadec_inspect context_audit.",
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now
            };
            ledger.Proposals[existingIndex] = updated;
            return updated;
        }

        var proposalId = Guid.NewGuid();
        var created = new ContextEvolutionProposalDto
        {
            Id = proposalId,
            Kind = ContextEvolutionProposalKinds.PromptDedupProposal,
            Status = ContextEvolutionProposalStatuses.Pending,
            TargetScope = observation.ModeId.HasValue ? ContextEvolutionTargetScopes.Mode : ContextEvolutionTargetScopes.Workspace,
            Title = $"Deduplicate {pair.SourceALabel} <-> {pair.SourceBLabel} ({lineLabel})",
            Summary = $"Saves ~{pair.EstimatedWastedTokens} tokens/turn via {remediationCode} across {lineLabel}.",
            Rationale = suggested,
            Confidence = Math.Clamp(0.75 + Math.Min(0.20, pair.EstimatedWastedTokens / 2500.0), 0.75, 0.95),
            RiskLevel = "medium",
            RequiresHumanApproval = true,
            SourceRunId = sourceRunGuid,
            EvidenceRunIds = [observation.RunId],
            ProjectId = observation.ProjectId,
            TargetModeId = observation.ModeId,
            TargetModeSlug = observation.ModeSlug,
            TargetModeName = observation.ModeName,
            RemediationCode = remediationCode,
            DedupEvidence = evidenceDto,
            LinkedAgentCandidateId = observation.LinkedAgentCandidateId,
            ComposerPrompt = $"Audit duplicated prompt/skill block ({remediationCode}) between {pair.SourceALabel} and {pair.SourceBLabel} at {lineLabel} (~{pair.EstimatedWastedTokens} wasted tokens) using tinadec_inspect context_audit.",
            CreatedAt = now,
            UpdatedAt = now
        };
        ledger.Proposals.Insert(0, created);
        return created;
    }

    private static bool PairMatches(ContextDedupProposalEvidenceDto existing, ContextOverlapPairEvidenceInput candidate) =>
        (string.Equals(existing.SourceAId, candidate.SourceAId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.SourceBId, candidate.SourceBId, StringComparison.OrdinalIgnoreCase))
        || (string.Equals(existing.SourceAId, candidate.SourceBId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.SourceBId, candidate.SourceAId, StringComparison.OrdinalIgnoreCase));

    private static string FormatLineRangeLabel(int startA, int endA, int startB, int endB)
    {
        var a1 = Math.Max(1, startA);
        var a2 = Math.Max(a1, endA);
        var b1 = Math.Max(1, startB);
        var b2 = Math.Max(b1, endB);
        return string.Create(CultureInfo.InvariantCulture, $"L{a1}-L{a2} <-> L{b1}-L{b2}");
    }

    private static string DescribeRemediation(string remediationCode, string sourceA, string sourceB, string lineRange) =>
        remediationCode switch
        {
            "move_to_skill_references" => $"Move duplicated reference prose between '{sourceA}' and '{sourceB}' ({lineRange}) into on-demand skill references loaded via load_skill.",
            "dedupe_workspace_instructions" => $"Remove repeated repository rules between '{sourceA}' and '{sourceB}' ({lineRange}) so AGENTS.md / .apm instructions remain single-sourced.",
            "dedupe_tool_schema_prose" => $"Strip tool parameter prose from '{sourceA}' / '{sourceB}' ({lineRange}) that already ships in the tool schema manifest.",
            "consolidate_shared_skill" => $"Consolidate overlapping skill instructions between '{sourceA}' and '{sourceB}' ({lineRange}) into a single shared skill section.",
            "extract_shared_fragment" => $"Extract duplicated prompt instructions between '{sourceA}' and '{sourceB}' ({lineRange}) into one shared Prompt Fragment.",
            _ => $"Deduplicate overlapping instructions between '{sourceA}' and '{sourceB}' ({lineRange}) via {remediationCode}."
        };

    private static bool IsLowRiskGlobalAutoTunable(ContextEvolutionProposalDto proposal) =>
        string.Equals(proposal.Kind, ContextEvolutionProposalKinds.ContextPolicyProposal, StringComparison.OrdinalIgnoreCase)
        && string.Equals(proposal.TargetScope, ContextEvolutionTargetScopes.Global, StringComparison.OrdinalIgnoreCase)
        && string.Equals(proposal.RiskLevel, "low", StringComparison.OrdinalIgnoreCase)
        && proposal.PolicyPatch is { ContextStrategy: null };

    private static ContextEvolutionProposalDto CloneProposalWithStatus(
        ContextEvolutionProposalDto source,
        string status,
        DateTimeOffset now,
        ContextEvolutionAppliedReceipt receipt) => new()
        {
            Id = source.Id,
            Kind = source.Kind,
            Status = status,
            TargetScope = source.TargetScope,
            Title = source.Title,
            Summary = source.Summary,
            Rationale = source.Rationale,
            Confidence = source.Confidence,
            RiskLevel = source.RiskLevel,
            RequiresHumanApproval = source.RequiresHumanApproval,
            SourceRunId = source.SourceRunId,
            EvidenceRunIds = source.EvidenceRunIds,
            ProjectId = source.ProjectId,
            TargetModeId = source.TargetModeId,
            TargetModeSlug = source.TargetModeSlug,
            TargetModeName = source.TargetModeName,
            RemediationCode = source.RemediationCode,
            PolicyPatch = source.PolicyPatch,
            DedupEvidence = source.DedupEvidence,
            MetricsComparison = source.MetricsComparison,
            LinkedAgentCandidateId = source.LinkedAgentCandidateId,
            EvalRunId = receipt.EvalRunId ?? source.EvalRunId,
            EvalGateStatus = receipt.EvalGateStatus ?? source.EvalGateStatus,
            DecisionReason = receipt.Reason ?? source.DecisionReason,
            AppliedTomlPath = receipt.AppliedTomlPath ?? source.AppliedTomlPath,
            AppliedTomlContentHash = receipt.AppliedTomlContentHash ?? source.AppliedTomlContentHash,
            AppliedModeId = receipt.AppliedModeId ?? source.AppliedModeId,
            AppliedModeVersion = receipt.AppliedModeVersion ?? source.AppliedModeVersion,
            PromotedAgentId = receipt.PromotedAgentId ?? source.PromotedAgentId,
            ComposerPrompt = source.ComposerPrompt,
            CreatedAt = source.CreatedAt,
            UpdatedAt = now,
            DecidedAt = now
        };

    private async Task<bool> ReadCurrentAutoTuneSwitchAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (HasWritableScopeRuntimeDocument())
            {
                var doc = await _scopeDocuments!.ReadAsync("runtime", cancellationToken).ConfigureAwait(false);
                return RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch(doc.Text);
            }
            if (_locations is not null)
            {
                var path = Path.Combine(_locations.Config, "runtime.toml");
                if (File.Exists(path))
                {
                    var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    return RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch(text);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return false;
    }

    private async Task TrySyncInMemoryContextPolicyAsync(ContextPolicyPatchDto patch, CancellationToken cancellationToken)
    {
        if (_contextMemory is null) return;
        try
        {
            await _contextMemory.UpdatePolicyAsync(new UpdateContextMemoryPolicyRequest
            {
                Strategy = patch.ContextStrategy,
                CompactionTriggerRatio = patch.CompactionTriggerRatio,
                RecentMessageLimit = patch.RecentMessageLimit,
                RecallFidelity = patch.RecallFidelity,
                AllowCrossSessionRecall = patch.AllowCrossSessionRecall,
                EvolutionMode = patch.EvolutionMode,
                CoreMarkdownMaxTokens = patch.CoreMarkdownMaxTokens
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private bool HasWritableScopeRuntimeDocument() =>
        _scopeDocuments is not null && _scopeDocuments.DocumentIds.Contains("runtime", StringComparer.Ordinal);

    private (Guid TenantId, Guid WorkspaceId, string ScopeKey) ResolveScopeIdentity()
    {
        try
        {
            if (_tenant is not null)
            {
                var current = _tenant.Current;
                return (current.TenantId, current.WorkspaceId, $"{current.TenantId:N}_{current.WorkspaceId:N}");
            }
        }
        catch
        {
        }
        return (Guid.Empty, Guid.Empty, "default_default");
    }

    private string? ResolveLedgerFilePath(string scopeKey)
    {
        if (_locations is null || string.IsNullOrWhiteSpace(_locations.State)) return null;
        return Path.Combine(_locations.State, "context-evolution", $"{scopeKey}.json");
    }

    private async Task<ScopeEvolutionLedger> LoadLedgerUnsafeAsync(CancellationToken cancellationToken)
    {
        var (_, _, scopeKey) = ResolveScopeIdentity();
        var filePath = ResolveLedgerFilePath(scopeKey);
        if (filePath is not null && File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<ScopeEvolutionLedger>(json, LedgerJson);
                if (loaded is not null)
                {
                    _memoryLedgers[scopeKey] = loaded;
                    return loaded;
                }
            }
            catch (JsonException)
            {
            }
        }
        return _memoryLedgers.GetOrAdd(scopeKey, _ => new ScopeEvolutionLedger());
    }

    private async Task SaveLedgerUnsafeAsync(ScopeEvolutionLedger ledger, CancellationToken cancellationToken)
    {
        var (_, _, scopeKey) = ResolveScopeIdentity();
        _memoryLedgers[scopeKey] = ledger;
        var filePath = ResolveLedgerFilePath(scopeKey);
        if (filePath is null) return;

        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(ledger, LedgerJson);
        await WriteFileAtomicallyAsync(filePath, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFileAtomicallyAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void TrimProposals(ScopeEvolutionLedger ledger)
    {
        if (ledger.Proposals.Count <= MaxStoredProposals) return;
        ledger.Proposals = ledger.Proposals
            .OrderByDescending(p => string.Equals(p.Status, ContextEvolutionProposalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.UpdatedAt)
            .Take(MaxStoredProposals)
            .ToList();
    }

    private static string BuildTomlExcerpt(string tomlText, Guid proposalId)
    {
        var marker = proposalId.ToString("D");
        var lines = tomlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var matchedIndices = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                matchedIndices.Add(i);
            }
        }
        if (matchedIndices.Count == 0) return BoundText(tomlText, 800);
        var start = Math.Max(0, matchedIndices[0] - 1);
        var end = Math.Min(lines.Length - 1, matchedIndices[^1] + 4);
        return string.Join('\n', lines[start..(end + 1)]);
    }

    private static string ReadBundledBaselineToml()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Configuration", "default-agent-runtime.toml"),
            Path.Combine(AppContext.BaseDirectory, "DmaEA", "Configuration", "default-agent-runtime.toml")
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
        }
        return string.Empty;
    }

    private static string ComputeSha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string BoundSingleLine(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var flat = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return flat.Length <= maxChars ? flat : flat[..maxChars] + "...";
    }

    private static string BoundText(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "...";
    }

    private sealed class ScopeEvolutionLedger
    {
        public List<RunContextEvolutionObservation> Observations { get; set; } = [];
        public List<ContextEvolutionProposalDto> Proposals { get; set; } = [];
    }
}
