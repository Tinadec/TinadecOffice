---
name: tinadec-agent-architect
description: Workflow for designing and publishing specialized Tinadec agents, prompt pipelines, and multi-agent teams/modes using live model profiles, skills, MCP servers, and tinadec_* self-config tools. Use when creating a new specialist agent, tuning model selection, or composing a custom workflow.
version: 1.0.0
---

# Tinadec agent architect

Follow this deliberate design loop whenever creating a specialist agent, choosing an optimal model/tool setup, or assembling a custom multi-agent mode.

## 1. Inspect available capabilities first

Before writing any definition, discover what the host actually has enabled:

1. **Models & specialties**: call `tinadec_inspect` `{ "target": "models" }` to inspect enabled providers, available models, existing routes, health status, and per-model `model_profiles` (`specialties`, `tier`, `notes`, `context_window`, `cost_tier`).
   - If a model's profile is missing or needs better routing hints (for example tagging a model with `specialties: ["code", "reasoning"]` and `tier: "deep"`), update non-secret metadata with `tinadec_model_profile_apply`.
2. **Existing agents & catalog**: call `tinadec_inspect` `{ "target": "agents" }` and `{ "target": "catalog" }` so you reuse proven roles rather than duplicating them, and inspect specific agents with `{ "target": "agent", "agent_id": "<slug_or_id>" }`.
3. **Prompts, Skills, & MCP**: call `tinadec_inspect` for `prompts`, `skills`, and `mcp` to find reusable prompt pipelines/fragments, domain skills, and connected MCP tools.

## 2. Design a tight specialist specification

A strong agent has focused responsibility, minimal blast radius, and clear handoffs:

- **Model strategy**: match the task difficulty to the `models` inventory:
  - Deep architecture/code/audit -> `deep` tier / reasoning-specialized model.
  - High-volume classification, routing, or lightweight edits -> `fast` / `low` cost tier model.
  - Specify `payload.model_binding` (provider + model + strategy/tier) when pinning a model rather than relying solely on defaults.
- **Minimal `tool_scope`**: grant only the tools needed for the job (e.g., a reviewer/planner keeps read-only tools plus `tinadec_inspect`, while an implementer gets `read_file`, `write_file`, `edit_file`, `command_run`). Do not grant unrestricted write tools to analysis-only agents.
- **Per-agent Skill & MCP bindings**: attach only relevant `tool_settings.skill_resource_ids` and `tool_settings.mcp_server_ids`.
- **Prompt architecture**: either attach an existing `base_prompt_pipeline_id`, or craft a modular fragment + pipeline via `tinadec_prompt_apply` (`target: "fragment"` / `target: "pipeline"`) and publish the pipeline before linking it.

## 3. Create, verify, and publish the agent

1. Create from scratch or clone a built-in archetype:
   - `tinadec_agent_apply` `{ "action": "create", "payload": { "display_name": "...", "slug": "...", "layer": "execution", "role": "...", "description": "...", "system_prompt": "...", "capabilities": ["..."], "tool_scope": ["..."], "base_prompt_pipeline_id": "<pipeline_id>", "model_binding": { "provider_id": "...", "model_id": "...", "strategy": "fixed" }, "tool_settings": { "mcp_server_ids": [], "skill_resource_ids": [] }, "source_kind": "agent_generated" } }`
   - Omit `source_kind` or set `"agent_generated"` when creating via an agent runtime; provenance is recorded automatically.
2. Inspect the resulting agent (`tinadec_inspect` `{ "target": "agent", "agent_id": "<agent_id>" }`) to confirm prompt, pipeline, model binding, and tool scope.
3. Publish so the agent gets a frozen immutable version:
   - `tinadec_agent_apply` `{ "action": "publish", "agent_id": "<agent_id>", "expected_revision": <rev> }`

## 4. Optionally compose or update a Team / Mode

If the new agent should collaborate inside a mode or workflow:

1. Inspect the target mode (`tinadec_inspect` `{ "target": "mode", "mode_id": "<mode_id_or_slug>" }`). Clone pack-managed modes first with `tinadec_mode_apply` (`action: "clone"`).
2. Add the published agent node with a clear `duty`, input/output expectations, `allowed_dispatch_targets`, and edges (each with a unique `edge_key`), setting `orchestration_tier` (`free_form`, `self_dispatch`, `deterministic`, or `solo_dispatch`) deliberately.
3. Publish with `tinadec_mode_publish`, fix any diagnostics or `EMPTY_EFFECTIVE_TOOLS` warnings, and inform the user the new agent/mode is ready for the next run.
