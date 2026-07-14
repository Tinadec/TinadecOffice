# DESKTOP APP KNOWLEDGE

## OVERVIEW
Electron + Vue 3 desktop app. Vite renders the UI; Electron provides the window/preload bridge; renderer talks to Gateway only.

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
    ├── composables/   # shared app composables (incl. useTerminal)
    ├── locales/       # en / zh-CN i18n bundles
    └── api.ts         # renderer DTO mirror of Core/Gateway shapes
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Electron startup | `electron/main.cjs`, `electron/preload.cjs` | Hardened renderer: context isolation, sandbox, no nodeIntegration. |
| Renderer bootstrap | `src/main.ts`, `src/App.vue`, `src/router.ts` | App is `RouterView`; routes lazy-load pages. |
| Main shell | `src/pages/HomePage.vue`, `src/components/*` | Chat, approvals, events, context, task graph. |
| Settings | `src/pages/SettingsPage.vue` | Large hotspot; model/providers/agents settings. |
| Runtime center view adapter | `src/runtimeCenterView.ts` | Converts Gateway center DTOs into provider forms, topology labels, and runtime-source presentation without persisting binding state. |
| Prompt Context settings | `src/pages/SettingsPage.vue`, `src/api.ts` | Manage/clone custom prompt fragments and preview Core-assembled prompts through Gateway; do not assemble prompts in the renderer. |
| Tool layer catalog/search | `src/pages/SettingsPage.vue`, `src/toolCatalog.ts`, `src/api.ts` | Settings presents Code-suite tools, Codex primitives, supported runtimes, Core manifest registry governance/design notes, and Core-owned tool search results. |
| Tool execution visibility | `src/pages/HomePage.vue`, `src/components/ContextPanel.vue`, `src/components/OrchestrationTab.vue`, `src/api.ts` | Right rail presents Core-owned tool execution timeline state, provider layer, duration, checkpoint summary, and step-result evidence. |
| Git management UI | `src/components/GitPanel.vue`, `src/components/ContextPanel.vue`, `src/gitDiffParser.ts`, `src/gitIndexPatch.ts`, `src/api.ts` | Right rail Git tab calls Gateway previews, builds approved hunk/line text patches for `git_stage` / `git_unstage`, and commits/pushes only through Core-approved tool calls; it never runs Git directly. |
| Marketplace | `src/pages/MarketPage.vue` | Extension source/catalog/install flow. |
| Debug Studio | `src/debug/DebugStudio.vue`, `src/debug/**` | Composables/types/components are feature-local. |
| UI primitives | `src/components/ui/index.ts`, `src/lib/utils.ts` | `Ui*` barrel exports; `cn()` uses clsx + tailwind-merge. |
| Theme/i18n | `src/composables/useTheme.ts`, `src/i18n.ts`, `src/locales/*` | Persisted theme/accent/locale behavior. |
| Detached panel windows | `electron/panelWindow.cjs`, `src/pages/DetachedPanelPage.vue`, `src/components/ContextPanel.vue`, `src/composables/usePanelTabs.ts` | Electron multi-window management: BrowserWindow creation, cursor-polling drag-to-detach (Chrome-style tab tearing), disk-based layout persistence, reattach/focus, and cross-window theme broadcast. Main window is tagged with `_isTinadecMain` so `getMainWindow()` distinguishes it from Debug Studio. Panel layout persisted to `~/.tinadec-panel-layout.json` on move/resize/quit and restored on launch. |
| Integrated terminal | `electron/terminalManager.cjs`, `src/composables/useTerminal.ts`, `src/components/TerminalPanel.vue`, `src/components/TerminalView.vue`, `src/components/ContextPanel.vue`, `src/components/PanelHome.vue` | Full PTY terminal via `node-pty` (with `child_process.spawn` fallback). Multi-instance tabs, shell profile selector (PowerShell/CMD/Git Bash/WSL/zsh/bash), xterm.js rendering with theme adaptation from CSS variables, keyboard shortcuts (Ctrl+Shift+T new, Ctrl+W close, Ctrl+Tab switch), auto-fit via ResizeObserver, and detachable panel windows. Terminal panel type is `'terminal'` in `usePanelTabs`; multiple instances allowed. Native module rebuild: `npm run rebuild:native` (requires Python + C++ build tools). |

## CONVENTIONS
- Use `@/*` for imports from `src/*` when it improves clarity.
- Router uses `createWebHashHistory()`; routes: `/`, `/settings`, `/market`, `/debug-studio`, `/panel` (detached panel window).
- No Pinia/store layer exists; use composables and local refs.
- UI stack: Vue, Tailwind via `@tailwindcss/vite`, lucide-vue, shadcn-style primitives.
- Tests are colocated `src/**/*.test.ts`; command is `vitest run`.
- Prompt Context UI is presentation and local preview only. The renderer calls Gateway APIs mirrored in `src/api.ts`; Core owns fragment selection, context pack handling, token estimates, and warnings.
- Model Center consumes the Gateway overview and renders five resource groups: Core suppliers, API/local connections, configured-only models, CLI runtimes, and ACP runtimes. Core supplier templates are executable-catalog authority; `providerTemplates.ts` may only supply presentation metadata such as translations, icons, colors, and placeholders.
- Model lists contain only provider defaults and existing route overrides until Core adds live discovery. Refresh controls and ACP probes must follow Gateway capability flags, while Gateway diagnostics remain visible and retryable without hiding usable partial data.
- Agent Center consumes Gateway-derived effective bindings for cards and topology. It may preview `inherit`, `fixed_model`, `provider_auto`, `cli`, and `acp`, but must keep save disabled while `agent_runtime_binding_write=false`; never persist drafts in Desktop, Gateway, or `localStorage`.
- Legacy `model_route_purpose` bindings can be shared by multiple agents. Show `LEGACY_SHARED_ROUTE` warnings and never save an agent runtime choice by rewriting the shared model route.
- Code-suite UI is presentation-only: group/filter tool descriptors and project template summaries from Gateway/Core, but keep approval and execution ownership outside Desktop.
- Git UI is presentation plus Core-approved execution: request Tool-layer previews from Gateway, use direct approved tools for the complete mutation surface including conflict resolution, and only execute with Core-verified approval ids; do not run Git directly or mint approval ids in Desktop.
- Tool search UI must consume Core/Gateway `/api/v1/tools/search` results. Do not invent provider-layer, matched-field, or human-checkpoint semantics in the renderer.
- Tool execution UI must consume Core/Gateway `/api/v1/sessions/{sessionId}/tool-executions` results. Do not reconstruct audit timelines, provider layers, durations, or checkpoint summaries from local event arrays in Desktop.
- Dev server is pinned: `127.0.0.1:5173`, `strictPort: true`.
- Terminal backend uses `node-pty` when available (requires `npm run rebuild:native` with Python + C++ build tools); falls back to `child_process.spawn` automatically. The fallback mode supports basic command execution but not interactive programs (vim, less) or terminal resize.
- Terminal IPC channels: `terminal:create`, `terminal:write`, `terminal:resize`, `terminal:destroy`, `terminal:get-shells`, `terminal:list`. Data/exit events use per-terminal channels: `terminal:data:{id}`, `terminal:exit:{id}`.
- Terminal state is managed by `useTerminal` composable (module-level singleton). Terminal processes are destroyed on component unmount (tab close, detach, or page navigation) to prevent orphaned PTY processes.

## ANTI-PATTERNS
- Do not call Core directly from renderer; call Gateway (`48730`).
- Do not expose filesystem/shell/model API keys to renderer; use preload/Gateway/Core boundaries.
- Do not add app-wide state store without checking existing composable/local-ref pattern.
- Do not duplicate route shells: `src/pages/DebugStudioPage.vue` is router-used; `src/debug/pages/DebugStudioPage.vue` appears alternate/unused.

## COMMANDS
```bash
npm run dev -w @tinadec/desktop
npm run build -w @tinadec/desktop
npm run test -w @tinadec/desktop
npm run rebuild:native -w @tinadec/desktop  # rebuild node-pty for Electron (requires Python)
```
