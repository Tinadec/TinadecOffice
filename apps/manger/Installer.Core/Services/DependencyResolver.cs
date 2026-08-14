using Installer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Installer.Core.Services;

/// <summary>
/// Version conflict details
/// </summary>
public record VersionConflict(string PackageId, string RequiredBy, string CurrentVersion, string TargetVersion);

/// <summary>
/// Resolution result with dependency graph
/// </summary>
public sealed class DependencyResolutionResult
{
    /// <summary>
    /// Packages to install in dependency order (dependencies first)
    /// </summary>
    public List<ApplicationInstallPlan> InstallationOrder { get; set; } = new();

    /// <summary>
    /// Upgrade impact analysis when resolving an upgrade
    /// </summary>
    public List<ApplicationUpgradePlan> UpgradePlans { get; set; } = new();

    /// <summary>
    /// Warnings generated during resolution
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Missing dependency ids
    /// </summary>
    public List<string> MissingDependencies { get; } = new();

    /// <summary>
    /// Version conflicts detected
    /// </summary>
    public List<VersionConflict> Conflicts { get; } = new();

    /// <summary>
    /// True when nothing blocks installation
    /// </summary>
    public bool IsSuccess => MissingDependencies.Count == 0 &&
                             Conflicts.Count == 0 &&
                             InstallationOrder.All(p => p.Status != DependencyStatus.Circular);

    public void AddWarning(string message) => Warnings.Add(message);
}

/// <summary>
/// Status of an application in the installation plan
/// </summary>
public enum DependencyStatus
{
    Satisfied,      // Already installed with a compatible version
    ToInstall,      // Needs installation
    Missing,        // Cannot satisfy dependency (no manifest)
    Conflict,       // Version conflict with an installed app
    Circular        // Detected circular dependency
}

/// <summary>
/// Status of an upgrade operation
/// </summary>
public enum UpgradeStatus
{
    Ready,
    Blocked,
    Conflict,
    OptionalSkipped
}

/// <summary>
/// Plan to install a single application with resolved status
/// </summary>
public sealed class ApplicationInstallPlan
{
    public Application Application { get; set; } = null!;
    public DependencyStatus Status { get; set; }
    public List<string> DependentApplications { get; set; } = new();
}

/// <summary>
/// Plan to upgrade an existing application
/// </summary>
public sealed class ApplicationUpgradePlan
{
    public Application Application { get; set; } = null!;
    public string CurrentVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public UpgradeStatus Status { get; set; } = UpgradeStatus.Ready;
    public string RequiredBy { get; set; } = string.Empty;
}

/// <summary>
/// Resolves and manages package dependencies (topological order + conflict detection)
/// </summary>
public sealed class DependencyResolver
{
    private readonly ManifestService _manifestService;
    private readonly IStorageProvider _storage;
    private readonly ILogger? _logger;

    public DependencyResolver(
        ManifestService manifestService,
        IStorageProvider storage,
        ILogger? logger = null)
    {
        _manifestService = manifestService;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Resolve the full dependency closure for installing an application.
    /// Returns plans ordered so dependencies come before dependents.
    /// </summary>
    public async Task<DependencyResolutionResult> ResolveForInstallAsync(
        Application targetApp,
        CancellationToken ct = default)
    {
        var result = new DependencyResolutionResult();
        var planByApp = new Dictionary<string, ApplicationInstallPlan>();

        // Detect circular dependencies before walking
        var cyclePath = FindCycle(targetApp.Id, []);
        if (cyclePath is { Count: > 0 })
        {
            result.InstallationOrder.Add(new ApplicationInstallPlan
            {
                Application = targetApp,
                Status = DependencyStatus.Circular
            });
            result.AddWarning($"Circular dependency detected: {string.Join(" -> ", cyclePath)}");
            return result;
        }

        // Collect the closure recursively
        await CollectAsync(targetApp, requiredBy: null, result, planByApp, ct);

        // Topological ordering: dependencies before dependents
        result.InstallationOrder = TopologicallySort(planByApp);

        _logger?.LogDebug(
            "Resolved {Count} packages for {App} ({Missing} missing)",
            result.InstallationOrder.Count, targetApp.Id, result.MissingDependencies.Count);

        return result;
    }

    /// <summary>
    /// Check whether upgrading an app to a version breaks installed dependents
    /// </summary>
    public async Task<DependencyResolutionResult> CheckUpgradeCompatibilityAsync(
        string appId,
        string targetVersion,
        CancellationToken ct = default)
    {
        var result = new DependencyResolutionResult();

        var existing = await _storage.LoadAsync<InstalledApplication>(appId)
            ?? throw new InvalidOperationException($"Application '{appId}' is not installed");

        var manifest = _manifestService.FindByIdOrName(appId);
        if (manifest is null)
        {
            result.AddWarning($"No manifest found for '{appId}'; compatibility check skipped");
            return result;
        }

        var upgradePlan = new ApplicationUpgradePlan
        {
            Application = manifest,
            CurrentVersion = existing.Version,
            TargetVersion = targetVersion,
            Status = UpgradeStatus.Ready
        };
        result.UpgradePlans.Add(upgradePlan);

        // Inspect every registered app that depends on this one
        foreach (var dependent in _manifestService.GetAllApplications())
        {
            var requirement = dependent.Dependencies.FirstOrDefault(d => d.PackageId == appId);
            if (requirement is null) continue;

            upgradePlan.RequiredBy = string.IsNullOrEmpty(upgradePlan.RequiredBy)
                ? dependent.Name
                : $"{upgradePlan.RequiredBy}, {dependent.Name}";

            if (!VersionRangeMatcher.Satisfies(targetVersion, requirement.VersionRange))
            {
                upgradePlan.Status = UpgradeStatus.Conflict;
                result.Conflicts.Add(new VersionConflict(appId, dependent.Name, existing.Version, targetVersion));
                result.AddWarning(
                    $"Version {targetVersion} of '{appId}' breaks '{dependent.Name}' " +
                    $"(requires {requirement.VersionRange})");
            }
        }

        return result;
    }

    #region Internals

    private async Task CollectAsync(
        Application app,
        string? requiredBy,
        DependencyResolutionResult result,
        Dictionary<string, ApplicationInstallPlan> planByApp,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (planByApp.TryGetValue(app.Id, out var existingPlan))
        {
            if (requiredBy is not null)
                existingPlan.DependentApplications.Add(requiredBy);
            return;
        }

        var plan = new ApplicationInstallPlan
        {
            Application = app,
            Status = DependencyStatus.ToInstall
        };
        if (requiredBy is not null)
            plan.DependentApplications.Add(requiredBy);
        planByApp[app.Id] = plan;

        // Already installed with a compatible version?
        var installed = await _storage.LoadAsync<InstalledApplication>(app.Id);
        if (installed is { IsActive: true } &&
            VersionRangeMatcher.Satisfies(installed.Version, app.Version))
        {
            plan.Status = DependencyStatus.Satisfied;
        }

        foreach (var depReq in app.Dependencies)
        {
            var depApp = _manifestService.FindByIdOrName(depReq.PackageId);

            if (depApp is null)
            {
                if (!depReq.Optional)
                {
                    plan.Status = plan.Status == DependencyStatus.Satisfied
                        ? DependencyStatus.Conflict
                        : DependencyStatus.Missing;
                    result.MissingDependencies.Add(depReq.PackageId);
                    result.AddWarning($"Missing dependency: {depReq.PackageId} required by {app.Id}");
                }
                else
                {
                    result.AddWarning($"Optional dependency not available: {depReq.PackageId}");
                }
                continue;
            }

            // Installed dependency must satisfy the required range
            if (installed is { IsActive: true })
            {
                var depInstalled = await _storage.LoadAsync<InstalledApplication>(depApp.Id);
                if (depInstalled is { IsActive: true } &&
                    !VersionRangeMatcher.Satisfies(depInstalled.Version, depReq.VersionRange))
                {
                    result.Conflicts.Add(new VersionConflict(
                        depApp.Id, app.Id, depInstalled.Version, depReq.VersionRange));
                    result.AddWarning(
                        $"Installed {depApp.Id} v{depInstalled.Version} does not satisfy " +
                        $"{depReq.VersionRange} required by {app.Id}");
                }
            }

            await CollectAsync(depApp, app.Id, result, planByApp, ct);
        }
    }

    /// <summary>
    /// Standard DFS cycle detection; returns the cycle path or an empty list
    /// </summary>
    private List<string>? FindCycle(string appId, List<string> path)
    {
        if (path.Contains(appId))
        {
            var cycleStart = path.IndexOf(appId);
            var cycle = path.GetRange(cycleStart, path.Count - cycleStart);
            cycle.Add(appId);
            return cycle;
        }

        var appDef = _manifestService.FindByIdOrName(appId);
        if (appDef is null) return null;

        path.Add(appId);
        foreach (var dep in appDef.Dependencies)
        {
            if (FindCycle(dep.PackageId, path) is { } cycle)
                return cycle;
        }
        path.RemoveAt(path.Count - 1);

        return null;
    }

    /// <summary>
    /// Topological sort so that every dependency precedes its dependents
    /// </summary>
    private static List<ApplicationInstallPlan> TopologicallySort(
        Dictionary<string, ApplicationInstallPlan> planByApp)
    {
        var ordered = new List<ApplicationInstallPlan>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(string appId)
        {
            if (visited.Contains(appId) || !planByApp.TryGetValue(appId, out var plan))
                return;
            if (!visiting.Add(appId))
                return; // cycle guard

            foreach (var dep in plan.Application.Dependencies)
                Visit(dep.PackageId);

            visiting.Remove(appId);
            visited.Add(appId);
            ordered.Add(plan);
        }

        foreach (var key in planByApp.Keys)
            Visit(key);

        return ordered;
    }

    #endregion
}
