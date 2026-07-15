using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Memory;

/// <summary>
/// Memory module registrar. Registers memory store with retention policy and provenance.
/// </summary>
public sealed class MemoryModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "memory";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<IMemoryStore, MemoryStore>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies"],
            Capabilities = ["session_serialization", "chat_history", "retention_policy", "provenance"],
            Language = "C#",
            MafPrimitives = ["memory", "session"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Skeleton memory store. Wraps MAF AgentSession serialization and ChatHistoryProvider.
/// Manages scope, retention policy, and provenance. No vector database selected this round.
/// </summary>
internal sealed class MemoryStore : IMemoryStore
{
    public Task<MemoryEntry[]> RetrieveAsync(
        string sessionId,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Array.Empty<MemoryEntry>());
    }

    public Task StoreAsync(
        string sessionId,
        MemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
