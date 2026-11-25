using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Runtime;

/// <summary>Publishes a complete owned layout with the manifest as its commit marker.</summary>
public sealed class StorageScopeInitializer
{
    public const int SchemaVersion = 1;
    private const string IgnoreRules = "# Tinadec runtime data is local; configuration and skills may be shared.\n/data/\n/state/\n/logs/\n/cache/\n/temp/\n/worktrees/\n/packages/\n";

    public static void EnsureUser(StorageScopeDescriptor scope)
    {
        StorageScopePaths.RejectLinks(scope.Root);
        Directory.CreateDirectory(scope.Root);
        foreach (var (_, path) in scope.Categories)
        {
            StorageScopePaths.RejectLinks(scope.Root, path);
            Directory.CreateDirectory(path);
        }
        WriteMissing(Path.Combine(scope.Root, ".gitignore"), IgnoreRules);
        foreach (var category in new[] { "security", "projects", "defaults", "toolchains" })
        {
            var path = Path.Combine(scope.Root, category); StorageScopePaths.RejectLinks(scope.Root, path); Directory.CreateDirectory(path);
        }
        var ignore = Path.Combine(scope.Root, ".gitignore");
        var rules = File.ReadAllText(ignore);
        foreach (var category in new[] { "security", "projects", "toolchains" })
            if (!rules.Contains("/" + category + "/", StringComparison.Ordinal)) rules += "/" + category + "/\n";
        if (rules != File.ReadAllText(ignore)) File.WriteAllText(ignore, rules);
        EnsureRuntime(scope);
    }

    public async Task<StorageScopeDescriptor> InitializeProjectAsync(string storageId, string projectPath,
        StorageScopeDescriptor defaults, string? backend = null, string? configuredRoot = null,
        string? postgresConnectionReference = null, CancellationToken ct = default, Guid? projectId = null,
        WorkspaceDefinition? workspace = null)
    {
        var projectRoot = StorageScopePaths.NormalizeProjectRoot(projectPath);
        var target = configuredRoot is null ? Path.Combine(projectRoot, ".tinadec") : Path.GetFullPath(configuredRoot);
        var external = !SamePath(target, Path.Combine(projectRoot, ".tinadec"));
        if (IsWithin(target, projectRoot) || IsWithin(target, defaults.Root))
            throw new InvalidOperationException("Project storage must not contain the project source directory or user storage root: " + target);
        if (defaults.Categories.Any(category => IsWithin(category.Path, target))
            || new[] { "security", "defaults", "toolchains" }.Any(category => IsWithin(Path.Combine(defaults.Root, category), target)))
            throw new InvalidOperationException("Project storage cannot be nested inside a user configuration, resource or runtime category: " + target);
        if (File.Exists(target)) throw new InvalidOperationException("The selected storage path is a file: " + target);
        StorageScopePaths.RejectLinks(target);
        if (Directory.Exists(target))
            return ReadManifest(storageId, projectRoot, target, backend, postgresConnectionReference, external);

        var scope = new StorageScopeDescriptor(storageId, "project", target, projectRoot, projectId ?? Guid.NewGuid(),
            ValidateBackend(backend ?? "sqlite"), external, postgresConnectionReference);
        try
        {
            await PublishAsync(scope, defaults, ct, workspace).ConfigureAwait(false);
            return scope;
        }
        catch (Exception ex) when (configuredRoot is null && ex is (UnauthorizedAccessException or IOException))
        {
            ct.ThrowIfCancellationRequested();
            // Another initializer may have published the same manifest while this staging
            // tree was being prepared. The winner is authoritative, rather than a fallback.
            if (Directory.Exists(target)) return ReadManifest(storageId, projectRoot, target, backend, postgresConnectionReference, external);
            var fallback = Path.Combine(defaults.Root, "projects", storageId);
            StorageScopePaths.RejectLinks(fallback);
            if (Directory.Exists(fallback)) return ReadManifest(storageId, projectRoot, fallback, backend, postgresConnectionReference, true);
            scope = scope with { Root = fallback, External = true };
            await PublishAsync(scope, defaults, ct, workspace).ConfigureAwait(false);
            return scope;
        }
    }

    public static StorageScopeDescriptor ReadManifest(string storageId, string projectRoot, string root,
        string? backend = null, string? postgresConnectionReference = null, bool external = false)
    {
        StorageScopePaths.RejectLinks(root, Path.Combine(root, "project.toml"));
        var file = Path.Combine(root, "project.toml");
        if (!File.Exists(file)) throw new InvalidOperationException("The storage directory has no completed project.toml manifest: " + root);
        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file))
            ?? throw new InvalidDataException("Project manifest is empty.");
        if (!model.TryGetValue("schema_version", out var version) || Convert.ToInt64(version) != SchemaVersion)
            throw new InvalidDataException("Unsupported project storage schema_version.");
        if (!Guid.TryParse(Value(model, "project_id")?.ToString(), out var projectId))
            throw new InvalidDataException("Project manifest has no valid project_id.");
        var storedBackend = ValidateBackend(Value(model, "backend")?.ToString() ?? "sqlite");
        if (backend is not null && ValidateBackend(backend) != storedBackend)
            throw new InvalidOperationException("Changing an existing backend requires the explicit storage configuration action.");
        var reference = postgresConnectionReference ?? Value(model, "postgres_connection_reference")?.ToString();
        return new StorageScopeDescriptor(storageId, "project", root, projectRoot, projectId, storedBackend, external, reference);
    }

    private static async Task PublishAsync(StorageScopeDescriptor scope, StorageScopeDescriptor defaults, CancellationToken ct,
        WorkspaceDefinition? workspace)
    {
        var parent = Path.GetDirectoryName(scope.Root)!;
        Directory.CreateDirectory(parent);
        var staging = scope.Root + ".initializing-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            var staged = scope with { Root = staging };
            foreach (var (_, path) in staged.Categories) Directory.CreateDirectory(path);
            // Copy the configuration and the resource versions it names, never user conversations or credentials.
            foreach (var id in new[] { "runtime", "agents", "models", "tools", "mcp", "prompts", "skills", "storage", "logging" })
            {
                var source = Path.Combine(defaults.Config, id + ".toml");
                if (!File.Exists(source)) continue;
                StorageScopePaths.RejectLinks(defaults.Root, source);
                File.Copy(source, Path.Combine(staged.Config, id + ".toml"));
            }
            foreach (var category in new[] { "skills", "packages" })
                await CopyOwnedTreeAsync(Path.Combine(defaults.Root, category), Path.Combine(staging, category), ct).ConfigureAwait(false);
            var resourceDiagnostics = await TinadecCore.Tools.ManagedMcpProgramService.InitializeCopiedResourcesAsync(defaults.Root, scope.Root, staging, ct).ConfigureAwait(false);
            if (resourceDiagnostics.Count > 0)
                await File.WriteAllLinesAsync(Path.Combine(staged.State, "resource-initialization-diagnostics.txt"), resourceDiagnostics, ct).ConfigureAwait(false);
            EnsureRuntime(staged);
            var storageFile = Path.Combine(staged.Config, "storage.toml");
            var storageText = File.Exists(storageFile) ? await File.ReadAllTextAsync(storageFile, ct).ConfigureAwait(false) : "version = 1\n[storage]\n";
            storageText = StorageConfigurationText.Set(storageText, "backend", scope.Backend);
            storageText = StorageConfigurationText.Set(storageText, "postgres_connection_reference", scope.PostgresConnectionReference);
            await File.WriteAllTextAsync(storageFile, storageText, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"), IgnoreRules, ct).ConfigureAwait(false);
            var manifest = new TomlTable
            {
                ["schema_version"] = SchemaVersion, ["project_id"] = scope.ProjectId!.Value.ToString("D"),
                ["backend"] = scope.Backend, ["configuration_version"] = 1,
                ["initialized_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["defaults_digest"] = DirectoryDigest(defaults.Config)
            };
            if (scope.PostgresConnectionReference is { } reference) manifest["postgres_connection_reference"] = reference;
            var manifestText = TomlSerializer.Serialize(manifest);
            if (workspace is not null) manifestText = WorkspaceDefinitionFile.Write(manifestText, workspace, scope.ProjectRoot!);
            await File.WriteAllTextAsync(Path.Combine(staging, "project.toml"), manifestText, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, scope.Root);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static async Task CopyOwnedTreeAsync(string source, string destination, CancellationToken ct)
    {
        if (!Directory.Exists(source)) return;
        StorageScopePaths.RejectLinks(source);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A default resource contains a link: " + entry);
            var name = Path.GetFileName(entry);
            if (name is ".tmp" || name.StartsWith(".initializing-", StringComparison.Ordinal)) continue;
            var target = Path.Combine(destination, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                await CopyOwnedTreeAsync(entry, target, ct).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(destination);
                await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureRuntime(IScopeStorageLocations scope)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Configuration", "default-agent-runtime.toml");
        ScopeConfigurationInitialization.Ensure(scope, "runtime", () => File.ReadAllText(bundled));
        // Seeding only covers a scope that has no document yet. An established one keeps the
        // policy it was created with, so the shipped numbers reach it through the idempotent
        // upgrade (values still equal to the old defaults only).
        RuntimeConfigurationMigration.Apply(scope);
    }

    private static void WriteMissing(string path, string text)
    {
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); stream.Write(Encoding.UTF8.GetBytes(text)); }
        catch (IOException) when (File.Exists(path)) { }
    }

    private static string DirectoryDigest(string root)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(root))
            foreach (var file in new[] { "runtime", "agents", "models", "tools", "mcp", "prompts", "skills", "storage", "logging" }
                .Select(id => Path.Combine(root, id + ".toml")).Where(File.Exists).Order(StringComparer.Ordinal))
            {
                StorageScopePaths.RejectLinks(root, file);
                digest.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file)));
                digest.AppendData(File.ReadAllBytes(file));
            }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    internal static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string root, string target) => SamePath(root, target) || Path.GetFullPath(target).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static object? Value(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value : null;

    internal static string ValidateBackend(string backend) => backend.ToLowerInvariant() is "sqlite" or "postgresql"
        ? backend.ToLowerInvariant() : throw new ArgumentException("backend must be sqlite or postgresql.");
}
