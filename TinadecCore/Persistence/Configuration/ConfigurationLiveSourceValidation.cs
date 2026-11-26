using System.Globalization;
using TinadecCore.Abstractions.Ports;
using Tomlyn.Model;

namespace TinadecCore.Persistence;

/// <summary>New admissions may only depend on identities present in the editing source.</summary>
internal static class ConfigurationLiveSourceValidation
{
    public static void Validate(IReadOnlyDictionary<string, TomlTable> documents)
    {
        var rules = new (string Document, string Table, string Reference, string TargetDocument, string TargetTable)[]
        {
            ("agents", "workspace_defaults", "default_agent_definition_id", "agents", "agent_definitions"),
            ("agents", "workspace_defaults", "default_agent_mode_id", "agents", "agent_modes"),
            ("agents", "workspace_defaults", "default_prompt_pipeline_id", "agents", "prompt_pipelines"),
            ("agents", "workspace_defaults", "default_agent_version_id", "agents", "agent_versions"),
            ("agents", "workspace_defaults", "default_mode_version_id", "agents", "mode_versions"),
            ("agents", "workspace_defaults", "default_prompt_version_id", "agents", "prompt_versions"),
            ("agents", "agent_pack_installations", "active_version_id", "agents", "agent_pack_versions"),
            ("models", "model_provider_instances", "current_version_id", "models", "model_provider_versions"),
            ("models", "model_routes", "current_version_id", "models", "model_route_versions"),
            ("prompts", "prompt_fragments", "current_version_id", "prompts", "prompt_fragment_versions"),
            ("skills", "workspace_extensions", "current_version_id", "skills", "workspace_extension_versions"),
            ("skills", "integration_instances", "current_version_id", "skills", "integration_instance_versions")
        };
        var liveIdsByTarget = new Dictionary<(string Document, string Table), HashSet<string>>();
        foreach (var rule in rules)
        {
            var key = (rule.TargetDocument, rule.TargetTable);
            if (!liveIdsByTarget.TryGetValue(key, out var liveIds))
            {
                liveIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var version in Rows(documents, rule.TargetDocument, rule.TargetTable))
                {
                    if (IsLive(version) && Text(version, "id") is { } id)
                        liveIds.Add(id);
                }
                liveIdsByTarget[key] = liveIds;
            }
            foreach (var row in Rows(documents, rule.Document, rule.Table).Where(IsLive))
            {
                if (!row.TryGetValue(rule.Reference, out var value) || value is not string reference) continue;
                if (!liveIds.Contains(reference))
                    Throw(rule.Document + "." + rule.Table + "." + rule.Reference,
                        "The active reference is absent from " + rule.TargetDocument + ".toml. Historical SQL rows cannot supply new configuration.");
            }
        }
        foreach (var (table, versions, owner) in new[]
            { ("agent_definitions", "agent_versions", "agent_definition_id"), ("agent_modes", "mode_versions", "agent_mode_id"),
              ("prompt_pipelines", "prompt_versions", "prompt_pipeline_id"), ("agent_templates", "agent_template_versions", "agent_template_id"),
              ("tool_definitions", "tool_definition_versions", "tool_definition_id") })
        {
            var publishedRows = Rows(documents, "agents", table)
                .Where(row => IsLive(row) && Text(row, "status") == "published")
                .ToList();
            if (publishedRows.Count == 0) continue;

            var liveVersions = new HashSet<(string? OwnerId, long Version)>();
            foreach (var version in Rows(documents, "agents", versions))
            {
                if (IsLive(version) && version.TryGetValue("version", out var current))
                    liveVersions.Add((Text(version, owner), Convert.ToInt64(current, CultureInfo.InvariantCulture)));
            }

            foreach (var row in publishedRows)
            {
                var number = row.TryGetValue("version", out var value) ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0L;
                if (number <= 0) continue;
                if (!liveVersions.Contains((Text(row, "id"), number)))
                    Throw("agents." + table, "A published definition's current version is absent from agents.toml.");
            }
        }
    }

    private static IEnumerable<TomlTable> Rows(IReadOnlyDictionary<string, TomlTable> documents, string document, string table)
        => documents.TryGetValue(document, out var model) && model.TryGetValue(table, out var value) && value is TomlTableArray rows ? rows : [];
    private static string? Text(TomlTable row, string name) => row.TryGetValue(name, out var value) ? value as string : null;
    private static bool IsLive(TomlTable row) => !row.ContainsKey("deleted_at") && !row.ContainsKey("archived_at")
        && (!row.TryGetValue("enabled", out var enabled) || enabled is not false)
        && Text(row, "status") is not ("retired" or "archived" or "disabled" or "uninstalled" or "deleted");
    private static void Throw(string path, string message) => throw new ConfigurationDocumentException("configuration_source_reference",
        "Active configuration must be present in its TOML editing source.", [new("configuration_source_reference", path + ": " + message)]);
}
