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
        });
        model.Entity<ChatConversation>(e =>
        {
            e.ToTable("tina_chat_conversations"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CreatorId, x.ClientRequestId }).IsUnique();
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
        });
        model.Entity<ChatAudit>(e =>
        {
            e.ToTable("tina_chat_audit"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ConversationId });
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
