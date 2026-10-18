using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Allow-list for host-owned mode workflows. Packs may choose a known policy and
/// its declared features, but may never ship executable behavior of their own.
/// </summary>
public static class CorrectionRuntimePolicy
{
    public const string StructuredInvestigationKind = "structured_investigation";
    public const int StructuredInvestigationVersion = 1;
    public const string RequiredCoreCapability = "structured_investigation_v1";

    public const string CausalTraceFeature = "causal_trace";
    public const string CompletionContractFeature = "completion_contract";
    public const string FailingReproRequiredFeature = "failing_repro_required";
    public const string ClarificationLedgerFeature = "clarification_ledger";
    public const string UserReproductionReopensFeature = "user_reproduction_reopens";
    public const string LocalHunkRevertFeature = "local_hunk_revert";

    public static readonly IReadOnlySet<string> StructuredInvestigationFeatures =
        new HashSet<string>(StringComparer.Ordinal)
        {
            CausalTraceFeature,
            CompletionContractFeature,
            FailingReproRequiredFeature,
            ClarificationLedgerFeature,
            UserReproductionReopensFeature,
            LocalHunkRevertFeature,
        };

    public static string? Validate(CorrectionRuntimePolicyDto? policy)
    {
        if (policy is null) return null;
        if (!string.Equals(policy.Kind, StructuredInvestigationKind, StringComparison.Ordinal))
            return $"runtime_policy.kind '{policy.Kind}' is not supported.";
        if (policy.Version != StructuredInvestigationVersion)
            return $"runtime_policy.version {policy.Version} is not supported.";

        var features = policy.Features.Distinct(StringComparer.Ordinal).ToArray();
        var unknown = features.Where(feature => !StructuredInvestigationFeatures.Contains(feature)).ToArray();
        if (unknown.Length > 0) return $"runtime_policy.features contains unsupported values: {string.Join(", ", unknown)}.";
        if (!features.Contains(CausalTraceFeature, StringComparer.Ordinal))
            return $"runtime_policy.features must include '{CausalTraceFeature}'.";
        if (features.Contains(FailingReproRequiredFeature, StringComparer.Ordinal)
            && !features.Contains(CompletionContractFeature, StringComparer.Ordinal))
            return $"'{FailingReproRequiredFeature}' requires '{CompletionContractFeature}'.";
        if (features.Contains(UserReproductionReopensFeature, StringComparer.Ordinal)
            && !features.Contains(CompletionContractFeature, StringComparer.Ordinal))
            return $"'{UserReproductionReopensFeature}' requires '{CompletionContractFeature}'.";
        if (features.Contains(LocalHunkRevertFeature, StringComparer.Ordinal)
            && !features.Contains(UserReproductionReopensFeature, StringComparer.Ordinal))
            return $"'{LocalHunkRevertFeature}' requires '{UserReproductionReopensFeature}'.";
        return null;
    }

    public static bool Has(CorrectionRuntimePolicyDto? policy, string feature) =>
        policy is not null
        && string.Equals(policy.Kind, StructuredInvestigationKind, StringComparison.Ordinal)
        && policy.Version == StructuredInvestigationVersion
        && policy.Features.Contains(feature, StringComparer.Ordinal);

    public static bool IsStructuredInvestigation(CorrectionRuntimePolicyDto? policy) =>
        Has(policy, CausalTraceFeature);
}
