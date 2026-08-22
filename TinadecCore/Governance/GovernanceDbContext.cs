using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Governance;

public sealed class GovernanceDbContext : DbContext
{
    public GovernanceDbContext(DbContextOptions<GovernanceDbContext> options) : base(options) { }

    public DbSet<PolicyBundleRecord> PolicyBundles => Set<PolicyBundleRecord>();
    public DbSet<PolicyVersionRecord> PolicyVersions => Set<PolicyVersionRecord>();
    public DbSet<CapabilityGrantRecord> CapabilityGrants => Set<CapabilityGrantRecord>();
    public DbSet<ApprovalDelegationRecord> ApprovalDelegations => Set<ApprovalDelegationRecord>();
    public DbSet<PermissionRequestRecord> PermissionRequests => Set<PermissionRequestRecord>();
    public DbSet<AuthorizationDecisionRecord> AuthorizationDecisions => Set<AuthorizationDecisionRecord>();
    public DbSet<CapabilityLeaseRecord> CapabilityLeases => Set<CapabilityLeaseRecord>();
    public DbSet<LeaseConsumptionRecord> LeaseConsumptions => Set<LeaseConsumptionRecord>();
    public DbSet<DelegationUseRecord> DelegationUses => Set<DelegationUseRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PolicyBundleRecord>(entity =>
        {
            entity.ToTable("policy_bundles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ScopeKind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ScopeKind, x.ScopeId, x.Slug }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ScopeKind, x.ScopeId, x.Status });
        });

        modelBuilder.Entity<PolicyVersionRecord>(entity =>
        {
            entity.ToTable("governance_policy_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ScopeKind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.RulesJson).HasMaxLength(65536).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.PolicyBundleId, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ScopeKind, x.ScopeId, x.Status, x.CreatedAt });
        });

        modelBuilder.Entity<CapabilityGrantRecord>(entity =>
        {
            entity.ToTable("capability_grants");
            entity.HasKey(x => x.Id);
            ConfigureClaim(entity);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(4096);
            entity.Property(x => x.RevokeReason).HasMaxLength(4096);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SubjectPrincipalId, x.Status, x.ExpiresAtUnixMilliseconds });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SubjectAgentInstanceId, x.Status, x.ExpiresAtUnixMilliseconds });
            entity.HasIndex(x => x.ParentGrantId);
        });

        modelBuilder.Entity<ApprovalDelegationRecord>(entity =>
        {
            entity.ToTable("approval_delegations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RulesJson).HasMaxLength(65536).IsRequired();
            entity.Property(x => x.RulesHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.MaxRisk).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.RevokeReason).HasMaxLength(4096);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.DelegateAgentVersionId, x.Status, x.ExpiresAtUnixMilliseconds });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.DelegateAgentInstanceId, x.Status });
        });

        modelBuilder.Entity<PermissionRequestRecord>(entity =>
        {
            entity.ToTable("permission_requests");
            entity.HasKey(x => x.Id);
            ConfigureClaim(entity);
            entity.Property(x => x.BoundariesJson).HasMaxLength(65536).IsRequired();
            entity.Property(x => x.Risk).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Rationale).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.PolicySnapshotHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EscalationChainJson).HasMaxLength(8192).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.ExpiresAtUnixMilliseconds });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.RunId, x.TaskId, x.CreatedAt });
        });

        modelBuilder.Entity<AuthorizationDecisionRecord>(entity =>
        {
            entity.ToTable("authorization_decisions");
            entity.HasKey(x => x.Id);
            ConfigureClaim(entity);
            entity.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.DecisionSource).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PolicySnapshotHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.EvaluationInputHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SubjectPrincipalId, x.CreatedAt });
            entity.HasIndex(x => new { x.PermissionRequestId, x.CreatedAt });
        });

        modelBuilder.Entity<CapabilityLeaseRecord>(entity =>
        {
            entity.ToTable("capability_leases");
            entity.HasKey(x => x.Id);
            ConfigureClaim(entity);
            entity.Property(x => x.NonceHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.NonceSecretReference).HasMaxLength(512).IsRequired();
            entity.Property(x => x.PolicySnapshotHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.RevokeReason).HasMaxLength(4096);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SubjectPrincipalId, x.Status, x.ExpiresAtUnixMilliseconds });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.RunId, x.TaskId, x.Status });
        });

        modelBuilder.Entity<LeaseConsumptionRecord>(entity =>
        {
            entity.ToTable("capability_lease_consumptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.InputHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.CapabilityLeaseId, x.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<DelegationUseRecord>(entity =>
        {
            entity.ToTable("approval_delegation_uses");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ApprovalDelegationId, x.PermissionRequestId }).IsUnique();
        });

        modelBuilder.UseTinadecSnakeCase();
    }

    private static void ConfigureClaim<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        entity.Property("Capability").HasMaxLength(256).IsRequired();
        entity.Property("Action").HasMaxLength(256).IsRequired();
        entity.Property("Resource").HasMaxLength(2048).IsRequired();
    }
}
