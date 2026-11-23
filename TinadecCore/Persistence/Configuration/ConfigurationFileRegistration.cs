using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Persistence;

public static class ConfigurationFileRegistration
{
    /// <summary>Call after module registration; each runtime storage scope owns a separate instance.</summary>
    public static IServiceCollection AddTinadecConfigurationFiles(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(ConfigurationProjectionCoordinator))) return services;
        services.TryAddSingleton<ScopeConfigurationDocuments>();
        services.TryAddSingleton<IScopeConfigurationDocuments>(sp => sp.GetRequiredService<ScopeConfigurationDocuments>());
        services.TryAddSingleton<ConfigurationProjectionCoordinator>();
        services.TryAddSingleton<IConfigurationProjectionCoordinator>(sp => sp.GetRequiredService<ConfigurationProjectionCoordinator>());
        foreach (var descriptor in services.Where(d => d.ServiceType.IsGenericType
            && d.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>)
            && typeof(ConfigurationProjectionDbContext).IsAssignableFrom(d.ServiceType.GenericTypeArguments[0])).ToArray())
        {
            var context = descriptor.ServiceType.GenericTypeArguments[0];
            var rawType = typeof(ConfigurationProjectionSource<>).MakeGenericType(context);
            var wrappedType = typeof(ConfigurationProjectionDbContextFactory<>).MakeGenericType(context);
            services.Remove(descriptor);
            services.AddSingleton(rawType, sp => Activator.CreateInstance(rawType, CreateOriginal(sp, descriptor))!);
            services.AddSingleton<IConfigurationProjectionSource>(sp => (IConfigurationProjectionSource)sp.GetRequiredService(rawType));
            // Coordinated migrations need the schema even when a document fails validation, so they
            // get a context that skips the projection attach. Registered per context; contexts without
            // a projection simply have no such registration and the participant falls back.
            services.AddSingleton(typeof(IRawDbContextFactory<>).MakeGenericType(context),
                sp => Activator.CreateInstance(typeof(RawConfigurationProjectionFactory<>).MakeGenericType(context), sp.GetRequiredService(rawType))!);
            services.Add(new ServiceDescriptor(descriptor.ServiceType,
                sp => Activator.CreateInstance(wrappedType, sp.GetRequiredService(rawType), sp.GetRequiredService<ConfigurationProjectionCoordinator>())!, descriptor.Lifetime));
        }
        foreach (var id in new[] { "agents", "models", "tools", "mcp", "prompts", "skills" })
            services.AddSingleton<IConfigurationDocumentValidator>(sp => new ConfigurationProjectionDocumentValidator(id, sp.GetServices<IConfigurationProjectionSource>()));
        services.AddSingleton<IConfigurationDocumentValidator>(new HostConfigurationDocumentValidator("storage"));
        services.AddSingleton<IConfigurationDocumentValidator>(new HostConfigurationDocumentValidator("logging"));
        return services;
    }

    private static object CreateOriginal(IServiceProvider services, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(services)
        ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);
}

internal interface IConfigurationProjectionSource
{
    Type ContextType { get; }
    Task<ConfigurationProjectionDbContext> CreateRawAsync(CancellationToken cancellationToken);
}

internal sealed class ConfigurationProjectionSource<T>(IDbContextFactory<T> original) : IConfigurationProjectionSource where T : ConfigurationProjectionDbContext
{
    public Type ContextType => typeof(T);
    public IDbContextFactory<T> Original => original;
    public async Task<ConfigurationProjectionDbContext> CreateRawAsync(CancellationToken cancellationToken) =>
        await original.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class RawConfigurationProjectionFactory<T>(ConfigurationProjectionSource<T> source)
    : IRawDbContextFactory<T> where T : ConfigurationProjectionDbContext
{
    public async Task<T> CreateRawDbContextAsync(CancellationToken cancellationToken = default) =>
        (T)await source.CreateRawAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class ConfigurationProjectionDbContextFactory<T>(
    ConfigurationProjectionSource<T> source, ConfigurationProjectionCoordinator coordinator) : IDbContextFactory<T> where T : ConfigurationProjectionDbContext
{
    public T CreateDbContext() => CreateDbContextAsync().GetAwaiter().GetResult();
    public async Task<T> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var db = await source.Original.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await coordinator.AttachAsync(db, cancellationToken).ConfigureAwait(false);
            return db;
        }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
