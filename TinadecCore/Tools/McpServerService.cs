using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Canonical Core MCP server management service unifying <c>/api/v1/mcp/servers</c>,
/// <c>/api/v1/tools/mcp/servers</c>, and <c>tinadec_mcp_apply</c> over
/// <see cref="IMcpResourceRegistry"/> and <see cref="ISecretStore"/>.
/// </summary>
public sealed partial class McpServerService(
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
            throw new ToolSettingsException("invalid_mcp_json", "No MCP server entries were found in the supplied JSON, TOML, or APM YAML snippet.");

        var existingRows = await registry.ListAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var byServerId = existingRows
            .Where(x => x.ProjectId == request.ProjectId)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        var imported = new List<McpResourceDto>();
        var skipped = new List<McpImportJsonSkippedDto>();
        var placeholders = new List<string>();
        var seenPlaceholders = new HashSet<string>(StringComparer.Ordinal);
        var importSource = string.IsNullOrWhiteSpace(request.ImportSource) ? "import_json" : request.ImportSource.Trim();

        foreach (var parsed in entries)
        {
            foreach (var ph in McpConfigParser.ExtractSecretPlaceholders(parsed))
            {
                if (seenPlaceholders.Add(ph))
                    placeholders.Add(ph);
            }

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
                SecretValues = request.SecretValues,
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
            SecretPlaceholders = placeholders,
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
            SecretValues = input.SecretValues,
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

    public async Task<McpExportResultDto> ExportAsync(
        Guid? projectId = null,
        string format = "json",
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = (format ?? "json").Trim().ToLowerInvariant() switch
        {
            "apm" or "yaml" or "yml" or "apm_yml" => "apm",
            "toml" or "codex" => "toml",
            _ => "json",
        };

        var configured = await registry.ListAsync(projectId, cancellationToken).ConfigureAwait(false);
        var effective = configured
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => projectId is not null && x.ProjectId == projectId).First())
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var placeholders = new List<string>();
        var seenPlaceholders = new HashSet<string>(StringComparer.Ordinal);

        string AddPlaceholder(string name)
        {
            if (seenPlaceholders.Add(name))
                placeholders.Add(name);
            return "${" + name + "}";
        }

        var serversObj = new JsonObject();
        var yamlBuilder = new StringBuilder();
        var tomlBuilder = new StringBuilder();
        if (normalizedFormat == "apm")
        {
            yamlBuilder.AppendLine("dependencies:");
            yamlBuilder.AppendLine("  mcp:");
        }

        foreach (var srv in effective)
        {
            var isStdio = string.Equals(srv.Transport, "stdio", StringComparison.OrdinalIgnoreCase);
            var envOut = new Dictionary<string, string>(StringComparer.Ordinal);
            var headersOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var knownPlaceholders = srv.SecretPlaceholders.ToList();
            if (srv.Env is { Count: > 0 })
            {
                foreach (var envKey in srv.Env.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    var match = knownPlaceholders.FirstOrDefault(p => string.Equals(p, envKey, StringComparison.OrdinalIgnoreCase))
                        ?? (srv.Env.Count == 1 && knownPlaceholders.Count == 1 ? knownPlaceholders[0] : null);
                    var varName = match ?? ToUpperEnvName(envKey);
                    envOut[envKey] = AddPlaceholder(varName);
                }
            }

            if (srv.Headers is { Count: > 0 })
            {
                foreach (var hdrKey in srv.Headers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    var unusedKnown = knownPlaceholders.FirstOrDefault(p => !envOut.Values.Contains("${" + p + "}", StringComparer.Ordinal));
                    var varName = unusedKnown ?? ToUpperEnvName($"{srv.Id}_{hdrKey}");
                    var ph = AddPlaceholder(varName);
                    headersOut[hdrKey] = string.Equals(hdrKey, "Authorization", StringComparison.OrdinalIgnoreCase)
                        ? $"Bearer {ph}"
                        : ph;
                }
            }

            var exportedArgs = srv.Args.Select(arg => SanitizeArgForExport(srv.Id, arg, AddPlaceholder)).ToArray();
            var exportedUrl = srv.Url is null ? null : SanitizeUrlForExport(srv.Id, srv.Url, AddPlaceholder);

            foreach (var existingPh in McpConfigParser.ExtractSecretPlaceholders(exportedUrl, exportedArgs, null, null))
            {
                if (seenPlaceholders.Add(existingPh))
                    placeholders.Add(existingPh);
            }

            if (normalizedFormat == "json")
            {
                var entryObj = new JsonObject();
                if (isStdio)
                {
                    entryObj["command"] = srv.Command;
                    entryObj["args"] = new JsonArray(exportedArgs.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
                    if (!string.IsNullOrWhiteSpace(srv.Cwd))
                        entryObj["cwd"] = srv.Cwd;
                }
                else
                {
                    entryObj["transport"] = srv.Transport;
                    entryObj["url"] = exportedUrl;
                }

                if (envOut.Count > 0)
                {
                    var envNode = new JsonObject();
                    foreach (var kv in envOut) envNode[kv.Key] = kv.Value;
                    entryObj["env"] = envNode;
                }
                if (headersOut.Count > 0)
                {
                    var hdrNode = new JsonObject();
                    foreach (var kv in headersOut) hdrNode[kv.Key] = kv.Value;
                    entryObj["headers"] = hdrNode;
                }
                if (!srv.Enabled)
                    entryObj["disabled"] = true;

                serversObj[srv.Id] = entryObj;
            }
            else if (normalizedFormat == "apm")
            {
                yamlBuilder.Append("    - id: ").AppendLine(QuoteYamlScalar(srv.Id));
                if (!string.Equals(srv.Name, srv.Id, StringComparison.Ordinal))
                    yamlBuilder.Append("      name: ").AppendLine(QuoteYamlScalar(srv.Name));
                if (isStdio)
                {
                    yamlBuilder.Append("      command: ").AppendLine(QuoteYamlScalar(srv.Command));
                    if (exportedArgs.Length > 0)
                    {
                        yamlBuilder.AppendLine("      args:");
                        foreach (var a in exportedArgs)
                            yamlBuilder.Append("        - ").AppendLine(QuoteYamlScalar(a));
                    }
                    if (!string.IsNullOrWhiteSpace(srv.Cwd))
                        yamlBuilder.Append("      cwd: ").AppendLine(QuoteYamlScalar(srv.Cwd!));
                }
                else
                {
                    yamlBuilder.Append("      transport: ").AppendLine(QuoteYamlScalar(srv.Transport));
                    if (exportedUrl is not null)
                        yamlBuilder.Append("      url: ").AppendLine(QuoteYamlScalar(exportedUrl));
                }
                if (envOut.Count > 0)
                {
                    yamlBuilder.AppendLine("      env:");
                    foreach (var kv in envOut)
                        yamlBuilder.Append("        ").Append(kv.Key).Append(": ").AppendLine(QuoteYamlScalar(kv.Value));
                }
                if (headersOut.Count > 0)
                {
                    yamlBuilder.AppendLine("      headers:");
                    foreach (var kv in headersOut)
                        yamlBuilder.Append("        ").Append(kv.Key).Append(": ").AppendLine(QuoteYamlScalar(kv.Value));
                }
                if (!srv.Enabled)
                    yamlBuilder.AppendLine("      enabled: false");
            }
            else
            {
                tomlBuilder.Append("[mcp_servers.").Append(srv.Id).AppendLine("]");
                if (isStdio)
                {
                    tomlBuilder.Append("command = ").AppendLine(JsonSerializer.Serialize(srv.Command));
                    tomlBuilder.Append("args = [").Append(string.Join(", ", exportedArgs.Select(a => JsonSerializer.Serialize(a)))).AppendLine("]");
                    if (!string.IsNullOrWhiteSpace(srv.Cwd))
                        tomlBuilder.Append("cwd = ").AppendLine(JsonSerializer.Serialize(srv.Cwd));
                }
                else
                {
                    tomlBuilder.Append("transport = ").AppendLine(JsonSerializer.Serialize(srv.Transport));
                    if (exportedUrl is not null)
                        tomlBuilder.Append("url = ").AppendLine(JsonSerializer.Serialize(exportedUrl));
                }
                if (!srv.Enabled)
                    tomlBuilder.AppendLine("enabled = false");
                if (envOut.Count > 0)
                {
                    tomlBuilder.Append("[mcp_servers.").Append(srv.Id).AppendLine(".env]");
                    foreach (var kv in envOut)
                        tomlBuilder.Append(kv.Key).Append(" = ").AppendLine(JsonSerializer.Serialize(kv.Value));
                }
                if (headersOut.Count > 0)
                {
                    tomlBuilder.Append("[mcp_servers.").Append(srv.Id).AppendLine(".http_headers]");
                    foreach (var kv in headersOut)
                        tomlBuilder.Append(JsonSerializer.Serialize(kv.Key)).Append(" = ").AppendLine(JsonSerializer.Serialize(kv.Value));
                }
                tomlBuilder.AppendLine();
            }
        }

        string content;
        if (normalizedFormat == "json")
        {
            var root = new JsonObject { ["mcpServers"] = serversObj };
            content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        else if (normalizedFormat == "apm")
        {
            content = effective.Length == 0 ? "dependencies:\n  mcp: []\n" : yamlBuilder.ToString();
        }
        else
        {
            content = tomlBuilder.ToString().TrimEnd();
        }

        return new McpExportResultDto
        {
            Format = normalizedFormat,
            Content = content,
            SecretPlaceholders = placeholders,
            ServerCount = effective.Length,
        };
    }

    private static string SanitizeArgForExport(string serverId, string arg, Func<string, string> addPlaceholder)
    {
        if (string.IsNullOrEmpty(arg) || McpConfigParser.ExtractSecretPlaceholders(arg).Count > 0)
            return arg;

        var eq = arg.IndexOf('=');
        if (eq > 0)
        {
            var key = arg[..eq];
            if (ExportSecretKeyRegex().IsMatch(key))
            {
                var varName = ToUpperEnvName($"{serverId}_{key.TrimStart('-')}");
                return key + "=" + addPlaceholder(varName);
            }
        }

        if (ExportSecretValueRegex().IsMatch(arg))
        {
            var varName = ToUpperEnvName($"{serverId}_SECRET");
            return addPlaceholder(varName);
        }

        return arg;
    }

    private static string SanitizeUrlForExport(string serverId, string url, Func<string, string> addPlaceholder)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        if (string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query))
            return url;

        var schemeHostPath = $"{uri.Scheme}://";
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var userVar = addPlaceholder(ToUpperEnvName($"{serverId}_USER"));
            var passVar = addPlaceholder(ToUpperEnvName($"{serverId}_PASSWORD"));
            schemeHostPath += uri.UserInfo.Contains(':') ? $"{userVar}:{passVar}@" : $"{userVar}@";
        }
        schemeHostPath += uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        schemeHostPath += uri.AbsolutePath;

        if (!string.IsNullOrEmpty(uri.Query))
        {
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < pairs.Length; i++)
            {
                var eq = pairs[i].IndexOf('=');
                if (eq > 0)
                {
                    var key = pairs[i][..eq];
                    var val = Uri.UnescapeDataString(pairs[i][(eq + 1)..]);
                    if (McpConfigParser.ExtractSecretPlaceholders(val).Count > 0)
                    {
                        pairs[i] = key + "=" + val;
                    }
                    else if (ExportSecretKeyRegex().IsMatch(key) || ExportSecretValueRegex().IsMatch(val))
                    {
                        var varName = ToUpperEnvName($"{serverId}_{key}");
                        pairs[i] = key + "=" + addPlaceholder(varName);
                    }
                }
            }
            schemeHostPath += "?" + string.Join('&', pairs);
        }

        return schemeHostPath;
    }

    private static string ToUpperEnvName(string raw)
    {
        var cleaned = Regex.Replace((raw ?? string.Empty).Trim(), @"[^A-Za-z0-9]+", "_").Trim('_').ToUpperInvariant();
        if (cleaned.Length == 0) return "MCP_SECRET";
        if (char.IsDigit(cleaned[0])) cleaned = "MCP_" + cleaned;
        return cleaned;
    }

    private static string QuoteYamlScalar(string value) =>
        JsonSerializer.Serialize(value ?? string.Empty);

    [GeneratedRegex(@"(?:key|secret|token|password|passwd|pwd|auth|bearer|credential)", RegexOptions.IgnoreCase)]
    private static partial Regex ExportSecretKeyRegex();

    [GeneratedRegex(@"^(?:sk-[A-Za-z0-9_\-]{10,}|ghp_[A-Za-z0-9]{10,}|Bearer\s+.+|\*{6,})$", RegexOptions.IgnoreCase)]
    private static partial Regex ExportSecretValueRegex();

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
