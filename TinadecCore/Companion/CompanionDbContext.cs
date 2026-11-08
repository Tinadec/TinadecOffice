using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Companion;

/// <summary>
/// Isolated companion application domain. None of these tables references the work-domain
/// agent/session/tool tables: a companion is a separate identity with its own memories,
/// bindings and run history. Every table carries tenant_id, so isolation is a predicate,
/// never a shared join.
/// Conversation/message/run/activity tables are defined here even though Task 2 owns their
/// behaviour, so their schema does not need to be re-created later.
/// </summary>
public sealed class CompanionDbContext(DbContextOptions<CompanionDbContext> options) : DbContext(options)
{
    public DbSet<CompanionAgentRecord> Companions => Set<CompanionAgentRecord>();
    public DbSet<CompanionMemoryRecord> Memories => Set<CompanionMemoryRecord>();
    public DbSet<CompanionSkillBindingRecord> SkillBindings => Set<CompanionSkillBindingRecord>();
    public DbSet<CompanionConnectionBindingRecord> ConnectionBindings => Set<CompanionConnectionBindingRecord>();
    public DbSet<CompanionSettingsRecord> Settings => Set<CompanionSettingsRecord>();
    public DbSet<CompanionConversationRecord> Conversations => Set<CompanionConversationRecord>();
    public DbSet<CompanionConversationMemberRecord> ConversationMembers => Set<CompanionConversationMemberRecord>();
    public DbSet<CompanionMessageRecord> Messages => Set<CompanionMessageRecord>();
    public DbSet<CompanionRunRecord> Runs => Set<CompanionRunRecord>();
    public DbSet<CompanionEffectRecord> Effects => Set<CompanionEffectRecord>();
    public DbSet<CompanionMandateRecord> Mandates => Set<CompanionMandateRecord>();
    public DbSet<CompanionActivityRecord> Activities => Set<CompanionActivityRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CompanionAgentRecord>(e =>
        {
            e.ToTable("companion_agents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Tagline).HasMaxLength(512);
            e.Property(x => x.AvatarKey).HasMaxLength(64);
            e.Property(x => x.PersonaDocument).HasMaxLength(65536);
            e.Property(x => x.ModelPurpose).HasMaxLength(64).IsRequired();
            // The model a companion speaks with: null means "resolve the tenant's route purpose",
            // set means the named provider instance with the named model overrides the route.
            e.Property(x => x.ModelName).HasMaxLength(256);
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
        });

        model.Entity<CompanionMemoryRecord>(e =>
        {
            e.ToTable("companion_memories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.SourceKind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Visibility).HasMaxLength(16).IsRequired();
            e.Property(x => x.IndexStatus).HasMaxLength(16).IsRequired();
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.IndexStatus, x.UpdatedAt });
        });

        model.Entity<CompanionSkillBindingRecord>(e =>
        {
            e.ToTable("companion_skill_bindings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceKind).HasMaxLength(16).IsRequired();
            e.Property(x => x.PackId).HasMaxLength(256);
            e.Property(x => x.PackVersion).HasMaxLength(64);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.Enabled });
        });

        model.Entity<CompanionConnectionBindingRecord>(e =>
        {
            e.ToTable("companion_connection_bindings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(512);
            e.Property(x => x.CredentialRef).HasMaxLength(256);
            e.Property(x => x.Authorization).HasMaxLength(16).IsRequired();
            e.Property(x => x.RuntimeStatus).HasMaxLength(16).IsRequired();
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.Enabled });
        });

        model.Entity<CompanionSettingsRecord>(e =>
        {
            e.ToTable("companion_settings");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });

        model.Entity<CompanionConversationRecord>(e =>
        {
            e.ToTable("companion_conversations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.LastMessageAt });
            e.HasIndex(x => new { x.TenantId, x.ParentConversationId });
            e.HasIndex(x => new { x.TenantId, x.Origin });
        });

        model.Entity<CompanionConversationMemberRecord>(e =>
        {
            e.ToTable("companion_conversation_members");
            e.HasKey(x => x.Id);
            e.Property(x => x.MemberKind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ConversationId, x.MemberKind, x.CompanionId }).IsUnique();
        });

        model.Entity<CompanionMessageRecord>(e =>
        {
            e.ToTable("companion_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.AuthorKind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Body).IsRequired();
            e.Property(x => x.AddressedToJson).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.HasIndex(x => new { x.TenantId, x.ConversationId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ConversationId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        });

        model.Entity<CompanionRunRecord>(e =>
        {
            e.ToTable("companion_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.ConfigSnapshotJson).IsRequired();
            e.Property(x => x.LoadedMemoryIdsJson).IsRequired();
            e.Property(x => x.LoadedSkillBindingIdsJson).IsRequired();
            e.Property(x => x.ErrorCode).HasMaxLength(64);
            e.Property(x => x.ErrorMessage).HasMaxLength(2048);
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.ConversationId, x.CreatedAt });
        });

        // The durable wake queue for companion runs. A run row says what the user sees; an effect
        // row says what the host still owes, so a crash between "message stored" and "reply written"
        // leaves a claimable row instead of a run that never moves.
        model.Entity<CompanionEffectRecord>(e =>
        {
            e.ToTable("companion_effects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.ClaimToken).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(2048);
            // One live effect per run: the enqueue path and the startup self-heal both target the
            // same run, and a second row would let two executors answer the same message.
            e.HasIndex(x => new { x.TenantId, x.RunId, x.Kind }).IsUnique().HasFilter("run_id IS NOT NULL");
            // The claim scan is (status, due) ordered, which the unique run index cannot serve.
            e.HasIndex(x => new { x.Status, x.AvailableAtUnixMs });
        });

        // A standing authorization for one class of autonomous action. Without an approved row the
        // autonomy tick only records what it WOULD do: an autonomous partner must not act on the
        // user's sessions and conversations before the user has said it may.
        model.Entity<CompanionMandateRecord>(e =>
        {
            e.ToTable("companion_mandates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.ProposalJson).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(512);
            e.Property(x => x.Revision).IsConcurrencyToken();
            // One standing answer per action class per companion: re-proposing updates the row
            // instead of stacking a second question the user cannot tell apart from the first.
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.Action }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
        });

        model.Entity<CompanionActivityRecord>(e =>
        {
            e.ToTable("companion_activities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(512).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(2048);
            // The live feed resumes from a single tenant-monotonic cursor, so (tenant, seq) must be
            // unique. The filter keeps rows written before this column existed (seq 0) from making
            // the index impossible to create on an upgraded database.
            e.HasIndex(x => new { x.TenantId, x.Seq }).IsUnique().HasFilter("seq > 0");
            e.HasIndex(x => new { x.TenantId, x.CompanionId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });

        model.UseTinadecSnakeCase();
    }
}

public sealed class CompanionAgentRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public string? AvatarKey { get; set; }
    public string? PersonaDocument { get; set; }
    public string ModelPurpose { get; set; } = "chat";
    public Guid? ModelProviderId { get; set; }
    public string? ModelName { get; set; }
    // active | paused | archived
    public string Status { get; set; } = "active";
    // autonomous | buddy — a buddy is the sidebar work-buddy (second user, manages sessions);
    // autonomous is the self-directed partner. Rows predating the column read as autonomous.
    public string Role { get; set; } = "autonomous";
    public bool AutonomyEnabled { get; set; }
    public bool AutonomyPaused { get; set; }
    public int AutonomyIntervalMinutes { get; set; } = 60;
    public int AutonomyDailyBudget { get; set; } = 6;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CompanionMemoryRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanionId { get; set; }
    public string Content { get; set; } = string.Empty;
    // user | companion_run | group_message
    public string SourceKind { get; set; } = "user";
    public Guid? SourceConversationId { get; set; }
    public Guid? SourceMessageId { get; set; }
    public string Visibility { get; set; } = "private";
    // indexed | deleted
    public string IndexStatus { get; set; } = "indexed";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CompanionSkillBindingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanionId { get; set; }
    public string Name { get; set; } = string.Empty;
    // manual | pack
    public string SourceKind { get; set; } = "manual";
    public string? PackId { get; set; }
    public string? PackVersion { get; set; }
    public string? Instructions { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? LastLoadedRunId { get; set; }
    public DateTimeOffset? LastLoadedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CompanionConnectionBindingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanionId { get; set; }
    // mcp | server | phone | external_agent
    public string Kind { get; set; } = "mcp";
    public string Name { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? CredentialRef { get; set; }
    // configured | authorized | revoked
    public string Authorization { get; set; } = "configured";
    // unsupported | unknown — never "online" without a real handshake
    public string RuntimeStatus { get; set; } = "unsupported";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CompanionSettingsRecord
{
    public Guid TenantId { get; set; }
    public Guid? MainCompanionId { get; set; }
    // The designated work-buddy shown in the sidebar below "new chat"; null = none designated.
    public Guid? BuddyCompanionId { get; set; }
    public bool AutonomyPaused { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ─────────────────────────────────────────────
// Task 2 schema (conversations / messages / runs / activity).
// Defined now so Task 2 can add behaviour without a second bootstrap migration.
// ─────────────────────────────────────────────

public sealed class CompanionConversationRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    // private | group
    public string Kind { get; set; } = "private";
    // Who opened it: user | companion. Null on rows that predate the column; readers fall back to
    // the autonomy activity trail for those instead of guessing from the member list, because every
    // group carries a user member regardless of who created it.
    public string? Origin { get; set; }
    public string? Title { get; set; }
    public Guid? ParentConversationId { get; set; }
    public bool AutonomyPaused { get; set; }
    public int TurnBudget { get; set; } = 6;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
}

public sealed class CompanionConversationMemberRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    // user | companion
    public string MemberKind { get; set; } = "user";
    public Guid? CompanionId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    // active | left
    public string Status { get; set; } = "active";
}

public sealed class CompanionMessageRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    public long Seq { get; set; }
    // user | companion | system
    public string AuthorKind { get; set; } = "user";
    public Guid? AuthorCompanionId { get; set; }
    public string Body { get; set; } = string.Empty;
    public string AddressedToJson { get; set; } = "[]";
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CompanionRunRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanionId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TriggerMessageId { get; set; }
    // queued | running | completed | failed
    public string Status { get; set; } = "queued";
    public int Depth { get; set; }
    public long ConfigRevision { get; set; }
    public string ConfigSnapshotJson { get; set; } = "{}";
    public string LoadedMemoryIdsJson { get; set; } = "[]";
    public string LoadedSkillBindingIdsJson { get; set; } = "[]";
    public int Rounds { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? ReplyMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// A unit of work the host still owes, independent of the run row it belongs to. Status is the
/// lease state (pending/running/done/failed) and <see cref="ClaimToken"/> fences a slow executor
/// out once another host has reclaimed an expired claim.
/// </summary>
public sealed class CompanionEffectRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CompanionId { get; set; }
    public Guid? ConversationId { get; set; }
    // Null for effects that are not tied to a run; the unique index filters those out.
    public Guid? RunId { get; set; }
    // run_reply — the only kind today; kept as a column so a second effect type does not need a
    // second queue.
    public string Kind { get; set; } = "run_reply";
    public string PayloadJson { get; set; } = "{}";
    // pending | running | done | failed
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    // Same instant as AvailableAt, in a form SQLite can compare and order. Without it the claim
    // scan would have to load every pending row and compare in memory.
    public long AvailableAtUnixMs { get; set; }
    public string? ClaimToken { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    // Claim expiry, likewise numeric for the reclaim scan.
    public long? ClaimedAtUnixMs { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CompanionMandateRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanionId { get; set; }
    // create_group | invite | send_message — one standing answer per action class.
    public string Action { get; set; } = string.Empty;
    // proposed | approved | denied
    public string Status { get; set; } = "proposed";
    // The decision the tick wanted to take when it asked. Approving authorizes the CLASS of action,
    // but this payload is what the user read before saying yes, so it stays on the row.
    public string ProposalJson { get; set; } = "{}";
    public string? Summary { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class CompanionActivityRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    // Tenant-monotonic order of the activity feed. Rows written before this column existed read 0
    // and stay behind every new row, which is exactly their age.
    public long Seq { get; set; }
    // run_started | run_completed | message_sent | group_created | autonomy_skipped | ...
    public string Kind { get; set; } = string.Empty;
    public Guid? CompanionId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
