---
name: tinadec-workbench
description: Inspect and configure Tinadec's agents, modes, graph topology, prompts, model profiles, skills, and MCP bindings. Use when editing teams, adding or wiring graph nodes, tuning prompts/models, or publishing modes.
version: 1.2.0
---

# Tinadec workbench configuration

You can inspect and configure the Tinadec workbench directly with the `tinadec_*` tools. Self-config tools execute immediately (with validation, optimistic revision checks, and version history snapshots preserved). Changes apply to **new runs only**: an already-admitted run keeps its frozen snapshot.

## Read before write

`tinadec_inspect` takes one `target`:

| target | needs | returns |
| --- | --- | --- |
| `agents` | — | list projection: id, slug, layer, status, capabilities, tool_count, pack flags |
| `agent` | `agent_id` (GUID **or** slug) | the FULL definition: `system_prompt`, `model_strategy`, `tool_scope`, `capabilities`, `base_prompt_pipeline_id`, `source_kind`, `provenance`, `runtime_binding` |
| `modes` | — | list projection incl. `status`, `revision`, `orchestration_tier`, `node_count`, pack flags |
| `mode` | `mode_id` (GUID **or** slug) | `revision`, `instructions`, `orchestration_tier`, `bindings`, `nodes[]` (each with `relationship`), `edges[]` |
| `catalog` | — | the authoring shortlist: agents with their tool ids + prompt pipelines |
| `models` | — | sanitized enabled providers, models, routes, health, **and `model_profiles`** |
| `prompts` | — | fragments (with `content`) and pipelines (list) |
| `prompt_pipeline` | `pipeline_id` (GUID **or** slug) | the full `graph` (nodes + edges) + latest published version |
| `skills` / `mcp` / `market` | — | those catalogs |

Pack-managed records (`pack_id` set, `built_in: true`, `writable: false`) are read-only. Clone them first.

## Write loop

```
inspect -> clone or create draft -> apply/validate -> publish
```

- **`tinadec_mode_apply`** — `action: create | clone | upsert | delete` on a **draft** mode. `mode_id` accepts a GUID or a slug (`free_director`, `plan`, …). All write fields go under top-level `payload`.
  - `payload` fields: `display_name`, `description`, `instructions` (≤4000 chars), `orchestration_tier`, `bindings[]`, `runtime_policy`, `canvas_layout`, `nodes[]`, `edges[]`.
  - `orchestration_tier` ∈ `solo | solo_dispatch | free_form | self_dispatch | deterministic`. Omit it to let the runtime derive the tier from the topology.
  - `bindings[]` is the mode-level team list. Each entry needs `node_key` **or** `agent_ref`; it may carry `duty_description_ref`, `tool_switches { <tool_id>: bool }`, `envelope { spawn { max_depth, max_agents_per_run, max_parallel_workers }, resources { read[], write[] }, capabilities[], tools[] }`, `includes_core_reserved`.
  - Every edge needs `edge_key`, `source_node_key`, `target_node_key`. A **submitted** `nodes[]`/`edges[]` array is a claim about the full state: anything it omits is deleted. Omitting the array entirely leaves that state alone.
  - Pass `expected_revision` on `upsert`; a stale value is refused with `conflict`.
- **`tinadec_mode_publish`** — `{ mode_id, expected_revision }`. Validates the graph and the team bindings, freezes agent/prompt versions, returns `topology_hash`, `version`, `revision`, `warnings`. **Every node must reference a published agent.**
- **`tinadec_agent_apply`** — `action: create | clone | update | publish`.
  - `create`/`update`/`publish` target an agent by `agent_id` (GUID or slug); `clone` names its source the same way.
  - `payload` fields: `display_name`, `slug`, `layer` (`operation`|`execution`), `role`, `capabilities[]`, `tool_scope[]`, `system_prompt`, `description`, `model_strategy`, `enabled`, `base_prompt_pipeline_id`, `source_kind`, `provenance`, `model_binding`, `tool_settings`.
  - `base_prompt_pipeline_id` must be an existing prompt pipeline GUID in this workspace; a fabricated id is refused at write time.
  - `model_binding` — `{ mode: "inherit" | "route" | "fixed", provider_instance_id, model, route_purpose }`. `fixed` needs `provider_instance_id`; `route` needs `route_purpose`; omit `mode` for `fixed`. This writes the same runtime-binding override the agent settings page writes, and a frozen run prefers it over `model_strategy`.
  - `tool_settings` — `{ mcp_server_ids: [], skill_resource_ids: [], mcp_enabled, skills_enabled }`. A supplied list **replaces** that binding (`[]` means none). An unknown or stale resource id fails the call; it never half-writes.
  - `provenance` — `{ created_by_agent_id, created_by_run_id, reason }`. Omitted values are filled from the calling run.
  - **`action: "publish"`** freezes an immutable agent version and flips the row to `published`. A draft agent cannot enter a mode; publish is the step that makes it usable.
- **`tinadec_prompt_apply`** — `{ target: "fragment" | "pipeline", action }`.
  - `target: "fragment"`, `action: create | update | clone | publish | rollback`. Identify by `fragment_id` (GUID, or its unique `key`). `payload`: `key`, `title`, `scope`, `category`, `target_agent_id` / `target_agent_slug`, `content`, `priority`, `enabled`, `change_summary`; `clone` uses `new_key`. `rollback` needs `version_id` (GUID) or `target_version` (number).
  - `target: "pipeline"`, `action: create | update | clone | publish | rollback`. Identify by `pipeline_id` (GUID or slug). `payload`: `display_name`, `slug`, `description`, `graph { nodes[], edges[] }` — or send `nodes[]` + `edges[]` at the payload top level and the wrapper is built for you. Publishing requires at least one `template` node and one `assemble` node.
- **`tinadec_model_profile_apply`** — `{ action: "inspect" | "update", provider_id, model_id, expected_revision, payload }`.
  - `payload`: `specialties[]` (≤32 entries, ≤64 chars each), `tier` (`fast|balanced|deep`), `notes` (≤500 chars), `context_window` (positive int), `cost_tier` (`low|medium|high`). Omitted fields keep their stored value; send `null` to clear.
  - Only non-secret metadata. API keys and secret references are never read or written here.
- **`tinadec_skill_apply`** / **`tinadec_mcp_apply`** — manage workspace/shared Skills and MCP servers; masked env values keep the stored secret.

## Team and graph semantics

- A mode needs exactly one conversation node: an `operation`-layer node whose agent carries `user.respond` (or `user.converse`), marked `config.conversation: true`, or named `meeting`.
- `orchestration_tier` values are `solo_dispatch`, `free_form`, `self_dispatch`, `deterministic` (alias `solo`).
- A node's role contract lives in its **`relationship` object**, never as flat node fields:
  `relationship { duty, inputs_outputs, allowed_dispatch_targets[], success_criteria[], agent_types[], subscriptions[] }`.
  `agent_types` is the spawn whitelist; `subscriptions` uses governance topic names (`lease_conflict`, `worker_settled`, `user_steer`, `plan_tick`, `budget_tick`).
- The conversation node usually carries `tool_switches` that switch its business tools **off**, so a coordinating mode does not degrade into `solo_dispatch`. Leave the eight `tinadec_*` config tools **on** in modes that are allowed to reconfigure the workbench.

## Examples (matching tool schema)

**Clone a Team mode and add a reviewer node**

1. `tinadec_inspect` `{ "target": "mode", "mode_id": "free_director" }` → note `revision`.
2. `tinadec_mode_apply` `{ "action": "clone", "mode_id": "free_director", "payload": { "display_name": "Team + Reviewer" } }` → note the new `mode_id`.
3. `tinadec_mode_apply`:
```json
{
  "action": "upsert",
  "mode_id": "<draft_mode_id>",
  "expected_revision": <revision from step 2>,
  "payload": {
    "display_name": "Team + Reviewer",
    "orchestration_tier": "free_form",
    "instructions": "Send every diff to the reviewer before you answer.",
    "nodes": [
      { "node_key": "meeting", "agent_definition_id": "<meeting_agent_id>", "layer": "operation",
        "config": { "conversation": true },
        "relationship": { "duty": "理解用户目标并指挥执行层", "allowed_dispatch_targets": ["reviewer"], "agent_types": ["reviewer"], "inputs_outputs": { "receives": "user goal", "returns": "final answer" }, "success_criteria": ["目标被满足或明确说明阻塞"] } },
      { "node_key": "reviewer", "agent_definition_id": "<reviewer_agent_id>", "layer": "execution",
        "relationship": { "duty": "审查 diff 与测试结果", "allowed_dispatch_targets": [], "agent_types": [], "inputs_outputs": { "receives": "diff", "returns": "review findings" }, "success_criteria": ["每个问题都指向具体行"] } }
    ],
    "edges": [ { "edge_key": "meeting-to-reviewer", "source_node_key": "meeting", "target_node_key": "reviewer", "condition": {} } ]
  }
}
```
4. `tinadec_mode_publish` `{ "mode_id": "<draft_mode_id>", "expected_revision": <revision from step 3> }`

**Create and publish an agent, then bind an MCP server and a Skill to it**

1. `tinadec_inspect` `{ "target": "models" }` → pick a provider + model; `{ "target": "prompts" }` → pick a `base_prompt_pipeline_id`.
2. `tinadec_agent_apply`:
```json
{ "action": "create", "payload": {
  "display_name": "Migration Auditor", "slug": "migration-auditor", "layer": "execution",
  "role": "audit", "description": "Reads migrations and reports risk.",
  "system_prompt": "You audit database migrations. Report risk with evidence.",
  "capabilities": ["code.audit"], "tool_scope": ["read_file", "file_search", "git_diff"],
  "base_prompt_pipeline_id": "<pipeline_id>",
  "model_binding": { "mode": "fixed", "provider_instance_id": "<provider_id>", "model": "<model_id>" },
  "tool_settings": { "mcp_server_ids": ["<mcp_resource_id>"], "skill_resource_ids": ["<skill_resource_id>"] }
} }
```
3. `tinadec_agent_apply` `{ "action": "publish", "agent_id": "migration-auditor", "expected_revision": <revision from step 2> }`

**Tune a model profile**

1. `tinadec_inspect` `{ "target": "models" }` → find the provider id and the exact `model_id`.
2. `tinadec_model_profile_apply` `{ "action": "update", "provider_id": "<provider_id>", "model_id": "<model_id>", "expected_revision": <provider revision>, "payload": { "specialties": ["code", "reasoning"], "tier": "deep", "cost_tier": "high" } }`

## Guardrails

- Never fabricate revision numbers; always read `expected_revision` from `tinadec_inspect` or the preceding apply response.
- Never fabricate an id. `tinadec_inspect` returns real ids and slugs, and every resolver accepts either.
- A node needs a published agent. If publish says "must reference a published agent", publish the agent first.
- Never claim a change affects the currently running session; tell the user it applies to new runs.
