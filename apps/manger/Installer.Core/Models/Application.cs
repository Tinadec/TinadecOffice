using System.Text.Json.Serialization;
using Installer.Core.Helpers;

namespace Installer.Core.Models;

/// <summary>
/// Application manifest and metadata definition
/// </summary>
public class Application
{
    /// <summary>
    /// Unique identifier for the application (e.g., "tinadec-gateway")
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of the application functionality
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Latest stable version available
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// GitHub repository path (e.g., "user/repo")
    /// </summary>
    [JsonPropertyName("github_repo")]
    public string GitHubRepo { get; set; } = string.Empty;

    /// <summary>
    /// Homepage URL
    /// </summary>
    [JsonPropertyName("homepage")]
    public string? HomePage { get; set; }

    /// <summary>
    /// Application category/tags
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Required dependencies
    /// </summary>
    [JsonPropertyName("dependencies")]
    public List<DependencyRequirement> Dependencies { get; set; } = new();

    /// <summary>
    /// Available release assets from GitHub
    /// </summary>
    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; set; } = new();

    /// <summary>
    /// License information
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; set; }

    /// <summary>
    /// Minimum .NET runtime version required (if applicable)
    /// </summary>
    [JsonPropertyName("min_dotnet_version")]
    public string? MinDotNetVersion { get; set; }

    /// <summary>
    /// Whether this app is a TinadecOffice core component
    /// </summary>
    [JsonPropertyName("is_core_component")]
    public bool IsCoreComponent { get; set; }

    /// <summary>
    /// Create a deep copy of this application
    /// </summary>
    public Application Clone()
    {
        return JsonSerializationHelper.Clone(this);
    }
}

/// <summary>
/// Dependency requirement specification
/// </summary>
public sealed class DependencyRequirement
{
    /// <summary>
    /// Package ID that must be installed
    /// </summary>
    [JsonPropertyName("package_id")]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>
    /// Version range constraint (e.g., ">=1.0.0,<2.0.0" or "latest")
    /// </summary>
    [JsonPropertyName("version_range")]
    public string VersionRange { get; set; } = string.Empty;

    /// <summary>
    /// Optional dependency (install if available)
    /// </summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; set; }

    /// <summary>
    /// Friendly name for display
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    public override string ToString() => $"{PackageId} ({VersionRange})";
}

/// <summary>
/// Represents a downloadable asset from a release
/// </summary>
public sealed class ReleaseAsset
{
    /// <summary>
    /// Download URL for the asset
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Browser download URL
    /// </summary>
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Asset file name (e.g., "app-1.0.0-win-x64.zip")
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Size in bytes
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    /// SHA256 checksum for validation (optional)
    /// </summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary>
    /// Target platform (win, linux, macos)
    /// </summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// Target architecture (x64, arm64)
    /// </summary>
    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    /// <summary>
    /// File extension (.zip, .tar.gz, etc.)
    /// </summary>
    [JsonPropertyName("extension")]
    public string Extension { get; set; } = string.Empty;

    /// <summary>
    /// Is the asset optimal for current platform?
    /// </summary>
    public bool MatchesCurrentPlatform => 
        Platform.ToLowerInvariant() == PlatformHelper.CurrentPlatformString &&
        Architecture.ToLowerInvariant() == PlatformHelper.CurrentArchitectureString;

    public override string ToString() => $"{Name} ({Size / 1024:F0} KB)";
}
