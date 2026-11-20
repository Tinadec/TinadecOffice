---
name: tinadec-agent-architect
description: Workflow for designing and publishing specialized Tinadec agents, prompt pipelines, and multi-agent teams/modes using live model profiles, skills, MCP servers, and tinadec_* self-config tools. Use when creating a new specialist agent, tuning model selection, or composing a custom workflow.
version: 1.1.0
---

# Tinadec agent architect

Follow this loop whenever you create a specialist agent, choose a model/tool setup, or assemble a multi-agent mode. Every step below is a real `tinadec_*` call with the real argument shape — there is no step you "just know", and nothing here needs a human in the editor.

## The loop, in one line

```
inspect models -> inspect prompts/agents -> design -> create agent (agent_generated)
-> publish agent -> inspect mode -> clone or create mode -> upsert nodes+bindings+tier
-> publish mode
```

## 1. Read the live inventory first

1. **Models and profiles** — `tinadec_inspect` `{ "target": "models" }`.
   Returns enabled providers with their `models[]`, `routes[]`, `health[]`, and the per-model `model_profiles` (`specialties`, `tier`, `notes`, `context_window`, `cost_tier`).
   If a model's profile is missing or wrong for the job, fix it before you build on it:
   `tinadec_model_profile_apply` `{ "action": "update", "provider_id": "<provider_id>", "model_id": "<model_id>", "expected_revision": <provider revision>, "payload": { "specialties": ["code", "reasoning"], "tier": "deep", "cost_tier": "high" } }`.
   Read `model_profiles` as *evidence for a choice*, not decoration: `tier: "fast"` for routing/classification work, `tier: "deep"` for architecture/audit, `context_window` before you hand an agent a large file set.
2. **Existing agents** — `tinadec_inspect` `{ "target": "agents" }` for the directory, then `{ "target": "agent", "agent_id": "<slug_or_guid>" }` for the full `system_prompt` / `model_strategy` / `tool_scope` / `base_prompt_pipeline_id` / `source_kind` of one. Reuse a role that already exists rather than duplicating it.
3. **Prompt pipelines** — `tinadec_inspect` `{ "target": "prompts" }` for fragments and pipelines; `{ "target": "prompt_pipeline", "pipeline_id": "<slug_or_guid>" }` for one pipeline's full `graph` (nodes + edges, the shape you must send back if you edit it).
4. **Skills and MCP** — `tinadec_inspect` `{ "target": "skills" }` and `{ "target": "mcp" }`. You need the real `resource_id`s here; the ids you put in `tool_settings` are validated against these catalogs, so a guessed id fails the write.

## 2. Design a tight specification

- **Model strategy** — choose from what `models` actually reported. `capabilities` and `model_profiles.tier` are the evidence; `health` tells you which providers are cooling.
- **Minimal `tool_scope`** — grant only what the job needs. A reviewer or planner holds read-only tools plus `tinadec_inspect`; an implementer gets `read_file` / `write_file` / `replace_lines` / `shell` / `git_*`. Never hand an analysis-only agent a mutating tool.
- **Prompt architecture** — either bind an existing pipeline with `base_prompt_pipeline_id`, or author a new one:
  1. `tinadec_prompt_apply` `{ "target": "fragment", "action": "create", "payload": { "key": "<stable-key>", "title": "...", "category": "persona", "content": "..." } }`
  2. `tinadec_prompt_apply` `{ "target": "pipeline", "action": "create", "payload": { "display_name": "...", "graph": { "nodes": [ { "node_key": "role", "kind": "template", "config": { "content": "..." } }, { "node_key": "out", "kind": "assemble", "config": {} } ], "edges": [ { "source_node_key": "role", "target_node_key": "out" } ] } } }`
  3. `tinadec_prompt_apply` `{ "target": "pipeline", "action": "publish", "pipeline_id": "<pipeline_id>", "expected_revision": <revision> }` — a pipeline needs at least one `template` and one `assemble` node to publish.
  4. Put `<pipeline_id>` (the GUID, not the slug) into the agent's `base_prompt_pipeline_id`.
  Built-in fragments and pack pipelines are read-only: use `action: "clone"` (fragments take `payload.new_key`) and edit the copy.

## 3. Create, verify, publish

1. **Create** — pass every write field under `payload`:
```json
{ "action": "create", "payload": {
  "display_name": "Migration Auditor", "slug": "migration-auditor",
  "layer": "execution", "role": "audit", "description": "Reads migrations and reports risk.",
  "system_prompt": "You audit database migrations. Cite file and line for every finding.",
  "capabilities": ["code.audit"], "tool_scope": ["read_file", "file_search", "git_diff"],
  "base_prompt_pipeline_id": "<pipeline_id>",
  "model_binding": { "mode": "fixed", "provider_instance_id": "<provider_id>", "model": "<model_id>" },
  "tool_settings": { "mcp_server_ids": [], "skill_resource_ids": ["<skill_resource_id>"] },
  "source_kind": "agent_generated",
  "provenance": { "reason": "asked to add an audit specialist" }
} }
```
   `source_kind` defaults to `agent_generated` when the call comes from a run, and `provenance` records the calling agent and run automatically. Set `"source_kind": "custom"` only when you are deliberately authoring something a person will own.
2. **Verify** — `tinadec_inspect` `{ "target": "agent", "agent_id": "<agent_id or slug>" }`. Check `base_prompt_pipeline_id`, `model_strategy`, `runtime_binding` and the full `tool_scope` are what you intended, not what you hoped.
3. **Publish** — `tinadec_agent_apply` `{ "action": "publish", "agent_id": "<agent_id>", "expected_revision": <revision from the create/verify response> }`.
   This writes the immutable agent version. **`tinadec_mode_publish` refuses a node whose agent is still a draft**, so this step is not optional.

## 4. Compose or update a mode

1. **Inspect the mode** — `tinadec_inspect` `{ "target": "mode", "mode_id": "<slug_or_guid>" }`. Read its `revision`, `instructions`, `orchestration_tier`, `bindings` and current `nodes[]`/`edges[]`.
   Pack-managed modes (`built_in: true`) are read-only: clone first with `tinadec_mode_apply` `{ "action": "clone", "mode_id": "<slug>", "payload": { "display_name": "..." } }`.
2. **Upsert the draft** — `tinadec_mode_apply` `{ "action": "upsert", "mode_id": "<draft id>", "expected_revision": <revision>, "payload": { ... } }`:
   - `orchestration_tier` — decide it, do not let it be guessed: `deterministic` for a declared chain, `self_dispatch` for a graph where nodes dispatch peers, `free_form` for a director that assembles a team at runtime, `solo_dispatch` for one identity doing the work itself.
   - `instructions` — the team-level collaboration prose (≤4000 chars).
   - `nodes[]` — each needs `node_key`, `agent_definition_id` (the **GUID**, not the slug), `layer` matching the agent's own layer, and a `relationship` object: `{ duty, inputs_outputs, allowed_dispatch_targets[], success_criteria[], agent_types[] }`. `agent_types` is the spawn whitelist — an empty list on a node that is expected to spawn means dispatch will be denied.
   - `edges[]` — each needs `edge_key`, `source_node_key`, `target_node_key`. A submitted `edges[]` array is a claim about the full set: edges you omit are deleted.
   - `bindings[]` — the mode-level team list. A node binding is `{ "node_key": "<key>", "agent_ref": "agent:<slug>", "tool_switches": { ... }, "envelope": { "spawn": { "max_depth": 2, "max_agents_per_run": 8, "max_parallel_workers": 4 }, "resources": { "read": [""], "write": [""] } } }`. A node-LESS binding attaches an envelope to a spawnable template the director may create at runtime.
   - Leave the eight `tinadec_*` config tools **on** in the conversation node's `tool_switches` if this mode should be able to reconfigure itself; switch everything else it does not need off.
3. **Publish** — `tinadec_mode_publish` `{ "mode_id": "<mode_id or slug>", "expected_revision": <revision from the upsert> }`.
   Fix any diagnostic it returns and retry. An `EMPTY_EFFECTIVE_TOOLS` warning means the conversation node ended up with no usable tool.

## 5. Report honestly

Tell the user which agent(s) and mode you created or changed, their versions, and that the change applies to **new runs only** — an already-admitted run keeps its frozen snapshot. If a step was refused, quote the code and message rather than reporting success.
