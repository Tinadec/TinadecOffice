using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>
/// Development bootstrap: idempotently creates a <c>chat</c> model route with an
/// OpenAI provider when none exists for
/// the current tenant workspace. Runs only when the configuration is missing, so
/// production control-plane writes are never overwritten. The provider is created
/// without a stored API key — real model calls require configuring the key first.
/// </summary>
public static class DevSeed
{
    public static async Task SeedIfMissingAsync(IServiceProvider services, CancellationToken ct)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TinadecCore.Runtime.DevSeed");
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        var seededChatRoute = false;
        await using (var models = await services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>().CreateDbContextAsync(ct))
        {
            var chatRoute = await models.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.Purpose == "chat" && r.TenantId == tenant.TenantId && r.WorkspaceId == tenant.WorkspaceId && r.DeletedAt == null, ct);
            if (chatRoute is null)
            {
                var now = DateTimeOffset.UtcNow;
                var providerId = Guid.NewGuid();
                var provider = new ModelProviderRecord
                {
                    Id = providerId,
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    Driver = "openai",
                    DisplayName = "OpenAI (dev default)",
                    Scope = "workspace",
                    ConnectionKind = "api-key",
                    SecretReference = $"provider-{providerId:N}",
                    Enabled = true,
                    Revision = 0,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    CreatedAt = now
                };
                var content = services.GetRequiredService<IContentStore>();
                var configJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["base_url"] = "https://api.openai.com/v1", ["model"] = "gpt-4o-mini", ["capabilities"] = new[] { "chat" } });
                await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(configJson)))
                {
                    var stored = await content.PutAsync(new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "model-config", "application/json", ms), ct);
                    var providerVersion = new ModelProviderVersionRecord
                    {
                        Id = Guid.NewGuid(),
                        ProviderId = provider.Id,
                        Version = 1,
                        ContentReference = stored.Value,
                        ContentHash = stored.Sha256,
                        ContentLength = stored.Length,
                        CreatedByPrincipalId = tenant.PrincipalId,
                        CreatedAt = now
                    };
                    provider.CurrentVersionId = providerVersion.Id;
                    models.Providers.Add(provider);
                    models.ProviderVersions.Add(providerVersion);
                }
                var route = new ModelRouteRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    Purpose = "chat",
                    Scope = "workspace",
                    Revision = 0,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    CreatedAt = now
                };
                var routeVersion = new ModelRouteVersionRecord
                {
                    Id = Guid.NewGuid(),
                    RouteId = route.Id,
                    Version = 1,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    CreatedAt = now
                };
                route.CurrentVersionId = routeVersion.Id;
                models.Routes.Add(route);
                models.RouteVersions.Add(routeVersion);
                models.RouteCandidates.Add(new ModelRouteCandidateRecord
                {
                    Id = Guid.NewGuid(), RouteVersionId = routeVersion.Id, Position = 0,
                    ProviderInstanceId = provider.Id, Model = "gpt-4o-mini"
                });
                await models.SaveChangesAsync(ct);
                seededChatRoute = true;
            }
        }

        if (seededChatRoute)
        {
            logger.LogInformation(
                "Development chat route seeded (chat_route={ChatRoute}).",
                seededChatRoute);
        }

        await BootstrapAgentDirectory.SeedIfEmptyAsync(services, ct).ConfigureAwait(false);
    }
}
