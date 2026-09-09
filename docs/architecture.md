# TinadecOffice Architecture

TinadecOffice is a four-product family. The normative product boundary and target DmaEA architecture are defined in [`tinadec-core-product-definition.zh-CN.md`](tinadec-core-product-definition.zh-CN.md). The current repository uses the following integrated deployment topology:

- `TinadecCore`: portable C# Core framework and runtime. It owns agents, runs, task graphs, context packs, supervision, approvals, model routes, events, secrets, permissions, capability discovery, shared database abstraction (default SQLite, optional PostgreSQL via EF Core), and **Agent Debug Studio tracing**.
- `TinadecTool` (current code path `TinadecTools`): tool discovery and execution provider. The current Core-owned child-process adapter is one integration, not a permanent product dependency.
- `TinadecGateway`: optional Elysia BFF/API layer. It exposes `/api/v1/*` (including `/api/v1/debug/*`), OpenAPI docs at `/docs`, and proxies to the Core runtime.
- `TinadecApp` (currently `apps/desktop`, `apps/web`, and `apps/TinadecUI`): client experiences and product-owned Agent Pack artifacts. A TinadecApp may implement either supported northbound deployment, but the current Desktop/Web renderer uses Gateway only; Electron exposes only the `window.tinadec.*` preload API.

Core is the only business-state authority. Gateway and App must not keep a second copy of session state, approval decisions, model routing state, tool policy state, or provider lifecycle state.

## Fixed v1 and Governance Transport

TinadecOffice has not shipped a first formal release. Every public HTTP, OpenAPI, SSE, and WebSocket contract stays under `/api/v1`; there is no `/api/v2`, compatibility alias, migration route, or deprecation window. Breaking changes update v1, DTOs, generated clients, snapshots, tests, and the Chinese product definition together. Domain values such as `AgentVersion`, `ModeVersion`, policy revisions, and content hashes are immutable state bindings, not API versions.

Gateway is a stateless northbound proxy. It forwards Core governance routes and preserves the two explicit user tool transport surfaces (`/api/v1/code/tools/*` and `/api/v1/tool-runtime/*`) without calculating risk, PDP, approvals, leases, hashes, or nonce material. Core user writes use `POST /api/v1/user/tool-actions`; agent writes use the run-scoped Core tool route. A user action is not a synthetic run and has its own durable snapshot, permission, action-approval, result, and audit references.

High-risk user and agent writes capture a provider-neutral workspace snapshot before permission or action approval. Git snapshots come from the safe-argument-list CLI provider and include HEAD/ref, index/tree, worktree, binary patches, untracked/deleted paths, and conflicts. Snapshot mismatch, parameter drift, manifest drift, expired/revoked lease, invalid nonce, and duplicate consumption fail closed. User actions persist `non_reversible` plus optional compensation guidance for snapshot overrides and remote side effects such as `git_push`. Desktop surfaces the Core action state (`snapshot_required`, `awaiting_delegate`, `awaiting_user`, `awaiting_approval`, `running`, `completed`, `blocked`, `outcome_unknown`) but never treats UI state as authorization fact.

## MAF 1.18 Adapter Boundary

TinadecCore pins the Microsoft Agent Framework package family to `1.18.0`. MAF-specific types and behavior are confined to the internal DmaEA adapter; public contracts, persisted events, checkpoints, permissions, approvals, and tool receipts remain Tinadec-owned.

- MAF compaction operates on atomic function-call/result groups and cannot split a pending tool exchange.
- Provider/MAF usage is normalized into a provider-neutral Core record before persistence or metrics.
- Agent and Workflow OpenTelemetry keep sensitive data disabled by default; prompts, user content, credentials, tool arguments, and tool results are not exported.
- MAF automatic-approval iteration limits are only a ceiling for Core tool-round budgets. They do not grant authority or bypass Core's durable approval and Tool Dispatcher path.
- MAF session or workflow checkpoints may be opaque sidecars to a Core checkpoint, but Core remains authoritative for scope, event watermarks, leases, approvals, idempotency, side effects, and recovery decisions.

TinadecOffice intentionally studies sibling projects. The source-backed TinadecCore decisions are recorded in [`tinadec-core-reference-decisions.zh-CN.md`](tinadec-core-reference-decisions.zh-CN.md); the earlier workbench-oriented map remains in [`reference-project-map.md`](reference-project-map.md).

## Default Ports

- TinadecOffice Elysia API: `http://127.0.0.1:48730`
- TinadecCore runtime: `http://127.0.0.1:48731`
- Vite renderer: `http://127.0.0.1:5173`

## Harness And Tool Layer APIs

Core owns the agent harness model and Tool-layer policy semantics. Gateway proxies these endpoints, and Desktop renders them without recomputing risk or provider-layer meaning.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/harness/manifest` | Core-owned summary of operation/execution agent layers, canonical tool registry governance, Tool-layer providers, tool risk policy, and registered tool descriptors. |
| `GET /api/v1/tools` | Raw Core tool descriptor list. |
| `GET /api/v1/tools/search` | Core-owned searchable tool discovery with matched metadata fields, provider layer, score, and human-checkpoint summary. Supports `query`, `domain`, `source`, `risk`, and `limit`. |
| `GET /api/v1/sessions/{sessionId}/tool-executions` | Core-owned tool execution timeline built from tool execution events, provider descriptors, checkpoint summaries, durations, and step-result evidence. Supports `runId` and `limit`. |
| `GET /api/v1/readiness` | Core-owned runtime readiness receipt covering storage (`storage.provider` / `storage.state` for SQLite or PostgreSQL), dual agent layers, canonical tool registry, model routes/providers, and extension runtime registries. Gateway/Desktop must proxy or display it without recomputing the status. |
| `GET /api/v1/tool-layer-readiness` | Core-owned Tool-layer receipt covering canonical tool dispatchability, provider layers, future-tool markers, human-checkpoint requirements, and execution-agent scope resolution. |
| `GET /api/v1/model-readiness` | **Deprecated compatibility shim** (`StubEndpoints.cs:58-60`): the route still exists for older clients but its counters are hard-coded to 0. Use `GET /api/v1/readiness` instead. |
| `GET /api/v1/model-catalog-readiness` | **Deprecated compatibility shim** (`StubEndpoints.cs:127-155`): returns `templates: []` and zeroed counts. Use `GET /api/v1/model-provider-templates` instead. |

### User actions, governance, and snapshots

| Endpoint | Purpose |
|----------|---------|
| `GET/POST /api/v1/user/tool-actions` | List or create a Core-owned user action. The request contains only project, tool, params, and an idempotency key; the response includes a stable `audit_reference` but never nonce material. |
| `GET /api/v1/user/tool-actions/{id}` | Read the durable action projection without nonce material. |
| `POST /api/v1/user/tool-actions/{id}/resume` | Re-evaluate the frozen governance state and resume the same action. |
| `POST /api/v1/user/tool-actions/{id}/snapshot-override` | Current-user-only one-time override for a failed pre-write snapshot, with a non-reversible audit mark. |
| `GET/POST /api/v1/governance/permission-requests/*` | Query and decide capability permission requests. Delegation and lease endpoints remain thin Core proxies. |
| `GET/POST /api/v1/workspace-snapshots/*` | Query, restore, and audit provider-neutral workspace snapshots. |

Permission granting and single-action approval remain separate state machines. Core wakes a user action directly after a permission decision; it never creates a run to represent Desktop work.

## Model And Agent Configuration APIs

Desktop composes Model Center and Agent Center from Core-owned versioned provider, route, Agent, Mode, PromptPipeline, WorkspaceDefaults and Agent Pack APIs through Gateway. The former `/model-center/overview`, `/agent-center/overview` and model-center refresh alias are intentionally absent; `PUT /api/v1/agents/{id}/runtime-binding` **does exist** (`AgentConfigurationEndpoints.cs:21`) and is part of the current contract; Gateway does not derive effective bindings or retain configuration drafts. Canonical model discovery is `POST /api/v1/model-providers/{id}/models/refresh`, and all Agent writes use draft/publish/archive plus immutable version contracts.

## Bundled Agent Pack Lifecycle

TinadecCore exposes a generic, workspace-scoped Agent Pack lifecycle; it does not compile TinadecOffice roles into Core. TinadecOffice carries schema `tinadec.io/agent-pack/v1alpha1` Pack `tinadec.office.agent-pack` (`owner=tinadec.office`, current `version=0.2.3`) as a deterministic renderer asset. It contains 14 Agent definitions, 5 PromptPipeline resources, 7 Modes (`default-mode` + `conversation.{plan,spec,ask,vibe,auto,agent}`), and recommended workspace defaults.

On each connected epoch the main renderer asks Core for an install preview through Gateway, validates the returned Pack identity/hash, shows owner/version/hash and default impact, and submits a confirmed install. Electron child/pet/debug windows skip bootstrap; Web tabs coordinate through `BroadcastChannel`/Web Locks, while Core idempotency and revision checks remain authoritative. Rejection is remembered only for the current App lifetime; errors stay non-blocking and expose a retry that obtains a fresh preview.

Core validates the complete Prompt -> Agent -> Mode graph, recomputes the RFC 8785/JCS SHA-256, and persists immutable pack versions and managed-resource bindings in one transaction. Preview expires after 15 minutes; PUT reuses the exact envelope and `preview_id`, requires `Idempotency-Key`, and requires `If-Match` for upgrades. Eligible defaults advance only while empty, legacy-equivalent, or still bound to the prior Pack version. Existing sessions keep their exact ModeVersion; upgrades affect only new sessions. Pack-managed resources are read-only and must be cloned before customization. App uninstall does not remove Core state, and digital signatures/market distribution are outside the first bundled-pack contract.

The northbound surface is `GET /api/v1/agent-packs`, `GET /api/v1/agent-packs/{pack_id}`, `POST /api/v1/agent-packs/install-preview`, and `PUT /api/v1/agent-packs/{pack_id}`.

## Office Agent Pack Runtime Boundary

The active path creates exact-version instances for `meeting`, `task_planner`, `supervisor`, and one selected specialist from `worker.code`, `worker.document`, `worker.data`, `worker.browser`, `worker.file`, `worker.git`, or `worker.general`. Worker selection requires complete capability/tool coverage, then prefers specialists, fewer extra permissions, roster order, and slug; no match fails closed as `worker_unavailable`. Assignment is persisted before first invocation and reused after recovery. Only `meeting` may answer the user.

`context_compressor`, `skill_recommender`, `evolution`, and `git_steward` are installed and included in the frozen roster. Since 2026-08-29 they are activated by the operation-layer trigger chain (`DmaEA/Operations/OperationalTriggers.cs`), but they still create no synthetic instance: they dispatch as bypass governance calls and emit their own events (`context.compacted`, `capability.recommended`, `evolution.agent_candidate_created`, `git.steward.reviewed`). `worker.git` is the executable Git specialist; mutations remain approval-gated through Core-governed Tool Provider calls.

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

## Canonical Dual-Layer Runtime Contract

The canonical layers are the governance layer (`operation`) and execution layer (`execution`). `planning` is accepted only as a migration input and must be normalized before new Core contracts, persisted versions, events, or UI payloads are produced. The governance layer includes the meeting entry point, context maintenance, capability advice, supervision, and evolution proposals. The execution layer plans task graphs, schedules task-bound workers, invokes Core-governed tools, and returns evidence.

The meeting agent is the only agent allowed to produce a user-facing answer. A run-time child agent is a Core-owned orchestration instance, not a generic tool: every spawn must carry its parent instance, intent (`temporary`/`persistent_candidate`/`persistent_profile`), target and success criteria, selected context, model, scoped tools/resources, and budget. `search`/`programming`/`testing` are execution specialists selected by the task planner — they remain execution workers and never become a third layer. A child cannot enlarge inherited authority or receive `direct_user_output`, formal-memory writes, or promotion authority. Temporary workers release when their run finishes; a reusable design first becomes an auditable candidate and requires human promotion (`agent.create_profile`, governance-layer only) into an immutable profile version.

Full duplex is coordinated by Core rather than by the lifetime of one HTTP response. A durable run accepts status queries, supplements, goal changes, new tasks, pause, resume, and cancellation while work continues. Shared state uses monotonically increasing `context_revision`; a patch or result based on an obsolete revision cannot overwrite newer constraints and must be rejected, reconciled safely, or trigger re-planning. The run-state vocabulary of record is 12 states (`RunStatusMachine.cs:12-17`): `planning`, `understanding`, `executing`, `replanning`, `awaiting_approval`, `awaiting_delegate`, `awaiting_user`, `paused`, `reviewing`, `completed`, `failed`, `cancelled`.

Long-term memory and reusable agents follow a candidate-to-promotion path. Session history and summaries may be used automatically; only reviewed, promoted long-term memory is retrievable across sessions. Full content remains in immutable ContentStore, while relational projections and events hold references, hashes, counts, and summaries. Tool approval and memory review are distinct state machines.

## Runtime Configuration Baseline

`TinadecCore/DmaEA/Configuration/default-agent-runtime.toml` is the annotated, read-only generic fallback and budget baseline. It defines mode availability, operation/execution policy, model/tool policy, supervision, context, memory, scheduling, and generation budgets, but it is not the formal Office roster. Creation authority is explicit: `agent.create_temporary` (temporary run workers), `agent.create_persistent` (candidate), and `agent.create_profile` (promotion/bound profile); `agent.spawn` is normalized to `agent.create_temporary` for compatibility.

When a session has an exact published ModeVersion, `FormalModeResolver` resolves the immutable AgentVersion/PromptVersion bindings from that snapshot and the run freezes them; it must not query latest published versions. TOML is used only when no formal ModeVersion is bound and as the source of generic scheduling/budget policy. `AgentRuntimeConfigurationStore` still validates and hot-reloads only valid fallback snapshots; workspace override application and readiness diagnostics remain open.

## Operation-Layer Trigger Chain (2026-08-29)

The four dormant operational roles are now activated by a trigger chain instead of engine hard-coding. `[triggers]` in the TOML baseline gates the chain and sets the compression threshold; `DmaEA/Operations/OperationalTriggers.cs` matches frozen-roster roles at four engine anchors (task graph created, task completed, run finalized, capability missing). Dispatches are bypass governance calls: they never enter the task graph, never produce user-visible output, and failures are isolated (`operation.dispatch.failed`). context_compressor applies ToolCallAware-guarded `compaction` CAS patches (`context.compacted`); skill_recommender records `checkpoint.RecommendedCapabilities` and feeds replanning; the experience curator curates memory/agent candidates at run close under the `[memory]` allow-lists; git_steward advises (`git.steward.reviewed`) only on runs that touched `git_*` tools. Workers may propose context patches via a `CONTEXT_PATCH: summary || content` line, arbitrated by CAS against the task's input revision. Promotion is review-driven: sanitize the immutable proposal, publish an immutable agent version, record the decision (canary/activation remain future stages). `GET /api/v1/runs/{runId}/replay` rebuilds the run timeline from the event journal as the supervision-replay and candidate-evaluation vehicle; MAF Workflows was analyzed and deliberately not adopted as the phase-machine replacement (no distributed lease or approval hooks, no time-travel).

## Current Delivery Status (2026-08-25; operation-layer update 2026-08-29)

| Surface | Present now | Still required for the full-duplex contract |
|---|---|---|
| Runtime configuration | Annotated TOML baseline, validation, aliases, in-process valid-only reload, relational projections for agent instances/candidates/profile overrides, and per-run frozen profile resolution in the full-duplex engine. | Workspace override resolution and readiness diagnostics. |
| Invocation | `POST /api/v1/sessions/{id}/interactions` admits work to the durable full-duplex engine (run output is read back through `GET /api/v1/runs/{runId}/stream`; the former `invoke-stream` route is retired and returns 404): idempotent admission, `context_revision` snapshots/patches, governance coordination → task planning → execution → supervision → meeting finalization, worker spawn/lineage, durable SSE (ack/delta/done/error) with replay/follow, run control, active-run limits, and leased-checkpoint recovery. | Gateway/Desktop normal-chat migration and remaining interaction-contract cleanup. |
| Layer terminology | Configuration parsing normalizes `planning` to `operation`. | Legacy DmaEA records, API projections, and persisted contracts still need migration/normalization to canonical `operation`. |
| Spawn, lineage, and promotion | Exact-version meeting/planner/supervisor/worker instances, persisted specialist assignment, stable capability/tool selection, recovery reuse, candidate review APIs, and review-driven immutable promotion (2026-08-29: sanitize → publish immutable agent version → record decision). | Canary/activation stages after promotion. |
| Context, prompts, and memory | Context snapshots/patches plus one frozen prompt assembly path: Core protocol -> agent system prompt -> PromptPipeline -> task evidence -> final constraints. Event-driven `context_compressor` (ToolCallAware-guarded compaction patches), reviewed-memory retrieval injection, and candidate promotion/revocation persistence landed 2026-08-29; worker-proposed context patches are CAS-arbitrated. | Vector retrieval (embedding route unconfigured), prompt graph `variable`/`condition`/`stage` nodes, memory expiry/supersede lifecycle. |
| Tools and approvals | Real TinadecTools child process per workspace root (auto-probed executable, manifest-v2 handshake, BOM-free pipe), frozen per-run manifest, one-time approval consumption, prepare/resume dispatch, crash/timeout handling, `UserToolAction`, governance nonce boundary, and `tool-layer-readiness` receipts. | Scheduling (501); ACP permission bridge. (`tools/shell` is implemented — the governed `shell` tool dispatch.) |
| Workspace snapshots | File-system and Git providers capture HEAD/index/worktree/conflicts and use deterministic restore/guard semantics. | Full restore-plan UX and external compensation records. |
| Gateway and Desktop | Gateway proxies Core governance and Agent Pack routes without state. Desktop/Web carry the Pack, run owner-confirmed bootstrap, and show Pack version/managed/clone/retry state. | Complete action history/recovery UX and normal-chat migration cleanup. |
| Bundled Agent Pack | Generic Core preview/install/version/default-adoption/managed-resource lifecycle plus TinadecOffice-owned 14-Agent `OfficeAgentPack`; SQLite/PostgreSQL migrations and exact runtime binding are wired. | Digital signatures, organization trust stores, market distribution, rollback and uninstall. |

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

## TinadecUIE Layout Engine

The Desktop main window is rendered by **TinadecUI** (in `apps/TinadecUI`), split into two modules: **TinadecUIE** (the Engine module — a pure-TS, DOM-free deterministic layout engine modeled after VS Code's single layout authority and Traycer's pure layout model / card registry) and the **Components module** (the Vue render components `UieShell`/`UieCanvas`/…, the `useUie` reactive store, and the cards). The engine owns the window layout behind a stable, command-driven interface; components depend on it one-way and only read the snapshot → geometry → DOM. The visual UI is unchanged from the pre-refactor layout; only the engine behind it is new. Consumers import it as `@tinadec/ui` (alias registered in `apps/desktop` and `apps/web` → `apps/TinadecUI/src/index.ts`).

### Layout model

- Three physical slots: `left / center / right`. Each slot holds a tab stack (`primary`) and optionally one vertical split into a `secondary` stack. No arbitrary recursive docking tree.
- The layout is a pure `UieLayoutSnapshot` (version 1): column order, column widths/collapsed/surface-mode/top-inset, per-stack ordered tab ids + active tab, a cards map (`instanceId -> {descriptorId, title, state}`), and the focused card id.
- **Single layout authority**: `apps/TinadecUI/src/engine/` is the only owner of layout state. The render layer only reads the snapshot → computes geometry → places DOM. Every mutation goes through `commandBus.dispatch({ command, source, expectedRevision })`.
- **Commands** (fixed set): `openCard / closeCard / activateCard / moveCard / moveStack / swapColumns / splitStack / mergeStack / resizeColumn / resizeSplit / collapseColumn / applyPreset / resetScope`. The reducer returns the next snapshot AND the inverse command; the undo stack keeps 50 records, coalescing consecutive drag-resizes into one.
- **Sources**: `user / route / restore` are executable; `ai` is reserved and rejected this round (a controlled entry point for future AI-driven layout).
- **Constraint solver**: `computeGeometry(container, snapshot)` derives column/stack geometry from the container size and per-card min widths. Under space pressure it visually collapses the right column first, then the left; an over-short split degrades to a single stack. These degradations are visual only — never written back to the user layout.

### Card registry & instance pool

- `apps/TinadecUI/src/components/cards/index.ts` registers every card descriptor (`{ type, component, minWidth, minHeight, singleton, movable, closable, detachable, defaultTitle, titlebarMode }`).
- The instance pool (`apps/TinadecUI/src/engine/instancePool.ts`) hydrates each card instance ONCE by `instanceId`; hidden cards stay mounted (visibility/inert/aria-hidden), and only an explicit close destroys the instance. Moving between slots/tabs/routes never remounts a card.

### Material integration

- Material is applied on the **stable stack root** (`UieStack`), which binds `data-panel-effect` + `--surface-section`/`--bg-primary` — the same contract as the legacy `.float-panel`. Legacy page components embedded as card content are neutralized to fill their card frame (see `apps/TinadecUI/src/components/uie-card-fill.css`).
- `App.vue` no longer wraps the main `RouterView` in a `mode="out-in"` transition: the engine owns the layout, and a transition wrapper would unload the page host on route change, breaking `backdrop-filter` and hitting removed-node patches.

### Route semantics

- The router keeps all existing hash paths (`/`, `/settings`, `/agent-center` → redirect, `/market`, `/debug-studio`, `/code-editor`, `/workbench`, `/governance`, `/snapshots`, `/recovery/:actionId`, `/library`, `/panel`, `/pet`). `/governance`, `/snapshots`, `/recovery/:actionId`, `/code-editor` and `/library` currently have no in-app navigation entry. The main window renders `UieShell` for the home page; other pages keep their page-level layouts. Pet / detached-panel / debug-studio windows remain separate renderer windows.

### Page transition animations

Spatial route transitions are **declarative and CSS-class driven** — page code never manipulates engine nodes (`.wb-column`) via DOM queries or inline styles, so TinadecUIE remains the single layout authority.

- **Home exit**: `HomePage.vue` wraps the shell in a classic `<Transition name="home-up-exit">` around a plain div (never the Vapor `UieShell` root — classic-around-Vapor leave paths crash the interop unmount, see `VaporExemptions.ts`). `onBeforeRouteLeave` flips a `visible` flag and defers navigation ~300 ms; `page-transitions.css` animates `.wb-column` `transform`/`opacity` only (the engine's `patchStyle` diff owns `left/top/width/height` and never touches these).
- **Home entry**: the same Transition's enter classes rise the columns from below with stagger when returning.
- **Settings entry/exit**: `settings.css` keyframes (`settings-nav-enter`/`settings-content-enter` and `settings-nav-exit`/`settings-content-exit`) drive both directions. Exit toggles a `.settings-exiting` class on the page root from `onBeforeRouteLeave` — no `document.querySelector`, no inline styles, no `animation: 'none'` detachment hack.
- All spatial animations are neutralized under `prefers-reduced-motion` in both `page-transitions.css` and `settings.css`.

### Persistence

- Layouts persist to `userData/workbench-layout.json` via `electron/layoutStore.cjs` (atomic temp+rename) with IPC `tinadec:layout-load` / `tinadec:layout-save`.
- The renderer `layerStore` holds three scope layers, resolved most-specific-first: `workspace-page(projectId, pageId)` > `page(pageId)` > `global(pageId)` > built-in preset. Auto-save is debounced (400 ms) to the write scope (workspace-scoped when a project is active).
- `repairLayout()` fixes corrupt/unknown-card/duplicate-singleton/illegal-size layouts and falls back to the built-in preset — never a blank window.
- Legacy detached-panel records (`~/.tinadec-panel-layout.json`, `PanelType`) migrate to generic card types via `apps/TinadecUI/src/engine/persistence/migrate.ts`.

### Vapor mode

- Vue 3.6 (RC) Vapor is enabled **per-SFC** via the `<template vapor>` block attribute. The root `package.json` overrides pin `vue` + `@vue/compiler-sfc` to `3.6.0-rc.7` so the compiler and runtime agree.
- `src/lib/vue-shim.ts` re-exports `@vue/runtime-dom` + `@vue/runtime-vapor` so vapor SFCs' `from 'vue'` resolves in both Vite build and vitest.
- `main.ts` installs `vaporInteropPlugin` so vapor components can live inside the classic vdom tree.
- Rollout is batched (`src/vapor/vaporBatch.ts`); exemptions are recorded in `src/vapor/VaporExemptions.ts`.

## Agent Debug Studio (frontend only — backend NOT implemented)

> **Status:** the Desktop window exists, but **no Core-side tracing or debug backend is implemented**. There is no `TinadecCore/Tracing/` or `TinadecCore/Debug/` directory; every `debug/*` route below is served by `AspNetCore/Endpoints/StubEndpoints.cs` and returns an empty array or `501`, and Gateway `/ws/debug` is a dead stub (`TinadecGateway/src/websocket.ts:58-60`). The section below describes the **target** design, not current behavior. See [`docs/agent-debug-studio-plan.md`](agent-debug-studio-plan.md) for the plan.

### Target architecture (not built)

### Architecture

- **C# Tracing Layer** (`src/TinadecCore/Tracing/`): OpenTelemetry-based span collection with NDJSON file export, metrics, and diagnostics.
- **Debug API** (`src/TinadecCore/Debug/`): REST endpoints for trace/metrics/diagnostics queries, plus simulation and breakpoint control.
- **WebSocket Feed** (`/api/v1/debug/ws`): Real-time span event streaming to the Debug Studio frontend.
- **Debug Studio Frontend** (`apps/desktop/src/debug/`): Electron BrowserWindow with Trace Timeline, Agent Graph Canvas, Metrics Dashboard, and Simulator Bar.

### Key API Endpoints

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/debug/traces` | Query trace list — **currently returns `[]`** |
| `GET /api/v1/debug/traces/{id}` | Get trace detail with span tree |
| `GET /api/v1/debug/metrics` | Query metric aggregations |
| `GET /api/v1/debug/diagnostics` | Get diagnostic report |
| `GET /api/v1/debug/processes` | Process resource info |
| `WS /api/v1/debug/ws` | Real-time debug event feed — **route does not exist** |
| `POST /api/v1/debug/simulate/message` | Inject simulated message |
| `POST /api/v1/debug/breakpoints` | Set breakpoint |

### Configuration

Tracing is configured in `appsettings.json` under `TinadecTracing` and can be overridden with environment variables:

- `TINADEC_TRACING_ENABLED` — Enable/disable tracing
- `TINADEC_TRACE_FILE` — NDJSON trace file path
- `TINADEC_OTLP_TRACES_URL` — OTLP traces export URL
- `TINADEC_OTLP_METRICS_URL` — OTLP metrics export URL
