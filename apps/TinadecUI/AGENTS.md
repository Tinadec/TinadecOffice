# TinadecUI — UI Engineering Suite

**Last Updated:** 2026-08-10

TinadecUI is the UI-engineering home inside TinadecOffice. Consumers (`apps/desktop`, `apps/web`) import it as `@tinadec/ui` — a registered alias in both packages' `vite.config.ts` and `tsconfig.json` that resolves to `apps/TinadecUI/src/index.ts`. Both consumers also map `@` → `apps/desktop/src`, so TinadecUI files may reference app code via `@/` and it resolves under every consumer. The boundary is a module home + public barrel, not a build-isolated library.

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
