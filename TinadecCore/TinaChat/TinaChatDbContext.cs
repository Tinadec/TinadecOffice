using Microsoft.EntityFrameworkCore;

namespace TinadecCore.TinaChat;

public sealed class TinaChatDbContext(DbContextOptions<TinaChatDbContext> options) : DbContext(options)
{
    public DbSet<ChatParticipant> Participants => Set<ChatParticipant>();
    public DbSet<ChatConversation> Conversations => Set<ChatConversation>();
    public DbSet<ChatMember> Members => Set<ChatMember>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ChatAudience> Audiences => Set<ChatAudience>();
    public DbSet<ChatWorkspacePolicy> Policies => Set<ChatWorkspacePolicy>();
    public DbSet<ChatIntent> Intents => Set<ChatIntent>();
    public DbSet<ChatExecution> Executions => Set<ChatExecution>();
    public DbSet<ChatAudit> Audit => Set<ChatAudit>();
    public DbSet<ChatWake> Wakes => Set<ChatWake>();
    public DbSet<ChatSessionIdentity> SessionIdentities => Set<ChatSessionIdentity>();
    public DbSet<ChatOrganization> Organizations => Set<ChatOrganization>();
    public DbSet<ChatContact> Contacts => Set<ChatContact>();
    public DbSet<ChatReport> Reports => Set<ChatReport>();
    public DbSet<ChatInstanceBinding> InstanceBindings => Set<ChatInstanceBinding>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ChatParticipant>(e =>
        {
            e.ToTable("tina_chat_participants"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Handle }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.OwnerPrincipalId });
            e.Property(x => x.Handle).HasMaxLength(80);
            e.Property(x => x.DisplayName).HasMaxLength(160);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.MemberKey }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.ParentParticipantId });
            e.HasIndex(x => x.CurrentRunId);
            e.Property(x => x.MemberKey).HasMaxLength(160);
            e.Property(x => x.OrgRole).HasMaxLength(32);
            e.Property(x => x.Presence).HasMaxLength(16);
            e.Property(x => x.VisibilityScope).HasMaxLength(16);
            e.Property(x => x.AgentSlug).HasMaxLength(128);
        });
        model.Entity<ChatConversation>(e =>
        {
            e.ToTable("tina_chat_conversations"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CreatorId, x.ClientRequestId }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Kind });
            e.Property(x => x.ClientRequestId).HasMaxLength(128);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ChatMember>(e =>
        {
            e.ToTable("tina_chat_members"); e.HasKey(x => new { x.ConversationId, x.ParticipantId });
            e.HasOne<ChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ChatParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ChatMessage>(e =>
        {
            e.ToTable("tina_chat_messages"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.ConversationId, x.SenderId, x.ClientMessageId }).IsUnique();
            e.Property(x => x.ClientMessageId).HasMaxLength(128);
            e.HasOne<ChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ChatAudience>(e =>
        {
            e.ToTable("tina_chat_audiences"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.MessageId, x.ParticipantId }).IsUnique();
            e.HasIndex(x => new { x.ParticipantId, x.Id });
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ChatParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ChatWorkspacePolicy>(e =>
        {
            e.ToTable("tina_chat_workspace_policies"); e.HasKey(x => new { x.TenantId, x.WorkspaceId });
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ChatIntent>(e =>
        {
            e.ToTable("tina_chat_intents"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ConversationId, x.AuthorId, x.ClientRequestId }).IsUnique();
            e.Property(x => x.ClientRequestId).HasMaxLength(128);
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ChatExecution>(e =>
        {
            e.ToTable("tina_chat_executions"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.IntentId, x.ParticipantId }).IsUnique();
            e.HasIndex(x => x.SessionId).IsUnique();
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.ResultRunStatus).HasMaxLength(32);
        });
        model.Entity<ChatAudit>(e =>
        {
            e.ToTable("tina_chat_audit"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ConversationId });
        });
        model.Entity<ChatWake>(e =>
        {
            e.ToTable("tina_chat_wakes"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.Status, x.AvailableAt });
            e.HasIndex(x => new { x.Status, x.DueAtUnixMs });
            e.HasIndex(x => new { x.Status, x.ClaimedAtUnixMs });
            e.HasIndex(x => new { x.ConversationId, x.ParticipantId, x.Status });
            e.Property(x => x.LastError).HasMaxLength(512);
        });
        model.Entity<ChatSessionIdentity>(e =>
        {
            e.ToTable("tina_chat_session_identities"); e.HasKey(x => x.SessionId);
            e.HasIndex(x => new { x.TenantId, x.ParticipantId });
            e.HasIndex(x => new { x.TenantId, x.OwnerPrincipalId });
        });
        model.Entity<ChatOrganization>(e =>
        {
            e.ToTable("tina_chat_organizations"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.SessionId }).IsUnique();
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ChatContact>(e =>
        {
            e.ToTable("tina_chat_contacts"); e.HasKey(x => new { x.OwnerId, x.ContactId });
            e.HasIndex(x => new { x.OrganizationId, x.ContactId, x.Status });
            e.Property(x => x.Status).HasMaxLength(16);
        });
        model.Entity<ChatReport>(e =>
        {
            e.ToTable("tina_chat_reports"); e.HasKey(x => x.MessageId);
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.Property(x => x.ReportKind).HasMaxLength(32);
            e.Property(x => x.Severity).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.SubjectKind).HasMaxLength(32);
            e.Property(x => x.SubjectId).HasMaxLength(512);
            e.Property(x => x.ProposedVerb).HasMaxLength(32);
            e.Property(x => x.ProposedArgs).HasMaxLength(1024);
            e.Property(x => x.Finding).HasMaxLength(4000);
            e.Property(x => x.DecisionNote).HasMaxLength(2000);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ChatInstanceBinding>(e =>
        {
            e.ToTable("tina_chat_instance_bindings"); e.HasKey(x => x.InstanceId);
            e.HasIndex(x => new { x.OrganizationId, x.ParticipantId });
        });
        model.UseTinadecSnakeCase();
    }
}

public sealed class ChatParticipant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid OwnerPrincipalId { get; set; }
    public string Handle { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "agent";
    public string? JobTitle { get; set; }
    public string? Description { get; set; }
    public Guid? AgentDefinitionId { get; set; }
    public bool ReceiveHumanMessages { get; set; }
    public bool CanInterpretIntent { get; set; }
    public bool Discoverable { get; set; } = true;
    public string Status { get; set; } = "active";
    public long Revision { get; set; } = 1;

    // Organization membership (null for a workspace-level participant). A member belongs to exactly
    // one session's organization; its key is unique there (user, host, conversation,
    // governance:{slug}, instance:{id}), which is what makes enrolment idempotent.
    public Guid? OrganizationId { get; set; }
    public string? MemberKey { get; set; }
    public string? OrgRole { get; set; }
    public string? AgentSlug { get; set; }
    /// <summary>The member that dispatched this one: its default contact, and what makes two executors siblings.</summary>
    public Guid? ParentParticipantId { get; set; }
    /// <summary>online while the member can act (its run is live, or it is a standing role); offline keeps it attributable.</summary>
    public string? Presence { get; set; }
    public Guid? CurrentRunId { get; set; }
    /// <summary>User-set visibility into run internals (todo E5): "down" (default) or "own". Null = unrestricted.</summary>
    public string? VisibilityScope { get; set; }
    /// <summary>Fixed one-hour window of this member's woken turns: O(1) to check and exact enough to stop a wake loop.</summary>
    public DateTimeOffset? TurnWindowStartedAt { get; set; }
    public int TurnsInWindow { get; set; }
}

public sealed class ChatConversation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CreatorId { get; set; }
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "group";
    public string ClientRequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public bool AllowCrossWorkspace { get; set; }
    public long Revision { get; set; } = 1;
    public long LastSequence { get; set; }
    public Guid? AcceptedIntentId { get; set; }
    /// <summary>The organization a room belongs to (lobby, board, plan, adhoc, or an org direct chat).</summary>
    public Guid? OrganizationId { get; set; }
    /// <summary>For a plan room: the dispatcher whose executors meet there.</summary>
    public Guid? PlanOwnerId { get; set; }
}

public sealed class ChatMember
{
    public Guid ConversationId { get; set; }
    public Guid ParticipantId { get; set; }
    public string Role { get; set; } = "member";
    public string Status { get; set; } = "invited";
    public long JoinedAfterSequence { get; set; }
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string SenderKind { get; set; } = "";
    public long Sequence { get; set; }
    public string Kind { get; set; } = "message";
    public string ClientMessageId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string ContentReference { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public long ContentLength { get; set; }
    public string Sensitivity { get; set; } = "normal";
    public bool AllowDerivedSharing { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public string SourceMessageIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Frozen audience and durable inbox. Original and derived visibility are separate grants.</summary>
public sealed class ChatAudience
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid ParticipantId { get; set; }
    public bool CanReadOriginal { get; set; }
    public bool CanReceiveDerived { get; set; }
    public bool Acknowledged { get; set; }
}

public sealed class ChatWorkspacePolicy
{
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public long Revision { get; set; }
    public bool AllowCrossWorkspaceDiscovery { get; set; }
    public bool AllowCrossWorkspaceMessaging { get; set; }
}

public sealed class ChatIntent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    public Guid AuthorId { get; set; }
    public string ClientRequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public long Revision { get; set; }
    public string Status { get; set; } = "proposed";
    public Guid? DecidedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ChatExecution
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid IntentId { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ModeVersionId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? RunId { get; set; }
    public long Revision { get; set; } = 1;
    /// <summary>Set once the outcome has been posted back to the conversation, so a reaper pass never double-posts.</summary>
    public Guid? ResultMessageId { get; set; }
    public string? ResultRunStatus { get; set; }
}

public sealed class ChatAudit
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid? ActorId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Action { get; set; } = "";
    public Guid TargetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Durable wake queue written in the same Serializable transaction as the message that
/// caused it, so a committed message is never separable from the turns it owes.
/// One live row per (conversation, participant, reason): a second arrival during a
/// pending turn merges its sources into the existing row instead of stacking turns.
/// </summary>
public sealed class ChatWake
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid ParticipantId { get; set; }
    public string Reason { get; set; } = "message";
    public string SourceMessageIdsJson { get; set; } = "[]";
    /// <summary>Sources owned by the current claim. They are ACKed only after the turn succeeds.</summary>
    public string ClaimedSourceMessageIdsJson { get; set; } = "[]";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    /// <summary>Fencing token for the host currently processing this row.</summary>
    public string? ClaimToken { get; set; }
    public long? ClaimedAtUnixMs { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    /// <summary>
    /// <see cref="AvailableAt"/> as Unix milliseconds, so "which wakes are due" is an index range in
    /// SQL on every provider. SQLite cannot compare DateTimeOffset server-side, and scanning every
    /// pending row per pass does not survive an organization with hundreds of postponed members.
    /// Rows written before the column existed read 0, which is simply "due".
    /// </summary>
    public long DueAtUnixMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Which named participant a Core session speaks as, claimed by a run whose initiating principal
/// already controls that participant. Without a binding a session has no voice in a conversation;
/// with one, every tool call is still re-authorized against the participant's own membership.
/// </summary>
public sealed class ChatSessionIdentity
{
    public Guid SessionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid OwnerPrincipalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A session in TinaChat (architecture §9.1): one organization per session, holding its members,
/// contacts, rooms and board. Archiving the session makes it read-only; nothing is deleted.
/// </summary>
public sealed class ChatOrganization
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid OwnerPrincipalId { get; set; }
    public string Status { get; set; } = "active";
    public Guid HostParticipantId { get; set; }
    public Guid HumanParticipantId { get; set; }
    public Guid LobbyConversationId { get; set; }
    public Guid BoardConversationId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    /// <summary>Fixed one-hour window of woken turns across the whole organization (the session budget).</summary>
    public DateTimeOffset? TurnWindowStartedAt { get; set; }
    public int TurnsInWindow { get; set; }
}

/// <summary>
/// An explicit contact edge, for pairs the graph does not already connect. Default contacts
/// (dispatcher and dispatched, siblings, governance / conversation / user with everybody) are derived
/// when checked and never stored, so an organization of hundreds of members holds no O(n^2) rows.
/// </summary>
public sealed class ChatContact
{
    public Guid OwnerId { get; set; }
    public Guid ContactId { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>requested (by the owner, awaiting the contact) / active / declined.</summary>
    public string Status { get; set; } = "requested";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// The structured half of a report post (architecture §9.3). The message carries the readable text
/// and the frozen audience; this row carries what a reader filters and a decider acts on.
/// </summary>
public sealed class ChatReport
{
    public Guid MessageId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid AuthorId { get; set; }
    public string ReportKind { get; set; } = "risk";
    public string Severity { get; set; } = "info";
    /// <summary>open / acted / dismissed / superseded.</summary>
    public string Status { get; set; } = "open";
    public string? SubjectKind { get; set; }
    public string? SubjectId { get; set; }
    public string? ProposedVerb { get; set; }
    public string? ProposedArgs { get; set; }
    public string Finding { get; set; } = "";
    public string EvidenceJson { get; set; } = "[]";
    public Guid? SupersedesMessageId { get; set; }
    public Guid? DecidedById { get; set; }
    public string? DecisionNote { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// Which member an agent instance acts as. A worker instance binds to its own member; every instance
/// of the conversation identity binds to the one conversation member, so a solo master's per-task
/// instances all speak with one voice.
/// </summary>
public sealed class ChatInstanceBinding
{
    public Guid InstanceId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
