using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Runtime;

internal static class BootstrapAgentDirectory
{
    private const string PromptSlug = "baseline-prompt";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static async Task<bool> SeedIfEmptyAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        var factory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var hasDirectory = await db.AgentDefinitions.AnyAsync(
                item => item.TenantId == tenant.TenantId && item.WorkspaceId == tenant.WorkspaceId,
                cancellationToken).ConfigureAwait(false)
            || await db.AgentModes.AnyAsync(
                item => item.TenantId == tenant.TenantId && item.WorkspaceId == tenant.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
        if (hasDirectory) return false;

        var configuration = services.GetRequiredService<IConfiguration>();
        var path = ResolvePath(configuration["TinadecAgent:BootstrapDirectoryPath"]);
        var manifest = ReadManifest(path);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var prompt = CreatePrompt(db, tenant, now);
        var agents = CreateAgents(db, tenant, prompt.Id, manifest.Agents, now);
        var modes = CreateModes(db, tenant, agents, manifest.Modes, now);

        if (!agents.TryGetValue(manifest.DefaultAgent, out var defaultAgent))
            throw new InvalidDataException($"Bootstrap default_agent '{manifest.DefaultAgent}' was not declared.");
        if (!modes.TryGetValue(manifest.DefaultMode, out var defaultMode))
            throw new InvalidDataException($"Bootstrap default_mode '{manifest.DefaultMode}' was not declared.");

        db.WorkspaceDefaults.Add(new WorkspaceDefaultsRecord
        {
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            DefaultAgentDefinitionId = defaultAgent.Definition.Id,
            DefaultAgentVersionId = defaultAgent.Version.Id,
            DefaultAgentModeId = defaultMode.Mode.Id,
            DefaultModeVersionId = defaultMode.Version.Id,
            DefaultPromptPipelineId = prompt.Id,
            DefaultPromptVersionId = prompt.Version.Id,
            Status = "active",
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        services.GetRequiredService<ILoggerFactory>().CreateLogger("TinadecCore.Runtime.BootstrapAgentDirectory")
            .LogInformation("Seeded formal bootstrap Agent directory from {Path} ({AgentCount} agents, {ModeCount} modes).", path, agents.Count, modes.Count);
        return true;
    }

    private static BootstrapPrompt CreatePrompt(AgentConfigurationDbContext db, TenantContext tenant, DateTimeOffset now)
    {
        const string graph = "{\"nodes\":[{\"id\":\"template\",\"type\":\"template\"},{\"id\":\"assemble\",\"type\":\"assemble\"}],\"edges\":[{\"source\":\"template\",\"target\":\"assemble\"}]}";
        var pipeline = new PromptPipelineRecord
        {
            Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
            Slug = PromptSlug, DisplayName = "Baseline Prompt", Description = "Bootstrap prompt pipeline.",
            GraphJson = graph, Status = "published", Revision = 1, Version = 1,
            CreatedAt = now, UpdatedAt = now,
            CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId
        };
        var version = new PromptVersionRecord
        {
            Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
            PromptPipelineId = pipeline.Id, Version = 1, GraphJson = graph,
            ContentHash = Hash(graph), ContentLength = Encoding.UTF8.GetByteCount(graph),
            Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = tenant.PrincipalId
        };
        db.PromptPipelines.Add(pipeline);
        db.PromptVersions.Add(version);
        return new BootstrapPrompt(pipeline.Id, version);
    }

    private static IReadOnlyDictionary<string, BootstrapAgentPublication> CreateAgents(
        AgentConfigurationDbContext db,
        TenantContext tenant,
        Guid promptPipelineId,
        IReadOnlyList<BootstrapAgent> agents,
        DateTimeOffset now)
    {
        var result = new Dictionary<string, BootstrapAgentPublication>(StringComparer.Ordinal);
        foreach (var item in agents)
        {
            if (!result.TryAdd(item.Key, null!)) throw new InvalidDataException($"Duplicate bootstrap agent key '{item.Key}'.");
            if (item.Layer is not ("operation" or "execution")) throw new InvalidDataException($"Bootstrap agent '{item.Key}' has invalid layer '{item.Layer}'.");
            var definition = new AgentDefinitionRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
                Slug = item.Key, DisplayName = item.DisplayName, Description = "Initial bootstrap Agent definition.",
                Layer = item.Layer, Role = item.Role,
                CapabilitiesJson = JsonSerializer.Serialize(item.Capabilities, JsonOptions),
                BasePromptPipelineId = promptPipelineId,
                ModelStrategyJson = "{\"kind\":\"inherit\"}",
                ToolScopeJson = JsonSerializer.Serialize(item.Tools, JsonOptions),
                SystemPrompt = item.SystemPrompt,
                SourceKind = "bootstrap", SourceKey = item.Key, Managed = false,
                Enabled = true, Status = "published", Revision = 1, Version = 1,
                CreatedAt = now, UpdatedAt = now,
                CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId
            };
            var snapshot = JsonSerializer.Serialize(new
            {
                id = definition.Id,
                slug = definition.Slug,
                display_name = definition.DisplayName,
                description = definition.Description,
                layer = definition.Layer,
                role = definition.Role,
                capabilities = item.Capabilities,
                base_prompt_pipeline_id = promptPipelineId,
                model_strategy = new { kind = "inherit" },
                tool_scope = item.Tools,
                system_prompt = item.SystemPrompt,
                source_kind = definition.SourceKind,
                source_key = definition.SourceKey,
                managed = false,
                enabled = true,
                status = "published",
                revision = 1,
                version = 1,
                created_at = now,
                updated_at = now
            });
            var version = new AgentVersionRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
                AgentDefinitionId = definition.Id, Version = 1, Layer = item.Layer, Role = item.Role,
                SnapshotJson = snapshot, ContentHash = Hash(snapshot), ContentLength = Encoding.UTF8.GetByteCount(snapshot),
                Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = tenant.PrincipalId
            };
            db.AgentDefinitions.Add(definition);
            db.AgentVersions.Add(version);
            result[item.Key] = new BootstrapAgentPublication(definition, version, item.Tools);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, BootstrapModePublication> CreateModes(
        AgentConfigurationDbContext db,
        TenantContext tenant,
        IReadOnlyDictionary<string, BootstrapAgentPublication> agents,
        IReadOnlyList<BootstrapMode> modes,
        DateTimeOffset now)
    {
        var result = new Dictionary<string, BootstrapModePublication>(StringComparer.Ordinal);
        foreach (var item in modes)
        {
            if (result.ContainsKey(item.Key)) throw new InvalidDataException($"Duplicate bootstrap mode key '{item.Key}'.");
            var mode = new AgentModeRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
                Slug = item.Key, DisplayName = item.DisplayName, Description = "Initial bootstrap Agent Mode.",
                Status = "published", Revision = 1, Version = 1,
                CreatedAt = now, UpdatedAt = now,
                CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId
            };
            db.AgentModes.Add(mode);

            var snapshotNodes = new List<object>();
            for (var index = 0; index < item.Agents.Count; index++)
            {
                var key = item.Agents[index];
                if (!agents.TryGetValue(key, out var agent)) throw new InvalidDataException($"Bootstrap mode '{item.Key}' references unknown agent '{key}'.");
                var nodeKey = $"node-{index + 1:D2}-{key.Replace('.', '-').Replace('_', '-')}";
                db.ModeNodes.Add(new ModeNodeRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
                    ModeId = mode.Id, NodeKey = nodeKey, AgentDefinitionId = agent.Definition.Id,
                    Layer = agent.Definition.Layer, Label = agent.Definition.DisplayName,
                    ConfigJson = "{}", Status = "published", Revision = 1, CreatedAt = now, UpdatedAt = now
                });
                snapshotNodes.Add(new
                {
                    node_key = nodeKey,
                    agent_definition_id = agent.Definition.Id,
                    agent_version_id = agent.Version.Id,
                    agent_version_hash = agent.Version.ContentHash,
                    layer = agent.Definition.Layer,
                    label = agent.Definition.DisplayName,
                    config = new { },
                    model_strategy_override = (object?)null,
                    model_strategy_source = "agent_version",
                    effective_tools = agent.Tools.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    prompt_pipeline_id = agent.Definition.BasePromptPipelineId,
                    prompt_version_id = db.PromptVersions.Local.Single(version => version.PromptPipelineId == agent.Definition.BasePromptPipelineId).Id,
                    prompt_version_hash = db.PromptVersions.Local.Single(version => version.PromptPipelineId == agent.Definition.BasePromptPipelineId).ContentHash
                });
            }
            db.CanvasLayouts.Add(new CanvasLayoutRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
                ModeId = mode.Id, LayoutJson = "{}", Status = "published", Revision = 1, CreatedAt = now, UpdatedAt = now
            });
            var snapshot = JsonSerializer.Serialize(new
            {
                schema = "tinadec.mode_version/v1",
                mode = new { id = mode.Id, slug = mode.Slug, display_name = mode.DisplayName },
                nodes = snapshotNodes.OrderBy(node => JsonSerializer.SerializeToElement(node).GetProperty("node_key").GetString(), StringComparer.Ordinal),
                edges = Array.Empty<object>(),
                canvas_layout = new { }
            });
            var version = new ModeVersionRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId,
                AgentModeId = mode.Id, Version = 1, SnapshotJson = snapshot, TopologyHash = Hash(snapshot),
                Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = tenant.PrincipalId
            };
            db.ModeVersions.Add(version);
            result.Add(item.Key, new BootstrapModePublication(mode, version));
        }
        return result;
    }

    private static BootstrapManifest ReadManifest(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Bootstrap Agent directory TOML was not found.", path);
        var root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path, Encoding.UTF8))
            ?? throw new InvalidDataException("Bootstrap Agent directory TOML has no root table.");
        if (Integer(root, "schema_version") != 1) throw new InvalidDataException("Unsupported bootstrap Agent directory schema_version.");
        var agents = Tables(root, "agents").Select(table => new BootstrapAgent(
            Text(table, "key"), Text(table, "display_name"), Text(table, "layer"), Text(table, "role"),
            Strings(table, "capabilities"), Strings(table, "tools"), Text(table, "system_prompt"))).ToArray();
        var modes = Tables(root, "modes").Select(table => new BootstrapMode(
            Text(table, "key"), Text(table, "display_name"), Strings(table, "agents"))).ToArray();
        if (agents.Length == 0 || modes.Length != 7) throw new InvalidDataException("Bootstrap Agent directory must declare agents and exactly seven modes.");
        return new BootstrapManifest(Text(root, "default_agent"), Text(root, "default_mode"), agents, modes);
    }

    private static string ResolvePath(string? configured) => !string.IsNullOrWhiteSpace(configured)
        ? Path.GetFullPath(configured)
        : Path.Combine(AppContext.BaseDirectory, "Configuration", "bootstrap-agent-directory.toml");

    private static IReadOnlyList<TomlTable> Tables(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is TomlTableArray rows
            ? rows.Cast<TomlTable>().ToArray()
            : throw new InvalidDataException($"Missing TOML table array '{key}'.");

    private static string Text(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString())
            ? value!.ToString()!.Trim()
            : throw new InvalidDataException($"Bootstrap TOML property '{key}' is required.");

    private static int Integer(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static IReadOnlyList<string> Strings(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is TomlArray array
            ? array.Select(item => item?.ToString()?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record BootstrapManifest(string DefaultAgent, string DefaultMode, IReadOnlyList<BootstrapAgent> Agents, IReadOnlyList<BootstrapMode> Modes);
    private sealed record BootstrapAgent(string Key, string DisplayName, string Layer, string Role, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Tools, string SystemPrompt);
    private sealed record BootstrapMode(string Key, string DisplayName, IReadOnlyList<string> Agents);
    private sealed record BootstrapPrompt(Guid Id, PromptVersionRecord Version);
    private sealed record BootstrapAgentPublication(AgentDefinitionRecord Definition, AgentVersionRecord Version, IReadOnlyList<string> Tools);
    private sealed record BootstrapModePublication(AgentModeRecord Mode, ModeVersionRecord Version);
}
