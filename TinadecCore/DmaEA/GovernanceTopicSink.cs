using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Which standing members a fact reaches: the run's declared subscribers (relationship file
/// <c>subscriptions</c>) that are neither a built-in behaviour (those run inside the engine) nor the
/// conversation identity (it hears the user, not the engine). One rule, used by the engine for the
/// facts it raises and by <see cref="GovernanceTopicSink"/> for the facts the tool layer raises.
/// </summary>
internal static class GovernanceSubscribers
{
    public static IReadOnlyList<RuntimeAgentDefinition> Standing(FrozenRunConfigurationV1 configuration) =>
        configuration.OperationAgents
            .Where(agent => agent.Enabled && agent.Triggers.Count > 0 && !agent.DirectUserOutput
                && OperationalRoles.BuiltInKind(agent) is null)
            .ToArray();

    public static IReadOnlyList<RuntimeAgentDefinition> For(FrozenRunConfigurationV1 configuration, string topic) =>
        configuration.Triggers.Enabled
            ? Standing(configuration).Where(agent => agent.Triggers.Contains(topic, StringComparer.OrdinalIgnoreCase)).ToArray()
            : [];
}

/// <summary>
/// Delivers a governance fact raised outside the run engine to the run's standing subscribers. Loads
/// the frozen configuration once per run (it never changes) and hands each subscriber a notice through
/// the organization — the same durable, coalesced, budgeted path the engine's own facts take.
/// Best-effort by contract: the caller is in the middle of a tool call and must never fail because a
/// reviewer could not be told.
/// </summary>
public sealed class GovernanceTopicSink : IGovernanceTopicSink
{
    private const int CacheLimit = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILifecycleManager _lifecycle;
    private readonly IServiceProvider _services;
    private readonly ILogger<GovernanceTopicSink>? _logger;
    private readonly ConcurrentDictionary<Guid, FrozenRunConfigurationV1?> _configurations = new();

    public GovernanceTopicSink(ILifecycleManager lifecycle, IServiceProvider services, ILogger<GovernanceTopicSink>? logger = null)
    {
        _lifecycle = lifecycle;
        _services = services;
        _logger = logger;
    }

    public async Task RaiseAsync(GovernanceTopicSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        try
        {
            if (!GovernanceTopics.IsKnown(signal.Topic)) return;
            if (_services.GetService(typeof(ISessionOrganization)) is not ISessionOrganization organization) return;
            var configuration = await ConfigurationAsync(signal.RunId, cancellationToken).ConfigureAwait(false);
            if (configuration is null) return;
            var subscribers = GovernanceSubscribers.For(configuration, signal.Topic);
            if (subscribers.Count == 0) return;
            var state = await _lifecycle.GetRunStateAsync(signal.RunId.ToString(), cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(state.TenantId, out var tenantId) || !Guid.TryParse(state.WorkspaceId, out var workspaceId)
                || !Guid.TryParse(state.InitiatedByPrincipalId, out var principalId)) return;
            var scope = new OrganizationScope(tenantId, workspaceId, principalId, signal.SessionId);
            foreach (var agent in subscribers)
            {
                await organization.NotifyAsync(new OrganizationNotice(scope, signal.RunId, signal.Topic, agent.Id, signal.Summary,
                    signal.SubjectKind, signal.SubjectId), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger?.TryLogWarning(ex, "Could not deliver governance fact {Topic} of run {RunId}.", signal.Topic, signal.RunId);
        }
    }

    private async Task<FrozenRunConfigurationV1?> ConfigurationAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (_configurations.TryGetValue(runId, out var cached)) return cached;
        var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        FrozenRunConfigurationV1? configuration = null;
        if (frozen is not null && string.Equals(frozen.SchemaVersion, FrozenRunConfigurationV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            try { configuration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(frozen.Content, Json); }
            catch (JsonException) { configuration = null; }
        }
        // A frozen body never changes, so the cache is only ever cleared for size, never invalidated.
        if (_configurations.Count >= CacheLimit) _configurations.Clear();
        _configurations[runId] = configuration;
        return configuration;
    }
}
