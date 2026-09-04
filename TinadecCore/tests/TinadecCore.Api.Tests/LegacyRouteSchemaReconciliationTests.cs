using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models;
using TinadecCore.Persistence;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Regression coverage for the 2026-09-04 live-instance failure: databases created
/// before commit f9c44c0 kept a NOT NULL <c>model_route_versions.provider_id</c>
/// column that the current model no longer maps (route candidates moved to
/// <c>model_route_candidates</c>). EF inserts never populate that column, so every
/// route save died with "NOT NULL constraint failed: model_route_versions.provider_id"
/// — meaning the chat route could never be rebound on upgraded databases, which is
/// the deeper root cause of the configuration-drift failed-run cluster. The schema
/// bootstrapper must drop legacy NOT NULL columns the model no longer maps.
/// </summary>
public sealed class LegacyRouteSchemaReconciliationTests
{
    [Fact]
    public async Task EnsureTables_DropsLegacyNotNullProviderColumn_AndRouteVersionInsertSucceeds()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-legacy-route-schema-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ModelControlDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            await using (var db = new ModelControlDbContext(options))
            {
                // The pre-f9c44c0 shape: provider_id inline NOT NULL on route versions.
                await db.Database.ExecuteSqlRawAsync("""
                    create table model_routes (
                        id text primary key, tenant_id text not null, workspace_id text null, project_id text null,
                        scope text not null, purpose text not null, revision integer not null, current_version_id text not null,
                        created_by_principal_id text not null, updated_by_principal_id text not null,
                        created_at text not null, updated_at text not null, deleted_at text null);
                    create table model_route_versions (
                        id text primary key, route_id text not null, version integer not null,
                        provider_id text not null, model text null,
                        created_by_principal_id text not null, created_at text not null);
                    create unique index ix_model_route_versions_route_version on model_route_versions(route_id, version);
                    """);

                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);

                var columns = await db.Database
                    .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('model_route_versions')")
                    .ToListAsync();
                Assert.DoesNotContain("provider_id", columns);
            }

            // The original failure mode: inserting a route version through EF
            // (which no longer maps provider_id) must succeed on the repaired table.
            await using (var db = new ModelControlDbContext(options))
            {
                var routeId = Guid.NewGuid();
                var versionId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                db.Routes.Add(new ModelRouteRecord
                {
                    Id = routeId,
                    TenantId = Guid.NewGuid(),
                    WorkspaceId = Guid.NewGuid(),
                    Purpose = "chat",
                    Revision = 1,
                    CurrentVersionId = versionId,
                    CreatedByPrincipalId = Guid.NewGuid(),
                    UpdatedByPrincipalId = Guid.NewGuid(),
                    CreatedAt = now,
                    UpdatedAt = now
                });
                db.RouteVersions.Add(new ModelRouteVersionRecord
                {
                    Id = versionId,
                    RouteId = routeId,
                    Version = 1,
                    CreatedByPrincipalId = Guid.NewGuid(),
                    CreatedAt = now
                });
                await db.SaveChangesAsync();
                Assert.Equal(1, await db.RouteVersions.CountAsync());
            }
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }
}
