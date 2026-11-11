using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Skills;

/// <summary>Project files and immutable Core-owned packages exposed through one validated catalog.</summary>
public sealed class ToolSkillResourceService(
    IDbContextFactory<IntegrationDbContext> database,
    ITenantContextAccessor tenants,
    ISessionLocator sessions,
    StoragePaths storage,
    IServiceProvider services,
    IToolProvider provider) : IToolSkillResourceService, IToolSkillCatalog
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PackageWrites = new(StringComparer.Ordinal);
    public Task<ToolSkillListDto> ListAsync(Guid? projectId, CancellationToken cancellationToken = default) =>
        ListAsync(projectId, includeBuiltins: false, cancellationToken);
    public async Task<ToolSkillListDto> ListAsync(Guid? projectId, bool includeBuiltins, CancellationToken cancellationToken = default)
    {
        var scope = tenants.Current;
        var project = await Project(projectId, cancellationToken);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var rows = await db.SharedSkills.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.WorkspaceId == scope.WorkspaceId && x.DeletedAt == null).ToArrayAsync(cancellationToken);
        var found = rows.Select(Shared).ToList();
        var pathComparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var knownPaths = found.Select(x => x.Path).ToHashSet(pathComparison);
        var liveSourcesAreProjectSources = project is not null && string.Equals(
            WorkspacePathSpelling.Canonical(Path.Combine(project.RootPath, WorkspaceSkillPolicy.SkillRoots[0])),
            WorkspacePathSpelling.Canonical(storage.Locations.Skills),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!liveSourcesAreProjectSources)
            foreach (var entry in WorkspaceSkillDiscovery.Discover(storage.Locations.Root, ["skills"]))
                if (knownPaths.Add(entry.FullPath)) found.Add(SharedSource(entry));
        if (project is not null)
            foreach (var entry in WorkspaceSkillDiscovery.Discover(project.RootPath))
                if (knownPaths.Add(entry.FullPath)) found.Add(ProjectDto(project, entry));

        // Compatibility roots are read-only and weaker than the canonical roots. They exist so a
        // repository that already keeps skills under .agents/skills does not need a migration.
        var userRoot = Path.GetFullPath(StorageScopePaths.UserRoot());
        if (project is not null) AddExternal(Path.GetFullPath(project.RootPath), "compat", 400, readOnly: true, [".agents/skills"]);
        AddExternal(userRoot, "user", 300, readOnly: true, ["skills"]);
        AddExternal(userRoot, "compat", 500, readOnly: true, [".agents/skills"]);

        // Built-ins are embedded and always available; every other root can shadow them by name.
        if (includeBuiltins)
            foreach (var builtin in BuiltinSkillCatalog.All) found.Add(Builtin(builtin));

        found = MarkShadowed(found);
        return new(found.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Scope).ToArray(),
            found.Where(x => !x.Valid).Select(x => $"{x.Path}: {x.Reason}").ToArray());

        void AddExternal(string rootPath, string sourceKind, int rank, bool readOnly, string[] relativeRoots)
        {
            if (!Directory.Exists(rootPath)) return;
            foreach (var entry in WorkspaceSkillDiscovery.Discover(rootPath, relativeRoots))
                if (knownPaths.Add(entry.FullPath))
                    found.Add(ExternalSource(entry, sourceKind, rank, readOnly));
        }
    }

    /// <summary>
    /// One name has one winner. Lower <see cref="ToolSkillResourceDto.Rank"/> wins; the losers stay
    /// visible with <c>shadowed_by</c> so an author who edits the wrong copy can see why nothing
    /// changed. Invalid entries are names nobody can win with, so they are never collapsed away.
    /// </summary>
    private static List<ToolSkillResourceDto> MarkShadowed(List<ToolSkillResourceDto> entries)
    {
        var winners = entries.Where(x => x.Valid && x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Rank)
                .ThenBy(x => x.Scope == "project" ? 0 : 1)
                .ThenBy(x => x.Scope == "shared" ? 0 : 1)
                .First(), StringComparer.Ordinal);
        return entries.Select(entry =>
        {
            if (!entry.Valid || entry.Name.Length == 0) return entry;
            if (!winners.TryGetValue(entry.Name, out var winner) || ReferenceEquals(winner, entry)) return entry;
            return entry with
            {
                ShadowedBy = $"{winner.SourceKind}:{winner.Path}",
                Reason = $"shadowed by {winner.SourceKind} skill at {winner.Path}",
                ReasonCode = "shadowed",
                Availability = "shadowed",
            };
        }).ToList();
    }

    public async Task<ToolSkillResourceDto?> GetAsync(Guid resourceId, Guid? projectId, CancellationToken cancellationToken = default)
    {
        var resource = (await ListAsync(projectId, true, cancellationToken)).Skills.FirstOrDefault(x => x.ResourceId == resourceId);
        if (resource is null) return null;
        if (resource.SourceKind == "builtin")
        {
            var builtin = BuiltinSkillCatalog.Find(resourceId);
            return builtin is null ? null : Builtin(builtin);
        }
        if (!resource.Valid && resource.ContentHash.Length == 0) return resource;
        var root = resource.Scope == "project" ? (await Project(projectId, cancellationToken))!.RootPath : Path.GetDirectoryName(resource.Path)!;
        var entry = WorkspaceSkillDiscovery.Read(root, resource.Path);
        if (entry.Content is null) return resource with { Valid = false, Reason = entry.Reason, ReasonCode = entry.ReasonCode, ReasonData = entry.ReasonData };
        var hash = resource.Scope == "project" ? await FileHash(root, resource.Path, cancellationToken) : entry.ContentHash;
        var packageRoot = Path.GetDirectoryName(resource.Path)!;
        var packageFiles = PackageFiles(packageRoot);
        var packageHash = ActualPackageHash(packageRoot);
        if (resource.Scope == "shared" && !string.Equals(packageHash, resource.ContentHash, StringComparison.Ordinal))
            return resource with { Valid = false, Availability = "invalid", Reason = "Skill package bytes changed outside Core; reinstall the reviewed package.", ReasonCode = "package_changed", ReasonData = null, Content = entry.Content, FileHash = hash, PackageHash = packageHash, PackageFiles = packageFiles };
        return resource with { Content = entry.Content, FileHash = hash, PackageHash = packageHash, PackageFiles = packageFiles };
    }

    public async Task<ToolSkillFileDto?> GetFileAsync(Guid resourceId, Guid? projectId, string relativePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ToolSettingsException("invalid_skill_asset", "A relative package path is required.");
        var resource = await GetAsync(resourceId, projectId, cancellationToken);
        if (resource is null) return null;
        if (resource.SourceKind == "builtin")
        {
            var builtin = BuiltinSkillCatalog.Find(resourceId);
            var file = builtin?.Files.FirstOrDefault(x => string.Equals(x.Path, relativePath.Replace('\\', '/'), StringComparison.Ordinal));
            if (file is null) return null;
            string? text = null;
            try { text = new UTF8Encoding(false, true).GetString(file.Bytes); } catch (DecoderFallbackException) { }
            return new ToolSkillFileDto
            {
                Path = file.Path, ContentHash = file.ContentHash, SizeBytes = file.Bytes.LongLength,
                Content = text, Base64 = Convert.ToBase64String(file.Bytes),
            };
        }
        var packageRoot = Path.GetDirectoryName(resource.Path)!;
        ValidateAsset(relativePath, allowSkillFile: true);
        var path = Path.GetFullPath(Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!WorkspaceSkillDiscovery.IsContained(packageRoot, path))
            throw new ToolSettingsException("skill_path_escape", "The requested package file is outside the skill root.");
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string? content = null;
        try { content = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { }
        return new ToolSkillFileDto { Path = relativePath.Replace('\\', '/'), ContentHash = hash, SizeBytes = bytes.LongLength, Content = content, Base64 = Convert.ToBase64String(bytes) };
    }

    public async Task<IReadOnlyList<ToolSkillSnapshotDto>> ResolveAsync(Guid? projectId, IReadOnlyList<Guid>? resourceIds, CancellationToken cancellationToken = default)
    {
        var catalog = (await ListAsync(projectId, true, cancellationToken)).Skills;
        IEnumerable<ToolSkillResourceDto> selected;
        if (resourceIds is not null)
        {
            var exact = new List<ToolSkillResourceDto>();
            foreach (var id in resourceIds)
            {
                var resource = catalog.FirstOrDefault(x => x.ResourceId == id);
                if (resource is null || !resource.Valid)
                    throw new ToolSettingsException("invalid_skill_binding", $"Skill {id} is missing, invalid or outside the selected project.");
                if (resource.Enabled) exact.Add(resource);
            }
            selected = exact;
        }
        else
        {
            var projectNames = catalog.Where(x => x.Scope == "project").Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            selected = catalog.Where(x => x.Valid && x.Enabled && x.ShadowedBy is null && (x.Scope == "project" || !projectNames.Contains(x.Name)));
        }
        return selected.Select(x => new ToolSkillSnapshotDto
        {
            ResourceId = x.ResourceId, Name = x.Name, Description = x.Description,
            RootPath = Path.GetDirectoryName(x.Path)!, SkillPath = x.Path, Revision = x.Revision, ContentHash = x.ContentHash,
        }).ToArray();
    }

    public Task<ToolSkillCatalogSnapshot> CaptureAsync(Guid? projectId, CancellationToken cancellationToken = default)
        => CaptureSelectedCoreAsync(projectId, null, cancellationToken);

    public Task<ToolSkillCatalogSnapshot> CaptureSelectedAsync(Guid? projectId, IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken = default)
        => CaptureSelectedCoreAsync(projectId, resourceIds, cancellationToken);

    private async Task<ToolSkillCatalogSnapshot> CaptureSelectedCoreAsync(Guid? projectId, IReadOnlyCollection<Guid>? resourceIds, CancellationToken cancellationToken)
    {
        var resources = (await ListAsync(projectId, true, cancellationToken)).Skills;
        var snapshots = new List<ToolSkillSnapshotDto>();
        foreach (var resource in resources.Where(x => x.Valid && x.Enabled
            && (resourceIds is null ? x.ShadowedBy is null : resourceIds.Contains(x.ResourceId))))
        {
            if (resource.SourceKind == "builtin")
            {
                var builtin = BuiltinSkillCatalog.Find(resource.ResourceId);
                if (builtin is not null)
                    snapshots.Add(new ToolSkillSnapshotDto
                    {
                        ResourceId = builtin.ResourceId, Name = builtin.Name, Description = builtin.Description,
                        RootPath = "", SkillPath = $"tinadec://builtin/{builtin.Name}/SKILL.md",
                        Revision = builtin.Revision, ContentHash = builtin.ContentHash,
                        SourceKind = "builtin", ResourceBase = $"tinadec://builtin/{builtin.Name}",
                    });
                continue;
            }
            var root = Path.GetDirectoryName(resource.Path)!;
            var hash = resource.ContentHash;
            {
                var expectedBodyHash = WorkspaceSkillDiscovery.Read(root, resource.Path).ContentHash;
                var capture = await ProjectSkillPackageCapture.CaptureAsync(storage, tenants.Current.TenantId,
                    tenants.Current.WorkspaceId, resource.Name, root, expectedBodyHash, cancellationToken,
                    resource.Scope == "project" ? "project-skills" : "skills");
                root = capture.RootPath;
                hash = capture.ContentHash;
            }
            snapshots.Add(new ToolSkillSnapshotDto
            {
                ResourceId = resource.ResourceId, Name = resource.Name, Description = resource.Description,
                RootPath = root, SkillPath = Path.Combine(root, "SKILL.md"), Revision = resource.Revision, ContentHash = hash,
                SourceKind = resource.SourceKind, ResourceBase = root,
            });
        }
        return new ToolSkillCatalogSnapshot(resources, snapshots);
    }

    /// <summary>
    /// The settings surface never writes a Skill directly. A shared package is queued as a governed
    /// <c>skill_resource_update</c> action and only lands when a human approves it; a project package
    /// is delegated to the existing governed project-file action. The receipt carries the frozen
    /// file manifest and package hash so the reviewer can see exactly what will be written.
    /// </summary>
    public async Task<ToolSkillResourceDto> RequestSaveAsync(Guid? resourceId, Guid? projectId, ToolSkillWriteDto input, long expectedRevision, CancellationToken cancellationToken = default)
    {
        projectId ??= input.ProjectId;
        var existing = resourceId is { } id ? await GetAsync(id, projectId, cancellationToken) : null;
        if (resourceId is not null && existing is null) throw new ToolSettingsException("skill_not_found", "Skill resource not found.", 404);
        if (existing?.ReadOnly == true) throw new ToolSettingsException("read_only_skill", "Built-in and compatibility skills are read-only. Copy the package into the project or user library to edit it.", 409);
        if ((existing?.Revision ?? 0) != expectedRevision) throw Conflict();
        var isProject = existing?.Scope == "project" || existing is null && input.Scope == "project";
        if (isProject || existing?.Scope == "project") return await SaveAsync(resourceId, projectId, input, expectedRevision, cancellationToken);
        if (existing is null && input.Scope != "shared") throw new ToolSettingsException("invalid_scope", "Scope must be shared or project.");
        if (existing is null)
        {
            var scope = tenants.Current;
            await using var lookup = await database.CreateDbContextAsync(cancellationToken);
            if (await lookup.SharedSkills.AnyAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Name == input.Name && x.DeletedAt == null, cancellationToken))
                throw new ToolSettingsException("duplicate_skill", "A shared skill already has this name.", 409);
        }

        var name = existing?.Name ?? input.Name ?? "";
        if (!WorkspaceSkillPolicy.ValidateName(name, out var reason)) throw new ToolSettingsException("invalid_skill", reason);
        var content = input.Content ?? existing?.Content ?? throw new ToolSettingsException("invalid_skill", "Skill content is required.");
        if (input.Enabled is { } flagged) content = WorkspaceSkillDiscovery.SetEnabled(content, flagged);
        var parse = WorkspaceSkillDiscovery.SetEnabled(content, true);
        if (Encoding.UTF8.GetByteCount(content) > WorkspaceSkillPolicy.MaxFileBytes
            || !WorkspaceSkillPolicy.TryRead(parse, name, WorkspaceSkillPolicy.RelativePathFor(name), out var skill, out reason))
            throw new ToolSettingsException("invalid_skill", reason.Length == 0 ? "Skill document exceeds the hard size limit." : reason);
        if (existing is not null && input.Files is not null && !input.ReplaceFiles)
            throw new ToolSettingsException("skill_assets_replace_confirmation", "Replacing a package's assets requires replace_files=true.", 428);

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["SKILL.md"] = Encoding.UTF8.GetBytes(content) };
        if (existing is not null && input.Files is null)
            foreach (var file in SafePackagePaths(Path.GetDirectoryName(existing.Path)!))
            {
                if (!WorkspaceSkillDiscovery.IsContained(Path.GetDirectoryName(existing.Path)!, file)) throw new ToolSettingsException("skill_path_escape", "Existing asset escapes the package.");
                var relative = Path.GetRelativePath(Path.GetDirectoryName(existing.Path)!, file).Replace('\\', '/');
                if (relative != "SKILL.md") files.Add(relative, await File.ReadAllBytesAsync(file, cancellationToken));
            }
        if (input.Files is not null)
        {
            var normalizedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKILL.md" };
            foreach (var (relative, base64) in input.Files)
            {
                ValidateAsset(relative);
                if (!normalizedAssets.Add(relative)) throw new ToolSettingsException("invalid_skill_asset", "Asset paths must be unique ignoring case.");
                try { files.Add(relative, Convert.FromBase64String(base64)); }
                catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new ToolSettingsException("invalid_skill_asset", "Asset must have a unique safe relative path and base64 content."); }
            }
        }
        if (files.Count > 256 || files.Sum(x => (long)x.Value.Length) > 16 * 1024 * 1024)
            throw new ToolSettingsException("skill_package_budget", "Skill packages are limited to 256 files and 16 MiB.");
        var hash = PackageDigest(files);
        var resourceIdForReview = existing?.ResourceId ?? input.ReservedResourceId ?? Guid.NewGuid();
        var parameters = new
        {
            action = "save", resource_id = resourceIdForReview, expected_revision = expectedRevision,
            expected_package_hash = existing?.PackageHash, scope = "shared", name, content,
            files = files.Where(x => x.Key != "SKILL.md").ToDictionary(x => x.Key, x => Convert.ToBase64String(x.Value)),
            replace_files = true, expected_package_digest = hash,
            source = input.Source, version = input.Version, commit = input.Commit,
        };
        var action = await services.GetRequiredService<IUserToolActionService>().CreateAsync(new UserToolActionRequest(
            Guid.Empty, "skill_resource_update", JsonSerializer.Serialize(parameters, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
            IdempotencyKey: $"skill-request:{resourceIdForReview:N}:{expectedRevision}:{hash}"), cancellationToken);
        return new ToolSkillResourceDto
        {
            ResourceId = resourceIdForReview, Scope = "shared", Name = name, Description = skill!.Description,
            Enabled = input.Enabled ?? true, Valid = false, Reason = "pending governed approval", ReasonCode = "pending_approval",
            Revision = expectedRevision, ContentHash = hash, PackageHash = hash, Availability = "pending",
            Source = NormalizeSource(input.Source), Version = input.Version, Commit = input.Commit,
            PackageFiles = files.Select(x => new ToolSkillPackageFileDto
            {
                Path = x.Key, ContentHash = Convert.ToHexStringLower(SHA256.HashData(x.Value)), SizeBytes = x.Value.LongLength,
            }).ToArray(),
            Content = content, UserActionId = action.Id, ActionStatus = action.Status,
        };
    }

    internal static string PackageDigest(SortedDictionary<string, byte[]> files)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, bytes) in files) { digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + bytes.Length + "\0")); digest.AppendData(bytes); }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    public async Task<ToolSkillResourceDto> SaveAsync(Guid? resourceId, Guid? projectId, ToolSkillWriteDto input, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var gate = PackageWrites.GetOrAdd(storage.Locations.StorageId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await SaveCoreAsync(resourceId, projectId, input, expectedRevision, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<ToolSkillResourceDto> SaveCoreAsync(Guid? resourceId, Guid? projectId, ToolSkillWriteDto input, long expectedRevision, CancellationToken cancellationToken)
    {
        projectId ??= input.ProjectId;
        var existing = resourceId is { } id ? await GetAsync(id, projectId, cancellationToken) : null;
        if (resourceId is not null && existing is null) throw new ToolSettingsException("skill_not_found", "Skill resource not found.", 404);
        if (existing?.ReadOnly == true) throw new ToolSettingsException("read_only_skill", "Built-in and compatibility skills are read-only. Copy the package into the project or user library to edit it.", 409);
        if ((existing?.Revision ?? 0) != expectedRevision) throw Conflict();
        var name = existing?.Name ?? input.Name ?? "";
        if (!WorkspaceSkillPolicy.ValidateName(name, out var reason)) throw new ToolSettingsException("invalid_skill", reason);
        var content = input.Content ?? existing?.Content ?? throw new ToolSettingsException("invalid_skill", "Skill content is required.");
        if (input.Enabled is { } enabled) content = WorkspaceSkillDiscovery.SetEnabled(content, enabled);
        var parse = WorkspaceSkillDiscovery.SetEnabled(content, true);
        if (Encoding.UTF8.GetByteCount(content) > WorkspaceSkillPolicy.MaxFileBytes
            || !WorkspaceSkillPolicy.TryRead(parse, name, WorkspaceSkillPolicy.RelativePathFor(name), out var skill, out reason))
            throw new ToolSettingsException("invalid_skill", reason.Length == 0 ? "Skill document exceeds the hard size limit." : reason);
        var isProject = existing?.Scope == "project" || existing is null && input.Scope == "project";
        if (existing is null && input.Scope is not ("shared" or "project")) throw new ToolSettingsException("invalid_scope", "Scope must be shared or project.");
        if (isProject)
        {
            var project = await Project(projectId, cancellationToken) ?? throw new ToolSettingsException("project_required", "Project skills require a project.");
            if (existing is not null && input.Files is not null && !input.ReplaceFiles)
                throw new ToolSettingsException("skill_assets_replace_confirmation", "Replacing a package's assets requires replace_files=true.", 428);
            var path = existing?.Path ?? WorkspaceSkillPolicy.AbsolutePathFor(project.RootPath, name);
            if (!WorkspaceSkillDiscovery.IsContained(project.RootPath, path)) throw new ToolSettingsException("skill_path_escape", "Skill path or parent link escapes the project.");
            if (existing is not null && (string.IsNullOrWhiteSpace(input.ExpectedFileHash) || input.ExpectedFileHash != existing.FileHash))
                throw new ToolSettingsException("skill_hash_conflict", "The reviewed file hash is required and must match the current skill.", 412);
            var assets = new Dictionary<string,string>(StringComparer.Ordinal);
            if (input.Files is null && existing is not null)
                foreach (var file in SafePackagePaths(Path.GetDirectoryName(path)!))
                {
                    var relativePath = Path.GetRelativePath(Path.GetDirectoryName(path)!, file).Replace('\\','/');
                    if (relativePath != "SKILL.md") assets[relativePath] = Convert.ToBase64String(await File.ReadAllBytesAsync(file, cancellationToken));
                }
            if (input.Files is not null)
            {
                var normalizedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (relativePath, encoded) in input.Files) { ValidateAsset(relativePath); if (!normalizedAssets.Add(relativePath)) throw new ToolSettingsException("invalid_skill_asset", "Asset paths must be unique ignoring case."); assets[relativePath] = encoded; }
            }
            var hashes = new Dictionary<string,string?> { ["SKILL.md"] = existing?.FileHash };
            foreach (var relativePath in assets.Keys) hashes[relativePath] = await FileHash(project.RootPath, Path.Combine(Path.GetDirectoryName(path)!, relativePath.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
            if (existing is not null && input.Files is not null) foreach(var file in SafePackagePaths(Path.GetDirectoryName(path)!))
            {
                var relativePath = Path.GetRelativePath(Path.GetDirectoryName(path)!,file).Replace('\\','/');
                if (!hashes.ContainsKey(relativePath)) hashes[relativePath] = await FileHash(project.RootPath,file,cancellationToken);
            }
            var parameters = new { name, content, files = assets, expected_file_hashes = hashes, package_root = Path.GetDirectoryName(path) };
            var action = await services.GetRequiredService<IUserToolActionService>().CreateAsync(new(project.ProjectId, "skill_project_package", JsonSerializer.Serialize(parameters)), cancellationToken);
            var result = action.Status == UserToolActionStatuses.Completed
                ? await GetAsync(ProjectId(project, Path.GetRelativePath(project.RootPath, path).Replace('\\', '/')), project.ProjectId, cancellationToken)
                : existing;
            return (result ?? new ToolSkillResourceDto { ResourceId = ProjectId(project, Path.GetRelativePath(project.RootPath, path).Replace('\\', '/')),
                Scope = "project", ProjectId = project.ProjectId, Name = name, Path = path, Description = skill!.Description, Reason = "pending governed file action", ReasonCode = "pending_file_action" })
                with { UserActionId = action.Id, ActionStatus = action.Status };
        }

        var scope = tenants.Current;
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var row = resourceId is { } sharedId ? await db.SharedSkills.SingleOrDefaultAsync(x => x.Id == sharedId && x.TenantId == scope.TenantId
            && x.WorkspaceId == scope.WorkspaceId && x.DeletedAt == null, cancellationToken) : null;
        if (resourceId is not null && existing?.Source != "source" && (row is null || Shared(row).Revision != expectedRevision)) throw Conflict();
        if (row is null && await db.SharedSkills.AnyAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
            && x.Name == name && x.DeletedAt == null, cancellationToken)) throw new ToolSettingsException("duplicate_skill", "A shared skill already has this name.", 409);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["SKILL.md"] = Encoding.UTF8.GetBytes(content) };
        if (existing is not null && input.Files is not null && !input.ReplaceFiles)
            throw new ToolSettingsException("skill_assets_replace_confirmation", "Replacing a package's assets requires replace_files=true.", 428);
        if (existing is not null && input.Files is null)
        {
            var oldRoot = Path.GetDirectoryName(existing.Path)!;
            foreach (var file in Directory.EnumerateFiles(oldRoot, "*", SearchOption.AllDirectories))
            {
                if (!WorkspaceSkillDiscovery.IsContained(oldRoot, file)) throw new ToolSettingsException("skill_path_escape", "Existing asset escapes the package.");
                var relative = Path.GetRelativePath(oldRoot, file).Replace('\\', '/');
                if (relative != "SKILL.md") files.Add(relative, await File.ReadAllBytesAsync(file, cancellationToken));
            }
        }
        if (input.Files is not null)
        {
            var normalizedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKILL.md" };
            foreach (var (relative, base64) in input.Files)
            {
                ValidateAsset(relative);
                if (!normalizedAssets.Add(relative)) throw new ToolSettingsException("invalid_skill_asset", "Asset paths must be unique ignoring case.");
                try { files.Add(relative, Convert.FromBase64String(base64)); }
                catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new ToolSettingsException("invalid_skill_asset", "Asset must have a unique safe relative path and base64 content."); }
            }
        }
        if (files.Count > 256 || files.Sum(x => (long)x.Value.Length) > 16 * 1024 * 1024)
            throw new ToolSettingsException("skill_package_budget", "Skill packages are limited to 256 files and 16 MiB.");
        var hash = PackageDigest(files);
        if (input.ExpectedPackageDigest is { Length: > 0 } reviewed && !string.Equals(reviewed, hash, StringComparison.Ordinal))
            throw new ToolSettingsException("skill_hash_conflict", "The reviewed package digest no longer matches the bytes being written.", 412);
        var reference = $"packages/skills/{hash}/{name}";
        var destination = Path.Combine(storage.Locations.Packages, "skills", hash, name);
        if (!Directory.Exists(destination))
        {
            var temp = Path.Combine(storage.Locations.Temp, "skills", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (var (relative, bytes) in files)
                {
                    var path = Path.Combine(temp, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try { Directory.Move(temp, destination); }
                catch (IOException) when (Directory.Exists(destination)) { Directory.Delete(temp, true); }
            }
            catch { if (Directory.Exists(temp)) Directory.Delete(temp, true); throw; }
        }
        if (row is null) { row = new() { Id = resourceId ?? input.ReservedResourceId ?? Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, Name = name }; db.SharedSkills.Add(row); }
        row.Description = skill!.Description; row.Enabled = WorkspaceSkillPolicy.TryRead(content, name, "SKILL.md", out _, out _);
        row.Source = input.Source == "manual" && existing is not null ? existing.Source : NormalizeSource(input.Source);
        row.Version = input.Version is null ? existing?.Version : NormalizeMetadata(input.Version, 128);
        row.Commit = input.Commit is null ? existing?.Commit : NormalizeMetadata(input.Commit, 64);
        row.Revision++; row.ContentHash = hash; row.PackageReference = reference; row.UpdatedAt = DateTimeOffset.UtcNow;
        await ReplaceLivePackageAsync(name, files, async () =>
        {
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { throw Conflict(); }
        }, cancellationToken);
        return Shared(row) with { Content = content, PackageFiles = PackageFiles(destination) };
    }

    public async Task DeleteAsync(Guid resourceId, Guid? projectId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var gate = PackageWrites.GetOrAdd(storage.Locations.StorageId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await DeleteCoreAsync(resourceId, projectId, expectedRevision, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task DeleteCoreAsync(Guid resourceId, Guid? projectId, long expectedRevision, CancellationToken cancellationToken)
    {
        var resource = await GetAsync(resourceId, projectId, cancellationToken) ?? throw new ToolSettingsException("skill_not_found", "Skill resource not found.", 404);
        if (resource.Revision != expectedRevision) throw Conflict();
        if (resource.ReadOnly) throw new ToolSettingsException("read_only_skill", "Built-in and compatibility skills are read-only.", 409);
        if (resource.Scope == "project") throw new ToolSettingsException("project_skill_delete_requires_file_tools", "Remove project skills with governed project file tools, or disable the skill here.");
        var scope = tenants.Current;
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var row = await db.SharedSkills.SingleOrDefaultAsync(x => x.Id == resourceId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken);
        if (row is not null && Shared(row).Revision != expectedRevision) throw Conflict();
        if (row is not null) { row.DeletedAt = DateTimeOffset.UtcNow; row.Enabled = false; row.Revision++; }
        var live = Path.Combine(storage.Locations.Skills, resource.Name);
        var removed = Path.Combine(storage.Locations.Temp, "skills-source", Guid.NewGuid().ToString("N"));
        if (!WorkspaceSkillDiscovery.IsContained(storage.Locations.Skills, live)) throw new ToolSettingsException("skill_path_escape", "The live skill path leaves the scope.");
        Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
        if (Directory.Exists(live)) Directory.Move(live, removed);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            if (Directory.Exists(removed)) Directory.Move(removed, live);
            throw;
        }
        if (Directory.Exists(removed)) Directory.Delete(removed, true);
    }

    public async Task<ToolSkillResourceDto> RequestCopyToProjectAsync(Guid resourceId, Guid projectId, CancellationToken cancellationToken = default)
    {
        var builtin = BuiltinSkillCatalog.Find(resourceId) ?? throw new ToolSettingsException("skill_not_found", "Built-in skill not found.", 404);
        var project = await Project(projectId, cancellationToken) ?? throw new ToolSettingsException("project_required", "A project is required.");
        var path = WorkspaceSkillPolicy.AbsolutePathFor(project.RootPath, builtin.Name);
        if (!WorkspaceSkillDiscovery.IsContained(project.RootPath, path)) throw new ToolSettingsException("skill_path_escape", "Skill path or parent link escapes the project.");
        if (Directory.Exists(Path.GetDirectoryName(path))) throw new ToolSettingsException("skill_exists", "A project skill already uses this name.", 409);
        var files = builtin.Files.Where(x => !string.Equals(x.Path, "SKILL.md", StringComparison.Ordinal))
            .ToDictionary(x => x.Path, x => Convert.ToBase64String(x.Bytes), StringComparer.Ordinal);
        var parameters = new
        {
            name = builtin.Name,
            content = builtin.Content,
            files,
            expected_file_hashes = files.Keys.Append("SKILL.md").ToDictionary(x => x, _ => (string?)null),
            package_root = Path.GetDirectoryName(path),
        };
        var action = await services.GetRequiredService<IUserToolActionService>().CreateAsync(
            new UserToolActionRequest(project.ProjectId, "skill_project_package", JsonSerializer.Serialize(parameters)), cancellationToken);
        var relative = Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
        return new ToolSkillResourceDto
        {
            ResourceId = ProjectId(project, relative), Scope = "project", ProjectId = project.ProjectId, Name = builtin.Name,
            Description = builtin.Description, Path = path, Enabled = true, Valid = true, Content = builtin.Content,
            Reason = "pending governed file action", ReasonCode = "pending_file_action", Availability = "pending",
            SourceKind = "project", Rank = 100, UserActionId = action.Id, ActionStatus = action.Status,
            PackageFiles = builtin.Files.Select(x => new ToolSkillPackageFileDto { Path = x.Path, ContentHash = x.ContentHash, SizeBytes = x.Bytes.LongLength }).ToArray(),
        };
    }

    public async Task<ToolSkillResourceDto> RequestDeleteAsync(Guid resourceId, Guid? projectId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var resource = await GetAsync(resourceId, projectId, cancellationToken) ?? throw new ToolSettingsException("skill_not_found", "Skill resource not found.", 404);
        if (resource.Revision != expectedRevision) throw Conflict();
        if (resource.ReadOnly) throw new ToolSettingsException("read_only_skill", "Built-in and compatibility skills are read-only.", 409);
        if (resource.Scope == "project")
        {
            var project = await Project(projectId, cancellationToken) ?? throw new ToolSettingsException("project_required", "A project is required.");
            var hashes = new Dictionary<string,string?>();
            foreach (var file in SafePackagePaths(Path.GetDirectoryName(resource.Path)!))
                hashes[Path.GetRelativePath(Path.GetDirectoryName(resource.Path)!, file).Replace('\\','/')] = await FileHash(project.RootPath, file, cancellationToken);
            if (hashes.Any(x => string.IsNullOrWhiteSpace(x.Value))) throw new ToolSettingsException("skill_hash_conflict", "All package files must be readable before deletion.", 412);
            var projectAction = await services.GetRequiredService<IUserToolActionService>().CreateAsync(new(project.ProjectId, "skill_project_package", JsonSerializer.Serialize(new { action = "delete", name = resource.Name, content = "", files = new Dictionary<string,string>(), expected_file_hashes = hashes, package_root = Path.GetDirectoryName(resource.Path) })), cancellationToken);
            return resource with { UserActionId = projectAction.Id, ActionStatus = projectAction.Status };
        }
        var action = await services.GetRequiredService<IUserToolActionService>().CreateAsync(new UserToolActionRequest(Guid.Empty, "skill_resource_update", JsonSerializer.Serialize(new {
            action = "delete", resource_id = resourceId, expected_revision = expectedRevision, expected_package_hash = resource.PackageHash,
            scope = "shared", name = resource.Name, content = "",
        })), cancellationToken);
        return resource with { UserActionId = action.Id, ActionStatus = action.Status };
    }

    private static ToolSkillResourceDto Builtin(BuiltinSkillPackage package) => new()
    {
        ResourceId = package.ResourceId, Scope = "shared", Name = package.Name, Description = package.Description,
        Enabled = true, Valid = true, Path = $"tinadec://builtin/{package.Name}/SKILL.md",
        Revision = package.Revision, ContentHash = package.ContentHash, PackageHash = package.ContentHash,
        PackageReference = $"builtin:{package.Version}", Source = "builtin", SourceKind = "builtin", Rank = 900,
        ReadOnly = true, Availability = "available", Content = package.Content,
        PackageFiles = package.Files.Select(x => new ToolSkillPackageFileDto
        {
            Path = x.Path, ContentHash = x.ContentHash, SizeBytes = x.Bytes.LongLength,
        }).ToArray(),
    };

    private ToolSkillResourceDto ExternalSource(WorkspaceSkillDiscovery.Entry entry, string sourceKind, int rank, bool readOnly)
    {
        var seed = $"{tenants.Current.TenantId:N}/{tenants.Current.WorkspaceId:N}/{sourceKind}/{entry.RelativePath}";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
        var root = Path.GetDirectoryName(entry.FullPath)!;
        var hash = entry.Valid ? ActualPackageHash(root) : entry.ContentHash;
        return new()
        {
            ResourceId = id, Scope = "shared", Name = entry.Name, Description = entry.Description,
            Enabled = entry.Enabled, Valid = entry.Valid, Reason = entry.Reason, ReasonCode = entry.ReasonCode,
            ReasonData = entry.ReasonData, Path = entry.FullPath, Revision = Revision(hash), ContentHash = hash,
            PackageHash = hash, Availability = entry.Valid ? entry.Enabled ? "available" : "disabled" : "invalid",
            Source = sourceKind, SourceKind = sourceKind, Rank = rank, ReadOnly = readOnly,
            PackageFiles = entry.Valid ? PackageFiles(root) : [],
        };
    }

    private ToolSkillResourceDto Shared(SharedSkillResourceRecord row)
    {
        var root = Path.Combine(storage.Locations.Skills, row.Name);
        var entry = WorkspaceSkillDiscovery.Read(root, Path.Combine(root, "SKILL.md"));
        var packageFiles = PackageFiles(root);
        var actualHash = Directory.Exists(root) ? ActualPackageHash(root) : "";
        return new() { ResourceId = row.Id, Scope = "shared", Name = row.Name, Description = entry.Description ?? row.Description,
            Enabled = row.Enabled && entry.Enabled, Valid = entry.Valid, Reason = entry.Valid ? row.Enabled ? entry.Reason : "disabled in Core" : entry.Reason,
            ReasonCode = entry.Valid && !row.Enabled ? "disabled" : entry.ReasonCode, ReasonData = entry.Valid && !row.Enabled ? null : entry.ReasonData,
            Path = entry.FullPath, Revision = string.Equals(actualHash, row.ContentHash, StringComparison.Ordinal) ? row.Revision : Revision(actualHash), ContentHash = actualHash, PackageHash = actualHash,
            PackageReference = row.PackageReference, Source = row.Source, Version = row.Version, Commit = row.Commit,
            SourceKind = "scope", Rank = 200, Availability = entry.Valid ? row.Enabled ? "available" : "disabled" : "invalid", PackageFiles = packageFiles };
    }

    private ToolSkillResourceDto SharedSource(WorkspaceSkillDiscovery.Entry entry)
    {
        var seed = $"{tenants.Current.TenantId:N}/{tenants.Current.WorkspaceId:N}/shared/{entry.RelativePath}";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
        var root = Path.GetDirectoryName(entry.FullPath)!;
        var hash = entry.Valid ? ActualPackageHash(root) : entry.ContentHash;
        return new() { ResourceId = id, Scope = "shared", Name = entry.Name, Description = entry.Description,
            Enabled = entry.Enabled, Valid = entry.Valid, Reason = entry.Reason, ReasonCode = entry.ReasonCode, ReasonData = entry.ReasonData, Path = entry.FullPath,
            Revision = Revision(hash), ContentHash = hash, PackageHash = hash,
            Availability = entry.Valid ? entry.Enabled ? "available" : "disabled" : "invalid", Source = "source",
            SourceKind = "scope", Rank = 200,
            PackageFiles = entry.Valid ? PackageFiles(root) : [] };
    }

    private async Task ReplaceLivePackageAsync(string name, IReadOnlyDictionary<string, byte[]> files, Func<Task> commit, CancellationToken ct)
    {
        var root = storage.Locations.Skills;
        var destination = Path.Combine(root, name);
        if (!WorkspaceSkillDiscovery.IsContained(root, destination)) throw new ToolSettingsException("skill_path_escape", "The live skill path leaves the scope.");
        var temporary = Path.Combine(storage.Locations.Temp, "skills-source", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(storage.Locations.Temp, "skills-source", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in files)
            {
                var output = Path.Combine(temporary, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllBytesAsync(output, file.Value, ct);
            }
            Directory.CreateDirectory(root);
            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            try { Directory.Move(temporary, destination); await commit(); }
            catch
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                if (Directory.Exists(backup)) Directory.Move(backup, destination);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }

    private static ToolSkillResourceDto ProjectDto(ProjectReference project, WorkspaceSkillDiscovery.Entry entry)
    {
        var packageRoot = Path.GetDirectoryName(entry.FullPath)!;
        var packageFiles = entry.Valid ? PackageFiles(packageRoot) : [];
        var packageHash = entry.Valid ? ActualPackageHash(packageRoot) : entry.ContentHash;
        return new()
        {
            ResourceId = ProjectId(project, entry.RelativePath), ProjectId = project.ProjectId, Scope = "project", Name = entry.Name,
            Description = entry.Description, Enabled = entry.Enabled, Valid = entry.Valid, Reason = entry.Reason, ReasonCode = entry.ReasonCode, ReasonData = entry.ReasonData,
            Path = entry.FullPath, ContentHash = entry.ContentHash, Revision = Revision(packageHash), PackageHash = packageHash, PackageFiles = packageFiles,
            Availability = !entry.Valid ? "invalid" : entry.Enabled ? "available" : "disabled", Source = "project",
            SourceKind = "project", Rank = 100,
        };
    }
    private static Guid ProjectId(ProjectReference project, string relative) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"skill:{project.TenantId:N}:{project.WorkspaceId:N}:{project.ProjectId:N}:{relative}"))[..16]);
    // Revisions cross JSON/JavaScript: retain exact integer representation in Desktop If-Match.
    private static long Revision(string hash) => hash.Length == 0 ? 0 : BinaryPrimitives.ReadInt64BigEndian(Convert.FromHexString(hash)[..8]) & ((1L << 53) - 1);
    private static ToolSettingsException Conflict() => new("revision_conflict", "Skill changed since it was loaded. Reload before saving.", 412);
    private async Task<ProjectReference?> Project(Guid? id, CancellationToken ct)
    {
        if (id is null) return null;
        var project = await sessions.FindProjectAsync(id.Value, ct);
        var scope = tenants.Current;
        if (project is null || project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId)
            throw new ToolSettingsException("project_not_found", "Project is not visible in this workspace.", 404);
        return project;
    }
    private async Task<string?> FileHash(string root, string path, CancellationToken ct)
    {
        var response = await provider.CallAsync(root, new ToolWireRequestDto { ToolId = "read_file", SessionId = "skill-settings",
            Params = JsonSerializer.SerializeToElement(new { filepath = path }) }, TimeSpan.FromSeconds(30), ct);
        return response.IsSuccess && response.Result is { } result && result.TryGetProperty("file_hash", out var hash) ? hash.GetString() : null;
    }
    private static IReadOnlyList<ToolSkillPackageFileDto> PackageFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        var files = new List<ToolSkillPackageFileDto>();
        long total = 0;
        foreach (var file in SafePackagePaths(root))
        {
            if (!WorkspaceSkillDiscovery.IsContained(root, file)) throw new ToolSettingsException("skill_path_escape", "A Skill package file leaves its root.");
            total += new FileInfo(file).Length;
            if (files.Count >= 256 || total > 16 * 1024 * 1024) throw new ToolSettingsException("skill_package_budget", "Skill packages are limited to 256 files and 16 MiB.");
            var bytes = File.ReadAllBytes(file);
            files.Add(new ToolSkillPackageFileDto
            {
                Path = Path.GetRelativePath(root, file).Replace('\\', '/'),
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                SizeBytes = bytes.LongLength,
            });
        }
        return files.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }

    private static string ActualPackageHash(string root)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in SafePackagePaths(root).OrderBy(x => Path.GetRelativePath(root,x).Replace('\\','/'), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root,path).Replace('\\','/'); var bytes = File.ReadAllBytes(path);
            digest.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + bytes.Length + "\0")); digest.AppendData(bytes);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static IEnumerable<string> SafePackagePaths(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!WorkspaceSkillDiscovery.IsContained(root, directory) || new DirectoryInfo(directory).LinkTarget is not null)
                throw new ToolSettingsException("skill_path_escape", "Skill package directory links are not accepted.");
            foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (new FileInfo(file).LinkTarget is not null) throw new ToolSettingsException("skill_path_escape", "Skill package file links are not accepted.");
                yield return file;
            }
        }
    }

    private static string NormalizeSource(string? value) => string.IsNullOrWhiteSpace(value) ? "manual" : value.Trim()[..Math.Min(value.Trim().Length, 128)];
    private static string? NormalizeMetadata(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    private static void ValidateAsset(string path, bool allowSkillFile = false)
    {
        if (string.IsNullOrWhiteSpace(path) || (!allowSkillFile && path == "SKILL.md") || path.Contains('\\') || path.Contains(':') || Path.IsPathRooted(path)
            || path.Any(char.IsControl) || path.Split('/').Any(x => x is "" or "." or "..")
            || path.Split('/').Any(x => x.Equals("CON", StringComparison.OrdinalIgnoreCase) || x.Equals("PRN", StringComparison.OrdinalIgnoreCase) || x.Equals("AUX", StringComparison.OrdinalIgnoreCase) || x.Equals("NUL", StringComparison.OrdinalIgnoreCase)))
            throw new ToolSettingsException("invalid_skill_asset", "Asset paths must stay relative to the skill package and cannot contain reserved names.");
    }
}

