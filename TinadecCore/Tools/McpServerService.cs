using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Canonical Core MCP server management service unifying <c>/api/v1/mcp/servers</c>,
/// <c>/api/v1/tools/mcp/servers</c>, and <c>tinadec_mcp_apply</c> over
/// <see cref="IMcpResourceRegistry"/> and <see cref="ISecretStore"/>.
/// </summary>
public sealed class McpServerService(
    IMcpResourceRegistry registry,
    IToolConfigurationResolver resolver,
    IToolProvider provider,
    ISessionLocator sessions,
    IConfiguration configuration) : IMcpServerService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public Task<IReadOnlyList<McpResourceDto>> ListConfiguredAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default) =>
        registry.ListAsync(projectId, cancellationToken);

    public async Task<McpResourceDto?> GetAsync(
        string serverIdOrResourceId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverIdOrResourceId))
            return null;

        if (Guid.TryParse(serverIdOrResourceId.Trim(), out var resourceId))
            return await registry.GetAsync(resourceId, cancellationToken).ConfigureAwait(false);

        var rows = await registry.ListAsync(projectId, cancellationToken).ConfigureAwait(false);
        return rows
            .Where(x => string.Equals(x.Id, serverIdOrResourceId.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => projectId is not null && x.ProjectId == projectId)
            .FirstOrDefault();
    }

    public Task<McpResourceDto> CreateAsync(
        McpResourceWriteDto input,
        long expectedRevision = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return registry.SaveAsync(null, input, expectedRevision, cancellationToken);
    }

    public async Task<McpImportJsonResultDto> ImportJsonAsync(
        McpImportJsonRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = McpConfigParser.ParseConfigEntries(request.Json, request.Config);
        if (entries.Count == 0)
            throw new ToolSettingsException("invalid_mcp_json", "No MCP server entries were found in the supplied JSON or TOML snippet.");

        var existingRows = await registry.ListAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var byServerId = existingRows
            .Where(x => x.ProjectId == request.ProjectId)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        var imported = new List<McpResourceDto>();
        var skipped = new List<McpImportJsonSkippedDto>();
        var importSource = string.IsNullOrWhiteSpace(request.ImportSource) ? "import_json" : request.ImportSource.Trim();

        foreach (var parsed in entries)
        {
            if (string.IsNullOrWhiteSpace(parsed.Id))
            {
                skipped.Add(new McpImportJsonSkippedDto { Id = "(unnamed)", Reason = "Missing server id." });
                continue;
            }

            if (byServerId.TryGetValue(parsed.Id, out var existing) && !request.Overwrite)
            {
                skipped.Add(new McpImportJsonSkippedDto
                {
                    Id = parsed.Id,
                    Reason = $"Server '{parsed.Id}' already exists (set overwrite=true to update).",
                });
                continue;
            }

            var enabled = parsed.Enabled ?? request.DefaultEnabled ?? true;
            var write = new McpResourceWriteDto
            {
                ProjectId = request.ProjectId,
                Id = parsed.Id,
                Name = string.IsNullOrWhiteSpace(parsed.Name) ? parsed.Id : parsed.Name,
                Enabled = enabled,
                Transport = parsed.Transport,
                Url = parsed.Url,
                Command = parsed.Command ?? string.Empty,
                Args = parsed.Args,
                Env = parsed.Env,
                Headers = parsed.Headers,
                Cwd = parsed.Cwd,
                ImportSource = importSource,
            };

            try
            {
                var saved = await registry.SaveAsync(
                    existing?.ResourceId,
                    write,
                    existing?.Revision ?? 0,
                    cancellationToken).ConfigureAwait(false);
                imported.Add(saved);
                byServerId[saved.Id] = saved;
            }
            catch (ToolSettingsException ex)
            {
                skipped.Add(new McpImportJsonSkippedDto { Id = parsed.Id, Reason = ex.Message });
            }
        }

        return new McpImportJsonResultDto
        {
            Imported = imported,
            Skipped = skipped,
            ImportedCount = imported.Count,
            SkippedCount = skipped.Count,
        };
    }

    public async Task<McpResourceDto> UpdateAsync(
        string serverIdOrResourceId,
        McpResourceWriteDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var existing = await ResolveRequiredAsync(serverIdOrResourceId, input.ProjectId, cancellationToken).ConfigureAwait(false);
        var revision = expectedRevision ?? existing.Revision;
        var merged = new McpResourceWriteDto
        {
            ProjectId = existing.ProjectId,
            Id = string.IsNullOrWhiteSpace(input.Id) ? existing.Id : input.Id,
            Name = string.IsNullOrWhiteSpace(input.Name) ? existing.Name : input.Name,
            Enabled = input.Enabled,
            Transport = string.IsNullOrWhiteSpace(input.Transport) ? existing.Transport : input.Transport,
            Url = input.Url ?? existing.Url,
            Command = input.Command,
            Args = input.Args,
            Env = input.Env ?? existing.Env,
            Headers = input.Headers ?? existing.Headers,
            Cwd = input.Cwd ?? existing.Cwd,
            ImportSource = input.ImportSource ?? existing.ImportSource,
        };
        return await registry.SaveAsync(existing.ResourceId, merged, revision, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpResourceDto> SetEnabledAsync(
        string serverIdOrResourceId,
        bool enabled,
        Guid? projectId = null,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await ResolveRequiredAsync(serverIdOrResourceId, projectId, cancellationToken).ConfigureAwait(false);
        var write = new McpResourceWriteDto
        {
            ProjectId = existing.ProjectId,
            Id = existing.Id,
            Name = existing.Name,
            Enabled = enabled,
            Transport = existing.Transport,
            Url = existing.Url,
            Command = existing.Command,
            Args = existing.Args,
            Env = existing.Env,
            Headers = existing.Headers,
            Cwd = existing.Cwd,
            ImportSource = existing.ImportSource,
        };
        return await registry.SaveAsync(existing.ResourceId, write, expectedRevision ?? existing.Revision, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpServerTestResultDto> TestAsync(
        string serverIdOrResourceId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var resource = await ResolveRequiredAsync(serverIdOrResourceId, projectId, cancellationToken).ConfigureAwait(false);
        if (!resource.Enabled)
        {
            return new McpServerTestResultDto
            {
                ResourceId = resource.ResourceId,
                Id = resource.Id,
                Name = resource.Name,
                Enabled = false,
                Status = "disabled",
                Reason = "Enable this server before testing it.",
                ToolCount = 0,
                Tools = [],
            };
        }

        var resolved = await resolver.ResolveForInspectionAsync(resource.ProjectId, null, cancellationToken).ConfigureAwait(false);
        var selected = new ToolExecutionContextDto
        {
            WorkspaceRoots = resolved.WorkspaceRoots,
            WorkspaceFiles = resolved.WorkspaceFiles,
            PrimaryRootId = resolved.PrimaryRootId,
            WorkingDirectory = resolved.WorkingDirectory,
            ProtectedStorageRoots = resolved.ProtectedStorageRoots,
            StorageId = resolved.StorageId,
            StorageRoot = resolved.StorageRoot,
            ProjectRoot = resolved.ProjectRoot,
            ProjectStorageWrite = resolved.ProjectStorageWrite,
            Settings = ToolSettingsSchema.Merge(resolved.Settings, JsonSerializer.SerializeToElement(new { mcp = new { enabled = true } })),
            SettingsHash = resolved.SettingsHash,
            McpServers = await registry.ResolveAsync(resource.ProjectId, [resource.ResourceId], cancellationToken).ConfigureAwait(false),
            ReadRoots = [],
            SkillResources = resolved.SkillResources,
            McpImportError = resolved.McpImportError,
        };
        var context = await resolver.MaterializeForCallAsync(selected, ["mcp_list"], cancellationToken).ConfigureAwait(false);
        var root = resource.ProjectId is { } pid
            ? (await sessions.FindProjectAsync(pid, cancellationToken).ConfigureAwait(false))?.RootPath
            : null;
        if (string.IsNullOrWhiteSpace(root))
            root = configuration["TinadecTools:DefaultWorkspaceRoot"] ?? Directory.GetCurrentDirectory();

        try
        {
            var response = await provider.CallAsync(
                root,
                new ToolWireRequestDto
                {
                    ToolId = "mcp_list",
                    SessionId = "mcp-resource-test",
                    Approved = false,
                    Params = JsonSerializer.SerializeToElement(new { include_schema = true }),
                    ExecutionContext = context,
                },
                ProbeTimeout,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                return new McpServerTestResultDto
                {
                    ResourceId = resource.ResourceId,
                    Id = resource.Id,
                    Name = resource.Name,
                    Enabled = true,
                    Status = "error",
                    Reason = response.Error ?? "The tool provider rejected the MCP test handshake.",
                    ToolCount = 0,
                    Tools = [],
                };
            }

            var row = response.Result is { ValueKind: JsonValueKind.Object } payload
                && payload.TryGetProperty("servers", out var listed)
                && listed.ValueKind == JsonValueKind.Array
                    ? listed.EnumerateArray().FirstOrDefault(s => s.TryGetProperty("id", out var sid) && string.Equals(sid.GetString(), resource.Id, StringComparison.OrdinalIgnoreCase))
                    : default;

            if (row.ValueKind == JsonValueKind.Undefined)
            {
                return new McpServerTestResultDto
                {
                    ResourceId = resource.ResourceId,
                    Id = resource.Id,
                    Name = resource.Name,
                    Enabled = true,
                    Status = "not_reported",
                    Reason = "The provider did not report this MCP server.",
                    ToolCount = 0,
                    Tools = [],
                };
            }

            var tools = ReadTools(row);
            return new McpServerTestResultDto
            {
                ResourceId = resource.ResourceId,
                Id = resource.Id,
                Name = resource.Name,
                Enabled = true,
                Status = row.TryGetProperty("status", out var status) ? status.GetString() ?? McpServerStatus.Unknown : McpServerStatus.Unknown,
                Reason = row.TryGetProperty("error", out var error) ? error.GetString() : null,
                ToolCount = tools.Count,
                Tools = tools,
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new McpServerTestResultDto
            {
                ResourceId = resource.ResourceId,
                Id = resource.Id,
                Name = resource.Name,
                Enabled = true,
                Status = "unavailable",
                Reason = ex.Message,
                ToolCount = 0,
                Tools = [],
            };
        }
    }

    public async Task DeleteAsync(
        string serverIdOrResourceId,
        Guid? projectId = null,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await ResolveRequiredAsync(serverIdOrResourceId, projectId, cancellationToken).ConfigureAwait(false);
        await registry.DeleteAsync(existing.ResourceId, expectedRevision ?? existing.Revision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpResourceDto> ResolveRequiredAsync(
        string serverIdOrResourceId,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        return await GetAsync(serverIdOrResourceId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolSettingsException("mcp_resource_not_found", "MCP resource not found.", 404);
    }

    private static IReadOnlyList<McpToolDto> ReadTools(JsonElement row)
    {
        if (!row.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<McpToolDto>();
        foreach (var tool in tools.EnumerateArray())
        {
            var id = tool.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()
                : tool.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var name = tool.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? id : id;
            var description = tool.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            JsonElement? schema = tool.TryGetProperty("input_schema", out var s) && s.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                ? s.Clone()
                : null;
            list.Add(new McpToolDto { Id = id, Name = name, Description = description, InputSchema = schema });
        }
        return list;
    }
}
