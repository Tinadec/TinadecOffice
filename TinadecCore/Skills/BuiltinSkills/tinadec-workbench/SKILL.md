---
name: tinadec-workbench
description: Inspect and change Tinadec's own agents, modes, graph topology, skills and MCP bindings through governed tools. Use when the user asks to edit a team, add or remove a graph node, change an agent's tools, or publish a mode.
version: 1.0.0
---

# Tinadec workbench configuration

You can change the workbench itself. The tools are `tinadec_*` and every write is approval-gated. A change applies to **new runs only**: an admitted run keeps the snapshot it froze.

## Read before write

1. `tinadec_inspect` with `target: "agents"` to see the roster and which agents are built in.
2. `tinadec_inspect` with `target: "modes"` to see modes, status, version and whether a mode is pack-managed.
3. `tinadec_inspect` with `target: "mode"` and a `mode_id` to read nodes, edges and validation warnings.

Built-in (`managed` / `is_built_in`) objects are read-only. To customize one, clone it first; never try to edit it in place.

## Write loop

```
inspect → clone or create draft → apply → validate → publish
```

- `tinadec_mode_apply` creates or updates a **draft**. It runs `GraphValidation` first and returns structured diagnostics. Fix diagnostics and call it again.
- Every edge needs a stable `edge_key`. Reuse the key from inspect. Omitting it is an error, not a request to generate a new one.
- `tinadec_mode_publish` validates bindings and publishes. It returns `topology_hash` and warnings. Do not publish a draft with `EMPTY_EFFECTIVE_TOOLS` unless the user accepts it.
- Removing a node or edge is an explicit delete in `tinadec_mode_apply`; the server removes it from the draft. Never emulate deletion by renaming.
- `tinadec_agent_apply` only touches custom agents. Changing tools on a built-in agent is refused; clone it and reference the clone from the mode.
- `tinadec_skill_apply` and `tinadec_mcp_apply` reuse the governed resource services. Secrets are write-only; a masked value means "unchanged", not the real value.

## Team and graph semantics

- The conversation identity is chosen by the node marker / capability (`user.respond`), then by the literal `meeting`. Keep exactly one marked conversation node on the operation layer.
- An execution node needs a duty, input/output contract, success criteria and, when it may dispatch, `allowed_dispatch_targets`.
- `free_form` permits parallel dispatch; `deterministic` follows declared edges; `solo_dispatch` means the conversation identity does the work itself.
- `max_agents_per_run` and the spawn ceilings are hard limits. A draft that exceeds them fails publish, not runtime.

## Examples

**Add a reviewer to Team**

1. `tinadec_inspect(target:"mode", mode_id:"free_director")`
2. `tinadec_mode_apply(action:"clone", mode_id:"free_director", name:"Team + reviewer")` (if the source is managed)
3. `tinadec_mode_apply(action:"upsert", mode_id:<draft>, nodes:[...existing..., reviewer node], edges:[] , edge_keys:{...})`
4. `tinadec_mode_publish(mode_id:<draft>)`

**Turn on MCP for the conversation identity**

1. Confirm the server exists: `tinadec_inspect(target:"mcp")`.
2. Bind it to `meeting` through `tinadec_mcp_apply` or the tool settings.
3. If `meeting` lacks `mcp_*`, grant the capability with `tinadec_agent_apply` on a clone, or use an executor that already holds it. Do not tell the user MCP is "configured" until `mcp_list` returns the server in a new run.

## Do not

- Do not publish a mode the user has not reviewed when the diff changes who can write.
- Do not edit `runtime.toml` through shell to bypass approval; runtime policy ceilings are host-owned.
- Do not claim a change is live. Say "it applies to the next run".
