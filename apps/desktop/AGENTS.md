# DESKTOP APP KNOWLEDGE

**Last Updated:** 2026-08-07
**Last Updated By:** Codex (converted the Workbench to a headless compatibility host and introduced the isolated Vue Vapor boundary)
**Last Verified Commit:** 2b4377c
**Branch:** UI/RE

## OVERVIEW
Electron + Vue 3 desktop app. Vite renders the UI; Electron provides the window/preload bridge; renderer talks to Gateway only. The main window owns one persistent, headless three-slot `WorkbenchShell`; main routes retain the established page UI while selecting a background preset and preserving visited page instances.

The General settings page owns the Desktop Gateway endpoint. The effective URL is resolved in this order: `TINADEC_GATEWAY_URL`, Electron `userData/settings.json`, then `http://127.0.0.1:48730`. Environment-managed values are read-only in the UI; persisted changes take effect after Desktop restarts.

## STRUCTURE
```
apps/desktop/
├── electron/          # Electron main, preload, Debug Studio window, panel window manager, terminal manager
├── scripts/dev.mjs    # Vite then Electron launcher
└── src/
    ├── pages/         # hash-router route pages
    ├── components/    # feature components (incl. TerminalPanel, TerminalView)
    ├── components/ui/ # shadcn-style Vue primitives + barrel
    ├── debug/         # self-contained Agent Debug Studio feature
    ├── workbench/     # layout model/reducer, constraints, registry, controllers, cards, stable surfaces
    ├── settings/      # async settings registry, boundary, shared context, and section modules
    ├── composables/   # shared app composables (incl. useTerminal)
    ├── locales/       # en / zh-CN i18n bundles
    └── api.ts         # renderer DTO mirror of Core/Gateway shapes
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Electron startup / app config | `electron/main.cjs`, `electron/preload.cjs`, `electron/appConfig.cjs` | Hardened renderer plus validated, main-process-owned Gateway URL persistence. |
| Local pet system | `electron/petStore.cjs`, `electron/petWindow.cjs`, `src/pets/petRuntime.ts`, `src/pages/DesktopPetPage.vue`, `src/pages/SettingsPage.vue` | Desktop-only Petdex v2 registry and transparent, always-on-top Canvas windows. `petRuntime.ts` is the renderer business module: it validates proportional 8-column sheets, maps the canonical nine Petdex state rows and active frame counts, loops with modulo, and calculates source rectangles. Petdex `pet.json` contains identity/path metadata, not animation definitions. Local files, enable state, bounds, and scale live under Electron `userData/pets/`; IPC is sender-scoped. Never call Gateway/Core. |
| Renderer bootstrap | `src/main.ts`, `src/App.vue`, `src/router.ts` | Main window mounts the headless `WorkbenchShell`; its invisible `WorkbenchVaporRuntime` is the isolated Vapor boundary, while the legacy `RouterView` UI remains VDOM through `KeepAlive`. Child windows retain direct RouterView rendering. |
| Workbench authority | `src/workbench/types.ts`, `reducer.ts`, `constraints.ts`, `useWorkbenchLayout.ts` | Versioned three-column model, revisioned commands, inverse operations, responsive degradation, 50-entry history, and scoped persistence. |
| Card registry / surfaces | `src/workbench/registry.ts`, `components/WorkbenchCanvas.vue`, `components/WorkbenchCardSurface.vue` | Registry-governed capabilities and stable instance-ID hosts. Canvas/surfaces are not mounted by default and must not introduce visible controls. |
| Domain controllers | `src/workbench/homeController.ts`, `codeController.ts`, `marketController.ts`, `debugController.ts`, `settingsController.ts` | One shared controller per visited page; Debug creates one WebSocket/data pipeline. These remain presentation state only. |
| Settings | `src/pages/SettingsPage.vue`, `src/settings/settingsModuleRegistry.ts`, `src/settings/modules/*` | Locked navigation/content Workbench cards; 12 async modules load on first visit, remain mounted, and render through a retryable error boundary. |
| Runtime center view adapter | `src/runtimeCenterView.ts` | Converts Gateway center DTOs into provider forms, topology labels, and runtime-source presentation without persisting binding state. |
| Provider presentation templates | `src/providerTemplates.ts` | Presentation-only metadata (i18n keys, brand colors, placeholders, icons). Brand icons are official `@lobehub/icons-static-svg` SVGs imported via Vite `?raw` (23 drivers); drivers without a lobehub slug (`sglang`, `llamacpp`, `custom`) keep hand-written `currentColor` SVGs. `icon` is an inline `<svg>` string rendered via `v-html` inside `.provider-brand-icon`/`.modal-provider-logo` (24px/32px CSS sizing). |
| Prompt Context settings | `src/pages/SettingsPage.vue`, `src/api.ts` | Manage/clone custom prompt fragments and preview Core-assembled prompts through Gateway; do not assemble prompts in the renderer. |
| Tool layer catalog/search | `src/pages/SettingsPage.vue`, `src/toolCatalog.ts`, `src/api.ts` | Settings presents Code-suite tools, Codex primitives, supported runtimes, Core manifest registry governance/design notes, and Core-owned tool search results. |
| Tool execution visibility | `src/workbench/cards/HomeToolsCard.vue`, `src/components/ContextPanel.vue`, `src/components/OrchestrationTab.vue`, `src/api.ts` | A movable card presents Core-owned tool execution timeline state, provider layer, duration, checkpoint summary, and step-result evidence. |
| Git management UI | `src/workbench/cards/HomeGitCard.vue`, `src/components/GitPanel.vue`, `src/gitDiffParser.ts`, `src/gitIndexPatch.ts`, `src/api.ts` | A movable Git card calls Gateway previews, builds approved hunk/line patches, and commits/pushes only through Core-approved tool calls. |
| Marketplace | `src/workbench/marketController.ts`, `src/workbench/cards/Market*Card.vue` | Shared extension source/catalog state rendered as filters, catalog, and details cards. |
| Debug Studio | `src/workbench/debugController.ts`, `src/workbench/cards/Debug*Card.vue`, `src/debug/**` | One shared controller feeds timeline, Inspector, graph, metrics, diagnostics, preview, and simulator cards; Electron still opens the dedicated window by default. |
| UI primitives | `src/components/ui/index.ts`, `src/lib/utils.ts` | `Ui*` barrel exports; `cn()` uses clsx + tailwind-merge. |
| Appearance/i18n | `src/composables/useTheme.ts`, `src/composables/usePanelStyles.ts`, `src/i18n.ts`, `src/locales/*` | Persisted theme/accent/locale plus synchronous normalized global panel material behavior. |
| Unified notifications | `src/composables/useNotifications.ts`, `src/components/NotificationIslandHost.vue`, `src/components/NotificationDetailDialog.vue`, `src/App.vue`, `electron/main.cjs`, `electron/preload.cjs` | Three kinds (`transient`/`status`/`task`) render in separate title-bar island zones; clicking a capsule opens the detail dialog, while hover expands a glass card that also opens the dialog when clicked. Repeated notifications merge with a counter; overflow opens the notification center with live + cleared sections. `status.*` replicates across renderer windows over IPC. System load/connection errors use islands; chat approvals stay contextual. |
| Workbench persistence / detached cards | `electron/workbenchLayoutStore.cjs`, `electron/panelWindow.cjs`, `src/workbench/persistence.ts`, `src/pages/DetachedPanelPage.vue` | Atomic layout at `userData/workbench-layout.json`; sender-scoped detached payloads and bounds at `userData/detached-cards.json`; URL carries only `windowId`; reattach restores identity/page/slot/stack/index; legacy `.tinadec-panel-layout.json` records migrate to card types. |
| Integrated terminal | `electron/terminalManager.cjs`, `src/composables/useTerminal.ts`, `src/components/TerminalPanel.vue`, `src/components/TerminalView.vue`, `src/components/ContextPanel.vue`, `src/components/PanelHome.vue` | Full PTY terminal via `node-pty` (with `child_process.spawn` fallback). Multi-instance tabs, shell profile selector (PowerShell/CMD/Git Bash/WSL/zsh/bash), xterm.js rendering with theme adaptation from CSS variables, keyboard shortcuts (Ctrl+Shift+T new, Ctrl+W close, Ctrl+Tab switch), auto-fit via ResizeObserver, and detachable panel windows. Terminal panel type is `'terminal'` in `usePanelTabs`; multiple instances allowed. Native module rebuild: `npm run rebuild:native` (requires Python + C++ build tools). |

## CONVENTIONS
- Use `@/*` for imports from `src/*` when it improves clarity.
- Windows system surfaces use `public/tinadec.ico`: main, Debug Studio, and detached `BrowserWindow` instances must reference it in both dev and built `dist`; keep `app.setAppUserModelId('com.tinadec.office')` for taskbar grouping.
- Main and Debug Studio windows use `titleBarStyle: 'hidden'` without `titleBarOverlay`, preserving the custom controls while leaving the native frame available for Windows DWM corners and shadows. Detached panels remain frameless because their drag and window-control hit testing depends on the custom title bar; pet windows remain transparent and frameless.
- Router uses `createWebHashHistory()`; main Workbench routes are `/`, `/settings`, `/market`, `/code-editor`, and `/debug-studio`. `/panel` renders a detached card and `/pet` renders the transparent local pet window.
- Main routes retain the existing `RouterView` presentation inside `WorkbenchShell`; it selects `home` / `settings` / `market` / `code` / `debug` layouts only in the background. Do not introduce card chrome, grips, resize handles, drop targets, layout menus, new gaps, or a default-on docking UI.
- Debug Studio and detached cards load with `?splash=0`: `App.vue` skips connection polling and renders their `RouterView`; failed Debug Studio renderers are destroyed so the next open recreates them. Web cannot detach, so Debug uses the main-window preset there.
- No Pinia/store layer exists; use composables and local refs.
- Workbench snapshots are pure local UI state. Structural changes go through the revisioned command union in `src/workbench/types.ts` and `reducer.ts`; `ai` source is reserved and rejected. Do not mutate snapshot placement directly or introduce arbitrary recursive docking.
- Each slot has `primary` and at most one `secondary` stack. Constraint degradation is render-only: temporarily collapse right then left on narrow widths and temporarily merge the split on short heights; never persist those automatic changes.
- Layout scope precedence is workspace-page (`ProjectDto.id`) -> page -> global geometry -> built-in. Electron stores `userData/workbench-layout.json`; Web uses localStorage `tinadec-workbench-layout-v1`. Repair unknown cards, duplicate singletons, and invalid geometry before resolving.
- A card component is keyed by stable instance ID. Moving, reordering, hiding, collapsing, and main route changes must preserve the mounted instance; only explicit close destroys it. Hidden content must remain inert, aria-hidden, pointer-disabled, and invisible.
- Detached-card URLs carry only `windowId`. Full payloads are main-process-owned, sender-scoped, size-validated state; reattach must preserve card ID and return placement.
- `usePanelStyles()` owns the global panel material under `tinadec-panel-style`. Its normalized `{ effect, opacity, blur }` state uses a detached module-lifetime `useStorage` scope with synchronous writes, so page unmounts, renderer reloads, and Desktop restarts preserve the selected material.
- Current page material roots remain the visible `data-panel-effect` hierarchy. Card frames are an unmounted future-presentation capability and must not replace or wrap current page material roots.
- Settings has fixed navigation and content cards. It rejects foreign cards, move/close/split/detach, and only allows navigation width changes. New sections must be async entries in `settingsModuleRegistry.ts`; visited modules stay mounted via `v-show` and use the shared error/retry boundary.
- Inside Settings frames, use `--surface-section`/`--surface-chrome`/`--surface-raised`, spacing, hover, and selected states before neutral decorative borders; preserve functional input/focus boundaries, semantic warning/error/risk accents, and modal elevation. `--material-filter-section` and `--material-filter-raised` are reserved for a few top-level groups, never repeated list items.
- `useNotifications()` is the only Desktop system notification API, organized in three semantic kinds: `notify.*` for short-lived transient feedback, `status.*` for persistent keyed state-driven conditions that replicate across all renderer windows, and `notify.task()` for long-running operations that settle in place. `banner.*` is a backward-compatible alias for `status.*`. `confirm()` shows a confirmation dialog; `dismiss`/`dismissByKey`/`dismissAll` remove items. Clicking a capsule opens the detail dialog directly; hover expands the glass detail card after an intent delay, and clicking that card also opens the dialog. Closing the card or detail dialog does not dismiss the notification. Dismissal follows the explicit lifecycle contract in `useNotifications.ts` (`NotificationPersistence` + `isUserDismissible`, the single source of truth for every close affordance): transient feedback auto-expires and is user-closable; `persistence: 'sticky'` transients never expire but stay user-closable; pending tasks are handle-owned and never user-closable until they settle into auto-expiring feedback; `status.*` conditions are source-owned and never user-closable — the owning state source clears them with `dismissByKey` when the condition recovers, and incidental local removal (cap eviction, programmatic `dismiss`) never broadcasts a clear to other windows. `dismissAll` clears only user-closable items. System load failures and backend connection state use `status.*` islands — never `error-strip` red banners. Keep field validation, diagnostics, Git conflicts, and agent/tool approval cards contextual. Core approvals and Tool `confirm_*` remain authoritative.
- Notifications with an identical key (or identical kind+level+title+message) merge in place with a repeat counter instead of stacking. Transient items auto-expire even while queued off-screen; only direct user attention (hover, open card, open detail) pauses their timer. Sticky transients, status items, and pending tasks never auto-expire.
- Transient and status notifications render in separate island zones, so a standing condition can never starve transient feedback. Items beyond the visible three are reachable through the notification center, which pairs the live list (source-owned rows show a residency marker instead of a close button) with the cleared-history ring.
- Notification state is per-window EXCEPT `status.*`, which is broadcast over Electron IPC (`tinadec:broadcast-status-notification` → `tinadec:status-notification`) to the main window, detached panels, and Debug Studio, excluding the sender and pet windows. Actions cannot cross IPC, so replicated items are flagged `remote` and render without an action button. Standard windows use the title-bar corridor; detached/Debug place islands below occupied title bars; pet windows never mount hosts.
- UI stack: Vue, Tailwind via `@tailwindcss/vite`, lucide-vue, `@lobehub/icons-static-svg` (provider brand icons only; framework-agnostic SVG files, imported `?raw` in `providerTemplates.ts`), shadcn-style primitives.
- Renderer tests are colocated `src/**/*.test.ts`; ordinary tests use the VDOM runtime. `vite.vapor.config.ts` runs the isolated `WorkbenchShell` Vapor interop test. `npm run test -w @tinadec/desktop` runs both plus Electron `node:test` coverage for app config, atomic Workbench storage, and detached-card migration/IPC.
- Prompt Context UI is presentation and local preview only. The renderer calls Gateway APIs mirrored in `src/api.ts`; Core owns fragment selection, context pack handling, token estimates, and warnings.
- Gateway URLs must use HTTP or HTTPS and contain no credentials, query, or fragment. Keep all renderer requests on `window.tinadec.gatewayUrl()` / `api.gatewayUrl`; do not add localhost request bypasses.
- Model Center consumes the Gateway overview and renders five resource groups: Core suppliers, API/local connections, configured-only models, CLI runtimes, and ACP runtimes. Core supplier templates are executable-catalog authority; `providerTemplates.ts` may only supply presentation metadata such as translations, icons, colors, and placeholders.
- Model lists contain only provider defaults and existing route overrides until Core adds live discovery. Refresh controls and ACP probes must follow Gateway capability flags, while Gateway diagnostics remain visible and retryable without hiding usable partial data.
- Agent Center consumes Gateway-derived effective bindings for cards and topology. It may preview `inherit`, `fixed_model`, `provider_auto`, `cli`, and `acp`, but must keep save disabled while `agent_runtime_binding_write=false`; never persist drafts in Desktop, Gateway, or `localStorage`.
- Legacy `model_route_purpose` bindings can be shared by multiple agents. Show `LEGACY_SHARED_ROUTE` warnings and never save an agent runtime choice by rewriting the shared model route.
- Code-suite UI is presentation-only: group/filter tool descriptors and project template summaries from Gateway/Core, but keep approval and execution ownership outside Desktop.
- Git UI is presentation plus Core-approved execution: request Tool-layer previews from Gateway, use direct approved tools for the complete mutation surface including conflict resolution, and only execute with Core-verified approval ids; do not run Git directly or mint approval ids in Desktop.
- Tool search UI must consume Core/Gateway `/api/v1/tools/search` results. Do not invent provider-layer, matched-field, or human-checkpoint semantics in the renderer.
- Tool execution UI must consume Core/Gateway `/api/v1/sessions/{sessionId}/tool-executions` results. Do not reconstruct audit timelines, provider layers, durations, or checkpoint summaries from local event arrays in Desktop.
- Dev server is pinned: `127.0.0.1:5173`, `strictPort: true`.
- Vite `base` must remain `./` because packaged Electron windows load `dist/index.html` through `loadFile()`.
- Terminal backend uses `node-pty` when available (requires `npm run rebuild:native` with Python + C++ build tools); falls back to `child_process.spawn` automatically. The fallback mode supports basic command execution but not interactive programs (vim, less) or terminal resize.
- Terminal IPC channels: `terminal:create`, `terminal:write`, `terminal:resize`, `terminal:destroy`, `terminal:get-shells`, `terminal:list`. Data/exit events use per-terminal channels: `terminal:data:{id}`, `terminal:exit:{id}`.
- Terminal state is managed by `useTerminal` composable (module-level singleton). Workbench hiding and route changes no longer unmount the terminal card; explicit close or detached-renderer shutdown still destroys its component-owned process to prevent orphaned PTYs.

## ANTI-PATTERNS
- Do not call Core directly from renderer; call Gateway (`48730`).
- Do not expose filesystem/shell/model API keys to renderer; use preload/Gateway/Core boundaries.
- Do not add app-wide state store without checking existing composable/local-ref pattern.
- Do not bypass the Workbench reducer, remount cards to move them, or add an unrestricted docking-tree dependency.
- Do not duplicate route shells: `src/pages/DebugStudioPage.vue` is router-used; `src/debug/pages/DebugStudioPage.vue` appears alternate/unused.

## MCP TOOLS (vue-mcp) — USE PROACTIVELY

The dev server exposes a live Vue introspection server via `vite-plugin-vue-mcp`. When it is available, use it to inspect the *running* app instead of guessing from source, especially for UI work:

- **Use it when** you need the actual runtime picture: component hierarchy, a component's current state, registered routes, or whether a UI change renders as intended. `get-component-tree` and `get-component-state` answer "what is really on screen" faster than reading source.
- **Tools**: `get-component-tree` (live hierarchy), `get-component-state` (`componentName`), `edit-component-state` (`componentName`, `path`, `value`, `valueType`), `highlight-component` (`componentName`), `get-router-info` (registered routes), `get-pinia-tree` / `get-pinia-state` (`storeName`). Note this app currently has **no Pinia store layer** — Pinia tools only matter if one is introduced.
- **Prerequisites**: dev server running (`npm run dev:desktop`) AND the app loaded in the Electron window (or a browser against the dev server) AND Claude Code connected to `vue-mcp` (root `.mcp.json`, SSE `http://localhost:5173/__mcp/sse`). Tools return empty/stale results if the app page is not open.
- **Fallback**: if the `vue-mcp` MCP server is not connected, read the source under `src/` instead — never report "no components" as a fact when you simply lack a live connection.

## COMMANDS
```bash
npm install          # 在仓库根目录；.npmrc 已设 legacy-peer-deps=true 以兼容 vite-plugin-vue-mcp 的 Vite 7 peer 范围
npm run dev -w @tinadec/desktop
npm run build -w @tinadec/desktop
npm run test -w @tinadec/desktop
npm run rebuild:native -w @tinadec/desktop  # rebuild node-pty for Electron (requires Python)
```

## NOTES
- `vite-plugin-vue-mcp` 在 Vite dev server 上暴露 MCP server（SSE，`http://localhost:5173/__mcp/sse`），供 AI 客户端读取组件树/状态/路由/Pinia。项目根 `.mcp.json` 已注册 `vue-mcp` 客户端；需先启动 dev server，再启动 Claude Code（或 `/mcp` 重连）。
- 该插件 peer 范围只到 Vite 6，故根目录 `.npmrc` 设 `legacy-peer-deps=true`。此模式下 npm 不自动安装 peer 依赖，因此 `react`/`react-dom`/`react-is` 已作为显式依赖保留，勿删除。
