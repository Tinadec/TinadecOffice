using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.AgentConfiguration;

public interface IAgentPackService
{
    Task<IReadOnlyList<AgentPackInstallationView>> ListAsync(CancellationToken cancellationToken = default);
    Task<AgentPackInstallationDetail?> GetAsync(string packId, CancellationToken cancellationToken = default);
    Task<AgentPackPreviewView> PreviewAsync(AgentPackEnvelopeDto envelope, CancellationToken cancellationToken = default);
    Task<AgentPackApplyView> ApplyAsync(string packId, AgentPackApplyRequestDto request, long? expectedRevision, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<AgentPackManagedResource?> FindManagedResourceAsync(string resourceKind, Guid logicalEntityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes a pack: its managed configuration resources, every run that
    /// referenced them, and the pack's own bookkeeping rows. Irreversible — the
    /// caller owns the confirmation UX.
    /// </summary>
    Task<AgentPackPurgeView> PurgeAsync(string packId, long? expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables a pack without deleting anything.</summary>
    Task<AgentPackInstallationView> SetEnabledAsync(string packId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Points the workspace defaults at this pack's activation resources.</summary>
    Task<AgentPackInstallationView> AdoptDefaultsAsync(string packId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pack ownership keyed by the managed resource's logical entity id, for the
    /// agent/mode/pipeline directory projections. Disabled packs are included:
    /// their resources stay read-only and still need an owner badge.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AgentPackManagedResource>> ListManagedResourcesAsync(CancellationToken cancellationToken = default);
}

public sealed record AgentPackCounts(
    int Agents,
    int PromptPipelines,
    int Modes,
    int Created,
    int Adopted,
    int Reused,
    int Updated);

public sealed record AgentPackPreviewView(
    string Action,
    Guid PreviewId,
    string PackId,
    string Owner,
    string BundledVersion,
    string? InstalledVersion,
    string IntegrityDigest,
    long Revision,
    DateTimeOffset ExpiresAt,
    AgentPackCounts Counts,
    IReadOnlyList<AgentPackResourceView> Resources,
    bool DefaultsWillAdopt,
    string? RequiredCoreVersion,
    string CurrentCoreVersion,
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> Warnings);

public sealed record AgentPackApplyView(
    string Status,
    string PackId,
    string Owner,
    string ActiveVersion,
    string IntegrityDigest,
    long Revision,
    AgentPackCounts Counts,
    IReadOnlyList<AgentPackResourceView> Resources,
    bool DefaultsAdopted,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentPackVersionView(Guid Id, string Version, string IntegrityDigest, DateTimeOffset CreatedAt, bool Active);

public sealed record AgentPackResourceView(string Kind, string ResourceKey, Guid? LogicalEntityId, Guid? VersionId, string? ContentHash, string Disposition);

public sealed record AgentPackInstallationView(
    string PackId,
    string Owner,
    string ProductId,
    string Name,
    string Status,
    string? ActiveVersion,
    string? IntegrityDigest,
    long Revision,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    /// <summary>工作区默认模式版本是否由本包提供（null = 本包从未接管过默认）。</summary>
    Guid? DefaultModeVersionId = null);

public sealed record AgentPackInstallationDetail(
    string PackId,
    string Owner,
    string ProductId,
    string Name,
    string Status,
    string? ActiveVersion,
    string? IntegrityDigest,
    long Revision,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AgentPackVersionView> Versions,
    IReadOnlyList<AgentPackResourceView> Resources);

public sealed record AgentPackManagedResource(string PackId, string ResourceKind, string ResourceKey, Guid LogicalEntityId);

/// <summary>
/// Per-table delete counts from <see cref="IAgentPackService.PurgeAsync"/>. SQLite
/// runs without foreign keys, so the delete order is the contract and these
/// counts are how a caller (and the test suite) proves it held.
/// </summary>
public sealed record AgentPackPurgeView(
    string PackId,
    long Revision,
    IReadOnlyDictionary<string, int> Deleted);

/// <summary>Thrown when a purge would leave the workspace unusable.</summary>
public sealed class AgentPackPurgeConflictException : Exception
{
    public AgentPackPurgeConflictException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

public sealed class AgentPackDomainException : Exception
{
    public AgentPackDomainException(int statusCode, string code, string title, string detail) : base(detail)
    {
        StatusCode = statusCode;
        Code = code;
        Title = title;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public string Title { get; }
}

public sealed class AgentPackService : IAgentPackService
{
    public const string ManifestApiVersion = "tinadec.io/agent-pack/v1alpha1";
    public const string ManifestKind = "AgentPack";
    public const string CurrentCoreVersion = "0.1.0";

    private static readonly HashSet<string> SupportedCapabilities = new(StringComparer.Ordinal)
    {
        "agent_pack_lifecycle",
        "versioned_agent_configuration",
        "frozen_agent_version_binding",
        // DmaEA graph orchestration pack surface (resources.tools, mode bindings,
        // node relationship files). Declaring it opts the pack into those semantics;
        // older Cores reject the unknown capability fail-closed.
        GraphValidation.GraphModePacksCapability
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IDbContextFactory<AgentConfigurationDbContext> _factory;
    private readonly IContentStore _contentStore;
    private readonly ITenantContextAccessor _tenantAccessor;
    // The run-state fan-out of a purge lives in the lifecycle/memory/DmaEA stores
    // this assembly must not reference (architecture rule ②: AgentConfiguration
    // depends on Abstractions only). The composition root supplies the port.
    private readonly IAgentPackResourceStore? _resourceStore;

    public AgentPackService(
        IDbContextFactory<AgentConfigurationDbContext> factory,
        IContentStore contentStore,
        ITenantContextAccessor tenantAccessor,
        IAgentPackResourceStore? resourceStore = null)
    {
        _factory = factory;
        _contentStore = contentStore;
        _tenantAccessor = tenantAccessor;
        _resourceStore = resourceStore;
    }

    public async Task<IReadOnlyList<AgentPackInstallationView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installations = await db.AgentPackInstallations.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId)
            .OrderBy(item => item.PackId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var versionIds = installations.Where(item => item.ActiveVersionId.HasValue).Select(item => item.ActiveVersionId!.Value).ToArray();
        var versions = await db.AgentPackVersions.AsNoTracking()
            .Where(item => versionIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
        // Which pack currently owns the workspace default: the UI marks it and
        // offers "set as default" only on the others.
        var installationIds = installations.Select(item => item.Id).ToArray();
        var adoptedModeVersions = (await db.AgentPackDefaultAdoptions.AsNoTracking()
                .Where(item => installationIds.Contains(item.InstallationId) && item.AppliedModeVersionId != null)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(item => item.InstallationId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First().AppliedModeVersionId);
        return installations.Select(item => ToInstallationView(
            item,
            item.ActiveVersionId is { } id && versions.TryGetValue(id, out var version) ? version : null,
            adoptedModeVersions.TryGetValue(item.Id, out var adopted) ? adopted : null)).ToArray();
    }

    public async Task<AgentPackInstallationDetail?> GetAsync(string packId, CancellationToken cancellationToken = default)
    {
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installation = await db.AgentPackInstallations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.PackId == packId, cancellationToken).ConfigureAwait(false);
        if (installation is null) return null;
        var versions = (await db.AgentPackVersions.AsNoTracking()
            .Where(item => item.InstallationId == installation.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
        var active = versions.SingleOrDefault(item => item.Id == installation.ActiveVersionId);
        var bindings = active is null
            ? []
            : await db.AgentPackResourceBindings.AsNoTracking()
                .Where(item => item.PackVersionId == active.Id)
                .OrderBy(item => item.ResourceKind).ThenBy(item => item.ResourceKey)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new AgentPackInstallationDetail(
            installation.PackId,
            installation.Owner,
            installation.ProductId,
            installation.Name,
            installation.Status,
            active?.PackVersion,
            active?.ManifestHash,
            installation.Revision,
            installation.CreatedAt,
            installation.UpdatedAt,
            versions.Select(item => new AgentPackVersionView(item.Id, item.PackVersion, item.ManifestHash, item.CreatedAt, item.Id == installation.ActiveVersionId)).ToArray(),
            bindings.Select(item => new AgentPackResourceView(item.ResourceKind, item.ResourceKey, item.LogicalEntityId, item.VersionId, item.ContentHash, item.Disposition)).ToArray());
    }

    public async Task<AgentPackManagedResource?> FindManagedResourceAsync(string resourceKind, Guid logicalEntityId, CancellationToken cancellationToken = default)
    {
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // A disabled pack keeps its managed resources read-only: disabling hides
        // them from the selectable lists, it never hands them to the editor.
        var query = from resource in db.AgentPackManagedResources.AsNoTracking()
                    join installation in db.AgentPackInstallations.AsNoTracking() on resource.InstallationId equals installation.Id
                    where resource.TenantId == scope.TenantId
                        && resource.WorkspaceId == scope.WorkspaceId
                        && resource.ResourceKind == resourceKind
                        && resource.LogicalEntityId == logicalEntityId
                        && (installation.Status == "active" || installation.Status == "disabled")
                    select new AgentPackManagedResource(installation.PackId, resource.ResourceKind, resource.ResourceKey, resource.LogicalEntityId);
        return await query.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, AgentPackManagedResource>> ListManagedResourcesAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await (from resource in db.AgentPackManagedResources.AsNoTracking()
                          join installation in db.AgentPackInstallations.AsNoTracking() on resource.InstallationId equals installation.Id
                          where resource.TenantId == scope.TenantId && resource.WorkspaceId == scope.WorkspaceId
                          select new { installation.PackId, resource.ResourceKind, resource.ResourceKey, resource.LogicalEntityId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .GroupBy(row => row.LogicalEntityId)
            .ToDictionary(
                group => group.Key,
                group => new AgentPackManagedResource(group.First().PackId, group.First().ResourceKind, group.First().ResourceKey, group.Key));
    }

    public async Task<AgentPackInstallationView> SetEnabledAsync(string packId, bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureCanManage();
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installation = await db.AgentPackInstallations.SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.PackId == packId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentPackDomainException(404, "agent_pack_not_found", "Agent pack not found", $"Agent pack '{packId}' is not installed in this workspace.");
        // Disabling is a visibility switch, never a data change: the resources stay
        // in the database (and stay read-only) so re-enabling is lossless.
        installation.Status = enabled ? "active" : "disabled";
        installation.Revision++;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        installation.UpdatedByPrincipalId = scope.PrincipalId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var active = installation.ActiveVersionId is { } activeId
            ? await db.AgentPackVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == activeId, cancellationToken).ConfigureAwait(false)
            : null;
        return ToInstallationView(installation, active);
    }

    public async Task<AgentPackInstallationView> AdoptDefaultsAsync(string packId, CancellationToken cancellationToken = default)
    {
        EnsureCanManage();
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installation = await db.AgentPackInstallations.SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.PackId == packId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentPackDomainException(404, "agent_pack_not_found", "Agent pack not found", $"Agent pack '{packId}' is not installed in this workspace.");
        if (installation.ActiveVersionId is not { } activeVersionId)
            throw new AgentPackDomainException(409, "agent_pack_not_active", "Agent pack is not active", $"Agent pack '{packId}' has no active version to adopt defaults from.");
        var packVersion = await db.AgentPackVersions.AsNoTracking().SingleAsync(item => item.Id == activeVersionId, cancellationToken).ConfigureAwait(false);
        var bindings = await db.AgentPackResourceBindings.AsNoTracking()
            .Where(item => item.PackVersionId == packVersion.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var defaults = await db.WorkspaceDefaults.SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(packVersion, cancellationToken).ConfigureAwait(false);

        var publications = new PackPublications(
            bindings.Where(item => item.ResourceKind == "agent").ToDictionary(item => item.ResourceKey, item => new PublishedResource(item.LogicalEntityId, item.VersionId, item.ContentHash ?? string.Empty, item.Disposition), StringComparer.Ordinal),
            bindings.Where(item => item.ResourceKind == "prompt_pipeline").ToDictionary(item => item.ResourceKey, item => new PublishedResource(item.LogicalEntityId, item.VersionId, item.ContentHash ?? string.Empty, item.Disposition), StringComparer.Ordinal),
            bindings.Where(item => item.ResourceKind == "mode").ToDictionary(item => item.ResourceKey, item => new PublishedResource(item.LogicalEntityId, item.VersionId, item.ContentHash ?? string.Empty, item.Disposition), StringComparer.Ordinal),
            new AgentPackCounts(0, 0, 0, 0, 0, 0, 0),
            []);
        var adoption = await db.AgentPackDefaultAdoptions.SingleOrDefaultAsync(item =>
            item.InstallationId == installation.Id && item.PackVersionId == packVersion.Id, cancellationToken).ConfigureAwait(false);
        adoption ??= new AgentPackDefaultAdoptionRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            InstallationId = installation.Id,
            PackVersionId = packVersion.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (db.Entry(adoption).State == EntityState.Detached) db.AgentPackDefaultAdoptions.Add(adoption);
        ApplyWorkspaceDefaults(db, installation, packVersion, publications, manifest, defaults, shouldApply: true, scope, DateTimeOffset.UtcNow, adoption);
        installation.Revision++;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        installation.UpdatedByPrincipalId = scope.PrincipalId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToInstallationView(installation, packVersion);
    }

    /// <summary>
    /// Hard-deletes a pack. The order below is the contract: SQLite runs without
    /// foreign keys, so a wrong order silently leaves orphans rather than failing.
    /// Every table's delete count is returned so a caller (and the test suite) can
    /// assert the shape instead of trusting it.
    /// </summary>
    public async Task<AgentPackPurgeView> PurgeAsync(string packId, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        EnsureCanManage();
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installation = await db.AgentPackInstallations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.PackId == packId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentPackDomainException(404, "agent_pack_not_found", "Agent pack not found", $"Agent pack '{packId}' is not installed in this workspace.");
        if (expectedRevision is { } revision && revision != installation.Revision)
            throw new AgentPackDomainException(412, "agent_pack_revision_conflict", "Agent pack revision conflict", "If-Match must equal the current agent pack revision.");

        var versionIds = await db.AgentPackVersions.AsNoTracking()
            .Where(item => item.InstallationId == installation.Id)
            .Select(item => item.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var managed = await db.AgentPackManagedResources.AsNoTracking()
            .Where(item => item.InstallationId == installation.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (managed.Count == 0 && versionIds.Count == 0)
            throw new AgentPackDomainException(409, "agent_pack_not_active", "Agent pack is not active", $"Agent pack '{packId}' has no managed resources to purge.");

        var agentDefinitionIds = managed.Where(item => item.ResourceKind == "agent").Select(item => item.LogicalEntityId).ToArray();
        var modeIds = managed.Where(item => item.ResourceKind == "mode").Select(item => item.LogicalEntityId).ToArray();
        var promptPipelineIds = managed.Where(item => item.ResourceKind == "prompt_pipeline").Select(item => item.LogicalEntityId).ToArray();
        var agentVersionIds = await db.AgentVersions.AsNoTracking()
            .Where(item => agentDefinitionIds.Contains(item.AgentDefinitionId))
            .Select(item => item.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var modeVersionIds = await db.ModeVersions.AsNoTracking()
            .Where(item => modeIds.Contains(item.AgentModeId))
            .Select(item => item.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var promptVersionIds = await db.PromptVersions.AsNoTracking()
            .Where(item => promptPipelineIds.Contains(item.PromptPipelineId))
            .Select(item => item.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (_resourceStore is null)
            throw new InvalidOperationException("Purging an agent pack requires the run-resource store to be registered.");

        // 1. Runs that referenced this pack's resources: the frozen body names the
        //    mode version, and instances name the agent versions. The cross-context
        //    part lives behind IAgentPackResourceStore (architecture rule ②).
        var reference = await _resourceStore.FindReferencedRunsAsync(
            scope.TenantId, scope.WorkspaceId, agentDefinitionIds, agentVersionIds, modeVersionIds, cancellationToken).ConfigureAwait(false);
        var runDeletes = await _resourceStore.DeleteRunsAsync(scope.TenantId, scope.WorkspaceId, reference, cancellationToken).ConfigureAwait(false);
        var sessionsCleared = await _resourceStore.ClearSessionModeBindingsAsync(
            scope.TenantId, scope.WorkspaceId, modeVersionIds, cancellationToken).ConfigureAwait(false);

        // 2. Workspace defaults fall back to the pre-adoption values this pack
        //    recorded, so a purge restores the previous owner instead of leaving a
        //    dangling primary key.
        var adopted = await db.AgentPackDefaultAdoptions.AsNoTracking()
            .Where(item => item.InstallationId == installation.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var workspaceDefaults = await db.WorkspaceDefaults.SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var defaultsRestored = 0;
        if (workspaceDefaults is not null && adopted.Count != 0)
        {
            var adoption = adopted.OrderByDescending(item => item.CreatedAt).First();
            var stillOurs = (workspaceDefaults.DefaultAgentDefinitionId is { } agentId && agentDefinitionIds.Contains(agentId))
                || (workspaceDefaults.DefaultAgentModeId is { } modeId && modeIds.Contains(modeId))
                || (workspaceDefaults.DefaultPromptPipelineId is { } pipelineId && promptPipelineIds.Contains(pipelineId));
            if (stillOurs)
            {
                workspaceDefaults.DefaultAgentDefinitionId = adoption.PreviousAgentDefinitionId;
                workspaceDefaults.DefaultAgentVersionId = adoption.PreviousAgentVersionId;
                workspaceDefaults.DefaultAgentModeId = adoption.PreviousAgentModeId;
                workspaceDefaults.DefaultModeVersionId = adoption.PreviousModeVersionId;
                workspaceDefaults.DefaultPromptPipelineId = adoption.PreviousPromptPipelineId;
                workspaceDefaults.DefaultPromptVersionId = adoption.PreviousPromptVersionId;
                workspaceDefaults.Revision++;
                workspaceDefaults.UpdatedAt = DateTimeOffset.UtcNow;
                defaultsRestored = 1;
            }
        }
        var adoptions = await db.AgentPackDefaultAdoptions.Where(item => item.InstallationId == installation.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // 3. Managed configuration resources, then the pack's own bookkeeping. The
        //    children go before their parents: no foreign keys are enforced.
        var modeNodes = await db.ModeNodes.Where(item => modeIds.Contains(item.ModeId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var modeEdges = await db.ModeEdges.Where(item => modeIds.Contains(item.ModeId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var canvasLayouts = await db.CanvasLayouts.Where(item => modeIds.Contains(item.ModeId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var modeVersions = await db.ModeVersions.Where(item => modeIds.Contains(item.AgentModeId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var modes = await db.AgentModes.Where(item => modeIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var agentVersions = await db.AgentVersions.Where(item => agentDefinitionIds.Contains(item.AgentDefinitionId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var agents = await db.AgentDefinitions.Where(item => agentDefinitionIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var promptNodes = await db.PromptNodes.Where(item => promptPipelineIds.Contains(item.PromptPipelineId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var promptVersions = await db.PromptVersions.Where(item => promptPipelineIds.Contains(item.PromptPipelineId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var pipelines = await db.PromptPipelines.Where(item => promptPipelineIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var resourceBindings = await db.AgentPackResourceBindings.Where(item => versionIds.Contains(item.PackVersionId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var managedResources = await db.AgentPackManagedResources.Where(item => item.InstallationId == installation.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var versions = await db.AgentPackVersions.Where(item => item.InstallationId == installation.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var previews = await db.AgentPackPreviews.Where(item => item.PackId == packId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var operations = await db.AgentPackOperations.Where(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var installations = await db.AgentPackInstallations.Where(item => item.Id == installation.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The DB rows are gone; the run's on-disk evidence must go with them or a
        // later replay would surface a run the database no longer knows.
        _resourceStore.DeleteRunArtifacts(reference.RunIds);

        var deleted = new Dictionary<string, int>(runDeletes, StringComparer.Ordinal)
        {
            ["sessions_mode_binding_cleared"] = sessionsCleared,
            ["workspace_defaults_restored"] = defaultsRestored,
            ["agent_pack_default_adoptions"] = adoptions,
            ["mode_nodes"] = modeNodes,
            ["mode_edges"] = modeEdges,
            ["canvas_layouts"] = canvasLayouts,
            ["mode_versions"] = modeVersions,
            ["agent_modes"] = modes,
            ["agent_versions"] = agentVersions,
            ["agent_definitions"] = agents,
            ["prompt_nodes"] = promptNodes,
            ["prompt_versions"] = promptVersions,
            ["prompt_pipelines"] = pipelines,
            ["agent_pack_resource_bindings"] = resourceBindings,
            ["agent_pack_managed_resources"] = managedResources,
            ["agent_pack_versions"] = versions,
            ["agent_pack_previews"] = previews,
            ["agent_pack_operations"] = operations,
            ["agent_pack_installations"] = installations
        };
        return new AgentPackPurgeView(packId, installation.Revision, deleted);
    }

    /// <summary>
    /// Reads the manifest back from the stored content. What the store holds is the
    /// canonical <em>envelope</em> (<c>{manifest, integrity}</c>), not a bare
    /// manifest, so this unwraps it before returning.
    /// </summary>
    private async Task<AgentPackManifestDto> ReadManifestAsync(AgentPackVersionRecord packVersion, CancellationToken cancellationToken)
    {
        await using var stream = await _contentStore.OpenReadAsync(
            new ContentReference(packVersion.ManifestContentReference, string.Empty, packVersion.ManifestLength, "application/json"),
            cancellationToken).ConfigureAwait(false);
        var envelope = await JsonSerializer.DeserializeAsync<AgentPackEnvelopeDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Stored agent pack content is invalid.");
        return envelope.Manifest ?? throw new InvalidDataException("Stored agent pack envelope carries no manifest.");
    }

    public async Task<AgentPackPreviewView> PreviewAsync(AgentPackEnvelopeDto envelope, CancellationToken cancellationToken = default)
    {
        EnsureCanManage();
        var validated = ValidateEnvelope(envelope);
        var scope = _tenantAccessor.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installation = await db.AgentPackInstallations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.PackId == validated.PackId, cancellationToken).ConfigureAwait(false);
        AgentPackVersionRecord? activeVersion = null;
        if (installation?.ActiveVersionId is { } activeVersionId)
            activeVersion = await db.AgentPackVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == activeVersionId, cancellationToken).ConfigureAwait(false);

        if (installation is not null && !string.Equals(installation.Owner, validated.Owner, StringComparison.Ordinal))
            throw Conflict("agent_pack_owner_conflict", "Agent pack owner conflict", "The installed pack owner does not match the submitted manifest owner.");

        var action = ResolvePreviewAction(activeVersion, validated);
        var analysis = await AnalyzeResourcesAsync(db, installation, validated, cancellationToken).ConfigureAwait(false);
        if (action != "newer_installed" && analysis.Conflicts.Count != 0) action = "conflict";
        var defaultsWillAdopt = action is "install" or "upgrade"
            && await DefaultsWillAdoptAsync(db, installation, validated.Manifest, cancellationToken).ConfigureAwait(false);

        await using var manifestStream = new MemoryStream(validated.EnvelopeBytes, writable: false);
        var stored = await _contentStore.PutAsync(new ContentWriteRequest(
            scope.TenantId, scope.WorkspaceId, "agent-pack-manifest", "application/json", manifestStream), cancellationToken).ConfigureAwait(false);
        var defaultsRevision = await db.WorkspaceDefaults.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId)
            .Select(item => (long?)item.Revision)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var preview = new AgentPackPreviewRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            PrincipalId = scope.PrincipalId,
            Action = action,
            PackId = validated.PackId,
            Owner = validated.Owner,
            PackVersion = validated.Version,
            ManifestContentReference = stored.Value,
            ManifestHash = validated.Digest,
            ManifestLength = stored.Length,
            BaseInstallationRevision = installation?.Revision ?? 0,
            BaseDefaultsRevision = defaultsRevision,
            Status = "pending",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(15)
        };
        db.AgentPackPreviews.Add(preview);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new AgentPackPreviewView(
            action,
            preview.Id,
            validated.PackId,
            validated.Owner,
            validated.Version,
            activeVersion?.PackVersion,
            validated.Digest,
            installation?.Revision ?? 0,
            preview.ExpiresAt,
            analysis.Counts,
            analysis.Resources,
            defaultsWillAdopt,
            validated.Manifest.Compatibility?.MinimumCoreVersion,
            CurrentCoreVersion,
            analysis.Conflicts,
            analysis.Warnings);
    }

    public async Task<AgentPackApplyView> ApplyAsync(string packId, AgentPackApplyRequestDto request, long? expectedRevision, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        const int maximumAttempts = 4;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return await ApplyCoreAsync(packId, request, expectedRevision, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < maximumAttempts && IsConcurrentPackWrite(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 20), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Agent pack installation retry loop exited unexpectedly.");
    }

    private static bool IsConcurrentPackWrite(Exception exception)
    {
        if (exception is DbUpdateException) return true;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            if (type.FullName == "Microsoft.Data.Sqlite.SqliteException"
                && type.GetProperty("SqliteErrorCode")?.GetValue(current) is int sqliteCode
                && sqliteCode is 5 or 6 or 19)
            {
                return true;
            }
            if (type.FullName == "Npgsql.PostgresException"
                && type.GetProperty("SqlState")?.GetValue(current) is string sqlState
                && sqlState is "23505" or "40001" or "40P01")
            {
                return true;
            }
        }
        return false;
    }

    private static AgentPackInstallationView ToInstallationView(
        AgentPackInstallationRecord installation,
        AgentPackVersionRecord? active,
        Guid? defaultModeVersionId = null) => new(
        installation.PackId,
        installation.Owner,
        installation.ProductId,
        installation.Name,
        installation.Status,
        active?.PackVersion,
        active?.ManifestHash,
        installation.Revision,
        installation.CreatedAt,
        installation.UpdatedAt,
        defaultModeVersionId);

    private void EnsureCanManage()
    {
        var role = _tenantAccessor.Current.Role;
        if (!string.Equals(role, "owner", StringComparison.OrdinalIgnoreCase))
            throw new AgentPackDomainException(403, "agent_pack_management_forbidden", "Agent pack operation forbidden", "The current principal must be the workspace owner.");
    }

    private static AgentPackDomainException Conflict(string code, string title, string detail) => new(409, code, title, detail);

    private sealed record ValidatedEnvelope(
        AgentPackEnvelopeDto Envelope,
        AgentPackManifestDto Manifest,
        string PackId,
        string Owner,
        string ProductId,
        string Name,
        string Version,
        string Digest,
        byte[] EnvelopeBytes);

    private sealed record ResourceAnalysis(AgentPackCounts Counts, IReadOnlyList<AgentPackResourceView> Resources, IReadOnlyList<string> Warnings, IReadOnlyList<string> Conflicts);

    private async Task<AgentPackApplyView> ApplyCoreAsync(string packId, AgentPackApplyRequestDto request, long? expectedRevision, string idempotencyKey, CancellationToken cancellationToken)
    {
        EnsureCanManage();
        if (request.PreviewId == Guid.Empty) throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack request", "preview_id is required.");
        if (request.Envelope is null) throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack request", "envelope is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack request", "Idempotency-Key is required and must not exceed 256 characters.");
        var validated = ValidateEnvelope(request.Envelope);
        if (!string.Equals(packId, validated.PackId, StringComparison.Ordinal))
            throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack request", "Route pack_id does not match manifest metadata.pack_id.");
        var scope = _tenantAccessor.Current;
        var requestHash = Sha256Hex(Encoding.UTF8.GetBytes($"{packId}\n{request.PreviewId:D}\n{validated.Digest}"));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var priorOperation = await db.AgentPackOperations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId
            && item.WorkspaceId == scope.WorkspaceId
            && item.PrincipalId == scope.PrincipalId
            && item.Operation == "install"
            && item.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (priorOperation is not null)
        {
            if (!string.Equals(priorOperation.RequestHash, requestHash, StringComparison.Ordinal))
                throw Conflict("idempotency_key_reused", "Idempotency key reused", "The Idempotency-Key was already used for a different agent pack request.");
            return JsonSerializer.Deserialize<AgentPackApplyView>(priorOperation.ResponseJson, JsonOptions)
                ?? throw new InvalidDataException("Stored agent pack idempotency response is invalid.");
        }

        var preview = await db.AgentPackPreviews.SingleOrDefaultAsync(item =>
            item.Id == request.PreviewId
            && item.TenantId == scope.TenantId
            && item.WorkspaceId == scope.WorkspaceId
            && item.PrincipalId == scope.PrincipalId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentPackDomainException(412, "agent_pack_preview_stale", "Agent pack preview is stale", "The preview does not exist in the current workspace and principal scope.");
        if (preview.Status != "pending" || preview.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new AgentPackDomainException(412, "agent_pack_preview_stale", "Agent pack preview is stale", "The preview was already consumed or has expired.");
        if (preview.PackId != validated.PackId || preview.Owner != validated.Owner || preview.PackVersion != validated.Version || preview.ManifestHash != validated.Digest)
            throw new AgentPackDomainException(412, "agent_pack_preview_stale", "Agent pack preview is stale", "The submitted envelope differs from the previewed manifest.");
        await using (var previewContent = await _contentStore.OpenReadAsync(new ContentReference(preview.ManifestContentReference, string.Empty, preview.ManifestLength, "application/json"), cancellationToken).ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            await previewContent.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            if (!memory.ToArray().AsSpan().SequenceEqual(validated.EnvelopeBytes))
                throw new AgentPackDomainException(412, "agent_pack_preview_stale", "Agent pack preview is stale", "The submitted envelope is not byte-equivalent to the previewed canonical envelope.");
        }

        var installation = await db.AgentPackInstallations.SingleOrDefaultAsync(item =>
            item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId && item.PackId == packId, cancellationToken).ConfigureAwait(false);
        if (installation is not null && !string.Equals(installation.Owner, validated.Owner, StringComparison.Ordinal))
            throw Conflict("agent_pack_owner_conflict", "Agent pack owner conflict", "The installed pack owner differs from the submitted manifest owner.");
        AgentPackVersionRecord? active = installation?.ActiveVersionId is { } activeId
            ? await db.AgentPackVersions.SingleOrDefaultAsync(item => item.Id == activeId, cancellationToken).ConfigureAwait(false)
            : null;
        var convergedFreshInstall = installation is not null
            && active is not null
            && preview.Action == "install"
            && preview.BaseInstallationRevision == 0
            && expectedRevision is null or 0
            && string.Equals(active.PackVersion, validated.Version, StringComparison.Ordinal)
            && string.Equals(active.ManifestHash, validated.Digest, StringComparison.OrdinalIgnoreCase);
        if (installation is null)
        {
            if (preview.BaseInstallationRevision != 0 || expectedRevision is > 0)
                throw new AgentPackDomainException(412, "agent_pack_revision_conflict", "Agent pack revision conflict", "The installation state changed after preview.");
        }
        else
        {
            if (!convergedFreshInstall
                && (expectedRevision is null || expectedRevision.Value != installation.Revision || preview.BaseInstallationRevision != installation.Revision))
                throw new AgentPackDomainException(412, "agent_pack_revision_conflict", "Agent pack revision conflict", "If-Match must equal the current agent pack revision.");
        }
        var defaults = await db.WorkspaceDefaults.SingleOrDefaultAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (!convergedFreshInstall && preview.BaseDefaultsRevision != defaults?.Revision)
            throw new AgentPackDomainException(412, "agent_pack_preview_stale", "Agent pack preview is stale", "Workspace defaults changed after preview.");
        var action = ResolvePreviewAction(active, validated);
        var currentAnalysis = await AnalyzeResourcesAsync(db, installation, validated, cancellationToken).ConfigureAwait(false);
        if (preview.Action == "conflict" || (action != "newer_installed" && currentAnalysis.Conflicts.Count != 0))
            throw Conflict("agent_pack_resource_conflict", "Agent pack resource conflict", string.Join(" ", currentAnalysis.Conflicts.DefaultIfEmpty("The preview contains resource conflicts.")));
        if (!string.Equals(action, preview.Action, StringComparison.Ordinal)
            && !(convergedFreshInstall && action == "up_to_date"))
            throw new AgentPackDomainException(412, "agent_pack_preview_stale", "Agent pack preview is stale", "The applicable pack action changed after preview.");

        var defaultsWillAdopt = action is "install" or "upgrade"
            && await DefaultsWillAdoptAsync(db, installation, validated.Manifest, cancellationToken).ConfigureAwait(false);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (action is "up_to_date" or "newer_installed")
        {
            if (installation is null || active is null) throw new InvalidDataException("A no-op pack action has no active installation.");
            preview.Status = "consumed";
            preview.ConsumedAt = now;
            var noOpCounts = new AgentPackCounts(validated.Manifest.Resources!.Agents.Count, validated.Manifest.Resources.PromptPipelines.Count, validated.Manifest.Resources.Modes.Count, 0, 0, validated.Manifest.Resources.Agents.Count + validated.Manifest.Resources.PromptPipelines.Count + validated.Manifest.Resources.Modes.Count, 0);
            var noOpBindings = await db.AgentPackResourceBindings.AsNoTracking().Where(item => item.PackVersionId == active.Id)
                .OrderBy(item => item.ResourceKind).ThenBy(item => item.ResourceKey).ToListAsync(cancellationToken).ConfigureAwait(false);
            var noOpResources = noOpBindings.Select(ToResourceView).ToArray();
            var noOp = new AgentPackApplyView(action, packId, installation.Owner, active.PackVersion, active.ManifestHash, installation.Revision, noOpCounts, noOpResources, false, installation.CreatedAt, installation.UpdatedAt);
            AddOperation(db, scope, idempotencyKey, requestHash, noOp, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return noOp;
        }

        var firstInstall = installation is null;
        installation ??= new AgentPackInstallationRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            PackId = validated.PackId, Owner = validated.Owner, ProductId = validated.ProductId, Name = validated.Name,
            Status = "active", Revision = 0, CreatedAt = now, UpdatedAt = now,
            CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId
        };
        if (firstInstall) db.AgentPackInstallations.Add(installation);
        var packVersion = new AgentPackVersionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            InstallationId = installation.Id, PackVersion = validated.Version,
            ManifestContentReference = preview.ManifestContentReference, ManifestHash = validated.Digest, ManifestLength = preview.ManifestLength,
            PreviousVersionId = installation.ActiveVersionId, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
        };
        db.AgentPackVersions.Add(packVersion);
        var managed = firstInstall
            ? new Dictionary<(string Kind, string Key), AgentPackManagedResourceRecord>()
            : await db.AgentPackManagedResources.Where(item => item.InstallationId == installation.Id)
                .ToDictionaryAsync(item => (item.ResourceKind, item.ResourceKey), cancellationToken).ConfigureAwait(false);
        var publications = await PublishPackResourcesAsync(db, installation, packVersion, managed, validated, scope, now, cancellationToken).ConfigureAwait(false);
        var defaultsAdopted = ApplyWorkspaceDefaults(db, installation, packVersion, publications, validated.Manifest, defaults, defaultsWillAdopt, scope, now);
        installation.ActiveVersionId = packVersion.Id;
        installation.Owner = validated.Owner;
        installation.ProductId = validated.ProductId;
        installation.Name = validated.Name;
        installation.Status = "active";
        installation.Revision++;
        installation.UpdatedAt = now;
        installation.UpdatedByPrincipalId = scope.PrincipalId;
        preview.Status = "consumed";
        preview.ConsumedAt = now;
        var counts = publications.Counts;
        var result = new AgentPackApplyView(firstInstall ? "installed" : "updated", packId, installation.Owner, packVersion.PackVersion, packVersion.ManifestHash, installation.Revision, counts, publications.Resources, defaultsAdopted, installation.CreatedAt, installation.UpdatedAt);
        AddOperation(db, scope, idempotencyKey, requestHash, result, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static void AddOperation(AgentConfigurationDbContext db, TenantContext scope, string idempotencyKey, string requestHash, AgentPackApplyView result, DateTimeOffset now) =>
        db.AgentPackOperations.Add(new AgentPackOperationRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, PrincipalId = scope.PrincipalId,
            IdempotencyKey = idempotencyKey, Operation = "install", RequestHash = requestHash, StatusCode = 200,
            ResponseJson = JsonSerializer.Serialize(result, JsonOptions), CreatedAt = now
        });

    private sealed record PublishedResource(Guid LogicalId, Guid VersionId, string ContentHash, string Disposition);

    private sealed record PackPublications(
        IReadOnlyDictionary<string, PublishedResource> Agents,
        IReadOnlyDictionary<string, PublishedResource> Prompts,
        IReadOnlyDictionary<string, PublishedResource> Modes,
        AgentPackCounts Counts,
        IReadOnlyList<AgentPackResourceView> Resources);

    private async Task<PackPublications> PublishPackResourcesAsync(
        AgentConfigurationDbContext db,
        AgentPackInstallationRecord installation,
        AgentPackVersionRecord packVersion,
        Dictionary<(string Kind, string Key), AgentPackManagedResourceRecord> managed,
        ValidatedEnvelope envelope,
        TenantContext scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var resources = envelope.Manifest.Resources!;
        var prompts = new Dictionary<string, PublishedResource>(StringComparer.Ordinal);
        var agents = new Dictionary<string, PublishedResource>(StringComparer.Ordinal);
        var modes = new Dictionary<string, PublishedResource>(StringComparer.Ordinal);
        foreach (var resource in resources.PromptPipelines.OrderBy(item => item.ResourceKey, StringComparer.Ordinal))
            prompts.Add(resource.ResourceKey!, await PublishPromptAsync(db, installation, packVersion, managed, resource, scope, now, cancellationToken).ConfigureAwait(false));
        foreach (var resource in resources.Agents.OrderBy(item => item.ResourceKey, StringComparer.Ordinal))
            agents.Add(resource.ResourceKey!, await PublishAgentAsync(db, installation, packVersion, managed, resource, prompts, scope, now, cancellationToken).ConfigureAwait(false));
        foreach (var resource in resources.Modes.OrderBy(item => item.ResourceKey, StringComparer.Ordinal))
            modes.Add(resource.ResourceKey!, await PublishModeAsync(db, installation, packVersion, managed, resource, resources.Agents, agents, prompts, scope, now, cancellationToken).ConfigureAwait(false));
        var dispositions = prompts.Values.Concat(agents.Values).Concat(modes.Values).Select(item => item.Disposition).ToArray();
        var counts = new AgentPackCounts(
            agents.Count, prompts.Count, modes.Count,
            dispositions.Count(item => item == "created"),
            dispositions.Count(item => item == "adopted"),
            dispositions.Count(item => item == "reused"),
            dispositions.Count(item => item == "updated"));
        var views = agents.Select(item => ToResourceView("agent", item.Key, item.Value))
            .Concat(prompts.Select(item => ToResourceView("prompt_pipeline", item.Key, item.Value)))
            .Concat(modes.Select(item => ToResourceView("mode", item.Key, item.Value)))
            .OrderBy(item => item.Kind).ThenBy(item => item.ResourceKey).ToArray();
        return new PackPublications(agents, prompts, modes, counts, views);
    }

    private async Task<PublishedResource> PublishPromptAsync(
        AgentConfigurationDbContext db,
        AgentPackInstallationRecord installation,
        AgentPackVersionRecord packVersion,
        Dictionary<(string Kind, string Key), AgentPackManagedResourceRecord> managed,
        AgentPackPromptPipelineResourceDto resource,
        TenantContext scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var managedKey = ("prompt_pipeline", resource.ResourceKey!);
        managed.TryGetValue(managedKey, out var management);
        PromptPipelineRecord? logical = management is null
            ? null
            : await db.PromptPipelines.SingleAsync(item => item.Id == management.LogicalEntityId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var wasCreated = logical is null;
        logical ??= new PromptPipelineRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            Slug = resource.Slug!, Status = "published", Revision = 0, Version = 0,
            CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
        };
        if (wasCreated) db.PromptPipelines.Add(logical);
        var graph = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(resource.Graph));
        var hash = Sha256Hex(Encoding.UTF8.GetBytes(graph));
        var latest = await db.PromptVersions.Where(item => item.PromptPipelineId == logical.Id).OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        PromptVersionRecord version;
        var unchanged = latest is not null && string.Equals(latest.ContentHash, hash, StringComparison.OrdinalIgnoreCase) && string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(latest.GraphJson)), hash, StringComparison.OrdinalIgnoreCase);
        if (unchanged)
        {
            version = latest!;
        }
        else
        {
            version = new PromptVersionRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                PromptPipelineId = logical.Id, Version = (latest?.Version ?? 0) + 1,
                GraphJson = graph, ContentHash = hash, ContentLength = Encoding.UTF8.GetByteCount(graph),
                Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
            };
            db.PromptVersions.Add(version);
        }
        logical.DisplayName = resource.DisplayName!;
        logical.Description = resource.Description;
        logical.GraphJson = graph;
        logical.Status = "published";
        logical.Version = version.Version;
        logical.Revision++;
        logical.UpdatedAt = now;
        logical.UpdatedByPrincipalId = scope.PrincipalId;
        var disposition = ResolveDisposition(wasCreated, unchanged);
        EnsureManaged(db, managed, installation, "prompt_pipeline", resource.ResourceKey!, logical.Id, scope, now);
        AddBinding(db, packVersion, "prompt_pipeline", resource.ResourceKey!, logical.Id, version.Id, hash, disposition, scope, now);
        return new PublishedResource(logical.Id, version.Id, hash, disposition);
    }

    private async Task<PublishedResource> PublishAgentAsync(
        AgentConfigurationDbContext db,
        AgentPackInstallationRecord installation,
        AgentPackVersionRecord packVersion,
        Dictionary<(string Kind, string Key), AgentPackManagedResourceRecord> managed,
        AgentPackAgentResourceDto resource,
        IReadOnlyDictionary<string, PublishedResource> prompts,
        TenantContext scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var managedKey = ("agent", resource.ResourceKey!);
        managed.TryGetValue(managedKey, out var management);
        AgentDefinitionRecord? logical = management is null
            ? null
            : await db.AgentDefinitions.SingleAsync(item => item.Id == management.LogicalEntityId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var wasCreated = logical is null;
        logical ??= new AgentDefinitionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            Slug = resource.Slug!, Status = "published", Revision = 0, Version = 0,
            CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
        };
        if (wasCreated) db.AgentDefinitions.Add(logical);
        Guid? basePromptId = resource.BasePromptPipelineRef is { Length: > 0 } promptRef
            ? prompts[ReferenceKey(promptRef, "prompt", $"agent '{resource.ResourceKey}' base_prompt_pipeline_ref")].LogicalId
            : null;
        var capabilities = resource.Capabilities.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var tools = resource.ToolScope.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var snapshotElement = JsonSerializer.SerializeToElement(new
        {
            id = logical.Id,
            slug = resource.Slug,
            display_name = resource.DisplayName,
            layer = resource.Layer,
            role = resource.Role,
            capabilities,
            tool_scope = tools,
            model_strategy = resource.ModelStrategy,
            system_prompt = resource.SystemPrompt,
            description = resource.Description,
            enabled = resource.Enabled
        }, JsonOptions);
        var snapshot = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(snapshotElement));
        var hash = Sha256Hex(Encoding.UTF8.GetBytes(snapshot));
        var latest = await db.AgentVersions.Where(item => item.AgentDefinitionId == logical.Id).OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        AgentVersionRecord version;
        var unchanged = latest is not null && string.Equals(latest.ContentHash, hash, StringComparison.OrdinalIgnoreCase) && string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(latest.SnapshotJson)), hash, StringComparison.OrdinalIgnoreCase);
        if (unchanged)
        {
            version = latest!;
        }
        else
        {
            version = new AgentVersionRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                AgentDefinitionId = logical.Id, Version = (latest?.Version ?? 0) + 1,
                Layer = resource.Layer!, Role = resource.Role!, SnapshotJson = snapshot,
                ContentHash = hash, ContentLength = Encoding.UTF8.GetByteCount(snapshot), Status = "published", Revision = 1,
                CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
            };
            db.AgentVersions.Add(version);
        }
        logical.DisplayName = resource.DisplayName!;
        logical.Description = resource.Description;
        logical.Layer = resource.Layer!;
        logical.Role = resource.Role!;
        logical.CapabilitiesJson = JsonSerializer.Serialize(capabilities, JsonOptions);
        logical.ToolScopeJson = JsonSerializer.Serialize(tools, JsonOptions);
        logical.ModelStrategyJson = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(resource.ModelStrategy));
        logical.SystemPrompt = resource.SystemPrompt;
        logical.Enabled = resource.Enabled;
        logical.BasePromptPipelineId = basePromptId;
        logical.SourceKind = "pack";
        logical.SourceKey = $"{installation.PackId}:{resource.ResourceKey}";
        logical.Managed = true;
        logical.Status = "published";
        logical.Version = version.Version;
        logical.Revision++;
        logical.UpdatedAt = now;
        logical.UpdatedByPrincipalId = scope.PrincipalId;
        var disposition = ResolveDisposition(wasCreated, unchanged);
        EnsureManaged(db, managed, installation, "agent", resource.ResourceKey!, logical.Id, scope, now);
        AddBinding(db, packVersion, "agent", resource.ResourceKey!, logical.Id, version.Id, hash, disposition, scope, now);
        return new PublishedResource(logical.Id, version.Id, hash, disposition);
    }

    private async Task<PublishedResource> PublishModeAsync(
        AgentConfigurationDbContext db,
        AgentPackInstallationRecord installation,
        AgentPackVersionRecord packVersion,
        Dictionary<(string Kind, string Key), AgentPackManagedResourceRecord> managed,
        AgentPackModeResourceDto resource,
        IReadOnlyList<AgentPackAgentResourceDto> agentResources,
        IReadOnlyDictionary<string, PublishedResource> agents,
        IReadOnlyDictionary<string, PublishedResource> prompts,
        TenantContext scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var managedKey = ("mode", resource.ResourceKey!);
        managed.TryGetValue(managedKey, out var management);
        AgentModeRecord? logical = management is null
            ? null
            : await db.AgentModes.SingleAsync(item => item.Id == management.LogicalEntityId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var wasCreated = logical is null;
        logical ??= new AgentModeRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            Slug = resource.Slug!, Status = "published", Revision = 0, Version = 0,
            CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
        };
        if (wasCreated) db.AgentModes.Add(logical);

        // Gate 2 (mode publish) binding-envelope rules over the pack's declared
        // bindings: envelope ⊆ template capabilities/tools, operation deny floor
        // on the effective surface. TOML-ceiling comparison runs authoritatively at
        // run freeze (ceilings are not available in the pack admission context).
        if (resource.Bindings is { Count: > 0 })
        {
            var nodeAgentKey = resource.Nodes.ToDictionary(
                item => item.NodeKey!,
                item => ReferenceKey(item.AgentRef!, "agent", $"mode '{resource.ResourceKey}' node '{item.NodeKey}' agent_ref"),
                StringComparer.Ordinal);
            var agentResourceByKey = agentResources.ToDictionary(item => item.ResourceKey!, StringComparer.Ordinal);
            var subjects = resource.Bindings!
                .Where(binding => binding.NodeKey is { Length: > 0 })
                .Select(binding =>
                {
                    var agentKey = nodeAgentKey[binding.NodeKey!];
                    var agent = agentResourceByKey[agentKey];
                    return new ModePublishGate.BindingSubject(
                        binding.NodeKey!, agent.Slug ?? agentKey, agent.Layer ?? string.Empty,
                        agent.Capabilities, agent.ToolScope, binding.ToolSwitches, binding.Envelope);
                }).ToArray();
            ModePublishGate.ValidateBindings(resource.ResourceKey!, subjects, ceilings: null);
        }

        var agentDefinitions = agentResources.ToDictionary(item => item.ResourceKey!, StringComparer.Ordinal);
        // Binding tool_switches are part of the effective tool surface (narrowing
        // only — ModePublishGate enforces the same view): a switched-off tool must
        // not survive into the frozen roster's authority.
        var switchesByNode = (resource.Bindings ?? [])
            .Where(binding => binding.NodeKey is { Length: > 0 }
                && binding.ToolSwitches is { } switches && switches.ValueKind == JsonValueKind.Object)
            .GroupBy(binding => binding.NodeKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ToolSwitches!.Value, StringComparer.Ordinal);
        // Mode-level prompt pipeline (source priority: mode > agent > workspace default).
        // Resolved once and preferred over every node's agent-level binding, because a
        // mode's pipeline describes the COLLABORATION SEMANTICS of that mode — it must
        // reach the conversation identity and the execution-layer workers alike, or the
        // four modes would keep sharing one set of instructions (what the mode contract
        // says they no longer do). Publishing it into each node's snapshot keeps the
        // frozen roster, the run bindings and the assembler source unchanged: the mode
        // version already carries a prompt version per node.
        PublishedResource? modePrompt = resource.PromptPipelineRef is { Length: > 0 } modePromptRef
            ? prompts[ReferenceKey(modePromptRef, "prompt", $"mode '{resource.ResourceKey}' prompt_pipeline_ref")]
            : null;
        var snapshotNodes = resource.Nodes.OrderBy(item => item.NodeKey, StringComparer.Ordinal).Select(node =>
        {
            var agentKey = ReferenceKey(node.AgentRef, "agent", $"mode '{resource.ResourceKey}' node '{node.NodeKey}' agent_ref");
            var agentResource = agentDefinitions[agentKey];
            var agent = agents[agentKey];
            PublishedResource? prompt = modePrompt ?? (agentResource.BasePromptPipelineRef is { Length: > 0 } promptRef
                ? prompts[ReferenceKey(promptRef, "prompt", $"agent '{agentResource.ResourceKey}' base_prompt_pipeline_ref")]
                : null);
            var effectiveTools = EffectiveTools(agentResource.ToolScope, node.Config);
            if (switchesByNode.TryGetValue(node.NodeKey!, out var switches))
            {
                effectiveTools = effectiveTools
                    .Where(tool => !IsSwitchedOff(switches, tool))
                    .ToArray();
            }
            return new
            {
                node_key = node.NodeKey,
                agent_definition_id = agent.LogicalId,
                agent_version_id = agent.VersionId,
                agent_version_hash = agent.ContentHash,
                layer = node.Layer,
                label = node.Label,
                config = NormalizeObject(node.Config),
                relationship = node.Relationship is { } relationshipElement ? (object?)NormalizeObject(relationshipElement) : null,
                effective_tools = effectiveTools,
                prompt_pipeline_id = prompt?.LogicalId,
                prompt_version_id = prompt?.VersionId,
                prompt_version_hash = prompt?.ContentHash
            };
        }).ToArray();
        var snapshotEdges = resource.Edges.OrderBy(item => item.EdgeKey, StringComparer.Ordinal).Select(edge => new
        {
            edge_key = edge.EdgeKey,
            source_node_key = edge.SourceNodeKey,
            target_node_key = edge.TargetNodeKey,
            condition = NormalizeObject(edge.Condition)
        }).ToArray();
        var agentKeyByNode = resource.Nodes.ToDictionary(
            item => item.NodeKey!,
            item => ReferenceKey(item.AgentRef!, "agent", $"mode '{resource.ResourceKey}' node '{item.NodeKey}' agent_ref"),
            StringComparer.Ordinal);
        var snapshotBindings = (resource.Bindings ?? []).OrderBy(item => item.NodeKey, StringComparer.Ordinal).Select(binding =>
        {
            // Node-less bindings attach an envelope to a spawnable template
            // (agent_ref only) — the free_form director's buildable workers are
            // not mode nodes, yet their frozen envelopes must ship with the mode.
            var agentRef = binding.NodeKey is { Length: > 0 } nodeKey
                ? agentKeyByNode[nodeKey]
                : ReferenceKey(RequiredText(binding.AgentRef, $"mode '{resource.ResourceKey}' binding agent_ref", 256), "agent", $"mode '{resource.ResourceKey}' node-less binding agent_ref");
            return new
            {
                node_key = binding.NodeKey,
                agent_ref = agentRef,
                duty_description_ref = binding.DutyDescriptionRef,
                tool_switches = binding.ToolSwitches is { } switchesElement ? (object?)NormalizeObject(switchesElement) : null,
                envelope = binding.Envelope is { } envelopeElement ? (object?)NormalizeObject(envelopeElement) : null,
                includes_core_reserved = binding.IncludesCoreReserved,
                instance_naming = binding.InstanceNaming is { } namingElement ? (object?)NormalizeObject(namingElement) : null,
            };
        }).ToArray();
        var snapshotElement = JsonSerializer.SerializeToElement(new
        {
            schema = "tinadec.mode_version/v1",
            mode = new { id = logical.Id, slug = resource.Slug, display_name = resource.DisplayName },
            nodes = snapshotNodes,
            edges = snapshotEdges,
            bindings = snapshotBindings,
            canvas_layout = NormalizeObject(resource.CanvasLayout)
        }, JsonOptions);
        var snapshot = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(snapshotElement));
        var hash = Sha256Hex(Encoding.UTF8.GetBytes(snapshot));
        var latest = await db.ModeVersions.Where(item => item.AgentModeId == logical.Id).OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        ModeVersionRecord version;
        var unchanged = latest is not null && string.Equals(latest.TopologyHash, hash, StringComparison.OrdinalIgnoreCase) && string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(latest.SnapshotJson ?? string.Empty)), hash, StringComparison.OrdinalIgnoreCase);
        if (unchanged)
        {
            version = latest!;
        }
        else
        {
            version = new ModeVersionRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                AgentModeId = logical.Id, Version = (latest?.Version ?? 0) + 1,
                SnapshotJson = snapshot, TopologyHash = hash, WarningJson = null,
                Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
            };
            db.ModeVersions.Add(version);
            await ReplaceModeProjectionAsync(db, logical.Id, resource, agents, scope, now, cancellationToken).ConfigureAwait(false);
        }
        logical.DisplayName = resource.DisplayName!;
        logical.Description = resource.Description;
        logical.Status = "published";
        logical.Version = version.Version;
        logical.Revision++;
        logical.UpdatedAt = now;
        logical.UpdatedByPrincipalId = scope.PrincipalId;
        var disposition = ResolveDisposition(wasCreated, unchanged);
        EnsureManaged(db, managed, installation, "mode", resource.ResourceKey!, logical.Id, scope, now);
        AddBinding(db, packVersion, "mode", resource.ResourceKey!, logical.Id, version.Id, hash, disposition, scope, now);
        return new PublishedResource(logical.Id, version.Id, hash, disposition);
    }

    private static async Task ReplaceModeProjectionAsync(
        AgentConfigurationDbContext db,
        Guid modeId,
        AgentPackModeResourceDto resource,
        IReadOnlyDictionary<string, PublishedResource> agents,
        TenantContext scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var oldNodes = await db.ModeNodes.Where(item => item.ModeId == modeId && item.Status != "archived").ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in oldNodes) { item.Status = "archived"; item.ArchivedAt = now; item.Revision++; item.UpdatedAt = now; }
        var oldEdges = await db.ModeEdges.Where(item => item.ModeId == modeId && item.Status != "archived").ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in oldEdges) { item.Status = "archived"; item.ArchivedAt = now; item.Revision++; item.UpdatedAt = now; }
        var oldLayouts = await db.CanvasLayouts.Where(item => item.ModeId == modeId && item.Status != "archived").ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in oldLayouts) { item.Status = "archived"; item.ArchivedAt = now; item.Revision++; item.UpdatedAt = now; }
        db.ModeNodes.AddRange(resource.Nodes.Select(node => new ModeNodeRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ModeId = modeId,
            NodeKey = node.NodeKey!,
            AgentDefinitionId = agents[ReferenceKey(node.AgentRef, "agent", $"mode '{resource.ResourceKey}' node '{node.NodeKey}' agent_ref")].LogicalId,
            Layer = node.Layer!, Label = node.Label,
            PositionJson = node.Position.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(node.Position)),
            ConfigJson = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(NormalizeObject(node.Config))),
            Status = "published", Revision = 1, CreatedAt = now, UpdatedAt = now
        }));
        db.ModeEdges.AddRange(resource.Edges.Select(edge => new ModeEdgeRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ModeId = modeId,
            EdgeKey = edge.EdgeKey!, SourceNodeKey = edge.SourceNodeKey!, TargetNodeKey = edge.TargetNodeKey!,
            ConditionJson = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(NormalizeObject(edge.Condition))),
            Status = "published", Revision = 1, CreatedAt = now, UpdatedAt = now
        }));
        db.CanvasLayouts.Add(new CanvasLayoutRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ModeId = modeId,
            LayoutJson = Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(NormalizeObject(resource.CanvasLayout))),
            Status = "published", Revision = 1, CreatedAt = now, UpdatedAt = now
        });
    }

    private static IReadOnlyList<string> EffectiveTools(IReadOnlyList<string> agentTools, JsonElement config)
    {
        var agent = agentTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "allowed_tools", "tool_enabled", "tools" })
                if (config.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array)
                    foreach (var value in values.EnumerateArray()) if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) mode.Add(value.GetString()!.Trim());
        }
        HashSet<string> effective;
        if (agent.Contains("*")) effective = mode.Count == 0 ? new HashSet<string>(["*"], StringComparer.OrdinalIgnoreCase) : mode;
        else if (mode.Count == 0) effective = agent;
        else effective = agent.Intersect(mode, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return effective.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static bool IsSwitchedOff(JsonElement switches, string toolId) =>
        switches.ValueKind == JsonValueKind.Object
        && switches.TryGetProperty(toolId, out var state)
        && (state.ValueKind == JsonValueKind.False
            || (state.ValueKind == JsonValueKind.String && string.Equals(state.GetString(), "false", StringComparison.OrdinalIgnoreCase)));

    private static JsonElement NormalizeObject(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value
        : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), JsonOptions);

    private static string ResolveDisposition(bool created, bool unchanged) => created ? "created" : unchanged ? "reused" : "updated";

    private static void EnsureManaged(
        AgentConfigurationDbContext db,
        Dictionary<(string Kind, string Key), AgentPackManagedResourceRecord> managed,
        AgentPackInstallationRecord installation,
        string kind,
        string key,
        Guid logicalId,
        TenantContext scope,
        DateTimeOffset now)
    {
        if (managed.ContainsKey((kind, key))) return;
        var record = new AgentPackManagedResourceRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            InstallationId = installation.Id, ResourceKind = kind, ResourceKey = key, LogicalEntityId = logicalId, CreatedAt = now
        };
        db.AgentPackManagedResources.Add(record);
        managed.Add((kind, key), record);
    }

    private static void AddBinding(
        AgentConfigurationDbContext db,
        AgentPackVersionRecord packVersion,
        string kind,
        string key,
        Guid logicalId,
        Guid versionId,
        string hash,
        string disposition,
        TenantContext scope,
        DateTimeOffset now) => db.AgentPackResourceBindings.Add(new AgentPackResourceBindingRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            PackVersionId = packVersion.Id, ResourceKind = kind, ResourceKey = key,
            LogicalEntityId = logicalId, VersionId = versionId, ContentHash = hash, Disposition = disposition, CreatedAt = now
        });

    private static bool ApplyWorkspaceDefaults(
        AgentConfigurationDbContext db,
        AgentPackInstallationRecord installation,
        AgentPackVersionRecord packVersion,
        PackPublications publications,
        AgentPackManifestDto manifest,
        WorkspaceDefaultsRecord? defaults,
        bool shouldApply,
        TenantContext scope,
        DateTimeOffset now,
        AgentPackDefaultAdoptionRecord? adoption = null)
    {
        var activation = manifest.Activation!.WorkspaceDefaults!;
        var targetAgent = publications.Agents[ReferenceKey(activation.AgentRef, "agent", "activation.workspace_defaults.agent_ref")];
        var targetMode = publications.Modes[ReferenceKey(activation.ModeRef, "mode", "activation.workspace_defaults.mode_ref")];
        var targetPrompt = publications.Prompts[ReferenceKey(activation.PromptPipelineRef, "prompt", "activation.workspace_defaults.prompt_pipeline_ref")];
        var previous = defaults;
        if (defaults is null)
        {
            defaults = new WorkspaceDefaultsRecord
            {
                TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                Status = "active", Revision = 0, CreatedAt = now, UpdatedAt = now
            };
            db.WorkspaceDefaults.Add(defaults);
            shouldApply = true;
        }
        var record = adoption ?? new AgentPackDefaultAdoptionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            InstallationId = installation.Id, PackVersionId = packVersion.Id,
            CreatedAt = now
        };
        record.PreviousAgentDefinitionId = previous?.DefaultAgentDefinitionId;
        record.PreviousAgentVersionId = previous?.DefaultAgentVersionId;
        record.PreviousAgentModeId = previous?.DefaultAgentModeId;
        record.PreviousModeVersionId = previous?.DefaultModeVersionId;
        record.PreviousPromptPipelineId = previous?.DefaultPromptPipelineId;
        record.PreviousPromptVersionId = previous?.DefaultPromptVersionId;
        record.AppliedAgentDefinitionId = shouldApply ? targetAgent.LogicalId : null;
        record.AppliedAgentVersionId = shouldApply ? targetAgent.VersionId : null;
        record.AppliedAgentModeId = shouldApply ? targetMode.LogicalId : null;
        record.AppliedModeVersionId = shouldApply ? targetMode.VersionId : null;
        record.AppliedPromptPipelineId = shouldApply ? targetPrompt.LogicalId : null;
        record.AppliedPromptVersionId = shouldApply ? targetPrompt.VersionId : null;
        if (adoption is null) db.AgentPackDefaultAdoptions.Add(record);
        if (!shouldApply) return false;
        defaults.DefaultAgentDefinitionId = targetAgent.LogicalId;
        defaults.DefaultAgentVersionId = targetAgent.VersionId;
        defaults.DefaultAgentModeId = targetMode.LogicalId;
        defaults.DefaultModeVersionId = targetMode.VersionId;
        defaults.DefaultPromptPipelineId = targetPrompt.LogicalId;
        defaults.DefaultPromptVersionId = targetPrompt.VersionId;
        defaults.Status = "active";
        defaults.ArchivedAt = null;
        defaults.Revision++;
        defaults.UpdatedAt = now;
        return true;
    }

    private static AgentPackResourceView ToResourceView(string kind, string key, PublishedResource resource) =>
        new(kind, key, resource.LogicalId, resource.VersionId, resource.ContentHash, resource.Disposition);

    private static AgentPackResourceView ToResourceView(AgentPackResourceBindingRecord resource) =>
        new(resource.ResourceKind, resource.ResourceKey, resource.LogicalEntityId, resource.VersionId, resource.ContentHash, resource.Disposition);

    private static string Sha256Hex(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    /// <summary>
    /// Wraps a manifest into an integrity-signed envelope using this Core's own
    /// RFC 8785 digest path, so deployment-supplied bootstrap manifests are
    /// validated exactly like an HTTP-submitted envelope.
    /// </summary>
    public static AgentPackEnvelopeDto CreateEnvelope(AgentPackManifestDto manifest)
    {
        var manifestElement = JsonSerializer.SerializeToElement(manifest, JsonOptions);
        var digest = Convert.ToHexString(SHA256.HashData(JsonCanonicalizer.Canonicalize(manifestElement))).ToLowerInvariant();
        return new AgentPackEnvelopeDto
        {
            Manifest = manifest,
            Integrity = new AgentPackIntegrityDto { Algorithm = "sha256", Digest = digest }
        };
    }

    private Task<ResourceAnalysis> AnalyzeResourcesAsync(AgentConfigurationDbContext db, AgentPackInstallationRecord? installation, ValidatedEnvelope envelope, CancellationToken cancellationToken) =>
        AnalyzeResourcesCoreAsync(db, installation, envelope, cancellationToken);

    private static ValidatedEnvelope ValidateEnvelope(AgentPackEnvelopeDto envelope) => ValidateEnvelopeCore(envelope);

    private static string ResolvePreviewAction(AgentPackVersionRecord? active, ValidatedEnvelope envelope)
    {
        if (active is null) return "install";
        var comparison = CompareSemVer(envelope.Version, active.PackVersion);
        if (comparison < 0) return "newer_installed";
        if (comparison == 0)
        {
            if (!string.Equals(active.ManifestHash, envelope.Digest, StringComparison.OrdinalIgnoreCase))
                throw Conflict("agent_pack_version_hash_conflict", "Agent pack version hash conflict", "The same pack version is already installed with a different manifest digest.");
            return "up_to_date";
        }
        return "upgrade";
    }

    private sealed record SemanticVersion(
        string Major,
        string Minor,
        string Patch,
        IReadOnlyList<string> Prerelease);

    private static int CompareSemVer(
        string left,
        string right,
        string leftField = "version",
        string rightField = "installed version")
    {
        var a = ParseSemVer(left, leftField);
        var b = ParseSemVer(right, rightField);
        foreach (var (leftIdentifier, rightIdentifier) in new[]
        {
            (a.Major, b.Major),
            (a.Minor, b.Minor),
            (a.Patch, b.Patch)
        })
        {
            var comparison = CompareNumericIdentifier(leftIdentifier, rightIdentifier);
            if (comparison != 0) return comparison;
        }

        if (a.Prerelease.Count == 0) return b.Prerelease.Count == 0 ? 0 : 1;
        if (b.Prerelease.Count == 0) return -1;
        var sharedCount = Math.Min(a.Prerelease.Count, b.Prerelease.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            var leftIdentifier = a.Prerelease[index];
            var rightIdentifier = b.Prerelease[index];
            var leftNumeric = IsNumericIdentifier(leftIdentifier);
            var rightNumeric = IsNumericIdentifier(rightIdentifier);
            int comparison;
            if (leftNumeric && rightNumeric)
            {
                comparison = CompareNumericIdentifier(leftIdentifier, rightIdentifier);
            }
            else if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = StringComparer.Ordinal.Compare(leftIdentifier, rightIdentifier);
            }
            if (comparison != 0) return comparison;
        }
        return a.Prerelease.Count.CompareTo(b.Prerelease.Count);
    }

    private static SemanticVersion ParseSemVer(string value, string field)
    {
        static AgentPackDomainException InvalidSemVer(string fieldName) =>
            new(400, "invalid_agent_pack_manifest", "Invalid agent pack", $"{fieldName} must be a SemVer 2.0 value.");

        if (string.IsNullOrEmpty(value)) throw InvalidSemVer(field);
        var buildSeparator = value.IndexOf('+');
        var precedence = buildSeparator < 0 ? value : value[..buildSeparator];
        if (buildSeparator >= 0)
        {
            var build = value[(buildSeparator + 1)..];
            if (!ValidIdentifiers(build, numericIdentifiersMayHaveLeadingZeros: true)) throw InvalidSemVer(field);
        }

        var prereleaseSeparator = precedence.IndexOf('-');
        var core = prereleaseSeparator < 0 ? precedence : precedence[..prereleaseSeparator];
        var prerelease = prereleaseSeparator < 0 ? string.Empty : precedence[(prereleaseSeparator + 1)..];
        var coreIdentifiers = core.Split('.');
        if (coreIdentifiers.Length != 3
            || coreIdentifiers.Any(identifier => !IsNumericIdentifier(identifier) || HasLeadingZero(identifier))
            || (prereleaseSeparator >= 0 && !ValidIdentifiers(prerelease, numericIdentifiersMayHaveLeadingZeros: false)))
        {
            throw InvalidSemVer(field);
        }

        return new SemanticVersion(
            coreIdentifiers[0],
            coreIdentifiers[1],
            coreIdentifiers[2],
            prereleaseSeparator < 0 ? [] : prerelease.Split('.'));
    }

    private static bool ValidIdentifiers(string value, bool numericIdentifiersMayHaveLeadingZeros)
    {
        var identifiers = value.Split('.');
        return identifiers.All(identifier => identifier.Length != 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (numericIdentifiersMayHaveLeadingZeros || !IsNumericIdentifier(identifier) || !HasLeadingZero(identifier)));
    }

    private static bool IsNumericIdentifier(string value) =>
        value.Length != 0 && value.All(char.IsAsciiDigit);

    private static bool HasLeadingZero(string value) =>
        value.Length > 1 && value[0] == '0';

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : StringComparer.Ordinal.Compare(left, right);
    }

    private static ValidatedEnvelope ValidateEnvelopeCore(AgentPackEnvelopeDto envelope)
    {
        var manifest = envelope.Manifest ?? throw InvalidException("manifest is required.");
        var integrity = envelope.Integrity ?? throw InvalidException("integrity is required.");
        if (!string.Equals(manifest.ApiVersion, ManifestApiVersion, StringComparison.Ordinal)) throw InvalidException($"api_version must be '{ManifestApiVersion}'.");
        if (!string.Equals(manifest.Kind, ManifestKind, StringComparison.Ordinal)) throw InvalidException($"kind must be '{ManifestKind}'.");
        var metadata = manifest.Metadata ?? throw InvalidException("metadata is required.");
        var packId = RequiredToken(metadata.PackId, "metadata.pack_id", 256);
        var owner = RequiredToken(metadata.Owner, "metadata.owner", 256);
        var productId = RequiredToken(metadata.ProductId, "metadata.product_id", 256);
        var name = RequiredText(metadata.Name, "metadata.name", 256);
        var version = RequiredText(metadata.Version, "metadata.version", 64);
        if (!string.Equals(metadata.Version, version, StringComparison.Ordinal))
            throw InvalidException("metadata.version must not contain leading or trailing whitespace.");
        ParseSemVer(version, "metadata.version");
        if (!string.Equals(integrity.Algorithm, "sha256", StringComparison.OrdinalIgnoreCase))
            throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack", "integrity.algorithm must be sha256.");
        var declaredDigest = RequiredText(integrity.Digest, "integrity.digest", 64).ToLowerInvariant();
        if (declaredDigest.Length != 64 || declaredDigest.Any(character => !Uri.IsHexDigit(character)))
            throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack", "integrity.digest must be a lowercase SHA-256 hex digest.");

        var compatibility = manifest.Compatibility ?? throw InvalidException("compatibility is required.");
        if (!string.IsNullOrWhiteSpace(compatibility.MinimumCoreVersion)
            && CompareSemVer(
                compatibility.MinimumCoreVersion!,
                CurrentCoreVersion,
                "compatibility.minimum_core_version",
                "current Core version") > 0)
            throw new AgentPackDomainException(422, "agent_pack_incompatible", "Agent pack is incompatible", $"Core {compatibility.MinimumCoreVersion} or later is required; current Core is {CurrentCoreVersion}.");
        var unknownCapabilities = compatibility.RequiredCoreCapabilities
            .Where(item => !SupportedCapabilities.Contains(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (unknownCapabilities.Length != 0)
            throw new AgentPackDomainException(422, "agent_pack_incompatible", "Agent pack is incompatible", $"Unsupported Core capabilities: {string.Join(", ", unknownCapabilities)}.");

        ValidateResources(manifest);
        var manifestElement = JsonSerializer.SerializeToElement(manifest, JsonOptions);
        var manifestCanonical = JsonCanonicalizer.Canonicalize(manifestElement);
        var computedDigest = Convert.ToHexString(SHA256.HashData(manifestCanonical)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(computedDigest), Convert.FromHexString(declaredDigest)))
            throw new AgentPackDomainException(422, "invalid_agent_pack_manifest", "Agent pack hash mismatch", "integrity.digest does not match the RFC 8785 canonical manifest SHA-256 digest.");
        var envelopeBytes = JsonCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(envelope, JsonOptions));
        return new ValidatedEnvelope(envelope, manifest, packId, owner, productId, name, version, computedDigest, envelopeBytes);
    }

    private static void ValidateResources(AgentPackManifestDto manifest)
    {
        var resources = manifest.Resources ?? throw InvalidException("resources is required.");
        if (resources.Agents.Count == 0) Invalid("resources.agents must contain at least one agent.");
        if (resources.PromptPipelines.Count == 0) Invalid("resources.prompt_pipelines must contain at least one prompt pipeline.");
        if (resources.Modes.Count == 0) Invalid("resources.modes must contain at least one mode.");
        EnsureUnique(resources.Agents.Select(item => RequiredToken(item.ResourceKey, "agent.resource_key", 256)), "agent resource_key");
        EnsureUnique(resources.Agents.Select(item => RequiredToken(item.Slug, "agent.slug", 128)), "agent slug");
        EnsureUnique(resources.PromptPipelines.Select(item => RequiredToken(item.ResourceKey, "prompt_pipeline.resource_key", 256)), "prompt pipeline resource_key");
        EnsureUnique(resources.PromptPipelines.Select(item => RequiredToken(item.Slug, "prompt_pipeline.slug", 128)), "prompt pipeline slug");
        EnsureUnique(resources.Modes.Select(item => RequiredToken(item.ResourceKey, "mode.resource_key", 256)), "mode resource_key");
        EnsureUnique(resources.Modes.Select(item => RequiredToken(item.Slug, "mode.slug", 128)), "mode slug");
        var agents = resources.Agents.ToDictionary(item => item.ResourceKey!, StringComparer.Ordinal);
        var prompts = resources.PromptPipelines.ToDictionary(item => item.ResourceKey!, StringComparer.Ordinal);

        foreach (var agent in resources.Agents)
        {
            RequiredText(agent.DisplayName, $"agent '{agent.ResourceKey}' display_name", 256);
            RequiredText(agent.Role, $"agent '{agent.ResourceKey}' role", 128);
            if (agent.Layer is not ("operation" or "execution")) Invalid($"agent '{agent.ResourceKey}' layer must be operation or execution.");
            ValidateStringSet(agent.Capabilities, $"agent '{agent.ResourceKey}' capabilities");
            ValidateStringSet(agent.ToolScope, $"agent '{agent.ResourceKey}' tool_scope");
            if (agent.ModelStrategy.ValueKind != JsonValueKind.Object) Invalid($"agent '{agent.ResourceKey}' model_strategy must be an object.");
            try { _ = ModelStrategyJson.Parse(agent.ModelStrategy); }
            catch (ArgumentException exception) { Invalid($"agent '{agent.ResourceKey}' {exception.Message}"); }
            if (agent.BasePromptPipelineRef is { Length: > 0 } promptRef)
            {
                var promptKey = ReferenceKey(promptRef, "prompt", $"agent '{agent.ResourceKey}' base_prompt_pipeline_ref");
                if (!prompts.ContainsKey(promptKey)) Invalid($"agent '{agent.ResourceKey}' references unknown prompt pipeline '{promptRef}'.");
            }
        }

        foreach (var prompt in resources.PromptPipelines)
        {
            RequiredText(prompt.DisplayName, $"prompt pipeline '{prompt.ResourceKey}' display_name", 256);
            ValidatePromptGraph(prompt.ResourceKey!, prompt.Graph);
        }

        foreach (var mode in resources.Modes)
        {
            RequiredText(mode.DisplayName, $"mode '{mode.ResourceKey}' display_name", 256);
            if (mode.Nodes.Count == 0) Invalid($"mode '{mode.ResourceKey}' must contain nodes.");
            EnsureUnique(mode.Nodes.Select(item => RequiredToken(item.NodeKey, $"mode '{mode.ResourceKey}' node_key", 128)), $"mode '{mode.ResourceKey}' node_key");
            EnsureUnique(mode.Edges.Select(item => RequiredToken(item.EdgeKey, $"mode '{mode.ResourceKey}' edge_key", 128)), $"mode '{mode.ResourceKey}' edge_key");
            // Mode-level prompt pipeline: typed reference to a pipeline declared by the
            // same pack. Same contract as an agent's base_prompt_pipeline_ref — the
            // "prompt:" prefix is mandatory and an unknown target fails closed. Absent
            // is legal and means "keep binding prompts per agent".
            if (mode.PromptPipelineRef is { Length: > 0 } modePromptRef)
            {
                var modePromptKey = ReferenceKey(modePromptRef, "prompt", $"mode '{mode.ResourceKey}' prompt_pipeline_ref");
                if (!prompts.ContainsKey(modePromptKey)) Invalid($"mode '{mode.ResourceKey}' references unknown prompt pipeline '{modePromptRef}'.");
            }

            // Graph semantics shared with manual mode publish (one implementation so
            // the two historical meeting-enforcement sites cannot drift): resolvable
            // conversation node, edge endpoint/self-loop checks, relationship files.
            // The legacy operation-layer-meeting + execution-count checks are folded
            // into the conversation-node and edge validation below.
            var agentRefs = resources.Agents
                .Select(item => new GraphValidation.AgentRef(item.ResourceKey!, item.Slug ?? string.Empty, item.Layer ?? string.Empty, item.Capabilities))
                .ToArray();
            var nodeRefs = mode.Nodes
                .Select(node => new GraphValidation.NodeRef(
                    node.NodeKey!, ReferenceKey(RequiredText(node.AgentRef, $"mode '{mode.ResourceKey}' node '{node.NodeKey}' agent_ref", 256), "agent", $"mode '{mode.ResourceKey}' node '{node.NodeKey}' agent_ref"),
                    node.Layer ?? string.Empty, node.Label, node.Config, node.Relationship))
                .ToArray();
            var edgeRefs = mode.Edges
                .Select(edge => new GraphValidation.EdgeRef(edge.EdgeKey!, edge.SourceNodeKey!, edge.TargetNodeKey!))
                .ToArray();
            foreach (var node in mode.Nodes)
            {
                if (node.Position.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)) Invalid($"mode '{mode.ResourceKey}' node '{node.NodeKey}' position must be an object or null.");
            }
            try { GraphValidation.ValidateModeGraph(mode.ResourceKey!, nodeRefs, edgeRefs, agentRefs); }
            catch (InvalidDataException graphError) { Invalid(graphError.Message); }
            // The free_form single-director shape is publishable: a mode with no
            // execution-layer node is legal when its relationship files declare a
            // spawnable agent_types whitelist (the director builds its execution
            // layer at runtime from the frozen whitelist).
            var declaresSpawnable = nodeRefs
                .Select(node => node.Relationship)
                .Where(relationship => relationship is { } relationshipElement && relationshipElement.ValueKind == JsonValueKind.Object)
                .SelectMany(relationship => relationship!.Value.TryGetProperty("agent_types", out var agentTypes) && agentTypes.ValueKind == JsonValueKind.Array
                    ? agentTypes.EnumerateArray()
                    : Enumerable.Empty<JsonElement>())
                .Any(entry => entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()));
            if (!nodeRefs.Any(node => string.Equals(node.Layer, "execution", StringComparison.Ordinal)) && !declaresSpawnable)
                Invalid($"mode '{mode.ResourceKey}' requires at least one execution-layer agent (or a relationship agent_types whitelist for a free-form director).");

            ValidateModeBindings(mode, resources.Agents.Select(agent => agent.Slug!).ToHashSet(StringComparer.Ordinal));
            foreach (var edge in mode.Edges)
            {
                if (edge.Condition.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) Invalid($"mode '{mode.ResourceKey}' edge '{edge.EdgeKey}' condition must be an object.");
            }
            if (mode.CanvasLayout.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) Invalid($"mode '{mode.ResourceKey}' canvas_layout must be an object.");
        }

        ValidateToolResources(manifest, resources, resources.Modes.Any(mode =>
            (mode.Bindings is { Count: > 0 })
            || mode.Nodes.Any(node => node.Relationship is { })));

        var defaults = manifest.Activation?.WorkspaceDefaults ?? throw InvalidException("activation.workspace_defaults is required.");
        if (!agents.ContainsKey(ReferenceKey(defaults.AgentRef, "agent", "activation.workspace_defaults.agent_ref"))) Invalid("activation workspace default agent_ref is unknown.");
        if (!resources.Modes.Any(item => item.ResourceKey == ReferenceKey(defaults.ModeRef, "mode", "activation.workspace_defaults.mode_ref"))) Invalid("activation workspace default mode_ref is unknown.");
        if (!prompts.ContainsKey(ReferenceKey(defaults.PromptPipelineRef, "prompt", "activation.workspace_defaults.prompt_pipeline_ref"))) Invalid("activation workspace default prompt_pipeline_ref is unknown.");
    }

    /// <summary>
    /// Optional per-node mode bindings (always-v1 additive surface). Structural
    /// checks only: node must exist, agent_ref (when present) must match the node's
    /// agent, envelope/tool_switches/instance_naming must be JSON objects. The
    /// narrowing-only envelope semantics are enforced by the mode publish gate
    /// (ModePublishGate), not here — pack admission has no TOML ceiling context.
    /// </summary>
    private static void ValidateModeBindings(AgentPackModeResourceDto mode, IReadOnlySet<string> packAgentSlugs)
    {
        if (mode.Bindings is not { Count: > 0 }) return;
        var nodeKeys = mode.Nodes.Select(item => item.NodeKey!).ToHashSet(StringComparer.Ordinal);
        var agentKeyByNode = mode.Nodes.ToDictionary(item => item.NodeKey!, item => ReferenceKey(item.AgentRef!, "agent", $"mode '{mode.ResourceKey}' node '{item.NodeKey}' agent_ref"), StringComparer.Ordinal);
        var packAgentKeys = packAgentSlugs.ToHashSet(StringComparer.Ordinal);
        EnsureUnique(mode.Bindings.Where(item => item.NodeKey is { Length: > 0 }).Select(item => item.NodeKey!), $"mode '{mode.ResourceKey}' binding node_key");
        foreach (var binding in mode.Bindings)
        {
            if (binding.NodeKey is not { Length: > 0 } nodeKey)
            {
                // Node-less binding: attaches an envelope to a spawnable template
                // (the free_form director's buildable workers are not mode nodes).
                if (binding.AgentRef is not { Length: > 0 } nodelessAgentRef)
                    throw InvalidException($"mode '{mode.ResourceKey}' node-less binding requires agent_ref.");
                var nodelessAgentKey = ReferenceKey(binding.AgentRef!, "agent", $"mode '{mode.ResourceKey}' node-less binding agent_ref");
                if (!packAgentKeys.Contains(nodelessAgentKey))
                    throw InvalidException($"mode '{mode.ResourceKey}' node-less binding agent_ref '{nodelessAgentRef}' does not reference an agent of this pack.");
            }
            else
            {
                if (!nodeKeys.Contains(nodeKey)) Invalid($"mode '{mode.ResourceKey}' binding references unknown node '{nodeKey}'.");
                if (binding.AgentRef is { Length: > 0 } bindingAgentRef)
                {
                    var bindingAgentKey = ReferenceKey(bindingAgentRef, "agent", $"mode '{mode.ResourceKey}' binding '{nodeKey}' agent_ref");
                    if (!string.Equals(bindingAgentKey, agentKeyByNode[nodeKey], StringComparison.Ordinal))
                        Invalid($"mode '{mode.ResourceKey}' binding '{nodeKey}' agent_ref does not match the node's agent.");
                }
            }
            if (binding.Envelope is { } envelopeElement && envelopeElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) Invalid($"mode '{mode.ResourceKey}' binding '{binding.NodeKey}' envelope must be an object.");
            if (binding.ToolSwitches is { } switchesElement && switchesElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) Invalid($"mode '{mode.ResourceKey}' binding '{binding.NodeKey}' tool_switches must be an object.");
            if (binding.InstanceNaming is { } namingElement && namingElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) Invalid($"mode '{mode.ResourceKey}' binding '{binding.NodeKey}' instance_naming must be an object or null.");
        }
    }

    /// <summary>
    /// Optional declared-tools section (always-v1 additive surface). Absent/empty
    /// keeps the legacy pure-id-reference behavior. Present, it requires the
    /// graph_mode_packs core capability so older Cores reject the pack fail-closed
    /// instead of silently ignoring semantics they cannot enforce. Reference tier
    /// pins the sha256 of the TinadecTools manifest entry (drift fails at freeze);
    /// definition tier is the controlled import registry — names/refs/schema only,
    /// inline secret values are rejected here and never reach storage.
    /// </summary>
    private static void ValidateToolResources(AgentPackManifestDto manifest, AgentPackResourcesDto resources, bool hasGraphPackSurface)
    {
        var hasTools = resources.Tools is { Count: > 0 };
        if (!hasTools && !hasGraphPackSurface) return;
        var required = manifest.Compatibility?.RequiredCoreCapabilities ?? [];
        if (!required.Contains(GraphValidation.GraphModePacksCapability, StringComparer.Ordinal))
            Invalid($"resources.tools (and mode bindings/relationship files) require compatibility.required_core_capabilities to declare '{GraphValidation.GraphModePacksCapability}'.");

        if (!hasTools) return;
        EnsureUnique(resources.Tools!.Select(item => RequiredToken(item.ToolId, "tool.tool_id", 128)), "tool tool_id");
        foreach (var tool in resources.Tools!)
        {
            switch (tool.Kind)
            {
                case "reference":
                    if (tool.EntryHash is not { Length: 64 } entryHash || entryHash.Any(character => !Uri.IsHexDigit(character)))
                        Invalid($"tool '{tool.ToolId}' reference entries must pin a 64-character sha256 entry_hash.");
                    break;
                case "definition":
                    RequiredText(tool.DisplayName, $"tool '{tool.ToolId}' display_name", 256);
                    if (tool.Definition is not { } definitionElement || definitionElement.ValueKind is not JsonValueKind.Object)
                        Invalid($"tool '{tool.ToolId}' definition must be an object.");
                    else
                        RejectInlineSecrets(tool.ToolId!, definitionElement);
                    break;
                default:
                    Invalid($"tool '{tool.ToolId}' kind must be 'reference' or 'definition'.");
                    break;
            }
        }
    }

    /// <summary>
    /// Tool-definition secret hygiene: definitions may carry names and references
    /// only. Secret VALUES live in ISecretStore / .tinadec/sandbox.json, never in
    /// the pack or in the tool_definitions rows.
    /// </summary>
    private static readonly HashSet<string> InlineSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    { "api_key", "secret", "secret_value", "password", "credential", "access_token", "private_key" };

    private static void RejectInlineSecrets(string toolId, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (InlineSecretKeys.Contains(property.Name)
                        && property.Value.ValueKind is JsonValueKind.String
                        && !string.IsNullOrEmpty(property.Value.GetString()))
                    {
                        Invalid($"tool '{toolId}' definition carries an inline secret value in '{property.Name}'; reference the secret by name instead.");
                    }
                    RejectInlineSecrets(toolId, property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) RejectInlineSecrets(toolId, item);
                break;
        }
    }

    private static void ValidatePromptGraph(string resourceKey, JsonElement graph)
    {
        if (graph.ValueKind != JsonValueKind.Object) Invalid($"prompt pipeline '{resourceKey}' graph must be an object.");
        if (!graph.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) Invalid($"prompt pipeline '{resourceKey}' graph.nodes must be an array.");
        if (!graph.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array) Invalid($"prompt pipeline '{resourceKey}' graph.edges must be an array.");
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            var key = node.TryGetProperty("node_key", out var nodeKey) ? nodeKey.GetString() : node.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (!keys.Add(RequiredToken(key, $"prompt pipeline '{resourceKey}' node key", 128))) Invalid($"prompt pipeline '{resourceKey}' contains a duplicate node key.");
            var kind = node.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : node.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            kinds.Add(RequiredToken(kind, $"prompt pipeline '{resourceKey}' node kind", 64));
        }
        if (!kinds.Contains("template") || !kinds.Contains("assemble")) Invalid($"prompt pipeline '{resourceKey}' requires template and assemble nodes.");
        foreach (var edge in edges.EnumerateArray())
        {
            var source = edge.TryGetProperty("source_node_key", out var sourceKey) ? sourceKey.GetString() : edge.TryGetProperty("source", out var sourceAlias) ? sourceAlias.GetString() : null;
            var target = edge.TryGetProperty("target_node_key", out var targetKey) ? targetKey.GetString() : edge.TryGetProperty("target", out var targetAlias) ? targetAlias.GetString() : null;
            if (!keys.Contains(RequiredToken(source, $"prompt pipeline '{resourceKey}' edge source", 128))
                || !keys.Contains(RequiredToken(target, $"prompt pipeline '{resourceKey}' edge target", 128))) Invalid($"prompt pipeline '{resourceKey}' edge references an unknown node.");
        }
    }

    private async Task<ResourceAnalysis> AnalyzeResourcesCoreAsync(AgentConfigurationDbContext db, AgentPackInstallationRecord? installation, ValidatedEnvelope envelope, CancellationToken cancellationToken)
    {
        var scope = _tenantAccessor.Current;
        var resources = envelope.Manifest.Resources!;
        var created = 0;
        var adopted = 0;
        var reused = 0;
        var updated = 0;
        var warnings = new List<string>();
        var conflicts = new List<string>();
        var differences = new List<AgentPackResourceView>();
        var managed = installation is null
            ? new Dictionary<(string Kind, string Key), AgentPackManagedResourceRecord>()
            : await db.AgentPackManagedResources.AsNoTracking().Where(item => item.InstallationId == installation.Id)
                .ToDictionaryAsync(item => (item.ResourceKind, item.ResourceKey), cancellationToken).ConfigureAwait(false);

        foreach (var agent in resources.Agents)
        {
            if (managed.TryGetValue(("agent", agent.ResourceKey!), out var managedAgent))
            {
                var current = await db.AgentDefinitions.AsNoTracking().SingleAsync(item => item.Id == managedAgent.LogicalEntityId, cancellationToken).ConfigureAwait(false);
                var disposition = AgentSemanticallyMatches(current, agent, includePromptText: true) ? "reused" : "updated";
                if (disposition == "reused") reused++; else updated++;
                differences.Add(new AgentPackResourceView("agent", agent.ResourceKey!, current.Id, null, null, disposition));
                continue;
            }
            created++;
            differences.Add(new AgentPackResourceView("agent", agent.ResourceKey!, null, null, null, "created"));
        }
        foreach (var prompt in resources.PromptPipelines)
        {
            if (managed.TryGetValue(("prompt_pipeline", prompt.ResourceKey!), out var managedPrompt))
            {
                var current = await db.PromptPipelines.AsNoTracking().SingleAsync(item => item.Id == managedPrompt.LogicalEntityId, cancellationToken).ConfigureAwait(false);
                var disposition = JsonSemanticEquals(current.GraphJson, prompt.Graph) ? "reused" : "updated";
                if (disposition == "reused") reused++; else updated++;
                differences.Add(new AgentPackResourceView("prompt_pipeline", prompt.ResourceKey!, current.Id, null, null, disposition));
                continue;
            }
            created++;
            differences.Add(new AgentPackResourceView("prompt_pipeline", prompt.ResourceKey!, null, null, null, "created"));
        }
        foreach (var mode in resources.Modes)
        {
            if (managed.TryGetValue(("mode", mode.ResourceKey!), out var managedMode))
            {
                updated++;
                differences.Add(new AgentPackResourceView("mode", mode.ResourceKey!, managedMode.LogicalEntityId, null, null, "updated"));
                continue;
            }
            created++;
            differences.Add(new AgentPackResourceView("mode", mode.ResourceKey!, null, null, null, "created"));
        }
        if (installation is not null && updated != 0) warnings.Add("Pack-managed resources will receive immutable versions only when their content changed.");
        return new ResourceAnalysis(new AgentPackCounts(resources.Agents.Count, resources.PromptPipelines.Count, resources.Modes.Count, created, adopted, reused, updated), differences, warnings, conflicts);
    }

    private async Task<bool> DefaultsWillAdoptAsync(
        AgentConfigurationDbContext db,
        AgentPackInstallationRecord? installation,
        AgentPackManifestDto manifest,
        CancellationToken cancellationToken)
    {
        var scope = _tenantAccessor.Current;
        var defaults = await db.WorkspaceDefaults.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (defaults is null) return true;
        if (installation is null)
        {
            // Automation stays conservative: only an empty workspace is filled by
            // an install. Re-pointing an already-configured workspace is the
            // explicit adopt-defaults operation, never an install side effect.
            return WorkspaceDefaultsAreEmpty(defaults);
        }
        if (installation.ActiveVersionId is not { } activeVersionId) return false;
        var prior = await db.AgentPackDefaultAdoptions.AsNoTracking().SingleOrDefaultAsync(item => item.InstallationId == installation.Id && item.PackVersionId == activeVersionId, cancellationToken).ConfigureAwait(false);
        return prior is not null
            && prior.AppliedModeVersionId.HasValue
            && defaults.DefaultAgentDefinitionId == prior.AppliedAgentDefinitionId
            && defaults.DefaultAgentVersionId == prior.AppliedAgentVersionId
            && defaults.DefaultAgentModeId == prior.AppliedAgentModeId
            && defaults.DefaultModeVersionId == prior.AppliedModeVersionId
            && defaults.DefaultPromptPipelineId == prior.AppliedPromptPipelineId
            && defaults.DefaultPromptVersionId == prior.AppliedPromptVersionId;
    }

    private static bool WorkspaceDefaultsAreEmpty(WorkspaceDefaultsRecord defaults) =>
        defaults.DefaultAgentDefinitionId is null
        && defaults.DefaultAgentVersionId is null
        && defaults.DefaultAgentModeId is null
        && defaults.DefaultModeVersionId is null
        && defaults.DefaultPromptPipelineId is null
        && defaults.DefaultPromptVersionId is null;

    private static bool AgentSemanticallyMatches(AgentDefinitionRecord record, AgentPackAgentResourceDto resource, bool includePromptText) =>
        string.Equals(record.DisplayName, resource.DisplayName, StringComparison.Ordinal)
        && string.Equals(record.Layer, resource.Layer, StringComparison.Ordinal)
        && string.Equals(record.Role, resource.Role, StringComparison.Ordinal)
        && JsonStringSetEquals(record.CapabilitiesJson, resource.Capabilities)
        && JsonStringSetEquals(record.ToolScopeJson, resource.ToolScope)
        && JsonSemanticEquals(record.ModelStrategyJson ?? "{\"kind\":\"inherit\"}", resource.ModelStrategy)
        && record.Enabled == resource.Enabled
        && (!includePromptText || (string.Equals(record.SystemPrompt, resource.SystemPrompt, StringComparison.Ordinal) && string.Equals(record.Description, resource.Description, StringComparison.Ordinal)));

    private static bool JsonStringSetEquals(string? json, IReadOnlyList<string> values)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return values.Count == 0;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("allowed_tools", out var allowed)) root = allowed;
            if (root.ValueKind != JsonValueKind.Array) return false;
            return root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(values);
        }
        catch { return false; }
    }

    private static bool JsonSemanticEquals(string json, JsonElement value)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonCanonicalizer.Canonicalize(document.RootElement).AsSpan().SequenceEqual(JsonCanonicalizer.Canonicalize(value));
        }
        catch { return false; }
    }

    private static bool JsonSemanticEquals(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonCanonicalizer.Canonicalize(leftDocument.RootElement).AsSpan().SequenceEqual(JsonCanonicalizer.Canonicalize(rightDocument.RootElement));
        }
        catch { return false; }
    }

    private static void ValidateStringSet(IReadOnlyList<string> values, string field)
    {
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Count) Invalid($"{field} must contain unique non-empty strings.");
    }

    private static void EnsureUnique(IEnumerable<string> values, string field)
    {
        var materialized = values.ToArray();
        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length) Invalid($"{field} values must be unique.");
    }

    private static string RequiredToken(string? value, string field, int maxLength)
    {
        var result = RequiredText(value, field, maxLength);
        if (result.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))) Invalid($"{field} contains unsupported characters.");
        if (Guid.TryParse(result, out _)) Invalid($"{field} must be a stable resource key, not a Core UUID.");
        return result;
    }

    private static string ReferenceKey(string? value, string expectedKind, string field)
    {
        var reference = RequiredText(value, field, 256);
        var prefix = $"{expectedKind}:";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            Invalid($"{field} must use the '{prefix}<resource_key>' reference format.");
        return RequiredToken(reference[prefix.Length..], $"{field} resource_key", 256 - prefix.Length);
    }

    private static string RequiredText(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) Invalid($"{field} is required.");
        var result = value.Trim();
        if (result.Length > maxLength) Invalid($"{field} exceeds {maxLength} characters.");
        return result;
    }

    [DoesNotReturn]
    private static void Invalid(string detail) => throw InvalidException(detail);

    private static AgentPackDomainException InvalidException(string detail) => new(400, "invalid_agent_pack_manifest", "Invalid agent pack", detail);
}

internal static class JsonCanonicalizer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false
    };

    public static byte[] Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) Write(writer, value);
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(FormatNumber(value), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack", "Undefined JSON values cannot be canonicalized.");
        }
    }

    private static string FormatNumber(JsonElement value)
    {
        if (value.TryGetInt64(out var integer) && Math.Abs((double)integer) <= 9007199254740991d) return integer.ToString(CultureInfo.InvariantCulture);
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new AgentPackDomainException(400, "invalid_agent_pack_manifest", "Invalid agent pack", "Manifest numbers must be finite IEEE-754 values.");
        if (number == 0d) return "0";
        var negative = number < 0;
        var raw = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture).ToUpperInvariant();
        var exponentIndex = raw.IndexOf('E');
        var exponent = exponentIndex >= 0 ? int.Parse(raw[(exponentIndex + 1)..], CultureInfo.InvariantCulture) : 0;
        var mantissa = exponentIndex >= 0 ? raw[..exponentIndex] : raw;
        var dotIndex = mantissa.IndexOf('.');
        var fractionalDigits = dotIndex >= 0 ? mantissa.Length - dotIndex - 1 : 0;
        var significant = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        var trailingZeros = significant.Length - significant.TrimEnd('0').Length;
        var digits = significant.TrimEnd('0');
        if (digits.Length == 0) return "0";
        var scientificExponent = exponent - fractionalDigits + trailingZeros + digits.Length - 1;
        string formatted;
        if (scientificExponent >= -6 && scientificExponent < 21)
        {
            var decimalPosition = scientificExponent + 1;
            formatted = decimalPosition <= 0
                ? "0." + new string('0', -decimalPosition) + digits
                : decimalPosition >= digits.Length
                    ? digits + new string('0', decimalPosition - digits.Length)
                    : digits[..decimalPosition] + "." + digits[decimalPosition..];
        }
        else
        {
            var coefficient = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            formatted = coefficient + "e" + (scientificExponent >= 0 ? "+" : string.Empty) + scientificExponent.ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + formatted : formatted;
    }
}
