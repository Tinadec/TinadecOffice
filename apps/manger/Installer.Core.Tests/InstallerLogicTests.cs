using Installer.Core.Models;
using Installer.Core.Services;
using Xunit;

namespace Tinadec.Installer.Core.Tests;

public class MemoryStorageTests
{
    [Fact]
    public async Task Save_and_load_roundtrip()
    {
        var storage = new MemoryStorageProvider();
        var app = new InstalledApplication
        {
            Id = "tinadec-core",
            Name = "Tinadec Core",
            Version = "0.1.0",
            InstallPath = "/apps/tinadec-core/0.1.0",
            IsActive = true
        };

        await storage.SaveAsync(app.Id, app);
        var loaded = await storage.LoadAsync<InstalledApplication>("tinadec-core");

        Assert.NotNull(loaded);
        Assert.Equal("0.1.0", loaded.Version);
        Assert.Equal("Tinadec Core", loaded.Name);
    }

    [Fact]
    public async Task Remove_deletes_record()
    {
        var storage = new MemoryStorageProvider();
        await storage.SaveAsync("app-a", new InstalledApplication { Id = "app-a", IsActive = true });

        await storage.RemoveAsync("app-a");

        Assert.Null(await storage.LoadAsync<InstalledApplication>("app-a"));
        Assert.Empty(await storage.ListInstalledAsync());
    }

    [Fact]
    public async Task ListInstalled_skips_inactive()
    {
        var storage = new MemoryStorageProvider();
        await storage.SaveAsync("active", new InstalledApplication { Id = "active", IsActive = true });
        await storage.SaveAsync("inactive", new InstalledApplication { Id = "inactive", IsActive = false });

        var list = await storage.ListInstalledAsync();

        var only = Assert.Single(list);
        Assert.Equal("active", only.Id);
    }
}

public class InstallationRecordTests
{
    [Fact]
    public void Upsert_replaces_existing_entry()
    {
        var record = new InstallationRecord();
        record.Upsert(new InstalledApplication { Id = "app", Version = "1.0.0" });
        record.Upsert(new InstalledApplication { Id = "app", Version = "2.0.0" });

        var single = Assert.Single(record.Applications);
        Assert.Equal("2.0.0", single.Version);
    }

    [Fact]
    public void IsInstalled_considers_only_active_apps()
    {
        var record = new InstallationRecord();
        record.Upsert(new InstalledApplication { Id = "app", IsActive = false });

        Assert.False(record.IsInstalled("app"));
    }
}

public class DependencyResolverTests
{
    private static (DependencyResolver Resolver, MemoryStorageProvider Storage) Create(
        params Application[] manifests)
    {
        var storage = new MemoryStorageProvider();
        var manifestService = new ManifestService([.. manifests]);
        return (new DependencyResolver(manifestService, storage), storage);
    }

    [Fact]
    public async Task Single_app_without_dependencies_resolves_cleanly()
    {
        var (resolver, _) = Create(new Application { Id = "solo", Version = "1.0.0" });

        var result = await resolver.ResolveForInstallAsync(new Application { Id = "solo", Version = "1.0.0" });

        Assert.True(result.IsSuccess);
        var plan = Assert.Single(result.InstallationOrder);
        Assert.Equal(DependencyStatus.ToInstall, plan.Status);
    }

    [Fact]
    public async Task Missing_required_dependency_is_reported()
    {
        var (resolver, _) = Create(new Application
        {
            Id = "app",
            Version = "1.0.0",
            Dependencies = [new DependencyRequirement { PackageId = "ghost", VersionRange = ">=1.0.0" }]
        });

        var result = await resolver.ResolveForInstallAsync(
            new Application { Id = "app", Version = "1.0.0", Dependencies = [new DependencyRequirement { PackageId = "ghost", VersionRange = ">=1.0.0" }] });

        Assert.False(result.IsSuccess);
        Assert.Contains("ghost", result.MissingDependencies);
    }

    [Fact]
    public async Task Optional_missing_dependency_only_warns()
    {
        var dep = new DependencyRequirement { PackageId = "ghost", VersionRange = "*", Optional = true };
        var (resolver, _) = Create(new Application { Id = "app", Version = "1.0.0", Dependencies = [dep] });

        var result = await resolver.ResolveForInstallAsync(
            new Application { Id = "app", Version = "1.0.0", Dependencies = [dep] });

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Warnings, w => w.Contains("ghost"));
    }

    [Fact]
    public async Task Already_installed_app_is_satisfied()
    {
        var (resolver, storage) = Create(new Application { Id = "dep", Version = "1.2.0" });
        await storage.SaveAsync("dep", new InstalledApplication
        {
            Id = "dep",
            Version = "1.2.0",
            IsActive = true
        });

        var result = await resolver.ResolveForInstallAsync(new Application { Id = "dep", Version = "1.2.0" });

        Assert.True(result.IsSuccess);
        Assert.Equal(DependencyStatus.Satisfied, Assert.Single(result.InstallationOrder).Status);
    }

    [Fact]
    public async Task Dependencies_are_ordered_before_dependents()
    {
        var (resolver, _) = Create(
            new Application { Id = "leaf", Version = "1.0.0" },
            new Application
            {
                Id = "root",
                Version = "1.0.0",
                Dependencies = [new DependencyRequirement { PackageId = "leaf", VersionRange = "*" }]
            });

        var result = await resolver.ResolveForInstallAsync(
            new Application
            {
                Id = "root",
                Version = "1.0.0",
                Dependencies = [new DependencyRequirement { PackageId = "leaf", VersionRange = "*" }]
            });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.InstallationOrder.Count);
        Assert.Equal("leaf", result.InstallationOrder[0].Application.Id);
        Assert.Equal("root", result.InstallationOrder[1].Application.Id);
    }

    [Fact]
    public async Task Circular_dependency_is_detected()
    {
        var depOnB = new DependencyRequirement { PackageId = "b", VersionRange = "*" };
        var depOnA = new DependencyRequirement { PackageId = "a", VersionRange = "*" };
        var (resolver, _) = Create(
            new Application { Id = "a", Version = "1.0.0", Dependencies = [depOnB] },
            new Application { Id = "b", Version = "1.0.0", Dependencies = [depOnA] });

        var result = await resolver.ResolveForInstallAsync(
            new Application { Id = "a", Version = "1.0.0", Dependencies = [depOnB] });

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Warnings, w => w.Contains("Circular"));
    }
}

public class ManifestServiceTests
{
    [Fact]
    public void Search_matches_id_name_and_tags()
    {
        var service = new ManifestService(
        [
            new Application { Id = "tinadec-core", Name = "Tinadec Core", Tags = ["backend"] },
            new Application { Id = "tool", Name = "Helper", Description = "gateway helper" }
        ]);

        Assert.Single(service.Search("tinadec"));
        Assert.Single(service.Search("backend"));
        Assert.Single(service.Search("gateway"));
    }

    [Fact]
    public void FindByIdOrName_prefers_exact_id()
    {
        var service = new ManifestService(
        [
            new Application { Id = "core", Name = "Something Else" },
            new Application { Id = "other", Name = "Core" }
        ]);

        Assert.Equal("core", service.FindByIdOrName("core")!.Id);
    }

    [Fact]
    public void Default_manifests_cover_tinadec_components()
    {
        var service = new ManifestService();

        var ids = service.GetAllApplications().Select(a => a.Id).ToList();

        Assert.Contains("tinadec-core", ids);
        Assert.Contains("tinadec-gateway", ids);
    }
}
