using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Installer.Core.Services;

/// <summary>
/// Configuration options for Installer.Core
/// </summary>
public sealed class InstallerCoreOptions
{
    /// <summary>
    /// GitHub personal access token for API authentication
    /// </summary>
    public string? GitHubAuthToken { get; set; }

    /// <summary>
    /// Default timeout for network operations (seconds)
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of retries for failed operations
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Minimum required free disk space (MB)
    /// </summary>
    public int MinFreeSpaceMb { get; set; } = 512;
}

/// <summary>
/// Dependency injection extension methods for Installer.Core services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add all core installer services to the service collection
    /// </summary>
    public static IServiceCollection AddInstallerCore(
        this IServiceCollection services,
        Action<InstallerCoreOptions>? configure = null)
    {
        var options = new InstallerCoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);

        services.AddSingleton<IStorageProvider>(sp =>
            new FileStorageProvider(logger: sp.GetService<ILogger<FileStorageProvider>>()));

        services.AddSingleton<GitHubApiClient>(sp =>
        {
            var client = new GitHubApiClient(logger: sp.GetService<ILogger<GitHubApiClient>>());
            if (!string.IsNullOrEmpty(options.GitHubAuthToken))
            {
                client.SetAuthToken(options.GitHubAuthToken);
            }
            return client;
        });

        services.AddSingleton<ManifestService>(sp =>
            new ManifestService(logger: sp.GetService<ILogger<ManifestService>>()));

        services.AddSingleton<DependencyResolver>();
        services.AddSingleton<VersionManager>();
        services.AddTransient<Installer>();

        return services;
    }

    /// <summary>
    /// Replace storage with a custom provider (e.g., for tests)
    /// </summary>
    public static IServiceCollection UseStorageProvider(
        this IServiceCollection services,
        IStorageProvider storageProvider)
    {
        services.AddSingleton(storageProvider);
        return services;
    }

    /// <summary>
    /// Replace storage with in-memory provider (for tests)
    /// </summary>
    public static IServiceCollection UseMemoryStorage(this IServiceCollection services)
    {
        services.AddSingleton<IStorageProvider>(new MemoryStorageProvider());
        return services;
    }

    /// <summary>
    /// Set GitHub authentication token to bypass anonymous rate limits
    /// </summary>
    public static IServiceCollection UseGitHubAuth(
        this IServiceCollection services,
        string authToken)
    {
        services.AddSingleton(new InstallerCoreOptions { GitHubAuthToken = authToken });
        return services;
    }
}
