---
name: tinadec-mcp-setup
description: Configure and troubleshoot MCP servers in Tinadec (stdio, HTTP, environment references, agent grants, connection tests). Use when the user asks to add, test, or debug an MCP server.
version: 1.0.0
---

# MCP server setup in Tinadec

Tinadec stores MCP servers as governed resources in Core, freezes the selected servers into each run, and starts them from the TinadecTools process. Editing `mcp_servers.json` by hand does nothing in a managed run.

## The model-facing surface

Unless eager projection is enabled, the model sees three generic tools:

- `mcp_list` — list the servers and tools available to this run.
- `mcp_search` — find a tool by name or description.
- `mcp_invoke` — call a tool. This is approval-gated.

A server that is configured but not granted to the agent's tool scope is invisible to the model. `mcp_list` returning nothing usually means the grant is missing, not that the server is broken.

## Procedure for adding a server

1. Inspect what is bound: `tinadec_inspect` with `target: "mcp"` (or Settings → Tools → MCP).
2. Create the resource through the UI or `tinadec_mcp_apply`. Never paste a secret into a prompt; use the secret field or `${ENV_NAME}`.
3. Run the connection test. It performs a real handshake and `mcp_list`; a timeout names the step and the budget.
4. For `npx` / `uvx` servers, install the managed program first. A server whose program is missing freezes with `not_installed` and is refused at connect time.
5. Bind the server to the agent that should use it. The conversation identity must hold `mcp_list`, `mcp_search` and `mcp_invoke`; otherwise dispatch to an executor that does.
6. Start a new run. The server set is frozen at admission, so an in-flight run keeps the old set.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `mcp_list` shows nothing | agent tool scope lacks `mcp_*`, or no server is bound, or `mcp.enabled=false` |
| `not_installed` | install the managed program for the server |
| connect timeout | command is wrong, cwd is not readable, or the launcher cannot find `node`/`uvx` |
| secret missing | the secret reference was deleted; re-enter the value, never the mask |
| tool call rejected | `mcp_invoke` requires approval; ask the user or use an auto-approved run mode |

## Boundaries

- stdio is the only transport today; HTTP/SSE is a separate capability and may be unavailable.
- A server id is unique per scope; project scope shadows a shared server with the same `server_id`.
- Tools are not exposed one-per-server by default. Use `mcp_search` to find the exact tool name.
