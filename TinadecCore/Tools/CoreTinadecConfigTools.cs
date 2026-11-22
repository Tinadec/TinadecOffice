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
            "Read live workbench configuration or runtime diagnostics before changing anything: agents, one full agent detail, modes, "
            + "one mode's graph, the authoring catalog, sanitized models & model_profiles, prompt fragments "
            + "& pipelines, one full prompt_pipeline graph, skills, MCP servers, redacted external CLI tools, "
            + "context_memory ([context]/[memory] runtime.toml policy, hot Markdown budgets & presets), "
            + "run_replay, recent_failures, or system_health. Results are sanitized (no secrets).",
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
            "Install (inline, local_dir, git_url, or external_cli_import), import, enable, disable, reload, or "
            + "remove a Skill resource. Preserves validation and optimistic revision checks, never returns skill "
            + "content in bulk, and keeps built-in skills read-only.",
            SkillSchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
        CoreVirtualToolPolicy.TinadecMcpApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecMcpApplyToolId,
            "Upsert (stdio/http/sse), import_json (.mcp.json / mcpServers), enable/disable, test, or remove a "
            + "Core-managed MCP server registration. Environment and header secrets are stored in ISecretStore "
            + "and always come back masked; sending a masked value back keeps the stored secret.",
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
        CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId,
            "Configure [context] & [memory] runtime policy (strategy direct|managed|deep_curator, compaction_trigger_ratio, "
            + "recall_fidelity, evolution_mode, core_markdown_max_tokens, companion_can_write_project_memory) or "
            + "add, supersede, revoke, or 1-click revert structured durable memory items.",
            ContextMemoryApplySchema, requiresApproval: false, risk: "high", retrySafety: "unsafe", confirmation: []),
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
        "\"target\":{\"type\":\"string\",\"enum\":[\"agents\",\"agent\",\"modes\",\"mode\",\"catalog\",\"models\",\"prompts\",\"prompt_pipeline\",\"tools\",\"skills\",\"mcp\",\"external_cli\",\"run_replay\",\"recent_failures\",\"system_health\",\"context_memory\"],\"description\":\"What to read. 'agent' needs agent_id; 'mode' needs mode_id; 'prompt_pipeline' needs pipeline_id; 'run_replay' needs run_id (replay + frozen snapshot + invocations + prompt warnings); 'recent_failures' returns sanitized recent 5xx failures; 'system_health' returns ReadinessService, model cooling breakers, and MCP status; 'context_memory' reads [context]/[memory] runtime.toml policy, hot Markdown budgets & strategy presets (optional companion_id).\"}," +
        "\"agent_id\":{\"type\":\"string\",\"description\":\"Agent id (GUID) or slug, required for target=agent.\"}," +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Mode id (GUID) or slug, required for target=mode.\"}," +
        "\"pipeline_id\":{\"type\":\"string\",\"description\":\"Prompt pipeline id (GUID) or slug, required for target=prompt_pipeline.\"}," +
        "\"run_id\":{\"type\":\"string\",\"description\":\"Run id (GUID), required for target=run_replay.\"}," +
        "\"companion_id\":{\"type\":\"string\",\"description\":\"Optional companion GUID when target=context_memory inspects companion memory.\"}," +
        "\"target_id\":{\"type\":\"string\",\"description\":\"Optional alias for run_id/agent_id/mode_id/pipeline_id.\"}," +
        "\"query\":{\"type\":\"string\",\"description\":\"Optional literal filter matched against names, slugs and descriptions.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"description\":\"Rows to return; defaults to 20 and is capped at 50.\"}" +
        "},\"required\":[\"target\"],\"additionalProperties\":false}";

    private const string AgentSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"clone\",\"create\",\"update\",\"publish\"]}," +
        "\"agent_id\":{\"type\":\"string\",\"description\":\"Agent id (GUID) or slug: source for clone, target for update/publish.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Optional optimistic revision from inspect; stale values fail with conflict.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Write fields: display_name, slug, layer (operation|execution), role, capabilities[], tool_scope[], system_prompt, description, model_strategy { kind (inherit|route|fixed), route_purpose, provider_instance_id, model }, enabled, base_prompt_pipeline_id, source_kind (agent_generated|custom), provenance { session_id, run_id, reason }, model_binding { mode (inherit|route|fixed), provider_id, model_id, route_purpose }, tool_settings { mcp_server_ids[], skill_resource_ids[] }.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string ModeSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"clone\",\"create\",\"upsert\",\"delete\"]}," +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Target mode (GUID or slug): source for clone, target for upsert/delete.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"The revision tinadec_inspect returned; a stale value is refused with a conflict.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"display_name, slug, description, instructions, orchestration_tier (solo_dispatch|free_form|self_dispatch|deterministic), default_options { multi_agent, plan_first, spec_enabled, bulletin_board, worktree }, bindings[], runtime_policy, canvas_layout, nodes[] ({ node_key, agent_definition_id, layer, label, relationship: { duty, inputs_outputs, allowed_dispatch_targets[], success_criteria[], agent_types[] } }), edges[] ({ edge_key, source_node_key, target_node_key, condition? }). Publish requires a user.respond conversation node and valid bindings.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string PublishSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Mode id (GUID) or slug to publish.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0}" +
        "},\"required\":[\"mode_id\"],\"additionalProperties\":false}";

    private const string SkillSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"install\",\"import\",\"enable\",\"disable\",\"reload\",\"remove\"]}," +
        "\"resource_id\":{\"type\":\"string\",\"description\":\"Existing skill resource id; required for enable/disable/remove.\"}," +
        "\"project_id\":{\"type\":\"string\",\"description\":\"Project scope; omitted means the run's project, empty means shared.\"}," +
        "\"input\":{\"type\":\"object\",\"description\":\"install/import: source_type (inline|local_dir|git_url|external_cli_import), name, content (SKILL.md), files (path -> base64), local_path, git_url, subpath, item_ids[], ecosystems[], scope, overwrite, governed, expected_revision. enable/disable: expected_revision.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string McpSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"upsert\",\"import_json\",\"enable\",\"disable\",\"test\",\"remove\"]}," +
        "\"resource_id\":{\"type\":\"string\",\"description\":\"Existing MCP resource id or server id; required for enable/disable/test/remove and optional for upsert.\"}," +
        "\"input\":{\"type\":\"object\",\"description\":\"upsert: id, name, transport (stdio|http|sse), url, command, args[], env{}, headers{}, cwd, enabled, project_id, expected_revision. import_json: json, project_id, overwrite, default_enabled. Masked env/header values keep the stored secret.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string PromptApplySchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"target\":{\"type\":\"string\",\"enum\":[\"fragment\",\"pipeline\"],\"description\":\"Whether to mutate a prompt fragment or a prompt pipeline.\"}," +
        "\"action\":{\"type\":\"string\",\"enum\":[\"create\",\"update\",\"clone\",\"publish\",\"rollback\"]}," +
        "\"fragment_id\":{\"type\":\"string\",\"description\":\"Prompt fragment id (GUID) or key for target=fragment.\"}," +
        "\"pipeline_id\":{\"type\":\"string\",\"description\":\"Prompt pipeline id (GUID) or slug for target=pipeline.\"}," +
        "\"version_id\":{\"type\":\"string\",\"description\":\"Target version id or version number for rollback.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Optimistic revision check when updating or publishing.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Fragment fields: key, title, scope (global|workspace|agent), category, target_agent_id, target_agent_slug, content, priority, enabled, new_key, target_version, change_summary. Pipeline fields: display_name, slug, description, graph { nodes[], edges[], canvas_layout }, nodes[], edges[]. Node types: template, fragment_ref, workspace_rules, skills_index, condition, variable, stage, stage_select, retrieval, memory, budget, assemble, context_trim (with id/node_key, placement, content/template, fragment_key). Publishing requires >=1 template/fragment_ref node, >=1 assemble node, and acyclic edges.\"}" +
        "},\"required\":[\"target\",\"action\"],\"additionalProperties\":false}";

    private const string ModelProfileApplySchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"inspect\",\"update\"]}," +
        "\"provider_id\":{\"type\":\"string\",\"description\":\"Enabled model provider id (GUID) or name.\"}," +
        "\"model_id\":{\"type\":\"string\",\"description\":\"Model id on that provider.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Optional optimistic revision of the provider configuration.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Profile metadata for update: specialties (string[]), tier (fast|balanced|deep), notes (string), context_window (integer > 0), cost_tier (low|medium|high). Never accepts or returns secrets.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string ContextMemoryApplySchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"update_policy\",\"set_strategy\",\"upsert_memory\",\"supersede_memory\",\"revoke_memory\",\"revert_memory\"]}," +
        "\"policy\":{\"type\":\"object\",\"description\":\"Fields for update_policy/set_strategy: strategy (direct|managed|deep_curator), recall_fidelity (summary_only|balanced|verbatim_first), compaction_trigger_ratio (0.40..0.95), recent_message_limit (4..200), default_token_budget, allow_cross_session_recall, offload_large_tool_payload_bytes (>=1024), evolution_mode (auto_evolve|candidate_only), candidate_only, retrieval_limit, core_markdown_max_tokens (256..8000), project_markdown_projection, project_memory_path, companion_can_write_project_memory, expected_content_hash.\"}," +
        "\"memory\":{\"type\":\"object\",\"description\":\"Memory item payload for upsert_memory/supersede_memory: scope (principal|workspace|project|agent|companion), kind (fact|preference|decision|success_pattern|failure_pattern|task_template|supervision_rule|bond|episode), section, content, project_id, companion_id, supersede_item_id, applicability, expiry_condition, reason, is_companion_writer, respect_evolution_mode.\"}," +
        "\"item_id\":{\"type\":\"string\",\"description\":\"Target memory item GUID for supersede_memory, revoke_memory, or revert_memory.\"}," +
        "\"target_version\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Optional version number to restore for revert_memory (defaults to previous version).\"}," +
        "\"reason\":{\"type\":\"string\",\"description\":\"Audit reason recorded in the immutable version ledger.\"}," +
        "\"expected_content_hash\":{\"type\":\"string\",\"description\":\"Optional optimistic SHA-256 hash when updating policy.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Optional alias for policy or memory object fields.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";
}
