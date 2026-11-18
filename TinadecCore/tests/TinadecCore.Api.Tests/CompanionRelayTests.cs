using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Runtime;
using TinadecCore.TinaChat;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Companion-to-companion messaging through the real host: the relay is only useful if a message
/// written by one companion actually reaches the other's durable inbox, under the sender's own
/// participant identity. These tests run the production bridge against real SQLite and the real
/// TinaChat tables.
/// </summary>
public sealed class CompanionRelayTests
{
    private sealed class RelayFactory : IsolatedApiFactory
    {
        private readonly string _root = ApiTestStorage.CreateRoot("companion-relay");
        public MutableIdentity Identity { get; } = new();

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddSingleton<ITenantContextAccessor>(Identity));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class MutableIdentity : ITenantContextAccessor
    {
        public TenantContext Current { get; set; } = new(
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d001"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d002"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d003"), "owner", true);
    }

    private static async Task<Guid> AddCompanionAsync(RelayFactory factory, string name)
    {
        var tenantId = factory.Identity.Current.TenantId;
        var id = Guid.NewGuid();
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync();
        db.Companions.Add(new CompanionAgentRecord
        {
            Id = id, TenantId = tenantId, Name = name, Status = "active", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<TinaChatInboxItem[]> InboxAsync(RelayFactory factory, Guid companionId)
    {
        // The inbox is keyed by the TinaChat participant id, not the companion id: the bridge mints
        // a separate participant row per companion, so the companion id alone finds nothing.
        var bridge = factory.Services.GetRequiredService<CompanionChatBridge>();
        var participant = await bridge.EnsureParticipantAsync(companionId);
        // The inbox is keyed by the TinaChat participant id, not the companion id: the bridge mints
        // a separate participant row per companion, so the companion id alone finds nothing.
        var chat = factory.Services.GetRequiredService<ITinaChatService>();
        var page = await chat.ReadInboxAsync(participant.Id);
        return page.Items;
    }

    [Fact]
    public async Task Relay_DeliversToTheRecipientInbox_AsTheSenderIdentity()
    {
        using var factory = new RelayFactory();
        var buddyId = await AddCompanionAsync(factory, "搭子");
        var partnerId = await AddCompanionAsync(factory, "自主伙伴");
        var gateway = factory.Services.GetRequiredService<ICompanionWorkGateway>();

        var relayed = await gateway.RelayToCompanionAsync(
            partnerId, buddyId, "这个项目每天都要导出一份数据，帮我安排一个会话来做。", "relay-key-1");

        Assert.True(relayed.IsSuccess, relayed.Message);
        Assert.NotEqual(Guid.Empty, relayed.Value!.MessageId);

        // The recipient's durable inbox has it — that is the whole point of the relay.
        var inbox = await InboxAsync(factory, buddyId);
        var delivered = Assert.Single(inbox, item => item.Message.Id == relayed.Value!.MessageId);
        Assert.Contains("每天都要导出", delivered.Message.Content);

        // The sender is the sending companion's own participant, never the host.
        Assert.Equal("companion", delivered.Message.SenderKind);
        var senderParticipant = await factory.Services.GetRequiredService<ITinaChatService>()
            .GetParticipantAsync(delivered.Message.SenderId);
        Assert.Equal(partnerId, senderParticipant.CompanionId);
        Assert.Equal("自主伙伴", senderParticipant.DisplayName);
    }

    [Fact]
    public async Task Relay_IsIdempotentOnTheCallerKey_AndReusesOneThreadPerPair()
    {
        using var factory = new RelayFactory();
        var partnerId = await AddCompanionAsync(factory, "自主伙伴");
        var buddyId = await AddCompanionAsync(factory, "搭子");
        var gateway = factory.Services.GetRequiredService<ICompanionWorkGateway>();

        var first = await gateway.RelayToCompanionAsync(partnerId, buddyId, "同一句话。", "relay-key-dup");
        var second = await gateway.RelayToCompanionAsync(partnerId, buddyId, "同一句话。", "relay-key-dup");

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.MessageId, second.Value!.MessageId);

        // A different key is a different message, even with the same body.
        var third = await gateway.RelayToCompanionAsync(partnerId, buddyId, "同一句话。", "relay-key-other");
        Assert.NotEqual(first.Value!.MessageId, third.Value!.MessageId);

        // One direct thread per pair, whatever happens afterwards.
        var chat = factory.Services.GetRequiredService<ITinaChatService>();
        var senderParticipant = await chat.GetParticipantAsync((await chat.DiscoverAsync()).First().Id);
        var conversations = await chat.ListConversationsAsync(senderParticipant.Id);
        var threads = conversations.Where(item => item.Kind == "direct").ToList();
        Assert.Single(threads);
    }

    [Fact]
    public async Task Relay_RefusesSelfAndBlank_AndArchivedPeers()
    {
        using var factory = new RelayFactory();
        var partnerId = await AddCompanionAsync(factory, "自主伙伴");
        var archivedId = await AddCompanionAsync(factory, "已归档");
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == archivedId);
            row.Status = "archived";
            await db.SaveChangesAsync();
        }
        var gateway = factory.Services.GetRequiredService<ICompanionWorkGateway>();

        var self = await gateway.RelayToCompanionAsync(partnerId, partnerId, "自言自语", "relay-self");
        Assert.False(self.IsSuccess);
        Assert.Equal("companion_self_message", self.Error);

        var blank = await gateway.RelayToCompanionAsync(partnerId, archivedId, "   ", "relay-blank");
        Assert.False(blank.IsSuccess);
        Assert.Equal("companion_message_empty", blank.Error);

        var archived = await gateway.RelayToCompanionAsync(partnerId, archivedId, "呼叫已归档的伙伴", "relay-archived");
        Assert.False(archived.IsSuccess);
        Assert.Equal("companion_not_found", archived.Error);
    }
}
