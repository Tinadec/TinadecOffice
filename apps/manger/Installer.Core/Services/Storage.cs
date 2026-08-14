using Installer.Core.Helpers;
using Installer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Installer.Core.Services;

/// <summary>
/// In-memory storage provider for development/testing
/// </summary>
public sealed class MemoryStorageProvider : IStorageProvider
{
    private readonly Dictionary<string, object> _storage = new();

    public Task<T?> LoadAsync<T>(string key) where T : class
    {
        if (_storage.TryGetValue(key, out var value) && value is T typed)
        {
            return Task.FromResult<T?>(typed);
        }
        return Task.FromResult<T?>(null);
    }

    public Task SaveAsync<T>(string key, T entity) where T : class
    {
        _storage[key] = entity;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _storage.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InstalledApplication>> ListInstalledAsync()
    {
        var result = _storage.Values
            .OfType<InstalledApplication>()
            .Where(a => a.IsActive)
            .ToList();
        return Task.FromResult<IReadOnlyList<InstalledApplication>>(result);
    }
}

/// <summary>
/// File-based persistent storage. A single installation_records.json tracks all
/// installed applications; writes are atomic (temp file + rename).
/// </summary>
public sealed class FileStorageProvider : IStorageProvider
{
    private readonly string _recordPath;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileStorageProvider(string? storePath = null, ILogger? logger = null)
    {
        var root = storePath ?? PathHelper.GetStoreDirectory();
        Directory.CreateDirectory(root);
        _recordPath = Path.Combine(root, "installation_records.json");
        _logger = logger;
    }

    public async Task<T?> LoadAsync<T>(string key) where T : class
    {
        var record = await ReadRecordAsync();
        var installed = record.Get(key);

        if (installed is null)
            return null;

        if (typeof(T) == typeof(InstalledApplication))
            return (T)(object)installed;

        return JsonSerializationHelper.FromJsonSnakeCase<T>(
            JsonSerializationHelper.ToJsonSnakeCase(installed));
    }

    public async Task SaveAsync<T>(string key, T entity) where T : class
    {
        await _writeLock.WaitAsync();
        try
        {
            var record = await ReadRecordAsync();

            if (entity is InstalledApplication installed)
            {
                record.Upsert(installed);
            }
            else
            {
                // Generic entities are stored as embedded manifest info
                var manifest = JsonSerializationHelper.FromJsonSnakeCase<InstalledApplication>(
                    JsonSerializationHelper.ToJsonSnakeCase(entity));
                if (manifest is not null)
                {
                    manifest.Id = key;
                    record.Upsert(manifest);
                }
            }

            await WriteRecordAsync(record);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveAsync(string key)
    {
        await _writeLock.WaitAsync();
        try
        {
            var record = await ReadRecordAsync();
            if (record.Remove(key))
            {
                await WriteRecordAsync(record);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<InstalledApplication>> ListInstalledAsync()
    {
        var record = await ReadRecordAsync();
        return record.Applications.Where(a => a.IsActive).ToList();
    }

    private async Task<InstallationRecord> ReadRecordAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            if (!File.Exists(_recordPath))
                return new InstallationRecord();

            var content = await File.ReadAllTextAsync(_recordPath);
            var record = JsonSerializationHelper.FromJsonSnakeCase<InstallationRecord>(content);
            if (record is null)
            {
                _logger?.LogWarning("Installation record was corrupt; starting a fresh store");
                return new InstallationRecord();
            }
            return record;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read installation records");
            return new InstallationRecord();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteRecordAsync(InstallationRecord record)
    {
        record.LastSync = DateTime.UtcNow;
        var content = JsonSerializationHelper.ToJsonSnakeCase(record);

        var tempPath = _recordPath + ".tmp_" + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(tempPath, content);
        File.Move(tempPath, _recordPath, overwrite: true);
    }
}

/// <summary>
/// Application manifest service for discovering available applications
/// </summary>
public sealed class ManifestService
{
    private readonly List<Application> _manifests;
    private readonly ILogger? _logger;

    public ManifestService(List<Application>? manifests = null, ILogger? logger = null)
    {
        _manifests = manifests ?? CreateDefaultManifests();
        _logger = logger;
    }

    /// <summary>
    /// Get all registered applications
    /// </summary>
    public IReadOnlyList<Application> GetAllApplications() => _manifests;

    /// <summary>
    /// Find an application by exact id or name substring
    /// </summary>
    public Application? FindByIdOrName(string appIdOrName)
    {
        return _manifests.FirstOrDefault(a => a.Id == appIdOrName)
            ?? _manifests.FirstOrDefault(a =>
                a.Name.Contains(appIdOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Search applications by keyword across name/description/tags
    /// </summary>
    public List<Application> Search(string keyword)
    {
        return _manifests
            .Where(a => a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        a.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        a.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Filter by category/tag
    /// </summary>
    public List<Application> FilterByTag(string tag)
    {
        return _manifests.Where(a => a.Tags.Contains(tag)).ToList();
    }

    /// <summary>
    /// Built-in manifests for TinadecOffice components (offline fallback)
    /// </summary>
    private static List<Application> CreateDefaultManifests()
    {
        return
        [
            new Application
            {
                Id = "tinadec-core",
                Name = "Tinadec Core",
                Description = "MAF-based .NET 10 modular monolith agent orchestration core (port 48731)",
                GitHubRepo = "tinadec/TinadecCore",
                Version = "0.1.0",
                Tags = ["core", "backend", "dotnet"],
                IsCoreComponent = true
            },
            new Application
            {
                Id = "tinadec-gateway",
                Name = "Tinadec Gateway",
                Description = "Bun/Elysia BFF proxy between Desktop and Core (port 48730)",
                GitHubRepo = "tinadec/TinadecGateway",
                Version = "0.1.0",
                Tags = ["gateway", "typescript", "bff"],
                IsCoreComponent = true
            },
            new Application
            {
                Id = "tinadec-desktop",
                Name = "Tinadec Desktop",
                Description = "Electron + Vue 3 desktop workbench with Debug Studio (port 5173)",
                GitHubRepo = "tinadec/TinadecDesktop",
                Version = "0.1.0",
                Tags = ["desktop", "electron", "vue"],
                IsCoreComponent = true
            },
            new Application
            {
                Id = "tinadec-tools",
                Name = "Tinadec Tools",
                Description = "Approval-aware tool host with AOT-safe JSON and [ToolFunction] registry",
                GitHubRepo = "tinadec/TinadecTools",
                Version = "0.1.0",
                Tags = ["tools", "dotnet", "aot"]
            }
        ];
    }
}
