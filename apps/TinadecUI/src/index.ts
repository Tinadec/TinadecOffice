// TinadecUI — public barrel.
//
// Each module exports its public surface here. Consumers should import from
// `@tinadec/ui` (the package root) rather than deep file paths so the module
// boundary stays clean.
//
// Modules:
//   - engine (TinadecUIE) — the pure-TS layout engine + persistence (DOM-free).
//   - components — the Vue UI library: render components, cards, useUie.
//     Depends one-way on the engine.
//
// Note: the engine's layout-model types `UieColumn`/`UieStack` and the
// Components module's `UieColumn`/`UieStack` Vue components share names. The
// explicit component re-exports below shadow the type-only names (same as the
// pre-split engine barrel), so `@tinadec/ui` exports the components; the types
// remain importable from `@tinadec/ui/engine`.

export * from './engine'
export * from './components'
export { default as UieColumn } from './components/UieColumn.vue'
export { default as UieStack } from './components/UieStack.vue'
