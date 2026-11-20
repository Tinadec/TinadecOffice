using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manifest metadata for the <c>tinadec_*</c> workbench-configuration tools. This class
/// knows ids, descriptions and schemas only — never a service — so it can sit in the
/// Tools module while the execution lives behind <see cref="ITinadecConfigToolGateway"/>
/// in Runtime (and is exposed directly over the loopback MCP server in AspNetCore).
///
/// Policy shape: under the user-approved self-config policy, all <c>tinadec_*</c> tools
/// execute with <see cref="ToolManifestEntryDto.RequiresApproval"/> = <c>false</c>. Writes
/// still enforce input validation, optimistic revision checks, pack-managed immutability,
/// secret redaction, and version snapshots, and never mutate user workspace files.
/// </summary>
public static class CoreTinadecConfigTools
{
    public static IReadOnlyList<ToolManifestEntryDto> AllManifestEntries() =>
        CoreVirtualToolPolicy.TinadecConfigToolIds.Select(ManifestEntry).ToArray();

    public static ToolManifestEntryDto ManifestEntry(string toolId) => toolId switch
    {
        CoreVirtualToolPolicy.TinadecInspectToolId => Entry(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            "Read live workbench configuration before changing it: agents, one full agent detail, modes, "
            + "one mode's graph, the authoring catalog, sanitized models & model_profiles, prompt fragments "
            + "& pipelines, one full prompt_pipeline graph, skills, MCP servers, or the market. Results are "
            + "sanitized (no secrets) and list tools cap rows at 50.",
            InspectSchema, requiresApproval: false, risk: "low", retrySafety: "safe", confirmation: []),
        CoreVirtualToolPolicy.TinadecAgentApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecAgentApplyToolId,
            "Create, clone, update, or publish a CUSTOM agent definition (including base_prompt_pipeline_id, "
            + "source_kind/provenance, model_binding, and per-agent Skill/MCP tool_settings). Pack-managed agents "
            + "are read-only: clone one to customize it. Agent runtime creations/clones default to "
            + "source_kind='agent_generated' with provenance recorded.",
            AgentSchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        CoreVirtualToolPolicy.TinadecModeApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecModeApplyToolId,
            "Create, clone, upsert, or delete a DRAFT agent mode and its topology (resolving mode_id by GUID or "
            + "slug, accepting instructions, bindings, orchestration_tier, runtime_policy, nodes[], edges[]). "
            + "Nodes/edges absent from submitted arrays are deleted and every edge must carry edge_key.",
            ModeSchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        CoreVirtualToolPolicy.TinadecModePublishToolId => Entry(
            CoreVirtualToolPolicy.TinadecModePublishToolId,
            "Publish a draft mode (mode_id accepts GUID or slug): freezes agent/prompt versions and effective "
            + "tools, validates topology, and returns topology_hash, version, revision, and warnings.",
            PublishSchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        CoreVirtualToolPolicy.TinadecSkillApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecSkillApplyToolId,
            "Install, import, enable, disable, or remove a Skill resource. Preserves validation and optimistic "
            + "revision checks, never returns skill content in bulk, and keeps built-in skills read-only.",
            SkillSchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        CoreVirtualToolPolicy.TinadecMcpApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecMcpApplyToolId,
            "Upsert, test, or remove a Core-managed MCP server registration. Environment values come back masked; "
            + "sending a masked value back keeps the stored secret. Test reports connection status only.",
            McpSchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        CoreVirtualToolPolicy.TinadecPromptApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecPromptApplyToolId,
            "Create, update, clone, publish, or rollback Core prompt fragments and prompt pipelines. Preserves "
            + "graph validation, optimistic revision checks, and version history; built-in records must be cloned first.",
            PromptApplySchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        CoreVirtualToolPolicy.TinadecModelProfileApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecModelProfileApplyToolId,
            "Inspect or update per-model model_profiles (specialties, tier, notes, context_window, cost_tier) "
            + "on an enabled model provider without touching API keys or secret references.",
            ModelProfileApplySchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        _ => throw new InvalidOperationException($"No tinadec configuration tool named '{toolId}'."),
    };

    private static ToolManifestEntryDto Entry(
        string id,
        string description,
        string schema,
        bool requiresApproval,
        string risk,
        string retrySafety,
        IReadOnlyList<string> confirmation) => new()
    {
        Id = id,
        Description = description,
        RequiresApproval = requiresApproval,
        InputSchema = JsonDocument.Parse(schema).RootElement.Clone(),
        Risk = risk,
        MutatesWorkspace = false,
        RetrySafety = retrySafety,
        ConfirmationFields = confirmation,
    };

    private const string InspectSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"target\":{\"type\":\"string\",\"enum\":[\"agents\",\"agent\",\"modes\",\"mode\",\"catalog\",\"models\",\"prompts\",\"prompt_pipeline\",\"skills\",\"mcp\",\"market\"],\"description\":\"What to read. 'agent' needs agent_id (GUID or slug) and returns the FULL definition (system_prompt, model_strategy, tool_scope, capabilities, base_prompt_pipeline_id, source_kind, provenance, runtime_binding); 'mode' needs mode_id (GUID or slug); 'prompt_pipeline' needs pipeline_id (GUID or slug) and returns the full graph (nodes + edges); 'models' lists sanitized enabled providers, models, routes, health, and model_profiles; 'prompts' lists fragments (with content) and pipelines.\"}," +
        "\"agent_id\":{\"type\":\"string\",\"description\":\"Agent id (GUID) or slug, required for target=agent.\"}," +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Mode id (GUID) or slug, required for target=mode.\"}," +
        "\"pipeline_id\":{\"type\":\"string\",\"description\":\"Prompt pipeline id (GUID) or slug, required for target=prompt_pipeline.\"}," +
        "\"query\":{\"type\":\"string\",\"description\":\"Optional literal filter matched against names, slugs and descriptions.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"description\":\"Rows to return; defaults to 20 and is capped at 50.\"}" +
        "},\"required\":[\"target\"],\"additionalProperties\":false}";

    private const string AgentSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"clone\",\"create\",\"update\",\"publish\"],\"description\":\"create makes a draft; publish freezes an immutable version and is what makes a mode accept the agent.\"}," +
        "\"agent_id\":{\"type\":\"string\",\"description\":\"Agent id (GUID) or slug: source for clone, target for update and publish.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Optional optimistic revision from tinadec_inspect; a stale value fails with conflict.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Write fields: display_name, slug, layer (operation|execution), role, capabilities[], tool_scope[], system_prompt, description, model_strategy, enabled, base_prompt_pipeline_id (a prompt pipeline GUID, validated against this workspace), source_kind, provenance { created_by_agent_id, created_by_run_id, reason }, model_binding { mode (inherit|route|fixed; default fixed), provider_instance_id, model, route_purpose }, tool_settings { mcp_server_ids[], skill_resource_ids[], mcp_enabled, skills_enabled }. An omitted source_kind defaults to agent_generated when the call comes from a run.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string ModeSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"clone\",\"create\",\"upsert\",\"delete\"],\"description\":\"clone copies a pack or custom mode into an editable draft; upsert edits a draft you own.\"}," +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Target mode: a GUID or the mode slug (for example free_director). Source for clone, target for upsert and delete.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"The revision tinadec_inspect returned; a stale value is refused with a conflict.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"display_name, slug, description, instructions (at most 4000 chars), orchestration_tier (solo|solo_dispatch|free_form|self_dispatch|deterministic), bindings[] (each needs node_key or agent_ref), runtime_policy, canvas_layout, nodes[], edges[]. Nodes need node_key + agent_definition_id + layer and may carry relationship { duty, inputs_outputs, allowed_dispatch_targets, success_criteria, agent_types, subscriptions }; edges need edge_key + source_node_key + target_node_key. Nodes or edges omitted from a submitted array are deleted.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string PublishSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Mode id (GUID) or slug to publish.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0}" +
        "},\"required\":[\"mode_id\"],\"additionalProperties\":false}";

    private const string SkillSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"install\",\"import\",\"enable\",\"disable\",\"remove\"]}," +
        "\"resource_id\":{\"type\":\"string\",\"description\":\"Existing skill resource id; required for enable/disable/remove.\"}," +
        "\"project_id\":{\"type\":\"string\",\"description\":\"Project scope; omitted means the run's project, empty means shared.\"}," +
        "\"input\":{\"type\":\"object\",\"description\":\"install/import: name, content (SKILL.md), files (path -> base64), scope, expected_revision. enable/disable: expected_revision.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string McpSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"upsert\",\"test\",\"remove\"]}," +
        "\"resource_id\":{\"type\":\"string\",\"description\":\"Existing MCP resource id; required for test/remove and for updating one.\"}," +
        "\"input\":{\"type\":\"object\",\"description\":\"upsert: id, name, command, args[], env{}, cwd, enabled, project_id, expected_revision. Masked env values keep the stored secret.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string PromptApplySchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"target\":{\"type\":\"string\",\"enum\":[\"fragment\",\"pipeline\"],\"description\":\"Whether to mutate a prompt fragment or a prompt pipeline.\"}," +
        "\"action\":{\"type\":\"string\",\"enum\":[\"create\",\"update\",\"clone\",\"publish\",\"rollback\"]}," +
        "\"fragment_id\":{\"type\":\"string\",\"description\":\"Prompt fragment id (GUID) or key for target=fragment.\"}," +
        "\"pipeline_id\":{\"type\":\"string\",\"description\":\"Prompt pipeline id (GUID) or slug for target=pipeline.\"}," +
        "\"version_id\":{\"type\":\"string\",\"description\":\"Target version id or version number for rollback.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Optimistic revision check when updating or publishing.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Fragment fields: key, title, scope (global|mode|agent), category, target_agent_id, content, priority, enabled, new_key, target_version. Pipeline fields: display_name, slug, description, agent_definition_id, graph { nodes[], edges[], canvas_layout }, nodes[], edges[], notes.\"}" +
        "},\"required\":[\"target\",\"action\"],\"additionalProperties\":false}";

    private const string ModelProfileApplySchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"inspect\",\"update\"]}," +
        "\"provider_id\":{\"type\":\"string\",\"description\":\"Enabled model provider id (GUID) or name.\"}," +
        "\"model_id\":{\"type\":\"string\",\"description\":\"Model id on that provider.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Optional optimistic revision of the provider configuration.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Profile metadata for update: specialties (string[]), tier (fast|balanced|deep), notes (string), context_window (integer > 0), cost_tier (low|medium|high). Never accepts or returns secrets.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";
}
