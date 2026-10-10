using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Models;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Tools;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Api.Tests;

public sealed class ConfigurationDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-config-tests", Guid.NewGuid().ToString("N"));
    private static readonly TenantContext Actor = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");

    [Fact]
    public async Task UnchangedProjectionReusesValidationButStillChecksBytesAndRejectsChangedSource()
    {
        var observer = new CountingValidator();
        await using var services = Build(_root, new MemoryContentStore(), observer: observer);
        await InitializeAsync(services);
        var factory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var count = observer.Count;
        for (var i = 0; i < 8; i++)
        {
            await using var db = await factory.CreateDbContextAsync();
            Assert.Empty(await db.AgentDefinitions.ToListAsync());
        }
        Assert.Equal(count, observer.Count);

        // Same-length edits with unchanged timestamps must invalidate the
        // projection; file metadata cannot substitute for source bytes.
        var path = Path.Combine(_root, "config", "agents.toml");
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text + "\n# source-one\n");
        await using (var db = await factory.CreateDbContextAsync()) { }
        var firstEditCount = observer.Count;
        var stamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, text + "\n# source-two\n");
        File.SetLastWriteTimeUtc(path, stamp);
        await using (var db = await factory.CreateDbContextAsync()) { }
        Assert.Equal(firstEditCount + 1, observer.Count);

        await File.WriteAllTextAsync(path, text + "\n# reject-fixture\n");
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => factory.CreateDbContextAsync());
            Assert.Contains(error.Diagnostics, d => d.Code == "fixture_invalid");
        }
        Assert.Equal(firstEditCount + 3, observer.Count);
        Assert.EndsWith("# reject-fixture\n", await File.ReadAllTextAsync(path));

        await File.WriteAllTextAsync(path, text);
        await using (var db = await factory.CreateDbContextAsync()) { }
        var beforeExplicitRead = observer.Count;
        await services.GetRequiredService<IScopeConfigurationDocuments>().ReadAsync("agents");
        Assert.Equal(beforeExplicitRead + 1, observer.Count);
    }

    [Fact]
    public async Task ProjectionValidationCacheIsScopeLocalAndInvalidatedByCommittedWrites()
    {
        var observer = new CountingValidator();
        await using var services = Build(_root, new MemoryContentStore(), observer: observer);
        await InitializeAsync(services);
        var factory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AgentDefinitions.Add(new AgentDefinitionRecord { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId,
                Slug = "cache-probe", DisplayName = "Cache probe", SourceKind = "custom", SourceKey = "cache-probe", Status = "draft", Revision = 1 });
            await db.SaveChangesAsync();
        }
        var beforeRead = observer.Count;
        await using (var db = await factory.CreateDbContextAsync()) Assert.Single(await db.AgentDefinitions.ToListAsync());
        Assert.Equal(beforeRead + 1, observer.Count);

        var otherRoot = Path.Combine(_root, "other-scope");
        await using var other = Build(otherRoot, new MemoryContentStore(), observer: observer);
        var beforeOther = observer.Count;
        await InitializeAsync(other);
        Assert.True(observer.Count > beforeOther);
        await using var otherDb = await other.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        Assert.Empty(await otherDb.AgentDefinitions.ToListAsync());
    }

    private sealed class CountingValidator : IConfigurationDocumentValidator
    {
        public string DocumentId => "agents";
        public int Count { get; private set; }
        public IReadOnlyList<ConfigurationDiagnostic> Validate(string text)
        {
            Count++;
            return text.Contains("# reject-fixture", StringComparison.Ordinal) ? [new("fixture_invalid", "Rejected in isolated fixture.")] : [];
        }
    }

    [Fact]
    public async Task NativeBindingsRoundTripGuiWritesManualModesAndFrozenHistory()
    {
        await using var services = Build(_root, new MemoryContentStore(), validateToolSettings: true);
        await InitializeAsync(services);
        var factory = services.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>();
        var selected = Guid.NewGuid(); var alternate = Guid.NewGuid(); var agentId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Settings.Add(new() { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, ScopeKey = "shared", Revision = 1,
                SettingsJson = JsonSerializer.Serialize(new { mcp = new { server_resource_ids = new[] { selected } }, skills = new { resource_ids = new[] { selected } },
                    read = new { max_file_bytes = 1000 }, search = new { timeout_ms = 123, rg_path = "/selected/rg" } }) });
            await db.SaveChangesAsync();
        }
        var store = new ToolSettingsStore(factory, services.GetRequiredService<ITenantContextAccessor>(), services);
        var inherited = await store.GetAsync(agentId);
        Assert.Equal(selected, inherited.EffectiveSettings.GetProperty("mcp").GetProperty("server_resource_ids")[0].GetGuid());
        Assert.Equal(123, inherited.EffectiveSettings.GetProperty("search").GetProperty("timeout_ms").GetInt32());
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>(); var before = await documents.ReadAsync("tools");
        await documents.SaveIfMatchAsync("tools", "# binding-comment survives GUI CAS\n" + before.Text, before.ContentHash);
        var saved = await store.SaveAsync(agentId, JsonSerializer.Deserialize<JsonElement>(
            "{\"mcp\":{\"server_resource_ids\":null},\"skills\":{\"resource_ids\":null},\"write\":{\"max_file_bytes\":null},\"search\":{\"timeout_ms\":null,\"rg_path\":null}}"), 0);
        var file = await documents.ReadAsync("tools");
        Assert.DoesNotContain("__tinadec_null", file.Text); Assert.Contains("# binding-comment survives GUI CAS", file.Text);
        var model = TomlSerializer.Deserialize<TomlTable>(file.Text)!;
        var scopeKey = "agent:" + agentId.ToString("N"); var native = NativeSettings(model, scopeKey);
        Assert.Equal("all", ((TomlTable)((TomlTable)native["mcp"])["binding"])["mode"]);
        Assert.Equal("all", ((TomlTable)((TomlTable)native["skills"])["binding"])["mode"]);
        Assert.Equal("unlimited", ((TomlTable)((TomlTable)native["write"])["max_file_bytes"])["mode"]);
        Assert.Equal("outer_deadline", ((TomlTable)((TomlTable)native["search"])["timeout_ms"])["mode"]);
        Assert.Equal("host_search", ((TomlTable)((TomlTable)native["search"])["rg_path"])["mode"]);
        Assert.Equal(JsonValueKind.Null, saved.EffectiveSettings.GetProperty("mcp").GetProperty("server_resource_ids").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.EffectiveSettings.GetProperty("search").GetProperty("timeout_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.EffectiveSettings.GetProperty("search").GetProperty("rg_path").ValueKind);
        var history = ((TomlTableArray)model["tool_settings_versions"]).Single()["settings"] as string;
        Assert.Contains("null", history!); Assert.DoesNotContain("binding", history!);

        foreach (var mode in new[] { "none", "selected", "inherit", "all" })
        {
            file = await documents.ReadAsync("tools"); model = TomlSerializer.Deserialize<TomlTable>(file.Text)!; native = NativeSettings(model, scopeKey);
            foreach (var section in new[] { "mcp", "skills" })
            {
                var binding = new TomlTable { ["mode"] = mode };
                if (mode == "selected") { var ids = new TomlArray(); ids.Add(alternate.ToString()); binding["ids"] = ids; }
                ((TomlTable)native[section])["binding"] = binding;
            }
            await documents.SaveIfMatchAsync("tools", "# binding-comment survives GUI CAS\n" + TomlSerializer.Serialize(model), file.ContentHash);
            await services.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync();
            var read = await store.GetAsync(agentId);
            foreach (var (section, field) in new[] { ("mcp", "server_resource_ids"), ("skills", "resource_ids") })
            {
                var actual = read.EffectiveSettings.GetProperty(section).GetProperty(field);
                if (mode == "all") Assert.Equal(JsonValueKind.Null, actual.ValueKind);
                else if (mode == "none") Assert.Empty(actual.EnumerateArray());
                else Assert.Equal(mode == "inherit" ? selected : alternate, actual[0].GetGuid());
                if (mode == "inherit") Assert.False(read.Settings.GetProperty(section).TryGetProperty(field, out _));
            }
            await using var frozen = await factory.CreateDbContextAsync();
            Assert.Equal(history, (await frozen.SettingVersions.SingleAsync()).SettingsJson);
        }
        // Omission is also inheritance; removing binding must not become none/all.
        file = await documents.ReadAsync("tools"); model = TomlSerializer.Deserialize<TomlTable>(file.Text)!;
        ((TomlTable)NativeSettings(model, scopeKey)["mcp"]).Remove("binding");
        await documents.SaveIfMatchAsync("tools", TomlSerializer.Serialize(model), file.ContentHash);
        Assert.Equal(selected, (await store.GetAsync(agentId)).EffectiveSettings.GetProperty("mcp").GetProperty("server_resource_ids")[0].GetGuid());
        var limit = await Assert.ThrowsAsync<ToolSettingsException>(() => store.SaveAsync(agentId,
            JsonSerializer.Deserialize<JsonElement>("{\"read\":{\"max_file_bytes\":null}}"), saved.Revision));
        Assert.Equal("invalid_tool_settings", limit.Code);
    }

    [Fact]
    public async Task NativeBindingErrorsAndNullSentinelsCannotChangeAuthoritativeBytes()
    {
        await using var services = Build(_root, new MemoryContentStore(), validateToolSettings: true); await InitializeAsync(services);
        var factory = services.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>();
        var store = new ToolSettingsStore(factory, services.GetRequiredService<ITenantContextAccessor>(), services);
        await store.SaveAsync(null, JsonSerializer.Deserialize<JsonElement>("{\"mcp\":{\"server_resource_ids\":null}}"), 0);
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>(); var before = await documents.ReadAsync("tools");
        var selected = Guid.NewGuid();
        var invalid = new List<TomlTable> { new() { ["mode"] = "unknown" }, new() { ["mode"] = "selected" },
            new() { ["__tinadec_null"] = true } };
        foreach (var mode in new[] { "inherit", "all", "none", "selected" })
            invalid.Add(new() { ["mode"] = mode, ["ids"] = new TomlArray() });
        var duplicate = new TomlArray(); duplicate.Add(selected.ToString()); duplicate.Add(selected.ToString().ToUpperInvariant());
        invalid.Add(new() { ["mode"] = "selected", ["ids"] = duplicate });
        var badId = new TomlArray(); badId.Add(Guid.Empty.ToString()); invalid.Add(new() { ["mode"] = "selected", ["ids"] = badId });
        foreach (var binding in invalid)
        {
            var model = TomlSerializer.Deserialize<TomlTable>(before.Text)!;
            ((TomlTable)NativeSettings(model, "shared")["mcp"])["binding"] = binding;
            await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("tools", TomlSerializer.Serialize(model), before.ContentHash));
            Assert.Equal(before.ContentHash, (await documents.ReadAsync("tools")).ContentHash);
        }
        var legacy = TomlSerializer.Deserialize<TomlTable>(before.Text)!;
        NativeSettings(legacy, "shared")["mcp"] = new TomlTable { ["server_resource_ids"] = new TomlArray() };
        await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("tools", TomlSerializer.Serialize(legacy), before.ContentHash));
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("tools")).ContentHash);
        await using var invalidProjection = await factory.CreateDbContextAsync();
        (await invalidProjection.Settings.SingleAsync()).SettingsJson = "{\"mcp\":{\"server_resource_ids\":[\"" + Guid.Empty + "\"]}}";
        var projectionError = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => invalidProjection.SaveChangesAsync());
        Assert.Equal("configuration_invalid", projectionError.Code);
        Assert.Contains(projectionError.Diagnostics, diagnostic => diagnostic.Message.Contains("nonzero", StringComparison.Ordinal));
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("tools")).ContentHash);
    }

    [Fact]
    public void NativeJsonValuesUseOmittedInheritanceAndRejectAmbiguousArrayNulls()
    {
        var native = (TomlTable)ConfigurationTomlValues.FromJsonElement(JsonSerializer.Deserialize<JsonElement>("{\"inherit\":null,\"enabled\":false,\"nested\":{\"inherit\":null}}"))!;
        Assert.False(native.ContainsKey("inherit")); Assert.False(((TomlTable)native["nested"]).ContainsKey("inherit"));
        Assert.False((bool)native["enabled"]);
        Assert.Throws<InvalidDataException>(() => ConfigurationTomlValues.FromJsonElement(JsonSerializer.Deserialize<JsonElement>("[null]")));
        Assert.Throws<InvalidDataException>(() => ConfigurationTomlValues.ToJsonElement(new TomlTable { ["__tinadec_null"] = true }));
    }

    private static TomlTable NativeSettings(TomlTable model, string scope) => (TomlTable)((TomlTableArray)model["tool_settings"])
        .Single(row => (string)row["scope_key"] == scope)["settings"];

    [Fact]
    public async Task PublishedProjectMissingFileIsRejectedWithoutLocalStateOrSqlHistory()
    {
        await using (var original = Build(_root, new MemoryContentStore())) await InitializeAsync(original);
        File.WriteAllText(Path.Combine(_root, "project.toml"), "schema_version = 1\nproject_id = \"" + Guid.NewGuid() + "\"\n");
        File.Delete(Path.Combine(_root, "config", "tools.toml"));
        Directory.Delete(Path.Combine(_root, "state"), recursive: true);
        await using var copied = Build(_root, new MemoryContentStore());
        var documents = copied.GetRequiredService<IScopeConfigurationDocuments>();
        Assert.Contains((await documents.ReadAsync("tools")).Diagnostics, d => d.Code == "configuration_missing");
        var missing = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => copied.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>().CreateDbContextAsync());
        Assert.Equal("configuration_missing", missing.Code);
        var scope = copied.GetRequiredService<IScopeStorageLocations>();
        Assert.Equal("configuration_missing", Assert.Throws<ConfigurationDocumentException>(() => ScopeConfigurationInitialization.Ensure(scope, "runtime", () => "# forbidden reseed\n")).Code);
        Assert.False(File.Exists(Path.Combine(scope.Config, "runtime.toml")));
    }

    [Fact]
    public async Task UserStorageIdentityIsStableConcurrentAndNewRootsHaveIndependentSchemas()
    {
        var first = new StorageScopeDescriptor("user", "user", Path.Combine(_root, "first-user"));
        var identities = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => UserStorageIdentity.ReadOrCreate(first))));
        Assert.All(identities, identity => Assert.Equal(identities[0], identity));
        Assert.Equal(identities[0], UserStorageIdentity.ReadOrCreate(first));
        var second = new StorageScopeDescriptor("user", "user", Path.Combine(_root, "second-user"));
        Assert.NotEqual(identities[0], UserStorageIdentity.ReadOrCreate(second));
        foreach (var scope in new[] { first, second })
        {
            Directory.CreateDirectory(scope.Config); File.WriteAllText(Path.Combine(scope.Config, "runtime.toml"), "# existing runtime\n");
            var config = new ConfigurationManager(); config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TinadecStorage:UserRoot"] = scope.Root, ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(_root, "workspace") });
            StorageHostBootstrap.Configure(config);
            Assert.Equal("tinadec_" + UserStorageIdentity.ReadOrCreate(scope).ToString("N"), config["TinadecPersistence:PostgreSql:Schema"]);
        }
        File.WriteAllText(Path.Combine(first.State, "storage-identity.toml"), "version = 1\nstorage_identity = \"invalid\"\n");
        Assert.Equal("storage_identity_invalid", Assert.Throws<ConfigurationDocumentException>(() => UserStorageIdentity.ReadOrCreate(first)).Code);
        File.Delete(Path.Combine(first.State, "storage-identity.toml"));
        Assert.Equal("storage_identity_missing", Assert.Throws<ConfigurationDocumentException>(() => UserStorageIdentity.ReadOrCreate(first)).Code);
    }

    [Fact]
    public void HostBootstrapCannotReseedDeletedRuntimeStorageOrLoggingFiles()
    {
        var scope = new StorageScopeDescriptor("user", "user", Path.Combine(_root, "bootstrap"));
        Directory.CreateDirectory(scope.Config);
        File.WriteAllText(Path.Combine(scope.Config, "runtime.toml"), "# existing installed runtime\n");
        StorageScopeInitializer.EnsureUser(scope);
        File.Delete(Path.Combine(scope.Config, "runtime.toml"));
        Assert.Equal("configuration_missing", Assert.Throws<ConfigurationDocumentException>(() => StorageScopeInitializer.EnsureUser(scope)).Code);
        File.WriteAllText(Path.Combine(scope.Config, "runtime.toml"), "# restored runtime\n");
        using (var logger = new ScopeDiagnosticLoggerProvider(scope)) { }
        File.Delete(Path.Combine(scope.Config, "logging.toml"));
        Assert.Equal("configuration_missing", Assert.Throws<ConfigurationDocumentException>(() => new ScopeDiagnosticLoggerProvider(scope)).Code);
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["TinadecStorage:UserRoot"] = scope.Root,
            ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(_root, "workspace") });
        StorageHostBootstrap.Configure(config);
        File.Delete(Path.Combine(scope.Config, "storage.toml"));
        Assert.Equal("configuration_missing", Assert.Throws<ConfigurationDocumentException>(() => StorageHostBootstrap.Configure(config)).Code);
    }

    [Fact]
    public async Task MissingEstablishedFileCannotBeRegeneratedFromSqlAfterRestart()
    {
        var content = new MemoryContentStore();
        await using (var first = Build(_root, content))
        {
            await InitializeAsync(first);
            var factory = first.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Settings.Add(new() { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId,
                    ScopeKey = "shared", Revision = 1, SettingsJson = "{\"label\":\"sql-history\"}" });
                await db.SaveChangesAsync();
            }
            var documents = first.GetRequiredService<IScopeConfigurationDocuments>(); var saved = await documents.ReadAsync("tools");
            File.Delete(saved.Path);
            Assert.Contains((await documents.ReadAsync("tools")).Diagnostics, d => d.Code == "configuration_missing");
            var missing = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => factory.CreateDbContextAsync());
            Assert.True(missing.Code == "configuration_missing", missing.Code + ": " + missing.Message + " / " + string.Join("; ", missing.Diagnostics));
            Assert.False(File.Exists(saved.Path));
        }
        await using var restarted = Build(_root, content);
        var repeated = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => restarted.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>().CreateDbContextAsync());
        Assert.Contains(repeated.Diagnostics, d => d.Code == "configuration_missing");
        var store = restarted.GetRequiredService<IScopeConfigurationDocuments>();
        await store.SaveIfMatchAsync("tools", "schema_version = 1\nversion = 1\n", "");
        await using var rebuilt = await restarted.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>().CreateDbContextAsync();
        Assert.Empty(await rebuilt.Settings.ToListAsync());
    }

    [Fact]
    public async Task MissingStorageConfigurationBlocksCompilation()
    {
        await using var services = Build(_root, new MemoryContentStore()); await InitializeAsync(services);
        File.Delete((await services.GetRequiredService<IScopeConfigurationDocuments>().ReadAsync("storage")).Path);
        var missing = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => services.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync());
        Assert.Equal("configuration_missing", missing.Code);
    }

    [Fact]
    public async Task ConditionalSaveRejectsStaleHashAndInvalidTomlWithoutChangingBytes()
    {
        Directory.CreateDirectory(_root);
        var store = new ScopeConfigurationDocuments(new StorageScopeDescriptor("user", "user", _root), []);
        var initial = await store.SaveIfMatchAsync("tools", "# keep this comment\nschema_version = 1\n", "");
        var next = await store.SaveIfMatchAsync("tools", initial.Text + "version = 2\n", initial.ContentHash);
        Assert.Contains("# keep this comment", next.Text);
        var conflict = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => store.SaveIfMatchAsync("tools", initial.Text, initial.ContentHash));
        Assert.Equal("configuration_conflict", conflict.Code);
        var invalid = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => store.SaveIfMatchAsync("tools", "invalid = [", next.ContentHash));
        Assert.Equal("configuration_invalid", invalid.Code);
        Assert.Contains(invalid.Diagnostics, d => d.Line is > 0 && d.Column is > 0);
        Assert.Equal(next.Text, (await store.ReadAsync("tools")).Text);
    }

    [Fact]
    public async Task GuiWritesTomlAndManualEditRebuildsProjectionWithCommentsPreserved()
    {
        await using var services = Build(_root, new MemoryContentStore());
        await InitializeAsync(services);
        var factory = services.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>();
        var id = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Settings.Add(new() { Id = id, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, ScopeKey = "shared", Revision = 1, SettingsJson = "{\"label\":\"first\"}" });
            await db.SaveChangesAsync();
        }
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var before = await documents.ReadAsync("tools");
        Assert.Contains("first", before.Text);
        await documents.SaveIfMatchAsync("tools", "# preserved by GUI\n" + before.Text.Replace("first", "from-file"), before.ContentHash);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.Settings.SingleAsync(x => x.Id == id);
            Assert.Contains("from-file", row.SettingsJson);
            row.SettingsJson = "{\"label\":\"from-gui\"}"; row.Revision++;
            await db.SaveChangesAsync();
        }
        var after = await documents.ReadAsync("tools");
        Assert.Contains("# preserved by GUI", after.Text);
        Assert.Contains("from-gui", after.Text);
    }

    [Fact]
    public async Task AnEditCreatedBeforeExternalFileChangeCannotOverwriteIt()
    {
        await using var services = Build(_root, new MemoryContentStore());
        await InitializeAsync(services);
        var factory = services.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>();
        await using var stale = await factory.CreateDbContextAsync();
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var before = await documents.ReadAsync("tools");
        await documents.SaveIfMatchAsync("tools", "# later external edit\n" + before.Text, before.ContentHash);
        stale.Settings.Add(new() { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, Revision = 1, SettingsJson = "{}" });
        var conflict = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => stale.SaveChangesAsync());
        Assert.Equal("configuration_conflict", conflict.Code);
        Assert.Contains("# later external edit", (await documents.ReadAsync("tools")).Text);
    }

    [Fact]
    public async Task CopiedConfigurationMaterializesProviderContentInIndependentProjectDatabase()
    {
        var sourceContent = new MemoryContentStore();
        var providerId = Guid.NewGuid(); var versionId = Guid.NewGuid();
        const string body = "{\"base_url\":\"https://example.invalid\",\"model\":\"test\"}";
        await using (var user = Build(_root, sourceContent))
        {
            await InitializeAsync(user);
            var reference = await sourceContent.PutAsync(new(Actor.TenantId, Actor.WorkspaceId, "model-config", "application/json", new MemoryStream(Encoding.UTF8.GetBytes(body))));
            await using var db = await user.GetRequiredService<IDbContextFactory<ModelControlDbContext>>().CreateDbContextAsync();
            db.Providers.Add(new() { Id = providerId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, Driver = "openai", DisplayName = "test", Revision = 1, CurrentVersionId = versionId });
            db.ProviderVersions.Add(new() { Id = versionId, ProviderId = providerId, Version = 1, ContentReference = reference.Value, ContentHash = reference.Sha256, ContentLength = reference.Length });
            await db.SaveChangesAsync();
        }
        var projectRoot = Path.Combine(_root, "project"); Directory.CreateDirectory(Path.Combine(projectRoot, "config"));
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "config"), "*.toml"))
            File.Copy(file, Path.Combine(projectRoot, "config", Path.GetFileName(file)));
        var projectContent = new MemoryContentStore();
        await using var project = Build(projectRoot, projectContent);
        await InitializeAsync(project);
        await using var projected = await project.GetRequiredService<IDbContextFactory<ModelControlDbContext>>().CreateDbContextAsync();
        var version = await projected.ProviderVersions.SingleAsync(x => x.Id == versionId);
        await using var stream = await projectContent.OpenReadAsync(new(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"));
        Assert.Equal(body, await new StreamReader(stream).ReadToEndAsync());
        Assert.Equal(providerId, (await projected.Providers.SingleAsync()).Id);
    }

    [Fact]
    public async Task CompileDigestRejectsAnEditAfterAdmissionStarted()
    {
        await using var services = Build(_root, new MemoryContentStore());
        await InitializeAsync(services);
        var coordinator = services.GetRequiredService<IConfigurationProjectionCoordinator>();
        var snapshot = await coordinator.CompileAsync();
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var document = await documents.ReadAsync("tools");
        await documents.SaveIfMatchAsync("tools", "# changed during admission\n" + document.Text, document.ContentHash);
        var error = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => coordinator.VerifyAsync(snapshot));
        Assert.Equal("configuration_changed_during_admission", error.Code);
    }

    [Fact]
    public async Task SaveValidatesFieldTypesBeforeCommittingTheFile()
    {
        await using var services = Build(_root, new MemoryContentStore());
        await InitializeAsync(services);
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var before = await documents.ReadAsync("tools");
        var text = "schema_version = 1\n[[tool_settings]]\nid = \"not-a-guid\"\n";
        var invalid = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("tools", text, before.ContentHash));
        Assert.Equal("configuration_invalid", invalid.Code);
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("tools")).ContentHash);
    }

    [Fact]
    public async Task AgentPackEnvelopeBytesAndSemanticDigestRebuildInAnIndependentScope()
    {
        var source = new MemoryContentStore(); var versionId = Guid.NewGuid();
        const string envelope = "{\"digest\":\"semantic-digest\",\"manifest\":{\"pack_id\":\"test-pack\"}}";
        ContentReference reference;
        await using (var user = Build(_root, source))
        {
            await InitializeAsync(user);
            reference = await source.PutAsync(new(Actor.TenantId, Actor.WorkspaceId, "agent-pack-manifest", "application/json", new MemoryStream(Encoding.UTF8.GetBytes(envelope))));
            await using var db = await user.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
            db.AgentPackVersions.Add(new() { Id = versionId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, InstallationId = Guid.NewGuid(), PackVersion = "1.0.0", ManifestContentReference = reference.Value, ManifestHash = "semantic-digest", ManifestLength = reference.Length });
            await db.SaveChangesAsync();
            await user.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync();
            Assert.Contains(envelope.Replace("\"", "\\\""), (await user.GetRequiredService<IScopeConfigurationDocuments>().ReadAsync("agents")).Text);
        }
        var projectRoot = Path.Combine(_root, "project"); Directory.CreateDirectory(Path.Combine(projectRoot, "config"));
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "config"), "*.toml")) File.Copy(file, Path.Combine(projectRoot, "config", Path.GetFileName(file)));
        var projectContent = new MemoryContentStore(); await using var project = Build(projectRoot, projectContent); await InitializeAsync(project);
        await using var projected = await project.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        var pack = await projected.AgentPackVersions.SingleAsync();
        Assert.Equal("semantic-digest", pack.ManifestHash); Assert.Equal(reference.Value, pack.ManifestContentReference);
        await using var stream = await projectContent.OpenReadAsync(reference);
        Assert.Equal(envelope, await new StreamReader(stream).ReadToEndAsync());
    }

    [Fact]
    public async Task StorageAndLoggingValidateAndChangedBackendBlocksAdmissionUntilApplied()
    {
        await using var services = Build(_root, new MemoryContentStore()); await InitializeAsync(services);
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var storage = await documents.ReadAsync("storage"); var logging = await documents.ReadAsync("logging");
        Assert.Contains("storage", documents.DocumentIds); Assert.Contains("logging", documents.DocumentIds);
        await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("storage", "[storage]\nbackend = \"postgresql\"\npostgres_connection_reference = \"Host=example;Password=literal\"\n", storage.ContentHash));
        await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("logging", "[logging]\nrotation_bytes = 1024\ntotal_bytes = 8192\n", logging.ContentHash));
        await documents.SaveIfMatchAsync("storage", "[storage]\nbackend = \"postgresql\"\npostgres_connection_reference = \"test-binding\"\n", storage.ContentHash);
        var mismatch = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => services.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync());
        Assert.Equal("configuration_restart_required", mismatch.Code);
        Assert.True(File.Exists(Path.Combine(_root, "state", ".configuration-write.lock")));
        Assert.False(File.Exists(Path.Combine(_root, "config", ".configuration-write.lock")));
    }

    [Fact]
    public async Task EmptyConfigurationCannotEraseTheFileProjection()
    {
        await using var services = Build(_root, new MemoryContentStore()); await InitializeAsync(services);
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>(); var before = await documents.ReadAsync("tools");
        await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("tools", "", before.ContentHash));
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("tools")).ContentHash);
    }

    [Fact]
    public async Task DuplicateLogicalIdentityIsRejectedBeforeTheFileCommit()
    {
        await using var services = Build(_root, new MemoryContentStore()); await InitializeAsync(services);
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>(); var before = await documents.ReadAsync("tools");
        var row = "[[tool_settings]]\ntenant_id = \"" + Actor.TenantId + "\"\nworkspace_id = \"" + Actor.WorkspaceId + "\"\nscope_key = \"shared\"\n";
        var duplicate = "schema_version = 1\n" + row + "id = \"" + Guid.NewGuid() + "\"\n" + row + "id = \"" + Guid.NewGuid() + "\"\n";
        var rejected = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("tools", duplicate, before.ContentHash));
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "configuration_unique");
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("tools")).ContentHash);
    }

    [Fact]
    public async Task RetiredFileVersionRemainsHistoryAndCannotSupplyAnActiveDefault()
    {
        await using var services = Build(_root, new MemoryContentStore()); await InitializeAsync(services);
        var versionId = Guid.NewGuid(); var factory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ModeVersions.Add(new() { Id = versionId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SnapshotJson = "{}", Version = 1 });
            db.WorkspaceDefaults.Add(new() { TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, DefaultModeVersionId = versionId });
            await db.SaveChangesAsync();
        }
        await services.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync();
        var document = await services.GetRequiredService<IScopeConfigurationDocuments>().ReadAsync("agents");
        var model = TomlSerializer.Deserialize<TomlTable>(document.Text)!; model.Remove("mode_versions");
        await File.WriteAllTextAsync(document.Path, TomlSerializer.Serialize(model));
        var error = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => services.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync());
        Assert.Equal("configuration_source_reference", error.Code);
        await using var historical = await factory.CreateDbContextAsync();
        Assert.Equal("retired", (await historical.ModeVersions.SingleAsync()).Status);
        Assert.Equal("{}", (await historical.ModeVersions.SingleAsync()).SnapshotJson);
    }

    private static ServiceProvider Build(string root, MemoryContentStore content, bool validateToolSettings = false, IConfigurationDocumentValidator? observer = null)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var storage = Path.Combine(root, "config", "storage.toml");
        var logging = Path.Combine(root, "config", "logging.toml");
        if (!File.Exists(storage)) File.WriteAllText(storage, "[storage]\nbackend = \"sqlite\"\n");
        if (!File.Exists(logging)) File.WriteAllText(logging, "[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");
        var services = new ServiceCollection();
        services.AddSingleton<IScopeStorageLocations>(new StorageScopeDescriptor(root == Path.GetDirectoryName(root) ? "user" : root, "project", root));
        services.AddSingleton<ITenantContextAccessor>(new TenantAccessor());
        services.AddSingleton<IContentStore>(content);
        var connection = "Data Source=" + Path.Combine(root, "test.db");
        services.AddDbContextFactory<ToolsSettingsDbContext>(options => options.UseSqlite(connection));
        services.AddDbContextFactory<ModelControlDbContext>(options => options.UseSqlite(connection));
        services.AddDbContextFactory<AgentConfigurationDbContext>(options => options.UseSqlite(connection));
        if (validateToolSettings) services.AddSingleton<IConfigurationDocumentValidator, ToolSettingsDocumentValidator>();
        if (observer is not null) services.AddSingleton(observer);
        services.AddTinadecConfigurationFiles();
        return services.BuildServiceProvider();
    }

    private static async Task InitializeAsync(ServiceProvider services)
    {
        await using (var tools = await services.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>().CreateDbContextAsync())
            await DbContextSchemaBootstrapper.EnsureTablesAsync(tools);
        await using (var models = await services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>().CreateDbContextAsync())
            await DbContextSchemaBootstrapper.EnsureTablesAsync(models);
        await using (var agents = await services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync())
            await DbContextSchemaBootstrapper.EnsureTablesAsync(agents);
        await services.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync();
    }

    private sealed class TenantAccessor : ITenantContextAccessor { public TenantContext Current => Actor; }
    private sealed class MemoryContentStore : IContentStore
    {
        private readonly Dictionary<string, byte[]> _bytes = new();
        public async Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream(); await request.Content.CopyToAsync(output, cancellationToken);
            var bytes = output.ToArray(); var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var path = $"content/tenants/{request.TenantId:N}/{request.WorkspaceId:N}/{request.Kind}/{hash}";
            _bytes[path] = bytes; return new(path, hash, bytes.Length, request.MediaType);
        }
        public Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(_bytes[reference.Value]));
        public Task<bool> ExistsAsync(ContentReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_bytes.ContainsKey(reference.Value));
        public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken = default) { _bytes.Remove(reference.Value); return Task.CompletedTask; }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
