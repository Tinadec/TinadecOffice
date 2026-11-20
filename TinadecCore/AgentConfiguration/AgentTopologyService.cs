using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AgentConfiguration;

/// <summary>
/// Stable machine codes the topology service answers with. They travel unchanged
/// through HTTP, the Gateway and the <c>tinadec_*</c> tools so a client branches on
/// one vocabulary instead of on English prose.
/// </summary>
public static class AgentTopologyCodes
{
    public const string BuiltInReadOnly = "built_in_read_only";
    public const string NotFound = "not_found";
    public const string NotDraft = "not_draft";
    public const string Conflict = "conflict";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidRuntimePolicy = "invalid_runtime_policy";
    public const string InvalidLayer = "invalid_layer";
    public const string InvalidModelStrategy = "invalid_model_strategy";
    public const string EdgeKeyRequired = "edge_key_required";
    public const string NodeKeyRequired = "node_key_required";
    public const string EdgeEndpointsRequired = "edge_endpoints_required";
    public const string NodeAgentRequired = "agent_definition_id_required";
    public const string ModeGraphInvalid = "mode_graph_invalid";
    /// <summary>
    /// The mode's bindings or effective tool surface failed the same gate a pack publish
    /// runs (<see cref="ModePublishGate"/> / <see cref="ExplorationToolPolicy"/>). Carried
    /// as its own code so a caller can tell "the graph is malformed" from "the arms are
    /// over-declared".
    /// </summary>
    public const string ModePublishRejected = "mode_publish_rejected";
    public const string PublishedVersionExists = "published_version_exists";
    public const string DuplicateSlug = "duplicate_slug";
}

/// <summary>One structured reason a topology write was refused or published with a caveat.</summary>
public sealed record AgentTopologyDiagnostic(string Code, string Message);

/// <summary>
/// Outcome of a topology read or write. Failures carry the HTTP status the endpoint
/// maps and the code the tool returns; successes carry the same projection
/// <c>GET /agent-modes/{id}</c> serves so the two callers cannot drift.
/// </summary>
public sealed class AgentTopologyResult
{
    public bool Ok { get; init; }
    public int StatusCode { get; init; } = AgentTopologyStatus.Ok;
    public string? Code { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<AgentTopologyDiagnostic> Diagnostics { get; init; } = [];
    public Guid? ModeId { get; init; }
    public JsonElement? Mode { get; init; }
    public int? Version { get; init; }
    public long? Revision { get; init; }
    public string? TopologyHash { get; init; }
    public IReadOnlyList<JsonElement> Warnings { get; init; } = [];

    public static AgentTopologyResult Failure(int status, string code, string message, params AgentTopologyDiagnostic[] diagnostics) =>
        new() { Ok = false, StatusCode = status, Code = code, Message = message, Diagnostics = diagnostics };
}

internal static class AgentTopologyStatus
{
    public const int Ok = 200;
    public const int BadRequest = 400;
    public const int NotFound = 404;
    public const int Conflict = 409;
    public const int PreconditionFailed = 412;
}

/// <summary>
/// The one draft/publish implementation for agent-mode topology. Both the HTTP
/// endpoints and the <c>tinadec_mode_*</c> tools call it; the editor's write path and
/// the tool's write path must never be two different publish rules.
///
/// Draft writes are delete-aware: nodes and edges absent from the submitted arrays
/// are removed from the DRAFT projection (published rows are never touched), and an
/// edge without its own <c>edge_key</c> is refused instead of being minted a fresh
/// GUID — that minting is what duplicated every edge on every save.
/// </summary>
public interface IAgentTopologyService
{
    Task<AgentTopologyResult> GetAsync(Guid modeId, CancellationToken cancellationToken = default);
    Task<AgentTopologyResult> CreateDraftAsync(JsonElement payload, CancellationToken cancellationToken = default);
    Task<AgentTopologyResult> CloneDraftAsync(Guid sourceModeId, JsonElement payload, CancellationToken cancellationToken = default);
    Task<AgentTopologyResult> UpsertDraftAsync(Guid modeId, JsonElement payload, long expectedRevision, CancellationToken cancellationToken = default);
    Task<AgentTopologyResult> DeleteDraftAsync(Guid modeId, CancellationToken cancellationToken = default);
    Task<AgentTopologyResult> PublishAsync(Guid modeId, long expectedRevision, CancellationToken cancellationToken = default);
}

public sealed class AgentTopologyService : IAgentTopologyService
{
    private readonly IDbContextFactory<AgentConfigurationDbContext> _factory;
    private readonly ITenantContextAccessor _tenant;
    private readonly IAgentPackService _packs;

    public AgentTopologyService(
        IDbContextFactory<AgentConfigurationDbContext> factory,
        ITenantContextAccessor tenant,
        IAgentPackService packs)
    {
        _factory = factory;
        _tenant = tenant;
        _packs = packs;
    }

    // ── reads ──

    public async Task<AgentTopologyResult> GetAsync(Guid modeId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // AsNoTracking on the whole read: the snapshot backfill below mutates the projected
        // objects, and a read must never be able to persist that repair as a side effect.
        var mode = await db.AgentModes.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
        if (mode is null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.NotFound, AgentTopologyCodes.NotFound, "Mode not found.");
        if (string.Equals(mode.Status, "archived", StringComparison.OrdinalIgnoreCase))
            return AgentTopologyResult.Failure(AgentTopologyStatus.NotFound, AgentTopologyCodes.NotFound, "Mode is archived.");

        // Read returns the current topology for any live status: draft edits read the
        // draft projection; published/managed modes read the published projection. The
        // draft-only guard belongs on writes, not on this read.
        var nodeStatus = string.Equals(mode.Status, "draft", StringComparison.OrdinalIgnoreCase) ? "draft" : "published";
        var nodes = await db.ModeNodes.AsNoTracking().Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == nodeStatus)
            .OrderBy(x => x.NodeKey).ToListAsync(cancellationToken);
        var edges = await db.ModeEdges.AsNoTracking().Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == nodeStatus)
            .OrderBy(x => x.EdgeKey).ToListAsync(cancellationToken);
        var layout = await db.CanvasLayouts.AsNoTracking().FirstOrDefaultAsync(
            x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == nodeStatus, cancellationToken);
        var managed = await _packs.FindManagedResourceAsync("mode", modeId, cancellationToken) is not null;

        // Retroactive repair of modes installed before the row projection carried these
        // documents: backfill the mode's team bindings and each node's relationship file
        // from the newest frozen snapshot so a reader sees what the pack published
        // without reinstalling it. Projection-only by design — a read never writes.
        // The snapshot query is skipped entirely when the projection is already complete,
        // which is every mode published by the current build: the repair costs one extra
        // read only for the modes that actually need it.
        if (NeedsSnapshotBackfill(mode, nodes))
        {
            var repaired = await SnapshotBackfillAsync(db, modeId, scope.TenantId, scope.WorkspaceId, cancellationToken);
            if (repaired is not null)
            {
                mode.BindingsJson = BackfillDocument(mode.BindingsJson, repaired.BindingsJson);
                mode.Instructions = BackfillDocument(mode.Instructions, repaired.Instructions);
                mode.OrchestrationTier = BackfillDocument(mode.OrchestrationTier, repaired.OrchestrationTier);
                foreach (var node in nodes)
                    node.RelationshipJson = BackfillDocument(node.RelationshipJson, repaired.RelationshipFor(node.NodeKey));
            }
        }
        return new AgentTopologyResult
        {
            Ok = true,
            ModeId = mode.Id,
            Revision = mode.Revision,
            Version = mode.Version,
            Mode = ProjectDetail(mode, nodes, edges, layout, managed),
        };
    }

    /// <summary>
    /// The documents a frozen snapshot still holds but a pre-fix row projection lost.
    /// Every member is raw JSON text, so a backfilled value is byte-comparable with
    /// what the pack manifest declared instead of a re-serialization of a different shape.
    /// </summary>
    private sealed record ModeSnapshotBackfill(string? BindingsJson, string? Instructions, string? OrchestrationTier, IReadOnlyDictionary<string, string> RelationshipsByNodeKey)
    {
        public string? RelationshipFor(string nodeKey) =>
            RelationshipsByNodeKey.TryGetValue(nodeKey, out var relationship) ? relationship : null;
    }

    private static string? BackfillDocument(string? current, string? snapshotValue) =>
        string.IsNullOrWhiteSpace(current) ? snapshotValue : current;

    /// <summary>
    /// True when the stored projection is missing a document the frozen snapshot may still
    /// hold. Keyed on the mode's bindings, instructions and tier plus every node's
    /// relationship: a complete projection costs nothing, an incomplete one costs one extra
    /// read. A node with no relationship in either place is indistinguishable from a node
    /// whose pack declared none, so an empty relationship alone does not trigger the repair
    /// unless the mode-level documents are also missing.
    /// </summary>
    private static bool NeedsSnapshotBackfill(AgentModeRecord mode, IReadOnlyList<ModeNodeRecord> nodes) =>
        string.IsNullOrWhiteSpace(mode.BindingsJson)
        || string.IsNullOrWhiteSpace(mode.Instructions)
        || string.IsNullOrWhiteSpace(mode.OrchestrationTier)
        || nodes.Any(node => string.IsNullOrWhiteSpace(node.RelationshipJson));

    /// <summary>
    /// Reads the newest <see cref="ModeVersionRecord"/> of a mode and extracts the three
    /// documents the pre-fix projection never persisted: the mode-level bindings array,
    /// the team instructions and the explicit tier, plus each node's relationship file.
    /// Returns null when the mode has no snapshot (never published) or the snapshot is
    /// unreadable — a corrupt snapshot degrades the read to the stored rows instead of
    /// throwing on a GET.
    /// </summary>
    private static async Task<ModeSnapshotBackfill?> SnapshotBackfillAsync(
        AgentConfigurationDbContext db,
        Guid modeId,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var snapshotJson = await db.ModeVersions.AsNoTracking()
            .Where(x => x.AgentModeId == modeId && x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.SnapshotJson != null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.SnapshotJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson!);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // An EMPTY array is not a document: the pack snapshot always writes a bindings
            // array, so backfilling `[]` would make a mode that never declared bindings read
            // back as "declared an empty list" and would grow a key on every legacy mode.
            string? bindings = root.TryGetProperty("bindings", out var bindingsElement)
                && bindingsElement.ValueKind == JsonValueKind.Array
                && bindingsElement.GetArrayLength() > 0
                    ? bindingsElement.GetRawText()
                    : null;
            string? instructions = root.TryGetProperty("instructions", out var instructionsElement) && instructionsElement.ValueKind == JsonValueKind.String
                ? instructionsElement.GetString()
                : null;
            string? tier = root.TryGetProperty("orchestration_tier", out var tierElement) && tierElement.ValueKind == JsonValueKind.String
                ? tierElement.GetString()
                : null;

            var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("nodes", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodeArray.EnumerateArray())
                {
                    if (node.ValueKind != JsonValueKind.Object
                        || !node.TryGetProperty("node_key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String
                        || !node.TryGetProperty("relationship", out var relationshipElement) || relationshipElement.ValueKind != JsonValueKind.Object)
                        continue;
                    var key = keyElement.GetString();
                    if (!string.IsNullOrWhiteSpace(key)) relationships[key!] = relationshipElement.GetRawText();
                }
            }
            return new ModeSnapshotBackfill(bindings, instructions, tier, relationships);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The single projection of a mode. Both the HTTP read and every topology write
    /// answer with this shape; a second hand-written shape is how the editor and the
    /// tools would start disagreeing about what a mode is.
    /// </summary>
    public static JsonElement ProjectDetail(
        AgentModeRecord mode,
        IReadOnlyList<ModeNodeRecord> nodes,
        IReadOnlyList<ModeEdgeRecord> edges,
        CanvasLayoutRecord? layout,
        bool managed) => JsonSerializer.SerializeToElement(new
    {
        id = mode.Id,
        slug = mode.Slug,
        display_name = mode.DisplayName,
        description = mode.Description,
        instructions = mode.Instructions,
        // The explicit tier is what the editor's selector binds to and what the runtime
        // prefers over inference at freeze time; omitting it from the projection is why
        // the feature was invisible to every reader (and to tinadec_inspect).
        orchestration_tier = mode.OrchestrationTier,
        i18n = Element(mode.I18nJson),
        runtime_policy = Element(mode.RuntimePolicyJson),
        bindings = Element(mode.BindingsJson),
        status = mode.Status,
        revision = mode.Revision,
        version = mode.Version,
        managed,
        nodes = nodes.Select(n => new
        {
            id = n.Id,
            node_key = n.NodeKey,
            agent_definition_id = n.AgentDefinitionId,
            layer = n.Layer,
            label = n.Label,
            i18n = Element(n.I18nJson),
            position = Element(n.PositionJson),
            config = Element(n.ConfigJson),
            relationship = Element(n.RelationshipJson),
            model_strategy_override = Element(n.ModelStrategyOverrideJson),
        }).ToArray(),
        edges = edges.Select(e => new
        {
            id = e.Id,
            edge_key = e.EdgeKey,
            source_node_key = e.SourceNodeKey,
            target_node_key = e.TargetNodeKey,
            condition = Element(e.ConditionJson),
        }).ToArray(),
        canvas_layout = Element(layout?.LayoutJson),
        created_at = mode.CreatedAt,
        updated_at = mode.UpdatedAt,
    });

    // ── draft writes ──

    public async Task<AgentTopologyResult> CreateDraftAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "A JSON object body is required.");
        var scope = _tenant.Current;
        var name = Text(payload, "display_name") ?? Text(payload, "name");
        if (string.IsNullOrWhiteSpace(name))
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "display_name is required.");
        var policy = ParseRuntimePolicy(payload);
        if (policy.Error is not null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRuntimePolicy, policy.Error);
        var topology = ParseTopology(payload);
        if (topology.Failure is not null) return topology.Failure;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var slugBase = Slugify(Text(payload, "slug") ?? name);
        var slug = await UniqueDraftSlugAsync(db, slugBase, scope.TenantId, scope.WorkspaceId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var mode = new AgentModeRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            Slug = slug, DisplayName = name.Trim(), Description = Text(payload, "description"),
            RuntimePolicyJson = policy.Json,
            OrchestrationTier = topology.Input!.OrchestrationTier,
            Instructions = topology.Input!.Instructions,
            BindingsJson = topology.Input!.BindingsJson,
            Status = "draft", Revision = 1, Version = 0,
            CreatedAt = now, UpdatedAt = now,
            CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
        };
        db.AgentModes.Add(mode);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return AgentTopologyResult.Failure(AgentTopologyStatus.Conflict, AgentTopologyCodes.DuplicateSlug, "A draft mode already uses this slug."); }

        // An empty draft is a legal authoring state (the editor creates one before the
        // first node); publish is where a graph has to be valid.
        var (_, createFailure) = await ApplyTopologyAsync(db, mode, topology.Input!, scope.TenantId, scope.WorkspaceId, cancellationToken);
        if (createFailure is not null) return createFailure;
        await db.SaveChangesAsync(cancellationToken);
        return await LoadResultAsync(db, mode, cancellationToken);
    }

    public async Task<AgentTopologyResult> CloneDraftAsync(Guid sourceModeId, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var source = await db.AgentModes.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == sourceModeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
        if (source is null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.NotFound, AgentTopologyCodes.NotFound, "Source mode not found.");
        if (string.Equals(source.Status, "archived", StringComparison.OrdinalIgnoreCase))
            return AgentTopologyResult.Failure(AgentTopologyStatus.Conflict, AgentTopologyCodes.Conflict, "An archived mode cannot be cloned.");

        // A clone is a copy of the LIVE topology: a pack reinstall archives the previous
        // projection rows and adds new ones, so copying every row would carry the whole
        // history — including nodes and edges the current version deleted — into the copy.
        var nodes = await db.ModeNodes.AsNoTracking().Where(x => x.ModeId == sourceModeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Status != "archived" && x.ArchivedAt == null).ToListAsync(cancellationToken);
        var edges = await db.ModeEdges.AsNoTracking().Where(x => x.ModeId == sourceModeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Status != "archived" && x.ArchivedAt == null).ToListAsync(cancellationToken);
        // The layout is ordered IN MEMORY on purpose: SQLite cannot translate a
        // DateTimeOffset ORDER BY, so `.OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync()`
        // throws at query time. PublishAsync at L682 already does it this way.
        var layouts = await db.CanvasLayouts.AsNoTracking().Where(x => x.ModeId == sourceModeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Status != "archived" && x.ArchivedAt == null).ToListAsync(cancellationToken);
        var layout = layouts.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();

        // A clone must be an EDITABLE COPY, not a shell: instructions, the team binding
        // list, every node's relationship file and the explicit tier all travel with it.
        // Modes installed before the row projection carried bindings/relationships get
        // them from the newest frozen snapshot here — the same repair GET applies.
        var backfill = NeedsSnapshotBackfill(source, nodes)
            ? await SnapshotBackfillAsync(db, sourceModeId, scope.TenantId, scope.WorkspaceId, cancellationToken)
            : null;
        var instructions = BackfillDocument(source.Instructions, backfill?.Instructions);
        var bindingsJson = BackfillDocument(source.BindingsJson, backfill?.BindingsJson);
        var orchestrationTier = BackfillDocument(source.OrchestrationTier, backfill?.OrchestrationTier);

        var now = DateTimeOffset.UtcNow;
        var name = (payload.ValueKind == JsonValueKind.Object ? Text(payload, "display_name") : null) ?? $"{source.DisplayName} (copy)";
        var slug = await UniqueDraftSlugAsync(db, Slugify(Text(payload, "slug") ?? $"{source.Slug}-copy"), scope.TenantId, scope.WorkspaceId, cancellationToken);
        var copy = new AgentModeRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            Slug = slug, DisplayName = name.Trim(), Description = source.Description,
            I18nJson = null, RuntimePolicyJson = source.RuntimePolicyJson,
            Instructions = instructions, BindingsJson = bindingsJson, OrchestrationTier = orchestrationTier,
            Status = "draft", Revision = 1, Version = 0,
            CreatedAt = now, UpdatedAt = now,
            CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
        };
        db.AgentModes.Add(copy);
        foreach (var node in nodes)
            db.ModeNodes.Add(new ModeNodeRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ModeId = copy.Id,
                NodeKey = node.NodeKey, AgentDefinitionId = node.AgentDefinitionId, Layer = node.Layer,
                Label = node.Label, I18nJson = node.I18nJson, PositionJson = node.PositionJson,
                ConfigJson = node.ConfigJson,
                RelationshipJson = BackfillDocument(node.RelationshipJson, backfill?.RelationshipFor(node.NodeKey)),
                ModelStrategyOverrideJson = node.ModelStrategyOverrideJson,
                Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now,
            });
        foreach (var edge in edges)
            db.ModeEdges.Add(new ModeEdgeRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ModeId = copy.Id,
                EdgeKey = edge.EdgeKey, SourceNodeKey = edge.SourceNodeKey, TargetNodeKey = edge.TargetNodeKey,
                ConditionJson = edge.ConditionJson, Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now,
            });
        if (layout is not null)
            db.CanvasLayouts.Add(new CanvasLayoutRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ModeId = copy.Id,
                LayoutJson = layout.LayoutJson, Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync(cancellationToken);
        return await LoadResultAsync(db, copy, cancellationToken);
    }

    public async Task<AgentTopologyResult> UpsertDraftAsync(Guid modeId, JsonElement payload, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "A JSON object body is required.");
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var mode = await db.AgentModes.FirstOrDefaultAsync(
            x => x.Id == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
        if (mode is null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.NotFound, AgentTopologyCodes.NotFound, "Mode not found.");
        var managed = await _packs.FindManagedResourceAsync("mode", modeId, cancellationToken);
        if (managed is not null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.Conflict, AgentTopologyCodes.BuiltInReadOnly,
                $"Pack '{managed.PackId}' manages mode '{managed.ResourceKey}'. Clone it before customizing.");
        if (expectedRevision >= 0 && mode.Revision != expectedRevision)
            return AgentTopologyResult.Failure(AgentTopologyStatus.PreconditionFailed, AgentTopologyCodes.Conflict, "Revision conflict.");
        if (!string.Equals(mode.Status, "draft", StringComparison.OrdinalIgnoreCase))
            return AgentTopologyResult.Failure(AgentTopologyStatus.Conflict, AgentTopologyCodes.NotDraft, "Only a draft mode can be edited.");

        var displayName = Text(payload, "display_name");
        if (!string.IsNullOrWhiteSpace(displayName)) mode.DisplayName = displayName;
        if (payload.TryGetProperty("description", out var description)) mode.Description = description.ValueKind == JsonValueKind.String ? description.GetString() : null;
        var policy = ParseRuntimePolicy(payload);
        if (policy.Error is not null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRuntimePolicy, policy.Error);
        if (payload.TryGetProperty("runtime_policy", out _)) mode.RuntimePolicyJson = policy.Json;

        var topology = ParseTopology(payload);
        if (topology.Failure is not null) return topology.Failure;
        var (state, applied) = await ApplyTopologyAsync(db, mode, topology.Input!, scope.TenantId, scope.WorkspaceId, cancellationToken);
        if (applied is not null) return applied;

        // Validation runs against the post-write projection, before anything is saved:
        // a rejected graph leaves the stored draft exactly as it was.
        var validated = await ValidateGraphAsync(db, mode, state!, scope.TenantId, scope.WorkspaceId, cancellationToken);
        if (validated is not null) return validated;

        mode.Revision++;
        mode.UpdatedAt = DateTimeOffset.UtcNow;
        mode.UpdatedByPrincipalId = scope.PrincipalId;
        mode.Status = "draft";
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return AgentTopologyResult.Failure(AgentTopologyStatus.PreconditionFailed, AgentTopologyCodes.Conflict, "Concurrent update."); }
        return await LoadResultAsync(db, mode, cancellationToken);
    }

    /// <summary>
    /// Deletes a user-authored mode — draft OR published. Three things this must get right:
    /// historical <see cref="ModeVersionRecord"/> rows are KEPT (they are the audit trail of
    /// what ran, and a purge of them would orphan every run binding that names one), every
    /// projection row and mode binding belonging to the mode is removed, and any workspace
    /// default still pointing at the mode or at one of its versions is cleared so a fresh
    /// workspace cannot resolve a deleted mode. Pack-managed modes are refused: the pack
    /// still owns them and its next reconcile would resurrect them.
    /// </summary>
    public async Task<AgentTopologyResult> DeleteDraftAsync(Guid modeId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var mode = await db.AgentModes.FirstOrDefaultAsync(
            x => x.Id == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
        if (mode is null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.NotFound, AgentTopologyCodes.NotFound, "Mode not found.");
        var managed = await _packs.FindManagedResourceAsync("mode", modeId, cancellationToken);
        if (managed is not null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.Conflict, AgentTopologyCodes.BuiltInReadOnly,
                $"Pack '{managed.PackId}' manages mode '{managed.ResourceKey}'. Clone it before editing it.");

        var now = DateTimeOffset.UtcNow;
        db.ModeNodes.RemoveRange(db.ModeNodes.Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId));
        db.ModeEdges.RemoveRange(db.ModeEdges.Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId));
        db.CanvasLayouts.RemoveRange(db.CanvasLayouts.Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId));
        db.ModeBindings.RemoveRange(db.ModeBindings.Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId));

        // Workspace defaults pointing at the mode (or at a version of it) are a dangling
        // reference the moment the row disappears. Both halves are cleared together because
        // the publish gate requires default_mode_version_id to belong to
        // default_agent_mode_id; the version half is checked independently as well, because a
        // draft write can set it without going through that gate.
        var defaults = await db.WorkspaceDefaults.FirstOrDefaultAsync(
            x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
        if (defaults is not null)
        {
            var versionPointsAtMode = defaults.DefaultModeVersionId is { } versionId
                && await db.ModeVersions.AnyAsync(x => x.Id == versionId && x.AgentModeId == modeId
                    && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
            if (defaults.DefaultAgentModeId == modeId || versionPointsAtMode)
            {
                defaults.DefaultAgentModeId = null;
                defaults.DefaultModeVersionId = null;
                defaults.Revision++;
                defaults.UpdatedAt = now;
            }
        }

        db.AgentModes.Remove(mode);
        await db.SaveChangesAsync(cancellationToken);
        return new AgentTopologyResult { Ok = true, ModeId = modeId };
    }

    // ── publish ──

    public async Task<AgentTopologyResult> PublishAsync(Guid modeId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var mode = await db.AgentModes.FirstOrDefaultAsync(
            x => x.Id == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
        if (mode is null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.NotFound, AgentTopologyCodes.NotFound, "Mode not found.");
        var managed = await _packs.FindManagedResourceAsync("mode", modeId, cancellationToken);
        if (managed is not null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.Conflict, AgentTopologyCodes.BuiltInReadOnly,
                $"Pack '{managed.PackId}' manages mode '{managed.ResourceKey}'. Clone it before publishing your own version.");
        if (expectedRevision >= 0 && mode.Revision != expectedRevision)
            return AgentTopologyResult.Failure(AgentTopologyStatus.PreconditionFailed, AgentTopologyCodes.Conflict, "Revision conflict.");
        if (!string.Equals(mode.Status, "draft", StringComparison.OrdinalIgnoreCase))
            return AgentTopologyResult.Failure(AgentTopologyStatus.Conflict, AgentTopologyCodes.NotDraft, "Only a draft mode can be published.");

        var nodes = await db.ModeNodes.Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId).ToListAsync(cancellationToken);
        var edges = await db.ModeEdges.Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId).ToListAsync(cancellationToken);
        if (nodes.Count == 0)
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "Mode must have at least one node.");

        // Graph semantics come from GraphValidation's marker/capability contract, the
        // same one pack admission uses — not the retired "one meeting node plus one
        // execution node" rule, which refused every Solo-shaped draft.
        var agentIds = nodes.Select(x => x.AgentDefinitionId).Distinct().ToArray();
        var agentDefinitions = await db.AgentDefinitions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && agentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var graphError = ValidateGraph(mode.Slug, nodes, edges, agentDefinitions);
        if (graphError is not null)
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.ModeGraphInvalid, graphError,
                new AgentTopologyDiagnostic(AgentTopologyCodes.ModeGraphInvalid, graphError));

        // A manual publish must not be able to reach a state a pack publish would refuse:
        // the binding-envelope gate (ModePublishGate) and the exploration-tool/policy gate
        // run here with the same shapes pack admission builds. Skipping them made the two
        // publish paths carry two different rule sets, which is exactly how a custom mode
        // could arm an envelope the pack path rejects.
        // The slug lookup covers the WHOLE workspace because a node-less binding attaches an
        // envelope to a spawnable template that is deliberately not a node of this mode — the
        // pack path resolves those against the pack's own agents, and skipping them here
        // would leave exactly that shape unchecked.
        // Grouped by slug: two packs may legitimately install agents with the SAME slug
        // (the roster contract allows it), and a ToDictionary would throw on that.
        var workspaceAgentRows = await db.AgentDefinitions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(cancellationToken);
        var workspaceAgentsBySlug = workspaceAgentRows
            .GroupBy(x => x.Slug, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var gateFailure = ValidatePublishGates(mode, nodes, agentDefinitions, workspaceAgentsBySlug);
        if (gateFailure is not null) return gateFailure;

        // cross-layer reuse warning
        var byAgent = nodes.GroupBy(n => n.AgentDefinitionId).Where(g => g.Select(x => x.Layer).Distinct().Count() > 1).Select(g => g.Key).ToArray();
        var warnings = new List<JsonElement>();
        if (byAgent.Length > 0)
            warnings.Add(JsonSerializer.SerializeToElement(new { code = "CROSS_LAYER_REUSE", message = "Same agent reused across operation and execution", agent_ids = byAgent }));

        // Per-node tool_switches from the mode bindings narrow the node's effective
        // tools the same way pack publishing does. `"*": false` on the conversation
        // leader is what sheds its business tool face and keeps a team run in the
        // free_form tier instead of solo_dispatch.
        var switchesByNode = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var binding in ParseBindingArray(mode.BindingsJson))
        {
            if (binding.TryGetProperty("node_key", out var bindingNode) && bindingNode.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(bindingNode.GetString())
                && binding.TryGetProperty("tool_switches", out var switches) && switches.ValueKind == JsonValueKind.Object)
                switchesByNode[bindingNode.GetString()!] = switches;
        }

        // Freeze exact AgentVersion/PromptVersion bindings. Runtime consumes only this snapshot.
        var frozenNodes = new List<object>();
        foreach (var node in nodes.OrderBy(n => n.NodeKey, StringComparer.Ordinal))
        {
            if (!agentDefinitions.TryGetValue(node.AgentDefinitionId, out var agentDef) || agentDef.Status != "published")
                return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, $"Node {node.NodeKey} must reference a published agent.");
            var agentVersion = await db.AgentVersions.AsNoTracking().Where(x => x.AgentDefinitionId == agentDef.Id && x.Status == "published")
                .OrderByDescending(x => x.Version).FirstOrDefaultAsync(cancellationToken);
            if (agentVersion is null || string.IsNullOrWhiteSpace(agentVersion.ContentHash))
                return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, $"Node {node.NodeKey} has no published immutable AgentVersion.");
            var agentTools = ParseTools(agentDef.ToolScopeJson);
            var modeTools = ParseTools(node.ConfigJson);
            HashSet<string> effective;
            if (agentTools.Contains("*")) effective = modeTools.Count > 0 ? new HashSet<string>(modeTools, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" };
            else if (modeTools.Count == 0) effective = new HashSet<string>(agentTools, StringComparer.OrdinalIgnoreCase);
            else effective = new HashSet<string>(agentTools.Intersect(modeTools, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (switchesByNode.TryGetValue(node.NodeKey, out var nodeSwitches))
                effective = new HashSet<string>(effective.Where(tool => !IsSwitchedOff(nodeSwitches, tool)), StringComparer.OrdinalIgnoreCase);
            if (effective.Count == 0)
                warnings.Add(JsonSerializer.SerializeToElement(new { code = "EMPTY_EFFECTIVE_TOOLS", message = $"Node {node.NodeKey} has no effective tools after intersection", node_key = node.NodeKey, agent_id = node.AgentDefinitionId }));
            PromptVersionRecord? promptVersion = null;
            if (agentDef.BasePromptPipelineId is { } promptPipelineId)
                promptVersion = await db.PromptVersions.AsNoTracking().Where(x => x.PromptPipelineId == promptPipelineId && x.Status == "published")
                    .OrderByDescending(x => x.Version).FirstOrDefaultAsync(cancellationToken);
            JsonElement config;
            try { config = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(node.ConfigJson) ? "{}" : node.ConfigJson); }
            catch
            {
                return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, $"Node {node.NodeKey} config is invalid JSON.");
            }
            JsonElement? modelStrategyOverride = null;
            if (!string.IsNullOrWhiteSpace(node.ModelStrategyOverrideJson))
            {
                var strategyError = ValidateModelStrategy(node.ModelStrategyOverrideJson);
                if (strategyError is not null)
                    return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidModelStrategy, $"Node {node.NodeKey}: {strategyError}");
                modelStrategyOverride = JsonSerializer.Deserialize<JsonElement>(node.ModelStrategyOverrideJson);
            }
            JsonElement? relationship = null;
            if (!string.IsNullOrWhiteSpace(node.RelationshipJson))
            {
                try { relationship = JsonSerializer.Deserialize<JsonElement>(node.RelationshipJson); }
                catch { return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, $"Node {node.NodeKey} relationship is invalid JSON."); }
            }
            frozenNodes.Add(new
            {
                node_key = node.NodeKey,
                agent_definition_id = agentDef.Id,
                agent_version_id = agentVersion.Id,
                agent_version_hash = agentVersion.ContentHash,
                layer = node.Layer,
                label = node.Label,
                i18n = Element(node.I18nJson),
                config,
                relationship,
                model_strategy_override = modelStrategyOverride,
                model_strategy_source = modelStrategyOverride is null ? "agent_version" : "mode_node_override",
                effective_tools = effective.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                prompt_pipeline_id = agentDef.BasePromptPipelineId,
                prompt_version_id = promptVersion?.Id,
                prompt_version_hash = promptVersion?.ContentHash
            });
        }
        // A node config that parsed for the snapshot but not for the warning scan is already
        // rejected above; this is the stored form of the same warnings the HTTP body carries.
        string? warningJson = warnings.Count > 0 ? JsonSerializer.Serialize(warnings) : null;
        var frozenEdges = edges.OrderBy(e => e.EdgeKey, StringComparer.Ordinal).Select(e => new
        {
            edge_key = e.EdgeKey,
            source_node_key = e.SourceNodeKey,
            target_node_key = e.TargetNodeKey,
            condition = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(e.ConditionJson) ? "{}" : e.ConditionJson)
        }).ToArray();
        var layouts = await db.CanvasLayouts.Where(x => x.ModeId == modeId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "draft").ToListAsync(cancellationToken);
        var layout = layouts.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        var canvasLayout = JsonSerializer.Deserialize<JsonElement>(layout?.LayoutJson ?? "{}");
        var snapshotObject = new Dictionary<string, object?>
        {
            ["schema"] = "tinadec.mode_version/v1",
            ["mode"] = new { id = mode.Id, slug = mode.Slug, display_name = mode.DisplayName, i18n = Element(mode.I18nJson) },
            ["nodes"] = frozenNodes,
            ["edges"] = frozenEdges,
            ["canvas_layout"] = canvasLayout
        };
        // The mode-level team bindings are part of the frozen shape so the runtime and
        // readers see the same list the editor saved; absent/empty serializes as [].
        snapshotObject["bindings"] = string.IsNullOrWhiteSpace(mode.BindingsJson)
            ? Array.Empty<object>()
            : JsonSerializer.Deserialize<JsonElement>(mode.BindingsJson);
        if (mode.RuntimePolicyJson is not null)
            snapshotObject["runtime_policy"] = JsonSerializer.Deserialize<JsonElement>(mode.RuntimePolicyJson);
        // Only non-empty instructions are frozen: writing the key unconditionally would
        // change the topology hash of every newly published legacy mode.
        if (!string.IsNullOrWhiteSpace(mode.Instructions))
            snapshotObject["instructions"] = mode.Instructions;
        // Same rule for the explicit tier: omitted when the mode declares none, so a mode
        // that never opted in keeps its inferred tier and its historical hash.
        if (!string.IsNullOrWhiteSpace(mode.OrchestrationTier))
            snapshotObject["orchestration_tier"] = GraphValidation.NormalizeOrchestrationTier(mode.OrchestrationTier);
        var snapshot = JsonSerializer.Serialize(snapshotObject);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
        var version = (await db.ModeVersions.Where(x => x.AgentModeId == modeId).MaxAsync(x => (int?)x.Version, cancellationToken) ?? 0) + 1;
        var now = DateTimeOffset.UtcNow;
        db.ModeVersions.Add(new ModeVersionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, AgentModeId = modeId,
            Version = version, SnapshotJson = snapshot, TopologyHash = hash, WarningJson = warningJson,
            Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId
        });
        // The published projection is what GET serves for a published mode and what the
        // editor clones; leaving the rows at "draft" made a published user mode read back
        // with no nodes at all.
        foreach (var node in nodes) { node.Status = "published"; node.Revision++; node.UpdatedAt = now; }
        foreach (var edge in edges) { edge.Status = "published"; edge.Revision++; edge.UpdatedAt = now; }
        if (layout is not null) { layout.Status = "published"; layout.Revision++; layout.UpdatedAt = now; }
        mode.Version = version;
        mode.Revision++;
        mode.UpdatedAt = now;
        mode.UpdatedByPrincipalId = scope.PrincipalId;
        mode.Status = "published";
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return AgentTopologyResult.Failure(AgentTopologyStatus.PreconditionFailed, AgentTopologyCodes.Conflict, "Concurrent update."); }
        return new AgentTopologyResult
        {
            Ok = true,
            ModeId = mode.Id,
            Version = version,
            Revision = mode.Revision,
            TopologyHash = hash,
            Warnings = warnings,
        };
    }

    // ── topology apply/validate ──

    /// <summary>
    /// The two gates a PACK publish already runs, applied to a CUSTOM publish so the two
    /// paths cannot diverge: <see cref="ModePublishGate.ValidateBindings"/> over the stored
    /// binding list (envelope ⊆ template capabilities/tools, spawn ceilings, and a kept
    /// workspace-mutating tool needs a write grant) and
    /// <see cref="ExplorationToolPolicy.Validate"/> over the mode's effective tool surface
    /// (every exploration tool must be backed by a policy feature). Subjects are built the
    /// same way pack admission builds them, so one vocabulary decides both.
    ///
    /// The pack path passes <c>ceilings: null</c> because TOML ceilings are not available at
    /// admission; the same is true here — the freeze gate remains authoritative for numbers.
    /// </summary>
    private static AgentTopologyResult? ValidatePublishGates(
        AgentModeRecord mode,
        IReadOnlyList<ModeNodeRecord> nodes,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agentDefinitions,
        IReadOnlyDictionary<string, AgentDefinitionRecord> workspaceAgentsBySlug)
    {
        var nodeByKey = nodes.GroupBy(x => x.NodeKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        // A slug resolves to the agent this mode actually nodes first, then to any workspace
        // agent (a node-less binding names a spawnable template that is not a mode node).
        AgentDefinitionRecord? ResolveAgentBySlug(string slug)
        {
            foreach (var node in nodes)
                if (agentDefinitions.TryGetValue(node.AgentDefinitionId, out var candidate)
                    && string.Equals(candidate.Slug, slug, StringComparison.Ordinal))
                    return candidate;
            return workspaceAgentsBySlug.TryGetValue(slug, out var agent) ? agent : null;
        }

        // switchesByNode mirrors the pack path: a node-keyed binding's tool_switches narrow that
        // node's effective surface (narrowing only — this map can never add a tool).
        var switchesByNode = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var subjects = new List<ModePublishGate.BindingSubject>();
        foreach (var binding in ParseBindingArray(mode.BindingsJson))
        {
            var nodeKey = Text(binding, "node_key");
            var agentRef = Text(binding, "agent_ref");
            AgentDefinitionRecord? agent = null;
            string subjectKey;
            if (!string.IsNullOrWhiteSpace(nodeKey))
            {
                // A dangling node_key is GraphValidation's diagnostic, not this gate's: keep
                // building subjects for the keys that resolve so the two errors do not compete.
                if (!nodeByKey.TryGetValue(nodeKey, out var node)) continue;
                agentDefinitions.TryGetValue(node.AgentDefinitionId, out agent);
                subjectKey = nodeKey;
            }
            else if (!string.IsNullOrWhiteSpace(agentRef))
            {
                agent = ResolveAgentBySlug(StripAgentReferencePrefix(agentRef));
                subjectKey = agentRef;
            }
            else
            {
                continue;
            }

            var switches = binding.TryGetProperty("tool_switches", out var switchElement) && switchElement.ValueKind == JsonValueKind.Object
                ? switchElement
                : (JsonElement?)null;
            var envelope = binding.TryGetProperty("envelope", out var envelopeElement) && envelopeElement.ValueKind == JsonValueKind.Object
                ? envelopeElement
                : (JsonElement?)null;
            if (nodeKey is { Length: > 0 } && switches is { } nodeSwitches) switchesByNode[nodeKey] = nodeSwitches;

            subjects.Add(new ModePublishGate.BindingSubject(
                subjectKey,
                agent?.Slug ?? string.Empty,
                agent?.Layer ?? string.Empty,
                ParseCapabilities(agent?.CapabilitiesJson),
                ParseTools(agent?.ToolScopeJson).ToArray(),
                switches,
                envelope));
        }

        try
        {
            if (subjects.Count > 0) ModePublishGate.ValidateBindings(mode.Slug, subjects, ceilings: null);
        }
        catch (InvalidDataException exception)
        {
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.ModePublishRejected, exception.Message,
                new AgentTopologyDiagnostic(AgentTopologyCodes.ModePublishRejected, exception.Message));
        }

        // The exploration gate reads the mode's ENTIRE effective surface, which pack admission
        // builds by walking every node (a node with no binding still holds its template scope)
        // and then every node-less binding that arms a spawnable template. Same walk here.
        var tools = new List<string>();
        foreach (var node in nodes)
        {
            if (!agentDefinitions.TryGetValue(node.AgentDefinitionId, out var agent)) continue;
            var effective = EffectiveTools(agent, node.ConfigJson);
            if (switchesByNode.TryGetValue(node.NodeKey, out var nodeSwitches))
                effective = effective.Where(tool => !GraphValidation.IsSwitchedOff(nodeSwitches, tool));
            tools.AddRange(effective);
        }
        foreach (var binding in ParseBindingArray(mode.BindingsJson))
        {
            if (Text(binding, "node_key") is { Length: > 0 }) continue;
            var agentRef = StripAgentReferencePrefix(Text(binding, "agent_ref"));
            var agent = ResolveAgentBySlug(agentRef);
            if (agent is null) continue;
            var switches = binding.TryGetProperty("tool_switches", out var switchElement) && switchElement.ValueKind == JsonValueKind.Object
                ? switchElement
                : (JsonElement?)null;
            tools.AddRange(ParseTools(agent.ToolScopeJson).Where(tool => !GraphValidation.IsSwitchedOff(switches, tool)));
        }

        var policy = ParseRuntimePolicyJson(mode.RuntimePolicyJson);
        if (ExplorationToolPolicy.Validate(policy, tools, $"mode '{mode.Slug}'") is { } explorationError)
            return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.ModePublishRejected, explorationError,
                new AgentTopologyDiagnostic(AgentTopologyCodes.ModePublishRejected, explorationError));
        return null;
    }

    /// <summary>Agent-reference spelling on the wire may be a bare slug or <c>agent:&lt;slug&gt;</c>.</summary>
    private static string StripAgentReferencePrefix(string? value) =>
        value is { Length: > 6 } reference && reference.StartsWith("agent:", StringComparison.Ordinal) ? reference[6..] : value ?? string.Empty;

    /// <summary>
    /// The node's effective tools: the agent's scope narrowed by the node config's own tool
    /// arrays. Same precedence as the pack publish path — a wildcard agent scope plus a
    /// non-empty node list means "exactly the node list", never the wildcard.
    /// </summary>
    private static IEnumerable<string> EffectiveTools(AgentDefinitionRecord agent, string? configJson)
    {
        var agentTools = ParseTools(agent.ToolScopeJson);
        var modeTools = ParseTools(configJson);
        if (agentTools.Contains("*"))
            return modeTools.Count > 0 ? modeTools : agentTools;
        return modeTools.Count == 0 ? agentTools : agentTools.Intersect(modeTools, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ModeRuntimePolicyDto? ParseRuntimePolicyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ModeRuntimePolicyDto>(json); }
        catch (JsonException) { return null; }
    }

    private sealed record SubmittedNode(
        string NodeKey, Guid AgentDefinitionId, string Layer, string? Label,
        string? PositionJson, string? ConfigJson, string? RelationshipJson, string? ModelStrategyOverrideJson);

    private sealed record SubmittedEdge(string EdgeKey, string SourceNodeKey, string TargetNodeKey, string? ConditionJson);

    /// <summary>
    /// A submitted topology. <see cref="HasNodes"/>/<see cref="HasEdges"/> distinguish
    /// "an empty array = delete everything" from "the property was not sent = leave it
    /// alone": only a submitted array can be the full state a deletion is derived from.
    /// <see cref="HasInstructions"/>/<see cref="HasBindings"/>/<see cref="HasOrchestrationTier"/>
    /// apply the same rule to the mode-level fields: absent leaves the stored value, an
    /// explicit value (including JSON <c>null</c>) replaces it.
    /// </summary>
    private sealed record TopologyInput(
        IReadOnlyList<SubmittedNode> Nodes,
        IReadOnlyList<SubmittedEdge> Edges,
        bool HasNodes,
        bool HasEdges,
        bool HasLayout,
        string? LayoutJson,
        bool HasInstructions,
        string? Instructions,
        bool HasBindings,
        string? BindingsJson,
        bool HasOrchestrationTier,
        string? OrchestrationTier);

    /// <summary>The draft projection after a write: what validation reads.</summary>
    private sealed record TopologyState(IReadOnlyList<SubmittedNode> Nodes, IReadOnlyList<SubmittedEdge> Edges);

    private sealed record TopologyParse(TopologyInput? Input, AgentTopologyResult? Failure);

    private static TopologyParse ParseTopology(JsonElement payload)
    {
        var nodes = new List<SubmittedNode>();
        var edges = new List<SubmittedEdge>();
        var hasNodes = false;
        var hasEdges = false;
        if (payload.TryGetProperty("nodes", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array)
        {
            hasNodes = true;
            foreach (var node in nodeArray.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "Each node must be an object."));
                var key = (Text(node, "node_key") ?? Text(node, "id"))?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.NodeKeyRequired,
                        "Every node needs a stable node_key; a generated key is deleted by the next save."));
                var agentText = Text(node, "agent_definition_id") ?? Text(node, "agent_id");
                if (!Guid.TryParse(agentText, out var agentId) || agentId == Guid.Empty)
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.NodeAgentRequired,
                        $"Node '{key}' needs agent_definition_id."));
                var layer = (Text(node, "layer") ?? "operation").Trim().ToLowerInvariant();
                if (layer is not ("operation" or "execution"))
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidLayer,
                        $"Node '{key}' layer must be operation or execution."));
                if (node.TryGetProperty("config", out var config) && config.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, $"Node '{key}' config must be an object."));
                if (node.TryGetProperty("relationship", out var relationshipProbe) && relationshipProbe.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, $"Node '{key}' relationship must be an object."));
                var modelOverride = node.TryGetProperty("model_strategy_override", out var overrideValue) && overrideValue.ValueKind != JsonValueKind.Null
                    ? overrideValue.GetRawText() : null;
                if (modelOverride is not null && ValidateModelStrategy(modelOverride) is { } strategyError)
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidModelStrategy, $"Node '{key}': {strategyError}"));
                nodes.Add(new SubmittedNode(
                    key,
                    agentId,
                    layer,
                    Text(node, "label"),
                    node.TryGetProperty("position", out var position) ? position.GetRawText() : node.TryGetProperty("position_json", out var positionJson) ? positionJson.GetRawText() : null,
                    node.TryGetProperty("config", out var configValue) ? configValue.GetRawText() : null,
                    node.TryGetProperty("relationship", out var relationship) && relationship.ValueKind != JsonValueKind.Null ? relationship.GetRawText() : null,
                    modelOverride));
            }
        }

        if (payload.TryGetProperty("edges", out var edgeArray) && edgeArray.ValueKind == JsonValueKind.Array)
        {
            hasEdges = true;
            foreach (var edge in edgeArray.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object)
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "Each edge must be an object."));
                var key = Text(edge, "edge_key")?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.EdgeKeyRequired,
                        "Every edge needs its own edge_key; a missing key would be re-created as a duplicate edge on the next save."));
                var source = (Text(edge, "source_node_key") ?? Text(edge, "source"))?.Trim();
                var target = (Text(edge, "target_node_key") ?? Text(edge, "target"))?.Trim();
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.EdgeEndpointsRequired,
                        $"Edge '{key}' needs source_node_key and target_node_key."));
                edges.Add(new SubmittedEdge(key, source, target,
                    edge.TryGetProperty("condition", out var condition) ? condition.GetRawText() : null));
            }
        }

        var hasLayout = payload.TryGetProperty("canvas_layout", out var layout) || payload.TryGetProperty("layout", out layout);

        // instructions: a string (<=4000 chars) or null to clear. Absent = leave alone.
        var hasInstructions = payload.TryGetProperty("instructions", out var instructionsElement);
        string? instructions = null;
        if (hasInstructions && instructionsElement.ValueKind != JsonValueKind.Null)
        {
            if (instructionsElement.ValueKind != JsonValueKind.String)
                return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "instructions must be a string or null."));
            instructions = instructionsElement.GetString();
            if (instructions is { Length: > MaxModeInstructionsLength })
                return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, $"instructions must be at most {MaxModeInstructionsLength} characters."));
        }

        // bindings: a JSON array of objects (each needs node_key or agent_ref) or null to
        // clear. This is the mode-level team-member binding list, not the per-node
        // mode_bindings narrowing table; it rides the same draft rows and is frozen into
        // the published snapshot verbatim.
        var hasBindings = payload.TryGetProperty("bindings", out var bindingsElement);
        string? bindingsJson = null;
        if (hasBindings && bindingsElement.ValueKind != JsonValueKind.Null)
        {
            if (bindingsElement.ValueKind != JsonValueKind.Array)
                return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "bindings must be an array or null."));
            foreach (var binding in bindingsElement.EnumerateArray())
            {
                if (binding.ValueKind != JsonValueKind.Object)
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "Each mode binding must be an object."));
                var hasNode = binding.TryGetProperty("node_key", out var bindingNode) && bindingNode.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(bindingNode.GetString());
                var hasAgent = binding.TryGetProperty("agent_ref", out var bindingAgent) && bindingAgent.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(bindingAgent.GetString());
                if (!hasNode && !hasAgent)
                    return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "Each mode binding needs node_key or agent_ref."));
            }
            bindingsJson = bindingsElement.GetRawText();
        }

        // orchestration_tier: an explicit tier string (or the "solo" alias), null to
        // clear. Absent = leave alone. Normalization lives in GraphValidation so the
        // pack manifest, the editor and the tools accept exactly one vocabulary.
        var hasTier = payload.TryGetProperty("orchestration_tier", out var tierElement);
        string? orchestrationTier = null;
        if (hasTier && tierElement.ValueKind != JsonValueKind.Null)
        {
            if (tierElement.ValueKind != JsonValueKind.String)
                return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "orchestration_tier must be a string or null."));
            try { orchestrationTier = GraphValidation.NormalizeOrchestrationTier(tierElement.GetString()); }
            catch (InvalidDataException exception)
            {
                return new(null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, exception.Message));
            }
        }

        return new(new TopologyInput(nodes, edges, hasNodes, hasEdges, hasLayout, hasLayout ? layout.GetRawText() : null,
            hasInstructions, instructions, hasBindings, bindingsJson, hasTier, orchestrationTier), null);
    }

    /// A mode's collaboration instructions are frozen into the version snapshot; the
    /// 4 KiB cap keeps that snapshot inside its 32 KiB column.
    private const int MaxModeInstructionsLength = GraphValidation.MaxModeInstructionsLength;

    /// <summary>
    /// Applies the submitted arrays to the tracked draft projection: upsert by key,
    /// then delete every draft row the submitted array no longer mentions. An array
    /// that was not sent at all is left alone — only a submitted array is a claim about
    /// the full state — and published rows are out of scope by construction, because
    /// every query and delete here filters on <c>status = draft</c>.
    ///
    /// A draft edge whose endpoint node disappeared goes with it instead of dangling:
    /// that is what the editor's node delete means, and Core would refuse the dangling
    /// edge anyway.
    /// </summary>
    private static async Task<(TopologyState? State, AgentTopologyResult? Failure)> ApplyTopologyAsync(
        AgentConfigurationDbContext db,
        AgentModeRecord mode,
        TopologyInput input,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var submittedNodeKeys = input.Nodes.Select(n => n.NodeKey).ToHashSet(StringComparer.Ordinal);
        if (submittedNodeKeys.Count != input.Nodes.Count)
            return (null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "Node keys must be unique within one mode."));

        var existingNodes = await db.ModeNodes.Where(x => x.ModeId == mode.Id && x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.Status == "draft")
            .ToListAsync(cancellationToken);
        var nodeState = new Dictionary<string, SubmittedNode>(StringComparer.Ordinal);
        if (input.HasNodes)
        {
            foreach (var node in input.Nodes)
            {
                var existing = existingNodes.FirstOrDefault(x => string.Equals(x.NodeKey, node.NodeKey, StringComparison.Ordinal));
                if (existing is null)
                {
                    db.ModeNodes.Add(new ModeNodeRecord
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId, ModeId = mode.Id,
                        NodeKey = node.NodeKey, AgentDefinitionId = node.AgentDefinitionId, Layer = node.Layer,
                        Label = node.Label, PositionJson = node.PositionJson, ConfigJson = node.ConfigJson,
                        RelationshipJson = node.RelationshipJson,
                        ModelStrategyOverrideJson = node.ModelStrategyOverrideJson,
                        Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now,
                    });
                }
                else
                {
                    existing.AgentDefinitionId = node.AgentDefinitionId;
                    existing.Layer = node.Layer;
                    existing.Label = node.Label;
                    existing.PositionJson = node.PositionJson;
                    existing.ConfigJson = node.ConfigJson;
                    existing.RelationshipJson = node.RelationshipJson;
                    existing.ModelStrategyOverrideJson = node.ModelStrategyOverrideJson;
                    existing.UpdatedAt = now;
                    existing.Revision++;
                }
                nodeState[node.NodeKey] = node;
            }
            foreach (var stale in existingNodes.Where(x => !submittedNodeKeys.Contains(x.NodeKey)))
                db.ModeNodes.Remove(stale);
        }
        else
        {
            foreach (var existing in existingNodes)
                nodeState[existing.NodeKey] = new SubmittedNode(existing.NodeKey, existing.AgentDefinitionId, existing.Layer,
                    existing.Label, existing.PositionJson, existing.ConfigJson, existing.RelationshipJson, existing.ModelStrategyOverrideJson);
        }

        var submittedEdgeKeys = input.Edges.Select(e => e.EdgeKey).ToHashSet(StringComparer.Ordinal);
        if (submittedEdgeKeys.Count != input.Edges.Count)
            return (null, AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.InvalidRequest, "Edge keys must be unique within one mode."));

        var existingEdges = await db.ModeEdges.Where(x => x.ModeId == mode.Id && x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.Status == "draft")
            .ToListAsync(cancellationToken);
        var edgeState = new Dictionary<string, SubmittedEdge>(StringComparer.Ordinal);
        if (input.HasEdges)
        {
            foreach (var edge in input.Edges)
            {
                var existing = existingEdges.FirstOrDefault(x => string.Equals(x.EdgeKey, edge.EdgeKey, StringComparison.Ordinal));
                if (existing is null)
                {
                    db.ModeEdges.Add(new ModeEdgeRecord
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId, ModeId = mode.Id,
                        EdgeKey = edge.EdgeKey, SourceNodeKey = edge.SourceNodeKey, TargetNodeKey = edge.TargetNodeKey,
                        ConditionJson = edge.ConditionJson, Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now,
                    });
                }
                else
                {
                    existing.SourceNodeKey = edge.SourceNodeKey;
                    existing.TargetNodeKey = edge.TargetNodeKey;
                    existing.ConditionJson = edge.ConditionJson;
                    existing.UpdatedAt = now;
                    existing.Revision++;
                }
                edgeState[edge.EdgeKey] = edge;
            }
            foreach (var stale in existingEdges.Where(x => !submittedEdgeKeys.Contains(x.EdgeKey)))
                db.ModeEdges.Remove(stale);
        }
        else
        {
            foreach (var existing in existingEdges)
                edgeState[existing.EdgeKey] = new SubmittedEdge(existing.EdgeKey, existing.SourceNodeKey, existing.TargetNodeKey, existing.ConditionJson);
        }
        foreach (var existing in existingEdges)
        {
            if (!edgeState.TryGetValue(existing.EdgeKey, out var kept)) continue;
            if (nodeState.ContainsKey(kept.SourceNodeKey) && nodeState.ContainsKey(kept.TargetNodeKey)) continue;
            edgeState.Remove(existing.EdgeKey);
            db.ModeEdges.Remove(existing);
        }

        if (input.HasLayout)
        {
            var layout = await db.CanvasLayouts.FirstOrDefaultAsync(
                x => x.ModeId == mode.Id && x.TenantId == tenantId && x.WorkspaceId == workspaceId, cancellationToken);
            var json = input.LayoutJson ?? "{}";
            if (layout is null)
                db.CanvasLayouts.Add(new CanvasLayoutRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId, ModeId = mode.Id,
                    LayoutJson = json, Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now,
                });
            else
            {
                layout.LayoutJson = json;
                layout.UpdatedAt = now;
                layout.Revision++;
            }
        }

        // Mode-level fields ride the same draft-write semantics as the topology arrays:
        // only a submitted value replaces what is stored (null clears; absent leaves).
        if (input.HasInstructions)
            mode.Instructions = input.Instructions;
        if (input.HasBindings)
            mode.BindingsJson = input.BindingsJson;
        if (input.HasOrchestrationTier)
            mode.OrchestrationTier = input.OrchestrationTier;

        return (new TopologyState(nodeState.Values.ToArray(), edgeState.Values.ToArray()), null);
    }

    private static async Task<AgentTopologyResult?> ValidateGraphAsync(
        AgentConfigurationDbContext db,
        AgentModeRecord mode,
        TopologyState state,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var agentIds = state.Nodes.Select(n => n.AgentDefinitionId).Distinct().ToArray();
        var agents = await db.AgentDefinitions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId && agentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var nodeRefs = state.Nodes.Select(n => new GraphValidation.NodeRef(
            n.NodeKey, n.AgentDefinitionId.ToString(), n.Layer, n.Label, ParseConfig(n.ConfigJson), ParseRelationship(n.RelationshipJson))).ToArray();
        var edgeRefs = state.Edges.Select(e => new GraphValidation.EdgeRef(e.EdgeKey, e.SourceNodeKey, e.TargetNodeKey)).ToArray();
        var error = ValidateGraph(mode.Slug, nodeRefs, edgeRefs, agents);
        if (error is null) return null;
        // Validation failures come back as structured diagnostics so a caller (and the
        // model editing the graph) can fix one node or edge instead of parsing prose.
        return AgentTopologyResult.Failure(AgentTopologyStatus.BadRequest, AgentTopologyCodes.ModeGraphInvalid, error,
            new AgentTopologyDiagnostic(AgentTopologyCodes.ModeGraphInvalid, error));
    }

    private static string? ValidateGraph(
        string modeKey,
        IReadOnlyList<GraphValidation.NodeRef> nodes,
        IReadOnlyList<GraphValidation.EdgeRef> edges,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agents)
    {
        var agentRefs = agents.Values
            .Select(x => new GraphValidation.AgentRef(x.Id.ToString(), x.Slug, x.Layer, ParseCapabilities(x.CapabilitiesJson)))
            .ToArray();
        try
        {
            GraphValidation.ValidateModeGraph(modeKey, nodes, edges, agentRefs);
            return null;
        }
        catch (InvalidDataException exception)
        {
            return exception.Message;
        }
    }

    private static string? ValidateGraph(
        string modeKey,
        IReadOnlyList<ModeNodeRecord> nodes,
        IReadOnlyList<ModeEdgeRecord> edges,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agents)
    {
        var nodeRefs = nodes.Select(n => new GraphValidation.NodeRef(n.NodeKey, n.AgentDefinitionId.ToString(), n.Layer, n.Label, ParseConfig(n.ConfigJson), ParseRelationship(n.RelationshipJson))).ToArray();
        var edgeRefs = edges.Select(e => new GraphValidation.EdgeRef(e.EdgeKey, e.SourceNodeKey, e.TargetNodeKey)).ToArray();
        return ValidateGraph(modeKey, nodeRefs, edgeRefs, agents);
    }

    private async Task<AgentTopologyResult> LoadResultAsync(AgentConfigurationDbContext db, AgentModeRecord mode, CancellationToken cancellationToken)
    {
        var scope = _tenant.Current;
        var nodes = await db.ModeNodes.Where(x => x.ModeId == mode.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "draft")
            .OrderBy(x => x.NodeKey).ToListAsync(cancellationToken);
        var edges = await db.ModeEdges.Where(x => x.ModeId == mode.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "draft")
            .OrderBy(x => x.EdgeKey).ToListAsync(cancellationToken);
        var layout = await db.CanvasLayouts.FirstOrDefaultAsync(
            x => x.ModeId == mode.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "draft", cancellationToken);
        return new AgentTopologyResult
        {
            Ok = true,
            ModeId = mode.Id,
            Revision = mode.Revision,
            Version = mode.Version,
            Mode = ProjectDetail(mode, nodes, edges, layout, managed: false),
        };
    }

    // ── shared helpers ──

    /// <summary>Runtime-policy parse/validate shared with the endpoint: absent means "leave as is".</summary>
    public static (string? Json, string? Error) ParseRuntimePolicy(JsonElement body)
    {
        if (!body.TryGetProperty("runtime_policy", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (null, null);
        if (value.ValueKind != JsonValueKind.Object)
            return (null, "runtime_policy must be an object or null.");
        try
        {
            var policy = value.Deserialize<ModeRuntimePolicyDto>();
            var error = ModeRuntimePolicyRegistry.Validate(policy);
            return error is null && policy is not null
                ? (JsonSerializer.Serialize(policy), null)
                : (null, error ?? "runtime_policy is invalid.");
        }
        catch (JsonException)
        {
            return (null, "runtime_policy is invalid.");
        }
    }

    public static string? ValidateModelStrategy(string json)
    {
        try
        {
            _ = ModelStrategyJson.Parse(json);
            return null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return exception.Message;
        }
    }

    public static string Slugify(string value) =>
        string.Join('-', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<string> UniqueDraftSlugAsync(
        AgentConfigurationDbContext db,
        string baseSlug,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrWhiteSpace(baseSlug) ? "mode" : baseSlug;
        var taken = (await db.AgentModes.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.Status == "draft")
                .Select(x => x.Slug).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(slug)) return slug;
        for (var index = 2; index < 500; index++)
        {
            var candidate = $"{slug}-{index}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{slug}-{Guid.NewGuid():N}";
    }

    private static HashSet<string> ParseTools(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                    if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
                        set.Add(element.GetString()!.Trim());
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("allowed_tools", out var allowed) && allowed.ValueKind == JsonValueKind.Array)
                    foreach (var element in allowed.EnumerateArray()) if (element.ValueKind == JsonValueKind.String) set.Add(element.GetString()!.Trim());
                if (root.TryGetProperty("tool_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.Array)
                    foreach (var element in enabled.EnumerateArray()) if (element.ValueKind == JsonValueKind.String) set.Add(element.GetString()!.Trim());
                if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                    foreach (var element in tools.EnumerateArray()) if (element.ValueKind == JsonValueKind.String) set.Add(element.GetString()!.Trim());
            }
        }
        catch { }
        return set;
    }

    /// <summary>Parses the mode-level bindings array; malformed/non-array reads as
    /// empty so a corrupt draft degrades to "no switches" instead of throwing.</summary>
    private static IReadOnlyList<JsonElement> ParseBindingArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// The snapshot-fetch path's view of a tool switch map: delegates to the single shared
    /// predicate (<see cref="GraphValidation.IsSwitchedOff"/>) so the surface frozen into the
    /// published snapshot sheds exactly what the pack path sheds — including <c>"*": false</c>.
    /// </summary>
    private static bool IsSwitchedOff(JsonElement switches, string toolId) =>
        GraphValidation.IsSwitchedOff(switches, toolId);

    private static IReadOnlyList<string> ParseCapabilities(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson)) return [];
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(capabilitiesJson);
            if (element.ValueKind != JsonValueKind.Array) return [];
            return element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
        }
        catch (JsonException) { return []; }
    }

    // Undefined when absent/non-object so GraphValidation's config contract holds.
    private static JsonElement ParseConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return default;
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(configJson);
            return element.ValueKind == JsonValueKind.Object ? element : default;
        }
        catch (JsonException) { return default; }
    }

    /// <summary>Node relationship for graph validation: parsed the same tolerant way
    /// as config; a malformed document reads as absent rather than throwing here, and
    /// the publish path re-parses it strictly where it can return a real error.</summary>
    private static JsonElement? ParseRelationship(string? relationshipJson)
    {
        if (string.IsNullOrWhiteSpace(relationshipJson)) return null;
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(relationshipJson);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? Element(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
