using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Governance;

public sealed class GovernanceModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "governance";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IAuthorizationContextResolver, FailClosedAuthorizationContextResolver>();
        builder.Services.AddDbContextFactory<GovernanceDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<GovernanceDbContext>>();
        builder.Services.AddSingleton<GovernanceService>();
        builder.Services.AddSingleton<IPolicyDecisionPoint>(sp => sp.GetRequiredService<GovernanceService>());
        builder.Services.AddSingleton<IAuthorizationService>(sp => sp.GetRequiredService<GovernanceService>());
        builder.Services.AddSingleton<IPolicySnapshotProvider>(sp => sp.GetRequiredService<GovernanceService>());
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence", "tenancy"],
            Capabilities = ["policy_bundle", "capability_grant", "permission_request", "approval_delegation", "authorization_decision", "capability_lease"],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}

internal sealed class FailClosedAuthorizationContextResolver : IAuthorizationContextResolver
{
    public Task<IReadOnlyList<AuthorizationBoundary>> ResolveBoundariesAsync(
        AuthorizationContextRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuthorizationBoundary>>(
            [new AuthorizationBoundary("trusted_context_unavailable", [])]);

    public Task<Guid?> ResolveAgentVersionIdAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid agentInstanceId,
        CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

    public Task<bool> IsSelfOrDescendantAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid requesterAgentInstanceId,
        Guid candidateApproverAgentInstanceId,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
}
