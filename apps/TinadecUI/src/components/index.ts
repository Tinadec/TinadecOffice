// TinadecUI — Components module public barrel.
//
// The UI library: Vue render components that realize the TinadecUIE layout
// engine, the reactive store (`useUie`), and the card registry. Everything
// here depends one-way on the Engine module (`src/engine`) — it never mutates
// layout state directly, only through `commandBus.dispatch`.

// --- Reactive store ---
export * from './useUie'

// --- Vue render components ---
export { default as UieShell } from './UieShell.vue'
export { default as UieCanvas } from './UieCanvas.vue'
export { default as UieColumn } from './UieColumn.vue'
export { default as UieStack } from './UieStack.vue'
export { default as UieCardHost } from './UieCardHost.vue'
export { default as UieCardFrame } from './UieCardFrame.vue'

// --- Cards registry ---
export * from './cards'
