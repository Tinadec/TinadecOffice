using System.Text.Json.Serialization;
using Installer.Core.Helpers;

namespace Installer.Core.Models;

/// <summary>
/// Represents an installed application with runtime metadata
/// </summary>
public sealed class InstalledApplication : Application
{
    /// <summary>
    /// Installation path on disk
    /// </summary>
    [JsonPropertyName("install_path")]
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>
    /// Symbolic link or alias path (current version)
    /// </summary>
    [JsonPropertyName("symlink")]
    public string? Symlink { get; set; }

    /// <summary>
    /// Installation timestamp
    /// </summary>
    [JsonPropertyName("installed_at")]
    public DateTime InstalledAt { get; set; }

    /// <summary>
    /// SHA256 checksum of the downloaded package
    /// </summary>
    [JsonPropertyName("checksum")]
    public string? Checksum { get; set; }

    /// <summary>
    /// Configuration files preserved during upgrade
    /// </summary>
    [JsonPropertyName("config_preserved")]
    public bool ConfigPreserved { get; set; }

    /// <summary>
    /// Whether this installation is currently active
    /// </summary>
    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Last updated timestamp
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Create a deep copy including installed-specific fields
    /// </summary>
    public new InstalledApplication Clone()
    {
        var clone = (InstalledApplication)JsonSerializationHelper.Clone(this);
        // Ensure list references are fresh
        clone.Tags = new List<string>(Tags);
        clone.Dependencies = Dependencies.Select(d => 
            new DependencyRequirement 
            { 
                PackageId = d.PackageId,
                VersionRange = d.VersionRange,
                Optional = d.Optional,
                DisplayName = d.DisplayName
            }).ToList();
        clone.Assets = new List<ReleaseAsset>(Assets);
        return clone;
    }
}

/// <summary>
/// Store record for tracking all installed applications
/// </summary>
public sealed class InstallationRecord
{
    /// <summary>
    /// Unique identifier for this record entry
    /// </summary>
    [JsonPropertyName("record_id")]
    public string RecordId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// List of all installed applications
    /// </summary>
    [JsonPropertyName("applications")]
    public List<InstalledApplication> Applications { get; set; } = new();

    /// <summary>
    /// Store schema version for migration compatibility
    /// </summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Last synchronization timestamp
    /// </summary>
    [JsonPropertyName("last_sync")]
    public DateTime LastSync { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Add or update an installed application
    /// </summary>
    public void Upsert(InstalledApplication app)
    {
        var existingIndex = Applications.FindIndex(a => a.Id == app.Id);
        if (existingIndex >= 0)
        {
            Applications[existingIndex] = app;
        }
        else
        {
            Applications.Add(app);
        }
        LastSync = DateTime.UtcNow;
    }

    /// <summary>
    /// Remove an application by ID
    /// </summary>
    public bool Remove(string appId)
    {
        return Applications.RemoveAll(a => a.Id == appId) > 0;
    }

    /// <summary>
    /// Get application by ID
    /// </summary>
    public InstalledApplication? Get(string appId)
    {
        return Applications.FirstOrDefault(a => a.Id == appId);
    }

    /// <summary>
    /// Check if application is installed
    /// </summary>
    public bool IsInstalled(string appId)
    {
        return Applications.Any(a => a.Id == appId && a.IsActive);
    }
}
