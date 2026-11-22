---
name: tinadec-workbench
description: Inspect and configure Tinadec's agents, modes, graph topology, prompts, model profiles, skills, and MCP bindings. Use when editing teams, adding or wiring graph nodes, tuning prompts/models, or publishing modes.
version: 1.1.0
---

# Tinadec workbench configuration

You can inspect and configure the Tinadec workbench directly with the `tinadec_*` tools. Self-config tools execute immediately (with validation, optimistic revision checks, and version history snapshots preserved). Changes apply to **new runs only**: an already-admitted run keeps its frozen snapshot.

## Read before write

1. `tinadec_inspect` with `target: "agents"` (or `target: "agent", agent_id: "<id_or_slug>"` for full `system_prompt`, `tool_scope`, `model_strategy`, `base_prompt_pipeline_id`, and `source_kind`), and `target: "tools"` to list all valid tool IDs.
2. `tinadec_inspect` with `target: "modes"` to list modes, status, version, `orchestration_tier`, and pack management flags.
3. `tinadec_inspect` with `target: "mode"` and `mode_id: "<guid_or_slug>"` to read `revision`, `instructions`, `orchestration_tier`, `bindings`, `nodes[]`, `edges[]` (with `edge_key`), and validation diagnostics.
4. `tinadec_inspect` with `target: "models"`, `target: "prompts"`, or `target: "prompt_pipeline", pipeline_id: "<id_or_slug>"` before picking model strategies or prompt pipelines.
5. `tinadec_inspect` with `target: "context_memory"` (optional `companion_id`) to read the `[context]`/`[memory]` `runtime.toml` policy, hot Markdown token budgets/hashes across `principal`/`project`/`companion`, and 3-tier strategy presets (`direct`, `managed`, `deep_curator`).

Built-in / pack-managed (`pack_managed` / `is_built_in`) records are read-only. Clone them first before customizing.

## Write loop

```
inspect -> clone or create draft -> apply/validate -> publish
```

- `tinadec_mode_apply` creates, clones, upserts, or deletes a **draft** mode (`mode_id` accepts GUID or slug). Send all fields under top-level `payload` (`display_name`, `description`, `instructions`, `orchestration_tier`, `bindings`, `runtime_policy`, `canvas_layout`, `nodes`, `edges`) and pass `expected_revision` on edits.
- Every edge in `payload.edges[]` must carry a non-empty `edge_key`, `source_node_key`, and `target_node_key`. Nodes and edges omitted from `payload.nodes[]` / `payload.edges[]` on `upsert` are deleted.
- `tinadec_mode_publish` validates bindings and publishes a mode (`mode_id` by GUID or slug), returning `topology_hash`, `version`, `revision`, and warnings.
- `tinadec_agent_apply` supports `create`, `clone`, `update`, and `publish`. Pass write fields inside `payload` (including `base_prompt_pipeline_id`, `model_binding`, `tool_settings`, `source_kind`, `provenance`). Agent-created or cloned custom agents default to `source_kind: "agent_generated"`.
- `tinadec_prompt_apply` creates, updates, clones, publishes, or rolls back prompt fragments (`target: "fragment"`) and prompt pipelines (`target: "pipeline"`).
- `tinadec_model_profile_apply` inspects or updates non-secret `model_profiles` (`specialties`, `tier`, `notes`, `context_window`, `cost_tier`) on enabled providers.
- `tinadec_skill_apply` and `tinadec_mcp_apply` manage workspace/shared Skills and MCP servers; masked env values keep the stored secret.
- `tinadec_context_memory_apply` configures `[context]`/`[memory]` `runtime.toml` policy (`action: "update_policy"` | `"set_strategy"` with `policy` and optional `expected_content_hash`) or mutates durable memory items (`upsert_memory`, `supersede_memory`, `revoke_memory`, `revert_memory`). Pass `companion_id` inside `memory` when updating companion memory; companions cannot write `.tinadec/memory/project-core.md` unless `companion_can_write_project_memory` is true. Use `recall_conversation_history` when you need exact earlier turns (`summary_only` | `balanced` | `verbatim_first`).

## Team and graph semantics

- Keep one conversation entry node on the `operation` layer (marked `user.respond` or slug `meeting`).
- Canonical `orchestration_tier` values are `solo_dispatch`, `free_form`, `self_dispatch`, and `deterministic`.
- Execution nodes declare `duty`, contracts, and `allowed_dispatch_targets` / `spawnable_agent_types` when dispatching peers or workers.

## Examples (matching tool schema)

**Clone and update a Team mode to add a reviewer**

1. Inspect:
   `tinadec_inspect` `{ "target": "mode", "mode_id": "free_director" }`
2. Clone the pack-managed mode (`name` goes inside `payload.display_name`):
   `tinadec_mode_apply` `{ "action": "clone", "mode_id": "free_director", "payload": { "display_name": "Team + Reviewer" } }`
3. Upsert the draft with all kept nodes + reviewer node (putting `duty` and dispatch lists inside `node.relationship`) and explicit `edge_key` values:
   `tinadec_mode_apply` `{ "action": "upsert", "mode_id": "<draft_mode_id>", "expected_revision": <revision>, "payload": { "display_name": "Team + Reviewer", "orchestration_tier": "free_form", "instructions": "...", "nodes": [ ...existingNodes, { "node_key": "reviewer", "agent_definition_id": "<reviewer_agent_id>", "layer": "execution", "relationship": { "duty": "Verify diff and tests", "inputs_outputs": {}, "allowed_dispatch_targets": [], "success_criteria": [], "agent_types": [] } } ], "edges": [ { "edge_key": "meeting-to-reviewer", "source_node_key": "meeting", "target_node_key": "reviewer", "condition": {} } ] } }`
4. Publish:
   `tinadec_mode_publish` `{ "mode_id": "<draft_mode_id>", "expected_revision": <new_revision> }`

**Bind an MCP server or Skill to a custom agent clone**

1. Confirm server/skill IDs: `tinadec_inspect` `{ "target": "mcp" }` and `{ "target": "skills" }`.
2. Clone built-in agent or update custom agent with `payload.tool_settings`:
   `tinadec_agent_apply` `{ "action": "update", "agent_id": "<custom_agent_id>", "payload": { "tool_scope": ["read_file", "mcp_list", "mcp_invoke"], "tool_settings": { "mcp_server_ids": ["<mcp_id>"], "skill_resource_ids": ["<skill_id>"] } } }`
3. Publish:
   `tinadec_agent_apply` `{ "action": "publish", "agent_id": "<custom_agent_id>" }`

## Guardrails

- Never fabricate revision numbers; always read `expected_revision` from `tinadec_inspect` or the preceding apply response.
- Never claim a change affects the currently running session; tell the user it applies to new runs.
