using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Tools;

public sealed class ToolConfigurationResolver(IToolSettingsStore settings, IMcpResourceRegistry mcp, ISecretStore secrets, IServiceProvider services) : IToolConfigurationResolver
{
    public async Task<ToolExecutionContextDto> ResolveAsync(Guid? projectId, Guid? agentDefinitionId = null, CancellationToken cancellationToken = default)
    {
        var document = await settings.GetAsync(agentDefinitionId, cancellationToken);
        return await ResolveDocumentAsync(projectId, document, cancellationToken);
    }
    public async Task<ToolExecutionContextDto> ResolveForInspectionAsync(Guid? projectId, Guid? agentDefinitionId = null, CancellationToken cancellationToken = default)
        => await ResolveDocumentAsync(projectId, await settings.GetAsync(agentDefinitionId, cancellationToken), cancellationToken, true);

    private async Task<ToolExecutionContextDto> ResolveDocumentAsync(Guid? projectId, ToolSettingsDocumentDto document, CancellationToken ct, bool inspection = false, AdmissionResources? admitted = null)
    {
        var effective = document.EffectiveSettings;
        IReadOnlyList<ToolMcpServerSnapshotDto> servers = [];
        string? importError = admitted?.McpImportError;
        var diagnostics = new List<ToolResourceDiagnosticDto>();
        var mcpBindings = Bindings(effective, "mcp", "server_resource_ids");
        try
        {
            if (admitted is not null)
            {
                if (effective.GetProperty("mcp").GetProperty("enabled").GetBoolean() && importError is null)
                {
                    var mcpSnapshot = admitted.Mcp;
                    if (mcpBindings is not null && mcpBindings.Any(id => mcpSnapshot.Resources.All(x => x.ResourceId != id))) throw new ToolSettingsException("mcp_binding_not_found", "An MCP binding is outside this project or no longer exists.");
                    var selected = mcpSnapshot.Resources.Where(x => mcpBindings is null || mcpBindings.Contains(x.ResourceId));
                    if (mcpBindings is null) selected = selected.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.ProjectId is not null).First());
                    var ids = selected.Where(x => x.Enabled).Select(x => x.ResourceId).ToHashSet();
                    servers = mcpSnapshot.Servers.Where(x => ids.Contains(x.ResourceId)).ToArray();
                }
            }
            else
            {
                if (inspection && mcpBindings is not null)
                {
                    var inventory = await mcp.ListAsync(projectId, ct);
                    foreach (var id in mcpBindings)
                    {
                        var resource = inventory.SingleOrDefault(x => x.ResourceId == id);
                        if (resource is null || !resource.Enabled) diagnostics.Add(new() { Kind = "mcp", ResourceId = id, Status = resource is null ? "missing" : "disabled", Reason = resource is null ? "The exact MCP resource is missing or not visible in this project." : "This MCP resource is disabled." });
                    }
                    mcpBindings = mcpBindings.Where(id => inventory.Any(x => x.ResourceId == id && x.Enabled)).ToArray();
                }
                if (effective.GetProperty("mcp").GetProperty("enabled").GetBoolean()) servers = await mcp.ResolveAsync(projectId, mcpBindings, ct);
            }
        }
        catch (ToolSettingsException ex) when (ex.Code == "mcp_import_failed")
        {
            importError = ex.Message;
            diagnostics.Add(new() { Kind = "mcp", Status = "import_failed", Reason = ex.Message });
        }
        var catalog = services.GetService<IToolSkillCatalog>();
        var skillBindings = Bindings(effective, "skills", "resource_ids");
        if (inspection && skillBindings is not null)
        {
            var resourceService = services.GetService<IToolSkillResourceService>();
            var inventory = resourceService is null ? [] : (await resourceService.ListAsync(projectId, ct)).Skills;
            foreach (var id in skillBindings)
            {
                var resource = inventory.SingleOrDefault(x => x.ResourceId == id);
                if (resource is null || !resource.Valid || !resource.Enabled) diagnostics.Add(new() { Kind = "skills", ResourceId = id, Status = resource is null ? "missing" : !resource.Valid ? "invalid" : "disabled", Reason = resource?.Reason ?? (resource is null ? "The exact Skill resource is missing or not visible in this project." : "This Skill resource is disabled.") });
            }
            skillBindings = skillBindings.Where(id => inventory.Any(x => x.ResourceId == id && x.Valid && x.Enabled)).ToArray();
        }
        IReadOnlyList<ToolSkillSnapshotDto> skills = [];
        if (effective.GetProperty("skills").GetProperty("enabled").GetBoolean())
        {
            if (admitted is not null)
            {
                var resources = admitted.Skills;
                if (skillBindings is not null && skillBindings.Any(id => !resources.Resources.Any(x => x.ResourceId == id && x.Valid))) throw new ToolSettingsException("invalid_skill_binding", "A Skill binding is missing, invalid or outside the selected project.");
                var projectNames = resources.Resources.Where(x => x.Scope == "project").Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                var ids = resources.Resources.Where(x => x.Valid && x.Enabled && (skillBindings is not null ? skillBindings.Contains(x.ResourceId) : x.ShadowedBy is null && (x.Scope == "project" || !projectNames.Contains(x.Name)))).Select(x => x.ResourceId).ToHashSet();
                skills = resources.Skills.Where(x => ids.Contains(x.ResourceId)).ToArray();
            }
            else if (catalog is not null) skills = await catalog.ResolveAsync(projectId, skillBindings, ct);
        }
        var roots = skills.Select(s => new ToolReadRootDto { ResourceId = s.ResourceId, Path = s.RootPath }).ToArray();
        if (admitted is null && services.GetService<IManagedMcpProgramService>() is { } programs)
        {
            var resolvedServers = new List<ToolMcpServerSnapshotDto>();
            foreach (var server in servers)
            {
                var resolved = await programs.ResolveForExecutionAsync(server, ct);
                resolvedServers.Add(resolved);
                if (resolved.ProgramStatus is "not_installed" or "outdated" or "invalid" or "needs_reinstall")
                    diagnostics.Add(new() { Kind = "mcp_program", ResourceId = resolved.ResourceId, Status = resolved.ProgramStatus, Reason = "Registering an MCP server does not install its program. Review and approve the separate installation action." });
            }
            servers = resolvedServers;
        }
        var locations = services.GetRequiredService<IScopeStorageLocations>();
        var workspace = admitted?.Workspace ?? services.GetService<IWorkspaceDefinitionProvider>()?.Read(requireAvailable: !inspection);
        var workspaceRoots = workspace?.Roots.Select(root => new ToolWorkspaceRootDto { Id = root.Id, Path = root.Path }).ToArray() ?? [];
        var workspaceFiles = workspace?.Files.Select(file => new ToolWorkspaceFileDto { Id = file.Id, Path = file.Path }).ToArray() ?? [];
        var workingDirectory = workspace?.PrimaryPath ?? locations.ProjectRoot;
        var storageWrite = services.GetService<IProjectStorageWritePolicy>()?.AllowStorageWrite == true;
        var protectedRoots = services.GetService<IProtectedStorageRoots>()?.Roots ?? [];
        var hash = ToolSettingsSchema.Hash(JsonSerializer.SerializeToElement(new { settings = effective, mcp = servers.Select(s => new { s.ConfigurationHash, s.ProgramHash, s.ProgramStatus }), skills = skills.Select(s => new { s.ResourceId, s.ContentHash, s.Revision }), importError, locations.StorageId, locations.Root, locations.ProjectRoot, workspaceRoots, workspaceFiles, workingDirectory, workspace?.PrimaryRootId, storageWrite, protectedRoots }));
        return new() { WorkspaceRoots = workspaceRoots, WorkspaceFiles = workspaceFiles, PrimaryRootId = workspace?.PrimaryRootId, WorkingDirectory = workingDirectory, ProtectedStorageRoots = protectedRoots.ToArray(), StorageId = locations.StorageId, StorageRoot = locations.Root, ProjectRoot = locations.ProjectRoot, ProjectStorageWrite = storageWrite, AgentDefinitionId = document.AgentDefinitionId, Settings = effective, SettingsHash = hash, McpServers = servers, ReadRoots = roots, SkillResources = skills, McpImportError = importError, ResourceDiagnostics = diagnostics };
    }
    public async Task<FrozenToolConfigurationDto> ResolveForRunAsync(Guid? projectId, IReadOnlyList<Guid> agentDefinitionIds, CancellationToken cancellationToken = default)
    {
        var sharedDocument = await settings.GetAsync(null, cancellationToken);
        var documents = new Dictionary<Guid, ToolSettingsDocumentDto>();
        foreach (var id in agentDefinitionIds.Distinct())
        {
            var agent = await settings.GetAsync(id, cancellationToken);
            var effective = ToolSettingsSchema.MergeAgent(sharedDocument.EffectiveSettings, agent.Settings);
            var fixedDocument = new ToolSettingsDocumentDto { AgentDefinitionId = id, Settings = agent.Settings, Revision = agent.Revision, EffectiveSettings = effective, SettingsHash = ToolSettingsSchema.Hash(effective), UpdatedAt = agent.UpdatedAt };
            documents[id] = fixedDocument;
        }
        var admitted = await CaptureResourcesAsync(projectId, documents.Values.Select(x => x.EffectiveSettings).Prepend(sharedDocument.EffectiveSettings).ToArray(), cancellationToken);
        var shared = await ResolveDocumentAsync(projectId, sharedDocument, cancellationToken, admitted: admitted);
        var contexts = new Dictionary<Guid, ToolExecutionContextDto>();
        foreach (var document in documents) contexts[document.Key] = await ResolveDocumentAsync(projectId, document.Value, cancellationToken, admitted: admitted);
        if (admitted.Workspace is { } workspace && services.GetService<IWorkspaceDefinitionProvider>()?.Read().ContentHash != workspace.ContentHash)
            throw new ConfigurationDocumentException("configuration_changed_during_admission", "Workspace changed during run admission. Retry the submission.");
        return new() { SharedRevision = sharedDocument.Revision, SharedContext = shared, AgentContexts = contexts, ConfigurationHash = ToolSettingsSchema.Hash(JsonSerializer.SerializeToElement(new { shared.SettingsHash, agents = contexts.OrderBy(x => x.Key).Select(x => new { id = x.Key, hash = x.Value.SettingsHash }) })) };
    }
    private async Task<AdmissionResources> CaptureResourcesAsync(Guid? projectId, IReadOnlyList<JsonElement> effectiveSettings, CancellationToken ct)
    {
        McpResourceCatalogSnapshot mcpResources;
        string? importError = null;
        try { mcpResources = await mcp.CaptureAsync(projectId, ct); }
        catch (ToolSettingsException ex) when (ex.Code == "mcp_import_failed") { mcpResources = new([], []); importError = ex.Message; }
        if (services.GetService<IManagedMcpProgramService>() is { } programs)
        {
            var frozenPrograms = new List<ToolMcpServerSnapshotDto>();
            foreach (var server in mcpResources.Servers)
                frozenPrograms.Add(await programs.ResolveForExecutionAsync(server, ct));
            mcpResources = new(mcpResources.Resources, frozenPrograms);
        }
        var skills = new ToolSkillCatalogSnapshot([], []);
        if (services.GetService<IToolSkillCatalog>() is { } catalog && services.GetService<IToolSkillResourceService>() is { } resourceService)
        {
            var resources = (await resourceService.ListAsync(projectId, includeBuiltins: true, ct)).Skills;
            var projectNames = resources.Where(x => x.Scope == "project").Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            var selected = new HashSet<Guid>();
            foreach (var effective in effectiveSettings.Where(x => x.GetProperty("skills").GetProperty("enabled").GetBoolean()))
            {
                var bindings = Bindings(effective, "skills", "resource_ids");
                foreach (var resource in resources.Where(x => x.Valid && x.Enabled && (bindings is not null ? bindings.Contains(x.ResourceId) : x.ShadowedBy is null && (x.Scope == "project" || !projectNames.Contains(x.Name)))))
                    selected.Add(resource.ResourceId);
            }
            skills = await catalog.CaptureSelectedAsync(projectId, selected, ct);
        }
        return new(mcpResources, skills, importError, services.GetService<IWorkspaceDefinitionProvider>()?.Read(requireAvailable: true));
    }
    private sealed record AdmissionResources(McpResourceCatalogSnapshot Mcp, ToolSkillCatalogSnapshot Skills, string? McpImportError, WorkspaceDefinition? Workspace);
    public async Task<ToolExecutionContextDto> MaterializeForCallAsync(ToolExecutionContextDto frozen, IReadOnlyList<string> allowedToolIds, CancellationToken cancellationToken = default, string? runId = null, string? toolId = null, string? workingDirectory = null)
    {
        workingDirectory ??= frozen.WorkingDirectory;
        var locations = services.GetRequiredService<IScopeStorageLocations>();
        if (frozen.StorageId != locations.StorageId || frozen.StorageRoot != locations.Root)
            throw new ToolSettingsException("frozen_storage_scope_mismatch", "This historical run belongs to a different storage scope. Start a new run in the current scope.", 409);
        workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? frozen.WorkingDirectory ?? locations.ProjectRoot : Path.GetFullPath(workingDirectory);
        if (!string.IsNullOrWhiteSpace(workingDirectory)) workingDirectory = WorkspacePathSpelling.Canonical(workingDirectory);
        var servers = new List<ToolMcpServerSnapshotDto>();
        foreach (var server in frozen.McpServers)
        {
            var env = new Dictionary<string,string?>();
            var headers = new Dictionary<string,string?>();
            if (toolId is null || toolId.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var secret in server.SecretReferences ?? new Dictionary<string,string>()) env[secret.Key] = await secrets.GetAsync(secret.Value, cancellationToken) ?? throw new ToolSettingsException("mcp_secret_unavailable", "An MCP resource secret is unavailable.", 503);
                foreach (var header in server.HeaderSecretReferences ?? new Dictionary<string,string>()) headers[header.Key] = await secrets.GetAsync(header.Value, cancellationToken) ?? throw new ToolSettingsException("mcp_secret_unavailable", "An MCP header secret is unavailable.", 503);
            }
            servers.Add(new() { ResourceId = server.ResourceId, Id = server.Id, Name = server.Name, Transport = server.Transport, Url = server.Url, Command = server.Command, Args = server.Args, Env = env, Headers = headers, HeaderSecretReferences = server.HeaderSecretReferences, Cwd = server.Cwd, Revision = server.Revision, ConfigurationHash = server.ConfigurationHash, ProgramRoot = server.ProgramRoot, ProgramHash = server.ProgramHash, ProgramStatus = server.ProgramStatus });
        }
        return new()
        {
            // A command's cwd can be any subdirectory of the frozen sources without
            // changing their identities or grant boundaries. Only the separate,
            // Core-owned worktree assignment rebinds the primary checkout.
            WorkspaceRoots = frozen.WorkspaceRoots.Select(root => root.Id == frozen.PrimaryRootId && workingDirectory is not null
                && !frozen.WorkspaceRoots.Any(source => ContainsDirectory(source.Path, workingDirectory))
                ? new ToolWorkspaceRootDto { Id = root.Id, Path = workingDirectory } : root).ToArray(), PrimaryRootId = frozen.PrimaryRootId,
            WorkspaceFiles = frozen.WorkspaceFiles,
            ProtectedStorageRoots = frozen.ProtectedStorageRoots.Concat(services.GetService<IProtectedStorageRoots>()?.Roots ?? []).Distinct().ToArray(),
            WorkingDirectory = workingDirectory, StorageId = frozen.StorageId, StorageRoot = frozen.StorageRoot,
            ProjectRoot = frozen.ProjectRoot, ProjectStorageWrite = frozen.ProjectStorageWrite,
            RunId = runId ?? frozen.RunId, AgentDefinitionId = frozen.AgentDefinitionId, SettingsHash = frozen.SettingsHash,
            Settings = frozen.Settings.Clone(), McpServers = servers, ReadRoots = frozen.ReadRoots,
            AllowedToolIds = allowedToolIds.Where(id => ToolSettingsSchema.IsEnabled(frozen.Settings,id)).ToArray(),
            SkillResources = frozen.SkillResources, McpImportError = frozen.McpImportError
        };
    }
    private static bool ContainsDirectory(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(WorkspacePathSpelling.Canonical(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(root, path, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
    private static IReadOnlyList<Guid>? Bindings(JsonElement settings, string section, string name)
    {
        var value = settings.GetProperty(section).GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.EnumerateArray().Select(x => x.GetGuid()).ToArray();
    }
}
