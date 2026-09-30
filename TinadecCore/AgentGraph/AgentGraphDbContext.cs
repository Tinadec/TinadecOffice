using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentGraph;

/// <summary>
/// Persistence for graph-shaped governance: the resource ledger and the per-gate approval records a
/// two-gate decision needs. One context, because both are the state of one concern — who may act on
/// what, and who decided. Governance wakes are not here: a governance role is a member of the
/// session's TinaChat organization, and a wake is a delivery into its inbox (TinaChat's queue).
/// </summary>
public sealed class AgentGraphDbContext(DbContextOptions<AgentGraphDbContext> options) : DbContext(options)
{
    public DbSet<ResourceLeaseRecord> ResourceLeases => Set<ResourceLeaseRecord>();
    public DbSet<ApprovalGateRecord> ApprovalGates => Set<ApprovalGateRecord>();
    public DbSet<EvidenceEntryRecord> Evidence => Set<EvidenceEntryRecord>();
    public DbSet<EnvironmentRecord> Environments => Set<EnvironmentRecord>();
    public DbSet<ApprovalRuleRecord> ApprovalRules => Set<ApprovalRuleRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ResourceLeaseRecord>(e =>
        {
            e.ToTable("agent_graph_resource_leases");
            e.HasKey(x => x.Id);
            // The overlap query is "who else holds this key, an ancestor of it, or something under it,
            // right now". It reads active rows by normalized key, so that column leads.
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.KeyNorm });
            e.HasIndex(x => new { x.RunId, x.Status });
            e.HasIndex(x => x.SessionId);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.ResourceKey).HasMaxLength(512);
            e.Property(x => x.KeyNorm).HasMaxLength(512);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Purpose).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(512);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ApprovalGateRecord>(e =>
        {
            e.ToTable("agent_graph_approval_gates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PermissionRequestId, x.GateIndex }).IsUnique();
            e.HasIndex(x => new { x.RunId, x.Status });
            e.Property(x => x.GateKind).HasMaxLength(48);
            e.Property(x => x.DeciderAgent).HasMaxLength(128);
            e.Property(x => x.ToolId).HasMaxLength(128);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(1024);
        });
        model.Entity<EvidenceEntryRecord>(e =>
        {
            e.ToTable("agent_graph_evidence");
            e.HasKey(x => x.Id);
            // One entry per source per session: re-archiving a task result or a report is a no-op.
            e.HasIndex(x => new { x.SessionId, x.SourceKey }).IsUnique();
            // Recall reads a session newest first; the indexer reads what is due.
            e.HasIndex(x => new { x.SessionId, x.CreatedAtUnixMs });
            e.HasIndex(x => new { x.VectorStatus, x.VectorNextAttemptAtUnixMs });
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Author).HasMaxLength(128);
            e.Property(x => x.SourceKey).HasMaxLength(256);
            e.Property(x => x.VectorStatus).HasMaxLength(32);
            e.Property(x => x.VectorModelId).HasMaxLength(256);
        });
        model.Entity<EnvironmentRecord>(e =>
        {
            e.ToTable("agent_graph_environments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.Description).HasMaxLength(2048);
            e.Property(x => x.ConnectionJson).HasMaxLength(8192);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ApprovalRuleRecord>(e =>
        {
            e.ToTable("agent_graph_approval_rules");
            e.HasKey(x => x.Id);
            // Matching reads the active rules of one tool in one workspace; the session narrows after.
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ToolId, x.Revoked });
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.ToolId).HasMaxLength(128);
            e.Property(x => x.Pattern).HasMaxLength(256);
        });
    }
}

/// <summary>A standing approval a person gave (todo E7). Revoked rows stay for the audit trail.</summary>
public sealed class ApprovalRuleRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>Null: the whole workspace.</summary>
    public Guid? SessionId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string? Pattern { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public bool Revoked { get; set; }
    public int UseCount { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// One environment the steward can hand out (todo E1). Occupancy is not here: it is the resource
/// ledger's <c>environment</c> leases, keyed <c>&lt;key&gt;#&lt;slot&gt;</c>, so it ends with its run.
/// </summary>
public sealed class EnvironmentRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ConnectionJson { get; set; } = "{}";
    public int Capacity { get; set; } = 1;
    public string Status { get; set; } = EnvironmentStatuses.Available;
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One claim on a finite resource. <see cref="ResourceKey"/> is the canonical spelling a person reads;
/// <see cref="KeyNorm"/> is the same key case-folded, which is what every comparison uses, so two
/// spellings of one resource (<c>C:\Repo\a.ts</c>, <c>c:/repo/a.ts</c>) are one row's business.
///
/// There is no expiry. A lease lives exactly as long as its holder run: released at task or run
/// terminal, and treated as dead — and released — the moment a conflict check finds its run terminal,
/// which is what reclaims a lease a crashed host never got to release.
/// </summary>
public sealed class ResourceLeaseRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? AgentInstanceId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;
    public string KeyNorm { get; set; } = string.Empty;
    public bool Exclusive { get; set; }
    public string Status { get; set; } = ResourceLeaseStatuses.Active;
    /// <summary>One of <see cref="ResourceLeasePurposes"/>: a tool's claim, a declared write scope, or a steward's assignment.</summary>
    public string Purpose { get; set; } = ResourceLeasePurposes.Tool;
    public string Reason { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

/// <summary>
/// One gate of one approval. An approval may need several independent decisions (a reviewer's, then
/// the conversation identity's — the user picks which, as a <c>delegate-*</c> permission mode), and
/// each leaves its own evidence: "two contexts agreed" is only auditable if each decision records what
/// it was shown.
/// </summary>
public sealed class ApprovalGateRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>
    /// The approval this gate decides — the id the approval card shows (a tool approval). Several gates
    /// share one approval. (The column predates delegated modes; it names the request being decided.)
    /// </summary>
    public Guid PermissionRequestId { get; set; }
    public Guid? RunId { get; set; }
    /// <summary>0-based order. A gate is asked only after every gate before it approved.</summary>
    public int GateIndex { get; set; }
    public string GateKind { get; set; } = ApprovalGateKinds.Human;
    public Guid? DeciderAgentInstanceId { get; set; }
    /// <summary>Which role decided: an agent id from the run's frozen roster, or Core's own reviewer.</summary>
    public string? DeciderAgent { get; set; }
    public string? ToolId { get; set; }
    public string Status { get; set; } = ApprovalGateStatuses.Pending;
    public string? Reason { get; set; }
    /// <summary>What this gate was shown. The point of an independent review is that this is bounded.</summary>
    public string ContextEvidenceJson { get; set; } = "{}";
    public long Revision { get; set; }
    /// <summary>When a host took the gate to evaluate it; a claim older than the lapse is taken again (crashed host).</summary>
    public long? ClaimedAtUnixMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// One piece of evidence, verbatim (bounded): a task's result, a report, a member's conclusion or a
/// summary. The row is the durable fact; the semantic index is a derived view that may lag, be
/// missing (no embedding model) or be rebuilt — recall falls back to keywords over these rows.
/// </summary>
public sealed class EvidenceEntryRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>The session's project; null for a projectless session, which is then keyword-only.</summary>
    public Guid? ProjectId { get; set; }
    public Guid SessionId { get; set; }
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string Content { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    /// <summary>pending · indexed · unavailable (no embedding model; retried later) · failed · not_applicable (no project).</summary>
    public string VectorStatus { get; set; } = EvidenceVectorStatuses.Pending;
    public int VectorAttempts { get; set; }
    public long? VectorNextAttemptAtUnixMs { get; set; }
    public string? VectorModelId { get; set; }
    /// <summary>Ordering column: SQLite cannot order by DateTimeOffset.</summary>
    public long CreatedAtUnixMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class EvidenceVectorStatuses
{
    public const string Pending = "pending";
    public const string Indexed = "indexed";
    public const string Unavailable = "unavailable";
    public const string Failed = "failed";
    public const string NotApplicable = "not_applicable";
}

public static class ApprovalGateKinds
{
    public const string Human = "human";
    public const string ConversationIdentity = ApprovalGateKindNames.Conversation;
    public const string ReviewerAgent = ApprovalGateKindNames.Reviewer;
}

public static class ApprovalGateStatuses
{
    public const string Pending = "pending";
    /// <summary>Taken by a host that is asking the gate's model right now.</summary>
    public const string Evaluating = "evaluating";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    /// <summary>The gate could not or would not decide; the approval waits for the person.</summary>
    public const string Escalated = "escalated";
    /// <summary>Never asked: an earlier gate already refused or escalated.</summary>
    public const string Skipped = "skipped";
    /// <summary>The person (or an expiry) decided the approval first; this gate's answer, if any, changed nothing.</summary>
    public const string Superseded = "superseded";

    public static bool IsOpen(string status) => status is Pending or Evaluating;
}
