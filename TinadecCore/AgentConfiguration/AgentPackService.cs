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
    DateTimeOffset UpdatedAt);

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

    public AgentPackService(
        IDbContextFactory<AgentConfigurationDbContext> factory,
        IContentStore contentStore,
        ITenantContextAccessor tenantAccessor)
    {
        _factory = factory;
        _contentStore = contentStore;
        _tenantAccessor = tenantAccessor;
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
        return installations.Select(item => ToInstallationView(item, item.ActiveVersionId is { } id && versions.TryGetValue(id, out var version) ? version : null)).ToArray();
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
        var query = from resource in db.AgentPackManagedResources.AsNoTracking()
                    join installation in db.AgentPackInstallations.AsNoTracking() on resource.InstallationId equals installation.Id
                    where resource.TenantId == scope.TenantId
                        && resource.WorkspaceId == scope.WorkspaceId
                        && resource.ResourceKind == resourceKind
                        && resource.LogicalEntityId == logicalEntityId
                        && installation.Status == "active"
                    select new AgentPackManagedResource(installation.PackId, resource.ResourceKind, resource.ResourceKey, resource.LogicalEntityId);
        return await query.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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

    private static AgentPackInstallationView ToInstallationView(AgentPackInstallationRecord installation, AgentPackVersionRecord? active) => new(
        installation.PackId,
        installation.Owner,
        installation.ProductId,
        installation.Name,
        installation.Status,
        active?.PackVersion,
        active?.ManifestHash,
        installation.Revision,
        installation.CreatedAt,
        installation.UpdatedAt);

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
            ? await FindAdoptablePromptAsync(db, scope, resource, cancellationToken).ConfigureAwait(false)
            : await db.PromptPipelines.SingleAsync(item => item.Id == management.LogicalEntityId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var wasCreated = logical is null;
        var wasAdopted = logical is not null && management is null;
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
        var disposition = ResolveDisposition(wasCreated, wasAdopted, unchanged);
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
            ? await FindAdoptableAgentAsync(db, scope, resource, cancellationToken).ConfigureAwait(false)
            : await db.AgentDefinitions.SingleAsync(item => item.Id == management.LogicalEntityId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var wasCreated = logical is null;
        var wasAdopted = logical is not null && management is null;
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
        var disposition = ResolveDisposition(wasCreated, wasAdopted, unchanged);
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
            ? await FindAdoptableModeAsync(db, scope, resource, agentResources, cancellationToken).ConfigureAwait(false)
            : await db.AgentModes.SingleAsync(item => item.Id == management.LogicalEntityId && item.TenantId == scope.TenantId && item.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var wasCreated = logical is null;
        var wasAdopted = logical is not null && management is null;
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
            var subjects = resource.Bindings!.Select(binding =>
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
        var snapshotNodes = resource.Nodes.OrderBy(item => item.NodeKey, StringComparer.Ordinal).Select(node =>
        {
            var agentKey = ReferenceKey(node.AgentRef, "agent", $"mode '{resource.ResourceKey}' node '{node.NodeKey}' agent_ref");
            var agentResource = agentDefinitions[agentKey];
            var agent = agents[agentKey];
            PublishedResource? prompt = agentResource.BasePromptPipelineRef is { Length: > 0 } promptRef
                ? prompts[ReferenceKey(promptRef, "prompt", $"agent '{agentResource.ResourceKey}' base_prompt_pipeline_ref")]
                : null;
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
                effective_tools = EffectiveTools(agentResource.ToolScope, node.Config),
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
        var snapshotBindings = (resource.Bindings ?? []).OrderBy(item => item.NodeKey, StringComparer.Ordinal).Select(binding => new
        {
            node_key = binding.NodeKey,
            agent_ref = agentKeyByNode[binding.NodeKey!],
            duty_description_ref = binding.DutyDescriptionRef,
            tool_switches = binding.ToolSwitches is { } switchesElement ? (object?)NormalizeObject(switchesElement) : null,
            envelope = binding.Envelope is { } envelopeElement ? (object?)NormalizeObject(envelopeElement) : null,
            includes_core_reserved = binding.IncludesCoreReserved,
            instance_naming = binding.InstanceNaming is { } namingElement ? (object?)NormalizeObject(namingElement) : null,
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
        var disposition = ResolveDisposition(wasCreated, wasAdopted, unchanged);
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

    private static JsonElement NormalizeObject(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value
        : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), JsonOptions);

    private static string ResolveDisposition(bool created, bool adopted, bool unchanged) => created ? "created" : adopted ? "adopted" : unchanged ? "reused" : "updated";

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
        DateTimeOffset now)
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
        db.AgentPackDefaultAdoptions.Add(new AgentPackDefaultAdoptionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            InstallationId = installation.Id, PackVersionId = packVersion.Id,
            PreviousAgentDefinitionId = previous?.DefaultAgentDefinitionId,
            PreviousAgentVersionId = previous?.DefaultAgentVersionId,
            PreviousAgentModeId = previous?.DefaultAgentModeId,
            PreviousModeVersionId = previous?.DefaultModeVersionId,
            PreviousPromptPipelineId = previous?.DefaultPromptPipelineId,
            PreviousPromptVersionId = previous?.DefaultPromptVersionId,
            AppliedAgentDefinitionId = shouldApply ? targetAgent.LogicalId : null,
            AppliedAgentVersionId = shouldApply ? targetAgent.VersionId : null,
            AppliedAgentModeId = shouldApply ? targetMode.LogicalId : null,
            AppliedModeVersionId = shouldApply ? targetMode.VersionId : null,
            AppliedPromptPipelineId = shouldApply ? targetPrompt.LogicalId : null,
            AppliedPromptVersionId = shouldApply ? targetPrompt.VersionId : null,
            CreatedAt = now
        });
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
            if (!nodeRefs.Any(node => string.Equals(node.Layer, "execution", StringComparison.Ordinal)))
                Invalid($"mode '{mode.ResourceKey}' requires at least one execution-layer agent.");

            ValidateModeBindings(mode);
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
    private static void ValidateModeBindings(AgentPackModeResourceDto mode)
    {
        if (mode.Bindings is not { Count: > 0 }) return;
        var nodeKeys = mode.Nodes.Select(item => item.NodeKey!).ToHashSet(StringComparer.Ordinal);
        var agentKeyByNode = mode.Nodes.ToDictionary(item => item.NodeKey!, item => ReferenceKey(item.AgentRef!, "agent", $"mode '{mode.ResourceKey}' node '{item.NodeKey}' agent_ref"), StringComparer.Ordinal);
        EnsureUnique(mode.Bindings.Select(item => RequiredToken(item.NodeKey, $"mode '{mode.ResourceKey}' binding node_key", 128)), $"mode '{mode.ResourceKey}' binding node_key");
        foreach (var binding in mode.Bindings)
        {
            if (!nodeKeys.Contains(binding.NodeKey!)) Invalid($"mode '{mode.ResourceKey}' binding references unknown node '{binding.NodeKey}'.");
            if (binding.AgentRef is { Length: > 0 } bindingAgentRef)
            {
                var bindingAgentKey = ReferenceKey(bindingAgentRef, "agent", $"mode '{mode.ResourceKey}' binding '{binding.NodeKey}' agent_ref");
                if (!string.Equals(bindingAgentKey, agentKeyByNode[binding.NodeKey!], StringComparison.Ordinal))
                    Invalid($"mode '{mode.ResourceKey}' binding '{binding.NodeKey}' agent_ref does not match the node's agent.");
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
            var adoptable = await FindAdoptableAgentAsync(db, scope, agent, cancellationToken).ConfigureAwait(false);
            if (adoptable is not null) { adopted++; differences.Add(new AgentPackResourceView("agent", agent.ResourceKey!, adoptable.Id, null, null, "adopted")); continue; }
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
            var adoptable = await FindAdoptablePromptAsync(db, scope, prompt, cancellationToken).ConfigureAwait(false);
            if (adoptable is not null) { adopted++; differences.Add(new AgentPackResourceView("prompt_pipeline", prompt.ResourceKey!, adoptable.Id, null, null, "adopted")); continue; }
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
            var adoptable = await FindAdoptableModeAsync(db, scope, mode, resources.Agents, cancellationToken).ConfigureAwait(false);
            if (adoptable is not null) { adopted++; differences.Add(new AgentPackResourceView("mode", mode.ResourceKey!, adoptable.Id, null, null, "adopted")); continue; }
            created++;
            differences.Add(new AgentPackResourceView("mode", mode.ResourceKey!, null, null, null, "created"));
        }
        if (installation is not null && updated != 0) warnings.Add("Pack-managed resources will receive immutable versions only when their content changed.");
        return new ResourceAnalysis(new AgentPackCounts(resources.Agents.Count, resources.PromptPipelines.Count, resources.Modes.Count, created, adopted, reused, updated), differences, warnings, conflicts);
    }

    private static async Task<AgentDefinitionRecord?> FindAdoptableAgentAsync(
        AgentConfigurationDbContext db,
        TenantContext scope,
        AgentPackAgentResourceDto resource,
        CancellationToken cancellationToken)
    {
        var candidates = await db.AgentDefinitions
            .Where(item => item.TenantId == scope.TenantId
                && item.WorkspaceId == scope.WorkspaceId
                && item.Slug == resource.Slug
                && item.Status != "archived"
                && item.SourceKind != "bootstrap"
                && item.SourceKind != "pack")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (await IsLegacySeedAgentAsync(db, candidate, resource, cancellationToken).ConfigureAwait(false)) return candidate;
        }
        return null;
    }

    private static async Task<PromptPipelineRecord?> FindAdoptablePromptAsync(
        AgentConfigurationDbContext db,
        TenantContext scope,
        AgentPackPromptPipelineResourceDto resource,
        CancellationToken cancellationToken)
    {
        var candidates = await db.PromptPipelines
            .Where(item => item.TenantId == scope.TenantId
                && item.WorkspaceId == scope.WorkspaceId
                && item.Slug == resource.Slug
                && item.Status != "archived")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (await IsBootstrapPromptAsync(db, candidate.Id, cancellationToken).ConfigureAwait(false)) continue;
            if (await IsLegacySeedPromptAsync(db, candidate, cancellationToken).ConfigureAwait(false)) return candidate;
        }
        return null;
    }

    private static async Task<AgentModeRecord?> FindAdoptableModeAsync(
        AgentConfigurationDbContext db,
        TenantContext scope,
        AgentPackModeResourceDto resource,
        IReadOnlyList<AgentPackAgentResourceDto> agents,
        CancellationToken cancellationToken)
    {
        var candidates = await db.AgentModes
            .Where(item => item.TenantId == scope.TenantId
                && item.WorkspaceId == scope.WorkspaceId
                && item.Slug == resource.Slug
                && item.Status != "archived")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (await IsBootstrapModeAsync(db, candidate.Id, cancellationToken).ConfigureAwait(false)) continue;
            if (await IsLegacySeedModeAsync(db, candidate, resource, agents, cancellationToken).ConfigureAwait(false)) return candidate;
        }
        return null;
    }

    private static Task<bool> IsBootstrapPromptAsync(AgentConfigurationDbContext db, Guid promptPipelineId, CancellationToken cancellationToken) =>
        db.AgentDefinitions.AsNoTracking().AnyAsync(
            item => item.BasePromptPipelineId == promptPipelineId && item.SourceKind == "bootstrap",
            cancellationToken);

    private static async Task<bool> IsBootstrapModeAsync(AgentConfigurationDbContext db, Guid modeId, CancellationToken cancellationToken)
    {
        var definitionIds = await db.ModeNodes.AsNoTracking()
            .Where(item => item.ModeId == modeId && item.Status != "archived")
            .Select(item => item.AgentDefinitionId)
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return definitionIds.Length != 0 && await db.AgentDefinitions.AsNoTracking().AnyAsync(
            item => definitionIds.Contains(item.Id) && item.SourceKind == "bootstrap",
            cancellationToken).ConfigureAwait(false);
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
            if (WorkspaceDefaultsAreEmpty(defaults)) return true;
            if (await IsBootstrapWorkspaceDefaultsAsync(db, defaults, manifest, cancellationToken).ConfigureAwait(false)) return true;
            return await IsLegacySeedWorkspaceDefaultsAsync(db, defaults, manifest, cancellationToken).ConfigureAwait(false);
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

    private static async Task<bool> IsBootstrapWorkspaceDefaultsAsync(
        AgentConfigurationDbContext db,
        WorkspaceDefaultsRecord defaults,
        AgentPackManifestDto manifest,
        CancellationToken cancellationToken)
    {
        if (defaults.Status != "active" || defaults.ArchivedAt is not null
            || defaults.DefaultAgentDefinitionId is not { } agentId
            || defaults.DefaultAgentVersionId is not { } agentVersionId
            || defaults.DefaultAgentModeId is not { } modeId
            || defaults.DefaultModeVersionId is not { } modeVersionId
            || defaults.DefaultPromptPipelineId is not { } promptId
            || defaults.DefaultPromptVersionId is not { } promptVersionId)
            return false;

        var activation = manifest.Activation!.WorkspaceDefaults!;
        var expectedAgentKey = ReferenceKey(activation.AgentRef, "agent", "activation.workspace_defaults.agent_ref");
        var expectedModeKey = ReferenceKey(activation.ModeRef, "mode", "activation.workspace_defaults.mode_ref");
        var agent = await db.AgentDefinitions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == agentId, cancellationToken).ConfigureAwait(false);
        var mode = await db.AgentModes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == modeId, cancellationToken).ConfigureAwait(false);
        var prompt = await db.PromptPipelines.AsNoTracking().SingleOrDefaultAsync(item => item.Id == promptId, cancellationToken).ConfigureAwait(false);
        return agent is not null && mode is not null && prompt is not null
            && agent.SourceKind == "bootstrap" && agent.SourceKey == expectedAgentKey
            && mode.Slug == expectedModeKey && prompt.Slug == "baseline-prompt"
            && await db.AgentVersions.AsNoTracking().AnyAsync(item => item.Id == agentVersionId && item.AgentDefinitionId == agentId && item.Status == "published", cancellationToken).ConfigureAwait(false)
            && await db.ModeVersions.AsNoTracking().AnyAsync(item => item.Id == modeVersionId && item.AgentModeId == modeId && item.Status == "published", cancellationToken).ConfigureAwait(false)
            && await db.PromptVersions.AsNoTracking().AnyAsync(item => item.Id == promptVersionId && item.PromptPipelineId == promptId && item.Status == "published", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsLegacySeedWorkspaceDefaultsAsync(
        AgentConfigurationDbContext db,
        WorkspaceDefaultsRecord defaults,
        AgentPackManifestDto manifest,
        CancellationToken cancellationToken)
    {
        if (defaults.Status != "active" || defaults.Revision != 1 || defaults.ArchivedAt is not null
            || defaults.DefaultAgentVersionId is not null || defaults.DefaultModeVersionId is not null || defaults.DefaultPromptVersionId is not null
            || defaults.DefaultAgentDefinitionId is not { } agentId || defaults.DefaultAgentModeId is not { } modeId || defaults.DefaultPromptPipelineId is not { } promptId)
            return false;

        var resources = manifest.Resources!;
        var activation = manifest.Activation!.WorkspaceDefaults!;
        var targetAgent = resources.Agents.Single(item => item.ResourceKey == ReferenceKey(activation.AgentRef, "agent", "activation.workspace_defaults.agent_ref"));
        var targetMode = resources.Modes.Single(item => item.ResourceKey == ReferenceKey(activation.ModeRef, "mode", "activation.workspace_defaults.mode_ref"));
        var agent = await db.AgentDefinitions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == agentId, cancellationToken).ConfigureAwait(false);
        var mode = await db.AgentModes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == modeId, cancellationToken).ConfigureAwait(false);
        var prompt = await db.PromptPipelines.AsNoTracking().SingleOrDefaultAsync(item => item.Id == promptId, cancellationToken).ConfigureAwait(false);
        return agent is not null && mode is not null && prompt is not null
            && await IsLegacySeedAgentAsync(db, agent, targetAgent, cancellationToken).ConfigureAwait(false)
            && await IsLegacySeedPromptAsync(db, prompt, cancellationToken).ConfigureAwait(false)
            && await IsLegacySeedModeAsync(db, mode, targetMode, resources.Agents, cancellationToken).ConfigureAwait(false);
    }

    private static bool AgentSemanticallyMatches(AgentDefinitionRecord record, AgentPackAgentResourceDto resource, bool includePromptText) =>
        string.Equals(record.DisplayName, resource.DisplayName, StringComparison.Ordinal)
        && string.Equals(record.Layer, resource.Layer, StringComparison.Ordinal)
        && string.Equals(record.Role, resource.Role, StringComparison.Ordinal)
        && JsonStringSetEquals(record.CapabilitiesJson, resource.Capabilities)
        && JsonStringSetEquals(record.ToolScopeJson, resource.ToolScope)
        && JsonSemanticEquals(record.ModelStrategyJson ?? "{\"kind\":\"inherit\"}", resource.ModelStrategy)
        && record.Enabled == resource.Enabled
        && (!includePromptText || (string.Equals(record.SystemPrompt, resource.SystemPrompt, StringComparison.Ordinal) && string.Equals(record.Description, resource.Description, StringComparison.Ordinal)));

    private static async Task<bool> IsLegacySeedAgentAsync(AgentConfigurationDbContext db, AgentDefinitionRecord record, AgentPackAgentResourceDto resource, CancellationToken cancellationToken)
    {
        if (record.Version != 1 || record.Status != "published" || !LegacyAgentSlugs.Contains(record.Slug) || !AgentSemanticallyMatches(record, resource, includePromptText: false)) return false;
        return await db.AgentVersions.AsNoTracking().CountAsync(item => item.AgentDefinitionId == record.Id, cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> IsLegacySeedPromptAsync(AgentConfigurationDbContext db, PromptPipelineRecord record, CancellationToken cancellationToken)
    {
        const string legacyGraph = "{\"nodes\":[{\"id\":\"template\",\"type\":\"template\"},{\"id\":\"assemble\",\"type\":\"assemble\"}],\"edges\":[{\"source\":\"template\",\"target\":\"assemble\"}]}";
        if (record.Version != 1 || record.Status != "published" || record.Slug != "baseline-prompt" || !JsonSemanticEquals(record.GraphJson, legacyGraph)) return false;
        var versions = await db.PromptVersions.AsNoTracking().Where(item => item.PromptPipelineId == record.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return versions.Count == 1 && versions[0].Version == 1 && versions[0].Status == "published" && JsonSemanticEquals(versions[0].GraphJson, legacyGraph);
    }

    private static async Task<bool> IsLegacySeedModeAsync(AgentConfigurationDbContext db, AgentModeRecord record, AgentPackModeResourceDto resource, IReadOnlyList<AgentPackAgentResourceDto> agents, CancellationToken cancellationToken)
    {
        if (record.Version != 1 || record.Status != "published" || record.Slug != "default-mode"
            || record.DisplayName != resource.DisplayName || record.Description != resource.Description) return false;
        var definitions = await db.AgentDefinitions.AsNoTracking().Where(item => item.TenantId == record.TenantId && item.WorkspaceId == record.WorkspaceId).ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
        var nodes = await db.ModeNodes.AsNoTracking().Where(item => item.ModeId == record.Id && item.Status == "published").ToListAsync(cancellationToken).ConfigureAwait(false);
        var expected = resource.Nodes
            .Select(item => (item.NodeKey!, ReferenceKey(item.AgentRef, "agent", $"mode '{resource.ResourceKey}' node '{item.NodeKey}' agent_ref"), item.Layer!))
            .OrderBy(item => item.Item1, StringComparer.Ordinal)
            .ToArray();
        var actual = nodes.Where(item => definitions.ContainsKey(item.AgentDefinitionId)).Select(item => (item.NodeKey, definitions[item.AgentDefinitionId].Slug, item.Layer)).OrderBy(item => item.NodeKey, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual)) return false;
        if (await db.ModeEdges.AsNoTracking().AnyAsync(item => item.ModeId == record.Id && item.Status == "published", cancellationToken).ConfigureAwait(false)) return false;
        return await db.ModeVersions.AsNoTracking().CountAsync(item => item.AgentModeId == record.Id && item.Version == 1 && item.Status == "published", cancellationToken).ConfigureAwait(false) == 1
            && await db.ModeVersions.AsNoTracking().CountAsync(item => item.AgentModeId == record.Id, cancellationToken).ConfigureAwait(false) == 1;
    }

    private static readonly HashSet<string> LegacyAgentSlugs = new(StringComparer.Ordinal)
    {
        "meeting", "context_compressor", "skill_recommender", "supervisor", "evolution", "git_steward", "task_planner",
        "worker.code", "worker.document", "worker.data", "worker.browser", "worker.file", "worker.general", "worker.git"
    };

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
