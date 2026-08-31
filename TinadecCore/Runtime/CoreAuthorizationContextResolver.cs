using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>
/// Resolves authorization facts from the admitted run and its immutable snapshots.
/// This adapter intentionally lives in Runtime: Governance remains independent of
/// lifecycle, agent configuration, and MAF/DmaEA implementation details.
/// </summary>
internal sealed class CoreAuthorizationContextResolver : IAuthorizationContextResolver
{
    private readonly ILifecycleManager _lifecycle;
    private readonly IDbContextFactory<AgentControlDbContext> _instances;
    private readonly IDbContextFactory<AgentConfigurationDbContext> _configuration;
    private readonly IContentStore _content;

    public CoreAuthorizationContextResolver(
        ILifecycleManager lifecycle,
        IDbContextFactory<AgentControlDbContext> instances,
        IDbContextFactory<AgentConfigurationDbContext> configuration,
        IContentStore content)
    {
        _lifecycle = lifecycle;
        _instances = instances;
        _configuration = configuration;
        _content = content;
    }

    public async Task<IReadOnlyList<AuthorizationBoundary>> ResolveBoundariesAsync(
        AuthorizationContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TenantId == Guid.Empty || request.WorkspaceId == Guid.Empty || request.SubjectPrincipalId == Guid.Empty)
            return [DenyBoundary("missing_identity", request.Claim)];

        if (request.RunId is not { } runId || request.TaskId is not { } taskId || runId == Guid.Empty || taskId == Guid.Empty)
        {
            // Explicit Desktop/user actions intentionally have no synthetic run,
            // task, or agent instance. Their principal and tenant/workspace are
            // already bound by UserToolActionService; this boundary only says
            // that the human action may enter the normal permission-request
            // state machine. A later grant, policy, or explicit deny still
            // decides whether a lease is issued.
            return request.SubjectAgentInstanceId is null
                ? [new AuthorizationBoundary("user_principal", [new CapabilityRule(
                    "allow", request.Claim.Capability, request.Claim.Action, request.Claim.Resource)])]
                : [DenyBoundary("missing_run_boundary", request.Claim)];
        }

        var run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(run.TenantId, out var runTenant) || !Guid.TryParse(run.WorkspaceId, out var runWorkspace)
            || runTenant != request.TenantId || runWorkspace != request.WorkspaceId
            || !Guid.TryParse(run.InitiatedByPrincipalId, out var initiatedBy)
            || initiatedBy != request.SubjectPrincipalId)
            return [DenyBoundary("run_principal_mismatch", request.Claim)];

        if (request.SubjectAgentInstanceId is not { } agentId || agentId == Guid.Empty)
            return [DenyBoundary("agent_instance_required", request.Claim)];

        await using var db = await _instances.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == agentId && x.RunId == runId && x.TenantId == request.TenantId && x.WorkspaceId == request.WorkspaceId
            && x.ReleasedAt == null && (x.TaskNodeId == null || x.TaskNodeId == taskId), cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Status is not ("created" or "running"))
            return [DenyBoundary("agent_instance_unavailable", request.Claim)];

        var claim = request.Claim;

        // Dual-layer invariant, enforced as policy rather than as a call-site convention:
        // the governance layer coordinates, reviews, and proposes; it never executes a side
        // effect. A published operation agent that still declares a broad tool scope cannot
        // use it, because every external action must be owned by an execution-layer instance.
        if (string.Equals(instance.Layer, "operation", StringComparison.Ordinal))
            return [DenyBoundary("operation_layer_cannot_invoke_tools", claim)];

        var rules = new List<AuthorizationBoundary>
        {
            new("run", RunRules(run.PermissionMode, claim)),
            new("task", [new CapabilityRule("allow", "tool.invoke", claim.Action, claim.Resource)]),
            // The persisted instance definition is a narrower child scope than
            // the published AgentVersion. Keep it as an independent boundary so
            // a generated worker cannot inherit a parent's wildcard tools.
            new("agent_instance", AgentRules(instance, claim))
        };

        if (await TryInstanceDefinitionRulesAsync(instance, claim, cancellationToken).ConfigureAwait(false) is { } instanceScopeRules)
            rules.Add(new AuthorizationBoundary("agent_instance_scope", instanceScopeRules));
        else
            rules.Add(DenyBoundary("agent_instance_scope_missing", claim));

        var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (frozen is null) return [DenyBoundary("frozen_configuration_missing", claim)];
        rules.Add(new AuthorizationBoundary("tool_manifest", ManifestRules(frozen.Content, claim)));
        rules.AddRange(FrozenPolicyRules(frozen.Content, claim));

        if (instance.AgentVersionId is { } versionId && versionId != Guid.Empty)
        {
            await using var cfg = await _configuration.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var version = await cfg.AgentVersions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == versionId && x.AgentDefinitionId == instance.AgentDefinitionId && x.Status == "published"
                && x.TenantId == request.TenantId && x.WorkspaceId == request.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                if (!string.Equals(instance.AgentVersionHash, version.ContentHash, StringComparison.OrdinalIgnoreCase))
                    return [DenyBoundary("agent_version_changed", claim)];
                var versionRules = AgentVersionRules(version.SnapshotJson, claim);
                if (versionRules is not null)
                {
                    // The published immutable version is the authority: a narrow declared
                    // scope must never be re-widened by the frozen roster or instance copy.
                    rules.Add(new AuthorizationBoundary("agent_version", versionRules));
                }
                else if (TryFrozenAgentRules(frozen.Content, instance, claim, out var frozenRules))
                    rules.Add(new AuthorizationBoundary("agent_version", frozenRules));
                else if (await TryInstanceDefinitionRulesAsync(instance, claim, cancellationToken).ConfigureAwait(false) is { } instanceRules)
                    rules.Add(new AuthorizationBoundary("agent_version", instanceRules));
                else
                    rules.Add(DenyBoundary("agent_version_scope_unavailable", claim));
            }
            else if (TryFrozenAgentRules(frozen.Content, instance, claim, out var frozenRules))
            {
                // TOML built-ins and generated workers use deterministic virtual
                // version ids. Their binding is still immutable because it must
                // match the admitted frozen roster and content hash exactly.
                rules.Add(new AuthorizationBoundary("agent_version", frozenRules));
            }
            else
            {
                return [DenyBoundary("agent_version_changed", claim)];
            }
        }
        else
        {
            // Legacy/TOML instances do not have a trusted immutable version. They may
            // complete text-only work but cannot authorize an external tool action.
            rules.Add(DenyBoundary("agent_version_missing", claim));
        }

        return rules;
    }

    public async Task<Guid?> ResolveAgentVersionIdAsync(Guid tenantId, Guid workspaceId, Guid agentInstanceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _instances.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Instances.AsNoTracking().Where(x => x.Id == agentInstanceId && x.TenantId == tenantId && x.WorkspaceId == workspaceId)
            .Select(x => x.AgentVersionId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsSelfOrDescendantAsync(Guid tenantId, Guid workspaceId, Guid requesterAgentInstanceId, Guid candidateApproverAgentInstanceId, CancellationToken cancellationToken = default)
    {
        if (requesterAgentInstanceId == Guid.Empty || candidateApproverAgentInstanceId == Guid.Empty) return false;
        await using var db = await _instances.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Instances.AsNoTracking().Where(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId).Select(x => new { x.Id, x.ParentInstanceId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var parent = rows.ToDictionary(x => x.Id, x => x.ParentInstanceId);
        var current = candidateApproverAgentInstanceId;
        var seen = new HashSet<Guid>();
        while (current != Guid.Empty && seen.Add(current))
        {
            if (current == requesterAgentInstanceId) return true;
            current = parent.TryGetValue(current, out var next) && next is { } value ? value : Guid.Empty;
        }
        return false;
    }

    private static IReadOnlyList<CapabilityRule> RunRules(string permissionMode, CapabilityClaim claim) =>
        string.Equals(permissionMode, "deny", StringComparison.OrdinalIgnoreCase)
            ? [new CapabilityRule("deny", "tool.invoke", "*", "*")]
            : [new CapabilityRule("allow", "tool.invoke", claim.Action, claim.Resource)];

    private static IReadOnlyList<CapabilityRule> AgentRules(AgentInstanceRecord instance, CapabilityClaim claim) =>
        [new CapabilityRule("allow", "tool.invoke", claim.Action, claim.Resource)];

    private static IReadOnlyList<CapabilityRule> ManifestRules(string content, CapabilityClaim claim)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("toolManifest", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                var id = claim.Resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase) ? claim.Resource[7..] : string.Empty;
                if (tools.EnumerateArray().Any(x => x.TryGetProperty("id", out var p) && string.Equals(p.GetString(), id, StringComparison.OrdinalIgnoreCase)))
                    return [new CapabilityRule("allow", "tool.invoke", claim.Action, claim.Resource)];
            }
        }
        catch (JsonException) { }
        return [DenyBoundary("tool_not_in_frozen_manifest", claim).Rules[0]];
    }

    private static IReadOnlyList<AuthorizationBoundary> FrozenPolicyRules(string content, CapabilityClaim claim)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("policySnapshotHash", out var hash)
                || hash.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(hash.GetString()))
            {
                // Legacy frozen configuration has no trustworthy policy snapshot;
                // the Governance service will retain its compatibility behavior for
                // text-only runs, while external actions remain bounded by the
                // other Core-owned boundaries.
                return [];
            }

            var bundles = doc.RootElement.TryGetProperty("policyBundles", out var value)
                && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : [];
            if (bundles.Length == 0)
            {
                return [new AuthorizationBoundary(
                    "policy_snapshot:empty",
                    [new CapabilityRule("allow", claim.Capability, claim.Action, claim.Resource)])];
            }

            var result = new List<AuthorizationBoundary>(bundles.Length);
            foreach (var bundle in bundles)
            {
                var slug = bundle.TryGetProperty("slug", out var slugValue) && slugValue.ValueKind == JsonValueKind.String
                    ? slugValue.GetString()
                    : "unknown";
                var version = bundle.TryGetProperty("version", out var versionValue) && versionValue.TryGetInt32(out var number)
                    ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "0";
                var rules = new List<CapabilityRule>();
                if (bundle.TryGetProperty("rules", out var ruleValues) && ruleValues.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rule in ruleValues.EnumerateArray())
                    {
                        if (rule.ValueKind != JsonValueKind.Object) continue;
                        var effect = ReadString(rule, "effect");
                        var capability = ReadString(rule, "capability");
                        var action = ReadString(rule, "action");
                        var resource = ReadString(rule, "resourcePattern");
                        if (!string.IsNullOrWhiteSpace(effect) && !string.IsNullOrWhiteSpace(capability)
                            && !string.IsNullOrWhiteSpace(action) && !string.IsNullOrWhiteSpace(resource))
                        {
                            rules.Add(new CapabilityRule(effect, capability, action, resource));
                        }
                    }
                }
                result.Add(new AuthorizationBoundary($"policy:{slug}:v{version}", rules));
            }
            return result;
        }
        catch (JsonException)
        {
            return [DenyBoundary("policy_snapshot_invalid", claim)];
        }

        static string ReadString(JsonElement value, string name) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>
    /// Reads the immutable AgentVersion's own declared tool scope.  The writers
    /// (<c>BootstrapAgentDirectory</c>) and <c>AgentPackService</c> persist the
    /// scope as <c>tool_scope</c>, while hand-authored and legacy snapshots use
    /// <c>allowed_tools</c>/<c>tools</c>.  A snapshot that declares a scope is a final
    /// answer, including a deny: only a snapshot with no readable scope at all returns
    /// null so the caller may fall back to another immutable binding source.
    /// </summary>
    private static IReadOnlyList<CapabilityRule>? AgentVersionRules(string snapshot, CapabilityClaim claim)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshot);
            var id = claim.Resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase) ? claim.Resource[7..] : string.Empty;
            foreach (var name in new[] { "tool_scope", "allowed_tools", "tools" })
            {
                if (!TryProperty(doc.RootElement, name, out var scope) || scope.ValueKind != JsonValueKind.Array) continue;
                var declared = scope.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim())
                    .Where(x => x.Length != 0)
                    .ToArray();
                if (declared.Length == 0) return [new CapabilityRule("deny", "tool.invoke", "*", "*")];
                return declared.Any(x => x == "*" || string.Equals(x, id, StringComparison.OrdinalIgnoreCase))
                    ? [new CapabilityRule("allow", "tool.invoke", claim.Action, claim.Resource)]
                    : [new CapabilityRule("deny", "tool.invoke", "*", "*")];
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool TryFrozenAgentRules(
        string content,
        AgentInstanceRecord instance,
        CapabilityClaim claim,
        out IReadOnlyList<CapabilityRule> rules)
    {
        rules = [];
        try
        {
            using var doc = JsonDocument.Parse(content);
            foreach (var lane in new[] { "operationAgents", "executionAgents" })
            {
                if (!doc.RootElement.TryGetProperty(lane, out var agents) || agents.ValueKind != JsonValueKind.Array) continue;
                foreach (var agent in agents.EnumerateArray())
                {
                    if (!TryGuid(agent, "agentDefinitionId", out var definitionId) || definitionId != instance.AgentDefinitionId
                        || !TryGuid(agent, "agentVersionId", out var versionId) || versionId != instance.AgentVersionId)
                        continue;
                    if (!TryString(agent, "versionContentHash", out var hash)
                        || !string.Equals(hash, instance.AgentVersionHash, StringComparison.OrdinalIgnoreCase)) return false;
                    var tools = ReadStringArray(agent, "allowedTools");
                    var id = claim.Resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase) ? claim.Resource[7..] : string.Empty;
                    var allowed = tools.Any(x => x == "*" || string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
                    rules = allowed
                        ? [new CapabilityRule("allow", "tool.invoke", claim.Action, claim.Resource)]
                        : [new CapabilityRule("deny", "tool.invoke", "*", "*")];
                    return true;
                }
            }
        }
        catch (JsonException) { }
        return false;
    }

    private async Task<IReadOnlyList<CapabilityRule>?> TryInstanceDefinitionRulesAsync(
        AgentInstanceRecord instance,
        CapabilityClaim claim,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.DefinitionReference) || string.IsNullOrWhiteSpace(instance.DefinitionHash)) return null;
        try
        {
            await using var stream = await _content.OpenReadAsync(
                new ContentReference(instance.DefinitionReference, instance.DefinitionHash, instance.DefinitionLength, "application/json"),
                cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var tools = ReadStringArray(doc.RootElement, "AllowedTools");
            var id = claim.Resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase) ? claim.Resource[7..] : string.Empty;
            return tools.Any(x => x == "*" || string.Equals(x, id, StringComparison.OrdinalIgnoreCase))
                ? [new CapabilityRule("allow", "tool.invoke", claim.Action, claim.Resource)]
                : [new CapabilityRule("deny", "tool.invoke", "*", "*")];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (TryProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var property) || property.ValueKind != JsonValueKind.Array) return [];
        return property.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!.Trim()).Where(x => x.Length != 0).ToArray();
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        var snake = string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
        if (element.TryGetProperty(snake, out value)) return true;
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        if (element.TryGetProperty(camel, out value)) return true;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return element.TryGetProperty(pascal, out value);
    }

    private static AuthorizationBoundary DenyBoundary(string reason, CapabilityClaim claim) =>
        new(reason, [new CapabilityRule("deny", claim.Capability, "*", "*")]);
}
