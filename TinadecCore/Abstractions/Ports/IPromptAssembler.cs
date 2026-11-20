using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Deterministically assembles prompt fragments, agent instructions, skill contributions,
/// and ContextPack into MAF ChatOptions.Instructions / AIContext.
/// Full prompt content stays local to preview UI and in-memory calls.
/// </summary>
public interface IPromptAssembler
{
    Task<PromptAssemblyResult> AssembleAsync(
        string agentId,
        ContextPack? contextPack,
        CancellationToken cancellationToken = default);

    Task<PromptAssemblyResult> AssembleAsync(
        FrozenPromptAssemblyRequest request,
        CancellationToken cancellationToken = default) =>
        AssembleAsync(request.AgentId, request.ContextPack, cancellationToken);
}

/// <summary>
/// Immutable prompt inputs captured in a run configuration. Durable execution uses
/// this request so a resumed run never reloads a mutable agent or prompt pipeline.
/// </summary>
public sealed record FrozenPromptAssemblyRequest(
    string AgentId,
    ContextPack? ContextPack,
    string? SystemPrompt = null,
    Guid? AgentVersionId = null,
    string? AgentVersionContentHash = null,
    Guid? PromptPipelineId = null,
    Guid? PromptVersionId = null,
    string? PromptVersionContentHash = null,
    string? PromptGraphJson = null,
    bool IncludeLiveFragments = false,
    string? ResponseLocale = null)
{
    /// <summary>
    /// The run's frozen mode policy. B5 reads it only to decide whether the tool-attribution
    /// constraint (blocked calls are not retried) belongs in this prompt.
    /// </summary>
    public ModeRuntimePolicyDto? RuntimePolicy { get; init; }

    /// <summary>
    /// The run's frozen workspace, when one is bound. It is prompt input, not a
    /// live lookup: the assembler renders the frozen root, git facts, listing and
    /// path contract, and never asks the store for a current root.
    /// </summary>
    public FrozenWorkspaceBinding? Workspace { get; init; }

    public Guid? AgentDefinitionId { get; init; }
    public string? AgentName { get; init; }
    public string? AgentSlug { get; init; }
    public string? AgentLayer { get; init; }
    public string? ModeName { get; init; }
    public string? ModeSlug { get; init; }
    public string? ModeInstructions { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public bool? HasTools { get; init; }
    public IReadOnlyList<FrozenPromptFragment>? FrozenFragments { get; init; }
    public IReadOnlyList<Guid>? IncludeFragmentIds { get; init; }
    public IReadOnlyList<string>? WorkspaceRules { get; init; }
    public string? SkillsIndexText { get; init; }
}

public sealed record FrozenPromptFragment(
    Guid Id,
    string Key,
    string Title,
    string Scope,
    Guid? TargetAgentId,
    string? TargetAgentSlug,
    string Category,
    int Priority,
    string Content,
    int Version,
    string ContentHash);

public sealed record PromptAssemblyLayer(
    string Id,
    string Kind,
    string Title,
    string Content,
    int EstimatedTokens,
    string Source);

public sealed class PromptAssemblyResult
{
    public string Instructions { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public IReadOnlyList<string> FragmentIds { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<PromptAssemblyLayer> Layers { get; init; } = [];
}

public interface IFrozenPromptFragmentSource
{
    Task<IReadOnlyList<FrozenPromptFragment>> SnapshotActiveFragmentsAsync(
        Guid tenantId,
        Guid? workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same snapshot, narrowed to the fragments that can ever apply to one agent. The run
    /// engine freezes this into <c>FrozenRunConfigurationV1</c> when the prompt is scoped to a
    /// single agent, so a resumed run carries no fragment belonging to another agent.
    /// </summary>
    async Task<IReadOnlyList<FrozenPromptFragment>> SnapshotForAgentAsync(
        Guid tenantId,
        Guid? workspaceId,
        string agentIdOrSlug,
        Guid? agentDefinitionId = null,
        CancellationToken cancellationToken = default)
    {
        var all = await SnapshotActiveFragmentsAsync(tenantId, workspaceId, cancellationToken).ConfigureAwait(false);
        return PromptFragmentTargeting.FilterForAgent(all, agentIdOrSlug, agentDefinitionId);
    }
}

/// <summary>
/// The one place that decides whether an agent-scoped fragment applies to an agent. Both the
/// assembler and the frozen-snapshot reader use it, so a fragment that applies at preview time
/// applies identically at run time.
/// </summary>
/// <remarks>
/// An agent is addressable two ways — its definition GUID and its slug ("meeting") — and stored
/// fragments use either form: a scoped fragment may carry only <c>TargetAgentId</c>, only
/// <c>TargetAgentSlug</c>, or a GUID that a resolver once also recorded as the slug text. Matching
/// therefore compares both columns against the whole identity set rather than against one form.
/// </remarks>
public static class PromptFragmentTargeting
{
    /// <summary>The fragments from a snapshot that could ever apply to this agent.</summary>
    public static IReadOnlyList<FrozenPromptFragment> FilterForAgent(
        IReadOnlyList<FrozenPromptFragment> fragments,
        string agentIdOrSlug,
        Guid? agentDefinitionId = null,
        string? agentSlug = null)
    {
        var (guids, slugs) = BuildIdentity(agentIdOrSlug, agentDefinitionId, agentSlug);
        return fragments
            .Where(fragment => Matches(fragment.TargetAgentId, fragment.TargetAgentSlug, guids, slugs))
            .ToArray();
    }

    public static (IReadOnlySet<Guid> Guids, IReadOnlySet<string> Slugs) BuildIdentity(
        string? agentIdOrSlug,
        Guid? agentDefinitionId = null,
        string? agentSlug = null)
    {
        var guids = new HashSet<Guid>();
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddToken(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) return;
            if (Guid.TryParse(trimmed, out var parsed) && parsed != Guid.Empty)
            {
                guids.Add(parsed);
                // A resolver that cannot find the definition records the GUID text as the slug.
                slugs.Add(trimmed);
                return;
            }
            slugs.Add(trimmed);
        }

        AddToken(agentIdOrSlug);
        AddToken(agentSlug);
        if (agentDefinitionId is { } definitionId && definitionId != Guid.Empty) guids.Add(definitionId);
        return (guids, slugs);
    }

    public static bool Matches(
        Guid? targetAgentId,
        string? targetAgentSlug,
        IReadOnlySet<Guid> candidateGuids,
        IReadOnlySet<string> candidateSlugs)
    {
        var hasId = targetAgentId is { } id && id != Guid.Empty;
        var hasSlug = !string.IsNullOrWhiteSpace(targetAgentSlug);
        if (!hasId && !hasSlug) return true;

        if (hasSlug)
        {
            var slug = targetAgentSlug!.Trim();
            if (slug.Length > 0)
            {
                if (candidateSlugs.Contains(slug)) return true;
                if (Guid.TryParse(slug, out var slugAsGuid) && slugAsGuid != Guid.Empty && candidateGuids.Contains(slugAsGuid))
                    return true;
            }
        }

        if (hasId)
        {
            if (candidateGuids.Contains(targetAgentId!.Value)) return true;
            if (candidateSlugs.Contains(targetAgentId.Value.ToString())) return true;
        }

        return false;
    }
}

public sealed record AgentIdentitySnapshot(
    Guid Id,
    string Slug,
    string DisplayName,
    string Layer,
    IReadOnlyList<string> Capabilities,
    bool HasTools);

public interface IAgentIdentityLookup
{
    Task<AgentIdentitySnapshot?> ResolveAsync(
        string agentIdOrSlug,
        CancellationToken cancellationToken = default);
}

public sealed class PromptFragmentException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public interface IPromptFragmentService
{
    Task<IReadOnlyList<PromptFragmentDto>> ListAsync(
        string? scope = null,
        Guid? targetAgentId = null,
        string? targetAgentSlug = null,
        string? category = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PromptFragmentDto> SaveAsync(
        Guid? id,
        PromptFragmentSaveRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromptFragmentVersionDto>> ListVersionsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentVersionDto> CreateVersionAsync(
        Guid id,
        PromptFragmentVersionCreateRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentDto> RollbackAsync(
        Guid id,
        PromptFragmentRollbackRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentDto> CloneAsync(
        Guid id,
        PromptFragmentCloneRequestDto? input = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
