using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tinadec.Contracts.Events;
using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Json;
using TinadecModel.Providers;

namespace TinadecModel.Storage;

public sealed class ModelStore : IModelStore
{
    private readonly object _gate = new();
    private readonly string _connectionString;

    public ModelStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public void Initialize()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                create table if not exists model_settings (
                    id integer primary key check (id = 1),
                    base_url text not null,
                    model text not null,
                    encrypted_api_key text null,
                    updated_at text not null
                );

                create table if not exists model_provider_instances (
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
                    health_status text not null default 'healthy',
                    cooldown_until text null,
                    failure_count integer not null default 0,
                    last_failure_at text null,
                    last_error_category text null,
                    created_at text not null,
                    updated_at text not null
                );

                create table if not exists model_routes (
                    purpose text primary key,
                    provider_instance_id text not null,
                    model text null,
                    updated_at text not null
                );
                """);
        }
    }

    // --- Settings ---

    public StoredModelSettings GetModelSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select base_url, model, encrypted_api_key, updated_at from model_settings where id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new StoredModelSettings("https://api.openai.com/v1", "gpt-5.4-mini", null, DateTimeOffset.UtcNow);
        return new StoredModelSettings(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), ParseTime(reader.GetString(3)));
    }

    public StoredModelSettings SaveModelSettings(string baseUrl, string model, string? encryptedApiKey)
    {
        var normalizedBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? "gpt-5.4-mini" : model.Trim();
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into model_settings (id, base_url, model, encrypted_api_key, updated_at)
                values (1, $base_url, $model, $encrypted_api_key, $updated_at)
                on conflict(id) do update set
                    base_url = excluded.base_url, model = excluded.model,
                    encrypted_api_key = excluded.encrypted_api_key, updated_at = excluded.updated_at
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$base_url", normalizedBaseUrl);
                cmd.Parameters.AddWithValue("$model", normalizedModel);
                cmd.Parameters.AddWithValue("$encrypted_api_key", (object?)encryptedApiKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });

            Execute(connection, """
                insert into model_provider_instances (
                    id, driver, display_name, connection_kind, base_url, model, encrypted_api_key,
                    binary_path, home_path, server_url, launch_args, capabilities_json, enabled,
                    health_status, cooldown_until, failure_count, last_failure_at, last_error_category,
                    created_at, updated_at
                ) values (
                    'openai_default', 'openai-compatible', 'OpenAI Compatible', 'api-key',
                    $base_url, $model, $encrypted_api_key, null, null, null, null,
                    '["chat","streaming","tool-calls"]', 1, 'healthy', null, 0, null, null,
                    $updated_at, $updated_at
                ) on conflict(id) do update set
                    base_url = excluded.base_url, model = excluded.model,
                    encrypted_api_key = excluded.encrypted_api_key, updated_at = excluded.updated_at
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$base_url", normalizedBaseUrl);
                cmd.Parameters.AddWithValue("$model", normalizedModel);
                cmd.Parameters.AddWithValue("$encrypted_api_key", (object?)encryptedApiKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });

            Execute(connection, """
                insert into model_routes (purpose, provider_instance_id, model, updated_at)
                values ('chat', 'openai_default', $model, $updated_at)
                on conflict(purpose) do update set
                    provider_instance_id = excluded.provider_instance_id,
                    model = excluded.model, updated_at = excluded.updated_at
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$model", normalizedModel);
                cmd.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return new StoredModelSettings(normalizedBaseUrl, normalizedModel, encryptedApiKey, now);
    }

    // --- Provider Instances ---

    public IReadOnlyList<ModelProviderInstanceDto> ListModelProviderInstances()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, driver, display_name, connection_kind, base_url, model, encrypted_api_key, binary_path, home_path,
                   server_url, launch_args, capabilities_json, enabled, health_status, cooldown_until, failure_count,
                   last_failure_at, last_error_category, created_at, updated_at
            from model_provider_instances order by updated_at desc, display_name
            """;
        using var reader = command.ExecuteReader();
        var providers = new List<ModelProviderInstanceDto>();
        while (reader.Read()) providers.Add(ReadModelProvider(reader).ToDto());
        return providers;
    }

    public StoredModelProviderInstance? GetStoredModelProviderInstance(string providerInstanceId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, driver, display_name, connection_kind, base_url, model, encrypted_api_key, binary_path, home_path,
                   server_url, launch_args, capabilities_json, enabled, health_status, cooldown_until, failure_count,
                   last_failure_at, last_error_category, created_at, updated_at
            from model_provider_instances where id = $id
            """;
        command.Parameters.AddWithValue("$id", providerInstanceId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelProvider(reader) : null;
    }

    public ModelProviderInstanceDto SaveModelProviderInstance(
        SaveModelProviderInstanceRequest request, string? encryptedApiKey)
    {
        var now = DateTimeOffset.UtcNow;
        var id = string.IsNullOrWhiteSpace(request.Id)
            ? $"provider_{Guid.NewGuid():N}" : NormalizeProviderInstanceId(request.Id);
        var driver = NormalizePlain(request.Driver, "openai-compatible");
        var displayName = NormalizePlain(request.DisplayName, driver);
        var connectionKind = NormalizeConnectionKind(request.ConnectionKind, driver);
        var baseUrl = NormalizeOptionalUrl(request.BaseUrl);
        var model = NormalizeOptional(request.Model);
        var capabilities = request.Capabilities is { Count: > 0 }
            ? request.Capabilities.Select(NormalizeOptional).Where(v => !string.IsNullOrWhiteSpace(v)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : InferCapabilities(driver, connectionKind);
        var capabilitiesJson = JsonSerializer.Serialize(capabilities, TinadecJson.Options);
        var existing = GetStoredModelProviderInstance(id);
        var health = ResolveProviderHealth(capabilities, existing);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into model_provider_instances (
                    id, driver, display_name, connection_kind, base_url, model, encrypted_api_key,
                    binary_path, home_path, server_url, launch_args, capabilities_json, enabled,
                    health_status, cooldown_until, failure_count, last_failure_at, last_error_category,
                    created_at, updated_at
                ) values (
                    $id, $driver, $display_name, $connection_kind, $base_url, $model, $encrypted_api_key,
                    $binary_path, $home_path, $server_url, $launch_args, $capabilities_json, $enabled,
                    $health_status, $cooldown_until, $failure_count, $last_failure_at, $last_error_category,
                    $created_at, $updated_at
                ) on conflict(id) do update set
                    driver = excluded.driver, display_name = excluded.display_name,
                    connection_kind = excluded.connection_kind, base_url = excluded.base_url,
                    model = excluded.model, encrypted_api_key = excluded.encrypted_api_key,
                    binary_path = excluded.binary_path, home_path = excluded.home_path,
                    server_url = excluded.server_url, launch_args = excluded.launch_args,
                    capabilities_json = excluded.capabilities_json, enabled = excluded.enabled,
                    health_status = excluded.health_status, cooldown_until = excluded.cooldown_until,
                    failure_count = excluded.failure_count, last_failure_at = excluded.last_failure_at,
                    last_error_category = excluded.last_error_category, updated_at = excluded.updated_at
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$driver", driver);
                cmd.Parameters.AddWithValue("$display_name", displayName);
                cmd.Parameters.AddWithValue("$connection_kind", connectionKind);
                cmd.Parameters.AddWithValue("$base_url", (object?)baseUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$encrypted_api_key", (object?)encryptedApiKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$binary_path", (object?)NormalizeOptional(request.BinaryPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$home_path", (object?)NormalizeOptional(request.HomePath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$server_url", (object?)NormalizeOptionalUrl(request.ServerUrl) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$launch_args", (object?)NormalizeOptional(request.LaunchArgs) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$capabilities_json", capabilitiesJson);
                cmd.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$health_status", ToProviderHealthStatusStorageValue(health.HealthStatus));
                cmd.Parameters.AddWithValue("$cooldown_until", health.CooldownUntil is null ? DBNull.Value : health.CooldownUntil.Value.ToString("O"));
                cmd.Parameters.AddWithValue("$failure_count", health.FailureCount);
                cmd.Parameters.AddWithValue("$last_failure_at", health.LastFailureAt is null ? DBNull.Value : health.LastFailureAt.Value.ToString("O"));
                cmd.Parameters.AddWithValue("$last_error_category", health.LastErrorCategory is null ? DBNull.Value : ToProviderErrorCategoryStorageValue(health.LastErrorCategory.Value));
                cmd.Parameters.AddWithValue("$created_at", now.ToString("O"));
                cmd.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return GetStoredModelProviderInstance(id)?.ToDto()
            ?? throw new InvalidOperationException("Saved model provider instance was not found.");
    }

    public bool DeleteModelProviderInstance(string providerInstanceId)
    {
        var existing = GetStoredModelProviderInstance(providerInstanceId);
        if (existing is null) return false;
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, "delete from model_routes where provider_instance_id = $p",
                cmd => cmd.Parameters.AddWithValue("$p", providerInstanceId));
            Execute(connection, "delete from model_provider_instances where id = $id",
                cmd => cmd.Parameters.AddWithValue("$id", providerInstanceId));
        }
        return true;
    }

    // --- Health ---

    public void RecordModelProviderFailure(string providerInstanceId, ProviderErrorCategory category, DateTimeOffset now)
    {
        var provider = GetStoredModelProviderInstance(providerInstanceId)
            ?? throw new InvalidOperationException($"Model provider '{providerInstanceId}' was not found.");
        var failureCount = provider.FailureCount + 1;
        var capabilities = provider.Capabilities
            .Where(c => !HasCapabilityKey(c, "health") && !HasCapabilityKey(c, "cooldown_started_at")
                && !HasCapabilityKey(c, "cooldown_until") && !HasCapabilityKey(c, "last_error")
                && !HasCapabilityKey(c, "failure_count"))
            .Concat([
                "health:cooldown",
                $"cooldown_started_at:{now:O}",
                $"cooldown_until:{now.AddMinutes(5):O}",
                $"last_error:{category.ToString()}",
                $"failure_count:{failureCount}"
            ]).ToArray();
        var request = new SaveModelProviderInstanceRequest(
            provider.Id, provider.Driver, provider.DisplayName, provider.ConnectionKind,
            provider.BaseUrl, provider.Model, null, false,
            provider.BinaryPath, provider.HomePath, provider.ServerUrl, provider.LaunchArgs,
            capabilities, provider.Enabled);
        SaveModelProviderInstance(request, provider.EncryptedApiKey);
    }

    public void RecordModelProviderSuccess(string providerInstanceId)
    {
        var provider = GetStoredModelProviderInstance(providerInstanceId)
            ?? throw new InvalidOperationException($"Model provider '{providerInstanceId}' was not found.");
        if (provider.HealthStatus is not ProviderHealthStatus.Cooldown and not ProviderHealthStatus.Unhealthy)
            return;

        var now = DateTimeOffset.UtcNow;
        var capabilities = provider.Capabilities
            .Where(c => !HasCapabilityKey(c, "health") && !HasCapabilityKey(c, "cooldown_started_at")
                && !HasCapabilityKey(c, "cooldown_until") && !HasCapabilityKey(c, "last_error")
                && !HasCapabilityKey(c, "failure_count"))
            .Concat(["health:healthy", "failure_count:0"]).ToArray();
        var capabilitiesJson = JsonSerializer.Serialize(capabilities, TinadecJson.Options);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update model_provider_instances
                set health_status = $hs, cooldown_until = null, failure_count = 0,
                    last_failure_at = null, last_error_category = null,
                    capabilities_json = $cj, updated_at = $ua
                where id = $id
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$hs", "healthy");
                cmd.Parameters.AddWithValue("$cj", capabilitiesJson);
                cmd.Parameters.AddWithValue("$ua", now.ToString("O"));
                cmd.Parameters.AddWithValue("$id", providerInstanceId);
            });
        }
    }

    // --- Routes ---

    public IReadOnlyList<ModelRouteDto> ListModelRoutes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select purpose, provider_instance_id, model, updated_at from model_routes order by purpose";
        using var reader = command.ExecuteReader();
        var routes = new List<ModelRouteDto>();
        while (reader.Read()) routes.Add(ReadModelRoute(reader));
        return routes;
    }

    public ModelRouteDto? GetModelRoute(string purpose)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select purpose, provider_instance_id, model, updated_at from model_routes where purpose = $purpose";
        command.Parameters.AddWithValue("$purpose", NormalizeRoutePurpose(purpose));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelRoute(reader) : null;
    }

    public ModelRouteDto SaveModelRoute(string purpose, string providerInstanceId, string? model)
    {
        var normalizedPurpose = NormalizeRoutePurpose(purpose);
        var normalizedModel = NormalizeOptional(model);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into model_routes (purpose, provider_instance_id, model, updated_at)
                values ($purpose, $pid, $model, $ua)
                on conflict(purpose) do update set
                    provider_instance_id = excluded.provider_instance_id,
                    model = excluded.model, updated_at = excluded.updated_at
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$purpose", normalizedPurpose);
                cmd.Parameters.AddWithValue("$pid", providerInstanceId);
                cmd.Parameters.AddWithValue("$model", (object?)normalizedModel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ua", now.ToString("O"));
            });
        }
        return new ModelRouteDto(normalizedPurpose, providerInstanceId, normalizedModel, now);
    }

    // --- Private Helpers ---

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, Action<SqliteCommand>? configure = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset ParseTime(string value)
        => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static StoredModelProviderInstance ReadModelProvider(SqliteDataReader reader)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(11), TinadecJson.Options) ?? [];
        return new StoredModelProviderInstance(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            capabilities, reader.GetInt32(12) == 1,
            ParseProviderHealthStatus(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseTime(reader.GetString(14)),
            reader.GetInt32(15),
            reader.IsDBNull(16) ? null : ParseTime(reader.GetString(16)),
            reader.IsDBNull(17) ? null : ParseProviderErrorCategory(reader.GetString(17)),
            ParseTime(reader.GetString(18)), ParseTime(reader.GetString(19)));
    }

    private static ModelRouteDto ReadModelRoute(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), ParseTime(reader.GetString(3)));

    private static string NormalizeProviderInstanceId(string value)
    {
        var normalized = new string(value.Trim().ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
        normalized = normalized.Trim('_', '-');
        return string.IsNullOrWhiteSpace(normalized) ? $"provider_{Guid.NewGuid():N}" : normalized;
    }

    private static string NormalizeRoutePurpose(string value) => NormalizePlain(value, "chat").ToLowerInvariant();
    private static string NormalizePlain(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeOptionalUrl(string? value) => NormalizeOptional(value)?.TrimEnd('/');

    private static string NormalizeConnectionKind(string? value, string driver)
    {
        var normalized = NormalizePlain(value, InferConnectionKind(driver)).ToLowerInvariant();
        return normalized is "api-key" or "cli" or "local-server" or "http" or "public-api" ? normalized : InferConnectionKind(driver);
    }

    private static string InferConnectionKind(string driver)
    {
        var n = driver.ToLowerInvariant();
        if (n.Contains("cli", StringComparison.OrdinalIgnoreCase) || n.Contains("cursor", StringComparison.OrdinalIgnoreCase) || n.Contains("opencode", StringComparison.OrdinalIgnoreCase))
            return "cli";
        if (ProviderTemplateRules.IsLocalOpenAiCompatibleDriver(n))
            return "local-server";
        return n is "pollinations" ? "public-api" : "api-key";
    }

    private static string[] InferCapabilities(string driver, string connectionKind)
    {
        if (connectionKind.Equals("cli", StringComparison.OrdinalIgnoreCase))
            return driver.Contains("cursor", StringComparison.OrdinalIgnoreCase)
                ? ["agent", "cli", "acp"] : ["agent", "cli", "workspace"];
        if (connectionKind.Equals("local-server", StringComparison.OrdinalIgnoreCase) || connectionKind.Equals("public-api", StringComparison.OrdinalIgnoreCase))
            return connectionKind.Equals("local-server", StringComparison.OrdinalIgnoreCase)
                ? ["chat", "local", "no-api-key"] : ["chat", "streaming", "public-api", "no-api-key"];
        return ["chat", "streaming", "tool-calls"];
    }

    private static ProviderHealthState ResolveProviderHealth(IReadOnlyList<string> capabilities, StoredModelProviderInstance? existing)
    {
        var health = ResolveCapabilityValue(capabilities, "health") is { } hv ? ParseProviderHealthStatus(hv) : existing?.HealthStatus ?? ProviderHealthStatus.Healthy;
        var cooldownUntil = ResolveCapabilityValue(capabilities, "cooldown_until") is { } cv ? ParseTime(cv) : existing?.CooldownUntil;
        var failureCount = ResolveCapabilityValue(capabilities, "failure_count") is { } fv && int.TryParse(fv, out var pfc) ? pfc : existing?.FailureCount ?? 0;
        var lastFailureAt = ResolveCapabilityValue(capabilities, "cooldown_started_at") is { } fav ? ParseTime(fav) : existing?.LastFailureAt;
        var lastErrorCategory = ResolveCapabilityValue(capabilities, "last_error") is { } ev ? ParseProviderErrorCategory(ev) : existing?.LastErrorCategory;
        return new ProviderHealthState(health, cooldownUntil, failureCount, lastFailureAt, lastErrorCategory);
    }

    private static ProviderHealthStatus ParseProviderHealthStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "healthy" => ProviderHealthStatus.Healthy,
        "unhealthy" => ProviderHealthStatus.Unhealthy,
        "disabled" => ProviderHealthStatus.Disabled,
        "cooldown" => ProviderHealthStatus.Cooldown,
        _ => ProviderHealthStatus.Unknown
    };

    private static string ToProviderHealthStatusStorageValue(ProviderHealthStatus status) => status switch
    {
        ProviderHealthStatus.Healthy => "healthy",
        ProviderHealthStatus.Unhealthy => "unhealthy",
        ProviderHealthStatus.Disabled => "disabled",
        ProviderHealthStatus.Cooldown => "cooldown",
        _ => "unknown"
    };

    private static ProviderErrorCategory ParseProviderErrorCategory(string value) => value.Trim().ToLowerInvariant() switch
    {
        "authentication_failed" or "authenticationfailed" => ProviderErrorCategory.AuthenticationFailed,
        "rate_limited" or "ratelimited" => ProviderErrorCategory.RateLimited,
        "timeout" => ProviderErrorCategory.Timeout,
        "provider_unavailable" or "providerunavailable" => ProviderErrorCategory.ProviderUnavailable,
        "invalid_request" or "invalidrequest" => ProviderErrorCategory.InvalidRequest,
        "cancelled" => ProviderErrorCategory.Cancelled,
        _ => ProviderErrorCategory.Unknown
    };

    private static string ToProviderErrorCategoryStorageValue(ProviderErrorCategory category) => category switch
    {
        ProviderErrorCategory.AuthenticationFailed => "authentication_failed",
        ProviderErrorCategory.RateLimited => "rate_limited",
        ProviderErrorCategory.Timeout => "timeout",
        ProviderErrorCategory.ProviderUnavailable => "provider_unavailable",
        ProviderErrorCategory.InvalidRequest => "invalid_request",
        ProviderErrorCategory.Cancelled => "cancelled",
        _ => "unknown"
    };

    private static string? ResolveCapabilityValue(IReadOnlyList<string> capabilities, string key)
    {
        var prefix = key + ":";
        return capabilities.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private static bool HasCapabilityKey(string capability, string key)
        => capability.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase);

    private sealed record ProviderHealthState(
        ProviderHealthStatus HealthStatus,
        DateTimeOffset? CooldownUntil,
        int FailureCount,
        DateTimeOffset? LastFailureAt,
        ProviderErrorCategory? LastErrorCategory);
}
