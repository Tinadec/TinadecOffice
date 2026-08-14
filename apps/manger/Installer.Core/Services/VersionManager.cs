using Installer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Installer.Core.Services;

/// <summary>
/// Semantic version implementation (major.minor.patch[-prerelease][+build])
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }

    public SemanticVersion(int major, int minor, int patch, string? preRelease = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    /// <summary>
    /// Parse version strings like "1.2.3", "v1.2", "1.2.3-beta.1"
    /// </summary>
    public static SemanticVersion Parse(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        var mainPart = trimmed.Split('+')[0];
        var parts = mainPart.Split('-');

        var coreParts = parts[0].Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        var major = coreParts.Length > 0 && int.TryParse(coreParts[0], out var mj) ? mj : 0;
        var minor = coreParts.Length > 1 && int.TryParse(coreParts[1], out var mn) ? mn : 0;
        var patch = coreParts.Length > 2 && int.TryParse(coreParts[2], out var pt) ? pt : 0;

        var preRelease = parts.Length > 1 ? parts[1] : null;
        return new SemanticVersion(major, minor, patch, preRelease);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as SemanticVersion);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.Equals(right);
    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(PreRelease is null ? "" : "-" + PreRelease)}";

    private static int ComparePreRelease(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;  // release > pre-release
        if (b is null) return -1;

        var aParts = a.Split('.');
        var bParts = b.Split('.');

        for (var i = 0; i < Math.Max(aParts.Length, bParts.Length); i++)
        {
            if (i >= aParts.Length) return -1;
            if (i >= bParts.Length) return 1;

            var aIsNum = int.TryParse(aParts[i], out var an);
            var bIsNum = int.TryParse(bParts[i], out var bn);

            if (aIsNum && bIsNum)
            {
                var cmp = an.CompareTo(bn);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.CompareOrdinal(aParts[i], bParts[i]);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }
}

/// <summary>
/// Version range matching for dependency specifications
/// </summary>
public static class VersionRangeMatcher
{
    /// <summary>
    /// Check whether a version satisfies a range constraint.
    /// Supported: "latest", "*", ">=X", "<=X", ">X", "<X", "=X", exact "X", "A..B" inclusive.
    /// </summary>
    public static bool Satisfies(string version, string range)
    {
        if (string.IsNullOrWhiteSpace(range)) return true;
        if (range is "latest" or "*") return true;
        if (string.IsNullOrWhiteSpace(version)) return false;

        // Multiple constraints combined with ',' or ';' must all hold (AND)
        if (range.Contains(',') || range.Contains(';'))
        {
            return range.Split(',', ';')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .All(constraint => Satisfies(version, constraint));
        }

        var semVer = SemanticVersion.Parse(version);

        if (range.StartsWith(">=")) return semVer >= SemanticVersion.Parse(range[2..].Trim());
        if (range.StartsWith("<=")) return semVer <= SemanticVersion.Parse(range[2..].Trim());
        if (range.StartsWith('>'))  return semVer >  SemanticVersion.Parse(range[1..].Trim());
        if (range.StartsWith('<'))  return semVer <  SemanticVersion.Parse(range[1..].Trim());
        if (range.StartsWith("==")) return semVer == SemanticVersion.Parse(range[2..].Trim());
        if (range.StartsWith('='))  return semVer == SemanticVersion.Parse(range[1..].Trim());

        if (range.Contains(".."))
        {
            var parts = range.Split("..", 2);
            var min = SemanticVersion.Parse(parts[0].Trim());
            var max = SemanticVersion.Parse(parts[1].Trim());
            return semVer >= min && semVer <= max;
        }

        return semVer == SemanticVersion.Parse(range);
    }

    /// <summary>
    /// Find the latest version that satisfies a range.
    /// Pre-release versions are excluded by default (package-manager convention).
    /// </summary>
    public static string? FindLatestCompatible(IEnumerable<string> availableVersions, string range)
    {
        var stable = availableVersions
            .Where(v => Satisfies(v, range))
            .Select(SemanticVersion.Parse)
            .Where(v => v.PreRelease is null)
            .OrderByDescending(v => v)
            .ToList();

        if (stable.Count > 0)
            return stable[0].ToString();

        // No stable match: fall back to the newest pre-release that satisfies the range
        return availableVersions
            .Where(v => Satisfies(v, range))
            .Select(SemanticVersion.Parse)
            .OrderByDescending(v => v)
            .FirstOrDefault()?.ToString();
    }
}

/// <summary>
/// Manages installed application versions and upgrade paths
/// </summary>
public sealed class VersionManager
{
    private readonly IStorageProvider _storage;
    private readonly ILogger? _logger;

    public VersionManager(IStorageProvider storage, ILogger? logger = null)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Get all active installed applications
    /// </summary>
    public Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync() =>
        _storage.ListInstalledAsync();

    /// <summary>
    /// Detect installed applications whose manifest advertises a newer version
    /// </summary>
    public async Task<List<(InstalledApplication Installed, string Available)>> DetectOutdatedAsync(
        ManifestService manifestService,
        CancellationToken ct = default)
    {
        var installedApps = await _storage.ListInstalledAsync();
        var outdated = new List<(InstalledApplication, string)>();

        foreach (var installed in installedApps)
        {
            var manifest = manifestService.FindByIdOrName(installed.Id);
            if (manifest is null) continue;

            var installedVer = SemanticVersion.Parse(installed.Version);
            var availableVer = SemanticVersion.Parse(manifest.Version);

            if (installedVer < availableVer)
            {
                outdated.Add((installed, manifest.Version));
            }
        }

        return outdated;
    }

    /// <summary>
    /// Determine whether a target version is an upgrade, downgrade or no-op
    /// </summary>
    public async Task<VersionTransition> ClassifyTransitionAsync(
        string appId, string targetVersion, CancellationToken ct = default)
    {
        var existing = await _storage.LoadAsync<InstalledApplication>(appId);

        if (existing is null)
            return VersionTransition.FreshInstall;

        var current = SemanticVersion.Parse(existing.Version);
        var target = SemanticVersion.Parse(targetVersion);

        if (target > current) return VersionTransition.Upgrade;
        if (target < current) return VersionTransition.Downgrade;
        return VersionTransition.Same;
    }

    /// <summary>
    /// Normalize a version string to canonical form
    /// </summary>
    public static string NormalizeVersionFormat(string version)
    {
        try
        {
            return SemanticVersion.Parse(version).ToString();
        }
        catch
        {
            return version;
        }
    }
}

/// <summary>
/// Relationship between the installed and target versions
/// </summary>
public enum VersionTransition
{
    FreshInstall,
    Upgrade,
    Downgrade,
    Same
}
