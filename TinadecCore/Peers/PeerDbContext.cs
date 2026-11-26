using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

public sealed class PeerDbContext : DbContext
{
    public PeerDbContext(DbContextOptions<PeerDbContext> options) : base(options) { }

    public DbSet<PeerInboundSettingsRecord> InboundSettings => Set<PeerInboundSettingsRecord>();
    public DbSet<PeerDeviceRecord> Devices => Set<PeerDeviceRecord>();
    public DbSet<PeerGrantRecord> Grants => Set<PeerGrantRecord>();
    public DbSet<PeerAccessPolicyRecord> AccessPolicies => Set<PeerAccessPolicyRecord>();
    public DbSet<PeerShadowWorkspaceRecord> ShadowWorkspaces => Set<PeerShadowWorkspaceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PeerInboundSettingsRecord>(entity =>
        {
            entity.ToTable("peer_inbound_settings", t =>
            {
                t.HasCheckConstraint("ck_peer_inbound_settings_singleton", "id = 1");
                t.HasCheckConstraint("ck_peer_inbound_settings_enabled", "enabled IN (0, 1)");
                t.HasCheckConstraint("ck_peer_inbound_settings_port", "listen_port >= 1 AND listen_port <= 65535");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.LocalDeviceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Enabled).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.ListenPort).HasDefaultValue(PeerConstants.DefaultListenPort).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
        });

        modelBuilder.Entity<PeerDeviceRecord>(entity =>
        {
            entity.ToTable("peer_devices", t =>
            {
                t.HasCheckConstraint("ck_peer_devices_transport", "transport IN ('lan', 'relay')");
                t.HasCheckConstraint("ck_peer_devices_connection_desired", "connection_desired IN (0, 1)");
                t.HasCheckConstraint("ck_peer_devices_adopt_sessions", "adopt_sessions IN (0, 1)");
                t.HasCheckConstraint(
                    "ck_peer_devices_last_state",
                    "last_state IN ('disconnected', 'connecting', 'handshaking', 'ready', 'version_incompatible', 'unauthorized', 'fingerprint_mismatch', 'error')");
            });
            entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.DeviceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Transport).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Address).HasMaxLength(512).IsRequired();
            entity.Property(x => x.RemoteDeviceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CertificateFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AccountIdentity).HasMaxLength(256);
            entity.Property(x => x.OrganizationId).HasMaxLength(256);
            entity.Property(x => x.ConnectionDesired).HasDefaultValue(1).IsRequired();
            entity.Property(x => x.AdoptSessions).HasDefaultValue(1).IsRequired();
            entity.Property(x => x.LastState).HasMaxLength(32).HasDefaultValue("disconnected").IsRequired();
            entity.Property(x => x.LastFailureCode).HasMaxLength(128);
            entity.Property(x => x.AccessRevision).HasMaxLength(128);

            entity.HasIndex(x => new { x.Transport, x.RemoteDeviceId })
                .HasDatabaseName("ix_peer_devices_identity")
                .IsUnique()
                .HasFilter("archived_at IS NULL");
            entity.HasIndex(x => new { x.ArchivedAt, x.UpdatedAt })
                .HasDatabaseName("ix_peer_devices_active");
        });

        modelBuilder.Entity<PeerGrantRecord>(entity =>
        {
            entity.ToTable("peer_grants", t =>
            {
                t.HasCheckConstraint("ck_peer_grants_transport", "transport IN ('lan', 'relay')");
            });
            entity.HasKey(x => x.GrantId);
            entity.Property(x => x.GrantId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ControllerDeviceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Transport).HasMaxLength(16).IsRequired();
            entity.Property(x => x.CertificateFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AccountIdentity).HasMaxLength(256);
            entity.Property(x => x.OrganizationId).HasMaxLength(256);

            entity.HasIndex(x => new { x.Transport, x.ControllerDeviceId })
                .HasDatabaseName("ix_peer_grants_controller")
                .IsUnique()
                .HasFilter("revoked_at IS NULL");
            entity.HasIndex(x => new { x.RevokedAt, x.GrantedAt })
                .HasDatabaseName("ix_peer_grants_active");
        });

        modelBuilder.Entity<PeerAccessPolicyRecord>(entity =>
        {
            entity.ToTable("peer_access_policies", t =>
            {
                t.HasCheckConstraint("ck_peer_access_policies_role", "role IN ('observer', 'operator', 'owner')");
                t.HasCheckConstraint("ck_peer_access_policies_workspace_mode", "workspace_mode IN ('all', 'selected')");
                t.HasCheckConstraint(
                    "ck_peer_access_policies_workspace_ids",
                    "(workspace_mode = 'all' AND workspace_ids_json IS NULL) OR (workspace_mode = 'selected' AND workspace_ids_json IS NOT NULL)");
            });
            entity.HasKey(x => x.ControllerDeviceId);
            entity.Property(x => x.ControllerDeviceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired();
            entity.Property(x => x.WorkspaceMode).HasMaxLength(32).IsRequired();
            entity.Property(x => x.WorkspaceIdsJson).HasMaxLength(65536);
            entity.Property(x => x.AllowNonWorkspaceSessions).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.SyncAllSessions).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.AllowAgentDelegation).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.AllowProviderProxy).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.Revision).HasMaxLength(128).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
        });

        modelBuilder.Entity<PeerShadowWorkspaceRecord>(entity =>
        {
            entity.ToTable("peer_shadow_workspaces");
            entity.HasKey(x => x.AliasWorkspaceId);
            entity.Property(x => x.AliasWorkspaceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PeerDeviceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RemoteProjectId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RemoteRootPath).HasMaxLength(2048);
            entity.Property(x => x.Visible).HasDefaultValue(1).IsRequired();
            entity.Property(x => x.SyncedAt).IsRequired();

            entity.HasOne<PeerDeviceRecord>()
                .WithMany()
                .HasForeignKey(x => x.PeerDeviceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.PeerDeviceId, x.RemoteProjectId })
                .HasDatabaseName("ix_peer_shadow_ws_identity")
                .IsUnique();
        });

        modelBuilder.UseTinadecSnakeCase();
    }
}
