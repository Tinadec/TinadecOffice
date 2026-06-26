# DESKTOP APP KNOWLEDGE

## OVERVIEW
Electron + Vue 3 desktop app. Vite renders the UI; Electron provides the window/preload bridge; renderer talks to Gateway only.

## STRUCTURE
```
apps/desktop/
├── electron/          # Electron main, preload, Debug Studio window, panel window manager
├── scripts/dev.mjs    # Vite then Electron launcher
└── src/
    ├── pages/         # hash-router route pages
    ├── components/    # feature components
    ├── components/ui/ # shadcn-style Vue primitives + barrel
    ├── debug/         # self-contained Agent Debug Studio feature
    ├── composables/   # shared app composables
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
| Prompt Context settings | `src/pages/SettingsPage.vue`, `src/api.ts` | Manage/clone custom prompt fragments and preview Core-assembled prompts through Gateway; do not assemble prompts in the renderer. |
| Tool layer catalog/search | `src/pages/SettingsPage.vue`, `src/toolCatalog.ts`, `src/api.ts` | Settings presents Code-suite tools, Codex primitives, supported runtimes, Core manifest registry governance/design notes, and Core-owned tool search results. |
| Tool execution visibility | `src/pages/HomePage.vue`, `src/components/ContextPanel.vue`, `src/components/OrchestrationTab.vue`, `src/api.ts` | Right rail presents Core-owned tool execution timeline state, provider layer, duration, checkpoint summary, and step-result evidence. |
| Git management UI | `src/components/GitPanel.vue`, `src/components/ContextPanel.vue`, `src/gitDiffParser.ts`, `src/api.ts` | Right rail Git tab calls Gateway `git_worktree_manager` with `status`, `push_plan`, and `diff_preview`; it can request Core `git` approvals and execute approved `stage` / `unstage` / `commit` / `push` calls, but never runs Git directly. |
| Marketplace | `src/pages/MarketPage.vue` | Extension source/catalog/install flow. |
| Debug Studio | `src/debug/DebugStudio.vue`, `src/debug/**` | Composables/types/components are feature-local. |
| UI primitives | `src/components/ui/index.ts`, `src/lib/utils.ts` | `Ui*` barrel exports; `cn()` uses clsx + tailwind-merge. |
| Theme/i18n | `src/composables/useTheme.ts`, `src/i18n.ts`, `src/locales/*` | Persisted theme/accent/locale behavior. |
| Detached panel windows | `electron/panelWindow.cjs`, `src/pages/DetachedPanelPage.vue`, `src/components/ContextPanel.vue`, `src/composables/usePanelTabs.ts` | Electron multi-window management: BrowserWindow creation, cursor-polling drag-to-detach (Chrome-style tab tearing), disk-based layout persistence, reattach/focus, and cross-window theme broadcast. Main window is tagged with `_isTinadecMain` so `getMainWindow()` distinguishes it from Debug Studio. Panel layout persisted to `~/.tinadec-panel-layout.json` on move/resize/quit and restored on launch. |

## CONVENTIONS
- Use `@/*` for imports from `src/*` when it improves clarity.
- Router uses `createWebHashHistory()`; routes: `/`, `/settings`, `/market`, `/debug-studio`, `/panel` (detached panel window).
- No Pinia/store layer exists; use composables and local refs.
- UI stack: Vue, Tailwind via `@tailwindcss/vite`, lucide-vue, shadcn-style primitives.
- Tests are colocated `src/**/*.test.ts`; command is `vitest run`.
- Prompt Context UI is presentation and local preview only. The renderer calls Gateway APIs mirrored in `src/api.ts`; Core owns fragment selection, context pack handling, token estimates, and warnings.
- Code-suite UI is presentation-only: group/filter tool descriptors and project template summaries from Gateway/Core, but keep approval and execution ownership outside Desktop.
- Git UI is presentation plus Core-approved execution: request Tool-layer previews from Gateway, show blockers/diffs/worktrees, create Core approval records when needed, and only call approved `stage` / `unstage` / `commit` / `push` tool executions with Core-verified approval ids; do not run Git directly or mint approval ids in Desktop.
- Tool search UI must consume Core/Gateway `/api/v1/tools/search` results. Do not invent provider-layer, matched-field, or human-checkpoint semantics in the renderer.
- Tool execution UI must consume Core/Gateway `/api/v1/sessions/{sessionId}/tool-executions` results. Do not reconstruct audit timelines, provider layers, durations, or checkpoint summaries from local event arrays in Desktop.
- Dev server is pinned: `127.0.0.1:5173`, `strictPort: true`.

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
```
