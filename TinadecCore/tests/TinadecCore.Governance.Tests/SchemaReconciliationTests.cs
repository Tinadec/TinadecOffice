using Microsoft.EntityFrameworkCore;
using TinadecCore.Governance;
using TinadecCore.Persistence;

namespace TinadecCore.Governance.Tests;

/// <summary>
/// Regression coverage for the upgraded-database schema drift found on
/// 2026-08-29: pre-20260822 capability leases persisted nonce material inline
/// (<c>nonce</c>, NOT NULL) and lacked <c>nonce_secret_reference</c>. Governance
/// tables are bootstrap-owned (no migration history), so the schema
/// bootstrapper must reconcile columns of existing tables; approval decisions
/// otherwise fail with "table capability_leases has no column named
/// nonce_secret_reference" and runs stay stuck in awaiting_user.
/// </summary>
public sealed class SchemaReconciliationTests
{
    [Fact]
    public async Task EnsureTables_ReconcilesLegacyCapabilityLeaseShape_AndLeaseInsertSucceeds()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-schema-recon-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<GovernanceDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var factory = new TestDbContextFactory(options);

            await using (var db = await factory.CreateDbContextAsync())
            {
                // A database created before 20260822: capability_leases exists in
                // its legacy shape (inline NOT NULL nonce, no secret reference).
                await db.Database.ExecuteSqlRawAsync("""
                    create table capability_leases (
                        id text primary key,
                        tenant_id text not null,
                        workspace_id text not null,
                        subject_principal_id text not null,
                        subject_agent_instance_id text,
                        capability_grant_id text,
                        permission_request_id text,
                        capability text not null,
                        action text not null,
                        resource text not null,
                        run_id text,
                        task_id text,
                        nonce_hash text not null,
                        nonce text not null,
                        policy_snapshot_hash text not null,
                        status text not null,
                        max_uses integer not null,
                        use_count integer not null,
                        revision integer not null,
                        starts_at text not null,
                        expires_at text not null,
                        starts_at_unix_milliseconds integer not null,
                        expires_at_unix_milliseconds integer not null,
                        revoked_at text,
                        revoke_reason text,
                        created_at text not null,
                        updated_at text not null
                    );
                    """);

                // The production startup path: migrations (none for governance)
                // followed by the idempotent bootstrap with column reconciliation.
                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);

                var columns = await db.Database
                    .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('capability_leases')")
                    .ToListAsync();
                Assert.Contains("nonce_secret_reference", columns);
                Assert.DoesNotContain("nonce", columns);
            }

            // The original failure mode: inserting a lease through EF (which no
            // longer maps the inline nonce) must succeed on the reconciled table.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var now = DateTimeOffset.UtcNow;
                db.CapabilityLeases.Add(new CapabilityLeaseRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = Guid.NewGuid(),
                    WorkspaceId = Guid.NewGuid(),
                    SubjectPrincipalId = Guid.NewGuid(),
                    Capability = "files",
                    Action = "write",
                    Resource = "workspace://sandbox/hello.txt",
                    NonceHash = "hash",
                    NonceSecretReference = "lease_nonce_test",
                    PolicySnapshotHash = "policy",
                    Status = "active",
                    MaxUses = 1,
                    UseCount = 0,
                    Revision = 1,
                    StartsAt = now,
                    ExpiresAt = now.AddMinutes(5),
                    StartsAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
                    ExpiresAtUnixMilliseconds = now.AddMinutes(5).ToUnixTimeMilliseconds(),
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync();
                Assert.Equal(1, await db.CapabilityLeases.CountAsync());
            }
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }
}
