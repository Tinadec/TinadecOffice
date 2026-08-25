// uieBridge.ts — TinadecUIE engine bootstrap for the Debug Studio preview.
//
// Real pages that render through the engine (MarketPage via UieCanvas) call
// `useUie()`, which throws until `initUie()` runs. In the app shell HomePage
// initializes the singleton WITH the Electron persistence adapter, which
// auto-saves layout snapshots to the shared `workbench-layout.json`. The
// Debug Studio window must NEVER write that file (it would clobber the
// user's real Home workbench layout), so the preview initializes the engine
// WITHOUT a persistence layer: fully in-memory for this window only.

import { buildUieRegistry, createComponentLookup, initUie, useUie } from '@tinadec/ui'

export function ensurePreviewUie(): void {
  try {
    useUie()
    return // already initialized in this window (idempotent singleton)
  } catch {
    // not initialized yet — fall through to a persistence-free init
  }
  const registry = buildUieRegistry()
  initUie({
    registry,
    componentFor: createComponentLookup(registry),
    // Deliberately NO `persistence` — see file header.
  })
}
