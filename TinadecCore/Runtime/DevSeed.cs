using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>
/// Development bootstrap: idempotently creates a <c>chat</c> model route with an
/// OpenAI provider and the default planner/executor agent pair when none exist for
/// the current tenant workspace. Runs only when the configuration is missing, so
/// production control-plane writes are never overwritten. The provider is created
/// without a stored API key — real model calls require configuring the key first.
/// </summary>
public static class DevSeed
{
    public static async Task SeedIfMissingAsync(IServiceProvider services, CancellationToken ct)
    {
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        await using (var models = await services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>().CreateDbContextAsync(ct))
        {
            var chatRoute = await models.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.Purpose == "chat" && r.TenantId == tenant.TenantId && r.WorkspaceId == tenant.WorkspaceId && r.DeletedAt == null, ct);
            if (chatRoute is not null) return;
            var now = DateTimeOffset.UtcNow;
            var provider = new ModelProviderRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                Driver = "openai",
                DisplayName = "OpenAI (dev default)",
                Scope = "workspace",
                ConnectionKind = "api-key",
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
                ProviderId = provider.Id,
                Model = "gpt-4o-mini",
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            route.CurrentVersionId = routeVersion.Id;
            models.Routes.Add(route);
            models.RouteVersions.Add(routeVersion);
            await models.SaveChangesAsync(ct);
        }

        await using (var agentsDb = await services.GetRequiredService<IDbContextFactory<AgentControlDbContext>>().CreateDbContextAsync(ct))
        {
            var existing = await agentsDb.Agents.AsNoTracking().AnyAsync(a => a.TenantId == tenant.TenantId && a.WorkspaceId == tenant.WorkspaceId && a.DeletedAt == null, ct);
            if (existing) return;
            var now = DateTimeOffset.UtcNow;
            var content = services.GetRequiredService<IContentStore>();
            var agentDefs = new[]
            {
                new { name = "planner", layer = "planning", agent_type = "planner", system_prompt = "You are the planning layer agent. Break the user goal into executable subtasks.", model_route_purpose = "chat" },
                new { name = "executor", layer = "execution", agent_type = "executor", system_prompt = "You are the execution layer agent. Execute the assigned task and output a completion summary.", model_route_purpose = "chat" }
            };
            foreach (var def in agentDefs)
            {
                var agentRow = new AgentProfileRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    Scope = "workspace",
                    Name = def.name,
                    Layer = def.layer,
                    AgentType = def.agent_type,
                    Enabled = true,
                    Revision = 0,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    CreatedAt = now
                };
                var agentBody = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["mode"] = "default",
                    ["description"] = def.name,
                    ["model_route_purpose"] = def.model_route_purpose,
                    ["allowed_tools"] = Array.Empty<string>(),
                    ["capabilities"] = Array.Empty<string>(),
                    ["system_prompt"] = def.system_prompt
                });
                await using (var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(agentBody)))
                {
                    var stored2 = await content.PutAsync(new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "agent-profile", "application/json", ms2), ct);
                    var agentVersion = new AgentProfileVersionRecord
                    {
                        Id = Guid.NewGuid(),
                        AgentId = agentRow.Id,
                        Version = 1,
                        ContentReference = stored2.Value,
                        ContentHash = stored2.Sha256,
                        ContentLength = stored2.Length,
                        CreatedByPrincipalId = tenant.PrincipalId,
                        CreatedAt = now
                    };
                    agentRow.CurrentVersionId = agentVersion.Id;
                    agentsDb.Agents.Add(agentRow);
                    agentsDb.Versions.Add(agentVersion);
                }
            }
            await agentsDb.SaveChangesAsync(ct);
        }
    }
}
