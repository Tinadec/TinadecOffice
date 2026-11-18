using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manifest metadata for the <c>tinadec_*</c> workbench-configuration tools. This class
/// knows ids, descriptions and schemas only — never a service — so it can sit in the
/// Tools module while the execution lives behind <see cref="ITinadecConfigToolGateway"/>
/// in Runtime.
///
/// Policy shape: <c>tinadec_inspect</c> is read-only (no approval, low risk, safe to
/// retry). Every write is high risk, approval-gated and NOT workspace-mutating — these
/// tools change Core configuration, not the user's files, so the pre-write workspace
/// snapshot and the file-path guards do not apply, but the human approval gate does.
/// A retried write is unsafe: the same call may already have created a version.
/// </summary>
internal static class CoreTinadecConfigTools
{
    public static ToolManifestEntryDto ManifestEntry(string toolId) => toolId switch
    {
        CoreVirtualToolPolicy.TinadecInspectToolId => Entry(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            "Read the live workbench configuration before changing it: agents, modes, one mode's graph, "
            + "the authoring catalog, skills, MCP servers, or the market. Results are sanitized (no secrets) "
            + "and list at most 50 rows. Call this first whenever the user asks to inspect or change agents, "
            + "modes, skills or MCP.",
            InspectSchema, requiresApproval: false, risk: "low", retrySafety: "safe", confirmation: []),
        CoreVirtualToolPolicy.TinadecAgentApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecAgentApplyToolId,
            "Create, clone or update a CUSTOM agent definition. Pack-managed (built-in) agents are read-only: "
            + "clone one to get an editable draft. Changes land on a draft and only reach new runs after publish.",
            AgentSchema, requiresApproval: true, risk: "high", retrySafety: "unsafe", confirmation: ["action"]),
        CoreVirtualToolPolicy.TinadecModeApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecModeApplyToolId,
            "Create, clone, upsert or delete a DRAFT agent mode and its topology. Nodes and edges absent from the "
            + "submitted arrays are deleted, every edge must carry its own edge_key, and the graph is validated "
            + "before saving. Returns the saved graph plus structured diagnostics when validation fails.",
            ModeSchema, requiresApproval: true, risk: "high", retrySafety: "unsafe", confirmation: ["action"]),
        CoreVirtualToolPolicy.TinadecModePublishToolId => Entry(
            CoreVirtualToolPolicy.TinadecModePublishToolId,
            "Publish a draft mode: freezes agent/prompt versions and every node's effective tools, validates the "
            + "graph and returns topology_hash, version, revision and warnings. Pack-managed modes must be cloned first.",
            PublishSchema, requiresApproval: true, risk: "high", retrySafety: "unsafe", confirmation: ["mode_id"]),
        CoreVirtualToolPolicy.TinadecSkillApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecSkillApplyToolId,
            "Install, import, enable, disable or remove a Skill resource. Writes stay governed (they are queued "
            + "as approval-required actions), never return skill content in bulk, and built-in skills are read-only.",
            SkillSchema, requiresApproval: true, risk: "high", retrySafety: "unsafe", confirmation: ["action"]),
        CoreVirtualToolPolicy.TinadecMcpApplyToolId => Entry(
            CoreVirtualToolPolicy.TinadecMcpApplyToolId,
            "Upsert, test or remove a Core-managed MCP server registration. Environment values come back masked; "
            + "sending a masked value back means 'keep the stored secret'. Test reports connection status only.",
            McpSchema, requiresApproval: true, risk: "high", retrySafety: "unsafe", confirmation: ["action"]),
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
        "\"target\":{\"type\":\"string\",\"enum\":[\"agents\",\"modes\",\"mode\",\"catalog\",\"skills\",\"mcp\",\"market\"],\"description\":\"What to read. 'mode' needs mode_id; 'catalog' lists the agents and prompt pipelines a mode can be built from.\"}," +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Mode id, required for target=mode.\"}," +
        "\"query\":{\"type\":\"string\",\"description\":\"Optional literal filter matched against names, slugs and descriptions.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"description\":\"Rows to return; defaults to 20 and is capped at 50.\"}" +
        "},\"required\":[\"target\"],\"additionalProperties\":false}";

    private const string AgentSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"clone\",\"create\",\"update\"]}," +
        "\"agent_id\":{\"type\":\"string\",\"description\":\"Agent id: the source for clone, the target for update.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"Write fields: display_name, slug, layer (operation|execution), role, capabilities[], tool_scope[], system_prompt, description, model_strategy, enabled.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string ModeSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"clone\",\"create\",\"upsert\",\"delete\"]}," +
        "\"mode_id\":{\"type\":\"string\",\"description\":\"Target mode: source for clone, target for upsert/delete.\"}," +
        "\"expected_revision\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"The revision tinadec_inspect returned; a stale value is refused with a conflict.\"}," +
        "\"payload\":{\"type\":\"object\",\"description\":\"display_name, description, runtime_policy, canvas_layout, nodes[], edges[]. Nodes need node_key + agent_definition_id + layer; edges need edge_key + source_node_key + target_node_key.\"}" +
        "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string PublishSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"mode_id\":{\"type\":\"string\"}," +
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
}
