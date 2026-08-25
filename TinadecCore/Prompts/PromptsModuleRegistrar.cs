using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Prompts;

/// <summary>
/// Prompts module registrar. Registers prompt assembler.
/// </summary>
public sealed class PromptsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "prompts";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<PromptControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<PromptControlDbContext>>();
        builder.Services.AddSingleton<IPromptAssembler, PromptAssembler>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies", "persistence"],
            Capabilities = ["fragment_assembly", "agent_instructions", "skill_contributions", "deterministic_assembly"],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Deterministically assembles the local prompt in the following order: architecture baseline,
/// persisted mode/profile fragments, agent fragments, task context, then runtime constraints.
/// The assembled body is returned only to an in-process caller or an explicit preview endpoint;
/// runtime events keep references/counts rather than prompt text.
/// </summary>
internal sealed class PromptAssembler : IPromptAssembler
{
    private readonly IDbContextFactory<PromptControlDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;

    public PromptAssembler(
        IDbContextFactory<PromptControlDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
    }

    public async Task<PromptAssemblyResult> AssembleAsync(
        string agentId,
        ContextPack? contextPack,
        CancellationToken cancellationToken = default) =>
        await AssembleAsync(new FrozenPromptAssemblyRequest(
            agentId,
            contextPack,
            IncludeLiveFragments: true), cancellationToken).ConfigureAwait(false);

    public async Task<PromptAssemblyResult> AssembleAsync(
        FrozenPromptAssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId)) throw new ArgumentException("Agent id is required.", nameof(request));
        var agentId = request.AgentId.Trim();
        var contextPack = request.ContextPack;
        var sections = new List<string> { ArchitectureBaseline(agentId) };
        var fragmentIds = new List<string> { "builtin:architecture-baseline" };
        var warnings = new List<string>();
        var budget = contextPack?.TokenBudget ?? 8192;
        var used = EstimateTokens(sections[0]);

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            var systemPrompt = request.SystemPrompt.Trim();
            sections.Add(systemPrompt);
            used += EstimateTokens(systemPrompt);
            fragmentIds.Add(request.AgentVersionId is { } agentVersionId
                ? $"agent-version:{agentVersionId}:{request.AgentVersionContentHash}"
                : $"agent:{agentId}:system-prompt");
        }
        var runtimeProfile = contextPack?.Metadata.TryGetValue("runtime_profile_id", out var profileId) == true ? profileId : "unspecified";
        var applicationMode = contextPack?.Metadata.TryGetValue("application_mode", out var applicationModeId) == true ? applicationModeId : "conversation";
        var agentMode = contextPack?.Metadata.TryGetValue("agent_mode", out var agentModeId) == true ? agentModeId : "auto";
        var modeProfile = $"Mode profile: application_mode={applicationMode}; agent_mode={agentMode}; runtime_profile_id={runtimeProfile}.";
        var modeTokens = EstimateTokens(modeProfile);
        if (modeTokens <= budget - used)
        {
            sections.Add(modeProfile);
            fragmentIds.Add("builtin:mode-profile");
            used += modeTokens;
        }
        else
        {
            warnings.Add("Mode profile could not fit in the configured context budget.");
        }

        var agentProfile = $"Agent profile: agent_id={agentId}. Follow only the capabilities and layer constraints frozen for this run.";
        var agentTokens = EstimateTokens(agentProfile);
        if (agentTokens <= budget - used)
        {
            sections.Add(agentProfile);
            fragmentIds.Add("builtin:agent-profile");
            used += agentTokens;
        }
        else
        {
            warnings.Add("Agent profile could not fit in the configured context budget.");
        }

        if (request.PromptVersionId is { } promptVersionId)
        {
            fragmentIds.Add($"prompt-version:{promptVersionId}:{request.PromptVersionContentHash}");
            var templates = ReadPromptTemplates(request.PromptGraphJson, warnings);
            if (templates.Count == 0)
            {
                warnings.Add($"Prompt version '{promptVersionId}' contains no non-empty template content.");
            }
            foreach (var template in templates)
            {
                var tokens = EstimateTokens(template.Content);
                if (tokens > budget - used)
                {
                    warnings.Add($"Prompt template '{template.Id}' was omitted because the context budget is exhausted.");
                    continue;
                }
                sections.Add(template.Content);
                fragmentIds.Add($"prompt-node:{promptVersionId}:{template.Id}");
                used += tokens;
            }
        }

        if (request.IncludeLiveFragments)
        {
            var scope = _tenant.Current;
            Guid? targetAgentId = Guid.TryParse(agentId, out var parsedAgentId) ? parsedAgentId : null;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var fragmentRows = await db.Fragments.AsNoTracking()
                .Where(fragment => fragment.TenantId == scope.TenantId
                    && fragment.Enabled
                    && fragment.DeletedAt == null
                    && (fragment.WorkspaceId == null || fragment.WorkspaceId == scope.WorkspaceId)
                    && (fragment.TargetAgentId == null || fragment.TargetAgentId == targetAgentId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var fragments = fragmentRows
                .OrderBy(fragment => CategoryOrder(fragment.Category))
                .ThenByDescending(fragment => fragment.Priority)
                .ThenBy(fragment => fragment.Key, StringComparer.Ordinal)
                .ThenBy(fragment => fragment.Id)
                .ToArray();

            foreach (var fragment in fragments)
            {
                var version = await db.Versions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == fragment.CurrentVersionId, cancellationToken).ConfigureAwait(false);
                if (version is null)
                {
                    warnings.Add($"Prompt fragment '{fragment.Key}' has no current version.");
                    continue;
                }
                var text = await ReadTextAsync(version, cancellationToken).ConfigureAwait(false);
                var tokens = EstimateTokens(text);
                if (tokens > budget - used)
                {
                    warnings.Add($"Prompt fragment '{fragment.Key}' was omitted because the context budget is exhausted.");
                    continue;
                }
                sections.Add(text);
                fragmentIds.Add(fragment.Id.ToString());
                used += tokens;
            }
        }

        if (contextPack is not null)
        {
            foreach (var evidence in contextPack.Evidence)
            {
                var section = $"[{evidence.Source}]\n{evidence.Content}";
                var tokens = EstimateTokens(section);
                if (tokens > budget - used)
                {
                    warnings.Add($"Context evidence from '{evidence.Source}' was omitted because the context budget is exhausted.");
                    continue;
                }

                sections.Add(section);
                fragmentIds.Add($"context:{evidence.Source}");
                used += tokens;
            }
        }

        const string runtimeConstraints = "Runtime constraints: only the meeting agent may produce direct user output. Do not claim an external action unless execution evidence confirms it. Respect Core-owned approval and tool policy.";
        var runtimeTokens = EstimateTokens(runtimeConstraints);
        sections.Add(runtimeConstraints);
        fragmentIds.Add("builtin:runtime-constraints");
        used += runtimeTokens;

        return new PromptAssemblyResult
        {
            Instructions = string.Join("\n\n", sections),
            EstimatedTokens = used,
            FragmentIds = fragmentIds,
            Warnings = warnings
        };
    }

    private async Task<string> ReadTextAsync(PromptFragmentVersionRecord version, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(
            new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "text/plain; charset=utf-8"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ArchitectureBaseline(string agentId) =>
        $"TinadecOffice uses a Core-owned operation/execution agent harness. You are '{agentId}'. The meeting agent is the only formal user-facing agent; all other agents submit auditable evidence to Core.";

    private static IReadOnlyList<PromptTemplate> ReadPromptTemplates(string? graphJson, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(graphJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(graphJson);
            if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return [];
            var result = new List<PromptTemplate>();
            var index = 0;
            foreach (var node in nodes.EnumerateArray())
            {
                index++;
                var kind = String(node, "type") ?? String(node, "kind");
                if (!string.Equals(kind, "template", StringComparison.OrdinalIgnoreCase)) continue;
                var id = String(node, "id") ?? String(node, "node_key") ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var content = TemplateContent(node);
                if (!string.IsNullOrWhiteSpace(content)) result.Add(new PromptTemplate(id, content.Trim()));
            }
            return result;
        }
        catch (JsonException ex)
        {
            warnings.Add("Frozen prompt graph could not be parsed: " + ex.Message);
            return [];
        }
    }

    private static string? TemplateContent(JsonElement node)
    {
        foreach (var property in new[] { "content", "template", "text", "value" })
            if (String(node, property) is { } direct) return direct;
        if (node.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
            foreach (var property in new[] { "content", "template", "text", "value" })
                if (String(config, property) is { } nested) return nested;
        return null;
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int CategoryOrder(string category) => category.Trim().ToLowerInvariant() switch
    {
        "mode_profile" or "mode" => 1,
        "agent_profile" or "agent" => 2,
        "task" or "task_context" => 3,
        _ => 4
    };

    private static int EstimateTokens(string text) => Math.Max(1, (text.Length + 3) / 4);

    private sealed record PromptTemplate(string Id, string Content);
}
