using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

public sealed class PeersModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "peers";

    public void Register(ITinadecCoreBuilder builder)
    {
        PeerSecretKeys.ValidateAll();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddDbContextFactory<PeerDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<PeerDbContext>>();
        builder.Services.AddSingleton<IStorageMigrationParticipant, PeerMigrations>();
        builder.Services.AddSingleton<PeerPolicyService>();
        builder.Services.AddSingleton<IPeerPolicyService>(sp => sp.GetRequiredService<PeerPolicyService>());
        builder.Services.AddSingleton<PeerIdentityService>();
        builder.Services.AddSingleton<IPeerIdentityService>(sp => sp.GetRequiredService<PeerIdentityService>());
        builder.Services.AddSingleton<PeerPairingService>();
        builder.Services.AddSingleton<IPeerPairingService>(sp => sp.GetRequiredService<PeerPairingService>());
        builder.Services.AddSingleton<PeerShadowService>();
        builder.Services.AddSingleton<IPeerShadowService>(sp => sp.GetRequiredService<PeerShadowService>());
        builder.Services.AddSingleton<PeerIngressQueueService>();
        builder.Services.AddSingleton<IPeerIngressQueueService>(sp => sp.GetRequiredService<PeerIngressQueueService>());
        builder.Services.AddSingleton<PeerAttachmentStageService>();
        builder.Services.AddSingleton<IPeerAttachmentStageService>(sp => sp.GetRequiredService<PeerAttachmentStageService>());
        builder.Services.TryAddSingleton<IPeerProviderProxyContextStore>(PeerProviderProxyRuntimeRegistry.Default);
        builder.Services.TryAddSingleton<IPeerTransportLoopbackClient, HttpPeerTransportLoopbackClient>();
        builder.Services.AddSingleton<PeerDelegationService>();
        builder.Services.AddSingleton<IPeerDelegationCoordinator>(sp => sp.GetRequiredService<PeerDelegationService>());

        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.3.0",
            Dependencies = ["contracts", "abstractions", "persistence", "tenancy"],
            Capabilities =
            [
                "peer_local_identity",
                "peer_pairing_code",
                "peer_device_directory",
                "peer_grants",
                "peer_access_policies",
                "peer_shadow_workspaces",
                "peer_shadow_sessions",
                "peer_ingress_queue",
                "peer_staged_attachments",
                "peer_agent_delegation"
            ],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
