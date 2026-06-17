using Microsoft.Data.Sqlite;
using Tinadec.Contracts.Models;
using TinadecCore.Services;
using TinadecCore.Storage;
using TinadecModel.Providers;

namespace TinadecCore.Tests;

public sealed class MigrationTests
{
    [Fact]
    public void Initialize_MigratesLegacyProviderRowsWithDefaultHealthFields()
    {
        var dbPath = CreateLegacyDatabase(
            providerId: "legacy_openai",
            driver: "openai-compatible",
            routePurpose: "chat",
            routeModel: "gpt-4o-mini");

        var store = new CoreStore(dbPath);
        store.Initialize();

        var provider = store.GetStoredModelProviderInstance("legacy_openai");
        Assert.NotNull(provider);
        Assert.Equal(ProviderHealthStatus.Healthy, provider.HealthStatus);
        Assert.Equal(0, provider.FailureCount);
        Assert.Null(provider.CooldownUntil);
        Assert.Null(provider.LastFailureAt);
        Assert.Null(provider.LastErrorCategory);
        Assert.Contains("chat", provider.Capabilities, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialize_KeepsLegacyRouteResolvableWithoutHealthMetadata()
    {
        var dbPath = CreateLegacyDatabase(
            providerId: "legacy_openai",
            driver: "openai-compatible",
            routePurpose: "chat",
            routeModel: "gpt-4o-mini");

        var store = new CoreStore(dbPath);
        store.Initialize();

        var resolved = new ModelRouteResolver(store).Resolve("chat");
        Assert.Equal("legacy_openai", resolved.ProviderInstanceId);
        Assert.Equal("openai-compatible", resolved.Driver);
        Assert.Equal("gpt-4o-mini", resolved.EffectiveModel);

        var route = store.GetModelRoute("chat");
        Assert.NotNull(route);
        Assert.Equal("legacy_openai", route.ProviderInstanceId);
    }

    [Fact]
    public void Initialize_PreservesOpenAiStableProviderIdDriverAndTemplateIdentity()
    {
        var dbPath = CreateLegacyDatabase(
            providerId: "openai_default",
            driver: "openai-compatible",
            routePurpose: "chat",
            routeModel: "gpt-5.4-mini");

        var store = new CoreStore(dbPath);
        store.Initialize();

        var provider = store.GetStoredModelProviderInstance("openai_default");
        Assert.NotNull(provider);
        Assert.Equal("openai-compatible", provider.Driver);
        Assert.Equal(ProviderHealthStatus.Healthy, provider.HealthStatus);

        var route = store.GetModelRoute("chat");
        Assert.NotNull(route);
        Assert.Equal("openai_default", route.ProviderInstanceId);

        var template = Assert.Single(
            ModelProviderCatalog.ListTemplates(),
            item => item.ProviderFamily.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("openai-compatible", template.Driver);
    }

    private static string CreateLegacyDatabase(string providerId, string driver, string routePurpose, string routeModel)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tinadec-migration-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();

        Execute(connection, """
            create table model_settings (
                id integer primary key check (id = 1),
                base_url text not null,
                model text not null,
                encrypted_api_key text null,
                updated_at text not null
            );

            create table model_provider_instances (
                id text primary key,
                driver text not null,
                display_name text not null,
                connection_kind text not null,
                base_url text null,
                model text null,
                encrypted_api_key text null,
                binary_path text null,
                home_path text null,
                server_url text null,
                launch_args text null,
                capabilities_json text not null,
                enabled integer not null,
                created_at text not null,
                updated_at text not null
            );

            create table model_routes (
                purpose text primary key,
                provider_instance_id text not null,
                model text null,
                updated_at text not null
            );
            """);

        var now = DateTimeOffset.UtcNow.ToString("O");

        Execute(connection, """
            insert into model_settings (id, base_url, model, encrypted_api_key, updated_at)
            values (1, 'https://api.openai.com/v1', $model, null, $updated_at)
            """, command =>
        {
            command.Parameters.AddWithValue("$model", routeModel);
            command.Parameters.AddWithValue("$updated_at", now);
        });

        Execute(connection, """
            insert into model_provider_instances (
                id,
                driver,
                display_name,
                connection_kind,
                base_url,
                model,
                encrypted_api_key,
                binary_path,
                home_path,
                server_url,
                launch_args,
                capabilities_json,
                enabled,
                created_at,
                updated_at
            )
            values (
                $id,
                $driver,
                'Legacy Provider',
                'api-key',
                'https://api.openai.com/v1',
                $model,
                'encrypted-key',
                null,
                null,
                null,
                null,
                '["chat","streaming","tool-calls"]',
                1,
                $created_at,
                $updated_at
            )
            """, command =>
        {
            command.Parameters.AddWithValue("$id", providerId);
            command.Parameters.AddWithValue("$driver", driver);
            command.Parameters.AddWithValue("$model", routeModel);
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);
        });

        Execute(connection, """
            insert into model_routes (purpose, provider_instance_id, model, updated_at)
            values ($purpose, $provider_id, $model, $updated_at)
            """, command =>
        {
            command.Parameters.AddWithValue("$purpose", routePurpose);
            command.Parameters.AddWithValue("$provider_id", providerId);
            command.Parameters.AddWithValue("$model", routeModel);
            command.Parameters.AddWithValue("$updated_at", now);
        });

        return dbPath;
    }

    private static void Execute(SqliteConnection connection, string sql, Action<SqliteCommand>? configure = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }
}
