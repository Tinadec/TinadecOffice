using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.TinaChat;

public sealed class TinaChatModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "tina_chat";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<TinaChatDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<TinaChatDbContext>>();
        builder.Services.AddSingleton<TinaChatService>();
        builder.Services.AddSingleton<ITinaChatService>(sp => sp.GetRequiredService<TinaChatService>());
        builder.Services.AddSingleton<ITinaChatRunInput>(sp => sp.GetRequiredService<TinaChatService>());
        builder.Services.AddSingleton<ITinaChatObserver>(sp => sp.GetRequiredService<TinaChatService>());
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId, Version = "0.1.0", Language = "C#",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["named_participants", "group_messaging", "durable_inbox", "message_visibility", "intent_handoffs"],
            MafPrimitives = [], RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
