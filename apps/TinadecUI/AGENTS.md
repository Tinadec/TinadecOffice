# TinadecUI — UI Engineering Suite

**Last Updated:** 2026-08-28

TinadecUI is the UI-engineering home inside TinadecOffice. Consumers (`apps/desktop`, `apps/web`) import it as `@tinadec/ui` — a registered alias in both packages' `vite.config.ts` and `tsconfig.json` that resolves to `apps/TinadecUI/src/index.ts`. Both consumers also map `@` → `apps/desktop/src`, so TinadecUI files may reference app code via `@/` and it resolves under every consumer. The boundary is a module home + public barrel, not a build-isolated library.

## Standalone display repo (2026-08-28)

`C:\git\agent\TinadecUI` is the standalone display/distribution surface for external consumers (e.g. the Tinadec official website at `C:\git\agent\TinadecOfficalWeb`). Source of truth stays in TinadecOffice; sync via `scripts/sync-tinadec-ui.mjs` (copies the 30 barrel primitives + `lib/utils.ts` + logo assets, rewriting `@/lib/utils` → `../../lib/utils`). `tokens.css`/`fonts.css` in that repo are curated derivatives of `apps/desktop/src/styles.css` L1-370 + the chat micro-interactions (L4894-5144), adapted to web selectors — edit them by hand after token changes. The standalone repo requires Vue 3.6.0-rc.2 + `vue-shim` + `vaporInteropPlugin` because 5 primitives are Vapor SFCs; see its `README.md`/`design.md`.

## Structure

```
apps/TinadecUI/
├── package.json      # @tinadec/ui (private; no standalone build — consumers resolve @/)
├── tsconfig.json     # mirrors desktop; @/* → ../desktop/src/*
├── AGENTS.md
└── src/
    ├── index.ts      # public barrel — re-export each module's public surface
    ├── engine/       # TinadecUIE — the Engine module (pure-TS layout engine + persistence)
    └── components/   # the Components module — the Vue UI library
```

## The three modules

TinadecUI organizes UI engineering into three modules (the user's framing):

| Module | Role | Location |
|--------|------|----------|
| **Engine (TinadecUIE)** | Deterministic layout authority — the pure-TS, DOM-free layout engine (types/commands/reducer/undoStack/scope/registry/presets/repair/constraints/commandBus/instancePool/dockDrop) + versioned persistence (layerStore/migrate). Owns layout state; all mutations go through `commandBus.dispatch`. | `src/engine/` |
| **Components** | The Vue UI library: render components (`UieShell`/`UieCanvas`/`UieColumn`/`UieStack`/`UieDock`/`UieCardHost`/`UieCardFrame` + `uie-card-fill.css`), the reactive store (`useUie`/`initUie`), and the card registry (`src/components/cards/`). Depends on the engine **one-way**. | `src/components/` |
| **Rendering** | Page/surface rendering & transitions that compose the engine. | future: `src/rendering/` |

## Dock (multi-pane split)

The feature/right column supports **dock splits** — recursive binary split trees of panes (`UieColumn.dock`, mutually exclusive with `primary/secondary`). Users drag a tab to a pane edge to split (row/column) or to a pane center to merge; a single collapsed rail collects every pane's feature icons.

- Engine: `engine/types.ts` (`UieDockNode`/`UieDockGeometry`), `engine/reducer.ts` (commands `splitDockPane`/`mergeDockPane`/`mergeDockColumn`/`moveCardToDockPane`/`resizeDockSplit`, tree helpers, `findInstanceLocation` with `paneId`), `engine/constraints.ts` (`flattenDock`), `engine/repair.ts` (`repairDock`), `engine/dockDrop.ts` (drop-zone pure function).
- Components: `components/UieDock.vue` (flat pane/divider/overlay rendering), `UieStack.vue` (`paneId`/`paneMain` + split-pane minimal tab bar + merge button), `BrowserTabBar.vue` (shared drag source via `@/composables/useDockDrag`), `UieColumn.vue` (virtual main-pane drop rect before the first split; collapsed rail collects all dock panes' icons).
- Invariants: exactly one `main` pane hosting `homePicker`; non-main panes are never empty; collapsing to a single main pane normalizes `dock` back to `null` (stacks restored).
- **Window-stacking overlay**: a float right feature column (hosts `homePicker`) wider than the center's comfort width (`MIN_CHAT_COMFORT_WIDTH`, the composer no-wrap threshold) floats over the chat instead of squeezing it — `computeGeometry` marks `degraded.overlayRight` + `ColumnGeometry.overlay`, clamps the panel so `MIN_OVERLAY_STRIP` of chat stays visible, and `UieColumn` renders it at a higher z-index. Drag ceiling is `maxOverlayColumnWidth`. Visual-only, never written back.
- `snapshot.version` stays **1**: `dock` is an optional additive field; old persisted snapshots (no `dock`) load unchanged via `repairLayout`, so no `migrate.ts` bump is required.

## Module boundary rules

- **Engine core is pure TS and DOM-free.** Do not add Vue/DOM imports to `engine/types/commands/reducer/undoStack/scope/registry/presets/repair/constraints/commandBus/instancePool`.
- **Dependency direction is Components → Engine (one-way).** Components read `useUie()`/types and dispatch commands; the engine never imports Vue components or `useUie`.
- **Persistence** (`engine/persistence/`) is part of the Engine module (storage logic, not UI); it stays DOM-free and writes through Electron `layoutStore.cjs` → `userData/workbench-layout.json`.
- All layout mutations go through `commandBus.dispatch({ command, source, expectedRevision })`; `ai` source is reserved/rejected.
- Persistence format is versioned; changing snapshot shape requires a `migrate.ts` bump.
- Vapor: Components-module SFCs are `<template vapor>`; keep `apps/desktop/src/vapor/` registries in sync when adding/renaming components or cards.

## Adding a new module
1. Create `apps/TinadecUI/src/<module>/`.
2. Export its public surface from `apps/TinadecUI/src/index.ts`.
3. Add a row to the module table and this doc's structure tree.
4. Document the module's `@/` app-code dependencies in this doc.

## Importing TinadecUI
```ts
// desktop or web renderer
import { UieShell, initUie, buildUieRegistry, createLayerStore } from '@tinadec/ui'
```
`@tinadec/ui` is registered in `apps/desktop` and `apps/web` (vite alias + tsconfig paths → `../TinadecUI/src/index.ts`). When a TinadecUI file needs desktop app code (e.g. `@/api`, `@/controllers/*`), it uses `@/` — resolved to `apps/desktop/src` under both consumers. Tests run from `apps/TinadecUI` via `vitest` (see `vitest.config.ts`, which defines the same `@` alias).
