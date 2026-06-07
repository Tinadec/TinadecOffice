# GATEWAY KNOWLEDGE

## OVERVIEW
Elysia TypeScript BFF/API layer. It proxies Core HTTP/SSE/debug routes and hosts Code/native tool endpoints for Desktop.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Server routes | `src/index.ts` | Elysia app, Swagger, manual CORS, `/api/v1/*`. |
| Core proxy | `src/coreClient.ts` | `coreUrl`, JSON proxy, SSE proxy. |
| Debug proxy | `src/debugProxy.ts` | Debug API + WebSocket URL helpers. |
| Code tools | `src/codeTools.ts` | Native tool execution/fallback boundary. |
| Tests | `src/coreClient.test.ts` | Node test runner + `tsx`. |

## CONVENTIONS
- Package is ESM (`"type": "module"`); TypeScript uses `NodeNext`.
- Keep Gateway thin. Core owns state, approvals, model routes, sessions, events, and persistence.
- Prompt fragment CRUD and prompt context preview routes are Core proxies only. Do not add prompt selection, token budgeting, or prompt assembly logic in Gateway.
- Harness manifest, tool search, and tool execution timeline routes are Core proxies only. Do not recompute agent layers, provider layers, risk policy, matched fields, approval summaries, or execution audit state in Gateway.
- `/api/v1/code/tools` publishes Tool-layer Code-suite metadata with snake_case public DTO fields. `src/codeTools.ts` keeps internal spec fields camelCase and maps them at the API boundary.
- Code-suite tools include project templates, runtime probe, bash-like environment, debugging, editor, Git worktree manager, and native-backed Codex primitives.
- `project_templates` is read-only list/preview. `project_template_scaffold` writes files and must remain approval-gated; direct Gateway execution treats `approval_id` as the Core-supplied approval proof.
- `git_worktree_manager` supports read-only `status`, `push_plan`, and `worktrees` previews from real Git commands. `commit` and `push` may execute real Git only with Core-supplied, Core-verified, `kind=git` approved `approval_id` plus explicit `confirm_commit` / `confirm_push`; path commits must stay inside the worktree, and push must block on dirty/behind/detached states. Merge, rebase, branch creation, checkout, and worktree creation remain blocked after approval until implemented deliberately.
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

Target one test from `apps/gateway/`:
```bash
node --test --import tsx src/coreClient.test.ts
```
