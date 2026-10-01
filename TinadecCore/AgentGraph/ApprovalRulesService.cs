using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentGraph;

/// <summary>
/// Standing approvals a person gave, over the durable rule rows (todo E7). The session (or workspace)
/// scope is a fact of the row, re-checked on every verification: a call cannot point at a rule from
/// another session and a run cannot inherit one through its arguments.
/// </summary>
internal sealed class ApprovalRulesService(
    IDbContextFactory<AgentGraphDbContext> factory,
    IServiceProvider services,
    ITenantContextAccessor tenant) : IApprovalRules
{
    public async Task<IReadOnlyList<ApprovalRuleView>> ListAsync(Guid? sessionId, CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ApprovalRules.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && !x.Revoked
                && (sessionId == null || x.SessionId == null || x.SessionId == sessionId))
            .OrderBy(x => x.CreatedAtUnixMs)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToView).ToArray();
    }

    public async Task<ApprovalRuleView> CreateAsync(ApprovalRuleCreate rule, CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        if (!ApprovalRuleKinds.All.Contains(rule.Kind, StringComparer.Ordinal))
            throw new ApprovalRuleException(400, "invalid_request", $"kind must be one of: {string.Join(", ", ApprovalRuleKinds.All)}.");
        if (string.IsNullOrWhiteSpace(rule.ToolId))
            throw new ApprovalRuleException(400, "invalid_request", "tool_id is required.");
        string? pattern = null;
        if (rule.Kind == ApprovalRuleKinds.CommandPrefix)
        {
            // Only shells execute command text, so only they can be prefix-matched.
            if (!CommandPrefixRules.IsRuleTool(rule.ToolId))
                throw new ApprovalRuleException(400, "invalid_request", $"A command_prefix rule's tool_id must be one of: {string.Join(", ", CommandPrefixRules.Tools)}.");
            pattern = rule.Pattern?.Trim() ?? string.Empty;
            if (CommandPrefixRules.PrefixError(pattern) is { } error)
                throw new ApprovalRuleException(400, "invalid_request", error);
        }
        else if (!string.IsNullOrWhiteSpace(rule.Pattern))
        {
            throw new ApprovalRuleException(400, "invalid_request", "A delegated-tool opt-in takes no pattern; it covers the tool, not a command.");
        }
        var toolId = rule.ToolId.Trim();
        if (rule.Kind == ApprovalRuleKinds.DelegateTool
            && (!DelegatedToolOptInRules.IsEligibleTool(toolId) || rule.SessionId is null || rule.SessionId == Guid.Empty))
            throw new ApprovalRuleException(400, "invalid_request", "A delegate_tool rule requires a session_id and tool_id shell or command_run; risk limits still apply.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var duplicate = await db.ApprovalRules.AsNoTracking().AnyAsync(x =>
                x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && !x.Revoked
                && x.SessionId == rule.SessionId && x.Kind == rule.Kind && x.ToolId == toolId && x.Pattern == pattern,
            cancellationToken).ConfigureAwait(false);
        if (duplicate)
            throw new ApprovalRuleException(409, "approval_rule_conflict", "An active rule with the same kind, tool and scope already exists.");
        var now = DateTimeOffset.UtcNow;
        var row = new ApprovalRuleRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SessionId = rule.SessionId,
            Kind = rule.Kind,
            ToolId = toolId,
            Pattern = pattern,
            CreatedByPrincipalId = scope.PrincipalId,
            CreatedAt = now,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        };
        db.ApprovalRules.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(row);
    }

    public async Task<bool> RevokeAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var revoked = await db.ApprovalRules
            .Where(x => x.Id == ruleId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && !x.Revoked)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Revoked, true)
                .SetProperty(x => x.RevokedAt, now), cancellationToken).ConfigureAwait(false);
        return revoked == 1;
    }

    public async Task<ApprovalRuleView?> MatchCommandAsync(Guid sessionId, string toolId, string command, CancellationToken cancellationToken = default)
    {
        if (!CommandPrefixRules.IsRuleTool(toolId)) return null;
        foreach (var rule in await ActiveAsync(sessionId, toolId, cancellationToken).ConfigureAwait(false))
            if (rule.Pattern is { } prefix && CommandPrefixRules.Matches(command, prefix))
                return ToView(rule);
        return null;
    }

    public async Task<bool> VerifyCommandRuleAsync(Guid ruleId, Guid runId, string toolId, CancellationToken cancellationToken = default)
    {
        // The caller names the rule; this service decides what it covers. The run's session is read
        // from its own row, so a run can only ever land on rules scoped to its session or workspace.
        var sessionId = await RunSessionAsync(runId, cancellationToken).ConfigureAwait(false);
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rule = await db.ApprovalRules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ruleId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && !x.Revoked, cancellationToken).ConfigureAwait(false);
        if (rule is null || rule.Kind != ApprovalRuleKinds.CommandPrefix || rule.ToolId != toolId
            || (rule.SessionId is { } bound && bound != sessionId))
            return false;
        return true;
    }

    public Task<ApprovalRuleView> RecordUseAsync(Guid ruleId, CancellationToken cancellationToken = default) =>
        CountUseAsync(ruleId, cancellationToken);

    public async Task<bool> IsDelegatedToolAsync(Guid runId, string toolId, CancellationToken cancellationToken = default)
    {
        // Check again when reading: old, overly broad rules must never acquire authority.
        if (!DelegatedToolOptInRules.IsEligibleTool(toolId)) return false;
        var sessionId = await RunSessionAsync(runId, cancellationToken).ConfigureAwait(false);
        if (sessionId is null || sessionId == Guid.Empty) return false;
        foreach (var rule in await ActiveAsync(sessionId, toolId, cancellationToken).ConfigureAwait(false))
            if (rule.Kind == ApprovalRuleKinds.DelegateTool && rule.SessionId == sessionId)
                return true;
        return false;
    }

    private async Task<List<ApprovalRuleRecord>> ActiveAsync(Guid? sessionId, string toolId, CancellationToken cancellationToken)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // An unknown session (e.g. a run that cannot be resolved) degrades to workspace-wide rules
        // only — never to "every rule". Anything wider would honor another session's rule.
        if (sessionId is null)
            return await db.ApprovalRules.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && !x.Revoked && x.ToolId == toolId && x.SessionId == null)
                .OrderBy(x => x.CreatedAtUnixMs)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await db.ApprovalRules.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && !x.Revoked && x.ToolId == toolId
                && (x.SessionId == null || x.SessionId == sessionId))
            .OrderBy(x => x.CreatedAtUnixMs)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The run's session from the run row itself: a call's session id is never trusted.
    /// Resolved through the lifecycle port because that module registers after this one.</summary>
    private async Task<Guid?> RunSessionAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(ILifecycleManager)) is not ILifecycleManager lifecycle) return null;
        try
        {
            var run = await lifecycle.GetRunStateAsync(runId.ToString("N"), cancellationToken).ConfigureAwait(false);
            return Guid.TryParse(run.SessionId, out var sessionId) ? sessionId : null;
        }
        catch { return null; }
    }

    private async Task<ApprovalRuleView> CountUseAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ApprovalRules.SingleAsync(x => x.Id == ruleId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        row.UseCount++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(row);
    }

    private static ApprovalRuleView ToView(ApprovalRuleRecord row) =>
        new(row.Id, row.Kind, row.ToolId, row.Pattern, row.SessionId, row.CreatedByPrincipalId, row.CreatedAt, row.UseCount);
}
