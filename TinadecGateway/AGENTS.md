# GATEWAY KNOWLEDGE

## OVERVIEW
Elysia TypeScript BFF/API layer. It proxies Core HTTP/SSE/debug routes and hosts Code tool endpoints for Desktop.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Server routes | `src/index.ts` | Elysia app, Swagger, manual CORS, `/api/v1/*`. |
| Core proxy | `src/coreClient.ts` | `coreUrl`, JSON proxy, SSE proxy. |
| Model/Agent center BFF | `src/modelAgentCenter.ts` | Stateless, secret-stripping aggregation for model resources and effective agent runtime bindings; includes unsupported-write validation. |
| Debug proxy | `src/debugProxy.ts` | Debug API + WebSocket URL helpers. |
| Code tools | `src/codeTools.ts` | Tool execution/fallback boundary and Gateway DTO adapters. |
| Tool-layer bridge | `src/toolLayerBridge.ts` | Workspace-scoped TinadecTools stdio process lifecycle and request correlation. |
| Tests | `src/coreClient.test.ts`, `src/codeTools.test.ts`, `src/modelAgentCenter.test.ts` | Node test runner + `tsx`; center tests cover normalization, diagnostics, binding validation, and secret stripping. |

## CONVENTIONS
- Package is ESM (`"type": "module"`); TypeScript uses `NodeNext`.
- Keep Gateway thin. Core owns state, approvals, model routes, sessions, events, and persistence.
- `GET /api/v1/model-center/overview` and `GET /api/v1/agent-center/overview` are transient BFF views over Core templates, provider instances, routes, agents, readiness, and ACP data. They must not persist or invent a second source of truth.
- Model Center reports only `configured_only` models derived from provider defaults and existing route overrides. Do not present this as live discovery or implement provider auto-selection in Gateway.
- Optional Core readiness, ACP, agent-mode, and candidate endpoints may degrade on `404/501` into response diagnostics; required catalog/provider/route/agent failures and Core unreachability remain errors.
- `POST /api/v1/model-center/provider-instances/:id/models/refresh` and `PUT /api/v1/agents/:agentId/runtime-binding` currently validate the public contract and return explicit `501` results. Do not store drafts, rewrite shared legacy routes, or return synthetic success before Core owns these capabilities.
- Center aggregation must recursively strip API keys and other secret fields. Keep CLI providers and ACP adapters separate; ACP-capable legacy providers are labeled `legacy_provider` rather than guessed or merged with adapters.
- Prompt fragment CRUD and prompt context preview routes are Core proxies only. Do not add prompt selection, token budgeting, or prompt assembly logic in Gateway.
- Harness manifest, tool search, and tool execution timeline routes are Core proxies only. Do not recompute agent layers, provider layers, risk policy, matched fields, approval summaries, or execution audit state in Gateway.
- `/api/v1/code/tools` publishes Tool-layer Code-suite metadata with snake_case public DTO fields. `src/codeTools.ts` keeps internal spec fields camelCase and maps them at the API boundary.
- `list_directory` maps to the C# TinadecTools `ls` tool; `search_files` and `glob_search` map to its `file_search` tool through `src/toolLayerBridge.ts`. Keep workspace/path validation and link traversal policy in TinadecTools; Gateway may only adapt request/result DTOs, hidden-entry filtering, ordering, and pagination.
- Tool-layer processes are cached per resolved workspace because TinadecTools snapshots its workspace at startup and keeps state. Calls reuse the existing stdio process; only a missing or exited instance is replaced, with `cwd` set to that workspace. Configure the executable with `TINADEC_TOOLS_BIN`; optional `TINADEC_TOOLS_ARGS` is a JSON string array.
- Code-suite tools include project templates, runtime probe, bash-like environment, debugging, editor, Git worktree manager, and Codex primitives.
- `project_templates` is read-only list/preview. `project_template_scaffold` writes files and must remain approval-gated; direct Gateway execution treats `approval_id` as the Core-supplied approval proof.
- `git_worktree_manager` is a legacy compatibility adapter for the complete migrated Git surface and must not execute Git locally. Direct Git mutation ids require a Core-verified `kind=git` approval and explicit confirmation; pull is fast-forward-only, merge/rebase are non-interactive lifecycle tools, and conflict preview/resolve share the Tool-layer three-way text engine.
- The HTTP Code-tool execute route must verify approval state against Core before passing approval-gated requests to `executeCodeTool`. Do not trust an arbitrary renderer-supplied `approval_id`.
- Manual CORS exists because `@elysiajs/cors` returned bad preflight behavior with the Node adapter.
- Use `setStatus(set, result.status)` when forwarding Core response status.
- OpenAPI docs are served at `/docs`.
- Default port is `TINADEC_GATEWAY_PORT ?? 48730`.

## ANTI-PATTERNS
- Do not add durable state here.
- Do not let Code tool execution bypass Core approval semantics; risky tools must remain blocked without approval context.
- Do not bypass Core contracts when forwarding `/api/v1/*` shapes.
- Do not remove local dev/Electron allowed origins without checking Desktop startup.
- Do not assume dependency diagnostics are valid until `npm install` has run; missing deps cause many false LSP errors.

## COMMANDS
```bash
npm run dev -w @tinadec/gateway
npm run build -w @tinadec/gateway
npm run test -w @tinadec/gateway
```

Target one test from `gateway/`:
```bash
node --test --import tsx src/coreClient.test.ts
```
