using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.AgentConfiguration;

/// <summary>
/// Stub for isolated agent/mode/prompt configuration.
/// Next stage will implement draft/version/E-Tag, explicit publish with frozen immutable version,
/// soft archive, and mode-topology validation (operation ≥1 meeting, execution ≥1, cross-layer reuse warning).
/// </summary>
public interface IAgentConfigurationService
{
    Task<object> CreateDraftAsync(string kind, object payload, CancellationToken cancellationToken = default);
    Task<object> PublishAsync(Guid entityId, long ifMatchRevision, CancellationToken cancellationToken = default);
}

public sealed class AgentConfigurationService : IAgentConfigurationService
{
    private readonly IDbContextFactory<AgentConfigurationDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenant;
    private readonly IContentStore _content;

    public AgentConfigurationService(IDbContextFactory<AgentConfigurationDbContext> dbFactory, ITenantContextAccessor tenant, IContentStore content)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _content = content;
    }

    public Task<object> CreateDraftAsync(string kind, object payload, CancellationToken cancellationToken = default)
    {
        // ponytail: stub — next stage implements workspace-scoped draft with single-draft invariant,
        // layer guard (reject planning), and capability/model/tool validation.
        throw new NotImplementedException("AgentConfiguration draft creation is not yet implemented.");
    }

    public Task<object> PublishAsync(Guid entityId, long ifMatchRevision, CancellationToken cancellationToken = default)
    {
        // ponytail: stub — next stage freezes immutable version, validates mode dual-lane topology
        // (operation ≥1 meeting + execution ≥1, cross-layer reuse → warning), bumps revision/ETag, archives prior.
        throw new NotImplementedException("AgentConfiguration publish is not yet implemented.");
    }

    // ponytail: helper — only operation/execution allowed (rejects planning).
    public static void ValidateLayer(string layer)
    {
        var normalized = layer?.Trim().ToLowerInvariant();
        if (normalized is not "operation" and not "execution")
            throw new ArgumentException("Agent layer must be 'operation' or 'execution'; 'planning' is not allowed.", nameof(layer));
    }
}
