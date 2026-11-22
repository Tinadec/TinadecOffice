using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Skills;

/// <summary>
/// Discovers external CLI tool configurations (<c>claude_code</c>, <c>codex</c>,
/// <c>z_code</c>, <c>minimax_code</c>, <c>pi</c>) from user home and workspace roots.
/// Always redacts environment/header secrets on discovery; migrates secrets into
/// <see cref="ISecretStore"/> via <see cref="IMcpServerService"/> on opted-in import
/// and defaults untrusted stdio MCP commands to <c>enabled: false</c>.
/// </summary>
public sealed partial class ExternalCliDiscoveryService(
    IMcpServerService mcpServers,
    IToolSkillResourceService skillResources,
    ISessionLocator sessions,
    StoragePaths storage,
    IConfiguration configuration) : IExternalCliDiscoveryService
{
    private const int MaxSkillsPerRoot = 128;
    private const int MaxCommandsPerRoot = 128;
    private const int MaxSkillAssets = 256;
    private const long MaxSkillPackageBytes = 16 * 1024 * 1024;
    private const int MaxPreviewChars = 1200;

    private sealed record InternalDiscoveredItem(
        ExternalCliDiscoveredItemDto Dto,
        McpResourceWriteDto? McpWrite,
        string? SkillDirPath,
        string? SkillContent,
        string? CommandContent);

    public async Task<ExternalCliDiscoveryDto> DiscoverAsync(
        Guid? projectId = null,
        IReadOnlyList<string>? ecosystems = null,
        CancellationToken cancellationToken = default)
    {
        var (userHome, workspaceRoot, items, statuses) =
            await ScanInternalAsync(projectId, ecosystems, cancellationToken).ConfigureAwait(false);
        return new ExternalCliDiscoveryDto
        {
            UserHome = userHome,
            WorkspaceRoot = workspaceRoot,
            Ecosystems = statuses,
            Items = items.Select(x => x.Dto).ToList(),
        };
    }

    public async Task<ExternalCliImportResultDto> ImportAsync(
        ExternalCliImportRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ItemIds is null || request.ItemIds.Count == 0)
            throw new ToolSettingsException("invalid_request", "Select at least one item_id to import.");

        var scope = string.Equals(request.Scope, "project", StringComparison.OrdinalIgnoreCase) ? "project" : "shared";
        if (scope == "project" && (request.ProjectId is null || request.ProjectId == Guid.Empty))
            throw new ToolSettingsException("project_required", "A project_id is required when scope is 'project'.");

        var (_, workspaceRoot, discovered, _) =
            await ScanInternalAsync(request.ProjectId, ecosystems: null, cancellationToken).ConfigureAwait(false);
        var byId = discovered.ToDictionary(x => x.Dto.ItemId, StringComparer.Ordinal);

        var installedSkills = new List<ToolSkillResourceDto>();
        var installedMcps = new List<McpResourceDto>();
        var installedCommands = new List<ExternalCliImportedCommandDto>();
        var skipped = new List<ExternalCliSkippedItemDto>();

        var existingMcps = await mcpServers.ListConfiguredAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var mcpByServerId = existingMcps
            .Where(x => x.ProjectId == request.ProjectId)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        var existingSkills = (await skillResources.ListAsync(request.ProjectId, includeBuiltins: false, cancellationToken).ConfigureAwait(false)).Skills;
        var skillByName = existingSkills
            .Where(x => string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var itemId in request.ItemIds.Distinct(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(itemId, out var item))
            {
                skipped.Add(new ExternalCliSkippedItemDto { ItemId = itemId, Reason = "Discovered item not found." });
                continue;
            }

            try
            {
                switch (item.Dto.Kind)
                {
                    case "mcp-server":
                    {
                        var write = item.McpWrite!;
                        var enabled = string.Equals(write.Transport, "stdio", StringComparison.OrdinalIgnoreCase)
                            ? request.EnableStdioMcp
                            : true;
                        var prepared = new McpResourceWriteDto
                        {
                            ProjectId = request.ProjectId,
                            Id = write.Id,
                            Name = write.Name,
                            Enabled = enabled,
                            Transport = write.Transport,
                            Url = write.Url,
                            Command = write.Command,
                            Args = write.Args,
                            Env = write.Env,
                            Headers = write.Headers,
                            Cwd = write.Cwd,
                            ImportSource = item.Dto.Ecosystem,
                        };

                        if (mcpByServerId.TryGetValue(prepared.Id, out var existing))
                        {
                            if (!request.Overwrite)
                            {
                                skipped.Add(new ExternalCliSkippedItemDto { ItemId = itemId, Reason = $"MCP server '{prepared.Id}' already exists." });
                                continue;
                            }
                            var updated = await mcpServers.UpdateAsync(existing.ResourceId.ToString("N"), prepared, existing.Revision, cancellationToken).ConfigureAwait(false);
                            installedMcps.Add(updated);
                            mcpByServerId[updated.Id] = updated;
                        }
                        else
                        {
                            var created = await mcpServers.CreateAsync(prepared, 0, cancellationToken).ConfigureAwait(false);
                            installedMcps.Add(created);
                            mcpByServerId[created.Id] = created;
                        }
                        break;
                    }
                    case "skill":
                    {
                        var normalizedName = NormalizeSkillName(item.Dto.Name);
                        if (skillByName.TryGetValue(normalizedName, out var existing))
                        {
                            if (!request.Overwrite)
                            {
                                skipped.Add(new ExternalCliSkippedItemDto { ItemId = itemId, Reason = $"Skill '{normalizedName}' already exists." });
                                continue;
                            }
                            if (existing.ReadOnly)
                            {
                                skipped.Add(new ExternalCliSkippedItemDto { ItemId = itemId, Reason = $"Skill '{normalizedName}' is read-only." });
                                continue;
                            }
                        }

                        var content = EnsureSkillFrontmatter(item.SkillContent!, normalizedName, item.Dto.Description);
                        var files = item.SkillDirPath is not null
                            ? ReadSkillAssets(item.SkillDirPath)
                            : new Dictionary<string, string>(StringComparer.Ordinal);
                        var saved = await skillResources.SaveAsync(
                            existing?.ResourceId,
                            request.ProjectId,
                            new ToolSkillWriteDto
                            {
                                Scope = scope,
                                ProjectId = request.ProjectId,
                                Name = normalizedName,
                                Content = content,
                                Enabled = true,
                                Files = files,
                                ReplaceFiles = true,
                                ExpectedFileHash = existing?.FileHash,
                                Source = item.Dto.Ecosystem,
                            },
                            existing?.Revision ?? 0,
                            cancellationToken).ConfigureAwait(false);
                        installedSkills.Add(saved);
                        skillByName[normalizedName] = saved;
                        break;
                    }
                    case "command":
                    {
                        var targetRoot = !string.IsNullOrWhiteSpace(workspaceRoot)
                            ? workspaceRoot
                            : storage.Locations.Root;
                        var commandsDir = Path.GetFullPath(Path.Combine(targetRoot, ".tinadec", "commands"));
                        Directory.CreateDirectory(commandsDir);
                        var safeFileName = NormalizeCommandFileName(item.Dto.Name) + ".md";
                        var targetPath = Path.GetFullPath(Path.Combine(commandsDir, safeFileName));
                        if (!WorkspaceSkillDiscovery.IsContained(commandsDir, targetPath))
                            throw new ToolSettingsException("invalid_command_path", "Command path escapes .tinadec/commands.");
                        if (File.Exists(targetPath) && !request.Overwrite)
                        {
                            skipped.Add(new ExternalCliSkippedItemDto { ItemId = itemId, Reason = $"Command '{safeFileName}' already exists." });
                            continue;
                        }
                        await File.WriteAllTextAsync(targetPath, item.CommandContent!, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                        installedCommands.Add(new ExternalCliImportedCommandDto
                        {
                            Name = Path.GetFileNameWithoutExtension(safeFileName),
                            Path = targetPath,
                            Ecosystem = item.Dto.Ecosystem,
                        });
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is ToolSettingsException or IOException or UnauthorizedAccessException)
            {
                skipped.Add(new ExternalCliSkippedItemDto { ItemId = itemId, Reason = ex.Message });
            }
        }

        return new ExternalCliImportResultDto
        {
            InstalledSkills = installedSkills,
            InstalledMcpServers = installedMcps,
            InstalledCommands = installedCommands,
            Skipped = skipped,
        };
    }

    private async Task<(
        string UserHome,
        string? WorkspaceRoot,
        List<InternalDiscoveredItem> Items,
        List<ExternalCliEcosystemStatusDto> Statuses)>
        ScanInternalAsync(
            Guid? projectId,
            IReadOnlyList<string>? ecosystems,
            CancellationToken cancellationToken)
    {
        var userHome = configuration["TinadecTools:ExternalCliUserHome"];
        if (string.IsNullOrWhiteSpace(userHome))
            userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        userHome = string.IsNullOrWhiteSpace(userHome) ? Directory.GetCurrentDirectory() : Path.GetFullPath(userHome);

        string? workspaceRoot = null;
        if (projectId is { } pid && pid != Guid.Empty)
            workspaceRoot = (await sessions.FindProjectAsync(pid, cancellationToken).ConfigureAwait(false))?.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            workspaceRoot = configuration["TinadecTools:DefaultWorkspaceRoot"];
        if (!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
            workspaceRoot = Path.GetFullPath(workspaceRoot);
        else
            workspaceRoot = null;

        var existingMcpIds = (await mcpServers.ListConfiguredAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingSkillNames = (await skillResources.ListAsync(projectId, includeBuiltins: false, cancellationToken).ConfigureAwait(false))
            .Skills
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filter = ecosystems is { Count: > 0 }
            ? ecosystems.Select(x => x.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var items = new List<InternalDiscoveredItem>();
        var statuses = new List<ExternalCliEcosystemStatusDto>();
        var seenItemIds = new HashSet<string>(StringComparer.Ordinal);

        void AddItem(InternalDiscoveredItem item)
        {
            if (seenItemIds.Add(item.Dto.ItemId))
                items.Add(item);
        }

        // 1. Claude Code
        if (filter is null || filter.Contains("claude_code"))
        {
            var scanned = new List<string>();
            var before = items.Count;
            ScanJsonMcpFile("claude_code", Path.Combine(userHome, ".claude.json"), scanned, existingMcpIds, AddItem);
            ScanJsonMcpFile("claude_code", Path.Combine(userHome, ".claude", "settings.json"), scanned, existingMcpIds, AddItem);
            ScanSkillsDirectory("claude_code", Path.Combine(userHome, ".claude", "skills"), scanned, existingSkillNames, AddItem);
            ScanCommandsDirectory("claude_code", Path.Combine(userHome, ".claude", "commands"), workspaceRoot, storage.Locations.Root, scanned, AddItem);
            if (workspaceRoot is not null)
            {
                ScanJsonMcpFile("claude_code", Path.Combine(workspaceRoot, ".mcp.json"), scanned, existingMcpIds, AddItem);
                ScanJsonMcpFile("claude_code", Path.Combine(workspaceRoot, ".claude", "settings.json"), scanned, existingMcpIds, AddItem);
                ScanSkillsDirectory("claude_code", Path.Combine(workspaceRoot, ".claude", "skills"), scanned, existingSkillNames, AddItem);
                ScanCommandsDirectory("claude_code", Path.Combine(workspaceRoot, ".claude", "commands"), workspaceRoot, storage.Locations.Root, scanned, AddItem);
            }
            statuses.Add(new ExternalCliEcosystemStatusDto
            {
                Id = "claude_code",
                Label = "Claude Code",
                Detected = items.Count > before || scanned.Count > 0,
                PathsScanned = scanned,
            });
        }

        // 2. Codex
        if (filter is null || filter.Contains("codex"))
        {
            var scanned = new List<string>();
            var before = items.Count;
            ScanCodexTomlFile("codex", Path.Combine(userHome, ".codex", "config.toml"), scanned, existingMcpIds, AddItem);
            ScanSkillsDirectory("codex", Path.Combine(userHome, ".codex", "skills"), scanned, existingSkillNames, AddItem);
            ScanSkillsDirectory("codex", Path.Combine(userHome, ".agents", "skills"), scanned, existingSkillNames, AddItem);
            if (workspaceRoot is not null)
            {
                ScanCodexTomlFile("codex", Path.Combine(workspaceRoot, ".codex", "config.toml"), scanned, existingMcpIds, AddItem);
                ScanSkillsDirectory("codex", Path.Combine(workspaceRoot, ".codex", "skills"), scanned, existingSkillNames, AddItem);
            }
            statuses.Add(new ExternalCliEcosystemStatusDto
            {
                Id = "codex",
                Label = "OpenAI Codex",
                Detected = items.Count > before || scanned.Count > 0,
                PathsScanned = scanned,
            });
        }

        // 3. Z Code
        if (filter is null || filter.Contains("z_code"))
        {
            var scanned = new List<string>();
            var before = items.Count;
            ScanJsonMcpFile("z_code", Path.Combine(userHome, ".zcode", "mcp.json"), scanned, existingMcpIds, AddItem);
            ScanJsonMcpFile("z_code", Path.Combine(userHome, ".zcode", "config.json"), scanned, existingMcpIds, AddItem);
            ScanSkillsDirectory("z_code", Path.Combine(userHome, ".zcode", "skills"), scanned, existingSkillNames, AddItem);
            ScanCommandsDirectory("z_code", Path.Combine(userHome, ".zcode", "commands"), workspaceRoot, storage.Locations.Root, scanned, AddItem);
            if (workspaceRoot is not null)
            {
                ScanJsonMcpFile("z_code", Path.Combine(workspaceRoot, ".zcode", "mcp.json"), scanned, existingMcpIds, AddItem);
                ScanSkillsDirectory("z_code", Path.Combine(workspaceRoot, ".zcode", "skills"), scanned, existingSkillNames, AddItem);
                ScanCommandsDirectory("z_code", Path.Combine(workspaceRoot, ".zcode", "commands"), workspaceRoot, storage.Locations.Root, scanned, AddItem);
            }
            statuses.Add(new ExternalCliEcosystemStatusDto
            {
                Id = "z_code",
                Label = "Z Code",
                Detected = items.Count > before || scanned.Count > 0,
                PathsScanned = scanned,
            });
        }

        // 4. MiniMax Code
        if (filter is null || filter.Contains("minimax_code"))
        {
            var scanned = new List<string>();
            var before = items.Count;
            ScanJsonMcpFile("minimax_code", Path.Combine(userHome, ".minimax", "mcp.json"), scanned, existingMcpIds, AddItem);
            ScanJsonMcpFile("minimax_code", Path.Combine(userHome, ".minimax", "config.json"), scanned, existingMcpIds, AddItem);
            ScanJsonMcpFile("minimax_code", Path.Combine(userHome, ".minimax-code", "mcp.json"), scanned, existingMcpIds, AddItem);
            ScanJsonMcpFile("minimax_code", Path.Combine(userHome, ".minimax-code", "config.json"), scanned, existingMcpIds, AddItem);
            ScanSkillsDirectory("minimax_code", Path.Combine(userHome, ".minimax", "skills"), scanned, existingSkillNames, AddItem);
            ScanSkillsDirectory("minimax_code", Path.Combine(userHome, ".minimax-code", "skills"), scanned, existingSkillNames, AddItem);
            ScanCommandsDirectory("minimax_code", Path.Combine(userHome, ".minimax", "commands"), workspaceRoot, storage.Locations.Root, scanned, AddItem);
            if (workspaceRoot is not null)
            {
                ScanJsonMcpFile("minimax_code", Path.Combine(workspaceRoot, ".minimax", "mcp.json"), scanned, existingMcpIds, AddItem);
                ScanSkillsDirectory("minimax_code", Path.Combine(workspaceRoot, ".minimax", "skills"), scanned, existingSkillNames, AddItem);
            }
            statuses.Add(new ExternalCliEcosystemStatusDto
            {
                Id = "minimax_code",
                Label = "MiniMax Code",
                Detected = items.Count > before || scanned.Count > 0,
                PathsScanned = scanned,
            });
        }

        // 5. Pi (派)
        if (filter is null || filter.Contains("pi"))
        {
            var scanned = new List<string>();
            var before = items.Count;
            ScanJsonMcpFile("pi", Path.Combine(userHome, ".pi", "agent", "mcp.json"), scanned, existingMcpIds, AddItem);
            ScanJsonMcpFile("pi", Path.Combine(userHome, ".pi", "agent", "settings.json"), scanned, existingMcpIds, AddItem);
            ScanSkillsDirectory("pi", Path.Combine(userHome, ".pi", "agent", "skills"), scanned, existingSkillNames, AddItem);
            ScanCommandsDirectory("pi", Path.Combine(userHome, ".pi", "agent", "prompts"), workspaceRoot, storage.Locations.Root, scanned, AddItem);
            if (workspaceRoot is not null)
            {
                ScanJsonMcpFile("pi", Path.Combine(workspaceRoot, ".pi", "mcp.json"), scanned, existingMcpIds, AddItem);
                ScanSkillsDirectory("pi", Path.Combine(workspaceRoot, ".pi", "skills"), scanned, existingSkillNames, AddItem);
                ScanCommandsDirectory("pi", Path.Combine(workspaceRoot, ".pi", "prompts"), workspaceRoot, storage.Locations.Root, scanned, AddItem);
            }
            statuses.Add(new ExternalCliEcosystemStatusDto
            {
                Id = "pi",
                Label = "Pi Coding Agent",
                Detected = items.Count > before || scanned.Count > 0,
                PathsScanned = scanned,
            });
        }

        // 6-8. Antigravity, Gemini CLI, Qwen Code (#31)
        ScanEcosystem("antigravity", "Antigravity (agy)",
            homeMcp: [Path.Combine(userHome, ".gemini", "antigravity", "mcp_config.json"), Path.Combine(userHome, ".gemini", "antigravity", "settings.json")],
            wsMcp: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".gemini", "antigravity", "mcp_config.json")],
            homeSkills: [Path.Combine(userHome, ".gemini", "antigravity", "skills")],
            wsSkills: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".agent", "skills"), Path.Combine(workspaceRoot, ".gemini", "antigravity", "skills")],
            homeCmds: [Path.Combine(userHome, ".gemini", "antigravity", "global_workflows"), Path.Combine(userHome, ".gemini", "antigravity", "commands")],
            wsCmds: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".agent", "workflows")]);

        ScanEcosystem("gemini_cli", "Gemini CLI",
            homeMcp: [Path.Combine(userHome, ".gemini", "settings.json"), Path.Combine(userHome, ".gemini", "mcp.json")],
            wsMcp: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".gemini", "settings.json"), Path.Combine(workspaceRoot, ".gemini", "mcp.json")],
            homeSkills: [Path.Combine(userHome, ".gemini", "skills")],
            wsSkills: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".gemini", "skills")],
            homeCmds: [Path.Combine(userHome, ".gemini", "commands")],
            wsCmds: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".gemini", "commands")]);

        ScanEcosystem("qwen_code", "Qwen Code",
            homeMcp: [Path.Combine(userHome, ".qwen", "settings.json"), Path.Combine(userHome, ".qwen", "mcp.json")],
            wsMcp: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".qwen", "settings.json"), Path.Combine(workspaceRoot, ".qwen", "mcp.json")],
            homeSkills: [Path.Combine(userHome, ".qwen", "skills")],
            wsSkills: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".qwen", "skills")],
            homeCmds: [Path.Combine(userHome, ".qwen", "commands")],
            wsCmds: workspaceRoot is null ? [] : [Path.Combine(workspaceRoot, ".qwen", "commands")]);

        return (userHome, workspaceRoot, items, statuses);

        void ScanEcosystem(
            string id,
            string label,
            string[] homeMcp,
            string[] wsMcp,
            string[] homeSkills,
            string[] wsSkills,
            string[] homeCmds,
            string[] wsCmds)
        {
            if (filter is not null && !filter.Contains(id)) return;
            var scanned = new List<string>();
            var before = items.Count;
            foreach (var p in homeMcp) ScanJsonMcpFile(id, p, scanned, existingMcpIds, AddItem);
            foreach (var p in wsMcp) ScanJsonMcpFile(id, p, scanned, existingMcpIds, AddItem);
            foreach (var d in homeSkills) ScanSkillsDirectory(id, d, scanned, existingSkillNames, AddItem);
            foreach (var d in wsSkills) ScanSkillsDirectory(id, d, scanned, existingSkillNames, AddItem);
            foreach (var d in homeCmds) ScanCommandsDirectory(id, d, workspaceRoot, storage.Locations.Root, scanned, AddItem);
            foreach (var d in wsCmds) ScanCommandsDirectory(id, d, workspaceRoot, storage.Locations.Root, scanned, AddItem);
            statuses.Add(new ExternalCliEcosystemStatusDto
            {
                Id = id,
                Label = label,
                Detected = items.Count > before || scanned.Count > 0,
                PathsScanned = scanned,
            });
        }
    }

    private static void ScanJsonMcpFile(
        string ecosystem,
        string path,
        List<string> scanned,
        HashSet<string> existingMcpIds,
        Action<InternalDiscoveredItem> add)
    {
        if (!File.Exists(path)) return;
        scanned.Add(path);
        try
        {
            var raw = File.ReadAllText(path, Encoding.UTF8);
            var entries = McpConfigParser.ParseConfigEntries(raw, null);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                add(BuildMcpItem(ecosystem, path, entry, existingMcpIds.Contains(entry.Id)));
            }
        }
        catch
        {
            // Tolerant discovery: skip malformed external config files cleanly.
        }
    }

    private static void ScanCodexTomlFile(
        string ecosystem,
        string path,
        List<string> scanned,
        HashSet<string> existingMcpIds,
        Action<InternalDiscoveredItem> add)
    {
        if (!File.Exists(path)) return;
        scanned.Add(path);
        try
        {
            var raw = File.ReadAllText(path, Encoding.UTF8);
            var entries = McpConfigParser.ParseCodexToml(raw);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                add(BuildMcpItem(ecosystem, path, entry, existingMcpIds.Contains(entry.Id)));
            }
        }
        catch
        {
            // Tolerant discovery: skip malformed TOML config cleanly.
        }
    }

    private static InternalDiscoveredItem BuildMcpItem(
        string ecosystem,
        string sourcePath,
        ParsedMcpEntry entry,
        bool alreadyImported)
    {
        var envKeys = (entry.Env?.Keys ?? Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var headerKeys = (entry.Headers?.Keys ?? Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var redactedArgs = entry.Args.Select(RedactInlineSecrets).ToArray();
        var redactedUrl = entry.Url is null ? null : RedactUrlSecrets(entry.Url);
        var hasSecrets = envKeys.Length > 0 || headerKeys.Length > 0 || redactedArgs.Any(a => a.Contains("********", StringComparison.Ordinal))
            || (redactedUrl?.Contains("********", StringComparison.Ordinal) == true);
        var isStdio = string.Equals(entry.Transport, "stdio", StringComparison.OrdinalIgnoreCase);

        var dto = new ExternalCliDiscoveredItemDto
        {
            ItemId = $"{ecosystem}:mcp:{entry.Id}",
            Ecosystem = ecosystem,
            Kind = "mcp-server",
            Name = entry.Id,
            DisplayName = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
            Description = isStdio
                ? $"stdio: {entry.Command}"
                : $"{entry.Transport}: {redactedUrl}",
            SourcePath = sourcePath,
            Transport = entry.Transport,
            Command = entry.Command,
            Args = redactedArgs,
            Url = redactedUrl,
            EnvKeys = envKeys,
            HeaderKeys = headerKeys,
            HasSecrets = hasSecrets,
            // Untrusted stdio MCP commands default to disabled on discovery/import until explicitly enabled.
            DefaultEnabled = !isStdio,
            AlreadyImported = alreadyImported,
            ContentPreview = null,
        };

        var write = new McpResourceWriteDto
        {
            Id = entry.Id,
            Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
            Enabled = !isStdio,
            Transport = entry.Transport,
            Url = entry.Url,
            Command = entry.Command ?? string.Empty,
            Args = entry.Args,
            Env = entry.Env,
            Headers = entry.Headers,
            Cwd = entry.Cwd,
            ImportSource = ecosystem,
        };

        return new InternalDiscoveredItem(dto, write, null, null, null);
    }

    private static void ScanSkillsDirectory(
        string ecosystem,
        string skillsRoot,
        List<string> scanned,
        HashSet<string> existingSkillNames,
        Action<InternalDiscoveredItem> add)
    {
        if (!Directory.Exists(skillsRoot)) return;
        if ((File.GetAttributes(skillsRoot) & FileAttributes.ReparsePoint) != 0) return;
        scanned.Add(skillsRoot);

        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(skillsRoot))
        {
            if (count >= MaxSkillsPerRoot) break;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            count++;

            try
            {
                var content = File.ReadAllText(skillFile, Encoding.UTF8);
                var rawDirName = Path.GetFileName(dir);
                var normalizedName = NormalizeSkillName(rawDirName);
                var description = ExtractSkillDescription(content) ?? $"Imported from {ecosystem} ({rawDirName})";
                var dto = new ExternalCliDiscoveredItemDto
                {
                    ItemId = $"{ecosystem}:skill:{normalizedName}",
                    Ecosystem = ecosystem,
                    Kind = "skill",
                    Name = normalizedName,
                    DisplayName = rawDirName,
                    Description = description,
                    SourcePath = skillFile,
                    HasSecrets = false,
                    DefaultEnabled = true,
                    AlreadyImported = existingSkillNames.Contains(normalizedName),
                    ContentPreview = BoundText(content, MaxPreviewChars),
                };
                add(new InternalDiscoveredItem(dto, null, dir, content, null));
            }
            catch
            {
                // Skip unreadable skill files cleanly.
            }
        }
    }

    private static void ScanCommandsDirectory(
        string ecosystem,
        string commandsRoot,
        string? workspaceRoot,
        string storageRoot,
        List<string> scanned,
        Action<InternalDiscoveredItem> add)
    {
        if (!Directory.Exists(commandsRoot)) return;
        if ((File.GetAttributes(commandsRoot) & FileAttributes.ReparsePoint) != 0) return;
        scanned.Add(commandsRoot);

        var targetRoot = !string.IsNullOrWhiteSpace(workspaceRoot) ? workspaceRoot : storageRoot;
        var tinadecCommandsDir = Path.Combine(targetRoot, ".tinadec", "commands");
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(commandsRoot, "*.md"))
        {
            if (count >= MaxCommandsPerRoot) break;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            count++;

            try
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                var rawName = Path.GetFileNameWithoutExtension(file);
                var normalized = NormalizeCommandFileName(rawName);
                var description = ExtractSkillDescription(content) ?? $"Slash command /{normalized} from {ecosystem}";
                var alreadyImported = File.Exists(Path.Combine(tinadecCommandsDir, normalized + ".md"));

                var dto = new ExternalCliDiscoveredItemDto
                {
                    ItemId = $"{ecosystem}:command:{normalized}",
                    Ecosystem = ecosystem,
                    Kind = "command",
                    Name = normalized,
                    DisplayName = $"/{normalized}",
                    Description = description,
                    SourcePath = file,
                    HasSecrets = false,
                    DefaultEnabled = true,
                    AlreadyImported = alreadyImported,
                    ContentPreview = BoundText(content, MaxPreviewChars),
                };
                add(new InternalDiscoveredItem(dto, null, null, null, content));
            }
            catch
            {
                // Skip unreadable command files cleanly.
            }
        }
    }

    internal static Dictionary<string, string> ReadSkillAssets(string skillDir)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(skillDir);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new ToolSettingsException("skill_assets_symlink", "Skill packages cannot contain symbolic links.");

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is ".git" or "node_modules") continue;
                pending.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new ToolSettingsException("skill_assets_symlink", "Skill packages cannot contain symbolic links.");
                if (!WorkspaceSkillDiscovery.IsContained(skillDir, file))
                    throw new ToolSettingsException("skill_path_escape", "Skill asset escapes directory root.");

                var relative = Path.GetRelativePath(skillDir, file).Replace('\\', '/');
                if (string.Equals(relative, "SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count >= MaxSkillAssets)
                    throw new ToolSettingsException("skill_package_budget", "Skill packages are limited to 256 files.");

                var bytes = File.ReadAllBytes(file);
                totalBytes += bytes.LongLength;
                if (totalBytes > MaxSkillPackageBytes)
                    throw new ToolSettingsException("skill_package_budget", "Skill packages are limited to 16 MiB.");
                files[relative] = Convert.ToBase64String(bytes);
            }
        }

        return files;
    }

    internal static string NormalizeSkillName(string raw)
    {
        var cleaned = Regex.Replace((raw ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9\-]+", "-").Trim('-');
        if (cleaned.Length == 0) cleaned = "imported-skill";
        if (cleaned.Length > 64) cleaned = cleaned[..64].TrimEnd('-');
        return cleaned;
    }

    internal static string NormalizeCommandFileName(string raw)
    {
        var cleaned = Regex.Replace((raw ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9\-_]+", "-").Trim('-', '_');
        if (cleaned.Length == 0) cleaned = "imported-command";
        if (cleaned.Length > 64) cleaned = cleaned[..64];
        return cleaned;
    }

    internal static string EnsureSkillFrontmatter(string rawContent, string name, string? fallbackDescription)
    {
        if (WorkspaceSkillPolicy.TryRead(rawContent, name, WorkspaceSkillPolicy.RelativePathFor(name), out _, out _))
            return rawContent;

        var desc = (fallbackDescription ?? $"Imported skill {name}").Replace("\"", "'", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (desc.Length == 0) desc = $"Imported skill {name}";

        var body = rawContent;
        if (rawContent.TrimStart().StartsWith("---", StringComparison.Ordinal))
        {
            var match = FrontmatterBlockRegex().Match(rawContent);
            if (match.Success)
                body = rawContent[(match.Index + match.Length)..].TrimStart('\r', '\n');
        }

        return $"---\nname: {name}\ndescription: \"{desc}\"\n---\n\n{body}";
    }

    private static string? ExtractSkillDescription(string content)
    {
        var match = FrontmatterDescRegex().Match(content);
        if (match.Success)
        {
            var val = match.Groups[1].Value.Trim().Trim('"', '\'');
            if (val.Length > 0) return BoundText(val, 240);
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "---" || trimmed.StartsWith('#')) continue;
            return BoundText(trimmed, 240);
        }
        return null;
    }

    private static string RedactInlineSecrets(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return arg;
        var eq = arg.IndexOf('=');
        if (eq > 0)
        {
            var key = arg[..eq];
            if (SecretKeyPattern().IsMatch(key))
                return key + "=********";
        }
        return SecretValuePattern().IsMatch(arg) ? "********" : arg;
    }

    private static string RedactUrlSecrets(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)) return url;

        var builder = new UriBuilder(uri);
        if (!string.IsNullOrEmpty(builder.Password)) builder.Password = "********";
        if (!string.IsNullOrEmpty(builder.UserName) && !string.IsNullOrEmpty(uri.UserInfo)) builder.UserName = "********";
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < pairs.Length; i++)
            {
                var eq = pairs[i].IndexOf('=');
                if (eq > 0 && SecretKeyPattern().IsMatch(pairs[i][..eq]))
                    pairs[i] = pairs[i][..(eq + 1)] + "********";
            }
            builder.Query = string.Join('&', pairs);
        }
        return builder.Uri.ToString();
    }

    private static string BoundText(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "...";

    [GeneratedRegex(@"^\s*---\r?\n[\s\S]*?\r?\n---\r?\n?", RegexOptions.Multiline)]
    private static partial Regex FrontmatterBlockRegex();

    [GeneratedRegex(@"^description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FrontmatterDescRegex();

    [GeneratedRegex(@"(?:key|secret|token|password|passwd|pwd|auth|bearer|credential)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyPattern();

    [GeneratedRegex(@"^(?:sk-[A-Za-z0-9_\-]{10,}|ghp_[A-Za-z0-9]{10,}|Bearer\s+.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValuePattern();
}
