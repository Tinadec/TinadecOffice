using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Persistence;

namespace TinadecCore.AspNetCore.Auth;

public static class AuthServiceExtensions
{
    public static IServiceCollection AddTinadecOidc(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TinadecOidcOptions>().Bind(configuration.GetSection(TinadecOidcOptions.SectionName));
        // Resolved lazily: persistence is registered before this in the host, but keeping the
        // lookup here means a host without a writable secret store still gets a working store
        // (memory-only) instead of a DI failure at startup.
        services.AddSingleton<OidcSessionStore>(sp => new OidcSessionStore(sp.GetService<ISecretStore>(), sp.GetService<TimeProvider>()));
        services.AddHttpClient<OidcClient>();
        return services;
    }
}