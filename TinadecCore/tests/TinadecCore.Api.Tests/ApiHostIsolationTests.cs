using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class ApiHostIsolationTests
{
    [Fact]
    public void EveryConcreteApiFactoryUsesTheEarlyIsolationBase()
    {
        var factories = typeof(ApiHostIsolationTests).Assembly.GetTypes().Where(type =>
            !type.IsAbstract && typeof(WebApplicationFactory<Program>).IsAssignableFrom(type)).ToArray();
        Assert.NotEmpty(factories);
        Assert.All(factories, type => Assert.True(typeof(IsolatedApiFactory).IsAssignableFrom(type), type.FullName));
    }

    [Fact]
    public void ProcessFallbackAndDefaultPersistenceOptionsCannotSelectTheRealUserRoot()
    {
        ApiTestStorage.AssertFallbackIsActive();
        Assert.Equal(ApiTestStorage.Home, StorageScopePaths.UserRoot());
        var defaults = new TinadecPersistenceOptions();
        Assert.Equal(Path.Combine(ApiTestStorage.Home, "data"), defaults.DataRoot);
        Assert.Equal(Path.Combine(ApiTestStorage.Home, "data", "tinadec.db"), defaults.Sqlite.DatabasePath);
        Assert.Equal(ApiTestStorage.Home, ApiTestStorage.RequireManagedRoot(ApiTestStorage.Home));
    }

    /// <summary>
    /// The guard is about the storage tree, not about where the platform put it.
    ///
    /// Walking from the volume root made it reject the host's own layout: macOS spells the temp
    /// directory through a `/var -> /private/var` symlink, so a scope created under it tripped on an
    /// ancestor the user never chose. That turned the whole `osx-arm64` leg red — nineteen
    /// `ConfigurationDocumentTests` failed on `/var` before reaching any product code.
    ///
    /// Driven through the injection seam rather than a real symlink: an unprivileged Windows
    /// account cannot create one, so a test that needed a real link would skip here and quietly
    /// prove nothing about the case that broke CI.
    /// </summary>
    [Fact]
    public void ALinkAboveTheScopeIsThePlatformsBusinessAndALinkInsideItIsNot()
    {
        var root = Directory.CreateTempSubdirectory("tinadec-scope-links-").FullName;
        try
        {
            var inside = Path.Combine(root, "state");
            var linked = Path.Combine(root, "linked");

            // The layout macOS actually has: a link on one of the path segments *between* the volume
            // root and the scope, not on the volume root itself. `/var -> /private/var` is exactly
            // this shape, and it is why starting the walk above the scope rejected the host's layout.
            var volumeRoot = Path.GetPathRoot(root)!;
            var firstSegment = Path.GetRelativePath(volumeRoot, root)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            var platformLink = Path.Combine(volumeRoot, firstSegment);
            var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { platformLink, linked };
            Func<string, bool> isLink = path => links.Contains(Path.TrimEndingDirectorySeparator(path));

            Directory.CreateDirectory(inside);

            // Passes even though an ancestor is a link. This is the assertion the old walk failed.
            StorageScopePaths.RejectLinks(root, null, isLink);
            StorageScopePaths.RejectLinks(root, Path.Combine(root, "state", "host.lock"), isLink);

            // Inside the scope a link is still refused, which is the claim the guard makes.
            var rejected = Assert.Throws<InvalidOperationException>(
                () => StorageScopePaths.RejectLinks(root, Path.Combine(linked, "host.lock"), isLink));
            Assert.Contains("symbolic links", rejected.Message, StringComparison.OrdinalIgnoreCase);

            // A scope that is itself the link is refused too, so this did not simply stop checking.
            var scopeThroughLink = Assert.Throws<InvalidOperationException>(
                () => StorageScopePaths.RejectLinks(linked, null, isLink));
            Assert.Contains("symbolic links", scopeThroughLink.Message, StringComparison.OrdinalIgnoreCase);

            // And an escape is still an escape.
            Assert.Throws<InvalidOperationException>(
                () => StorageScopePaths.RejectLinks(root, Path.Combine(root, "..", "elsewhere"), isLink));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The same rule against the real file system, wherever the host can actually make a link. On
    /// Windows without the privilege it is skipped rather than silently green — the seam test above
    /// is what covers the rule here.
    /// </summary>
    [Fact]
    public void RealLinksInsideScopeAreRefusedOnEveryHostThatCanCreateThem()
    {
        var root = Directory.CreateTempSubdirectory("tinadec-real-links-").FullName;
        try
        {
            var inside = Path.Combine(root, "state");
            Directory.CreateDirectory(inside);
            var linked = Path.Combine(root, "linked");
            try
            {
                Directory.CreateSymbolicLink(linked, inside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            StorageScopePaths.RejectLinks(root, Path.Combine(root, "state", "host.lock"));
            Assert.Throws<InvalidOperationException>(
                () => StorageScopePaths.RejectLinks(root, Path.Combine(linked, "host.lock"), null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyHostKeepsItsExplicitDataRootAndDatabaseWithoutChangingRealUserConfiguration()
    {
        var before = ApiTestStorage.ReadRealUserConfigurationHashes();
        var root = ApiTestStorage.CreateRoot("legacy-host");
        // Use the platform's declared spelling, including macOS /var ->
        // /private/var, then verify the actual service options are canonical.
        var declaredRoot = Path.Combine(Path.GetTempPath(), Path.GetRelativePath(
            WorkspacePathSpelling.Canonical(Path.GetTempPath()), root));
        try
        {
            await using (var factory = new LegacyFactory(declaredRoot))
            {
                using var client = factory.CreateClient();
                Assert.Equal("Testing", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
                Assert.False(factory.Services.GetRequiredService<IConfiguration>().GetValue<bool>("TinadecStorage:Enabled"));
                var options = factory.Services.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value;
                Assert.Equal(Path.Combine(root, "explicit-data"), options.DataRoot);
                Assert.Equal(Path.Combine(root, "explicit-database.db"), options.Sqlite.DatabasePath);
                Assert.Equal(options.DataRoot, factory.Services.GetRequiredService<StoragePaths>().Root);
                Assert.Equal(Path.Combine(root, "workspace"), factory.Services.GetRequiredService<IConfiguration>()["TinadecTools:DefaultWorkspaceRoot"]);
                Assert.Null(factory.Services.GetService<IScopeConfigurationDocuments>());
                Assert.True(File.Exists(options.Sqlite.DatabasePath));
                Assert.False(File.Exists(Path.Combine(root, "config", "agents.toml")));
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/core.json")).StatusCode);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            AssertConfigurationUnchanged(before);
        }
    }

    [Fact]
    public async Task ManagedHostBindsTheTemporaryRootBeforeProgramBootstrapsConfiguration()
    {
        var before = ApiTestStorage.ReadRealUserConfigurationHashes();
        var root = ApiTestStorage.CreateRoot("managed-host");
        try
        {
            await using (var factory = new ManagedFactory(root))
            {
                using var client = factory.CreateClient();
                Assert.Equal("StorageTesting", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
                Assert.True(factory.Services.GetRequiredService<IConfiguration>().GetValue<bool>("TinadecStorage:Enabled"));
                var scope = factory.Services.GetRequiredService<IScopeStorageLocations>();
                Assert.Equal(root, scope.Root);
                Assert.Equal(Path.Combine(root, "data"), factory.Services.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value.DataRoot);
                Assert.NotNull(factory.Services.GetService<IScopeConfigurationDocuments>());
                Assert.True(File.Exists(Path.Combine(root, "config", "agents.toml")));
                Assert.False(File.Exists(Path.Combine(ApiTestStorage.Home, "config", "agents.toml")));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            AssertConfigurationUnchanged(before);
        }
    }

    [Fact]
    public void ManagedHostWithoutAnExplicitRootFailsBeforeCreatingConfiguration()
    {
        var before = ApiTestStorage.ReadRealUserConfigurationHashes();
        using var factory = new ManagedFactory(null);
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("explicit temporary UserRoot", failure.Message);
        Assert.False(File.Exists(Path.Combine(ApiTestStorage.Home, "config", "agents.toml")));
        AssertConfigurationUnchanged(before);
    }

    [Fact]
    public void ARealUserRootCannotBeBoundByAnEarlyFactoryOverride()
    {
        var before = ApiTestStorage.ReadRealUserConfigurationHashes();
        using var factory = new UnsafeFactory();
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("early storage isolation settings", failure.Message);
        AssertConfigurationUnchanged(before);
    }

    [Fact]
    public void ManagedRootsOutsideThisProcessAreRejectedWithoutWritingAnything()
    {
        var before = ApiTestStorage.ReadRealUserConfigurationHashes();
        var realRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tinadec");
        Assert.Throws<InvalidOperationException>(() => ApiTestStorage.RequireManagedRoot(realRoot));
        Assert.Throws<InvalidOperationException>(() => ApiTestStorage.RequireManagedRoot(Path.Combine(Path.GetTempPath(), "unowned-test-root")));
        Assert.Throws<InvalidOperationException>(() => ApiTestStorage.RequireManagedRoot("relative-user-root"));
        AssertConfigurationUnchanged(before);
    }

    private static void AssertConfigurationUnchanged(IReadOnlyDictionary<string, string> before) =>
        Assert.Equal(before.OrderBy(pair => pair.Key), ApiTestStorage.ReadRealUserConfigurationHashes().OrderBy(pair => pair.Key));

    private sealed class LegacyFactory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "explicit-data"),
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "explicit-database.db"),
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(root, "workspace"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
    }

    private sealed class ManagedFactory(string? root) : IsolatedApiFactory
    {
        protected override bool UsesManagedStorage => true;
        protected override string? ManagedUserRoot => root;
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Warning" }));
    }

    private sealed class UnsafeFactory : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder.UseSetting("TinadecStorage:UserRoot",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tinadec"));
    }
}
