// TinadecUIE — Engine module public barrel.
//
// This is the single import point for the pure-TS layout engine. Consumers
// (components, app bootstrap) should import from here rather than deep file
// paths so the module boundary stays clean.
//
// Structure: pure TS engine (DOM-free, unit-tested) + persistence. The engine
// core owns layout state; all mutations go through `commandBus.dispatch`.
// Vue render components, cards, and the reactive store (`useUie`) live in the
// Components module (`src/components`) and depend on this engine one-way.

// --- Pure TS engine core ---
export * from './types'
export * from './commands'
export * from './reducer'
export * from './undoStack'
export * from './scope'
export * from './registry'
export * from './presets'
export * from './repair'
export * from './constraints'
export * from './commandBus'
export * from './instancePool'
export * from './dockDrop'

// --- Persistence ---
export * from './persistence/types'
export * from './persistence/layerStore'
export * from './persistence/migrate'
