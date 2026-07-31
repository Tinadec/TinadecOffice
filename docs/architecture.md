# TinadecOffice Architecture

TinadecOffice is split into three product responsibilities:

- `TinadecCore`: portable C# Core framework and runtime. It owns agents, runs, task graphs, context packs, supervision, approvals, model routes, events, secrets, permissions, capability discovery, shared database abstraction (default SQLite, optional PostgreSQL via EF Core), and **Agent Debug Studio tracing**.
- `gateway`: TinadecOffice Elysia BFF/API layer. It exposes `/api/v1/*` (including `/api/v1/debug/*`), OpenAPI docs at `/docs`, and proxies to the Core runtime.
- `apps/desktop`: TinadecOffice Desktop, built with Electron + Vue. The renderer receives only the `window.tinadec.*` preload API and talks to TinadecOffice over HTTP/SSE. Includes the **Agent Debug Studio** as a separate BrowserWindow.

Core is the only state authority. Gateway and Desktop must not keep session state, approval decisions, model routing state, tool policy state, or provider lifecycle state.

TinadecOffice intentionally studies sibling projects such as VS Code, Codex, t3code, OpenCode, OpenHarness, Open-ClaudeCode, openclaw, pi, DeepSeek-TUI, and The Zeroth Docs. The reference map in [`docs/reference-project-map.md`](reference-project-map.md) records what to absorb and what to reject so those influences strengthen, rather than flatten, the Core/Tool/Desktop split.

## Default Ports

- TinadecOffice Elysia API: `http://127.0.0.1:48730`
- TinadecCore runtime: `http://127.0.0.1:48731`
- Vite renderer: `http://127.0.0.1:5173`

## Harness And Tool Layer APIs

Core owns the agent harness model and Tool-layer policy semantics. Gateway proxies these endpoints, and Desktop renders them without recomputing risk or provider-layer meaning.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/harness/manifest` | Core-owned summary of planning/execution agent layers, canonical tool registry governance, Tool-layer providers, tool risk policy, and registered tool descriptors. |
| `GET /api/v1/tools` | Raw Core tool descriptor list. |
| `GET /api/v1/tools/search` | Core-owned searchable tool discovery with matched metadata fields, provider layer, score, and human-checkpoint summary. Supports `query`, `domain`, `source`, `risk`, and `limit`. |
| `GET /api/v1/sessions/{sessionId}/tool-executions` | Core-owned tool execution timeline built from tool execution events, provider descriptors, checkpoint summaries, durations, and step-result evidence. Supports `runId` and `limit`. |
| `GET /api/v1/readiness` | Core-owned runtime readiness receipt covering storage (`storage.provider` / `storage.state` for SQLite or PostgreSQL), dual agent layers, canonical tool registry, model routes/providers, and extension runtime registries. Gateway/Desktop must proxy or display it without recomputing the status. |
| `GET /api/v1/tool-layer-readiness` | Core-owned Tool-layer receipt covering canonical tool dispatchability, provider layers, future-tool markers, human-checkpoint requirements, and execution-agent scope resolution. |
| `GET /api/v1/model-readiness` | Core-owned model provider and route readiness receipt covering provider status, credential availability, route coverage, blocked routes, and advisory discovery notes. |
| `GET /api/v1/model-catalog-readiness` | Core-owned model catalog receipt covering static templates, runtime module coverage, configured instance counts, and advisory live-discovery policy. Static templates stay visible when live discovery is unavailable. |

## Model And Agent Center BFF APIs

Gateway exposes stateless center-oriented views for Desktop while Core remains the only authority for provider lifecycle, routes, agents, readiness, and secrets.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/model-center/overview` | Aggregates Core supplier templates, configured provider instances, model routes, model/catalog readiness, and ACP adapters into suppliers, API/local connections, configured-only models, CLI runtimes, ACP runtimes, capabilities, and diagnostics. |
| `GET /api/v1/agent-center/overview` | Adds agents, modes, candidates, selectable runtime sources, and a Gateway-derived effective binding for each existing `model_route_purpose`. Shared purposes are reported with `LEGACY_SHARED_ROUTE`; they are not treated as per-agent state. |
| `POST /api/v1/model-center/provider-instances/{providerInstanceId}/models/refresh` | Reserved live-discovery contract. It validates the provider id and returns `501 MODEL_DISCOVERY_UNSUPPORTED` until Core owns discovery. |
| `PUT /api/v1/agents/{agentId}/runtime-binding` | Reserved snake_case discriminated-union contract for inherit, fixed model, provider auto, CLI, and ACP. It validates the request and returns `501 AGENT_RUNTIME_BINDING_UNSUPPORTED` until Core owns persistent per-agent bindings. |

The BFF normalizes transport and credential kinds separately, recursively strips secret fields, keeps Core readiness receipts unchanged apart from secret removal, and degrades optional Core `404/501` responses into diagnostics. Required Core failures and Core unreachability remain errors. The model list is not live discovery: it is deduplicated only from configured provider defaults and current route overrides. CLI providers and ACP adapters stay distinct; ACP-capable legacy providers are labeled `legacy_provider` rather than merged by guesswork. Gateway and Desktop do not persist binding drafts, implement provider auto-selection, or rewrite shared legacy routes.

## Built-In Execution Subagents

`executor_git_manager` is the Git Manager Subagent in the execution layer. Git-related goals such as branch review, commit preparation, push readiness, worktree management, merge/rebase guidance, and user-facing handoff notes can route to it. It can explain repository state, but Git mutation and push flows must remain approval-gated through Core-governed tools such as `git_worktree_manager`.

## Event Envelope

All runtime events use:

```json
{
  "v": "1.0",
  "type": "message.created",
  "request_id": "req_xxx",
  "session_id": "sess_xxx",
  "trace_id": "trace_xxx",
  "seq": 1,
  "ts": "2026-05-18T10:15:30Z",
  "capabilities": ["agent.message"],
  "payload": {},
  "error": null
}
```

## Run Locally

```powershell
npm install
npm run restore:dotnet
npm run dev
```

The local environment currently contains `Version=V7.24.42SP3`, which breaks MSBuild version parsing. Root npm scripts remove that variable only for the child .NET process.

## Desktop Panel Material System

The desktop renderer has a single global panel material with three effects: `opaque`, `translucent`, and `blur`. The setting is persisted under the localStorage key `tinadec-panel-style` and owned by `apps/desktop/src/composables/usePanelStyles.ts`.

### How a material is applied

1. `computePanelStyle()` produces the panel root's inline style: `translucent` sets `background-color: rgba(var(--bg-primary-rgb), alpha)`; `blur` additionally sets `backdrop-filter: blur(Npx)` (plus the `-webkit-` prefix). `alpha` is the user's opacity setting (0-100) divided by 100; blur strength is clamped to 0-20px. Blur roots also expose `--material-filter-section` at 20% and `--material-filter-raised` at 35% of that clamped value; both are `none` at zero blur and are not emitted for `opaque` or `translucent`.
2. `getPanelDataAttributes()` puts `data-panel-effect` on the material root. Panels binding both today include the sidebar, chat panel, context panel (HomePage), and settings nav/content (SettingsPage). The SettingsPage root also carries the attribute and forwards only the two derived filter variables so sibling UI such as window controls and page-level dialogs participates in the same material scope; root blur/background styles remain on the nav/content panels.
3. `styles.css` re-maps the material-aware surface tokens for the panel and all descendants via CSS custom property inheritance — there is no per-component whitelist.

### Surface token contract (alpha tier mapping)

Interior surfaces inside a material panel must consume `--surface-*` tokens, never raw `--bg-*` tokens, for any background that should follow the material:

| Tier | Token | Opaque | Translucent | Blur |
|------|-------|--------|-------------|------|
| Chrome strips (tab bars, topbars) | `--surface-chrome` | `var(--bg-tertiary)` | `rgba(var(--bg-tertiary-rgb), 0.72)` | `rgba(var(--bg-tertiary-rgb), 0.18)` |
| Section frames (grouped content) | `--surface-section` | `var(--bg-secondary)` | `rgba(var(--bg-secondary-rgb), 0.70)` | `rgba(var(--bg-secondary-rgb), 0.38)` |
| Raised surfaces (cards, popovers) | `--surface-raised` | `var(--bg-tertiary)` | `rgba(var(--bg-tertiary-rgb), 0.78)` | `rgba(var(--bg-tertiary-rgb), 0.50)` |
| Hover emphasis | `--surface-hover` | `var(--bg-hover)` | `rgba(var(--bg-hover-rgb), 0.82)` | `rgba(var(--bg-hover-rgb), 0.62)` |
| Active surface | `--surface-active` | `var(--bg-primary)` | `rgba(var(--bg-primary-rgb), 0.86)` | `rgba(var(--bg-primary-rgb), 0.68)` |
| Accent selection | `--surface-selected` | `var(--bg-selected)` | `rgba(var(--bg-selected-rgb), 0.84)` | `rgba(var(--bg-selected-rgb), 0.72)` |
| Inputs / fields (input, textarea, select) | `--surface-input` | `var(--bg-input)` | `rgba(var(--bg-input-rgb), 0.88)` | `rgba(var(--bg-input-rgb), 0.70)` |
| Neutral buttons | `--surface-button` | `var(--bg-button)` | `rgba(var(--bg-button-rgb), 0.80)` | `rgba(var(--bg-button-rgb), 0.58)` |
| Neutral button hover | `--surface-button-hover` | `var(--bg-button-hover)` | `rgba(var(--bg-button-hover-rgb), 0.90)` | `rgba(var(--bg-button-hover-rgb), 0.72)` |

The canonical definition lives in the "Material-aware surface tokens" and "Panel Style Effects" sections of `apps/desktop/src/styles.css`; the RGB companions (`--bg-*-rgb`) are theme-scoped and must be kept in sync with the hex tokens for both `data-theme="dark"` and `data-theme="light"`.

Token names describe the component role, not the effect: use `--surface-<role>` (for example, `--surface-input` and `--surface-button`) and let the nearest `data-panel-effect` root select the mode. Reusable interaction variants append the state suffix to the base role (`--surface-button-hover`); shared state roles use `--surface-hover`, `--surface-active`, or `--surface-selected`. Do not add mode-specific names such as `--surface-input-blur` or embed alpha values in component CSS.

The alpha in this table is the foreground surface's own alpha. It is composited over the panel root, whose opacity is independently controlled by the user's setting, so the final pixel opacity is not the table value alone. Nested material surfaces composite again and become visually denser; use the shallowest semantic tier and avoid wrapping a card or field in redundant material backgrounds merely to increase contrast.

The two `--material-filter-*` values are optional depth cues, not new colour tiers. Apply them only to a small number of top-level grouped surfaces such as a workbench or overview band. Repeated rows and cards must rely on their `--surface-*` role so a long list does not create a backdrop-filter compositing layer for every item.

### Tailwind shadcn utilities

Material-aware UI primitives consume the tokens directly, normally through Tailwind arbitrary values such as `bg-[var(--surface-input)]` or component CSS using `background: var(--surface-button)`. This direct consumption is required for base and state styles because generated variants such as `hover:bg-*` and `data-[state=*]:bg-*` are not reliably covered by a plain class selector.

Scoped rules under `[data-panel-effect="translucent"]` and `[data-panel-effect="blur"]` re-point the following **neutral** Tailwind utilities as a compatibility fallback for existing content:

| Utility | Maps to |
|---------|---------|
| `bg-card`, `bg-popover` | `--surface-raised` |
| `bg-background` | `--surface-section` |
| `bg-secondary`, `bg-muted` | `--surface-button` |
| `bg-accent` | `--surface-hover` |
| `bg-input` | `--surface-input` |

This fallback is not the primitive contract and must not be expanded into a per-component whitelist. Primary and destructive actions remain solid to preserve action hierarchy. Status colors remain semantic solids, overlays keep their dedicated scrim opacity, background/media previews retain the opacity of the content being previewed, and the switch thumb remains solid for legibility; only the neutral switch track follows the material (`--surface-input` when unchecked, solid primary when checked).

### Rules for new development

- Never hard-code hex/rgb backgrounds or raw `--bg-*` tokens for surfaces rendered inside a material panel; pick the matching `--surface-*` tier (inputs → `--surface-input`, neutral buttons → `--surface-button`, frames/cards → `--surface-section`/`--surface-raised`).
- Bind `data-panel-effect` at the common ancestor that owns the material scope. Do not add per-child effect bindings when CSS custom property inheritance can cover the subtree.
- Never duplicate `computePanelStyle()` logic; import it (the settings preview in `panel-style-control.vue` does this).
- Inside the Settings page, separate neutral groups with surface tier, spacing, and typography before adding a decorative border. Do not use neutral header rules, footer rules, table row separators, or nested card outlines when `--surface-section`, `--surface-chrome`, `--surface-raised`, hover, and selected states already establish the hierarchy.
- Borders remain valid when they communicate function or state: input boundaries, focus rings, selection, warning/error/risk accents, and accessible modal elevation. Text, semantic borders, and icons keep their normal tokens; neutral surface backgrounds participate in the material.
- Component-owned glass effects (notification island, notification detail dialog) are intentionally independent of the global material and keep their own fixed blur values.
- Detached panel windows and Electron windows stay opaque by design: there is no OS-level vibrancy/`backgroundMaterial`, so `backdrop-filter` only blurs the in-app background layer rendered by `App.vue`.

## Agent Debug Studio

TinadecOffice includes an **Agent Debug Studio** — a dedicated debugging tool designed for Agent systems. See [`docs/agent-debug-studio-plan.md`](agent-debug-studio-plan.md) for the full implementation plan.

### Architecture

- **C# Tracing Layer** (`src/TinadecCore/Tracing/`): OpenTelemetry-based span collection with NDJSON file export, metrics, and diagnostics.
- **Debug API** (`src/TinadecCore/Debug/`): REST endpoints for trace/metrics/diagnostics queries, plus simulation and breakpoint control.
- **WebSocket Feed** (`/api/v1/debug/ws`): Real-time span event streaming to the Debug Studio frontend.
- **Debug Studio Frontend** (`apps/desktop/src/debug/`): Electron BrowserWindow with Trace Timeline, Agent Graph Canvas, Metrics Dashboard, and Simulator Bar.

### Key API Endpoints

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/debug/traces` | Query trace list |
| `GET /api/v1/debug/traces/{id}` | Get trace detail with span tree |
| `GET /api/v1/debug/metrics` | Query metric aggregations |
| `GET /api/v1/debug/diagnostics` | Get diagnostic report |
| `GET /api/v1/debug/processes` | Process resource info |
| `WS /api/v1/debug/ws` | Real-time debug event feed |
| `POST /api/v1/debug/simulate/message` | Inject simulated message |
| `POST /api/v1/debug/breakpoints` | Set breakpoint |

### Configuration

Tracing is configured in `appsettings.json` under `TinadecTracing` and can be overridden with environment variables:

- `TINADEC_TRACING_ENABLED` — Enable/disable tracing
- `TINADEC_TRACE_FILE` — NDJSON trace file path
- `TINADEC_OTLP_TRACES_URL` — OTLP traces export URL
- `TINADEC_OTLP_METRICS_URL` — OTLP metrics export URL
