using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;
using TinadecCore.Runtime;
using TinadecCore.TinaChat;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Startup reconciliation through the real host: the bridge is only useful if a host actually
/// calls it, so these tests run the production pass against a real SQLite database and the real
/// TinaChat tables.
/// </summary>
public sealed class CompanionParticipantReconcilerTests
{
    private sealed class ReconcilerFactory : IsolatedApiFactory
    {
        private readonly string _root = ApiTestStorage.CreateRoot("companion-reconciler");
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

    private static async Task<Guid> AddCompanionAsync(ReconcilerFactory factory, string name, string status = "active")
    {
        var tenantId = factory.Identity.Current.TenantId;
        var id = Guid.NewGuid();
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync();
        db.Companions.Add(new CompanionAgentRecord
        {
            Id = id, TenantId = tenantId, Name = name, Status = status, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<ChatParticipant[]> ParticipantsAsync(ReconcilerFactory factory, Guid companionId)
    {
        var tenantId = factory.Identity.Current.TenantId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        return await db.Participants.AsNoTracking().Where(x => x.TenantId == tenantId && x.CompanionId == companionId).ToArrayAsync();
    }

    [Fact]
    public async Task PassIsIdempotent_AndCreatesOneCompanionParticipantPerCompanion()
    {
        using var factory = new ReconcilerFactory();
        using var client = factory.CreateClient();
        var companionId = await AddCompanionAsync(factory, "对账伙伴");
        var reconciler = factory.Services.GetRequiredService<CompanionParticipantReconciler>();

        var first = await reconciler.RunPassAsync();
        Assert.Equal(1, first.Ensured);

        var second = await reconciler.RunPassAsync();
        Assert.Equal(1, second.Ensured);

        var participant = Assert.Single(await ParticipantsAsync(factory, companionId));
        Assert.Equal("companion", participant.Kind);
        Assert.Equal("active", participant.Status);
        Assert.Equal("对账伙伴", participant.DisplayName);
        Assert.True(participant.CanInterpretIntent);
    }

    [Fact]
    public async Task ArchivedCompanion_ParticipantIsRetired_AndNeverReawakened()
    {
        using var factory = new ReconcilerFactory();
        using var client = factory.CreateClient();
        var companionId = await AddCompanionAsync(factory, "将归档伙伴");
        var reconciler = factory.Services.GetRequiredService<CompanionParticipantReconciler>();
        await reconciler.RunPassAsync();

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == companionId);
            row.Status = "archived";
            await db.SaveChangesAsync();
        }

        var retired = await reconciler.RunPassAsync();
        Assert.Equal(1, retired.Retired);

        // The row stays archived: a second pass must not register a replacement, because an
        // archived companion is not allowed to hold a live participant.
        var again = await reconciler.RunPassAsync();
        Assert.Equal(0, again.Ensured);
        var participant = Assert.Single(await ParticipantsAsync(factory, companionId));
        Assert.Equal("archived", participant.Status);
    }

    [Fact]
    public async Task RestoredCompanion_GetsItsArchivedParticipantBack()
    {
        using var factory = new ReconcilerFactory();
        using var client = factory.CreateClient();
        var companionId = await AddCompanionAsync(factory, "恢复伙伴");
        var reconciler = factory.Services.GetRequiredService<CompanionParticipantReconciler>();
        await reconciler.RunPassAsync();
        var original = Assert.Single(await ParticipantsAsync(factory, companionId));

        // Archive then restore: the handle stays reserved by the archived row, so a plain register
        // would fail with participant_handle_exists and leave the companion unbridged.
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == companionId);
            row.Status = "archived";
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, (await reconciler.RunPassAsync()).Retired);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == companionId);
            row.Status = "active";
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await reconciler.RunPassAsync()).Ensured);
        var restored = Assert.Single(await ParticipantsAsync(factory, companionId));
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal("active", restored.Status);
    }

    [Fact]
    public async Task AnotherTenantsCompanion_IsNeverBridgedFromThisScope()
    {
        using var factory = new ReconcilerFactory();
        using var client = factory.CreateClient();
        // The pass must not reach across tenants: a foreign row is not registered and does not stop
        // the local companion from getting its participant.
        var local = await AddCompanionAsync(factory, "本地伙伴");
        var foreign = await AddCompanionAsync(factory, "外租户伙伴");
        var tenantId = factory.Identity.Current.TenantId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == foreign);
            row.TenantId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        var result = await factory.Services.GetRequiredService<CompanionParticipantReconciler>().RunPassAsync();
        Assert.Equal(1, result.Ensured);
        Assert.Single(await ParticipantsAsync(factory, local));
        Assert.Empty(await ParticipantsAsync(factory, foreign));
    }
}
